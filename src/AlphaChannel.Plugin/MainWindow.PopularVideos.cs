using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.YouTube;
using System.Diagnostics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly PopularVideoTracker popularVideoTracker = new();
    private readonly PopularWorkerClient popularWorker = new();
    private readonly List<(string AccountId, PopularSubmission Submission)> popularPending = new();
    private double nextPopularSample;
    private DateTime nextPopularListUtc, nextPopularSubmitUtc;
    private bool popularListLoading, popularSubmitting;
    private int popularListGeneration;
    private string? popularListError;
    private List<VideoSearchEntry>? popularPageVideos;
    private DateTime popularPageDay;
    private const int PopularVideoLimit = 200, PopularVideoBatchSize = 25;
    private int popularVisibleVideoCount = PopularVideoBatchSize;
    private bool popularResetScroll;
    private string? popularSubmissionError;

    internal void TickPopularVideos()
    {
        if (Volatile.Read(ref disposeStarted) != 0) return;
        TickWatchHistory();
        TickDailymotionPopular();
        TickArchivePopular();
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (now < nextPopularSample) return;
        nextPopularSample = now + .25;
        var session = CurrentSession;
        // Drop another account's queued events rather than send them with the
        // currently signed-in user's credentials.
        popularPending.RemoveAll(item => item.AccountId != session?.AccountId);
        var url = video.CurrentMediaUrl;
        var id = url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                 (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase))
            ? GetYouTubeVideoId(url) : null;
        var eligible = session is not null && Plugin.ClientState.IsLoggedIn &&
                       stream.Mode != StreamMode.Viewing && PopularWorkerClient.ValidVideoId(id);
        var (position, duration, paused) = video.GetProgress();
        var identity = eligible ? $"{session!.AccountId}:{id}:{video.PlaybackAttemptId}" : null;
        var qualified = popularVideoTracker.Sample(identity,
                eligible && video.State == VideoPlaybackState.Playing && !paused,
                position, duration, video.SeekVersion, now);
        if (qualified)
        {
            // This reads the cache only; qualifying a play never fetches views.
            searchResolver.TryGetVideoMetadata(url!, out var cachedDetails);
            if (popularPending.Count >= 32) popularPending.RemoveAt(0);
            popularPending.Add((session!.AccountId, new PopularSubmission(id!,
                Guid.NewGuid().ToString("N"), popularVideoTracker.WatchedSeconds,
                duration > 0 && double.IsFinite(duration) ? duration : null,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), cachedDetails?.ViewCount, cachedDetails?.ViewCountObservedAtUnix,
                cachedDetails is null ? null : PopularContribution(cachedDetails))));
        }
        if (session is not null && popularPending.Count > 0 && !popularSubmitting &&
            DateTime.UtcNow >= nextPopularSubmitUtc)
        {
            popularSubmitting = true;
            _ = SubmitPopularPlayAsync(session.AccountId, session.Token, popularPending[0].Submission);
        }
    }

    private async Task SubmitPopularPlayAsync(string accountId, string accountToken, PopularSubmission submission)
    {
        var completed = false;
        string? error = null;
        try
        {
            var result = await popularWorker.SubmitAsync(accountToken, submission, youtubeFeedToken).ConfigureAwait(false);
            completed = result.Completed;
            if (!result.Accepted && !result.Duplicate)
            {
                error = $"Your video contribution could not be submitted (HTTP {result.HttpStatus}: {result.ErrorCode}).";
                AepLog.Warning("[YouTube/Popular] Submission HTTP [redacted]: [redacted]; [redacted].");
            }
        }
        catch (OperationCanceledException) when (youtubeFeedToken.IsCancellationRequested) { return; }
        catch (Exception exception)
        {
            var failure = exception is OperationCanceledException ? "request timed out" :
                exception is HttpRequestException ? "network request failed" : "request failed";
            error = $"Your video contribution could not be submitted: {failure}. Retrying shortly.";
            {}
        }
        if (Volatile.Read(ref disposeStarted) != 0) return;
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (Volatile.Read(ref disposeStarted) != 0) return;
            if (completed) popularPending.RemoveAll(item =>
                item.AccountId == accountId && item.Submission.EventId == submission.EventId);
            nextPopularSubmitUtc = DateTime.UtcNow.AddSeconds(completed ? 1 : 60);
            popularSubmitting = false;
            if (CurrentSession?.AccountId == accountId) popularSubmissionError = error;
        }).ConfigureAwait(false);
    }

    private void RefreshPopularList(bool force = false)
    {
        if (popularListLoading || youtubeFeedToken.IsCancellationRequested ||
            (!force && DateTime.UtcNow < nextPopularListUtc)) return;
        if (force)
        {
            youtubeMetadataReloadVersion++;
            popularVisibleVideoCount = PopularVideoBatchSize;
            popularResetScroll = true;
        }
        popularListLoading = true;
        nextPopularListUtc = DateTime.UtcNow.AddMinutes(5);
        _ = RefreshPopularListAsync();
    }

    private async Task RefreshPopularListAsync()
    {
        var generation = popularListGeneration;
        try
        {
            var entries = await popularWorker.GetAsync(youtubeFeedToken).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0 || youtubeFeedToken.IsCancellationRequested || generation != popularListGeneration) return;
                var cached = Plugin.Cfg.YouTubeSharedPopularVideos;
                Plugin.Cfg.YouTubeSharedPopularVideos = entries.Select(entry =>
                {
                    var record = cached.FirstOrDefault(item => item.VideoId == entry.VideoId) ??
                        new PopularVideoRecord { VideoId = entry.VideoId };
                    record.SharedViewCountKnown = entry.ViewCount is not null;
                    ApplyPopularSharedViewCount(record, entry.ViewCount, entry.ViewCountObservedAtUnix);
                    ApplyPopularSharedMetadata(record, entry.Metadata, entry.MetadataRefreshAfterUnix);
                    return record;
                }).ToList();
                Plugin.Cfg.YouTubePopularVideos.Clear();
                popularPageVideos = null;
                popularListError = null;
                Plugin.Cfg.Save();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (youtubeFeedToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            var failure = PopularFailureSummary(exception);
            AepLog.YouTubeFetchFailure(YouTubeFetchDiagnostic.Classify(YouTubeFetchSection.Popular,
                YouTubeFetchData.FeedEntries, YouTubeFetchMethod.CloudflareFeed, exception,
                exception is PopularWorkerException worker ? worker.HttpStatus : 0));
            if (Volatile.Read(ref disposeStarted) == 0)
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (generation != popularListGeneration) return;
                    popularListError = $"Popular could not be refreshed ({failure}). Showing the last saved list.";
                    nextPopularListUtc = DateTime.UtcNow.AddMinutes(1);
                }).ConfigureAwait(false);
        }
        finally
        {
            if (Volatile.Read(ref disposeStarted) == 0)
                await Plugin.Framework.RunOnFrameworkThread(() => popularListLoading = false).ConfigureAwait(false);
        }
    }

    private static string PopularFailureSummary(Exception exception) => exception switch
    {
        PopularWorkerException worker => $"HTTP {worker.HttpStatus}: {worker.ErrorCode}",
        OperationCanceledException => "request timed out",
        HttpRequestException => "network request failed",
        _ => "invalid response or request failed",
    };

    private List<VideoSearchEntry> GetPopularVideos()
    {
        RefreshPopularList();
        var now = DateTime.UtcNow;
        if (popularPageVideos is null || popularPageDay != now.Date)
        {
            // Ranking refreshes return IDs, not details. Reuse local history/feed
            // metadata before visible cards decide whether any request is needed.
            SeedYouTubeMetadataCache();
            popularPageVideos = Plugin.Cfg.YouTubeSharedPopularVideos
                .Select(PopularEntry).Where(video => !IsYouTubeChannelHidden(video)).Take(PopularVideoLimit).ToList();
            popularPageDay = now.Date;
        }
        return popularPageVideos;
    }

    private void DrawBrowsePopularSection()
    {
        var videos = GetPopularVideos();
        var visibleCount = Math.Min(popularVisibleVideoCount, videos.Count);
        DrawYouTubeHubFeedHeading("Popular on Alpha Channel", videos.Count);
        if (popularListError is not null)
            ImGui.TextWrapped(popularListError);
        if (popularSubmissionError is not null)
            ImGui.TextWrapped(popularSubmissionError);
        if (videos.Count == 0)
        {
            ImGui.Dummy(UiVec(0f, 24f));
            using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
                .Push(ImGuiStyleVar.WindowPadding, UiVec(20f, 18f));
            using var color = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg);
            using var empty = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##popularVideosEmptyState", new Vector2(0f, Ui(150f)), false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse));
            if (empty)
            {
                using var wrap = ImRaii.TextWrapPos(0f);
                ImGui.TextColored(MutedText,
                    popularListLoading ? "Loading Popular videos..." :
                    popularListError is not null ? "The shared Popular list could not be loaded. Try Refresh again shortly." :
                    "No qualifying videos yet. Popular builds as signed-in hosts and solo users watch YouTube videos.");
            }
            return;
        }
        using var content = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##popularVideoContent", new Vector2(0f, -1f), false));
        if (!content) return;
        if (popularResetScroll)
        {
            ImGui.SetScrollY(0f);
            popularResetScroll = false;
        }
        var width = YouTubeCardGridWidth;
        var columns = width >= Ui(930f) ? 5 : width >= Ui(720f) ? 4 : 3;
        var gap = Ui(12f);
        var cardWidth = (width - gap * (columns - 1)) / columns;
        var cardHeight = Ui(266f);
        var origin = ImGui.GetCursorPos();
        for (var index = 0; index < visibleCount; index++)
        {
            if (index > 0)
            {
                if (index % columns == 0)
                    ImGui.SetCursorPos(new Vector2(origin.X, origin.Y + (index / columns) * (cardHeight + gap)));
                else ImGui.SameLine(0f, gap);
            }
            ImGui.PushID("popular_" + videos[index].Url);
            DrawBrowseVideoCardSurface(videos[index], null, cardWidth, cardHeight, showFavouriteAction: true);
            ImGui.PopID();
        }
        if (visibleCount < videos.Count)
        {
            var rows = (visibleCount + columns - 1) / columns;
            ImGui.SetCursorPos(new Vector2(origin.X, origin.Y + rows * (cardHeight + gap)));
            if (ImGui.Button("Show more##popularVideos", new Vector2(width, Ui(38f))))
                popularVisibleVideoCount = Math.Min(PopularVideoLimit, popularVisibleVideoCount + PopularVideoBatchSize);
        }
    }
}

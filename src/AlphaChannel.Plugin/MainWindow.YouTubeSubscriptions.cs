using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.YouTube;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private const int YouTubeSubscriptionVideoCacheLimit = 200;
    private static readonly TimeSpan YouTubeSubscriptionVideoRefreshInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan YouTubeSubscriptionVideoRetryInterval = TimeSpan.FromHours(1);
    private List<VideoSearchEntry>? youtubeSubscriptionFeedResults;
    private volatile bool isSyncingYouTubeSubscriptionVideos;
    private string? youtubeSubscriptionVideosError;

    private int youtubeSubscriptionFetchId;
    private bool IsCurrentYouTubeSubscriptionFetch(int generation) =>
        Volatile.Read(ref disposeStarted) == 0 && !youtubeFeedLifetime.IsCancellationRequested &&
        Volatile.Read(ref youtubeLikedVideoSessionGeneration) == generation && CanSyncYouTubeSubscriptionVideos;

    private bool CanSyncYouTubeSubscriptionVideos =>
        Plugin.Cfg.SyncYouTubeSubscriptionVideos &&
        Plugin.Cfg.YouTubeEmbeddedBrowserSessionEnabled &&
        File.Exists(YouTubeEmbeddedBrowserSession.CookieFilePath);

    private void RestoreYouTubeSubscriptionVideoCache()
    {
        if (youtubeSubscriptionFeedResults is not null)
            return;

        youtubeSubscriptionFeedResults = Plugin.Cfg.YouTubeSubscriptionVideoCache
            .Where(record => !string.IsNullOrWhiteSpace(record.Url))
            .Take(YouTubeSubscriptionVideoCacheLimit)
            .Select(record => new VideoSearchEntry(
                record.Title, record.Url, record.ChannelName,
                record.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                record.ThumbnailUrl, record.ViewCount, record.UploadDate, record.ChannelId, record.ViewCountObservedAtUnix))
            .ToList();
    }

    private void RefreshYouTubeSubscriptionVideosIfDue()
    {
        if (!CanSyncYouTubeSubscriptionVideos || isSyncingYouTubeSubscriptionVideos)
            return;

        var now = DateTime.UtcNow;
        if (now - Plugin.Cfg.YouTubeSubscriptionVideoCacheUpdatedUtc >= YouTubeSubscriptionVideoRefreshInterval &&
            now - Plugin.Cfg.YouTubeSubscriptionVideosLastAttemptUtc >= YouTubeSubscriptionVideoRetryInterval)
            _ = FetchYouTubeSubscriptionVideosAsync();
    }

    private async Task FetchYouTubeSubscriptionVideosAsync(bool manual = false)
    {
        if (!CanSyncYouTubeSubscriptionVideos || isSyncingYouTubeSubscriptionVideos)
            return;

        var now = DateTime.UtcNow;
        if (manual && now - Plugin.Cfg.YouTubeSubscriptionVideosLastManualFetchUtc < YouTubeSubscriptionVideoRetryInterval)
            return;

        var ytDlpPath = screenController.Engine.Resources.GetLocationYTDLP();
        if (string.IsNullOrWhiteSpace(ytDlpPath) || !File.Exists(ytDlpPath))
        {
            youtubeSubscriptionVideosError = "yt-dlp is still being prepared. Try again shortly.";
            return;
        }

        isSyncingYouTubeSubscriptionVideos = true;
        youtubeSubscriptionVideosError = null;
        var sessionGeneration = Volatile.Read(ref youtubeLikedVideoSessionGeneration);
        var fetchId = ++youtubeSubscriptionFetchId;
        Plugin.Cfg.YouTubeSubscriptionVideosLastAttemptUtc = now;
        if (manual)
            Plugin.Cfg.YouTubeSubscriptionVideosLastManualFetchUtc = now;
        Plugin.Cfg.Save();

        try
        {
            var fetched = await YouTubeSubscriptionFeedFetcher.FetchAsync(
                ytDlpPath, YouTubeEmbeddedBrowserSession.CookieFilePath, youtubeFeedToken,
                manual ? YouTubeRequestPriority.Manual : YouTubeRequestPriority.Background)
                .ConfigureAwait(false);
            if (Volatile.Read(ref disposeStarted) != 0) return;
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (!IsCurrentYouTubeSubscriptionFetch(sessionGeneration)) return;
                SetYouTubeSessionNeedsReconnect(false);

                RestoreYouTubeSubscriptionVideoCache();
                var previousById = (youtubeSubscriptionFeedResults ?? [])
                    .Select(video => (Id: GetYouTubeVideoId(video.Url), Video: video))
                    .Where(item => item.Id is not null)
                    .GroupBy(item => item.Id!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Video, StringComparer.OrdinalIgnoreCase);
                var merged = new List<VideoSearchEntry>(YouTubeSubscriptionVideoCacheLimit);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var video in fetched.Concat(youtubeSubscriptionFeedResults ?? []))
                {
                    var id = GetYouTubeVideoId(video.Url);
                    if (id is null || !seen.Add(id))
                        continue;
                    var candidate = video;
                    if (previousById.TryGetValue(id, out var previous))
                        candidate = candidate with
                        {
                            ViewCount = candidate.ViewCount ?? previous.ViewCount,
                            ViewCountObservedAtUnix = candidate.ViewCount is not null ? candidate.ViewCountObservedAtUnix : previous.ViewCountObservedAtUnix,
                            UploadDate = candidate.UploadDate ?? previous.UploadDate,
                            ChannelId = candidate.ChannelId ?? previous.ChannelId
                        };
                    merged.Add(candidate);
                    if (merged.Count == YouTubeSubscriptionVideoCacheLimit)
                        break;
                }

                // The feed becomes visible now; missing metadata is filled gradually.
                youtubeSubscriptionFeedResults = merged;
                Plugin.Cfg.YouTubeSubscriptionVideoCache = merged.Select(video => new CachedBrowseVideoRecord
                {
                    Title = video.Title,
                    Url = video.Url,
                    ChannelName = video.ChannelName,
                    DurationSeconds = video.Duration?.TotalSeconds,
                    ThumbnailUrl = video.ThumbnailUrl,
                    ViewCount = video.ViewCount,
                    ViewCountObservedAtUnix = video.ViewCountObservedAtUnix,
                    UploadDate = video.UploadDate,
                    ChannelId = video.ChannelId
                }).ToList();
                Plugin.Cfg.YouTubeSubscriptionVideoCacheUpdatedUtc = DateTime.UtcNow;
                Plugin.Cfg.Save();
                
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (youtubeFeedLifetime.IsCancellationRequested) { }
        catch (YouTubeSessionExpiredException)
        {
            if (Volatile.Read(ref disposeStarted) != 0) return;
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (!IsCurrentYouTubeSubscriptionFetch(sessionGeneration)) return;
                SetYouTubeSessionNeedsReconnect(true);
                youtubeSubscriptionVideosError = "YouTube session expired. Reconnect your account in Settings.";
            }).ConfigureAwait(false);
            AepLog.Warning("[YouTube/Subscriptions] Saved account cookies were rejected.");
        }
        catch (Exception exception)
        {
            YouTubeRequestScheduler.Shared.ReportFailure(YouTubeRequestIdentity.Account, exception.Message);
            if (Volatile.Read(ref disposeStarted) != 0) return;
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (IsCurrentYouTubeSubscriptionFetch(sessionGeneration))
                    youtubeSubscriptionVideosError = "Could not fetch YouTube subscriptions. Try Fetch Now later or refresh your YouTube session.";
            }).ConfigureAwait(false);
            AepLog.Warning("[YouTube/Subscriptions] [redacted]");
        }
        finally
        {
            if (Volatile.Read(ref disposeStarted) == 0)
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (Volatile.Read(ref disposeStarted) == 0 && youtubeSubscriptionFetchId == fetchId)
                        isSyncingYouTubeSubscriptionVideos = false;
                }).ConfigureAwait(false);
        }
    }

    private void ClearYouTubeSubscriptionVideoCache()
    {
        Interlocked.Increment(ref youtubeLikedVideoSessionGeneration);
        ++youtubeSubscriptionFetchId;
        isSyncingYouTubeSubscriptionVideos = false;
        youtubeSubscriptionFeedResults = [];
        youtubeSubscriptionVideosError = null;
        Plugin.Cfg.YouTubeSubscriptionVideoCache.Clear();
        Plugin.Cfg.YouTubeSubscriptionVideoCacheUpdatedUtc = default;
        Plugin.Cfg.YouTubeSubscriptionVideosLastAttemptUtc = default;
        Plugin.Cfg.YouTubeSubscriptionVideosLastManualFetchUtc = default;
        Plugin.Cfg.Save();
    }

}

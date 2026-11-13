using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.YouTube;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private const int YouTubeLikedVideoCacheLimit = 300;
    private static readonly TimeSpan YouTubeLikedVideoRefreshInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan YouTubeLikedVideoRetryInterval = TimeSpan.FromHours(1);
    private List<VideoSearchEntry>? youtubeLikedVideoResults;
    private volatile bool isSyncingYouTubeLikedVideos;
    private string? youtubeLikedVideosError;
    private int youtubeLikedVideoSessionGeneration;
    private int youtubeLikedVideoFetchId;

    private bool IsCurrentYouTubeLikedVideoFetch(int generation) =>
        Volatile.Read(ref disposeStarted) == 0 && !youtubeFeedLifetime.IsCancellationRequested &&
        Volatile.Read(ref youtubeLikedVideoSessionGeneration) == generation && CanSyncYouTubeLikedVideos;

    private bool CanSyncYouTubeLikedVideos =>
        Plugin.Cfg.SyncYouTubeLikedVideos &&
        Plugin.Cfg.YouTubeEmbeddedBrowserSessionEnabled &&
        File.Exists(YouTubeEmbeddedBrowserSession.CookieFilePath);

    private void RestoreYouTubeLikedVideoCache()
    {
        if (youtubeLikedVideoResults is not null)
            return;

        if (Plugin.Cfg.YouTubeLikedVideoCache.RemoveAll(record =>
                !YouTubeLikedVideosFetcher.HasUsableMetadata(record.Title, record.ChannelName)) > 0)
            Plugin.Cfg.Save();

        youtubeLikedVideoResults = Plugin.Cfg.YouTubeLikedVideoCache
            .Where(record => !string.IsNullOrWhiteSpace(record.Url) &&
                YouTubeLikedVideosFetcher.HasUsableMetadata(record.Title, record.ChannelName))
            .Take(YouTubeLikedVideoCacheLimit)
            .Select(record => new VideoSearchEntry(
                record.Title,
                record.Url,
                record.ChannelName,
                record.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                record.ThumbnailUrl,
                record.ViewCount,
                record.UploadDate,
                record.ChannelId, record.ViewCountObservedAtUnix))
            .ToList();
    }

    private void RefreshYouTubeLikedVideosIfDue()
    {
        if (!CanSyncYouTubeLikedVideos || isSyncingYouTubeLikedVideos)
            return;

        var now = DateTime.UtcNow;
        if (now - Plugin.Cfg.YouTubeLikedVideoCacheUpdatedUtc >= YouTubeLikedVideoRefreshInterval &&
            now - Plugin.Cfg.YouTubeLikedVideosLastAttemptUtc >= YouTubeLikedVideoRetryInterval)
            _ = FetchYouTubeLikedVideosAsync();
    }

    private async Task FetchYouTubeLikedVideosAsync(bool manual = false)
    {
        if (!CanSyncYouTubeLikedVideos || isSyncingYouTubeLikedVideos)
            return;

        var now = DateTime.UtcNow;
        if (manual && now - Plugin.Cfg.YouTubeLikedVideosLastManualFetchUtc < YouTubeLikedVideoRetryInterval)
            return;

        var ytDlpPath = screenController.Engine.Resources.GetLocationYTDLP();
        var cookiesPath = YouTubeEmbeddedBrowserSession.CookieFilePath;
        if (string.IsNullOrWhiteSpace(ytDlpPath) || !File.Exists(ytDlpPath))
        {
            youtubeLikedVideosError = "yt-dlp is still being prepared. Try again shortly.";
            return;
        }

        isSyncingYouTubeLikedVideos = true;
        youtubeLikedVideosError = null;
        var sessionGeneration = Volatile.Read(ref youtubeLikedVideoSessionGeneration);
        var fetchId = ++youtubeLikedVideoFetchId;
        Plugin.Cfg.YouTubeLikedVideosLastAttemptUtc = now;
        if (manual)
            Plugin.Cfg.YouTubeLikedVideosLastManualFetchUtc = now;
        Plugin.Cfg.Save();

        try
        {
            var fetched = await YouTubeLikedVideosFetcher.FetchAsync(
                ytDlpPath, cookiesPath, youtubeFeedToken,
                manual ? YouTubeRequestPriority.Manual : YouTubeRequestPriority.Background)
                .ConfigureAwait(false);
            if (Volatile.Read(ref disposeStarted) != 0) return;
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (!IsCurrentYouTubeLikedVideoFetch(sessionGeneration)) return;
                SetYouTubeSessionNeedsReconnect(false);
                RestoreYouTubeLikedVideoCache();
                var previousById = (youtubeLikedVideoResults ?? [])
                    .Select(video => (Id: GetYouTubeVideoId(video.Url), Video: video))
                    .Where(item => item.Id is not null)
                    .GroupBy(item => item.Id!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Video, StringComparer.OrdinalIgnoreCase);

                // Fresh entries lead the list. Older entries remain until the
                // separate 300-video account cache is full.
                var merged = new List<VideoSearchEntry>(YouTubeLikedVideoCacheLimit);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var video in fetched.Concat(youtubeLikedVideoResults ?? []))
                {
                    var id = GetYouTubeVideoId(video.Url);
                    if (!YouTubeLikedVideosFetcher.HasUsableMetadata(video.Title, video.ChannelName) ||
                        id is null || !seen.Add(id))
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
                    if (merged.Count == YouTubeLikedVideoCacheLimit)
                        break;
                }

                // Publish the feed before its individual metadata lookups run.
                youtubeLikedVideoResults = merged;
                Plugin.Cfg.YouTubeLikedVideoCache = merged.Select(video => new CachedBrowseVideoRecord
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
                Plugin.Cfg.YouTubeLikedVideoCacheUpdatedUtc = DateTime.UtcNow;
                Plugin.Cfg.Save();
                
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (youtubeFeedLifetime.IsCancellationRequested) { }
        catch (YouTubeSessionExpiredException)
        {
            if (Volatile.Read(ref disposeStarted) != 0) return;
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (!IsCurrentYouTubeLikedVideoFetch(sessionGeneration)) return;
                SetYouTubeSessionNeedsReconnect(true);
                youtubeLikedVideosError = "YouTube session expired. Reconnect your account in Settings.";
            }).ConfigureAwait(false);
            AepLog.Warning("[YouTube/Likes] Saved account cookies were rejected.");
        }
        catch (Exception exception)
        {
            YouTubeRequestScheduler.Shared.ReportFailure(YouTubeRequestIdentity.Account, exception.Message);
            if (Volatile.Read(ref disposeStarted) != 0) return;
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (IsCurrentYouTubeLikedVideoFetch(sessionGeneration))
                    youtubeLikedVideosError = "Could not fetch YouTube likes. Try Fetch Now later or refresh your YouTube session.";
            }).ConfigureAwait(false);
            AepLog.Warning("[YouTube/Likes] [redacted]");
        }
        finally
        {
            if (Volatile.Read(ref disposeStarted) == 0)
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (Volatile.Read(ref disposeStarted) == 0 &&
                        youtubeLikedVideoFetchId == fetchId)
                        isSyncingYouTubeLikedVideos = false;
                }).ConfigureAwait(false);
        }
    }

    private void ClearYouTubeLikedVideoCache()
    {
        Interlocked.Increment(ref youtubeLikedVideoSessionGeneration);
        ++youtubeLikedVideoFetchId;
        isSyncingYouTubeLikedVideos = false;
        youtubeLikedVideoResults = [];
        youtubeLikedVideosError = null;
        Plugin.Cfg.YouTubeLikedVideoCache.Clear();
        Plugin.Cfg.YouTubeLikedVideoCacheUpdatedUtc = default;
        Plugin.Cfg.YouTubeLikedVideosLastAttemptUtc = default;
        Plugin.Cfg.YouTubeLikedVideosLastManualFetchUtc = default;
        Plugin.Cfg.Save();
    }

}

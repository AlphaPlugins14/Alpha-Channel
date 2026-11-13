using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.YouTube;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly TimeSpan YouTubeRecommendationRefreshInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan YouTubeRecommendationRetryInterval = TimeSpan.FromHours(1);
    private int youtubeRecommendationFetchActive;
    private bool isSyncingYouTubeRecommendedVideos => Volatile.Read(ref youtubeRecommendationFetchActive) != 0;
    private string? youtubeRecommendedVideosError;

    private bool CanSyncYouTubeRecommendedVideos => Plugin.Cfg.SyncYouTubeRecommendedVideos &&
        !youtubeBrowserConnectionBusy &&
        Plugin.Cfg.YouTubeEmbeddedBrowserSessionEnabled &&
        File.Exists(YouTubeEmbeddedBrowserSession.CookieFilePath);

    private void RefreshYouTubeRecommendedVideosIfDue()
    {
        var now = DateTime.UtcNow;
        if (CanSyncYouTubeRecommendedVideos && !isSyncingYouTubeRecommendedVideos &&
            now - Plugin.Cfg.YouTubeRecommendedVideoCacheUpdatedUtc >= YouTubeRecommendationRefreshInterval &&
            now - Plugin.Cfg.YouTubeRecommendedVideosLastAttemptUtc >= YouTubeRecommendationRetryInterval)
            _ = FetchYouTubeRecommendedVideosAsync();
    }

    private async Task FetchYouTubeRecommendedVideosAsync(bool manual = false)
    {
        var now = DateTime.UtcNow;
        if (!CanSyncYouTubeRecommendedVideos || youtubeFeedLifetime.IsCancellationRequested ||
            (manual && now - Plugin.Cfg.YouTubeRecommendedVideosLastManualFetchUtc < YouTubeRecommendationRetryInterval))
            return;
        var ytDlpPath = screenController.Engine.Resources.GetLocationYTDLP();
        if (string.IsNullOrWhiteSpace(ytDlpPath) || !File.Exists(ytDlpPath))
        {
            youtubeRecommendedVideosError = "yt-dlp is still being prepared. Try again shortly.";
            return;
        }
        if (Interlocked.CompareExchange(ref youtubeRecommendationFetchActive, 1, 0) != 0) return;
        var generation = Volatile.Read(ref youtubeLikedVideoSessionGeneration);
        bool SessionStillCurrent() => !youtubeFeedLifetime.IsCancellationRequested &&
            Volatile.Read(ref disposeStarted) == 0 && CanSyncYouTubeRecommendedVideos &&
            Volatile.Read(ref youtubeLikedVideoSessionGeneration) == generation;
        youtubeRecommendedVideosError = null;
        Plugin.Cfg.YouTubeRecommendedVideosLastAttemptUtc = now;
        if (manual) Plugin.Cfg.YouTubeRecommendedVideosLastManualFetchUtc = now;
        try
        {
            Plugin.Cfg.Save();
            var fetched = await YouTubeSubscriptionFeedFetcher.FetchRecommendationsAsync(ytDlpPath,
                YouTubeEmbeddedBrowserSession.CookieFilePath, youtubeFeedToken,
                manual ? YouTubeRequestPriority.Manual : YouTubeRequestPriority.Background).ConfigureAwait(false);
            if (!SessionStillCurrent()) return;
            // Publish cache changes on the framework thread so settings/disconnect
            // cannot interleave with committing another account's results.
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (!SessionStillCurrent()) return;
                var merged = YouTubeRecommendationCache.Merge(fetched,
                    Plugin.Cfg.YouTubeRecommendedVideoCache.Select(ToRecommendedEntry));
                Plugin.Cfg.YouTubeRecommendedVideoCache = merged.Select(video => new CachedBrowseVideoRecord
                {
                    Title = video.Title, Url = video.Url, ChannelName = video.ChannelName,
                    DurationSeconds = video.Duration?.TotalSeconds, ThumbnailUrl = video.ThumbnailUrl,
                    ViewCount = video.ViewCount, ViewCountObservedAtUnix = video.ViewCountObservedAtUnix,
                    UploadDate = video.UploadDate, ChannelId = video.ChannelId
                }).ToList();
                Plugin.Cfg.YouTubeRecommendedVideoCacheUpdatedUtc = DateTime.UtcNow;
                SetYouTubeSessionNeedsReconnect(false);
                Plugin.Cfg.Save();
                youtubeBrowseRecommendedVideos = null;
                
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (youtubeFeedLifetime.IsCancellationRequested) { }
        catch (YouTubeSessionExpiredException)
        {
            if (!SessionStillCurrent()) return;
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (!SessionStillCurrent()) return;
                SetYouTubeSessionNeedsReconnect(true);
                youtubeRecommendedVideosError = "YouTube session expired. Reconnect your account in Settings.";
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (!SessionStillCurrent()) return;
            YouTubeRequestScheduler.Shared.ReportFailure(YouTubeRequestIdentity.Account, exception.Message);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (SessionStillCurrent())
                    youtubeRecommendedVideosError = "Could not fetch YouTube recommendations. Try Refresh later or reconnect your account.";
            }).ConfigureAwait(false);
            AepLog.Warning("[YouTube/Recommended] Account feed fetch failed.");
        }
        finally { Interlocked.Exchange(ref youtubeRecommendationFetchActive, 0); }
    }

    private void ClearYouTubeRecommendedVideoCache()
    {
        Interlocked.Increment(ref youtubeLikedVideoSessionGeneration);
        Plugin.Cfg.YouTubeRecommendedVideoCache.Clear();
        Plugin.Cfg.YouTubeRecommendedVideoCacheUpdatedUtc = default;
        Plugin.Cfg.YouTubeRecommendedVideosLastAttemptUtc = default;
        Plugin.Cfg.YouTubeRecommendedVideosLastManualFetchUtc = default;
        youtubeRecommendedVideosError = null;
        youtubeBrowseRecommendedVideos = null;
        Plugin.Cfg.Save();
    }
}

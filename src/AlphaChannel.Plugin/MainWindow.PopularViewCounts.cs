using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.YouTube;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly Dictionary<string, DateTime> popularCountNextAttempt = new(StringComparer.Ordinal);
    private bool popularCountLookupBusy;
    private bool IsPopularMetadataView => currentPage == HomePage.VideoGrid && browseVideoSectionTab == 4 &&
        !youtubeBrowseShowingSearch && viewedChannelId is null;

    private static PopularVideoMetadata PopularContribution(VideoSearchEntry entry)
    {
        static string? Clean(string? text, int limit) => text is null ? null :
            new string(text.Where(character => !char.IsControl(character)).Take(limit).ToArray()).Trim();
        return new(YouTubeMetadataCache.HasTitle(entry.Title) ? Clean(entry.Title, 512) : null,
            Clean(entry.ChannelName, 256), Clean(entry.ChannelId, 128), entry.ThumbnailUrl,
            entry.Duration?.TotalSeconds, entry.UploadDate, entry.ViewCount,
            entry.ViewCountObservedAtUnix, entry.MetadataObservedAtUnix);
    }

    private static VideoSearchEntry PopularMetadataEntry(string id, PopularVideoMetadata metadata) => new(
        metadata.Title ?? string.Empty, "https://www.youtube.com/watch?v=" + id, metadata.ChannelName ?? string.Empty,
        metadata.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
        metadata.ThumbnailUrl, metadata.ViewCount, metadata.UploadDate, metadata.ChannelId,
        metadata.ViewCountObservedAtUnix, metadata.MetadataObservedAtUnix);

    private void ApplyPopularSharedViewCount(PopularVideoRecord record, long? count, long? observedAt)
    {
        if (count is null) return;
        var merged = YouTubeMetadataCache.Merge(PopularEntry(record),
            PopularEntry(record) with { ViewCount = count, ViewCountObservedAtUnix = observedAt });
        record.ViewCount = merged.ViewCount; record.ViewCountObservedAtUnix = merged.ViewCountObservedAtUnix;
        searchResolver.RememberVideoMetadata([merged]);
    }

    private void ApplyPopularSharedMetadata(PopularVideoRecord record, PopularVideoMetadata? metadata, long refreshAfter)
    {
        record.SharedMetadataKnown = metadata?.IsComplete == true;
        record.SharedMetadataRefreshAfterUnix = refreshAfter;
        if (metadata is null) return;
        var merged = YouTubeMetadataCache.Merge(PopularEntry(record), PopularMetadataEntry(record.VideoId, metadata));
        record.Title = merged.Title; record.ChannelName = merged.ChannelName; record.ChannelId = merged.ChannelId;
        record.ThumbnailUrl = merged.ThumbnailUrl; record.DurationSeconds = merged.Duration?.TotalSeconds;
        record.UploadDate = merged.UploadDate; record.ViewCount = merged.ViewCount;
        record.ViewCountObservedAtUnix = merged.ViewCountObservedAtUnix; record.MetadataObservedAtUnix = merged.MetadataObservedAtUnix;
        record.SharedViewCountKnown = metadata.ViewCount is not null;
        searchResolver.RememberVideoMetadata([merged]);
    }

    private void ObservePopularViewCount(VideoSearchEntry entry)
    {
        if (popularCountLookupBusy || CurrentSession is not { } session || youtubeFeedToken.IsCancellationRequested) return;
        var id = GetYouTubeVideoId(entry.Url);
        var record = Plugin.Cfg.YouTubeSharedPopularVideos.FirstOrDefault(video => video.VideoId == id);
        if (id is null || record is null ||
            record.SharedMetadataKnown && record.SharedMetadataRefreshAfterUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds() ||
            popularCountNextAttempt.TryGetValue(id, out var retry) && DateTime.UtcNow < retry) return;
        if (popularCountNextAttempt.Count > 1000)
            foreach (var old in popularCountNextAttempt.Where(pair => pair.Value <= DateTime.UtcNow).Select(pair => pair.Key).ToArray())
                popularCountNextAttempt.Remove(old);
        popularCountNextAttempt[id] = DateTime.UtcNow.AddMinutes(15);
        popularCountLookupBusy = true;
        var generation = popularListGeneration;
        _ = Task.Run(async () =>
        {
            var phase = YouTubeFetchMethod.CloudflareClaim;
            async Task Publish(PopularVideoMetadata? metadata, long refreshAfter, VideoSearchEntry? local = null)
            {
                if (metadata is null && local is null) return;
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (Volatile.Read(ref disposeStarted) != 0 || !IsCurrentMessageSession(session) || generation != popularListGeneration) return;
                    var current = Plugin.Cfg.YouTubeSharedPopularVideos.FirstOrDefault(video => video.VideoId == id);
                    if (current is null) return;
                    ApplyPopularSharedMetadata(current, metadata, refreshAfter);
                    PublishVisibleYouTubeMetadata(local ?? PopularEntry(current));
                }).ConfigureAwait(false);
            }
            try
            {
                var claim = await popularWorker.ClaimMetadataAsync(session.Token, id,
                    PopularContribution(entry), youtubeFeedToken).ConfigureAwait(false);
                await Publish(claim.Metadata, claim.RefreshAfterUnix).ConfigureAwait(false);
                if (claim.State == "claimed")
                {
                    VideoSearchEntry? details = null;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(youtubeFeedToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(20));
                    try
                    {
                        // Refresh existing stale records, but reuse complete local
                        // contributions for records which simply lack shared fields.
                        details = await searchResolver.GetVideoEntryAsync(entry.Url, deadline.Token,
                            forceRefresh: claim.Metadata?.IsComplete == true,
                            priority: YouTubeRequestPriority.Background, section: YouTubeFetchSection.Popular,
                            data: YouTubeFetchData.FullMetadata, bypassSharedCache: true).ConfigureAwait(false);
                    }
                    catch (Exception error) when (!youtubeFeedToken.IsCancellationRequested)
                    {
                        AepLog.YouTubeFetchFailure(YouTubeFetchDiagnostic.Classify(YouTubeFetchSection.Popular,
                            YouTubeFetchData.FullMetadata, YouTubeFetchMethod.FullDetails, error));
                    }
                    // Keep local success even if Cloudflare reporting is unavailable.
                    if (details is not null) await Publish(claim.Metadata, claim.RefreshAfterUnix, details).ConfigureAwait(false);
                    phase = YouTubeFetchMethod.CloudflareReport;
                    var report = await popularWorker.ReportMetadataAsync(session.Token, id, claim.LeaseId!,
                        details is null ? null : PopularContribution(details), youtubeFeedToken).ConfigureAwait(false);
                    await Publish(report.Metadata, report.RefreshAfterUnix).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (youtubeFeedToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                AepLog.YouTubeFetchFailure(YouTubeFetchDiagnostic.Classify(YouTubeFetchSection.Popular,
                    YouTubeFetchData.FullMetadata, phase, error, error is PopularWorkerException worker ? worker.HttpStatus : 0));
            }
            finally
            {
                if (Volatile.Read(ref disposeStarted) == 0)
                    await Plugin.Framework.RunOnFrameworkThread(() => popularCountLookupBusy = false).ConfigureAwait(false);
            }
        });
    }
}

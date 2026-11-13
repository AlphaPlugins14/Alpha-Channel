using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.YouTube;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly YouTubeVisibleMetadataQueue youtubeVisibleMetadata = new();
    private bool youtubeCardMetadataLoading, youtubeMetadataSavePending;
    private long youtubeMetadataSaveAfter;
    private int youtubeMetadataRevision, youtubeMetadataReloadVersion;
    private (HomePage, int, bool, int, string?, int, int, int) youtubeMetadataView;
    private YouTubeFetchSection YouTubeMetadataSection => currentPage switch
    {
        HomePage.VideoGrid when viewedChannelId is not null => YouTubeFetchSection.Channel,
        HomePage.VideoGrid when youtubeBrowseShowingSearch => YouTubeFetchSection.Search,
        HomePage.VideoGrid => browseVideoSectionTab switch
        {
            0 => YouTubeFetchSection.Topics, 1 => YouTubeFetchSection.Subscriptions,
            2 => YouTubeFetchSection.Likes, 3 => YouTubeFetchSection.Recommended, _ => YouTubeFetchSection.Popular
        },
        HomePage.Home => YouTubeFetchSection.Home,
        HomePage.Player => YouTubeFetchSection.Playback,
        HomePage.UnifiedSearch => YouTubeFetchSection.Search,
        HomePage.PartyDirectory => YouTubeFetchSection.PartyDirectory,
        _ => YouTubeFetchSection.Background
    };

    private void BeginYouTubeCardMetadataView()
    {
        var view = (currentPage, browseVideoSectionTab, youtubeBrowseShowingSearch,
            youtubeSearchGeneration, viewedChannelId, youtubeLikedVideoSessionGeneration,
            youtubeMetadataReloadVersion, unifiedSearchGeneration);
        if (view != youtubeMetadataView)
        {
            youtubeMetadataView = view;
            youtubeVisibleMetadata.Reset();
        }
        youtubeVisibleMetadata.CancelOffscreen();
        if (youtubeMetadataSavePending && Environment.TickCount64 >= youtubeMetadataSaveAfter)
        { Plugin.Cfg.SavePublicStateDeferred(); youtubeMetadataSavePending = false; }
    }

    private VideoSearchEntry VisibleYouTubeCardMetadata(VideoSearchEntry entry)
    {
        if (!VideoUrlResolver.IsYouTubeUrl(entry.Url) || youtubeFeedToken.IsCancellationRequested) return entry;
        if (searchResolver.TryGetVideoMetadata(entry.Url, out var cached) && cached is not null)
        {
            var enriched = YouTubeMetadataCache.Merge(entry, cached) with { Url = entry.Url };
            // A later cache hit may be richer than the first hit in this view.
            // Persist each actual improvement rather than only the first one.
            if (entry != enriched) PublishVisibleYouTubeMetadata(enriched);
            entry = enriched;
        }
        if (IsPopularMetadataView && SharedYouTubeCache.Current is null)
        {
            ObservePopularViewCount(entry);
            // Authenticated clients coordinate full metadata through the Worker.
            // Signed-out viewers can still recover presentation via public oEmbed.
            if (CurrentSession is not null ||
                YouTubeMetadataCache.HasTitle(entry.Title) && !string.IsNullOrWhiteSpace(entry.ChannelName)) return entry;
        }
        SharedYouTubeCache.Current?.Observe(entry, YouTubeMetadataSection);
        if (!YouTubeMetadataCache.IsComplete(entry))
        {
            searchResolver.RememberVideoMetadata([entry]);
            if (youtubeVisibleMetadata.Observe(entry) && !youtubeCardMetadataLoading)
            { youtubeCardMetadataLoading = true; _ = ResolveVisibleAsync(); }
        }
        return entry;
    }

    private async Task ResolveVisibleAsync()
    {
        var section = YouTubeMetadataSection;
        try
        {
            while (!youtubeFeedToken.IsCancellationRequested && Volatile.Read(ref disposeStarted) == 0)
            {
                YouTubeVisibleMetadataQueue.Work? work = null;
                var presentationOnly = false;
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (Volatile.Read(ref disposeStarted) == 0)
                    { work = youtubeVisibleMetadata.TakeNext(youtubeFeedToken); presentationOnly = IsPopularMetadataView && SharedYouTubeCache.Current is null; section = YouTubeMetadataSection; }
                }).ConfigureAwait(false);
                if (work is null) break;
                var missingTitle = true;
                var missingViews = !presentationOnly && work.Entry.ViewCount is null;
                long deferMilliseconds = 0;
                try
                {
                    var details = await searchResolver.GetVisibleCardEntryAsync(work.Entry.Url, work.Token, presentationOnly, section).ConfigureAwait(false);
                    missingTitle = details is null || !YouTubeMetadataCache.HasTitle(details.Title);
                    missingViews = !presentationOnly && YouTubeMetadataCache.Merge(work.Entry, details ?? work.Entry).ViewCount is null;
                    if (details is not null && !work.Token.IsCancellationRequested)
                        await Plugin.Framework.RunOnFrameworkThread(() =>
                        {
                            if (Volatile.Read(ref disposeStarted) == 0 && !work.Token.IsCancellationRequested)
                                PublishVisibleYouTubeMetadata(YouTubeMetadataCache.Merge(work.Entry, details) with { Url = work.Entry.Url });
                        }).ConfigureAwait(false);
                }
                catch (YouTubeMetadataDeferredException deferred)
                { deferMilliseconds = Math.Max(1,(long)(deferred.ResumeUtc-DateTime.UtcNow).TotalMilliseconds); }
                catch (OperationCanceledException) when (work.Token.IsCancellationRequested) { }
                catch (Exception error)
                { AepLog.YouTubeFetchFailure(YouTubeFetchDiagnostic.Classify(section,
                    presentationOnly ? YouTubeFetchData.TitleAndChannel : YouTubeFetchData.FullMetadata,
                    presentationOnly ? YouTubeFetchMethod.OEmbed : YouTubeFetchMethod.FullDetails, error)); }
                finally
                {
                    try
                    {
                        if (Volatile.Read(ref disposeStarted) == 0)
                            await Plugin.Framework.RunOnFrameworkThread(() => youtubeVisibleMetadata.Complete(work, retryMissingTitle: missingTitle,
                                deferMilliseconds: deferMilliseconds, retryMissingViews: missingViews)).ConfigureAwait(false);
                    }
                    finally { work.Dispose(); }
                }
            }
        }
        catch (OperationCanceledException) when (youtubeFeedToken.IsCancellationRequested) { }
        catch (Exception error)
        { if (Volatile.Read(ref disposeStarted) == 0) AepLog.YouTubeFetchFailure(YouTubeFetchDiagnostic.Classify(section,
            YouTubeFetchData.VisibleQueue, YouTubeFetchMethod.Queue, error)); }
        finally
        {
            if (Volatile.Read(ref disposeStarted) == 0)
                await Plugin.Framework.RunOnFrameworkThread(() => youtubeCardMetadataLoading = false).ConfigureAwait(false);
        }
    }

    private void PublishVisibleYouTubeMetadata(VideoSearchEntry details)
    {
        details = YouTubeMetadataCache.Normalize(details);
        var id = GetYouTubeVideoId(details.Url);
        void Update(List<VideoSearchEntry>? list)
        {
            if (list is null) return;
            for (var index = 0; index < list.Count; index++)
                if (GetYouTubeVideoId(list[index].Url) == id)
                    list[index] = YouTubeMetadataCache.Merge(list[index], details) with { Url = list[index].Url };
        }
        Update(searchResults); Update(youtubeLikedVideoResults); Update(youtubeSubscriptionFeedResults);
        Update(ffxivYouTubeResults); Update(popularPageVideos);
        Update(homeYouTubeResults); Update(favouriteVideoResults);
        if(unifiedYouTubeResults.Any(entry=>GetYouTubeVideoId(entry.Url)==id))
            unifiedYouTubeResults=unifiedYouTubeResults.Select(entry=>GetYouTubeVideoId(entry.Url)==id
                ? YouTubeMetadataCache.Merge(entry,details) with {Url=entry.Url}:entry).ToArray();
        void UpdateGroups(Dictionary<string, List<VideoSearchEntry>>? groups)
        {
            if (groups is null) return;
            // Replace lists: discovery workers may own a snapshot of the old lists.
            foreach (var key in groups.Keys.ToArray())
                if (groups[key].Any(entry => GetYouTubeVideoId(entry.Url) == id))
                    groups[key] = groups[key].Select(entry => GetYouTubeVideoId(entry.Url) == id
                        ? YouTubeMetadataCache.Merge(entry, details) with { Url = entry.Url } : entry).ToList();
        }
        UpdateGroups(browseVideoResults); UpdateGroups(subscriptionVideoResults);
        if (youtubeBrowseRecommendedVideos is { } recommendations)
            for (var index = 0; index < recommendations.Count; index++)
                if (GetYouTubeVideoId(recommendations[index].Video.Url) == id)
                    recommendations[index] = recommendations[index] with
                    { Video = YouTubeMetadataCache.Merge(recommendations[index].Video, details) };
        var cfg = Plugin.Cfg;
        var records = cfg.YouTubeLikedVideoCache.Concat(cfg.YouTubeSubscriptionVideoCache)
            .Concat(cfg.YouTubeRecommendedVideoCache).Concat(cfg.FavouriteVideoCache)
            .Concat(cfg.FfxivVideoCache).Concat(cfg.FeaturedSlideCache)
            .Concat(cfg.BrowseVideoTopicCache.Values.SelectMany(list => list))
            .Concat(cfg.SubscriptionVideoChannelCache.Values.SelectMany(list => list));
        foreach (var record in records)
        {
            if (GetYouTubeVideoId(record.Url) != id) continue;
            var merged = YouTubeMetadataCache.Merge(new VideoSearchEntry(record.Title, record.Url,
                record.ChannelName, record.DurationSeconds is { } seconds && double.IsFinite(seconds) && seconds >= 0 &&
                    seconds < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : null,
                record.ThumbnailUrl, record.ViewCount, record.UploadDate, record.ChannelId, record.ViewCountObservedAtUnix), details);
            record.Title = merged.Title; record.ChannelName = merged.ChannelName;
            record.ChannelId = merged.ChannelId; record.DurationSeconds = merged.Duration?.TotalSeconds;
            record.ThumbnailUrl = merged.ThumbnailUrl; record.ViewCount = merged.ViewCount; record.UploadDate = merged.UploadDate;
            record.ViewCountObservedAtUnix = merged.ViewCountObservedAtUnix;
            youtubeMetadataSavePending = true;
        }
        foreach (var record in cfg.YouTubeSharedPopularVideos)
        {
            if (record.VideoId != id) continue;
            var merged = YouTubeMetadataCache.Merge(PopularEntry(record), details);
            record.Title = merged.Title; record.ChannelName = merged.ChannelName; record.ChannelId = merged.ChannelId;
            record.ThumbnailUrl = merged.ThumbnailUrl; record.DurationSeconds = merged.Duration?.TotalSeconds;
            record.ViewCount = merged.ViewCount; record.UploadDate = merged.UploadDate;
            record.ViewCountObservedAtUnix = merged.ViewCountObservedAtUnix;
            record.MetadataObservedAtUnix = merged.MetadataObservedAtUnix;
            youtubeMetadataSavePending = true;
        }
        youtubeMetadataRevision++;
        if (youtubeMetadataSavePending && youtubeMetadataSaveAfter <= Environment.TickCount64)
            youtubeMetadataSaveAfter = Environment.TickCount64 + 2000;
    }
}

using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.YouTube;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private int browseVideoSortMode;

    private readonly HashSet<string> browseSelectedTopics =
    new(
        StringComparer.Ordinal);


    // ---------------------------------------------------------
    // Browse Videos main tabs
    // 0 = Topics
    // 1 = Subscriptions
    // 2 = Liked Videos
    // 3 = Recommended
    // 4 = Popular
    // ---------------------------------------------------------

    private int browseVideoSectionTab = 4;
    private int previousBrowseVideoSectionTab = 4;

    // ---------------------------------------------------------
    // Favourite Videos tab
    // ---------------------------------------------------------

    private static readonly TimeSpan FavouriteVideoCacheDuration =
        TimeSpan.FromHours(3);

    private volatile List<VideoSearchEntry>?
        favouriteVideoResults;

    private volatile bool isLoadingFavouriteVideos;

    private string lastFavouriteVideoSignature =
        string.Empty;

    private DateTime favouriteVideoCacheUpdatedUtc;

    private int favouriteVideoSortMode =
    3;

    // ---------------------------------------------------------
    // Subscriptions tab
    // ---------------------------------------------------------

    private static readonly TimeSpan SubscriptionVideoCacheDuration =
        TimeSpan.FromHours(3);

    private volatile Dictionary<string, List<VideoSearchEntry>>?
        subscriptionVideoResults;

    private readonly HashSet<string> selectedSubscriptionChannels =
        new(StringComparer.OrdinalIgnoreCase);

    private int subscriptionVideoSortMode = 1;


    private volatile bool isLoadingSubscriptionVideos;

    private string lastSubscriptionSignature =
        string.Empty;

    private DateTime subscriptionVideoCacheUpdatedUtc;
    private DateTime subscriptionVideoLastAttemptUtc;

    private int subscriptionVideoExpectedChannelCount;

    private CancellationTokenSource? subscriptionVideosCts;

    private int subscriptionVideoLoadGeneration;

    private static string GetFavouriteVideoSignature(
    IEnumerable<string> videoIds)
    {
        //
        // Do not sort this signature. Favourite list order represents the
        // order in which videos were most recently favourited.
        //
        return string.Join(
            "\u001F",
            videoIds
                .Where(
                    id =>
                        !string.IsNullOrWhiteSpace(
                            id))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase));
    }

    private void RestorePersistedFavouriteVideoCache()
    {
        if (favouriteVideoResults is not null)
        {
            return;
        }

        if (Plugin.Cfg.FavouriteVideoCache.Count == 0)
        {
            return;
        }

        try
        {
            favouriteVideoResults =
                Plugin.Cfg.FavouriteVideoCache
                    .Where(
                        video =>
                            !string.IsNullOrWhiteSpace(
                                video.Url))
                    .Select(
                        video =>
                            new VideoSearchEntry(
                                video.Title,
                                video.Url,
                                video.ChannelName,
                                video.DurationSeconds is { } seconds
                                    ? TimeSpan.FromSeconds(
                                        seconds)
                                    : null,
                                video.ThumbnailUrl,
                                video.ViewCount,
                                video.UploadDate,
                                video.ChannelId, video.ViewCountObservedAtUnix))
                    .ToList();

            favouriteVideoCacheUpdatedUtc =
                Plugin.Cfg.FavouriteVideoCacheUpdatedUtc;

            lastFavouriteVideoSignature =
                Plugin.Cfg.FavouriteVideoCacheSignature ??
                string.Empty;
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[Favourites] Failed to restore cached videos.");

            favouriteVideoResults =
                null;

            Plugin.Cfg.FavouriteVideoCache.Clear();

            Plugin.Cfg.FavouriteVideoCacheUpdatedUtc =
                default;

            Plugin.Cfg.FavouriteVideoCacheSignature =
                null;

            Plugin.Cfg.Save();
        }
    }

    private void PersistFavouriteVideoCache()
    {
        Plugin.Cfg.FavouriteVideoCache =
            favouriteVideoResults?
                .Select(
                    video =>
                        new CachedBrowseVideoRecord
                        {
                            Title =
                                video.Title,

                            Url =
                                video.Url,

                            ChannelName =
                                video.ChannelName,

                            DurationSeconds =
                                video.Duration?.TotalSeconds,

                            ThumbnailUrl =
                                video.ThumbnailUrl,

                            ViewCount =
                                video.ViewCount,

                            ViewCountObservedAtUnix = video.ViewCountObservedAtUnix,

                            UploadDate =
                                video.UploadDate,

                            ChannelId =
                                video.ChannelId
                        })
                .ToList() ??
            [];

        Plugin.Cfg.FavouriteVideoCacheUpdatedUtc =
            favouriteVideoCacheUpdatedUtc;

        Plugin.Cfg.FavouriteVideoCacheSignature =
            lastFavouriteVideoSignature;

        Plugin.Cfg.Save();
    }

    private Task LoadFavouriteVideosAsync(
        bool forceRefresh = false)
    {
        if (isLoadingFavouriteVideos)
        {
            return Task.CompletedTask;
        }

        RestorePersistedFavouriteVideoCache();

        var ids =
            Plugin.Cfg.FavouriteYouTubeVideoIds
                .Where(
                    id =>
                        !string.IsNullOrWhiteSpace(
                            id))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        var signature =
            GetFavouriteVideoSignature(
                ids);

        var cacheIsFresh =
            !forceRefresh &&
            favouriteVideoResults is not null &&
            string.Equals(
                lastFavouriteVideoSignature,
                signature,
                StringComparison.Ordinal) &&
            DateTime.UtcNow -
            favouriteVideoCacheUpdatedUtc <
            FavouriteVideoCacheDuration;

        if (cacheIsFresh)
        {
            return Task.CompletedTask;
        }

        isLoadingFavouriteVideos =
            true;

        try
        {
            if (ids.Count == 0)
            {
                favouriteVideoResults =
                    [];

                lastFavouriteVideoSignature =
                    string.Empty;

                favouriteVideoCacheUpdatedUtc =
                    DateTime.UtcNow;

                PersistFavouriteVideoCache();

                return Task.CompletedTask;
            }

            //
            // Retain every still-favourited cached entry. A favourite-list
            // change must never cause those videos to be resolved again.
            //
            var cachedById =
                new Dictionary<string, VideoSearchEntry>(
                    StringComparer.OrdinalIgnoreCase);

            if (favouriteVideoResults is not null)
            {
                foreach (var cachedVideo in
                         favouriteVideoResults)
                {
                    var cachedId =
                        GetYouTubeVideoId(
                            cachedVideo.Url);

                    if (cachedId is not null &&
                        ids.Contains(
                            cachedId,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        cachedById.TryAdd(
                            cachedId,
                            cachedVideo);
                    }
                }
            }

            // Build every card without network requests. Missing details are
            // resolved by the shared visible-card queue when the user scrolls to it.
            foreach (var id in ids)
            {
                var url = $"https://www.youtube.com/watch?v={id}";
                var entry = cachedById.TryGetValue(id, out var saved)
                    ? saved
                    : new VideoSearchEntry(string.Empty, url, string.Empty, null,
                        $"https://i.ytimg.com/vi/{id}/hqdefault.jpg");
                if (searchResolver.TryGetVideoMetadata(url, out var shared) && shared is not null)
                    entry = YouTubeMetadataCache.Merge(entry, shared) with { Url = entry.Url };
                cachedById[id] = entry;
            }

            //
            // Rebuild using FavouriteYouTubeVideoIds order. This directly
            // supplies the "Recently Favourited" sort order.
            //
            favouriteVideoResults =
                ids
                    .Where(
                        cachedById.ContainsKey)
                    .Select(
                        id =>
                            cachedById[id])
                    .ToList();

            lastFavouriteVideoSignature =
                signature;

            favouriteVideoCacheUpdatedUtc =
                DateTime.UtcNow;

            PersistFavouriteVideoCache();
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[Favourites] Failed to load favourite videos.");

            favouriteVideoResults ??=
                [];
        }
        finally
        {
            isLoadingFavouriteVideos =
                false;
        }
        return Task.CompletedTask;
    }

    private void UpdateFavouriteVideoCacheAfterToggle(
    VideoSearchEntry video,
    bool isNowFavourite)
    {
        RestorePersistedFavouriteVideoCache();

        var videoId =
            GetYouTubeVideoId(
                video.Url);

        if (videoId is null)
        {
            return;
        }

        var updated =
            favouriteVideoResults?
                .Where(
                    existing =>
                        !string.Equals(
                            GetYouTubeVideoId(
                                existing.Url),
                            videoId,
                            StringComparison.OrdinalIgnoreCase))
                .ToList() ??
            [];

        if (isNowFavourite)
        {
            //
            // The card itself already contains the metadata, so insert it
            // directly without making any YouTube request.
            //
            updated.Insert(
                0,
                video);
        }

        var favouriteIds =
            Plugin.Cfg.FavouriteYouTubeVideoIds
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        var cachedById =
            updated
                .Select(
                    cached =>
                        (
                            Id:
                                GetYouTubeVideoId(
                                    cached.Url),
                            Video:
                                cached
                        ))
                .Where(
                    item =>
                        item.Id is not null)
                .ToDictionary(
                    item =>
                        item.Id!,
                    item =>
                        item.Video,
                    StringComparer.OrdinalIgnoreCase);

        favouriteVideoResults =
            favouriteIds
                .Where(
                    cachedById.ContainsKey)
                .Select(
                    id =>
                        cachedById[id])
                .ToList();

        lastFavouriteVideoSignature =
            GetFavouriteVideoSignature(
                favouriteIds);

        favouriteVideoCacheUpdatedUtc =
            DateTime.UtcNow;

        PersistFavouriteVideoCache();
    }

    private static string GetSubscriptionSignature(
      IEnumerable<string> channelIds)
    {
        return string.Join(
            "\u001F",
            channelIds
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    id =>
                        id,
                    StringComparer.OrdinalIgnoreCase));
    }

    private void RestorePersistedSubscriptionVideoCache()
    {
        if (subscriptionVideoResults is not null)
        {
            return;
        }

        var persisted =
            Plugin.Cfg.SubscriptionVideoChannelCache;

        if (Plugin.Cfg.SubscriptionVideoCacheUpdatedUtc ==
            default ||
            string.IsNullOrWhiteSpace(
                Plugin.Cfg.SubscriptionVideoCacheSignature))
        {
            return;
        }

        try
        {
            var restored =
                new Dictionary<string, List<VideoSearchEntry>>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var channel in persisted)
            {
                var videos =
                    channel.Value
                        .Where(
                            video =>
                                !string.IsNullOrWhiteSpace(
                                    video.Url))
                        .Select(
                            video =>
                                new VideoSearchEntry(
                                    video.Title,
                                    video.Url,
                                    video.ChannelName,
                                    video.DurationSeconds is { } seconds
                                        ? TimeSpan.FromSeconds(
                                            seconds)
                                        : null,
                                    video.ThumbnailUrl,
                                    video.ViewCount,
                                    video.UploadDate,
                                    video.ChannelId, video.ViewCountObservedAtUnix))
                        .ToList();

                if (videos.Count > 0)
                {
                    restored[channel.Key] =
                        videos;
                }
            }

            subscriptionVideoResults =
                restored;

            subscriptionVideoCacheUpdatedUtc =
                Plugin.Cfg.SubscriptionVideoCacheUpdatedUtc;

            lastSubscriptionSignature =
                Plugin.Cfg.SubscriptionVideoCacheSignature!;
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[Subscriptions] Failed to restore cached videos.");

            Plugin.Cfg.SubscriptionVideoChannelCache.Clear();

            Plugin.Cfg.SubscriptionVideoCacheUpdatedUtc =
                default;

            Plugin.Cfg.SubscriptionVideoCacheSignature =
                null;

            Plugin.Cfg.Save();
        }
    }

    private void PersistSubscriptionVideoCache()
    {
        if (subscriptionVideoResults is null ||
            string.IsNullOrWhiteSpace(
                lastSubscriptionSignature) ||
            subscriptionVideoCacheUpdatedUtc ==
            default)
        {
            return;
        }

        Plugin.Cfg.SubscriptionVideoChannelCache =
            subscriptionVideoResults.ToDictionary(
                channel =>
                    channel.Key,
                channel =>
                    channel.Value
                        .Select(
                            video =>
                                new CachedBrowseVideoRecord
                                {
                                    Title =
                                        video.Title,

                                    Url =
                                        video.Url,

                                    ChannelName =
                                        video.ChannelName,

                                    DurationSeconds =
                                        video.Duration?.TotalSeconds,

                                    ThumbnailUrl =
                                        video.ThumbnailUrl,

                                    ViewCount =
                                        video.ViewCount,

                                    ViewCountObservedAtUnix = video.ViewCountObservedAtUnix,

                                    UploadDate =
                                        video.UploadDate,

                                    ChannelId =
                                        video.ChannelId
                                })
                        .ToList(),
                StringComparer.OrdinalIgnoreCase);

        Plugin.Cfg.SubscriptionVideoCacheUpdatedUtc =
            subscriptionVideoCacheUpdatedUtc;

        Plugin.Cfg.SubscriptionVideoCacheSignature =
            lastSubscriptionSignature;

        Plugin.Cfg.Save();
    }

    private void ClearPersistedSubscriptionVideoCache()
    {
        Plugin.Cfg.SubscriptionVideoChannelCache.Clear();

        Plugin.Cfg.SubscriptionVideoCacheUpdatedUtc =
            default;

        Plugin.Cfg.SubscriptionVideoCacheSignature =
            null;

        Plugin.Cfg.Save();
    }

    private async Task LoadSubscriptionVideosAsync(
        bool forceRefresh = false)
    {
        RestorePersistedSubscriptionVideoCache();

        var channelIds =
            Plugin.Cfg.SubscribedYouTubeChannelIds
                .Where(
                    id =>
                        !string.IsNullOrWhiteSpace(
                            id))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        var signature =
            GetSubscriptionSignature(
                channelIds);

        var cacheIsUsable =
            !forceRefresh &&
            subscriptionVideoResults is not null &&
            string.Equals(
                lastSubscriptionSignature,
                signature,
                StringComparison.Ordinal) &&
            DateTime.UtcNow -
            subscriptionVideoCacheUpdatedUtc <
            SubscriptionVideoCacheDuration;

        if (cacheIsUsable)
        {
            subscriptionVideoExpectedChannelCount =
                channelIds.Count;

            isLoadingSubscriptionVideos =
                false;

            return;
        }

        subscriptionVideosCts?.Cancel();
        subscriptionVideoLastAttemptUtc = DateTime.UtcNow;

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(youtubeFeedToken);
        var token = cancellation.Token;

        subscriptionVideosCts =
            cancellation;

        var loadGeneration =
            Interlocked.Increment(
                ref subscriptionVideoLoadGeneration);

        var previousSignature =
            lastSubscriptionSignature;

        lastSubscriptionSignature =
            signature;

        subscriptionVideoExpectedChannelCount =
            channelIds.Count;

        isLoadingSubscriptionVideos =
            true;

        try
        {
            if (channelIds.Count == 0)
            {
                subscriptionVideoResults =
                    [];

                subscriptionVideoCacheUpdatedUtc =
                    DateTime.UtcNow;

                selectedSubscriptionChannels.Clear();

                ClearPersistedSubscriptionVideoCache();

                return;
            }

            var previousResultsMatch =
                subscriptionVideoResults is not null &&
                string.Equals(
                    previousSignature,
                    signature,
                    StringComparison.Ordinal);

            var workingResults =
                previousResultsMatch
                    ? new Dictionary<string, List<VideoSearchEntry>>(
                        subscriptionVideoResults!,
                        StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, List<VideoSearchEntry>>(
                        StringComparer.OrdinalIgnoreCase);

            subscriptionVideoResults =
                new Dictionary<string, List<VideoSearchEntry>>(
                    workingResults,
                    StringComparer.OrdinalIgnoreCase);

            const int batchSize =
                3;

            const int resultsPerChannel =
                15;

            for (var batchStart = 0;
                 batchStart < channelIds.Count;
                 batchStart += batchSize)
            {
                token
                    .ThrowIfCancellationRequested();

                var batch =
                    channelIds
                        .Skip(
                            batchStart)
                        .Take(
                            batchSize)
                        .ToList();

                var pendingChannels =
                    batch
                        .Select(
                            async channelId =>
                            {
                                //
                                // Channel enumeration is the fresh discovery
                                // request. Metadata enrichment uses the shared
                                // resolver cache whenever possible.
                                //
                                var uploads =
                                    await searchResolver
                                        .GetChannelUploadsAsync(
                                            channelId,
                                            resultsPerChannel,
                                            token,
                                            forceRefresh ? YouTubeRequestPriority.Manual : YouTubeRequestPriority.Background)
                                        .ConfigureAwait(false);

                                var uniqueUploads =
                                    uploads
                                        .GroupBy(
                                            video =>
                                                video.Url,
                                            StringComparer.OrdinalIgnoreCase)
                                        .Select(
                                            group =>
                                                group.First())
                                        .Take(
                                            resultsPerChannel)
                                        .ToList();

                                return
                                    (
                                        ChannelId: channelId,
                                        Videos: uniqueUploads
                                    );
                            })
                        .ToArray();

                var loadedBatch =
                    await Task.WhenAll(
                            pendingChannels)
                        .ConfigureAwait(false);

                token
                    .ThrowIfCancellationRequested();

                if (loadGeneration !=
                    subscriptionVideoLoadGeneration)
                {
                    return;
                }

                var enrichedBatch = loadedBatch.Select(channel => (channel.ChannelId,
                    Videos: channel.Videos.Select(searchResolver.ApplyCachedMetadata).ToList())).ToArray();

                var updatedResults =
                    new Dictionary<string, List<VideoSearchEntry>>(
                        workingResults,
                        StringComparer.OrdinalIgnoreCase);

                foreach (var loadedChannel in
                         enrichedBatch)
                {
                    updatedResults.Remove(
                        loadedChannel.ChannelId);

                    if (loadedChannel.Videos.Count >
                        0)
                    {
                        updatedResults[
                            loadedChannel.ChannelId] =
                            loadedChannel.Videos;
                    }
                }

                workingResults =
                    updatedResults;

                if (Volatile.Read(ref disposeStarted) != 0 || token.IsCancellationRequested) return;
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (Volatile.Read(ref disposeStarted) != 0 || token.IsCancellationRequested || loadGeneration != subscriptionVideoLoadGeneration) return;
                    subscriptionVideoResults =
                        new Dictionary<string, List<VideoSearchEntry>>(
                            workingResults,
                            StringComparer.OrdinalIgnoreCase);
                }).ConfigureAwait(false);
            }

            if (Volatile.Read(ref disposeStarted) != 0 || token.IsCancellationRequested) return;
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0 || token.IsCancellationRequested) return;
                if (loadGeneration ==
                    subscriptionVideoLoadGeneration)
                {
                    subscriptionVideoCacheUpdatedUtc =
                        DateTime.UtcNow;

                    PersistSubscriptionVideoCache();
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal when subscriptions change or a manual refresh replaces it.
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[Subscriptions] Failed to load subscription feed.");

            if (Volatile.Read(ref disposeStarted) == 0)
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (Volatile.Read(ref disposeStarted) != 0 || token.IsCancellationRequested) return;
                    if (loadGeneration ==
                            subscriptionVideoLoadGeneration &&
                        subscriptionVideoResults is null)
                    {
                        subscriptionVideoResults =
                            [];
                    }
                }).ConfigureAwait(false);
        }
        finally
        {
            if (Volatile.Read(ref disposeStarted) == 0)
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (Volatile.Read(ref disposeStarted) != 0) return;
                    if (loadGeneration ==
                        subscriptionVideoLoadGeneration)
                    {
                        isLoadingSubscriptionVideos =
                            false;
                    }
                    if (ReferenceEquals(subscriptionVideosCts, cancellation)) subscriptionVideosCts = null;
                }).ConfigureAwait(false);
        }
    }

    private void EnsureSubscriptionVideosLoaded()
    {
        if (isLoadingSubscriptionVideos)
        {
            return;
        }

        RestorePersistedSubscriptionVideoCache();

        var subscribedIds =
            Plugin.Cfg.SubscribedYouTubeChannelIds
                .Where(
                    id =>
                        !string.IsNullOrWhiteSpace(
                            id))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        if (subscribedIds.Count == 0)
        {
            subscriptionVideoResults =
                [];

            selectedSubscriptionChannels.Clear();

            if (Plugin.Cfg.SubscriptionVideoChannelCache.Count >
                0)
            {
                ClearPersistedSubscriptionVideoCache();
            }

            return;
        }

        var signature =
            GetSubscriptionSignature(
                subscribedIds);

        var cacheMissing =
            subscriptionVideoResults is null ||
            subscriptionVideoCacheUpdatedUtc == default;

        var cacheExpired =
            !cacheMissing &&
            DateTime.UtcNow -
            subscriptionVideoCacheUpdatedUtc >=
            SubscriptionVideoCacheDuration;

        var subscriptionsChanged =
            !string.Equals(
                signature,
                lastSubscriptionSignature,
                StringComparison.Ordinal);

        if (!cacheMissing &&
            !cacheExpired &&
            !subscriptionsChanged)
        {
            return;
        }

        // Failed/empty feeds must not start another network request every draw.
        if (!subscriptionsChanged && DateTime.UtcNow - subscriptionVideoLastAttemptUtc < TimeSpan.FromMinutes(1))
            return;

        _ =
            LoadSubscriptionVideosAsync(
                forceRefresh:
                    cacheExpired ||
                    subscriptionsChanged);
    }

    private void DrawBrowseSubscriptionsSection()
    {
        EnsureSubscriptionVideosLoaded();
        RestoreYouTubeSubscriptionVideoCache();
        RefreshYouTubeSubscriptionVideosIfDue();

        var subscribedIds =
            Plugin.Cfg.SubscribedYouTubeChannelIds
                .Where(
                    id =>
                        !string.IsNullOrWhiteSpace(
                            id))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        var accountVideos = CanSyncYouTubeSubscriptionVideos
            ? youtubeSubscriptionFeedResults ?? []
            : [];

        if (subscribedIds.Count == 0 && accountVideos.Count == 0)
        {
            if (isSyncingYouTubeSubscriptionVideos)
                DrawBrowseVideoSkeletonGrid("Subscription videos");
            else
                DrawBrowseVideoEmptyState(
                    "No subscriptions yet",
                    "Use the + button beside a YouTube creator to subscribe, or connect your YouTube account.");

            return;
        }


        if (isLoadingSubscriptionVideos && accountVideos.Count == 0 &&
            (subscriptionVideoResults is null ||
             subscriptionVideoResults.Count == 0))
        {
            DrawBrowseVideoSkeletonGrid("Subscription videos");

            return;
        }

        if (subscriptionVideoResults is not { Count: > 0 } && accountVideos.Count == 0)
        {
            DrawBrowseVideoEmptyState(
                "No subscription videos found",
                "Try refreshing the page or subscribing to another creator.");

            return;
        }

        var results = subscriptionVideoResults ?? new Dictionary<string, List<VideoSearchEntry>>();
        var currentSubscriptions =
            subscribedIds.ToHashSet(
                StringComparer.OrdinalIgnoreCase);

        selectedSubscriptionChannels.RemoveWhere(channelId =>
            !currentSubscriptions.Contains(channelId) &&
            !(CanSyncYouTubeSubscriptionVideos && accountVideos.Any(video =>
                string.Equals(GetYouTubeBrowseChannelKey(video), channelId,
                    StringComparison.OrdinalIgnoreCase))));

        var selectedChannels =
            results
                .Where(
                    pair =>
                        currentSubscriptions.Contains(
                            pair.Key) &&
                        (selectedSubscriptionChannels.Count == 0 ||
                         selectedSubscriptionChannels.Contains(
                             pair.Key)))
                .ToList();

        var mixedVideos =
            selectedChannels
                .SelectMany(pair => pair.Value)
                .GroupBy(
                    video =>
                        video.Url,
                    StringComparer.OrdinalIgnoreCase)
                .Select(
                    group =>
                        group.First())
                .ToList();

        // The account feed stays separate from locally fetched channel uploads.
        // A channel already subscribed to in Alpha Channel is supplied by the
        // local feed, so its YouTube-account entries are hidden here.
        var seenVideoIds = mixedVideos
            .Select(video => GetYouTubeVideoId(video.Url))
            .Where(id => id is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var localChannelNames = currentSubscriptions
            .Select(GetSubscriptionDisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var video in accountVideos)
        {
            if (selectedSubscriptionChannels.Count > 0 &&
                !selectedSubscriptionChannels.Contains(GetYouTubeBrowseChannelKey(video)))
                continue;
            if ((!string.IsNullOrWhiteSpace(video.ChannelId) &&
                 currentSubscriptions.Contains(video.ChannelId)) ||
                (string.IsNullOrWhiteSpace(video.ChannelId) &&
                 localChannelNames.Contains(video.ChannelName)))
                continue;
            var id = GetYouTubeVideoId(video.Url);
            if (id is not null && seenVideoIds.Add(id))
                mixedVideos.Add(video);
        }

        mixedVideos =
            subscriptionVideoSortMode switch
            {
                1 =>
                    mixedVideos
                        .OrderByDescending(
                            video =>
                                video.UploadDate ??
                                DateTimeOffset.MinValue)
                        .ToList(),

                2 =>
                    mixedVideos
                        .OrderByDescending(
                            video =>
                                video.ViewCount ??
                                0)
                        .ToList(),

                _ =>
                    mixedVideos
                        .OrderByDescending(
                            GetTrendingScore)
                        .ToList()
            };
        mixedVideos = mixedVideos.Take(YouTubeBrowseVideoLimit).ToList();

        DrawYouTubeHubFeedHeading("Subscription videos", mixedVideos.Count);
        using var hubContent = UiFonts.Window(ImGui.GetFontSize(),
            ImRaii.Child("##youtubeHubFeed" + browseVideoSectionTab, new Vector2(0, -1), false));
        if (!hubContent) return;

        if (mixedVideos.Count == 0)
        {
            DrawBrowseVideoEmptyState(
                "No matching videos",
                "Try selecting more subscriptions.");

            return;
        }

        var contentWidth = YouTubeCardGridWidth;

        var columns =
            BrowseVideoGridColumns(contentWidth);

        var cardGap =
            Ui(12f);

        var rowGap =
            cardGap;

        var cardWidth =
            (contentWidth -
             cardGap *
             (columns - 1)) /
            columns;

        var cardHeight =
            BrowseVideoCardHeight;

        var gridStartX =
            ImGui.GetCursorPosX();

        var gridStartY =
            ImGui.GetCursorPosY();

        for (var index = 0;
             index < mixedVideos.Count;
             index++)
        {
            if (index > 0)
            {
                if (index %
                    columns ==
                    0)
                {
                    var rowIndex =
                        index /
                        columns;

                    ImGui.SetCursorPos(
                        new Vector2(
                            gridStartX,
                            gridStartY +
                            rowIndex *
                            (cardHeight +
                             rowGap)));
                }
                else
                {
                    ImGui.SameLine(
                        0f,
                        cardGap);
                }
            }

            ImGui.PushID(
                $"subscriptionVideo_{index}");

            DrawBrowseVideoCardSurface(
     mixedVideos[index],
     null,
     cardWidth,
     cardHeight,
     showFavouriteAction: true);

            ImGui.PopID();
        }
    }

    private string GetSubscriptionDisplayName(
        string channelId)
    {
        if (Plugin.Cfg.SubscribedYouTubeChannelNames.TryGetValue(
                channelId,
                out var savedName) &&
            !string.IsNullOrWhiteSpace(
                savedName))
        {
            return savedName;
        }

        if (subscriptionVideoResults is { } results &&
            results.TryGetValue(
                channelId,
                out var channelVideos))
        {
            var discoveredName =
                channelVideos
                    .Select(
                        video =>
                            video.ChannelName)
                    .FirstOrDefault(
                        name =>
                            !string.IsNullOrWhiteSpace(
                                name));

            if (!string.IsNullOrWhiteSpace(
                    discoveredName))
            {
                return discoveredName;
            }
        }

        return channelId;
    }

    private void DrawBrowseFavouriteVideosSection()
    {
        RestoreYouTubeLikedVideoCache();
        RefreshYouTubeLikedVideosIfDue();

        var favouriteIds =
            Plugin.Cfg.FavouriteYouTubeVideoIds
                .Where(
                    id =>
                        !string.IsNullOrWhiteSpace(
                            id))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        var favouriteSignature =
            GetFavouriteVideoSignature(
                favouriteIds);

        RestorePersistedFavouriteVideoCache();

        var cacheExpired =
            favouriteVideoResults is not null &&
            DateTime.UtcNow -
            favouriteVideoCacheUpdatedUtc >=
            FavouriteVideoCacheDuration;

        var favouritesChanged =
            !string.Equals(
                favouriteSignature,
                lastFavouriteVideoSignature,
                StringComparison.Ordinal);

        if (!isLoadingFavouriteVideos &&
            (favouriteVideoResults is null ||
             cacheExpired ||
             favouritesChanged))
        {
            _ =
                LoadFavouriteVideosAsync(
                    forceRefresh:
                        cacheExpired ||
                        favouritesChanged);
        }

        var accountVideos = CanSyncYouTubeLikedVideos
            ? youtubeLikedVideoResults ?? []
            : [];

        if (favouriteIds.Count == 0 && accountVideos.Count == 0)
        {
            if (isSyncingYouTubeLikedVideos)
                DrawBrowseVideoSkeletonGrid("Liked videos");
            else
                DrawBrowseVideoEmptyState(
                    "No liked videos yet",
                    "Use the heart button on a YouTube video to save it here.");

            return;
        }


        if (isLoadingFavouriteVideos && accountVideos.Count == 0 &&
            (favouriteVideoResults is null ||
             favouriteVideoResults.Count == 0))
        {
            DrawBrowseVideoSkeletonGrid("Liked videos");

            return;
        }

        var results = new List<VideoSearchEntry>();
        var seenVideoIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var video in (favouriteVideoResults ?? []).Concat(accountVideos.Where(
                     video => YouTubeLikedVideosFetcher.HasUsableMetadata(video.Title, video.ChannelName))))
        {
            var id = GetYouTubeVideoId(video.Url);
            if (id is not null && seenVideoIds.Add(id))
                results.Add(video);
        }

        if (results.Count == 0)
        {
            DrawBrowseVideoEmptyState(
                "Liked videos could not be loaded",
                "Try refreshing the page.");

            return;
        }

        var favouriteOrder =
            favouriteIds.Concat(accountVideos.Select(video => GetYouTubeVideoId(video.Url) ?? string.Empty))
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(
                    (id, index) =>
                        (
                            Id: id,
                            Index: index
                        ))
                .ToDictionary(
                    item =>
                        item.Id,
                    item =>
                        item.Index,
                    StringComparer.OrdinalIgnoreCase);

        var visibleResults =
            results
                .Where(
                    result =>
                    {
                        var videoId =
                            GetYouTubeVideoId(
                                result.Url);

                        return videoId is not null &&
                               favouriteOrder.ContainsKey(
                                   videoId);
                    })
                .ToList();

        visibleResults =
            favouriteVideoSortMode switch
            {
                0 =>
                    visibleResults
                        .OrderByDescending(
                            GetTrendingScore)
                        .ToList(),

                1 =>
                    visibleResults
                        .OrderByDescending(
                            video =>
                                video.UploadDate ??
                                DateTimeOffset.MinValue)
                        .ToList(),

                2 =>
                    visibleResults
                        .OrderByDescending(
                            video =>
                                video.ViewCount ??
                                0)
                        .ToList(),

                _ =>
                    visibleResults
                        .OrderBy(
                            video =>
                            {
                                var videoId =
                                    GetYouTubeVideoId(
                                        video.Url);

                                return videoId is not null &&
                                       favouriteOrder.TryGetValue(
                                           videoId,
                                           out var index)
                                    ? index
                                    : int.MaxValue;
                            })
                        .ToList()
            };

        DrawYouTubeHubFeedHeading("Liked videos", visibleResults.Count);
        using var hubContent = UiFonts.Window(ImGui.GetFontSize(),
            ImRaii.Child("##youtubeHubFeed" + browseVideoSectionTab, new Vector2(0, -1), false));
        if (!hubContent) return;

        if (visibleResults.Count == 0)
        {
            DrawBrowseVideoEmptyState(
                "No liked videos are available",
                "The missing videos can be retried using Refresh.");

            return;
        }

        var contentWidth = YouTubeCardGridWidth;

        var columns =
            BrowseVideoGridColumns(contentWidth);

        var cardGap =
            Ui(12f);

        var rowGap =
            cardGap;

        var cardWidth =
            (contentWidth -
             cardGap *
             (columns - 1)) /
            columns;

        var cardHeight =
            BrowseVideoCardHeight;

        var gridStartX =
            ImGui.GetCursorPosX();

        var gridStartY =
            ImGui.GetCursorPosY();

        for (var index = 0;
             index < visibleResults.Count;
             index++)
        {
            if (index > 0)
            {
                if (index %
                    columns ==
                    0)
                {
                    var rowIndex =
                        index /
                        columns;

                    ImGui.SetCursorPos(
                        new Vector2(
                            gridStartX,
                            gridStartY +
                            rowIndex *
                            (cardHeight +
                             rowGap)));
                }
                else
                {
                    ImGui.SameLine(
                        0f,
                        cardGap);
                }
            }

            ImGui.PushID(
                $"favouriteVideo_{index}");

            DrawBrowseVideoCardSurface(
                visibleResults[index],
                null,
                cardWidth,
                cardHeight,
                showFavouriteAction: favouriteIds.Contains(
                    GetYouTubeVideoId(visibleResults[index].Url), StringComparer.OrdinalIgnoreCase));

            ImGui.PopID();
        }
    }

    private void DrawVideoGrid()
    {
        var enabledBrowseTopics =
    GetEnabledTrendingTopics();

        var currentBrowseTopicSignature =
            GetBrowseTopicSignature(
                enabledBrowseTopics);

        var browseTopicsChanged =
            browseVideoRequested &&
            !isLoadingBrowseVideos &&
            !string.Equals(
                browseVideoTopicSignature,
                currentBrowseTopicSignature,
                StringComparison.Ordinal);

        if (browseVideoSectionTab == 0 &&
            enabledBrowseTopics.Count > 0 &&
            (!browseVideoRequested ||
             browseTopicsChanged))
        {
            browseVideoRequested =
                true;

            isLoadingBrowseVideos =
                true;

            _ =
                LoadBrowseVideosAsync(
                    forceRefresh:
                        browseTopicsChanged);
        }

        if (viewedChannelId is not null)
        {
            DrawYouTubeChannelPage();

            return;
        }

        switch (browseVideoSectionTab)
        {
            case 1:
                DrawBrowseSubscriptionsSection();

                return;

            case 2:
                DrawBrowseFavouriteVideosSection();

                return;

            case 3:
                DrawBrowseRecommendedSection();

                return;

            case 4:
                DrawBrowsePopularSection();

                return;
        }

        if (GetSubscribedTopicCount() == 0)
        {
            browseVideosCts?.Cancel();

            browseVideoRequested =
                false;

            isLoadingBrowseVideos =
                false;

            browseVideoResults =
                null;

            DrawBrowseVideoEmptyState(
                "No subscribed topics yet",
                "Choose your topics during setup or from Settings " +
                "before opening topic recommendations.");

            return;
        }


        if (isLoadingBrowseVideos &&
      (browseVideoResults is null ||
       browseVideoResults.Count == 0))
        {
            DrawBrowseVideoSkeletonGrid();

            return;
        }

        if (browseVideoResults is null ||
            browseVideoResults.Count == 0)
        {
            DrawBrowseVideoEmptyState(
                "No videos found",
                "Try refreshing the page or selecting different topics.");

            return;
        }

        //
        // Remove selections that are no longer present in the loaded
        // topic collection.
        //

        browseSelectedTopics.RemoveWhere(
            topic =>
                !browseVideoResults.ContainsKey(
                    topic));

        var selectedTopicResults =
            browseVideoResults
                .Where(
                    pair =>
                        browseSelectedTopics.Count ==
                        0 ||
                        browseSelectedTopics.Contains(
                            pair.Key))
                .ToList();

        //
        // Combine the full selected topic caches, deduplicate, then sort the
        // entire feed before applying the display limit.
        //

        var mixedVideos =
            selectedTopicResults
                .SelectMany(pair => pair.Value.Select(video => (Video: video, Topic: pair.Key)))
                .Where(item => !IsYouTubeChannelHidden(item.Video))
                .GroupBy(
                    item =>
                        item.Video.Url,
                    StringComparer.OrdinalIgnoreCase)
                .Select(
                    group =>
                        group.First())
                .ToList();

        mixedVideos =
            browseVideoSortMode switch
            {
                1 =>
                    mixedVideos
                        .OrderByDescending(
                            item =>
                                item.Video.UploadDate ??
                                DateTimeOffset.MinValue)
                        .ToList(),

                2 =>
                    mixedVideos
                        .OrderByDescending(
                            item =>
                                item.Video.ViewCount ??
                                0)
                        .ToList(),

                _ =>
                    mixedVideos
                        .OrderByDescending(
                            item =>
                                GetTrendingScore(
                                    item.Video))
                        .ToList()
            };
        mixedVideos = mixedVideos.Take(YouTubeBrowseVideoLimit).ToList();

      

        DrawYouTubeHubFeedHeading("Videos by Topic", mixedVideos.Count);
        using var hubContent = UiFonts.Window(ImGui.GetFontSize(),
            ImRaii.Child("##youtubeHubFeed" + browseVideoSectionTab, new Vector2(0, -1), false));
        if (!hubContent) return;

        if (mixedVideos.Count == 0)
        {
            DrawBrowseVideoEmptyState(
                "No matching videos",
                "Try selecting more topics.");

            return;
        }

        var contentWidth = YouTubeCardGridWidth;

        var columns =
            BrowseVideoGridColumns(contentWidth);

        var cardGap =
            Ui(12f);

        var rowGap =
            cardGap;

        var cardWidth =
            (contentWidth -
             cardGap *
             (columns - 1)) /
            columns;

        var cardHeight =
            BrowseVideoCardHeight;

        var gridStartX =
    ImGui.GetCursorPosX();

        var gridStartY =
            ImGui.GetCursorPosY();

        for (var index = 0;
      index < mixedVideos.Count;
      index++)
        {
            if (index > 0)
            {
                if (index %
                    columns ==
                    0)
                {
                    var rowIndex =
                        index /
                        columns;

                    ImGui.SetCursorPos(
                        new Vector2(
                            gridStartX,
                            gridStartY +
                            rowIndex *
                            (cardHeight +
                             rowGap)));
                }
                else
                {
                    ImGui.SameLine(
                        0f,
                        cardGap);
                }
            }

            var item =
                mixedVideos[index];

            ImGui.PushID(
                $"browseMixed_{index}");

            DrawBrowseVideoCardSurface(
     item.Video,
     item.Topic,
     cardWidth,
     cardHeight,
     showFavouriteAction: true);

            ImGui.PopID();
        }
    }

    private void DrawBrowseVideoSkeletonGrid(string heading = "Videos by Topic")
    {
        DrawYouTubeHubFeedHeading(heading, 0);
        using var hubContent = UiFonts.Window(ImGui.GetFontSize(),
            ImRaii.Child("##youtubeHubFeed" + browseVideoSectionTab, new Vector2(0, -1), false));
        if (!hubContent) return;

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(7f)));

        var contentWidth = YouTubeCardGridWidth;

        var columns =
            BrowseVideoGridColumns(contentWidth);

        var cardGap =
            Ui(12f);

        var rowGap =
            cardGap;

        var cardWidth =
            (contentWidth -
             cardGap *
             (columns - 1)) /
            columns;

        var cardHeight =
            BrowseVideoCardHeight;

        var thumbnailHeight =
            cardWidth *
            9f /
            16f;

        var pulse =
            0.5f +
            0.5f *
            MathF.Sin(
                (float)ImGui.GetTime() *
                3.2f);

        var baseAlpha =
            0.10f +
            pulse *
            0.055f;

        var highlightAlpha =
            0.14f +
            pulse *
            0.07f;

        var drawList =
            ImGui.GetWindowDrawList();

        var cardCount =
            columns *
            2;

        var gridStartX =
    ImGui.GetCursorPosX();

        var gridStartY =
            ImGui.GetCursorPosY();

        for (var index = 0;
      index < cardCount;
      index++)
        {
            if (index > 0)
            {
                if (index %
                    columns ==
                    0)
                {
                    var rowIndex =
                        index /
                        columns;

                    ImGui.SetCursorPos(
                        new Vector2(
                            gridStartX,
                            gridStartY +
                            rowIndex *
                            (cardHeight +
                             rowGap)));
                }
                else
                {
                    ImGui.SameLine(
                        0f,
                        cardGap);
                }
            }

            var origin =
                ImGui.GetCursorScreenPos();

            var bottomRight =
                origin +
                new Vector2(
                    cardWidth,
                    cardHeight);

            drawList.AddRectFilled(
                origin,
                bottomRight,
                ImGui.GetColorU32(
                    CardBg),
                Ui(9f));

            drawList.AddRect(
                origin,
                bottomRight,
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.13f)),
                Ui(9f),
                ImDrawFlags.None,
                Ui(1f));

            var inset =
                Ui(7f);

            var thumbnailStart =
                origin +
                new Vector2(
                    inset,
                    inset);

            var thumbnailEnd =
                new Vector2(
                    origin.X +
                    cardWidth -
                    inset,
                    thumbnailStart.Y +
                    thumbnailHeight -
                    inset);

            drawList.AddRectFilled(
                thumbnailStart,
                thumbnailEnd,
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        highlightAlpha)),
                Ui(7f));

            var textStartX =
                origin.X +
                Ui(10f);

            var firstLineY =
                thumbnailEnd.Y +
                Ui(17f);

            drawList.AddRectFilled(
                new Vector2(
                    textStartX,
                    firstLineY),
                new Vector2(
                    origin.X +
                    cardWidth *
                    0.83f,
                    firstLineY +
                    Ui(10f)),
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        highlightAlpha)),
                Ui(4f));

            drawList.AddRectFilled(
                new Vector2(
                    textStartX,
                    firstLineY +
                    Ui(18f)),
                new Vector2(
                    origin.X +
                    cardWidth *
                    0.64f,
                    firstLineY +
                    Ui(27f)),
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        baseAlpha)),
                Ui(4f));

            drawList.AddRectFilled(
                new Vector2(
                    textStartX,
                    firstLineY +
                    Ui(49f)),
                new Vector2(
                    origin.X +
                    cardWidth *
                    0.48f,
                    firstLineY +
                    Ui(58f)),
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        baseAlpha)),
                Ui(4f));

            drawList.AddRectFilled(
                new Vector2(
                    textStartX,
                    bottomRight.Y -
                    Ui(43f)),
                new Vector2(
                    bottomRight.X -
                    Ui(10f),
                    bottomRight.Y -
                    Ui(9f)),
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        baseAlpha)),
                Ui(7f));

            ImGui.Dummy(
                new Vector2(
                    cardWidth,
                    cardHeight));
        }
    }

    private void DrawBrowseLoadingSpinner(
        float radius)
    {
        var size =
            radius *
            2f +
            Ui(2f);

        var origin =
            ImGui.GetCursorScreenPos();

        var center =
            origin +
            new Vector2(
                size * 0.5f,
                size * 0.5f);

        var rotation =
            (float)ImGui.GetTime() *
            4.5f;

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddCircle(
            center,
            radius,
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.20f)),
            24,
            Ui(2f));

        drawList.PathArcTo(
            center,
            radius,
            rotation,
            rotation +
            MathF.PI *
            1.45f,
            18);

        drawList.PathStroke(
            ImGui.GetColorU32(
                AccentHover),
            ImDrawFlags.None,
            Ui(2.2f));

        ImGui.Dummy(
            new Vector2(
                size,
                size));
    }

    private void DrawBrowseTopicSelector(
    float selectorWidth)
    {
        var summary =
            browseSelectedTopics.Count switch
            {
                0 =>
                    "All topics",

                1 =>
                    browseSelectedTopics.First(),

                _ =>
                    $"{browseSelectedTopics.Count} topics selected"
            };

        ImGui.SetNextItemWidth(
            selectorWidth);

        var comboOpen =
            false;

        var comboHovered =
            false;

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                Ui(8f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.FrameBg,
                FrameBg)
                .Push(
                    ImGuiCol.FrameBgHovered,
                    CardBgHover)
                .Push(
                    ImGuiCol.FrameBgActive,
                    CardBgHover))
        {
            comboOpen =
                UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo(
                    "##browseTopicMultiSelect",
                    summary));

            comboHovered =
                ImGui.IsItemHovered();

            if (comboOpen)
            {
                var allTopics =
                    browseSelectedTopics.Count ==
                    0;

                using (
                    UiDisabled.Scope(
                        allTopics))
                {
                    if (ImGui.Checkbox(
                            "All topics",
                            ref allTopics))
                    {
                        browseSelectedTopics.Clear();
                    }
                }

                ImGui.Separator();

                if (browseVideoResults is { Count: > 0 })
                {
                    foreach (var topicName in
                             browseVideoResults.Keys
                                 .OrderBy(
                                     name =>
                                         name,
                                     StringComparer.OrdinalIgnoreCase))
                    {
                        var selected =
                            browseSelectedTopics.Contains(
                                topicName);

                        if (ImGui.Checkbox(
                                $"{topicName}##browseMulti_{topicName}",
                                ref selected))
                        {
                            if (selected)
                            {
                                browseSelectedTopics.Add(
                                    topicName);
                            }
                            else
                            {
                                browseSelectedTopics.Remove(
                                    topicName);
                            }
                        }
                    }
                }

                ImGui.EndCombo();
            }
        }

        if (comboHovered)
        {
            UiFonts.SetTooltip(
                "Available topics can be adjusted in Settings.");
        }
    }

    private void DrawBrowseVideoCardSurface(
    VideoSearchEntry videoResult,
    string? topic,
    float width,
    float height,
    bool showFavouriteAction = false)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(
                width,
                height);

        if (!ImGui.IsRectVisible(origin, origin + size)) { ImGui.Dummy(size); return; }
        var drawList =
            ImGui.GetWindowDrawList();

        DrawBrowseVideoCardPanel(origin, size);
        ImGui.SetCursorScreenPos(origin + UiVec(6, 6));
        DrawHomeYouTubeCard(videoResult, width - Ui(12), height - Ui(12), browseLayout: true,
            browseTopic: topic, showBrowseFavouriteAction: showFavouriteAction);
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(size);
    }

    private void DrawBrowseVideoEmptyState(
        string title,
        string description)
    {
        if (currentPage == HomePage.VideoGrid && !youtubeBrowseShowingSearch && viewedChannelId is null && !youtubeBrowseRefreshDrawn)
        {
            var heading = browseVideoSectionTab switch
            {
                0 => "Videos by Topic", 1 => "Subscription videos", 2 => "Liked videos",
                3 => "Recommended for you", _ => "Popular on Alpha Channel"
            };
            DrawYouTubeHubFeedHeading(heading, 0);
        }

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(24f)));

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                Ui(10f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                UiVec(
                    20f,
                    18f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(
                    CardBg.X,
                    CardBg.Y,
                    CardBg.Z,
                    0.70f)))
        using (
            var emptyState =
                UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                    "##browseVideosEmptyState",
                    new Vector2(
                        -1f,
                        Ui(112f)),
                    false,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (!emptyState)
            {
                return;
            }

            using var uiFontScale81805=UiFonts.PushRelative(1.08f);

            ImGui.TextColored(
                Hex(0xE2E7EF),
                title);

            uiFontScale81805.Dispose();

            ImGui.Dummy(
                new Vector2(
                    0f,
                    Ui(6f)));

            ImGui.TextColored(
                MutedText,
                description);
        }
    }
}

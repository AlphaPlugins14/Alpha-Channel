using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.YouTube;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private sealed record RecommendedVideo(VideoSearchEntry Video, string? Topic);
    private int recommendedVideoSortMode;
    private List<RecommendedVideo>? youtubeBrowseRecommendedVideos;
    private List<RecommendedVideo>? recommendedDisplaySource, recommendedDisplayVideos;
    private int recommendedDisplayRevision = -1, recommendedDisplaySort = -1, recommendedDisplayHidden = -1;

    private void RefreshYouTubeBrowseRecommendations()
    {
        youtubeMetadataReloadVersion++;
        youtubeBrowseRecommendedVideos = null;
        _ = FetchYouTubeRecommendedVideosAsync(manual: true);
    }

    internal void RecordRecommendedTopicPlay(string url)
    {
        var videoId = GetYouTubeVideoId(url);
        if (videoId is null)
            return;

        var counts = Plugin.Cfg.BrowseVideoTopicPlayCounts ??=
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var matchingTopics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (topic, videos) in Plugin.Cfg.BrowseVideoTopicCache)
        {
            if (videos.Any(video => string.Equals(GetYouTubeVideoId(video.Url), videoId,
                    StringComparison.OrdinalIgnoreCase)))
                matchingTopics.Add(topic);
        }

        if (browseVideoResults is not null)
        {
            foreach (var (topic, videos) in browseVideoResults)
            {
                if (videos.Any(video => string.Equals(GetYouTubeVideoId(video.Url), videoId,
                        StringComparison.OrdinalIgnoreCase)))
                    matchingTopics.Add(topic);
            }
        }

        if (matchingTopics.Count == 0)
            return;

        foreach (var topic in matchingTopics)
            counts[topic] = counts.GetValueOrDefault(topic) == int.MaxValue
                ? int.MaxValue
                : counts.GetValueOrDefault(topic) + 1;

        Plugin.Cfg.Save();
        
    }

    private List<RecommendedVideo> BuildLocalRecommendedVideos()
    {
        RestoreYouTubeSubscriptionVideoCache();
        var topicCache = Plugin.Cfg.BrowseVideoTopicCache.ToDictionary(
            pair => pair.Key, pair => pair.Value.Select(ToRecommendedEntry).ToList(),
            StringComparer.OrdinalIgnoreCase);
        // Include batches already published by the cache builder, even before
        // its final metadata pass has persisted them.
        if (browseVideoResults is not null)
            foreach (var (topic, videos) in browseVideoResults)
                topicCache[topic] = videos;
        var subscribedIds = Plugin.Cfg.SubscribedYouTubeChannelIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var counts = Plugin.Cfg.BrowseVideoTopicPlayCounts ??
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var selected = new List<RecommendedVideo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(RecommendedVideo candidate)
        {
            if (IsYouTubeChannelHidden(candidate.Video)) return;
            var id = GetYouTubeVideoId(candidate.Video.Url);
            if (id is not null && seen.Add(id))
                selected.Add(candidate);
        }

        var topTopics = counts
            .Where(pair => pair.Value > 0 &&
                           topicCache.TryGetValue(pair.Key, out var videos) && videos.Count > 0)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(pair => pair.Key);

        var topicCandidates = topTopics
            .SelectMany(topic => topicCache[topic]
                .Where(video => !IsYouTubeChannelHidden(video))
                .OrderByDescending(video => video.UploadDate ?? DateTimeOffset.MinValue)
                .Take(5)
                .Select(video => new RecommendedVideo(video, topic)))
            .OrderByDescending(candidate => RecommendedViewsPerDay(candidate.Video))
            .ThenByDescending(candidate => candidate.Video.UploadDate ?? DateTimeOffset.MinValue);
        foreach (var candidate in topicCandidates)
        {
            if (selected.Count >= 10)
                break;
            Add(candidate);
        }

        var subscriptionCache = subscriptionVideoResults is not null
            ? subscriptionVideoResults
            : Plugin.Cfg.SubscriptionVideoChannelCache.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Select(ToRecommendedEntry).ToList(),
                StringComparer.OrdinalIgnoreCase);
        var accountSubscriptionVideos = CanSyncYouTubeSubscriptionVideos
            ? youtubeSubscriptionFeedResults ?? []
            : [];
        var recentCutoff = DateTimeOffset.UtcNow.AddHours(-72);
        var localSubscriptionCandidates = subscriptionCache
            .Where(pair => subscribedIds.Contains(pair.Key))
            .SelectMany(pair =>
            {
                var ordered = pair.Value
                    .OrderByDescending(video => video.UploadDate ?? DateTimeOffset.MinValue)
                    .ToList();
                return ordered.Take(1).Concat(ordered.Where(video => video.UploadDate >= recentCutoff));
            });
        var accountSubscriptionCandidates = accountSubscriptionVideos
            .GroupBy(video => !string.IsNullOrWhiteSpace(video.ChannelId)
                    ? video.ChannelId : "name:" + video.ChannelName,
                StringComparer.OrdinalIgnoreCase)
            .SelectMany(channel =>
            {
                var ordered = channel
                    .OrderByDescending(video => video.UploadDate ?? DateTimeOffset.MinValue)
                    .ToList();
                return ordered.Take(1).Concat(ordered.Where(video => video.UploadDate >= recentCutoff));
            })
            .OrderByDescending(RecommendedViewsPerDay)
            .ThenByDescending(video => video.UploadDate ?? DateTimeOffset.MinValue)
            .ToList();
        var subscriptionCandidates = localSubscriptionCandidates
            .Concat(accountSubscriptionCandidates)
            .OrderByDescending(RecommendedViewsPerDay)
            .ThenByDescending(video => video.UploadDate ?? DateTimeOffset.MinValue);
        var addedSubscriptions = 0;
        // Give the connected account a small share before ranking the combined
        // pool, so a full local subscription cache cannot crowd it out.
        foreach (var video in accountSubscriptionCandidates)
        {
            if (addedSubscriptions >= 5) break;
            var before = selected.Count;
            Add(new RecommendedVideo(video, null));
            if (selected.Count > before) addedSubscriptions++;
        }
        foreach (var video in subscriptionCandidates)
        {
            if (addedSubscriptions >= 25)
                break;
            var before = selected.Count;
            Add(new RecommendedVideo(video, null));
            if (selected.Count > before)
                addedSubscriptions++;
        }

        if (selected.Count < 15)
        {
            // Topic videos get first choice, then other saved discoveries,
            // including the connected YouTube account's subscription feed.
            var topicFallback = topicCache
                .SelectMany(pair => pair.Value.Select(video =>
                    new RecommendedVideo(video, pair.Key)))
                .OrderByDescending(candidate => candidate.Video.UploadDate ?? DateTimeOffset.MinValue);
            foreach (var candidate in topicFallback)
            {
                if (selected.Count >= 15) break;
                Add(candidate);
            }

            var otherFallback = subscriptionCache
                .Where(pair => subscribedIds.Contains(pair.Key))
                .SelectMany(pair => pair.Value)
                .Concat(accountSubscriptionVideos)
                .Concat(Plugin.Cfg.FavouriteVideoCache.Select(ToRecommendedEntry))
                .Concat(Plugin.Cfg.FfxivVideoCache.Select(ToRecommendedEntry))
                .Concat(Plugin.Cfg.FeaturedSlideCache.Select(ToRecommendedEntry))
                .OrderByDescending(video => video.UploadDate ?? DateTimeOffset.MinValue);
            foreach (var video in otherFallback)
            {
                if (selected.Count >= 15) break;
                Add(new RecommendedVideo(video, null));
            }
        }

        var localRecommendations = selected
            .OrderByDescending(candidate => RecommendedViewsPerDay(candidate.Video))
            .ThenByDescending(candidate => candidate.Video.UploadDate ?? DateTimeOffset.MinValue)
            .ToList();
        // Keep YouTube's recommendation order ahead of local discoveries.
        var accountRecommendations = CanSyncYouTubeRecommendedVideos
            ? Plugin.Cfg.YouTubeRecommendedVideoCache.Take(40)
                .Select(record => new RecommendedVideo(ToRecommendedEntry(record), null))
            : Enumerable.Empty<RecommendedVideo>();
        var candidates = accountRecommendations.Concat(localRecommendations).ToList();
        var topics = candidates.DistinctBy(candidate => candidate.Video.Url, StringComparer.Ordinal)
            .ToDictionary(candidate => candidate.Video.Url, candidate => candidate.Topic, StringComparer.Ordinal);
        return YouTubeRecommendationCache.CombineForDisplay(candidates.Select(candidate => candidate.Video))
            .Select(entry => new RecommendedVideo(entry, topics[entry.Url])).ToList();
    }

    private static VideoSearchEntry ToRecommendedEntry(CachedBrowseVideoRecord video) =>
        new(video.Title, video.Url, video.ChannelName,
            video.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            video.ThumbnailUrl, video.ViewCount, video.UploadDate, video.ChannelId, video.ViewCountObservedAtUnix);

    private static double RecommendedViewsPerDay(VideoSearchEntry video)
    {
        if (video.UploadDate is not { } posted || video.ViewCount is not { } views)
            return 0;
        return views / Math.Max(1d, (DateTimeOffset.UtcNow - posted).TotalDays);
    }

    private void DrawBrowseRecommendedSection()
    {
        EnsureSubscriptionVideosLoaded();
        RefreshYouTubeRecommendedVideosIfDue();
        var source = youtubeBrowseRecommendedVideos ??= BuildLocalRecommendedVideos();
        if (!ReferenceEquals(source, recommendedDisplaySource) || recommendedDisplayRevision != youtubeMetadataRevision ||
            recommendedDisplaySort != recommendedVideoSortMode || recommendedDisplayHidden != youtubeHiddenChannelsRevision)
        {
            var visible = source.Where(item => !IsYouTubeChannelHidden(item.Video));
            recommendedDisplayVideos = (recommendedVideoSortMode switch
            {
                1 => visible.OrderByDescending(item => item.Video.UploadDate ?? DateTimeOffset.MinValue),
                2 => visible.OrderByDescending(item => item.Video.ViewCount ?? 0),
                _ => visible
            }).ToList();
            recommendedDisplaySource = source; recommendedDisplayRevision = youtubeMetadataRevision;
            recommendedDisplaySort = recommendedVideoSortMode; recommendedDisplayHidden = youtubeHiddenChannelsRevision;
        }
        var videos = recommendedDisplayVideos!;

        DrawYouTubeHubFeedHeading("Recommended for you", videos.Count);

        if (videos.Count == 0)
        {
            DrawBrowseVideoEmptyState("No cached recommendations yet",
                "Watch some topic videos or subscribe to channels to build your recommendations.");
            return;
        }

        using var content = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##recommendedVideoContent", new Vector2(0f, -1f), false));
        if (!content) return;

        var width = YouTubeCardGridWidth;
        var columns = width >= Ui(930f) ? 5 : width >= Ui(720f) ? 4 : 3;
        var gap = Ui(12f);
        var cardWidth = (width - gap * (columns - 1)) / columns;
        var cardHeight = Ui(266f);
        var origin = ImGui.GetCursorPos();
        for (var index = 0; index < videos.Count; index++)
        {
            if (index > 0)
            {
                if (index % columns == 0)
                    ImGui.SetCursorPos(new Vector2(origin.X,
                        origin.Y + (index / columns) * (cardHeight + gap)));
                else
                    ImGui.SameLine(0f, gap);
            }

            ImGui.PushID($"recommendedVideo_{index}");
            DrawBrowseVideoCardSurface(videos[index].Video, null,
                cardWidth, cardHeight, showFavouriteAction: true);
            ImGui.PopID();
        }
    }
}

using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin.YouTube;

internal static class YouTubeRecommendationCache
{
    internal const int Capacity = 40;
    internal const int FetchLimit = 15;

    // Account recommendations lead the display order, but the same video may
    // already have a view count in a topic/subscription result. Keep those
    // details instead of discarding the richer duplicate with DistinctBy.
    internal static List<VideoSearchEntry> CombineForDisplay(IEnumerable<VideoSearchEntry> videos) =>
        videos.GroupBy(video => video.Url, StringComparer.Ordinal)
            .Select(group => group.Skip(1).Aggregate(group.First(),
                (preferred, duplicate) => YouTubeMetadataCache.Merge(duplicate, preferred)))
            .ToList();

    // Input URLs are canonical watch URLs from the account feed fetcher.
    // Most recently fetched entries lead; repeat videos retain missing metadata.
    internal static List<VideoSearchEntry> Merge(IEnumerable<VideoSearchEntry> fetched,
        IEnumerable<VideoSearchEntry> cached)
    {
        var previous = cached.Where(video => !string.IsNullOrWhiteSpace(video.Url))
            .DistinctBy(video => video.Url, StringComparer.Ordinal).ToList();
        var byUrl = previous.ToDictionary(video => video.Url, StringComparer.Ordinal);
        return fetched.Take(FetchLimit).Concat(previous)
            .Where(video => !string.IsNullOrWhiteSpace(video.Url))
            .DistinctBy(video => video.Url, StringComparer.Ordinal)
            .Take(Capacity)
            .Select(video => byUrl.TryGetValue(video.Url, out var old) ? video with
            {
                Duration = video.Duration ?? old.Duration,
                ThumbnailUrl = video.ThumbnailUrl ?? old.ThumbnailUrl,
                ViewCount = video.ViewCount ?? old.ViewCount,
                UploadDate = video.UploadDate ?? old.UploadDate,
                ChannelId = video.ChannelId ?? old.ChannelId,
                ViewCountObservedAtUnix = video.ViewCount is not null ? video.ViewCountObservedAtUnix : old.ViewCountObservedAtUnix
            } : video).ToList();
    }
}

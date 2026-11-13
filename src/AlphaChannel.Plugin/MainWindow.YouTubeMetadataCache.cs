using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.YouTube;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static VideoSearchEntry PopularEntry(PopularVideoRecord video) => new(
        YouTubeMetadataCache.HasTitle(video.Title) ? video.Title : string.Empty,
        "https://www.youtube.com/watch?v=" + video.VideoId, video.ChannelName,
        video.DurationSeconds is { } seconds && double.IsFinite(seconds) && seconds >= 0 &&
            seconds < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : null,
        video.ThumbnailUrl ?? $"https://i.ytimg.com/vi/{video.VideoId}/hqdefault.jpg",
        video.ViewCount, video.UploadDate, video.ChannelId, video.ViewCountObservedAtUnix, video.MetadataObservedAtUnix);

    // Read configuration only on the UI/framework thread; background requests use
    // the thread-safe public metadata cache rather than enumerating mutable feeds.
    private void SeedYouTubeMetadataCache()
    {
        var cfg = Plugin.Cfg;
        var records = cfg.YouTubeLikedVideoCache.Concat(cfg.YouTubeSubscriptionVideoCache)
            .Concat(cfg.YouTubeRecommendedVideoCache).Concat(cfg.FavouriteVideoCache)
            .Concat(cfg.FfxivVideoCache).Concat(cfg.FeaturedSlideCache)
            .Concat(cfg.BrowseVideoTopicCache.Values.SelectMany(entries => entries))
            .Concat(cfg.SubscriptionVideoChannelCache.Values.SelectMany(entries => entries));
        searchResolver.RememberVideoMetadata(records.Select(entry => new VideoSearchEntry(
            entry.Title, entry.Url, entry.ChannelName,
            entry.DurationSeconds is { } seconds && double.IsFinite(seconds) && seconds >= 0 &&
                seconds < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : null,
            entry.ThumbnailUrl, entry.ViewCount, entry.UploadDate, entry.ChannelId, entry.ViewCountObservedAtUnix)));
        searchResolver.RememberVideoMetadata(cfg.YouTubeSharedPopularVideos.Select(PopularEntry));
        // Playback history survives restarts independently of discovery feeds.
        // A downloaded/watched video must not lose its known title just because
        // the shared Popular ranking only supplies its video ID.
        searchResolver.RememberVideoMetadata(cfg.RecentlyWatchedVideos.Select(entry => new VideoSearchEntry(
            entry.Title, entry.Url, entry.ChannelName,
            double.IsFinite(entry.DurationSeconds) && entry.DurationSeconds > 0 &&
                entry.DurationSeconds < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(entry.DurationSeconds) : null,
            entry.ThumbnailUrl)));
        searchResolver.RememberVideoMetadata(cfg.VideoQueue.Select(entry => new VideoSearchEntry(
            entry.Title, entry.Url, entry.Source,
            entry.DurationSeconds is { } duration && double.IsFinite(duration) && duration >= 0 &&
                duration < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(duration) : null,
            entry.ThumbnailUrl)));
    }
}

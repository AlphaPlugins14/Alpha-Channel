namespace AlphaChannel.Plugin.Video;

// Reuse the bounded, visibility-driven texture cache with the public-chat transport.
internal sealed class ChatLinkedImageCache : SafeImagePreviewCache
{
    internal ChatLinkedImageCache() : this(new ChatLinkedImageDownloader()) { }

    private ChatLinkedImageCache(ChatLinkedImageDownloader source) : base(source,
        source.DownloadAsync, url => ChatLinkedImageDownloader.TryValidateUrl(url, out _), 1024) { }
}

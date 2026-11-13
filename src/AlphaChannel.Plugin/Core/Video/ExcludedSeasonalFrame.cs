namespace AlphaChannel.Plugin.Video;

// Compatibility with the shared playback protocol only. The seasonal show
// and its frame animation are excluded from this public source snapshot.
internal static class HalloweenTvFrame
{
    internal static bool IsVideo(string? url) => false;
    internal static bool ShouldOverride(bool localActive, bool viewer, string? reason, string? hostUrl, string? playingUrl) => false;
    internal static TvFrameSelection At(double seconds) => new();
    internal static string EncodeMode(string reason, bool enabled) => reason;
}

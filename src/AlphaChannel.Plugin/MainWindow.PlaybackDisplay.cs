using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // Studio preview deliberately bypasses the saved/shared queue: its loopback
    // URL must remain local. Describe it only while we own the actual playback.
    private VideoQueueEntry? WithStudioPlaybackDisplay(VideoQueueEntry? current)
    {
        if (!StudioTvTransportActive || string.IsNullOrWhiteSpace(video.CurrentMediaUrl)) return current;
        return new VideoQueueEntry(video.CurrentMediaUrl, DjBroadcastContentTitle,
            spotifyOwnsStudio ? "Spotify" : "Broadcast Studio", null, null, isTransient: true);
    }
}

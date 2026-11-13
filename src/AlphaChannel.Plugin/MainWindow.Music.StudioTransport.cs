using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool StudioTvTransportActive => studioClient is not null && studioSessionActive &&
        (StudioOwnPreviewPlaying || studioBroadcastRequested && StudioOwnStreamPlaying) && stream.Mode != StreamMode.Viewing;

    private bool CanSkipStudioSong => StudioTvTransportActive && !StudioUsesApplication && StudioState.Ready;

    private bool CanUseBroadcastStudio => stream.Mode != StreamMode.Viewing &&
        !screenController.Engine.HasActiveNonVideoPlayback &&
        (!studioSessionActive || video.State is not (VideoPlaybackState.Loading or VideoPlaybackState.Playing or VideoPlaybackState.Paused) ||
         StudioTvTransportActive || studioPreviewStarting);

    private string StudioUnavailableMessage => stream.Mode == StreamMode.Viewing
        ? "Leave this Watch Party before using Broadcast Studio."
        : "Stop the current TV content before using Broadcast Studio.";

    // Consume studio transport actions even when disabled, so they never fall
    // through to the unrelated video queue or attempt to seek the live feed.
    private bool TryStudioSongTransport(bool next)
    {
        if (!StudioTvTransportActive) return false;
        if (CanSkipStudioSong) StudioAction(next ? "next" : "previous");
        return true;
    }
}

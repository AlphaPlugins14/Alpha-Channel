using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    internal (bool Playing, string Status, string? Title) GetServerInfoActivity()
    {
        var engine = screenController.Engine;
        if (ActiveActivity != ActiveActivityKind.None)
            return (true, "Activity in progress", ActivityTitle(ActiveActivity));
        if (engine.IsPlayingGame)
        {
            var console = engine.IsPlayingSnes ? "SNES" : engine.IsPlayingNes ? "NES" :
                engine.IsPlayingGameBoyAdvance ? "Game Boy Advance" : engine.IsPlayingGameBoy ? "Game Boy" :
                engine.IsPlayingMasterSystem ? "Master System" : "Game Gear";
            return (true, "Playing", console);
        }
        if (engine.IsPlayingBrowser)
            return (true, "Browsing", "Web browser");
        if (engine.IsPlayingImage)
            return (true, "Displaying", engine.CurrentImageSelection?.Mode == ImageMediaMode.Slideshow
                ? "Slideshow" : "Still image");
        if (StudioOwnsPlaybackControls)
            return (StudioState.Playing, StudioState.Playing ? "Playing" : "Ready", "Broadcast Studio");

        if (video.State is VideoPlaybackState.Idle or VideoPlaybackState.Failed)
            return (false, stream.Mode == StreamMode.None ? "Idle" : "Waiting for content", null);
        var title = stream.Mode == StreamMode.Viewing
            ? stream.CurrentRoomState?.MediaTitle
            : queue.Current?.Title;
        if (string.IsNullOrWhiteSpace(title)) title = engine.GetMediaTitle();
        if (video.IsAudioOnly) title = GetRadioDisplayTitle(title);
        // Metadata may fall back to a URL or local file path; keep those out of this tooltip.
        if (string.IsNullOrWhiteSpace(title) || Uri.TryCreate(title, UriKind.Absolute, out _) ||
            title.StartsWith('/') || title.StartsWith('\\'))
            title = engine.IsPlayingLocalVideo ? "Local video" : video.IsAudioOnly ? "Audio" : "Video";
        title = new string(title.Where(character => !char.IsControl(character)).Take(200).ToArray());
        return (video.State == VideoPlaybackState.Playing, video.State switch
        {
            VideoPlaybackState.Playing => "Playing",
            VideoPlaybackState.Paused => "Paused",
            _ => "Loading",
        }, title);
    }
}

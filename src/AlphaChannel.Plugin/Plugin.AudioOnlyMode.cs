using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

public sealed partial class Plugin
{
    private StreamMode audioOnlyPartyMode;
    private bool MiniPlaybackActive => miniPlayerWindow.IsOpen || screenController.Engine.AudioOnlyMode;

    private bool CanEnterAudioOnlyMode() => !mainWindow.HalloweenActivityActive &&
        !screenController.Engine.HalloweenPlayback && !mainWindow.IsArcadeGameActive &&
        !screenController.Engine.HasActiveNonVideoPlayback &&
        video.State is VideoPlaybackState.Loading or VideoPlaybackState.Playing or VideoPlaybackState.Paused;

    private void EnterAudioOnlyMode()
    {
        if (!CanEnterAudioOnlyMode() || screenController.Engine.AudioOnlyMode) return;
        audioOnlyPartyMode = stream.Mode;
        screenController.Engine.SetAudioOnlyMode(true);
        miniPlayerWindow.IsOpen = false;
        miniPlayerWasOpen = false;
        dutyViewerPlaybackStopped = false;
        if (stream.Mode == StreamMode.Viewing) mainWindow.HideViewerTvForDuty();
        viewerTvNeedsLocationRecheckAfterZoning = false;
        ChatGui.Print("[Alpha Channel] Entering audio-only mode. Open the mini player or spawn a TV to return to video.");
    }

    private void TickAudioOnlyMode()
    {
        var engine = screenController.Engine;
        if (!engine.AudioOnlyMode) return;
        if (mainWindow.HalloweenActivityActive || engine.HalloweenPlayback ||
            stream.Mode != audioOnlyPartyMode || engine.HasActiveNonVideoPlayback || miniPlayerWindow.IsOpen ||
            video.State is VideoPlaybackState.Idle or VideoPlaybackState.Failed)
        {
            engine.SetAudioOnlyMode(false);
            return;
        }
        // New media may assign a screen as it loads. Keep its decoder alive,
        // but never let that assignment bring a listening-only TV back.
        if (stream.Mode == StreamMode.Viewing) mainWindow.HideViewerTvForDuty();
        engine.DespawnScreen();
    }
}

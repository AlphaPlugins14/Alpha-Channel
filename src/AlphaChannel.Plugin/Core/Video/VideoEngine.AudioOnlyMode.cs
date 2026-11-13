namespace AlphaChannel.Plugin.Video;

internal sealed partial class VideoEngine
{
    private volatile bool audioOnlyMode;
    // Listening preference, distinct from IsAudioOnly (the source is music).
    internal bool AudioOnlyMode => audioOnlyMode;

    internal void SetAudioOnlyMode(bool enabled)
    {
        if (audioOnlyMode == enabled) return;
        audioOnlyMode = enabled;
        if (enabled)
        {
            DespawnScreen();
            _screenPainter?.SetAudioEffectsPlaybackActive(false);
            // Native spotlight cleanup stays on the existing framework tick.
            SetFrameConsumersVisible(false);
        }
        else SetFrameConsumersVisible(true);
    }
}

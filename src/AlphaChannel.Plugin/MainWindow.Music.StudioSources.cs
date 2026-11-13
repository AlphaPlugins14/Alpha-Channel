using AlphaChannel.BroadcastStudio;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void SetStudioMusicSource(StudioMusicSource source)
    {
        if (source == studioSettings.MusicSource) return;
        studioSeeking = false;
        studioSelectedTrackId = null;
        // Source selection changes the mix, not ownership of the Studio activity.
        studioSettings = studioSettings with { MusicSource = source, ApplicationCaptureEnabled = false };
        StudioSettingsChanged();
        if (source == StudioMusicSource.Application) StudioAction("applications");
    }

    private void SetStudioApplicationCapture(bool enabled)
    {
        // Silence/capture is independent of Start/End, in preview and on air.
        studioSettings = studioSettings with { ApplicationCaptureEnabled = enabled };
        StudioSettingsChanged();
    }
}

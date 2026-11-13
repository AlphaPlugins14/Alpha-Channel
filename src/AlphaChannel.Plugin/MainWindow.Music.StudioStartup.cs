using AlphaChannel.BroadcastStudio;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void StartStudioActivity()
    {
        if (!CanUseBroadcastStudio || studioSessionActive) return;
        studioSessionActive = true;
        studioPreviewStarting = true;
        studioStartedUtc = DateTime.UtcNow;
        EnsureMusicScene();
        EnsureStudio();
        studioSettings = studioSettings with { MonitorVolume = Plugin.Cfg.Muted ? 0 : Plugin.Cfg.Volume / 100f };
        if (studioClient == null && studioAudioPreparation == null)
        {
            studioSessionActive = studioPreviewStarting = false;
            return;
        }
        queue.StopPlayback();
        video.ShowWaitingScreen();
        if (studioClient != null) RequestStudioPreview();
    }

    private void RequestStudioPreview()
    {
        // The helper connection timeout starts after dependency preparation.
        studioStartedUtc = DateTime.UtcNow;
        musicMessage = null;
        StudioSettingsChanged(false);
        studioClient!.Send(new StudioCommand("preview"));
        if (spotifyOwnsStudio) studioClient.Send(new StudioCommand("applications"));
    }

    private bool ContinueStudioAudioPreparation()
    {
        if (studioClient != null) return true;
        if (studioAudioPreparation is not { IsCompleted: true } preparation) return false;
        try { preparation.GetAwaiter().GetResult(); }
        catch
        {
            musicMessage = screenController.Engine.Resources.FFmpegUnavailableMessage;
            StopStudioBroadcast("Studio audio preparation failed", pauseSpotify: false);
            return false;
        }
        EnsureStudio();
        studioAudioPreparation = null;
        if (studioClient == null)
        {
            StopStudioBroadcast("Studio helper could not start after preparation", pauseSpotify: false);
            return false;
        }
        RequestStudioPreview();
        return true;
    }
}

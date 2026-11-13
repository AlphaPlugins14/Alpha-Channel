using AlphaChannel.BroadcastStudio;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool studioStopConfirmationRequested;
    private bool studioStopWasSpotify;
    private bool StudioOwnsPlaybackControls => stream.Mode != StreamMode.Viewing &&
        (studioSessionActive || studioClient is not null &&
            (studioBroadcastRequested || StudioState.Playing || studioSettings.ApplicationCaptureEnabled));

    internal bool CanStartOtherStudioMedia()
    {
        if (HalloweenActivityActive || hostTransferPreparing || stream.IsJoiningWatchParty) return false;
        if (!StudioOwnsPlaybackControls) return true;
        NotifyActivityStartBlocked();
        Plugin.ChatGui.Print("[AlphaChannel] Stop Broadcast Studio before starting other TV content.");
        return false;
    }

    internal bool CanStartStudioPlaybackUrl(string url)
    {
        if (!StudioOwnsPlaybackControls) return true;
        if (studioSessionActive && studioPreviewUrl != null && url.Split('#')[0] == studioPreviewUrl) return true;
        // Publishing/reloading our own live feed and adjusting its scene are allowed.
        if (studioBroadcastRequested && radioCredentials is { } radio &&
            Uri.TryCreate(url, UriKind.Absolute, out var source) && Uri.TryCreate(radio.ListenUrl, UriKind.Absolute, out var own) &&
            string.Equals(source.GetLeftPart(UriPartial.Path), own.GetLeftPart(UriPartial.Path), StringComparison.OrdinalIgnoreCase)) return true;
        return CanStartOtherStudioMedia();
    }

    internal void ApplyListeningVolume()
    {
        if (!StudioOwnsPlaybackControls)
        {
            video.SetVolume(Plugin.Cfg.Muted ? 0 : Plugin.Cfg.Volume);
            return;
        }
        var audible = !Plugin.Cfg.Muted && Plugin.Cfg.Volume > 0;
        studioSettings = studioSettings with
        {
            MonitorVolume = audible ? Plugin.Cfg.Volume / 100f : 0,
            MonitorApplication = !spotifyOwnsStudio && (StudioUsesApplication && audible || studioSettings.MonitorApplication)
        };
        StudioSettingsChanged(false);
        // Listen through the studio monitor, without also hearing the delayed TV feed.
        video.SetOutputMuted(true);
    }

    private void SetStudioListeningVolume(float volume)
    {
        Plugin.Cfg.Volume = Math.Clamp((int)MathF.Round(volume * 100), 0, 130);
        Plugin.Cfg.Muted = Plugin.Cfg.Volume == 0;
        studioSettings = studioSettings with { MonitorVolume = volume };
        if (!spotifyOwnsStudio && StudioUsesApplication && volume > 0) studioSettings = studioSettings with { MonitorApplication = true };
    }

    private void ToggleStudioSongPlayback()
    {
        var selected = Plugin.Cfg.StudioQueue.FirstOrDefault(track => track.Id == studioSelectedTrackId);
        var changeSong = selected is not null && selected.Id != StudioState.TrackId;
        StudioAction(StudioState.Playing && !changeSong ? "pause" : "play", selected?.Id);
    }

    private void SelectStudioSong(string track) => studioSelectedTrackId = track;

    private bool TryRequestStudioStop()
    {
        if (!studioBroadcastRequested) return false;
        studioStopWasSpotify = spotifyOwnsStudio;
        studioStopConfirmationRequested = true;
        OpenUi();
        SetMinimized(false);
        return true;
    }

}

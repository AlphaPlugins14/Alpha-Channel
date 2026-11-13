using AlphaChannel.BroadcastStudio;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool spotifyOwnsStudio, spotifyStartPending, spotifyCaptureSelected, spotifyShareAfterStart;
    private StudioSettings? spotifyPreviousStudioSettings;
    private string? spotifyBroadcastAccount;
    private DateTime spotifyBroadcastDeadline;
    private DateTime spotifyNextApplicationRefresh;
    private int spotifyApplicationCandidates;

    private void StartSpotifyBroadcast()
    {
        if (CurrentSession is null || radioCredentials is null || !CanUseBroadcastStudio) return;
        if (spotifyOwnsStudio)
        {
            if (spotifyStartPending) spotifyShareAfterStart = true;
            else BeginStudioBroadcast();
            return;
        }
        StartSpotifySession(share: true);
    }

    private bool CanStartSpotifyTv => !studioSessionActive && CanUseBroadcastStudio &&
        !HalloweenActivityActive && !hostTransferPreparing && !stream.IsJoiningWatchParty;

    private void ToggleSpotifyTvPlayback(SpotifyPlayback player)
    {
        if (!player.Connected) return;
        if (!spotifyOwnsStudio)
        {
            if (!StartSpotifySession(share: false)) return;
            // Already-playing desktop audio still needs a local TV session,
            // without toggling the song off while opening its scene.
            if (player.Playing) return;
        }
        var command = player.Playing ? "pause" : "play";
        spotifyClient?.SendCommand(player.Enabled(player.Playing ? "Pause" : "Play") ? command : "toggle");
    }

    private bool StartSpotifySession(bool share)
    {
        if (!CanStartSpotifyTv) return false;
        if (!screenController.Engine.TryInitializeGraphics())
        {
            musicMessage = screenController.Engine.LastError ?? "The TV could not start. Press Play to try again.";
            return false;
        }
        EnsureStudio();
        spotifyPreviousStudioSettings = studioSettings;
        // Capture only the selected Spotify process. Never include the user's
        // saved microphone, decks, or duplicate local monitoring in this mix.
        studioSettings = studioSettings with { MusicSource = StudioMusicSource.Application,
            ApplicationProcessId = 0, ApplicationStartedUtcTicks = 0, ApplicationCaptureEnabled = false,
            MicEnabled = false, TalkHeld = false, MonitorApplication = false, MonitorMic = false,
            MusicVolume = 1, MasterVolume = 1 };
        spotifyOwnsStudio = spotifyStartPending = true;
        spotifyShareAfterStart = share;
        spotifyCaptureSelected = false;
        spotifyApplicationCandidates = 0;
        spotifyNextApplicationRefresh = DateTime.UtcNow.AddSeconds(1);
        spotifyBroadcastAccount = CurrentSession?.AccountId;
        spotifyBroadcastDeadline = DateTime.UtcNow.AddSeconds(25);
        musicMessage = null;
        StartStudioActivity();
        if (!studioSessionActive) { RestoreSpotifyStudioSettings(pauseSpotify: false); return false; }
        return true;
    }

    internal void UpdateSpotifyBroadcast()
    {
        if (!spotifyOwnsStudio) return;
        spotifyClient?.Tick();
        if (CurrentSession?.AccountId != spotifyBroadcastAccount || stream.Mode == StreamMode.Viewing || !studioSessionActive)
        {
            StopStudioBroadcast(CurrentSession?.AccountId != spotifyBroadcastAccount ? "Spotify account changed" :
                stream.Mode == StreamMode.Viewing ? "Spotify session became a viewer" : "Studio session is inactive", pauseSpotify: false);
            return;
        }
        if (StudioState.ApplicationError is { Length: > 0 } error)
        { musicMessage = "Spotify capture stopped: " + error; StopStudioBroadcast(pauseSpotify: false); return; }
        if (!spotifyStartPending) return;
        if (studioPreviewStarting || !StudioState.Ready) return;
        if (DateTime.UtcNow > spotifyBroadcastDeadline)
        {
            musicMessage = !spotifyCaptureSelected
                ? spotifyApplicationCandidates > 1
                    ? "More than one Spotify app is running. Close the extra app and press Play again."
                    : "Spotify could not be found for TV audio. Keep the desktop app open and press Play again."
                : "Spotify audio could not start. Keep the Spotify desktop app open and try again.";
            StopStudioBroadcast(pauseSpotify: false);
            return;
        }
        if (!StudioState.ApplicationCaptureSupported)
        { musicMessage = "Spotify TV playback requires Windows build 20348 or later."; StopStudioBroadcast(pauseSpotify: false); return; }
        if (!spotifyCaptureSelected)
        {
            var apps = StudioState.Applications.Where(IsSpotifyApplication).ToArray();
            spotifyApplicationCandidates = apps.Length;
            if (apps.Length != 1)
            {
                if (DateTime.UtcNow >= spotifyNextApplicationRefresh)
                {
                    spotifyNextApplicationRefresh = DateTime.UtcNow.AddSeconds(1);
                    studioClient?.Send(new StudioCommand("applications"));
                }
                return;
            }
            var app = apps[0];
            studioSettings = studioSettings with { ApplicationProcessId = app.ProcessId,
                ApplicationStartedUtcTicks = app.StartedUtcTicks, ApplicationCaptureEnabled = true };
            StudioSettingsChanged(false);
            spotifyCaptureSelected = true;
            return;
        }
        if (!StudioState.ApplicationCapturing) return;
        spotifyStartPending = false;
        if (spotifyShareAfterStart)
        {
            spotifyShareAfterStart = false;
            BeginStudioBroadcast(); // Retains the existing room creation flow and allowance checks.
        }
    }

    // The helper enumerates the executable and owns capture. It revalidates
    // the selected PID and start time when opening the recorder.
    private static bool IsSpotifyApplication(StudioApplication app) =>
        app.ProcessId > 0 && app.StartedUtcTicks > 0 &&
        string.Equals(app.ProcessName, "Spotify", StringComparison.OrdinalIgnoreCase);

    private void RestoreSpotifyStudioSettings(bool pauseSpotify)
    {
        spotifyCoverPublisher.Update(null, null);
        // Queue behind any in-flight seek/play operation so ending the activity
        // leaves Spotify paused, rather than dropping the pause while busy.
        if (spotifyOwnsStudio && pauseSpotify) spotifyClient?.SendCommand("pause", queue: true);
        spotifyOwnsStudio = spotifyStartPending = spotifyCaptureSelected = spotifyShareAfterStart = false;
        spotifyBroadcastAccount = null;
        if (spotifyPreviousStudioSettings is { } previous)
            studioSettings = previous with { ApplicationCaptureEnabled = false, MicEnabled = false, TalkHeld = false };
        spotifyPreviousStudioSettings = null;
    }

    private string SpotifyBroadcastTitle
    {
        get
        {
            var player = spotifyClient is null ? null : new SpotifyPlayback(spotifyClient.Snapshot);
            var title = player?.Text("Track / Title");
            var artist = player?.Text("Track / Artist");
            return string.IsNullOrWhiteSpace(title) ? "Spotify" : string.IsNullOrWhiteSpace(artist) ? title : title + " — " + artist;
        }
    }
}

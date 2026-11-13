namespace AlphaChannel.Plugin.Video;

internal enum VideoPlaybackState : byte
{
    Idle,
    Loading,
    Playing,
    Paused,
    Failed,
}

internal sealed class VideoPlayer : IDisposable
{
    private readonly VideoEngine engine;

    private string? currentMediaUrl;
    private bool restartLiveOnResume;
    private DateTime? idleScreensaverEligibleSinceUtc;
    private string? idleScreensaverStatus;
    private int idleScreensaverPlaybackAttemptId = -1;
    private int waitingScreenVersion;
    private int idleScreensaverWaitingScreenVersion = -1;

    private AudioVisualizerMode currentVisualizerMode =
        AudioVisualizerMode.ClassicBars;

    private AudioVisualizerTheme currentVisualizerTheme =
    AudioVisualizerTheme.AlphaPurple;

    private bool viewerClassicBarsOverride;

    internal void SetViewerClassicBarsOverride(bool enabled)
    {
        if (viewerClassicBarsOverride == enabled) return;
        viewerClassicBarsOverride = enabled;
        // Retain the host's requested mode/theme so disabling the local override
        // restores their latest selection without reloading the radio stream.
        if (currentMediaUrl is not null && engine.HasVideoPlaybackSession)
            engine.SetAudioVisualizerMode(
                enabled ? AudioVisualizerMode.ClassicBars : currentVisualizerMode,
                currentVisualizerTheme);
    }

    public VideoPlayer(VideoEngine engine)
    {
        this.engine = engine;
        engine.ExternalPlaybackTakingOver += OnExternalPlaybackTakingOver;
    }

    internal event Action? ExternalPlaybackTakingOver;
    internal Func<bool>? ShouldSpawnTvOnResume { get; set; }
    internal Func<bool>? ShouldPreserveTvOnPlaybackFailure { get; set; }
    internal Func<bool>? PreserveHostDjAudioSignal { get; set; }
    internal Func<string, bool>? CanStartPlayback { get; set; }
    internal bool TryPreparePlayback(string url) => (CanStartPlayback?.Invoke(url) ?? true) && engine.TryPrepareYouTubePlayback(url);

    public VideoPlaybackState State { get; private set; } = VideoPlaybackState.Idle;
    internal string? CurrentMediaUrl => currentMediaUrl;
    internal string? LastDisplayMediaUrl { get; private set; }
    internal string? LastDisplayMediaTitle { get; private set; }
    internal string? CurrentResolvedMediaUrl => engine.GetCurrentUrl();
    internal IReadOnlyList<AudioTrack> GetAudioTracks() => engine.GetAudioTracks();
    internal int? GetSelectedAudioTrackId() => engine.GetSelectedAudioTrackId();
    internal bool SelectAudioTrack(int id) => engine.SelectAudioTrack(id);
    internal IReadOnlyList<SubtitleTrack> GetSubtitleTracks() => engine.GetSubtitleTracks();
    internal IReadOnlyList<SubtitleTrack> GetEmbeddedSubtitleTracks() => engine.GetEmbeddedSubtitleTracks();
    internal bool SelectSubtitleTrack(int id) => engine.SelectSubtitleTrack(id);
    internal bool SelectSubtitle(string? language, string? name) => engine.SelectSubtitle(language, name);
    internal string? GetSelectedSubtitleId() => engine.GetSelectedSubtitleId();
    public string? LastError { get; private set; }
    public int PlaybackAttemptId { get; private set; }
    internal int SeekVersion { get; private set; }

    public bool IsPlayingSnes =>
        engine.IsPlayingSnes;

    public bool IsPlayingGame =>
        engine.IsPlayingGame;

    public bool IsPlayingBrowser =>
        engine.IsPlayingBrowser;

    public bool IsPlayingWhiteboard =>
        engine.IsPlayingWhiteboard;

    public bool IsPlayingLocalVideo =>
        engine.IsPlayingLocalVideo;

    public bool IsPlayingImage =>
    engine.IsPlayingImage;

    internal bool HasVideoPlaybackSession =>
        engine.HasVideoPlaybackSession;

    internal BroadcastDiagnosticsSnapshot BroadcastDiagnostics =>
        engine.BroadcastDiagnostics;

    public bool IsAudioOnly =>
    engine.IsAudioOnly;

    public bool HardwareDecoding
    {
        get => engine.HardwareDecoding;
        set => engine.HardwareDecoding = value;
    }

    public bool AllowInsecureDirectUrls
    {
        get => engine.AllowInsecureDirectUrls;
        set => engine.AllowInsecureDirectUrls = value;
    }

    public int MaxQualityHeight
    {
        get => engine.MaxQualityHeight;
        set => engine.MaxQualityHeight = value;
    }

    public string? CookiesPath
    {
        get => engine.CookiesPath;
        set => engine.CookiesPath = value;
    }

    public void ShowWaitingScreen()
    {
        waitingScreenVersion++;
        engine.ShowWaitingScreen();
    }

    public void UpdateIdleScreensaver(bool miniPlayerOpen = false, bool miniWaitingForContent = false)
    {
        if (broadcastScreensaverStatus is { } broadcastStatus)
        {
            engine.SetIdleScreensaver(broadcastStatus);
            return;
        }

        string? status = null;
        if ((engine.IsActive || miniPlayerOpen) && !engine.HasActiveNonVideoPlayback)
        {
            status = engine.IsShowingWaitingScreen || miniWaitingForContent
                ? "Waiting for content"
                : State == VideoPlaybackState.Paused
                    ? "Playback paused"
                    : State is VideoPlaybackState.Idle or VideoPlaybackState.Failed
                        ? "Nothing playing"
                        : null;
        }

        var activityChanged = idleScreensaverPlaybackAttemptId != PlaybackAttemptId ||
                              idleScreensaverWaitingScreenVersion != waitingScreenVersion ||
                              !string.Equals(idleScreensaverStatus, status, StringComparison.Ordinal);
        if (activityChanged)
        {
            idleScreensaverPlaybackAttemptId = PlaybackAttemptId;
            idleScreensaverWaitingScreenVersion = waitingScreenVersion;
            idleScreensaverStatus = status;
            idleScreensaverEligibleSinceUtc = status is null ? null : DateTime.UtcNow;
            engine.SetIdleScreensaver(null);
        }

        if (status is null)
        {
            idleScreensaverEligibleSinceUtc = null;
            engine.SetIdleScreensaver(null);
            return;
        }

        // An empty TV or mini player starts the screensaver immediately.
        // Loading/waiting and paused playback retain their existing delay.
        if (status == "Nothing playing")
        {
            engine.SetIdleScreensaver(status);
            return;
        }

        idleScreensaverEligibleSinceUtc ??= DateTime.UtcNow;
        if (DateTime.UtcNow - idleScreensaverEligibleSinceUtc.Value >= TimeSpan.FromMinutes(5))
            engine.SetIdleScreensaver(status);
    }

    public void SetVolume(int volumePercent)
    {
        // Every volume/mute control uses this facade. Keep a host's own DJ
        // return feed decoding at its existing level when re-muting output.
        if (volumePercent == 0 && !engine.HasActiveNonVideoPlayback &&
            PreserveHostDjAudioSignal?.Invoke() == true)
        {
            engine.SetOutputMuted(true);
            return;
        }

        engine.SetVolume(volumePercent);
    }

    public void SetOutputMuted(bool muted) => engine.SetOutputMuted(muted);

    public void SetOverlayTitle(string title, string source)
    {
        if (!string.IsNullOrWhiteSpace(title) && currentMediaUrl is not null)
        {
            LastDisplayMediaUrl = currentMediaUrl;
            LastDisplayMediaTitle = title;
        }
        if (!string.IsNullOrWhiteSpace(source))
            restartLiveOnResume = IsNonSeekableLiveSource(source);
        engine.SetOverlayTitle(title, source);
    }

    internal static bool IsNonSeekableLiveSource(string source) =>
        source is "Radio" or "Music / DJ" or "Live Stream";

    internal IReadOnlyList<ReactionParticle> CurrentReactions { get; private set; } = [];

    public void SetReactions(IReadOnlyList<ReactionParticle> reactions)
    {
        CurrentReactions = reactions;
        engine.SetReactions(reactions);
    }

    public bool TryGetReactionSpawnX(Vector3 characterPosition, out float x) =>
        engine.TryGetReactionSpawnX(characterPosition, out x);

    // True once mpv has nothing left to play (natural end, with keep-open=yes so it doesn't
    // reset position) or before anything has ever been loaded. Callers polling this for
    // auto-advance should throttle - see AetherStreamQueue, which does not poll every frame.
    public bool IsIdle()
    {
        CheckForPlaybackFailure();

        if (State == VideoPlaybackState.Failed)
        {
            return true;
        }

        return engine.GetIdle();
    }

    private void CheckForPlaybackFailure()
    {
        if (State == VideoPlaybackState.Idle ||
            State == VideoPlaybackState.Failed)
        {
            return;
        }


        //
        // VideoEngine may currently be handling the first MPV failure
        // by running WebMediaUrlResolver and preparing a second attempt.
        //
        // During that period the original MPV failure is NOT the final
        // playback result.
        //
        // Do not call StopVideo() here, because that would kill the
        // resolver while it is still working.
        //

        if (engine.WebResolverFallbackRunning)
        {
            State =
                VideoPlaybackState.Loading;

            return;
        }


        //
        // No final playback error yet.
        //

        if (engine.LastError is not { } error)
        {
            return;
        }


        //
        // Both chances are now finished and playback genuinely failed.
        //

        State =
            VideoPlaybackState.Failed;


        LastError =
            error;


        AepLog.Warning(
            "[Video] Playback failed; resetting player.");


        try
        {
            engine.StopVideo(preserveScreen:
                ShouldPreserveTvOnPlaybackFailure?.Invoke() == true ||
                currentMediaUrl is { } failedUrl && (IsAlphaChannelLiveHls(failedUrl) || LiveDjStream.AppliesTo(failedUrl)));
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[Video] Failed to reset player after playback error.");
        }
    }

    // AlphaChannel's engine resolves both YouTube and generic page URLs itself, via mpv's bundled
    // ytdl_hook + yt-dlp (see MpvRenderer's "ytdl"/"ytdl-format" options) - unlike the old
    // VideoPlayer this replaces, there's no separate YoutubeExplode pre-resolution step needed
    // here; that resolver (VideoUrlResolver) is kept only for AetherStreamQueue's metadata
    // enrichment (title/duration/thumbnail), not for the playback URL itself.

    private static bool IsAlphaChannelLiveHls(string url) => LiveBroadcastStream.AppliesTo(url);

    public void Play(
    string url, bool forceRestart = false)
    {
        if (!TryPreparePlayback(url)) return;

        ClearBroadcastStatusScreen();

        if (engine.IsPlayingWhiteboard)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Stop the active TV activity before using video playback.");

            return;
        }

        if (engine.IsPlayingGame || engine.IsPlayingBrowser)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Stop the current game or browser before using video playback.");

            return;
        }

        if (engine.IsPlayingLocalVideo)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Stop the local video before using other media playback.");

            return;
        }

        //
        // Image descriptors must be handled before AudioVisualizerSelection,
        // because both features store synchronization metadata in URL
        // fragments. Image URLs never reach mpv or yt-dlp.
        //
        if (ImageMediaSelection.TryParse(
                url,
                out var imageSelection) &&
            imageSelection is not null)
        {
            var sameImageMedia =
                !string.IsNullOrWhiteSpace(
                    currentMediaUrl) &&
                string.Equals(
                    currentMediaUrl,
                    url,
                    StringComparison.Ordinal) &&
                engine.IsPlayingImage &&
                State is
                    VideoPlaybackState.Loading or
                    VideoPlaybackState.Playing or
                    VideoPlaybackState.Paused;

            if (sameImageMedia)
            {
                return;
            }

            try
            {
                PlaybackAttemptId++;

                currentMediaUrl =
                    url;

                currentVisualizerMode =
                    AudioVisualizerMode.ClassicBars;

                currentVisualizerTheme =
                    AudioVisualizerTheme.AlphaPurple;

                LastError =
                    null;

                State =
                    VideoPlaybackState.Loading;

                engine.PlayImage(
                    imageSelection);

                if (engine.LastError is { } immediateError)
                {
                    State =
                        VideoPlaybackState.Failed;

                    LastError =
                        immediateError;

                    return;
                }

                State =
                    VideoPlaybackState.Playing;
            }
            catch (Exception exception)
            {
                currentMediaUrl =
                    null;

                State =
                    VideoPlaybackState.Failed;

                LastError =
                    exception.Message;

                AepLog.Warning(
                    "[Image] Failed to start playback.");
            }

            return;
        }

        //
        // A malformed AlphaChannel image descriptor must not fall through to
        // mpv as an ordinary web/video URL.
        //
        if (url.Contains(
                "#acmedia=",
                StringComparison.OrdinalIgnoreCase))
        {
            currentMediaUrl =
                null;

            State =
                VideoPlaybackState.Failed;

            LastError =
                "The shared image or slideshow information is invalid.";

            return;
        }

        if (url.Contains(ResolvedMediaLink.Marker, StringComparison.Ordinal) && !ResolvedMediaLink.TryDecode(url, out _))
        {
            currentMediaUrl = null;
            State = VideoPlaybackState.Failed;
            LastError = "The shared video choice is invalid. Open the original page to choose again.";
            return;
        }

        var expectedAudioOnly =
            LiveDjStream.AppliesTo(url) || AudioVisualizerSelection
                .HasAudioVisualizerMetadata(
                    url);

        var selection =
            AudioVisualizerSelection.Parse(
                url);

        var sameMedia =
            !forceRestart && !string.IsNullOrWhiteSpace(
                currentMediaUrl) &&
            string.Equals(
                currentMediaUrl,
                selection.MediaUrl,
                StringComparison.Ordinal) &&
            // A game/browser can stop MPV directly while this facade still
            // remembers the last URL. Only suppress a duplicate Play request
            // when the engine still owns a live or pending MPV session.
            engine.HasVideoPlaybackSession &&
            State is
                VideoPlaybackState.Loading or
                VideoPlaybackState.Playing or
                VideoPlaybackState.Paused;

        //
        // Always record/apply the visualizer selection first. During Loading,
        // VideoEngine remembers it and applies it once audio-only playback is
        // confirmed.
        //
        var visualizerChanged =
            (currentVisualizerMode != selection.Mode ||
            currentVisualizerTheme != selection.Theme);

        currentVisualizerMode =
            selection.Mode;
        currentVisualizerTheme =
    selection.Theme;

        // Text-only presentation changes pass through Play as well. Rebuilding
        // the FFmpeg graph for the same visualizer can interrupt live audio.
        // A new playback session still needs its selection applied even when
        // it matches the previous session's settings.
        if (!sameMedia || visualizerChanged)
        {
            engine.SetAudioVisualizerMode(
                viewerClassicBarsOverride ? AudioVisualizerMode.ClassicBars : selection.Mode,
                selection.Theme);
        }
        engine.SetMusicSceneEffects(expectedAudioOnly ? MusicSceneEffects.Parse(url) : MusicSceneEffects.Default, expectedAudioOnly);
        engine.SetAudioScreenText(
            expectedAudioOnly
                ? AudioScreenTextSelection.Parse(url)
                : AudioScreenTextSelection.Empty);

        //
        // A fragment-only change is presentation state, not new media.
        // Do not call PlayVideo because that would reload the Icecast stream.
        //
        if (sameMedia)
        {
            if (url.Contains("ac_live=1", StringComparison.OrdinalIgnoreCase))
                restartLiveOnResume = true;
            if (visualizerChanged)
            {
                
            }

            return;
        }

        restartLiveOnResume = url.Contains("ac_live=1", StringComparison.OrdinalIgnoreCase);

        try
        {
            PlaybackAttemptId++;

            currentMediaUrl =
                selection.MediaUrl;

            LastError =
                null;

            State =
                VideoPlaybackState.Loading;

            //
            // Only the normalized media URL is passed to MPV. The fragment is
            // AlphaChannel synchronization metadata and never reaches Icecast.
            //
            engine.PlayVideo(
     selection.MediaUrl,
     allowWebResolverFallback:
         !IsAlphaChannelLiveHls(selection.MediaUrl) && !LiveDjStream.AppliesTo(selection.MediaUrl),
     expectedAudioOnly:
         expectedAudioOnly,
     forceRestart: forceRestart);

            if (engine.LastError is { } immediateError)
            {
                LastError = immediateError;
                State = VideoPlaybackState.Failed;
                return;
            }

            engine.SetAudioScreenText(
                expectedAudioOnly
                    ? AudioScreenTextSelection.Parse(url)
                    : AudioScreenTextSelection.Empty);

            State =
                VideoPlaybackState.Playing;
        }
        catch (Exception exception)
        {
            currentMediaUrl =
                null;

            State =
                VideoPlaybackState.Failed;

            LastError =
                exception.Message;

            AepLog.Warning(
                "[Video] Failed to start playback.");
        }
    }

    public bool PlayLocalVideo(
        string path)
    {
        if (!TryPreparePlayback(path)) return false;
        if (engine.IsPlayingWhiteboard)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Stop the active TV activity before playing a local video.");

            return false;
        }

        if (engine.IsPlayingGame || engine.IsPlayingBrowser)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Stop the current game or browser before playing a local video.");

            return false;
        }

        if (string.IsNullOrWhiteSpace(path) ||
            !File.Exists(path))
        {
            LastError =
                "The selected local video file could not be found.";

            State =
                VideoPlaybackState.Failed;

            return false;
        }

        try
        {
            PlaybackAttemptId++;

            LastError =
                null;

            State =
                VideoPlaybackState.Loading;

            engine.PlayVideo(
                path,
                allowWebResolverFallback: false,
                isLocalVideo: true);

            if (!engine.IsPlayingLocalVideo)
            {
                State =
                    VideoPlaybackState.Failed;

                LastError ??=
                    engine.LastError ??
                    "Local video playback could not be started.";

                return false;
            }

            State =
                VideoPlaybackState.Playing;

            return true;
        }
        catch (Exception exception)
        {
            State =
                VideoPlaybackState.Failed;

            LastError =
                exception.Message;

            AepLog.Warning(
                "[LocalVideo] Failed to start playback.");

            return false;
        }
    }

    internal bool ManualTransportLocked { get; set; }

    public void Pause(bool pause)
    {
        if (!ManualTransportLocked) PauseForSynchronization(pause);
    }

    internal void PauseForSynchronization(bool pause)
    {
        //
        // A remote Watch Party state can call Pause every framework update.
        // Check the renderer first and preserve Failed/Idle so the same MPV
        // failure is not reset to Playing and reported again every frame.
        //

        CheckForPlaybackFailure();

        if (State == VideoPlaybackState.Failed ||
            State == VideoPlaybackState.Idle)
        {
            return;
        }

        var resuming = !pause && State == VideoPlaybackState.Paused;
        var screenWasActive = engine.IsActive;
        if (resuming &&
            currentMediaUrl is { } liveUrl && (restartLiveOnResume || IsNonSeekableLiveStream(liveUrl)))
        {
            // A paused live buffer is not available to someone joining later.
            // Reload the same source so host and viewers resume at the live edge.
            try
            {
                PlaybackAttemptId++;
                LastError = null;
                State = VideoPlaybackState.Loading;
                engine.PlayVideo(liveUrl,
                    allowWebResolverFallback: !IsAlphaChannelLiveHls(liveUrl) && !LiveDjStream.AppliesTo(liveUrl),
                    expectedAudioOnly: engine.IsAudioOnly || LiveDjStream.AppliesTo(liveUrl),
                    forceRestart: true,
                    preserveScreenVisibility: !screenWasActive);
                State = VideoPlaybackState.Playing;
                RestoreScreenOnResume(screenWasActive);
            }
            catch (Exception exception)
            {
                State = VideoPlaybackState.Failed;
                LastError = exception.Message;
                AepLog.Warning("[Video] Could not restart live playback.");
            }
            return;
        }

        engine.Pause(
            pause);

        State =
            pause
                ? VideoPlaybackState.Paused
                : VideoPlaybackState.Playing;
        if (resuming) RestoreScreenOnResume(screenWasActive);
    }

    private void RestoreScreenOnResume(bool screenWasActive)
    {
        if (screenWasActive || ShouldSpawnTvOnResume is null) return;
        // Live resume reloads the decoder and briefly attaches its screen.
        // Restore the chosen display without requiring a separate TV click.
        engine.DespawnScreen();
        if (ShouldSpawnTvOnResume()) engine.RespawnScreen();
    }

    public void Seek(float seconds)
    {
        if (!ManualTransportLocked) SeekForSynchronization(seconds);
    }

    internal void SeekForSynchronization(float seconds)
    {
        if (LiveDjStream.AppliesTo(currentMediaUrl)) return;
        SeekVersion++;
        engine.Seek((int)MathF.Round(seconds));
    }

    private bool IsNonSeekableLiveStream(string url)
    {
        if (LiveDjStream.AppliesTo(url)) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("twitch.tv", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".twitch.tv", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IsAlphaChannelLiveHls(url))
            return !uri.Query.Contains("ac_content=local-video", StringComparison.OrdinalIgnoreCase);
        // Audio files and podcasts have a duration and can resume normally;
        // live radio/DJ streams do not expose a finite playback duration.
        return engine.IsAudioOnly && engine.GetInfo()[1] <= 0d;
    }

    public (float Position, float Duration, bool Paused) GetProgress()
    {
        CheckForPlaybackFailure();

        var info = engine.GetInfo();
        return ((float)info[0], (float)info[1], engine.GetPaused());
    }

    // Separate from GetProgress() rather than folded into its tuple - existing call sites
    // deconstruct that tuple positionally and would silently break if it grew.
    public (int Width, int Height) GetResolution()
    {
        var info = engine.GetInfo();
        return ((int)info[3], (int)info[4]);
    }

    public byte[]? TryGetFrame(out int width, out int height) => engine.TryGetFrame(out width, out height);

    internal int StopVersion { get; private set; }
    public void Stop() { engine.SetAudioOnlyMode(false); StopVersion++; ResetPlayback(preserveScreen: false); }

    internal void ReleaseReplacedRemoteImage(string url)
    {
        // Only authoritative viewer updates use this. A host must explicitly
        // end a slideshow, but viewers must follow that host's next media URL.
        if (engine.IsPlayingImage && !ImageMediaSelection.TryParse(url, out _))
            ResetPlayback(preserveScreen: true);
    }

    internal void CompletePlayback()
    {
        ResetPlayback(preserveScreen: true);
        ShowWaitingScreen();
    }

    private void ResetPlayback(bool preserveScreen)
    {
        engine.StopVideo(preserveScreen);
        broadcastScreensaverStatus = null;
        engine.SetIdleScreensaver(null);

        currentMediaUrl =
            null;

        currentVisualizerMode =
            AudioVisualizerMode.ClassicBars;

        currentVisualizerTheme =
    AudioVisualizerTheme.AlphaPurple;

        State =
            VideoPlaybackState.Idle;
    }

    private string? broadcastScreensaverStatus;

    public void ShowBroadcastStatusScreen(string status)
    {
        if (string.Equals(broadcastScreensaverStatus, status, StringComparison.Ordinal))
            return;

        ResetPlayback(preserveScreen: true);
        ShowWaitingScreen();
        broadcastScreensaverStatus = status;
        if (status == LiveBroadcastStream.ConnectingMessage)
            engine.ShowLiveBroadcastConnectingScreen();
        else
            engine.SetIdleScreensaver(status);
    }

    public void ClearBroadcastStatusScreen()
    {
        if (broadcastScreensaverStatus is null)
            return;

        broadcastScreensaverStatus = null;
        engine.SetIdleScreensaver(null);
    }

    private void OnExternalPlaybackTakingOver()
    {
        // Queue listeners save the current resume point before this facade
        // forgets the MPV session.
        ExternalPlaybackTakingOver?.Invoke();

        currentMediaUrl = null;
        currentVisualizerMode = AudioVisualizerMode.ClassicBars;
        currentVisualizerTheme = AudioVisualizerTheme.AlphaPurple;
        LastError = null;
        State = VideoPlaybackState.Idle;
        PlaybackAttemptId++;
        idleScreensaverEligibleSinceUtc = null;
        engine.SetIdleScreensaver(null);
    }

    public void Dispose()
    {
        engine.ExternalPlaybackTakingOver -= OnExternalPlaybackTakingOver;
        Stop();
    }
}

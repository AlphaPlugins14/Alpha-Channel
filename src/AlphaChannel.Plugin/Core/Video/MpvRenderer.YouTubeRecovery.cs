namespace AlphaChannel.Plugin.Video;

internal partial class MpvRenderer
{
    private readonly YouTubeAccessRecovery youtubeAccessRecovery = new();
    private DateTime nextYouTubeRecoverySample;
    private int videoFrameLoad;
    private bool youtubeRecoveryResolving;
    private int mediaLoadedDiagnosticLoad;
    internal bool YouTubeRecoveryResolving => Volatile.Read(ref youtubeRecoveryResolving);
    internal Action? OnYouTubePlaybackHealthy;

    internal void SuspendYouTubeStartup()
    {
        lock (_mpvLock)
        {
            youtubeAccessRecovery.Suspend();
            Volatile.Write(ref youtubeRecoveryResolving, true);
        }
    }

    private void ReportYouTubeAccessRecovery()
    {
        Action<string>? failed = null;
        YouTubeAccessFailure reason = YouTubeAccessFailure.None;
        var load = 0;
        var started = false;
        if (!_mpvLock.TryEnter()) return;
        try
        {
            var now = DateTime.UtcNow;
            if (_closed || _mpvCtx == IntPtr.Zero || audioDiagnosticSource != PlaybackSourceKind.YouTube || !youtubeAccessRecovery.NeedsObservation || now < nextYouTubeRecoverySample) return;
            nextYouTubeRecoverySample = now.AddSeconds(1);
            load = audioDiagnosticLoad;
            var wasStarted = youtubeAccessRecovery.PlaybackStarted;
            reason = youtubeAccessRecovery.Observe(now, Volatile.Read(ref videoFrameLoad) == load,
                frameConsumersVisible && ReadPropertyString("vid") != "no" && DiagnosticNumber("current-tracks/video/id", 1, 100000) is not null,
                DiagnosticNumber("time-pos", 0, 604800), DiagnosticNumber("duration", 0, 604800),
                DiagnosticFlag("pause") == true, DiagnosticFlag("paused-for-cache") == true,
                DiagnosticFlag("seeking") == true, DiagnosticFlag("eof-reached") == true,
                ReadPropertyString("aid") != "no" && DiagnosticNumber("current-tracks/audio/id", 1, 100000) is not null,
                DiagnosticNumber("audio-pts", -60, 604800), (int?)DiagnosticNumber("audio-out-params/samplerate", 1, 1000000),
                ReadPropertyString("aid") == "no");
            started = !wasStarted && youtubeAccessRecovery.PlaybackStarted;
            if (reason != YouTubeAccessFailure.None) failed = OnPlaybackFailed;
        }
        finally { _mpvLock.Exit(); }
        if (started && reason == YouTubeAccessFailure.None && !_closed && load == Volatile.Read(ref audioDiagnosticLoad))
        {
            AepLog.PlaybackAttempt(new(PlaybackAttemptStage.PlaybackStarted, diagnosticPlaybackRoute,
                0, audioDiagnosticId, load, diagnosticAccount, null, load > 1, true));
            OnYouTubePlaybackHealthy?.Invoke();
        }
        // Callbacks may issue commands or schedule extraction. Never invoke one
        // under the MPV lock, and never apply an old sample to a replacement load.
        if (failed != null && !_closed && load == Volatile.Read(ref audioDiagnosticLoad))
        {
            AepLog.Warning($"[YouTube] Playback recovery required: renderer={audioDiagnosticId} load={load} reason={reason}.");
            failed(reason == YouTubeAccessFailure.StartupTimeout
                ? "YouTube playback did not start within 30 seconds after loading began."
                : reason is YouTubeAccessFailure.MissingAudio or YouTubeAccessFailure.StalledAudio
                    ? "YouTube audio stopped advancing; playback could not continue."
                    : "YouTube media access failed (HTTP 403/410); playback could not continue.");
        }
    }
}

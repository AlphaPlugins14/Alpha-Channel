namespace AlphaChannel.Plugin.Video;

internal enum YouTubeAccessFailure { None, Startup, PrematureEnd, MissingAudio, StalledAudio, StartupTimeout }

// An access warning alone is not a playback failure (it can be a subtitle).
// Require a failed playback state as well, and signal only once per native load.
internal sealed class YouTubeAccessRecovery
{
    private DateTime? deniedAt, missingAudioSince;
    private double? lastPosition;
    private double? lastAudioPosition;
    private bool audioObserved;
    private bool reported;
    private DateTime? startupSample;
    private TimeSpan startupElapsed;
    private bool startupComplete, suspended, startupPaused, awaitingMediaLoaded;
    internal bool PlaybackStarted => startupComplete;
    internal bool NeedsObservation => !suspended && !reported && (deniedAt is not null || startupSample is not null);

    internal void Reset()
    {
        deniedAt = missingAudioSince = startupSample = null; lastPosition = lastAudioPosition = null; reported = audioObserved = false;
        startupElapsed = TimeSpan.Zero; startupComplete = suspended = startupPaused = awaitingMediaLoaded = false;
    }
    // Called at loadfile, AFTER the engine has awaited PO-provider startup.
    internal void Start(DateTime now, bool paused = false) { Reset(); startupSample = now; startupPaused = paused; awaitingMediaLoaded = true; }
    internal void MediaLoaded()
    {
        awaitingMediaLoaded = false;
        lastPosition = null;
        lastAudioPosition = null;
    }
    internal void SetPaused(bool paused, DateTime now)
    {
        if (startupPaused == paused) return;
        startupPaused = paused;
        if (startupSample is not null) startupSample = now;
    }
    // A failed load must not time out again while its resolver/provider is working.
    internal void Suspend() { suspended = true; }
    internal void AccessDenied(int? status, DateTime now)
    { if (status is 403 or 410) deniedAt ??= now; }
    internal YouTubeAccessFailure Observe(DateTime now, bool hasFrame, bool expectsVisibleFrame,
        double? position, double? duration, bool paused, bool buffering, bool seeking, bool eof,
        bool audioSelected, double? audioPosition, int? sampleRate, bool audioDisabled = false)
    {
        if (reported || suspended) return YouTubeAccessFailure.None;
        // MPV retains the previous file's frame, clock and track properties
        // while loadfile is extracting its replacement. None belong to this load.
        if (awaitingMediaLoaded)
        {
            if (startupSample is { } waiting)
            {
                startupSample = now;
                if (!startupPaused && now > waiting) startupElapsed += now - waiting;
                if (startupElapsed >= TimeSpan.FromSeconds(30))
                { reported = true; return YouTubeAccessFailure.StartupTimeout; }
            }
            return YouTubeAccessFailure.None;
        }
        var moving = position is { } p && lastPosition is { } last && p - last > .05;
        var discontinuity = position is { } current && lastPosition is { } previous &&
            (current < previous || current - previous > 5);
        var audioMoving = audioPosition is { } ap && lastAudioPosition is { } previousAudio && ap - previousAudio > .05;
        var audioExpected = !audioDisabled && (audioSelected || audioObserved);
        var audioHealthy = !audioExpected || audioPosition is not null && sampleRate is not null &&
            audioMoving;
        audioObserved |= audioSelected && !audioDisabled;
        lastPosition = position;
        lastAudioPosition = audioPosition;
        if (startupSample is { } sample && !startupComplete)
        {
            startupSample = now;
            // A seek/resume position alone is not proof that media ever played.
            if ((hasFrame || moving) && audioHealthy)
                startupComplete = true;
            else if (!startupPaused && !seeking)
            {
                startupElapsed += now > sample ? now - sample : TimeSpan.Zero;
                // Buffering and missing track information must not suppress this deadline.
                if (startupElapsed >= TimeSpan.FromSeconds(30))
                { reported = true; return YouTubeAccessFailure.StartupTimeout; }
            }
        }
        // A stopped audio clock can retain a valid sample rate and track ID.
        // Observe it throughout playback, even when MPV did not log an HTTP error.
        // Compare clocks, never signal level: an intentionally silent passage is valid audio.
        if (paused || buffering || seeking || eof || audioDisabled || !audioExpected || !moving || discontinuity)
            missingAudioSince = null;
        else if (audioPosition is null || !audioMoving)
        {
            missingAudioSince ??= now;
            if (now - missingAudioSince >= TimeSpan.FromSeconds(8))
            {
                reported = true;
                return audioPosition is null ? YouTubeAccessFailure.MissingAudio : YouTubeAccessFailure.StalledAudio;
            }
        }
        else missingAudioSince = null;
        if (deniedAt is not { } denied || now - denied < TimeSpan.FromSeconds(2)) return YouTubeAccessFailure.None;
        YouTubeAccessFailure failure = YouTubeAccessFailure.None;
        if (eof && (!hasFrame && expectsVisibleFrame || position is { } end && duration is > 0 && end < duration - 3))
            failure = YouTubeAccessFailure.PrematureEnd;
        else if (paused || buffering || seeking || eof)
            return YouTubeAccessFailure.None;
        else if (!hasFrame && expectsVisibleFrame && now - denied >= TimeSpan.FromSeconds(10))
            failure = YouTubeAccessFailure.Startup;
        if (failure != YouTubeAccessFailure.None) reported = true;
        return failure;
    }
}

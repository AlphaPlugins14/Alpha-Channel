using System.Globalization;

namespace AlphaChannel.Plugin.Video;

internal partial class MpvRenderer
{
    private static int nextAudioDiagnosticId;
    private readonly int audioDiagnosticId = Interlocked.Increment(ref nextAudioDiagnosticId);
    private readonly MpvAudioDiagnosticTracker audioDiagnosticTracker = new();
    private DateTime nextAudioDiagnosticSample;
    private PlaybackSourceKind audioDiagnosticSource;
    private int audioDiagnosticLoad;
    private PlaybackAttemptRoute diagnosticPlaybackRoute;
    private bool diagnosticAccount;
    internal int DiagnosticRendererId => audioDiagnosticId;
    internal int DiagnosticLoad => audioDiagnosticLoad;
    private bool audioDiagnosticWarningPending;
    private bool audioDiagnosticsEnabled;

    private void ResetAudioDiagnostics(string url, bool youtubePlayback = false, bool paused = false)
    {
        audioDiagnosticTracker.Reset(); nextAudioDiagnosticSample = default;
        youtubeAccessRecovery.Reset(); nextYouTubeRecoverySample = default;
        Volatile.Write(ref videoFrameLoad, 0);
        Volatile.Write(ref mediaLoadedDiagnosticLoad, 0);
        Volatile.Write(ref youtubeRecoveryResolving, false);
        audioDiagnosticWarningPending = false; audioDiagnosticLoad++;
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
        audioDiagnosticSource = host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase) || host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase)
            ? PlaybackSourceKind.Bilibili : VideoUrlResolver.IsYouTubeUrl(url) ? PlaybackSourceKind.YouTube :
            _diagnoseLivePlayback ? PlaybackSourceKind.DirectLive : PlaybackSourceKind.Other;
        if (youtubePlayback) audioDiagnosticSource = PlaybackSourceKind.YouTube;
        if (audioDiagnosticSource == PlaybackSourceKind.YouTube) youtubeAccessRecovery.Start(DateTime.UtcNow, paused);
    }

    private double? DiagnosticNumber(string name, double min, double max)
    {
        var value = ReadPropertyString(name);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= min && n <= max ? n : null;
    }
    private bool? DiagnosticFlag(string name) => ReadPropertyString(name) switch { "yes" => true, "no" => false, _ => null };

    private void ReportAudioDiagnostics()
    {
        // Only the existing MPV event worker samples properties. Never wait on
        // native commands from the game/render thread, or log a raw property.
        if (!_mpvLock.TryEnter()) return;
        MpvAudioDiagnostic? diagnostic = null;
        try
        {
            var now = DateTime.UtcNow;
            if (_closed || !audioDiagnosticsEnabled || _mpvCtx == IntPtr.Zero || now < nextAudioDiagnosticSample) return;
            nextAudioDiagnosticSample = now.AddSeconds(5);
            if (DiagnosticFlag("idle-active") != false) return;
            var codec = ReadPropertyString("current-tracks/audio/codec")?.ToLowerInvariant();
            var codecKind = codec switch { null => AudioCodecKind.Unknown, "aac" => AudioCodecKind.Aac, "opus" => AudioCodecKind.Opus,
                "mp3" => AudioCodecKind.Mp3, "flac" => AudioCodecKind.Flac, "vorbis" => AudioCodecKind.Vorbis,
                _ => codec.StartsWith("pcm_") ? AudioCodecKind.Pcm : AudioCodecKind.Other };
            var audioTrack = ReadPropertyString("aid") == "no" ? 0 : (int?)DiagnosticNumber("current-tracks/audio/id", 0, 100000);
            var rawRms = ReadPropertyString("af-metadata/alphavol/by-key/lavfi.astats.Overall.RMS_level");
            var rms = double.TryParse(rawRms, NumberStyles.Float, CultureInfo.InvariantCulture, out var db) &&
                double.IsFinite(db) && db is >= -200 and <= 100 ? (double?)db : null;
            bool? silent = string.Equals(rawRms, "-inf", StringComparison.OrdinalIgnoreCase) ? true : rms is { } level ? level <= -90 : null;
            var state = new MpvAudioSnapshot(audioDiagnosticId, audioDiagnosticLoad, audioDiagnosticSource,
                audioTrack, codecKind, DiagnosticFlag("mute"), DiagnosticNumber("volume", 0, 1000),
                DiagnosticFlag("pause"), DiagnosticFlag("paused-for-cache"), DiagnosticFlag("seeking"), DiagnosticFlag("eof-reached"),
                DiagnosticNumber("time-pos", -60, 604800), DiagnosticNumber("audio-pts", -60, 604800),
                DiagnosticNumber("demuxer-cache-duration", 0, 604800),
                rms,
                (int?)DiagnosticNumber("audio-out-params/samplerate", 1, 1000000),
                (int?)DiagnosticNumber("audio-out-params/channel-count", 1, 128), silent);
            diagnostic = audioDiagnosticTracker.Observe(now, state, audioDiagnosticWarningPending);
            audioDiagnosticWarningPending = false;
        }
        finally { _mpvLock.Exit(); }
        if (diagnostic is not null) AepLog.PlaybackAudio(diagnostic);
    }
}

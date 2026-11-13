using System.Globalization;

namespace AlphaChannel.Plugin.Video;

internal enum PlaybackSourceKind { Other, YouTube, Bilibili, DirectLive }
internal enum AudioDiagnosticReason { Baseline, StateChanged, NativeWarning, AudioClockStalled }
internal enum AudioCodecKind { Unknown, Other, Aac, Opus, Mp3, Flac, Vorbis, Pcm }
internal sealed record MpvLiveDiagnostic(int Renderer, int Load, bool Encrypted, int BufferingSamples,
    double? Position, double? CacheSeconds, double? VideoPosition, double? AudioPosition, bool? Eof)
{
    internal string Format() => FormattableString.Invariant($"[MPV/Live] renderer={Renderer} load={Load} encrypted={Encrypted} bufferingSamples={BufferingSamples} position={Position:0.00} cacheSeconds={CacheSeconds:0.00} videoPosition={VideoPosition:0.00} audioPosition={AudioPosition:0.00} eof={Eof}");
}
internal enum MpvComponentKind { Unknown, AudioDecoder, VideoDecoder, AudioOutput, Demuxer, Network, Resolver }
internal enum MpvFailureKind { Other, AccessDenied, Timeout, ReadFailure, EndOfStream, InvalidData, DecodeFailure, Underrun, Timing }

// Deliberately no string fields: raw MPV values, URLs, paths, names and device
// descriptions cannot cross the production logging boundary.
internal sealed record MpvAudioSnapshot(int Renderer, int Load, PlaybackSourceKind Source,
    int? AudioTrack, AudioCodecKind Codec, bool? Muted, double? Volume, bool? Paused,
    bool? Buffering, bool? Seeking, bool? Eof, double? Position, double? AudioPosition,
    double? CacheSeconds, double? RmsDb, int? SampleRate, int? Channels, bool? RmsSilent = null);

internal sealed record MpvAudioDiagnostic(AudioDiagnosticReason Reason, MpvAudioSnapshot State)
{
    internal string Format()
    {
        var s = State;
        return $"[MPV/Audio] reason={Reason} renderer={s.Renderer} load={s.Load} source={s.Source} " +
            $"position={Number(s.Position)} audioPosition={Number(s.AudioPosition)} aid={s.AudioTrack?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} " +
            $"codec={s.Codec} mute={Flag(s.Muted)} volume={Number(s.Volume)} pause={Flag(s.Paused)} " +
            $"buffering={Flag(s.Buffering)} seeking={Flag(s.Seeking)} eof={Flag(s.Eof)} " +
            $"cacheSeconds={Number(s.CacheSeconds)} rmsDb={Number(s.RmsDb)} rmsSilent={Flag(s.RmsSilent)} " +
            $"sampleRate={s.SampleRate?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} channels={s.Channels?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";
    }
    private static string Flag(bool? value) => value is null ? "unknown" : value.Value ? "yes" : "no";
    private static string Number(double? value) => value is { } n && double.IsFinite(n) ? n.ToString("0.0", CultureInfo.InvariantCulture) : "unknown";
}

internal sealed record MpvWarningDiagnostic(int Renderer, PlaybackSourceKind Source, MpvWarningKind Kind,
    MpvComponentKind Component, MpvFailureKind Failure, int Load = 0, int? HttpStatus = null,
    PlaybackAccessReason Access = PlaybackAccessReason.Unknown)
{
    internal string Format() => $"[MPV] warning renderer={Renderer} source={Source} kind={Kind} component={Component} failure={Failure} load={Load} http={HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} access={Access}";

    internal static MpvWarningDiagnostic Classify(int renderer, PlaybackSourceKind source, string? prefix, string message, int load = 0)
    {
        var p = prefix?.ToLowerInvariant() ?? ""; var text = message.ToLowerInvariant();
        var component = p.StartsWith("ao") ? MpvComponentKind.AudioOutput :
            p.StartsWith("ad") || p.Contains("/audio") ? MpvComponentKind.AudioDecoder :
            p.StartsWith("vd") || p.Contains("/video") ? MpvComponentKind.VideoDecoder :
            p.Contains("ytdl") ? MpvComponentKind.Resolver :
            p.Contains("demux") ? MpvComponentKind.Demuxer :
            p.Contains("http") || p.Contains("stream") ? MpvComponentKind.Network : MpvComponentKind.Unknown;
        var failure = text.Contains("403") || text.Contains("401") || text.Contains("forbidden") ? MpvFailureKind.AccessDenied :
            text.Contains("timeout") || text.Contains("timed out") ? MpvFailureKind.Timeout :
            text.Contains("underrun") ? MpvFailureKind.Underrun :
            text.Contains("end of file") || text.Contains("end of stream") || text.Contains("eof") ? MpvFailureKind.EndOfStream :
            text.Contains("invalid data") || text.Contains("corrupt") ? MpvFailureKind.InvalidData :
            text.Contains("decode") || text.Contains("decoding") ? MpvFailureKind.DecodeFailure :
            text.Contains("read error") || text.Contains("i/o error") || text.Contains("failed to read") ? MpvFailureKind.ReadFailure :
            text.Contains("timestamp") || text.Contains("desync") ? MpvFailureKind.Timing : MpvFailureKind.Other;
        return new(renderer, source, MpvWarningClassifier.Classify(prefix, message), component, failure,
            load, PlaybackAccessDiagnostic.HttpStatus(message), PlaybackAccessDiagnostic.Reason(message));
    }
}

internal sealed class MpvAudioDiagnosticTracker
{
    private MpvAudioSnapshot? previous;
    private DateTime nextLog, lastSample;
    private int stationarySamples;
    internal void Reset() { previous = null; nextLog = lastSample = default; stationarySamples = 0; }

    internal MpvAudioDiagnostic? Observe(DateTime now, MpvAudioSnapshot s, bool nativeWarning)
    {
        var old = previous;
        var gap = now - lastSample;
        var moving = old is not null && gap >= TimeSpan.FromSeconds(3) && gap <= TimeSpan.FromSeconds(10) &&
            s.Position is { } pos && old.Position is { } before && pos - before >= 2 && pos - before <= gap.TotalSeconds * 2 &&
            s.Paused == false && s.Buffering == false && s.Seeking == false && s.Eof == false && s.AudioTrack is > 0;
        var audioStopped = moving && (old!.AudioPosition is { } audioBefore
            ? s.AudioPosition is null || Math.Abs(s.AudioPosition.Value - audioBefore) < .05
            : s.AudioPosition is null && stationarySamples > 0);
        stationarySamples = audioStopped ? stationarySamples + 1 : 0;
        var changed = old is not null && (s.AudioTrack != old.AudioTrack || s.Codec != old.Codec || s.Muted != old.Muted ||
            s.Volume != old.Volume || s.Paused != old.Paused || s.Buffering != old.Buffering || s.SampleRate != old.SampleRate || s.Channels != old.Channels);
        previous = s; lastSample = now;
        var reason = stationarySamples == 3 ? AudioDiagnosticReason.AudioClockStalled : nativeWarning ? AudioDiagnosticReason.NativeWarning :
            changed ? AudioDiagnosticReason.StateChanged : AudioDiagnosticReason.Baseline;
        if (old is not null && !changed && !nativeWarning && stationarySamples != 3 && now < nextLog) return null;
        nextLog = now.AddMinutes(1);
        return new(reason, s);
    }
}

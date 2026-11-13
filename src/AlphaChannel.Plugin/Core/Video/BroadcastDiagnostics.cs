using System.Diagnostics;
using System.Globalization;

namespace AlphaChannel.Plugin.Video;

internal enum BroadcastHealth : byte
{
    Idle,
    Starting,
    Healthy,
    DroppingFrames,
    AudioBacklog,
    EncoderBehind,
    Failed,
}

internal enum BroadcastEncoderFailure : byte
{
    None, Certificate, TlsHandshake, UploadRejected, ConnectionRefused, ConnectionLost, Timeout, EncoderUnavailable, InvalidInput
}

[Flags]
internal enum BroadcastEncoderErrorDetails
{
    None = 0, InputOpen = 1, VideoInput = 2, AudioInput = 4, InvalidArgument = 8,
    InvalidData = 16, MissingFile = 32, PermissionDenied = 64, OutputOpen = 128,
    HeaderRead = 256, CodecParameters = 512, PixelFormat = 1024, FrameSize = 2048,
    FrameRate = 4096, Tls = 8192, IoError = 16384
}

internal static class BroadcastEncoderErrors
{
    internal static bool IsFailure(BroadcastEncoderFailure failure, BroadcastEncoderErrorDetails details, string line)
    {
        // Input names, sample formats, pixel formats and frame rates also appear
        // in ordinary FFmpeg output. They provide context, not evidence of failure.
        const BroadcastEncoderErrorDetails errors = BroadcastEncoderErrorDetails.InputOpen |
            BroadcastEncoderErrorDetails.InvalidArgument | BroadcastEncoderErrorDetails.InvalidData |
            BroadcastEncoderErrorDetails.MissingFile | BroadcastEncoderErrorDetails.PermissionDenied |
            BroadcastEncoderErrorDetails.OutputOpen | BroadcastEncoderErrorDetails.HeaderRead |
            BroadcastEncoderErrorDetails.CodecParameters |
            BroadcastEncoderErrorDetails.IoError;
        if (failure != BroadcastEncoderFailure.None || (details & errors) != 0) return true;
        // Preserve explicit failures even when the safe classifier does not yet
        // have a specific category for this FFmpeg diagnostic. Never log the raw line.
        var text = line.ToLowerInvariant();
        return text.Contains("error ") || text.Contains("error:") || text.Contains("failed") ||
            text.Contains("invalid ") || text.Contains("cannot ") || text.Contains("could not ") || text.Contains("unable to ");
    }

    internal static BroadcastEncoderErrorDetails Details(string line)
    {
        var text = line.ToLowerInvariant();
        var details = BroadcastEncoderErrorDetails.None;
        void Mark(bool match, BroadcastEncoderErrorDetails value) { if (match) details |= value; }
        Mark(text.Contains("error opening input"), BroadcastEncoderErrorDetails.InputOpen);
        Mark(text.Contains("pipe:0") || text.Contains("in#0") || text.Contains("rawvideo"), BroadcastEncoderErrorDetails.VideoInput);
        Mark(text.Contains("alphachannelaudio_") || text.Contains("in#1") || text.Contains("pcm_s16le") || text.Contains("s16le"), BroadcastEncoderErrorDetails.AudioInput);
        Mark(text.Contains("invalid argument"), BroadcastEncoderErrorDetails.InvalidArgument);
        Mark(text.Contains("invalid data"), BroadcastEncoderErrorDetails.InvalidData);
        Mark(text.Contains("no such file") || text.Contains("file not found") || text.Contains("cannot find the file"), BroadcastEncoderErrorDetails.MissingFile);
        Mark(text.Contains("permission denied") || text.Contains("access is denied"), BroadcastEncoderErrorDetails.PermissionDenied);
        Mark(text.Contains("error opening output") || text.Contains("could not write header"), BroadcastEncoderErrorDetails.OutputOpen);
        Mark(text.Contains("error reading header"), BroadcastEncoderErrorDetails.HeaderRead);
        Mark(text.Contains("could not find codec parameters"), BroadcastEncoderErrorDetails.CodecParameters);
        Mark(text.Contains("pixel format"), BroadcastEncoderErrorDetails.PixelFormat);
        Mark(text.Contains("invalid buffer size") || text.Contains("packet size") || text.Contains("picture size") || text.Contains("video_size"), BroadcastEncoderErrorDetails.FrameSize);
        Mark(text.Contains("frame rate") || text.Contains("framerate"), BroadcastEncoderErrorDetails.FrameRate);
        Mark(text.Contains("tls") || text.Contains("ssl") || text.Contains("certificate"), BroadcastEncoderErrorDetails.Tls);
        Mark(text.Contains("input/output error"), BroadcastEncoderErrorDetails.IoError);
        return details;
    }
    internal static BroadcastEncoderFailure Classify(string line)
    {
        var text = line.ToLowerInvariant();
        if (text.Contains("certificate verify failed") || text.Contains("certificate verification failed") || text.Contains("unable to get local issuer") || text.Contains("error setting certificate")) return BroadcastEncoderFailure.Certificate;
        if (text.Contains("tls handshake") || text.Contains("ssl handshake") || text.Contains("handshake failed") || text.Contains("tls connection was non-properly terminated")) return BroadcastEncoderFailure.TlsHandshake;
        if (text.Contains("server error: authentication") || text.Contains("authentication failed") || text.Contains("403 forbidden") || text.Contains("401 unauthorized")) return BroadcastEncoderFailure.UploadRejected;
        if (text.Contains("connection refused")) return BroadcastEncoderFailure.ConnectionRefused;
        if (text.Contains("connection reset") || text.Contains("broken pipe") || text.Contains("error writing trailer") || text.Contains("error muxing a packet")) return BroadcastEncoderFailure.ConnectionLost;
        if (text.Contains("timed out") || text.Contains("connection timeout")) return BroadcastEncoderFailure.Timeout;
        if (text.Contains("cannot load nvcuda") || text.Contains("no capable devices") || text.Contains("error while opening encoder") || text.Contains("unknown encoder") || text.Contains("cannot load libcuda")) return BroadcastEncoderFailure.EncoderUnavailable;
        if (text.Contains("invalid data found when processing input") || text.Contains("error opening input") ||
            text.Contains("invalid buffer size") || text.Contains("invalid packet size") || text.Contains("invalid picture size"))
            return BroadcastEncoderFailure.InvalidInput;
        return BroadcastEncoderFailure.None;
    }
}

internal sealed record BroadcastDiagnosticsSnapshot(
    bool Active,
    string Source,
    BroadcastHealth Health,
    int Width,
    int Height,
    double TargetFps,
    double CaptureFps,
    double EncodeFps,
    double Speed,
    double BitrateKbps,
    long BytesUploaded,
    int VideoQueueDepth,
    int AudioQueueDepth,
    long DroppedFrames,
    long DroppedAudioPackets,
    TimeSpan Duration,
    int? ExitCode,
    string? LastError,
    DateTime UpdatedUtc,
    BroadcastEncoderFailure EncoderFailure = BroadcastEncoderFailure.None,
    BroadcastEncoderErrorDetails EncoderErrorDetails = BroadcastEncoderErrorDetails.None)
{
    internal static readonly BroadcastDiagnosticsSnapshot Idle = new(
        false, "No active broadcast", BroadcastHealth.Idle, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, TimeSpan.Zero, null, null, DateTime.UtcNow);

    internal string ToSanitizedSummary()
    {
        var state = Health.ToString();
        var resolution = Width > 0 && Height > 0 ? $"{Width}x{Height}" : "unknown";
        return string.Join(Environment.NewLine,
            "Alpha Channel broadcast diagnostics",
            $"Source: {Source}",
            $"State: {state}",
            $"Encoder failure category: {EncoderFailure}",
            $"Encoder error details: {EncoderErrorDetails}",
            $"Resolution: {resolution}",
            $"Target / captured / encoded FPS: {TargetFps:0.##} / {CaptureFps:0.##} / {EncodeFps:0.##}",
            $"Encoder speed: {Speed:0.##}x",
            $"Bitrate: {BitrateKbps:0} kbps",
            $"Uploaded: {FormatBytes(BytesUploaded)}",
            $"Queues (video/audio): {VideoQueueDepth} / {AudioQueueDepth}",
            $"Discarded (frames/audio packets): {DroppedFrames} / {DroppedAudioPackets}",
            $"Duration: {Duration:hh\\:mm\\:ss}",
            $"Exit code: {(ExitCode.HasValue ? ExitCode.Value.ToString(CultureInfo.InvariantCulture) : "n/a")}",
            $"Last error: {LastError ?? "none"}");
    }

    internal static string FormatBytes(long value)
    {
        if (value < 1024) return $"{value} B";
        if (value < 1024L * 1024) return $"{value / 1024d:0.0} KiB";
        if (value < 1024L * 1024 * 1024) return $"{value / (1024d * 1024d):0.0} MiB";
        return $"{value / (1024d * 1024d * 1024d):0.00} GiB";
    }
}

internal sealed class BroadcastDiagnosticsTracker
{
    private readonly object gate = new();
    private readonly Stopwatch clock = new();
    private string source = "Broadcast";
    private int width;
    private int height;
    private double targetFps;
    private long capturedFrames;
    private long lastCapturedFrames;
    private long lastCaptureTicks;
    private double captureFps;
    private double encodeFps;
    private double speed;
    private double bitrateKbps;
    private long bytesUploaded;
    private int videoQueueDepth;
    private int audioQueueDepth;
    private long droppedFrames;
    private long droppedAudioPackets;
    private DateTime lastVideoDropUtc;
    private DateTime lastAudioDropUtc;
    private TimeSpan outputDuration;
    private int? exitCode;
    private string? lastError;
    private bool active;
    private BroadcastEncoderFailure encoderFailure;
    private BroadcastEncoderErrorDetails encoderErrorDetails;
    private DateTime updatedUtc = DateTime.UtcNow;

    internal void Start(string sourceName, int frameWidth = 0, int frameHeight = 0, double fps = 0)
    {
        lock (gate)
        {
            source = string.IsNullOrWhiteSpace(sourceName) ? "Broadcast" : sourceName;
            width = frameWidth;
            height = frameHeight;
            targetFps = fps;
            capturedFrames = lastCapturedFrames = 0;
            lastCaptureTicks = 0;
            captureFps = encodeFps = speed = bitrateKbps = 0;
            bytesUploaded = droppedFrames = droppedAudioPackets = 0;
            lastVideoDropUtc = lastAudioDropUtc = DateTime.MinValue;
            videoQueueDepth = audioQueueDepth = 0;
            outputDuration = TimeSpan.Zero;
            exitCode = null;
            lastError = null;
            encoderFailure = BroadcastEncoderFailure.None;
            encoderErrorDetails = BroadcastEncoderErrorDetails.None;
            active = true;
            updatedUtc = DateTime.UtcNow;
            clock.Restart();
        }
    }

    internal void Stop(int? code = null)
    {
        lock (gate)
        {
            active = false;
            exitCode = code ?? exitCode;
            updatedUtc = DateTime.UtcNow;
            clock.Stop();
        }
    }

    internal void Failed(string message, int? code = null)
    {
        lock (gate)
        {
            active = false;
            exitCode = code ?? exitCode;
            lastError = string.IsNullOrWhiteSpace(message) ? "FFmpeg stopped unexpectedly." : message;
            updatedUtc = DateTime.UtcNow;
            clock.Stop();
        }
    }

    internal void CapturedFrame(bool replacedPendingFrame)
    {
        lock (gate)
        {
            capturedFrames++;
            if (replacedPendingFrame)
            {
                droppedFrames++;
                lastVideoDropUtc = DateTime.UtcNow;
            }
            videoQueueDepth = 1;
            var ticks = clock.ElapsedTicks;
            if (lastCaptureTicks == 0) lastCaptureTicks = ticks;
            var elapsed = (ticks - lastCaptureTicks) / (double)Stopwatch.Frequency;
            if (elapsed >= 1d)
            {
                captureFps = (capturedFrames - lastCapturedFrames) / elapsed;
                lastCapturedFrames = capturedFrames;
                lastCaptureTicks = ticks;
            }
            updatedUtc = DateTime.UtcNow;
        }
    }

    internal void SetQueues(int video, int audio)
    {
        lock (gate)
        {
            videoQueueDepth = Math.Max(0, video);
            audioQueueDepth = Math.Max(0, audio);
            updatedUtc = DateTime.UtcNow;
        }
    }

    internal void DroppedAudio(long count)
    {
        if (count <= 0) return;
        lock (gate)
        {
            droppedAudioPackets += count;
            lastAudioDropUtc = DateTime.UtcNow;
        }
    }

    internal BroadcastEncoderFailure ObserveEncoderError(string line)
    {
        var failure = BroadcastEncoderErrors.Classify(line);
        var details = BroadcastEncoderErrors.Details(line);
        if (BroadcastEncoderErrors.IsFailure(failure, details, line))
        {
            lock (gate)
            {
                // Keep a specific root error instead of replacing it with
                // the broken-pipe/muxer errors that often follow it.
                if (encoderFailure == BroadcastEncoderFailure.None) encoderFailure = failure;
                encoderErrorDetails |= details;
            }
        }
        return failure;
    }

    internal bool TryConsumeProgress(string line)
    {
        var separator = line.IndexOf('=');
        if (separator <= 0) return false;
        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();
        lock (gate)
        {
            switch (key)
            {
                case "fps":
                    double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out encodeFps);
                    break;
                case "speed":
                    double.TryParse(value.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out speed);
                    break;
                case "bitrate":
                    double.TryParse(value.Replace("kbits/s", "", StringComparison.OrdinalIgnoreCase).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out bitrateKbps);
                    break;
                case "total_size":
                    long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out bytesUploaded);
                    break;
                case "out_time_us":
                case "out_time_ms":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
                        outputDuration = TimeSpan.FromTicks(microseconds * 10);
                    break;
                case "drop_frames":
                    if (long.TryParse(value, out var encoderDrops) && encoderDrops > droppedFrames)
                    {
                        droppedFrames = encoderDrops;
                        lastVideoDropUtc = DateTime.UtcNow;
                    }
                    break;
                case "progress":
                case "frame":
                case "dup_frames":
                case "out_time":
                case "packet":
                    break;
                default:
                    if (!key.StartsWith("stream_", StringComparison.Ordinal)) return false;
                    break;
            }
            updatedUtc = DateTime.UtcNow;

            return true;
        }
    }

    internal BroadcastDiagnosticsSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                var health = BroadcastHealth.Idle;
                if (!active && lastError is not null) health = BroadcastHealth.Failed;
                else if (active && clock.Elapsed < TimeSpan.FromSeconds(3)) health = BroadcastHealth.Starting;
                else if (active && DateTime.UtcNow - lastAudioDropUtc < TimeSpan.FromSeconds(10)) health = BroadcastHealth.AudioBacklog;
                else if (active && DateTime.UtcNow - lastVideoDropUtc < TimeSpan.FromSeconds(10)) health = BroadcastHealth.DroppingFrames;
                else if (active && speed > 0 && speed < .95) health = BroadcastHealth.EncoderBehind;
                else if (active) health = BroadcastHealth.Healthy;

                return new BroadcastDiagnosticsSnapshot(active, source, health, width, height, targetFps,
                    captureFps, encodeFps, speed, bitrateKbps, bytesUploaded, videoQueueDepth,
                    audioQueueDepth, droppedFrames, droppedAudioPackets,
                    outputDuration > TimeSpan.Zero ? outputDuration : clock.Elapsed,
                    exitCode, lastError, updatedUtc, encoderFailure, encoderErrorDetails);
            }
        }
    }
}

using System.Diagnostics;

namespace AlphaChannel.Plugin.Video;

// One summary after 5 seconds, then at most one every 30 seconds while the TV
// is drawing. Timings contain no media URLs and never log individual frames.
internal static class PlaybackRenderDiagnostics
{
    private struct Timing
    {
        internal int Count;
        internal double Total, Max;
        internal void Add(double ms) { Count++; Total += ms; Max = Math.Max(Max, ms); }
        internal readonly string Summary => Count == 0 ? "n/a" : $"{Total / Count:F2}/{Max:F2}";
    }
    private static readonly object gate = new();
    private static Timing native, uploadWait, upload, paint, callback, present, arcade;
    private static string? arcadeActivity;
    private static long lastTvDraw, windowStarted;
    private static bool firstReport = true;
    private static long focusRecoveryUntil;
    private static volatile bool monitoring;
    private static readonly Queue<string> liveSamples = new();
    internal static void RecordLivePlayback(string summary)
    {
        lock (gate)
        {
            liveSamples.Enqueue($"{DateTime.UtcNow:HH:mm:ss} UTC {summary}");
            while (liveSamples.Count > 18) liveSamples.Dequeue();
        }
    }
    internal static string LivePlaybackReport()
    {
        lock (gate) return "Recent broadcast playback samples (10-second intervals):" + Environment.NewLine +
            (liveSamples.Count == 0 ? "No samples yet; view a broadcast for at least 10 seconds." : string.Join(Environment.NewLine, liveSamples));
    }

    // Keep reporting briefly after foreground return even if the user stops
    // playback/despawns the TV, so a persistent presentation stall stays visible.
    internal static void BeginFocusRecovery()
    {
        lock (gate) { monitoring = true; focusRecoveryUntil = Environment.TickCount64 + 180_000; }
    }

    internal static void RecordNativeRender(double ms) { lock (gate) native.Add(ms); }
    internal static void RecordArcadeRender(string activity, double ms)
    {
        lock (gate) { arcadeActivity = activity; arcade.Add(ms); }
    }
    internal static void RecordUpload(double waitMs, double uploadMs)
    {
        lock (gate) { uploadWait.Add(waitMs); upload.Add(uploadMs); }
    }
    internal static void RecordTvDraw(double ms)
    {
        lock (gate) { monitoring = true; lastTvDraw = Stopwatch.GetTimestamp(); paint.Add(ms); }
    }
    internal static void RecordPresent(double callbackMs, double presentMs)
    {
        if (!monitoring) return;
        string? summary = null;
        lock (gate)
        {
            var now = Stopwatch.GetTimestamp();
            var monitoringRecovery = Environment.TickCount64 < focusRecoveryUntil;
            if (lastTvDraw == 0 && !monitoringRecovery) return;
            if (!monitoringRecovery && Stopwatch.GetElapsedTime(lastTvDraw, now).TotalSeconds > 1)
            {
                ClearWindow();
                lastTvDraw = 0; monitoring = false;
                return;
            }
            if (windowStarted == 0) windowStarted = now;
            callback.Add(callbackMs);
            present.Add(presentMs);
            var seconds = Stopwatch.GetElapsedTime(windowStarted, now).TotalSeconds;
            if (seconds < (firstReport ? 5 : 30)) return;
            summary = $"[TV Performance] fps={present.Count / seconds:F1}; avg/max ms: " +
                $"native-render={native.Summary}, upload-lock={uploadWait.Summary}, upload={upload.Summary}, " +
                $"TV-draw={paint.Summary}, plugin-present={callback.Summary}, driver-present={present.Summary}; " +
                $"arcade={arcadeActivity ?? "none"}, arcade-cpu={arcade.Summary}, arcade-frames={arcade.Count}; " +
                $"focus-recovery={monitoringRecovery}";
            firstReport = false;
            ClearWindow();
        }
        
    }

    private static void ClearWindow()
    {
        native = uploadWait = upload = paint = callback = present = arcade = default;
        arcadeActivity = null;
        windowStarted = 0;
    }
    internal static void Reset()
    {
        lock (gate) { ClearWindow(); lastTvDraw = 0; firstReport = true; focusRecoveryUntil = 0; monitoring = false; }
    }
}

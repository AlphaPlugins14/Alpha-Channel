namespace AlphaChannel.Plugin.Video;

internal enum VisualizerQuality { Full, Reduced, Minimal }

internal sealed class VisualizerPerformancePolicy
{
    private double warmupUntil;
    private int slowWindows;

    internal void Reset(double now)
    {
        warmupUntil = now + 10;
        slowWindows = 0;
    }

    // Called once per five-second window. Audio progress is required so pauses,
    // buffering, loading and seeks do not count as struggling video processing.
    internal bool ShouldReduce(double now, bool audioAdvancing, double deliveredFps,
        double targetFps)
    {
        if (now < warmupUntil || !audioAdvancing || targetFps <= 0)
        {
            slowWindows = 0;
            return false;
        }
        // Native render timing can include waiting for the next frame. It is not
        // a reliable measure of CPU load: healthy 20 FPS playback can report
        // almost 50 ms per render. Require an actual sustained delivery deficit.
        var slow = deliveredFps < targetFps * .65;
        slowWindows = slow ? slowWindows + 1 : 0;
        if (slowWindows < 3) return false;
        Reset(now);
        return true;
    }
}

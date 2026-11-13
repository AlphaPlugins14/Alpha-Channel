using System.Diagnostics;

namespace AlphaChannel.Plugin.Video;

internal enum MediaLifecycleStage
{
    StudioPreparation, StudioConnection, StudioCleanup,
    EncoderPreparation, EncoderCleanup, BrowserBroadcastCleanup, BrowserHelperCleanup
}

// Fixed stages and elapsed time only: never include paths, commands or stream secrets.
internal readonly struct MediaLifecycleTiming(MediaLifecycleStage stage) : IDisposable
{
    private readonly long started = Stopwatch.GetTimestamp();
    public void Dispose()
    {
        var milliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (milliseconds >= 500)
            AepLog.Warning($"[Media/Timing] stage={stage} elapsedMs={milliseconds}.");
    }
}

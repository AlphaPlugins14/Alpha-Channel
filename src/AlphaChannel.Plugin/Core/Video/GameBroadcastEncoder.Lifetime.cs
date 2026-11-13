namespace AlphaChannel.Plugin.Video;

// Capture callbacks see only the current session. Retired processes, queues and
// workers remain owned by their old session until background cleanup completes.
internal sealed class GameBroadcastEncoder(Task? preparationBarrier = null) : IDisposable
{
    private sealed class Session
    {
        internal readonly GameBroadcastEncoderSession Encoder = new();
        internal Task Launch = Task.CompletedTask;
        internal volatile bool Pending = true;
        internal volatile bool Cancelled;
    }

    // A replacement publisher must not race a retiring publisher to the relay.
    // This gate protects task registration only; no I/O or waiting happens under it.
    private static readonly object cleanupGate = new();
    private static Task pendingCleanup = Task.CompletedTask;
    private readonly object gate = new();
    private Session? current;
    private bool disposed;
    private Task cleanup = Task.CompletedTask;
    internal Task CleanupCompletion { get { lock (gate) return cleanup; } }
    internal bool IsRunning => Volatile.Read(ref current) is { Cancelled: false } session &&
        (session.Pending || session.Encoder.IsRunning);
    internal long VideoTimelineStartTick => Volatile.Read(ref current)?.Encoder.VideoTimelineStartTick ?? 0;
    internal BroadcastDiagnosticsSnapshot Diagnostics =>
        Volatile.Read(ref current)?.Encoder.Diagnostics ?? BroadcastDiagnosticsSnapshot.Idle;

    internal bool Start(string ffmpegPath, string publishUrl, int width, int height, double fps,
        int audioSampleRate, int? outputWidth = null, int? outputHeight = null,
        int maxPendingAudioPackets = 256, int ffmpegAudioQueuePackets = 0,
        string sourceName = "Retro game", double? outputFrameRate = null,
        int? videoBitrateKbps = null, int audioBitrateKbps = 128, bool inputIsBgra = false,
        int? encoderThreads = null)
    {
        if (!File.Exists(ffmpegPath) || string.IsNullOrWhiteSpace(publishUrl) ||
            width <= 0 || height <= 0 || fps <= 1 || audioSampleRate <= 0) return false;
        lock (gate)
        {
            if (disposed) return false;
            if (IsRunning) return true;
            RetireCurrent();
            var session = new Session();
            Task previous;
            lock (cleanupGate) previous = pendingCleanup;
            current = session;
            session.Launch = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    if (preparationBarrier is not null) await preparationBarrier.ConfigureAwait(false);
                    if (session.Cancelled) return;
                    session.Encoder.Start(ffmpegPath, publishUrl, width, height, fps, audioSampleRate,
                        outputWidth, outputHeight, maxPendingAudioPackets, ffmpegAudioQueuePackets,
                        sourceName, outputFrameRate, videoBitrateKbps, audioBitrateKbps, inputIsBgra, encoderThreads);
                }
                catch (Exception) { AepLog.Warning("[GAME-BROADCAST] Background preparation failed."); }
                finally { session.Pending = false; }
            });
            return true;
        }
    }

    // Called with gate held. This only detaches ownership and schedules work.
    private void RetireCurrent()
    {
        var session = current;
        if (session is null) return;
        current = null;
        session.Cancelled = true;
        lock (cleanupGate)
        {
            var previous = pendingCleanup;
            cleanup = pendingCleanup = Task.Run(async () =>
            {
                // Even if an earlier cleanup failed, this session still owns
                // resources that must be released. Keep the failed barrier so
                // a later publisher cannot overlap an uncontained process.
                var previousFailed = false;
                try { await previous.ConfigureAwait(false); }
                catch (Exception) { previousFailed = true; }
                await session.Launch.ConfigureAwait(false);
                using var timing = new MediaLifecycleTiming(MediaLifecycleStage.EncoderCleanup);
                session.Encoder.Dispose();
                if (previousFailed || !session.Encoder.CleanupSucceeded)
                    throw new InvalidOperationException("Encoder cleanup did not complete safely.");
            });
            _ = cleanup.ContinueWith(failed =>
            {
                _ = failed.Exception;
                AepLog.Warning("[GAME-BROADCAST] Background cleanup failed; replacement publishing is blocked.");
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    internal void Stop() { lock (gate) RetireCurrent(); }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            RetireCurrent();
        }
    }
    internal void SubmitAudio(IntPtr data, int frames) => Volatile.Read(ref current)?.Encoder.SubmitAudio(data, frames);
    internal void SubmitVideoFrame(IntPtr data, int width, int height, int pitch) =>
        Volatile.Read(ref current)?.Encoder.SubmitVideoFrame(data, width, height, pitch);
    internal void SubmitBgraVideoFrame(IntPtr data, int width, int height, int pitch, long? captureTimestamp = null) =>
        Volatile.Read(ref current)?.Encoder.SubmitBgraVideoFrame(data, width, height, pitch, captureTimestamp);
    internal void DiscardPendingAudio() => Volatile.Read(ref current)?.Encoder.DiscardPendingAudio();
    internal void MaskPendingBrowserFrames(byte[] frame) => Volatile.Read(ref current)?.Encoder.MaskPendingBrowserFrames(frame);
    internal static Task WriteAudioWithGapAsync(Stream output, byte[] audio, long gapFrames, CancellationToken cancellation) =>
        GameBroadcastEncoderSession.WriteAudioWithGapAsync(output, audio, gapFrames, cancellation);
}

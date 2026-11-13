namespace AlphaChannel.Plugin;

// Finish any started chunked snapshot so receivers do not retain partial
// assemblies. Queued snapshots collapse to the latest complete batch.
internal sealed class LatestRealtimeBatch
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private long version;
    internal async Task RunAsync(Func<Task> send, CancellationToken token)
    {
        var ticket = Interlocked.Increment(ref version);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { if (ticket == Interlocked.Read(ref version)) { token.ThrowIfCancellationRequested(); await send().ConfigureAwait(false); } }
        finally { gate.Release(); }
    }
}

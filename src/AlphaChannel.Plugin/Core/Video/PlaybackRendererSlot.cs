namespace AlphaChannel.Plugin.Video;

// Startup is serialized off the game thread. Native initialization remains
// private; publication/detachment take only a short managed lock.
internal sealed class PlaybackRendererSlot<T> where T : class
{
    private readonly SemaphoreSlim startup = new(1, 1);
    private readonly object publication = new();
    private T? current;
    internal T? Current => Volatile.Read(ref current);

    internal async Task<IDisposable> EnterAsync(CancellationToken token)
    {
        await startup.WaitAsync(token).ConfigureAwait(false);
        return new Lease(startup);
    }

    internal bool TryPublish(T initialized, Func<bool> requestIsCurrent)
    {
        lock (publication)
        {
            if (!requestIsCurrent()) return false;
            current = initialized;
            return true;
        }
    }

    internal T? Detach()
    {
        lock (publication)
        {
            var renderer = current;
            current = null;
            return renderer;
        }
    }

    internal void Clear(T renderer)
    {
        lock (publication)
            if (ReferenceEquals(current, renderer)) current = null;
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) gate.Release(); }
    }
}

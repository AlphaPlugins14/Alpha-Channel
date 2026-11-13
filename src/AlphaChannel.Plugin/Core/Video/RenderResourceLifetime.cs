namespace AlphaChannel.Plugin.Video;

// Queue cancellation cannot revoke a callback already picked up by Present.
// Serialize native-resource access and teardown, including those callbacks.
internal sealed class RenderResourceLifetime
{
    internal object SyncRoot { get; } = new();
    internal bool IsDisposed { get; private set; }

    internal void Use(Action work)
    {
        lock (SyncRoot)
        {
            if (!IsDisposed) work();
        }
    }

    internal void Dispose(Action release)
    {
        lock (SyncRoot)
        {
            if (IsDisposed) return;
            IsDisposed = true;
            release();
        }
    }
}

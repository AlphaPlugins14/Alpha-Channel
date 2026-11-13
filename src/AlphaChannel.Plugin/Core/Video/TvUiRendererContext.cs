using SharpDX.Direct3D11;

namespace AlphaChannel.Plugin.Video;

// Present and executed draws may use different threads. Serialize our complete
// context batches, and use the runtime's lock so game API calls cannot enter
// midway through a TV draw. Thread IDs are diagnostics, not eligibility rules.
internal sealed class TvUiRendererContext : IDisposable
{
    private readonly object gate = new();
    private Multithread? multithread;
    private bool active, previous, restorePending;
    private int depth;
    private bool nativeHeld;
    private string status = "Context protection inactive";
    internal string Status => Volatile.Read(ref status);

    internal Scope Enter()
    {
        // A game draw can arrive while the game already owns the runtime lock.
        // Take that lock before our monitor, matching the caller's lock order.
        var native = Volatile.Read(ref multithread);
        native?.Enter();
        try
        {
            Monitor.Enter(gate);
            depth++;
            return new Scope(this, native);
        }
        catch { native?.Leave(); throw; }
    }

    internal void Enable(Device device)
    {
        lock (gate)
        {
            if (active) return;
            if (restorePending)
            {
                restorePending = false; active = true;
                Volatile.Write(ref status, $"Context batches serialized; D3D multithread protection on (previously {previous})");
                return;
            }
            if ((device.CreationFlags & DeviceCreationFlags.SingleThreaded) != 0)
                throw new NotSupportedException("The graphics device was created single-threaded.");
            multithread ??= device.ImmediateContext.QueryInterface<Multithread>();
            previous = multithread.GetMultithreadProtected();
            multithread.SetMultithreadProtected(true);
            if (!multithread.GetMultithreadProtected())
                throw new NotSupportedException("The graphics runtime did not enable context protection.");
            active = true;
            // Enabling is queued inside an existing Present scope. Upgrade that
            // scope too, so the first enabled frame has an atomic context batch.
            if (depth > 0 && !nativeHeld) { multithread.Enter(); nativeHeld = true; }
            Volatile.Write(ref status, $"Context batches serialized; D3D multithread protection on (previously {previous})");
        }
    }

    internal void Disable()
    {
        lock (gate)
        {
            if (!active) return;
            active = false;
            restorePending = true;
            if (depth == 0) RestoreProtection();
        }
    }

    public void Dispose()
    {
        lock (gate) { Disable(); multithread?.Dispose(); multithread = null; }
    }

    private void Leave(Multithread? native)
    {
        try
        {
            if (--depth == 0)
            {
                if (restorePending) RestoreProtection();
                if (nativeHeld) { multithread!.Leave(); nativeHeld = false; }
            }
        }
        finally
        {
            try { native?.Leave(); }
            finally { Monitor.Exit(gate); }
        }
    }

    private void RestoreProtection()
    {
        multithread!.SetMultithreadProtected(previous);
        restorePending = false;
        Volatile.Write(ref status, $"Test stopped; prior D3D multithread protection restored ({previous})");
    }

    internal readonly struct Scope(TvUiRendererContext owner, Multithread? native) : IDisposable
    {
        public void Dispose() => owner?.Leave(native);
    }
}

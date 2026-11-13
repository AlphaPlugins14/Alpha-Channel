namespace AlphaChannel.BroadcastStudio;

// Bounded on both sides of the pipe. Only consecutive settings may replace
// one another: play/stop/queue commands remain ordered barriers.
internal sealed class StudioCommandQueue : IDisposable
{
    private readonly object gate = new();
    private readonly LinkedList<StudioCommand> pending = new();
    private readonly SemaphoreSlim available = new(0, 1), space = new(0, 1);
    private bool disposed;
    internal bool TryWrite(StudioCommand command)
    {
        lock (gate)
        {
            if (disposed) return false;
            if (command.Kind == "settings" && pending.Last?.Value.Kind == "settings")
            { pending.Last.Value = command; return true; }
            if (pending.Count >= 64) return false;
            pending.AddLast(command);
            if (available.CurrentCount == 0) available.Release();
            return true;
        }
    }
    internal bool TryRead(out StudioCommand command)
    {
        lock (gate)
        {
            if (disposed) { command = null!; return false; }
            if (pending.First is not { } first) { command = null!; return false; }
            command = first.Value; pending.RemoveFirst();
            if (space.CurrentCount == 0) space.Release();
            return true;
        }
    }
    internal async ValueTask WriteAsync(StudioCommand command, CancellationToken token)
    {
        while (!TryWrite(command)) await space.WaitAsync(token).ConfigureAwait(false);
    }
    internal async ValueTask<StudioCommand> ReadAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (TryRead(out var command)) return command;
            await available.WaitAsync(token).ConfigureAwait(false);
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; pending.Clear(); available.Dispose(); space.Dispose();
        }
    }
}

namespace AlphaChannel.Plugin;

// Poll Dalamud's cached repository information; never download or install the
// plugin ourselves. A slow repository reload must not block the game/UI thread.
internal sealed class PluginUpdateMonitor : IDisposable
{
    private readonly Func<Task<Version?>> check;
    private readonly Action<Exception> reportFailure;
    private readonly CancellationTokenSource shutdown = new();
    private readonly object gate = new();
    private Task? pending;
    private DateTime nextCheckUtc;
    private Version? available;
    private bool disposed;

    internal PluginUpdateMonitor(Func<Task<Version?>> check, Action<Exception> reportFailure)
    {
        this.check = check;
        this.reportFailure = reportFailure;
    }

    internal Version? AvailableVersion { get { lock (gate) return available; } }

    internal void Tick(DateTime nowUtc)
    {
        lock (gate)
        {
            if (disposed || pending is { IsCompleted: false } || nowUtc < nextCheckUtc) return;
            nextCheckUtc = nowUtc.AddMinutes(1);
            pending = CheckAsync(shutdown.Token);
        }
    }

    private async Task CheckAsync(CancellationToken token)
    {
        try
        {
            var result = await check().WaitAsync(token).ConfigureAwait(false);
            lock (gate) if (!disposed) available = result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { reportFailure(exception); }
    }

    public void Dispose()
    {
        Task? worker;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            available = null;
            worker = pending;
        }
        shutdown.Cancel();
        if (worker is null || worker.IsCompleted) shutdown.Dispose();
        else _ = worker.ContinueWith(_ => shutdown.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}

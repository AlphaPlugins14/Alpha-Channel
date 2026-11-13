namespace AlphaChannel.Plugin;

// No work starts in the constructor. Explicit feature requests share one
// installation and serialize heavyweight extraction across different features.
internal sealed class OnDemandFeaturePreparation(CancellationToken cancellation, Func<DateTime>? clock = null)
{
    private readonly object gate = new();
    private readonly SemaphoreSlim serial = new(1);
    private readonly Dictionary<string,(Task Work,DateTime Retry)> requests = new();
    internal Task Request(string feature,Func<Task> install)
    {
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            var now = clock?.Invoke() ?? DateTime.UtcNow;
            if (requests.TryGetValue(feature,out var existing) &&
                (!existing.Work.IsCompleted || existing.Work.IsCompletedSuccessfully || now < existing.Retry)) return existing.Work;
            var task = Task.Run(async () =>
            {
                await serial.WaitAsync(cancellation).ConfigureAwait(false);
                try { cancellation.ThrowIfCancellationRequested(); await install().ConfigureAwait(false); }
                finally { serial.Release(); }
            },cancellation);
            requests[feature]=(task,now.AddMinutes(2)); return task;
        }
    }
    internal void Retry(string feature) { lock (gate) requests.Remove(feature); }
}

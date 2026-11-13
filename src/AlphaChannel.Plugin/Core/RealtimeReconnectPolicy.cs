namespace AlphaChannel.Plugin;

internal sealed class RealtimeReconnectPolicy
{
    internal int Attempts { get; private set; }
    internal void Reset() => Attempts = 0;
    internal void ObserveConnection(TimeSpan duration, bool confirmed)
    {
        // An HTTP upgrade followed by immediate disconnect is still a failed
        // setup. Only a confirmed, stable connection resets exponential backoff.
        if (confirmed && duration >= TimeSpan.FromSeconds(30)) Reset();
    }
    internal TimeSpan NextDelay(bool openedSocket, double jitter)
    {
        Attempts = Math.Min(Attempts + 1, 30);
        var delay = RealtimeSocketSafety.ReconnectDelay(Attempts, jitter);
        // The old server may still be finishing its connection-finally work.
        return openedSocket && delay < TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : delay;
    }
}

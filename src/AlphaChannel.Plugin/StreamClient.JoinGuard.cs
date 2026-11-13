using AlphaChannel.Contracts;

namespace AlphaChannel.Plugin;

internal sealed partial class StreamClient
{
    internal Func<Task<bool>>? PrepareViewerJoinAsync { get; set; }
    internal bool IsJoiningWatchParty => Mode == StreamMode.None && !string.IsNullOrEmpty(Volatile.Read(ref pendingJoinHost));

    private async Task<bool> PrepareLocalViewerJoinAsync(StreamControl message)
    {
        var version = Volatile.Read(ref capacityJoinVersion);
        var generation = ConnectionGeneration;
        if (PrepareViewerJoinAsync is not { } prepare) return true;
        if (await prepare().ConfigureAwait(false)) return true;
        lock (lifecycleGate)
        {
            // A late preparation result cannot cancel a newer join or a host.
            if (version != Volatile.Read(ref capacityJoinVersion) || generation != ConnectionGeneration ||
                Mode != StreamMode.None || pendingJoinHost != message.HostId) return false;
            CancelCapacityRetry();
            ReleaseCapacitySeat();
            pendingJoinHost = pendingJoinPassword = HostId = null;
        }
        OnDeclined?.Invoke("End your current activity before joining a Watch Party.");
        return false;
    }
}

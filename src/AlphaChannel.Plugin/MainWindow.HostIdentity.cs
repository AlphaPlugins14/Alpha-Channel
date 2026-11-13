namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string? joinedHostIdentity;
    // Also runs for ordinary viewers, whose decoder does not need a handover.
    internal void UpdateWatchPartyHostName(bool forceRefresh = false)
    {
        if (stream.Mode != StreamMode.Viewing || string.IsNullOrWhiteSpace(stream.HostId))
        { joinedHostIdentity = null; return; }
        var host = stream.HostId;
        if (forceRefresh || joinedHostIdentity is not null && !string.Equals(joinedHostIdentity, host, StringComparison.OrdinalIgnoreCase))
            joinedHostDisplayName = null;
        joinedHostIdentity = host;
        if (stream.CurrentHostDisplayName is { Length: > 0 } name) joinedHostDisplayName = name;
    }
}

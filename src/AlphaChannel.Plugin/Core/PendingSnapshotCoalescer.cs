namespace AlphaChannel.Plugin;

// Only adjacent, unsent snapshots of the same activity boundary may replace
// one another. Every ordinary command and boundary change breaks the group.
internal sealed class PendingSnapshotCoalescer
{
    internal sealed class Ticket(string key)
    {
        internal string Key { get; } = key;
        internal bool Superseded { get; set; }
    }

    private readonly object gate = new();
    private Ticket? pending;

    internal Ticket? Register(string? key)
    {
        lock (gate)
        {
            if (key is null) { pending = null; return null; }
            if (pending?.Key == key) pending.Superseded = true;
            return pending = new Ticket(key);
        }
    }

    internal bool TryBegin(Ticket? ticket)
    {
        if (ticket is null) return true;
        lock (gate)
        {
            if (ticket.Superseded) return false;
            if (ReferenceEquals(pending, ticket)) pending = null;
            return true;
        }
    }
}

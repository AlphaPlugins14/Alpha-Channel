namespace AlphaChannel.Plugin;

// One local presentation queue shared by the drawer's TV and plugin footer.
internal sealed class PictionaryGuessNotificationQueue
{
    internal sealed record Notice(string Text, bool Correct);
    private readonly Queue<Notice> pending = new();
    private readonly HashSet<Guid> seen = [];
    private (string Session, int Round, string Drawer)? scope;
    private DateTime expiresUtc;
    internal Notice? Current { get; private set; }

    internal bool SetRound(string? session, int round, string drawer, bool drawing)
    {
        (string Session, int Round, string Drawer)? next = drawing && session is not null
            ? (session, round, drawer) : null;
        if (scope == next) return false;
        Clear();
        scope = next;
        return true;
    }

    internal void Clear()
    {
        pending.Clear();
        seen.Clear();
        scope = null;
        Current = null;
        expiresUtc = DateTime.MinValue;
    }

    internal bool Enqueue(string session, int round, string drawer, Guid id, Notice notice, DateTime now)
    {
        if (scope != (session, round, drawer) || !seen.Add(id)) return false;
        pending.Enqueue(notice);
        return Advance(now);
    }

    internal bool Advance(DateTime now)
    {
        if (Current is not null && now < expiresUtc) return false;
        var previous = Current;
        Current = pending.TryDequeue(out var next) ? next : null;
        // A delayed update shows the next queued item for a full three seconds,
        // rather than skipping multiple messages to catch up with the clock.
        if (Current is not null) expiresUtc = now.AddSeconds(3);
        return previous != Current;
    }
}

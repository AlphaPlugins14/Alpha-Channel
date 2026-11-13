namespace AlphaChannel.Plugin.Core;

internal sealed record PartyQueueNotice(Guid Id, Guid ItemId, string Kind, string ActorId, string ActorName, int Count = 1);

internal sealed class PartyQueueNoticeTracker
{
    private Guid party;
    private bool initialized;
    private readonly HashSet<Guid> seen = [];
    private readonly Queue<Guid> order = new();

    internal IEnumerable<PartyQueueNotice> Observe(Guid partyId, PartyQueueNotice[]? notices)
    {
        if (party != partyId) { party = partyId; initialized = false; seen.Clear(); order.Clear(); }
        if (notices is null) return [];
        var fresh = new List<PartyQueueNotice>();
        foreach (var notice in notices.TakeLast(64))
        {
            if (notice is null || notice.Id == Guid.Empty || !seen.Add(notice.Id)) continue;
            order.Enqueue(notice.Id);
            if (initialized && notice.ItemId != Guid.Empty && notice.Kind is "requested" or "added" &&
                notice.ActorId is { Length: > 0 and <= 128 } && notice.ActorName is { Length: > 0 and <= 64 } && notice.Count is >= 1 and <= 100)
                fresh.Add(notice);
        }
        while (order.Count > 256) seen.Remove(order.Dequeue());
        // Rejoining starts from the current notice history, without replaying it.
        initialized = true;
        return fresh;
    }
}

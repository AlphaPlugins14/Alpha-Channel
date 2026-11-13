namespace AlphaChannel.Plugin.Video;

// Framework-thread owned. Newly visible cards go first. Missing titles and
// cancelled off-screen work get at most three attempts per view, with a cooldown
// between attempts. Missing view counts can opt into the same bounded retries.
internal sealed class YouTubeVisibleMetadataQueue(Func<long>? clock = null)
{
    private readonly Dictionary<string, Card> cards = new(StringComparer.Ordinal);
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private long sequence;
    private Work? active;
    internal bool Observe(VideoSearchEntry entry)
    {
        var time = now();
        if (!cards.TryGetValue(entry.Url, out var card))
        {
            if (cards.Count >= 1000) return false;
            cards[entry.Url] = card = new Card(entry, ++sequence);
        }
        card.Entry = YouTubeMetadataCache.Merge(card.Entry, entry);
        if (card.RetryMissingViews && active?.Entry.Url != entry.Url && card.Entry.ViewCount is not null &&
            YouTubeMetadataCache.HasTitle(card.Entry.Title) && !card.RetryCancelled && !card.RetryDeferred)
            card.Attempted = true;
        if (card.Attempted && active?.Entry.Url != entry.Url && card.Attempts < 3 &&
            card.RetryAfter > 0 && time >= card.RetryAfter &&
            (card.RetryCancelled || card.RetryDeferred || !YouTubeMetadataCache.HasTitle(card.Entry.Title) ||
                card.RetryMissingViews && card.Entry.ViewCount is null)) card.Attempted = false;
        if (time - card.Seen > 1000) card.Sequence = ++sequence;
        card.Seen = time;
        if (active is { } work && cards.TryGetValue(work.Entry.Url, out var old) && time - old.Seen > 1000)
            work.Cancel();
        return !card.Attempted;
    }
    internal Work? TakeNext(CancellationToken lifetime)
    {
        if (active is not null) return null;
        var time = now();
        var next = cards.Values.Where(card => !card.Attempted && time - card.Seen <= 1000 &&
                (!card.RetryMissingViews || card.Entry.ViewCount is null || card.RetryCancelled || card.RetryDeferred ||
                    !YouTubeMetadataCache.HasTitle(card.Entry.Title)))
            .OrderByDescending(card => card.Sequence).FirstOrDefault();
        if (next is null) return null;
        next.Attempted = true;
        next.Attempts++;
        next.RetryAfter = 0;
        next.RetryCancelled = false;
        next.RetryDeferred = false;
        return active = new Work(next.Entry, lifetime);
    }
    internal bool IsPending(string url) => cards.TryGetValue(url, out var card) &&
        (!card.Attempted || active is { } work && work.Entry.Url == url);
    internal int PendingCount => cards.Values.Count(card => !card.Attempted && now() - card.Seen <= 1000) +
        (active is null ? 0 : 1);
    internal void CancelOffscreen()
    {
        if (active is { } work && cards.TryGetValue(work.Entry.Url, out var card) && now() - card.Seen > 1000)
            work.Cancel();
    }
    internal void Complete(Work work, bool retryMissingTitle = false, long deferMilliseconds = 0, bool retryMissingViews = false)
    {
        if (ReferenceEquals(active, work))
        {
            if (cards.TryGetValue(work.Entry.Url, out var card))
            {
                // Drawing a card cannot requeue active work. Cancellations also
                // consume their attempt: layout/scroll oscillation must not
                // refund the budget and restart this request every frame.
                card.Attempted = true;
                card.RetryCancelled = work.Token.IsCancellationRequested;
                card.RetryMissingViews = retryMissingViews;
                card.RetryDeferred = !card.RetryCancelled && deferMilliseconds>0;
                if (card.RetryDeferred) card.Attempts--;
                card.RetryAfter = card.RetryDeferred ? now()+deferMilliseconds : card.RetryCancelled || retryMissingTitle || retryMissingViews
                    ? now() + 30_000L * card.Attempts : 0;
            }
            active = null;
        }
        work.Dispose();
    }
    internal void Reset() { active?.Cancel(); active = null; cards.Clear(); }
    private sealed class Card(VideoSearchEntry entry, long sequence)
    { internal VideoSearchEntry Entry = entry; internal long Seen = long.MinValue / 2, Sequence = sequence, RetryAfter; internal bool Attempted, RetryCancelled, RetryDeferred, RetryMissingViews; internal int Attempts; }
    internal sealed class Work : IDisposable
    {
        private readonly CancellationTokenSource cancellation;
        internal VideoSearchEntry Entry { get; }
        internal CancellationToken Token { get; }
        internal Work(VideoSearchEntry entry, CancellationToken lifetime)
        { Entry = entry; cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime); Token = cancellation.Token; }
        internal void Cancel() => cancellation.Cancel();
        public void Dispose() => cancellation.Dispose();
    }
}

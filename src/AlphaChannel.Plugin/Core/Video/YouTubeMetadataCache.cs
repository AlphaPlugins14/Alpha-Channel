namespace AlphaChannel.Plugin.Video;

internal sealed record VideoSearchEntry(
    string Title,
    string Url,
    string ChannelName,
    TimeSpan? Duration,
    string? ThumbnailUrl,
    long? ViewCount = null,
    DateTimeOffset? UploadDate = null,
    string? ChannelId = null,
    long? ViewCountObservedAtUnix = null,
    long? MetadataObservedAtUnix = null);


// Public metadata only: account feed membership and expiring playback URLs stay separate.
internal sealed class YouTubeMetadataCache(int capacity = 1000)
{
    internal static readonly YouTubeMetadataCache Shared = new();
    private readonly object gate = new();
    private readonly Dictionary<string, VideoSearchEntry> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> order = new();
    private readonly Dictionary<string, LinkedListNode<string>> nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MetadataRequestContext> pending = new(StringComparer.Ordinal);
    internal KeyValuePair<string, VideoSearchEntry>[] Snapshot()
    {
        lock (gate) return order.Select(key => new KeyValuePair<string, VideoSearchEntry>(key, entries[key])).ToArray();
    }
    // These are UI states from older saved feeds, never actual video metadata.
    internal static bool HasTitle(string? title) => !string.IsNullOrWhiteSpace(title) && title.Trim() is not
        ("Fetching video details…" or "Fetching video details..." or "Loading video details…" or "Video details unavailable") &&
        !(Uri.TryCreate(title.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https");
    // Playback needs the search result's presentation details, not the extra
    // view-count/upload-date fields used to sort discovery feeds.
    internal static bool HasPlaybackMetadata(VideoSearchEntry entry) =>
        HasTitle(entry.Title) && !string.IsNullOrWhiteSpace(entry.ThumbnailUrl);
    internal static VideoSearchEntry Normalize(VideoSearchEntry entry) =>
        HasTitle(entry.Title) ? entry : entry with { Title = string.Empty };
    internal static bool IsComplete(VideoSearchEntry entry) =>
        HasTitle(entry.Title) && !string.IsNullOrWhiteSpace(entry.ChannelName) &&
        !string.IsNullOrWhiteSpace(entry.ChannelId) && entry.ViewCount is not null && entry.UploadDate is not null;
    internal bool TryGet(string key, out VideoSearchEntry? entry)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(key, out entry)) return false;
            Touch(key); return true;
        }
    }
    internal void Store(string key, VideoSearchEntry entry, bool replace = true)
    {
        lock (gate)
        {
            entry = Normalize(entry);
            if (entries.TryGetValue(key, out var previous))
                entry = replace ? Merge(previous, entry) : Merge(entry, previous);
            entries[key] = entry; Touch(key);
            while (entries.Count > capacity && order.First is { } first)
            { entries.Remove(first.Value); nodes.Remove(first.Value); order.RemoveFirst(); }
        }
    }
    private void Touch(string key)
    {
        if (nodes.TryGetValue(key, out var node)) { order.Remove(node); order.AddLast(node); }
        else nodes[key] = order.AddLast(key);
    }
    internal static VideoSearchEntry Merge(VideoSearchEntry previous, VideoSearchEntry incoming)
    {
        var newer = (incoming.MetadataObservedAtUnix ?? 0) >= (previous.MetadataObservedAtUnix ?? 0) ? incoming : previous;
        var older = ReferenceEquals(newer, incoming) ? previous : incoming;
        return incoming with
        {
            Title = HasTitle(newer.Title) ? newer.Title : HasTitle(older.Title) ? older.Title : string.Empty,
            ChannelName = string.IsNullOrWhiteSpace(newer.ChannelName) ? older.ChannelName : newer.ChannelName,
            ChannelId = string.IsNullOrWhiteSpace(newer.ChannelId) ? older.ChannelId : newer.ChannelId,
            Duration = newer.Duration ?? older.Duration,
            ThumbnailUrl = newer.ThumbnailUrl ?? older.ThumbnailUrl,
            ViewCount = PreferIncomingCount(previous, incoming) ? incoming.ViewCount : previous.ViewCount,
            UploadDate = newer.UploadDate ?? older.UploadDate,
            ViewCountObservedAtUnix = PreferIncomingCount(previous, incoming) ? incoming.ViewCountObservedAtUnix : previous.ViewCountObservedAtUnix,
            MetadataObservedAtUnix = newer.MetadataObservedAtUnix ?? older.MetadataObservedAtUnix
        };
    }
    private static bool PreferIncomingCount(VideoSearchEntry previous, VideoSearchEntry incoming) =>
        incoming.ViewCount is not null && (previous.ViewCount is null ||
            (incoming.ViewCountObservedAtUnix ?? 0) >= (previous.ViewCountObservedAtUnix ?? 0));
    internal Task<VideoSearchEntry?> GetOrCreateAsync(string key, Func<Task<VideoSearchEntry?>> factory)
        => GetOrCreateAsync(key, _ => factory(), CancellationToken.None, 2);

    internal Task<VideoSearchEntry?> GetOrCreateAsync(string key,
        Func<MetadataRequestContext, Task<VideoSearchEntry?>> factory, CancellationToken token, int priority)
    {
        token.ThrowIfCancellationRequested();
        MetadataRequestContext request;
        bool created;
        lock (gate)
        {
            created = !pending.TryGetValue(key, out request!);
            if (created) pending[key] = request = new MetadataRequestContext(priority);
            else request.Promote(priority);
            request.Waiters++;
        }
        if (created) _ = CompleteAsync();
        return WaitAsync();
        async Task<VideoSearchEntry?> WaitAsync()
        {
            try { return await request.Completion.Task.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException error) { throw request.AnnotateCancellation(error); }
            finally
            {
                bool cancel;
                lock (gate)
                {
                    cancel = --request.Waiters == 0 && !request.Completion.Task.IsCompleted;
                    if (cancel && pending.TryGetValue(key, out var owner) && ReferenceEquals(owner, request)) pending.Remove(key);
                }
                if (cancel) request.Cancel();
            }
        }
        async Task CompleteAsync()
        {
            try
            {
                var result = await factory(request).ConfigureAwait(false);
                lock (gate)
                {
                    if (pending.TryGetValue(key, out var owner) && ReferenceEquals(owner, request))
                    {
                        if (result is not null) Store(key, result);
                        pending.Remove(key);
                    }
                    request.Completion.TrySetResult(result);
                }
            }
            catch (Exception exception)
            {
                lock (gate)
                {
                    if (pending.TryGetValue(key, out var owner) && ReferenceEquals(owner, request)) pending.Remove(key);
                    if (exception is OperationCanceledException) request.Completion.TrySetCanceled();
                    else request.Completion.TrySetException(exception);
                }
            }
            finally { request.Finish(); }
        }
    }
}

// One fetch is shared, but each caller owns only its wait. The fetch stops when
// its last waiter leaves; a replacement fetch cannot be removed by the old one.
internal sealed class MetadataRequestContext
{
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private int priorityValue;
    private bool finished;
    private Func<OperationCanceledException, OperationCanceledException>? cancellationContext;
    internal void SetCancellationContext(Func<OperationCanceledException, OperationCanceledException> context)
        => Volatile.Write(ref cancellationContext, context);
    internal OperationCanceledException AnnotateCancellation(OperationCanceledException error)
        => Volatile.Read(ref cancellationContext)?.Invoke(error) ?? error;
    internal CancellationToken Token { get; }
    internal MetadataRequestContext(int priority) { priorityValue = priority; Token = lifetime.Token; }
    internal int Priority => Volatile.Read(ref priorityValue);
    internal int Waiters;
    internal TaskCompletionSource<VideoSearchEntry?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void Promote(int value)
    {
        if (value < Priority) Volatile.Write(ref priorityValue, value);
    }
    internal void Cancel() { lock (gate) { if (!finished) lifetime.Cancel(); } }
    internal void Finish() { lock (gate) { finished = true; lifetime.Dispose(); } }
}

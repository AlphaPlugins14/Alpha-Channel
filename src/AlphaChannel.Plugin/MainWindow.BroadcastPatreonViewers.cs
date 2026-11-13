namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // These statuses come directly from the Worker, never the persisted display badge cache.
    // State and completed task results are only consumed on the framework thread.
    private readonly Dictionary<string, DateTime> broadcastPatreonViewers = new(StringComparer.OrdinalIgnoreCase);
    private string? broadcastPatreonAccount;
    private string? broadcastPatreonToken;
    private string[] broadcastPatreonAudience = [];
    private Task<Dictionary<string, int>>? broadcastPatreonLookup;
    private CancellationTokenSource? broadcastPatreonLookupCts;
    private DateTime nextBroadcastPatreonInspectionUtc;
    private DateTime nextBroadcastPatreonLookupUtc;

    private void ResetBroadcastPatreonViewers()
    {
        broadcastPatreonLookupCts?.Cancel();
        broadcastPatreonLookupCts?.Dispose();
        broadcastPatreonLookupCts = null;
        if (broadcastPatreonLookup is { } pending)
            _ = pending.ContinueWith(task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        broadcastPatreonLookup = null;
        broadcastPatreonViewers.Clear();
        broadcastPatreonAudience = [];
        broadcastPatreonAccount = null;
        broadcastPatreonToken = null;
        nextBroadcastPatreonInspectionUtc = DateTime.MinValue;
        nextBroadcastPatreonLookupUtc = DateTime.MinValue;
    }

    private void UpdateBroadcastPatreonViewers()
    {
        var session = CurrentSession;
        if (stream.Mode != StreamMode.Hosting || ConfiguredPatreonTier() <= 0 || session is null)
        {
            if (broadcastPatreonAccount is not null) ResetBroadcastPatreonViewers();
            return;
        }
        if (broadcastPatreonAccount != session.AccountId || broadcastPatreonToken != session.Token)
        {
            ResetBroadcastPatreonViewers();
            broadcastPatreonAccount = session.AccountId;
            broadcastPatreonToken = session.Token;
        }
        var now = DateTime.UtcNow;
        if (now < nextBroadcastPatreonInspectionUtc) return;
        nextBroadcastPatreonInspectionUtc = now.AddSeconds(1);
        var audience = BroadcastViewerIds().Where(id => Guid.TryParse(id, out _))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!audience.SequenceEqual(broadcastPatreonAudience, StringComparer.OrdinalIgnoreCase))
        {
            broadcastPatreonAudience = audience;
            foreach (var id in broadcastPatreonViewers.Keys.Where(id =>
                !audience.Contains(id, StringComparer.OrdinalIgnoreCase)).ToArray())
                broadcastPatreonViewers.Remove(id);
            // Debounce arrivals; a single request in flight is allowed to finish.
            nextBroadcastPatreonLookupUtc = now.AddSeconds(2);
        }
        if (broadcastPatreonLookup is { IsCompleted: true } completed)
        {
            broadcastPatreonLookup = null;
            broadcastPatreonLookupCts?.Dispose();
            broadcastPatreonLookupCts = null;
            try
            {
                var verified = completed.GetAwaiter().GetResult();
                foreach (var id in audience)
                {
                    if (!verified.TryGetValue(id, out var tier)) continue;
                    if (tier is >= 1 and <= 5) broadcastPatreonViewers[id] = now.AddMinutes(5);
                    else broadcastPatreonViewers.Remove(id);
                }
                // Viewers who arrived during the request still need their own check.
                nextBroadcastPatreonLookupUtc = audience.Any(id => !verified.ContainsKey(id))
                    ? now.AddSeconds(2) : now.AddMinutes(4);
            }
            catch (Exception)
            {
                AepLog.Warning("[BroadcastAllowance] Viewer membership verification failed.");
                nextBroadcastPatreonLookupUtc = now.AddSeconds(30);
            }
        }
        if (audience.Length == 0 || broadcastPatreonLookup is not null || now < nextBroadcastPatreonLookupUtc)
            return;
        broadcastPatreonLookupCts = new CancellationTokenSource();
        broadcastPatreonLookup = FetchBroadcastPatreonViewers(session.Token, audience, broadcastPatreonLookupCts.Token);
    }

    private async Task<Dictionary<string, int>> FetchBroadcastPatreonViewers(
        string token, string[] audience, CancellationToken cancellationToken)
    {
        var verified = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in audience.Chunk(80))
        {
            var result = await patreonWorkerClient.GetBadgesAsync(token, batch, cancellationToken).ConfigureAwait(false);
            foreach (var id in batch)
                if (result.TryGetValue(id, out var tier) && tier is >= 0 and <= 5) verified[id] = tier;
        }
        return verified;
    }
}

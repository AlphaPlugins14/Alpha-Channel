using System.Text.Json;

namespace AlphaChannel.Plugin.Auth;

// Display-only cache. All mutation is serialized by MainWindow's badge lock.
internal sealed class PatreonBadgeCache : IDisposable
{
    internal sealed record Entry(int Tier, DateTime LastSeenUtc, DateTime? LastStatusCheckUtc,
        bool StatusCheckNeeded, bool IsFriend);
    internal sealed record Refresh(string[] AccountIds);
    private sealed record SavedCache(int Version, Dictionary<string, Entry> Entries,
        DateTime? PendingSinceUtc, DateTime? NextRefreshUtc, DateTime? LastFetchAttemptUtc,
        int SuccessfulFetchRuns);
    private readonly object stateGate = new(), writeGate = new();
    private readonly TimeSpan writeDelay;
    private long saveVersion, writtenVersion;
    private Task? pendingSave;
    private bool stopped;
    private readonly CancellationTokenSource saveLifetime = new();
    private readonly string path;
    private readonly Action<string> warn;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private DateTime nextCleanupUtc;
    private DateTime nextStatusInspectionUtc;
    private DateTime? pendingSinceUtc;
    private bool fetching;
    internal DateTime? NextRefreshUtc { get; private set; }
    internal DateTime? LastFetchAttemptUtc { get; private set; }
    internal int SuccessfulFetchRuns { get; private set; }
    internal IReadOnlyDictionary<string, Entry> Entries => entries;

    internal PatreonBadgeCache(string path, DateTime now, Action<string> warn, TimeSpan? writeDelay = null)
    {
        this.writeDelay = writeDelay ?? TimeSpan.FromSeconds(2);
        this.path = path;
        this.warn = warn;
        try
        {
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("Version", out var version) && version.GetInt32() == 2)
                {
                    var saved = document.RootElement.Deserialize<SavedCache>()!;
                    foreach (var (id, entry) in saved.Entries)
                        if (Guid.TryParse(id, out _) && entry.Tier is >= 0 and <= 5)
                            entries[id] = entry;
                    pendingSinceUtc = saved.PendingSinceUtc;
                    NextRefreshUtc = saved.NextRefreshUtc;
                    LastFetchAttemptUtc = saved.LastFetchAttemptUtc;
                    SuccessfulFetchRuns = Math.Clamp(saved.SuccessfulFetchRuns, 0, 6);
                }
                else
                {
                    // Legacy cache had only Tier/UpdatedUtc; retain known status and use
                    // its timestamp as the best available last encounter time.
                    if (document.RootElement.TryGetProperty("Entries", out var oldEntries))
                        foreach (var item in oldEntries.EnumerateObject())
                            if (Guid.TryParse(item.Name, out _) && item.Value.GetProperty("Tier").GetInt32() is >= 0 and <= 5)
                            {
                                var updated = item.Value.GetProperty("UpdatedUtc").GetDateTime();
                                entries[item.Name] = new(item.Value.GetProperty("Tier").GetInt32(), updated,
                                    updated, false, false);
                            }
                    if (document.RootElement.TryGetProperty("RetryIds", out var retryIds))
                        foreach (var item in retryIds.EnumerateArray())
                            if (item.GetString() is { } id && Guid.TryParse(id, out _))
                                entries[id] = entries.TryGetValue(id, out var entry)
                                    ? entry with { StatusCheckNeeded = true } : new(0, now, null, true, false);
                }
            }
        }
        catch (Exception exception) { warn($"[Patreon] Could not load badge cache: {exception.Message}"); }
        // Friend protection must be reconciled before launch cleanup (including legacy files).
        nextCleanupUtc = now;
        nextStatusInspectionUtc = now;
        Schedule(now);
    }

    internal int GetTier(string accountId, DateTime now) =>
        entries.TryGetValue(accountId, out var entry) && entry.LastStatusCheckUtc is not null ? entry.Tier : 0;

    private static bool NeedsStatus(Entry entry, DateTime now) => entry.LastStatusCheckUtc is not { } checkedAt ||
        now - checkedAt >= TimeSpan.FromHours(entry.Tier == 0 ? 6 : 24);

    internal void Observe(IEnumerable<string> accountIds, DateTime now)
    {
        lock (stateGate)
        {
            var observed = false;
            foreach (var id in accountIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Guid.TryParse(id, out _)) continue;
                observed = true;
                var entry = entries.GetValueOrDefault(id) ?? new Entry(0, now, null, true, false);
                entries[id] = entry with { LastSeenUtc = now,
                    StatusCheckNeeded = entry.StatusCheckNeeded || NeedsStatus(entry, now) };
            }
            if (!observed) return;
            Schedule(now);
            Save();
        }
    }

    internal void SetFriends(IEnumerable<string> accountIds, DateTime now)
    {
        lock (stateGate)
        {
            var friends = new HashSet<string>(accountIds.Where(id => Guid.TryParse(id, out _)), StringComparer.OrdinalIgnoreCase);
            foreach (var (id, entry) in entries.ToArray())
                if (entry.IsFriend != friends.Contains(id))
                    entries[id] = entry with { IsFriend = friends.Contains(id) };
            Observe(friends, now);
            foreach (var id in friends) entries[id] = entries[id] with { IsFriend = true };
            Save();
        }
    }

    internal void Maintain(DateTime now, bool cleanup = true)
    {
        lock (stateGate)
        {
            var changed = false;
            if (cleanup && now >= nextCleanupUtc)
            {
                foreach (var (id, entry) in entries.ToArray())
                    if (!entry.IsFriend && now - entry.LastSeenUtc > TimeSpan.FromDays(5))
                    { entries.Remove(id); changed = true; }
                nextCleanupUtc = now.AddHours(6);
            }
            if (now >= nextStatusInspectionUtc)
            {
                foreach (var (id, entry) in entries.ToArray())
                    if (!entry.StatusCheckNeeded && NeedsStatus(entry, now))
                    { entries[id] = entry with { StatusCheckNeeded = true }; changed = true; }
                nextStatusInspectionUtc = now.AddHours(3);
            }
            if (changed) { Schedule(now); Save(); }
        }
    }

    private bool CanFetch(DateTime now) => LastFetchAttemptUtc is not { } last || now - last >= TimeSpan.FromMinutes(15);

    internal void RequestImmediate(DateTime now)
    {
        lock (stateGate)
        {
            // A blocked launch/connection trigger is discarded, never deferred to cooldown expiry.
            if (fetching || !CanFetch(now) || !entries.Values.Any(entry => entry.StatusCheckNeeded)) return;
            NextRefreshUtc = now;
            Save();
        }
    }

    private void Schedule(DateTime now)
    {
        var count = entries.Values.Count(entry => entry.StatusCheckNeeded);
        if (count == 0)
        {
            pendingSinceUtc = null; NextRefreshUtc = null; return;
        }
        pendingSinceUtc ??= now;
        var hours = count switch { <= 3 => 12, <= 10 => 8, <= 15 => 6, <= 30 => 3, _ => 1 };
        if (SuccessfulFetchRuns < 3) hours = Math.Min(hours, 1);
        else if (SuccessfulFetchRuns < 6) hours = Math.Min(hours, 3);
        var deadline = pendingSinceUtc.Value.AddHours(hours);
        if (NextRefreshUtc is null || deadline < NextRefreshUtc)
        {
            NextRefreshUtc = deadline;
        }
    }

    internal bool IsPending(string id) => entries.TryGetValue(id, out var entry) && entry.StatusCheckNeeded;

    internal Refresh? BeginRefresh(DateTime now)
    {
        lock (stateGate)
        {
            if (fetching || NextRefreshUtc is not { } due || now < due || !CanFetch(now)) return null;
            var ids = entries.Where(pair => pair.Value.StatusCheckNeeded).Select(pair => pair.Key).ToArray();
            if (ids.Length == 0) { Schedule(now); Save(); return null; }
            fetching = true;
            LastFetchAttemptUtc = now;
            pendingSinceUtc = null;
            NextRefreshUtc = null;
            Save(); // Persist the attempt before any network work, including across failures/restarts.
            return new Refresh(ids);
        }
    }

    internal void CompleteRefresh(IReadOnlyDictionary<string, int> successes, IEnumerable<string> failures, DateTime now)
    {
        lock (stateGate)
        {
            var successful = false;
            foreach (var (id, tier) in successes)
                if (entries.TryGetValue(id, out var entry) && tier is >= 0 and <= 5)
                {
                    entries[id] = entry with { Tier = tier, LastStatusCheckUtc = now, StatusCheckNeeded = false };
                    successful = true;
                }
            if (successful) SuccessfulFetchRuns = Math.Min(6, SuccessfulFetchRuns + 1);
            fetching = false;
            Schedule(now);
            if (failures.Any(IsPending))
            {
                var retry = now.AddMinutes(15);
                if (NextRefreshUtc is null || retry < NextRefreshUtc) NextRefreshUtc = retry;
            }
            Save();
        }
    }

    private void Save()
    {
        saveVersion++;
        if (writeDelay == TimeSpan.Zero) { Persist(); return; }
        if (stopped || pendingSave is { IsCompleted: false }) return;
        pendingSave = Task.Run(async () =>
        {
            try { await Task.Delay(writeDelay,saveLifetime.Token).ConfigureAwait(false); Persist(); }
            catch (OperationCanceledException) { }
            finally
            {
                lock (stateGate)
                {
                    pendingSave = null;
                    if (!stopped && saveVersion > writtenVersion) Save();
                }
            }
        });
    }
    internal Task FlushAsync() => Task.Run(Persist);
    private void Persist()
    {
        lock (writeGate)
        {
            SavedCache snapshot; long revision;
            lock (stateGate)
            {
                if (saveVersion <= writtenVersion) return;
                revision = saveVersion;
                snapshot = new(2,new(entries,StringComparer.OrdinalIgnoreCase),pendingSinceUtc,
                    NextRefreshUtc,LastFetchAttemptUtc,SuccessfulFetchRuns);
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path+".tmp",JsonSerializer.Serialize(snapshot));
                File.Move(path+".tmp",path,overwrite:true);
                lock (stateGate) writtenVersion = revision;
            }
            catch (Exception exception)
            {
                // Retry on the next normal mutation; never spin on a broken disk.
                lock (stateGate) writtenVersion = revision;
                warn($"[Patreon] Could not save badge cache: {exception.Message}");
            }
        }
    }
    public void Dispose()
    {
        lock (stateGate) { stopped = true; saveLifetime.Cancel(); }
        // Account replacement and plugin shutdown flush the last snapshot.
        // Normal discovery, maintenance and message arrivals never write here.
        Persist();
    }
}

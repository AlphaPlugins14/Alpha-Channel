using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AlphaChannel.Plugin.ActivityPoints;

// A local, account-bound tally. DPAPI keeps it out of the editable plugin config
// and prevents a protected file copied to another Windows user from opening.
internal sealed class ActivityPointsService
{
    internal const long UnitsPerPoint = 1000;
    private const int RecentEventLimit = 1024;
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("AlphaChannel.ActivityPoints.v1");
    private readonly string statePath;
    private readonly Dictionary<string, AccountPoints> accounts = new(StringComparer.Ordinal);

    internal bool StorageHealthy { get; private set; } = true;

    internal ActivityPointsService(string configDirectory)
    {
        statePath = Path.Combine(configDirectory, "Usage", "activity-points.dat");
        Restore();
    }

    internal long GetPoints(string? accountId)
    {
        if (!StorageHealthy || string.IsNullOrWhiteSpace(accountId)) return 0;
        return accounts.TryGetValue(accountId, out var state) ? state.Units / UnitsPerPoint : 0;
    }

    // Returns whole points newly made visible. Fractional units remain saved.
    internal long Award(string? accountId, string eventId, long units)
        => AwardCore(accountId, eventId, units, null, 1);

    // Save the remainder and event atomically, including spins that do not yet earn a point.
    internal long AwardCounted(string? accountId, string eventId, string counter, int amount, int threshold)
        => threshold > 0 && !string.IsNullOrWhiteSpace(counter)
            ? AwardCore(accountId, eventId, amount, counter, threshold) : 0;

    private long AwardCore(string? accountId, string eventId, long units, string? counter, int threshold)
    {
        if (!StorageHealthy || string.IsNullOrWhiteSpace(accountId) ||
            string.IsNullOrWhiteSpace(eventId) || units <= 0) return 0;
        var before = accounts.GetValueOrDefault(accountId);
        if (before?.RecentEvents.Contains(eventId, StringComparer.Ordinal) == true) return 0;

        var oldUnits = before?.Units ?? 0;
        var counters = before?.Counters is { } saved ? new Dictionary<string, long>(saved) : new();
        if (counter is not null)
        {
            var total = Math.Clamp(counters.GetValueOrDefault(counter), 0, threshold - 1L) + Math.Min(units, int.MaxValue);
            counters[counter] = total % threshold;
            units = total / threshold * UnitsPerPoint;
        }
        var nextUnits = oldUnits > long.MaxValue - units ? long.MaxValue : oldUnits + units;
        var events = before?.RecentEvents.ToList() ?? [];
        events.Add(eventId);
        if (events.Count > RecentEventLimit)
            events.RemoveRange(0, events.Count - RecentEventLimit);
        accounts[accountId] = new AccountPoints(accountId, nextUnits, events.ToArray(), counters);
        if (!Save())
        {
            if (before is null) accounts.Remove(accountId);
            else accounts[accountId] = before;
            return 0;
        }
        return nextUnits / UnitsPerPoint - oldUnits / UnitsPerPoint;
    }

    private void Restore()
    {
        if (!File.Exists(statePath)) return;
        try
        {
            var encrypted = File.ReadAllBytes(statePath);
            var plaintext = ProtectedData.Unprotect(encrypted, DpapiEntropy, DataProtectionScope.CurrentUser);
            try
            {
                var document = JsonSerializer.Deserialize<PointsDocument>(plaintext)
                    ?? throw new InvalidDataException("Activity points record is empty.");
                if (document.Version != 1)
                    throw new InvalidDataException("Unsupported activity points record version.");
                foreach (var state in document.Accounts)
                {
                    if (string.IsNullOrWhiteSpace(state.AccountId) || state.Units < 0) continue;
                    accounts[state.AccountId] = state with
                    {
                        RecentEvents = (state.RecentEvents ?? [])
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .TakeLast(RecentEventLimit).ToArray(),
                    };
                }
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (Exception)
        {
            StorageHealthy = false;
            AepLog.Error("[Activity Points] Protected state could not be opened.");
        }
    }

    private bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(new PointsDocument(1, accounts.Values.ToArray()));
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(plaintext, DpapiEntropy, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            var temporary = statePath + ".tmp";
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, statePath, true);
            try { File.SetAttributes(statePath, File.GetAttributes(statePath) | FileAttributes.Hidden); }
            catch { /* Hiding is cosmetic; DPAPI protects the contents. */ }
            return true;
        }
        catch (Exception)
        {
            StorageHealthy = false;
            AepLog.Error("[Activity Points] Could not save protected state.");
            return false;
        }
    }

    private sealed record PointsDocument(int Version, AccountPoints[] Accounts);
    private sealed record AccountPoints(string AccountId, long Units, string[] RecentEvents, Dictionary<string, long>? Counters = null);
}

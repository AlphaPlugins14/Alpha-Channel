using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace AlphaChannel.Plugin.BroadcastAllowance;

internal enum BroadcastAllowanceMethod
{
    None,
    DjLive,
    RetroGame,
    Browser,
    LocalVideo,
    Obs,
}

internal enum BroadcastAllowanceQuality
{
    DataSaver,
    Standard,
    High,
}

internal readonly record struct BroadcastAllowanceUsage(
    BroadcastAllowanceMethod Method,
    double ObsBitrateMbps = 0d,
    double ViewerMultiplier = 1d,
    BroadcastAllowanceQuality Quality = BroadcastAllowanceQuality.DataSaver,
    int AudioBitrateKbps = 128)
{
    internal static BroadcastAllowanceUsage None =>
        new(BroadcastAllowanceMethod.None);
}

internal readonly record struct BroadcastAllowanceSnapshot(
    long RemainingUnits,
    long TotalUnits,
    double RemainingFraction,
    double TokensPerMinute,
    TimeSpan? EstimatedRemainingTime,
    DateTime NextResetUtc,
    int PatreonTier,
    bool StorageHealthy)
{
    internal double RemainingTokens =>
        RemainingUnits / (double)BroadcastAllowanceService.UnitsPerToken;

    internal double TotalTokens =>
        TotalUnits / (double)BroadcastAllowanceService.UnitsPerToken;
}

// Local-only allowance accounting. The aggregate record is protected with
// Windows DPAPI and written atomically. A separate Windows user marker makes
// clearing the editable config's first-run flag insufficient to reset usage.
// The server must eventually enforce allowance for protection from modified clients.
internal sealed partial class BroadcastAllowanceService : IDisposable
{
    internal const long WeeklyTokens = 420;
    internal const long UnitsPerToken = 600;
    internal const long WeeklyUnits = WeeklyTokens * UnitsPerToken;
    internal const long MaximumWeeklyTokens = 6920;
    internal const long MaximumWeeklyUnits =
        MaximumWeeklyTokens * UnitsPerToken;

    private static readonly byte[] DpapiEntropy =
        Encoding.UTF8.GetBytes("AlphaChannel.BroadcastAllowance.v1");
    private const string InitializationMarkerKey = @"Software\AlphaChannel\BroadcastAllowance";
    private const string InitializationMarkerValue = "InitializedV1";

    private static readonly TimeSpan SaveInterval =
        TimeSpan.FromSeconds(5);

    private readonly Configuration configuration;
    private readonly Func<DateTime> utcNow;
    private readonly string statePath;
    private readonly Func<bool> hasInitializationMarker;
    private readonly Action<bool> setInitializationMarker;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Dictionary<string, AccountAllowanceState> accounts =
        new(StringComparer.Ordinal);

    private string? activeAccountId;
    private BroadcastAllowanceUsage activeUsage;
    private long lastTick;
    private double pendingUnits;
    private DateTime lastSaveUtc = DateTime.MinValue;
    private bool dirty;
    private bool storageHealthy = true;
    private bool disposed;
    private DateTime lockedUntilUtc;
    private bool protectionActivated;

    internal BroadcastAllowanceService(
        Configuration configuration,
        string configDirectory,
        Func<bool>? hasInitializationMarker = null,
        Action<bool>? setInitializationMarker = null,
        Func<DateTime>? utcNow = null)
    {
        this.configuration = configuration;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.hasInitializationMarker = hasInitializationMarker ?? HasInitializationMarker;
        this.setInitializationMarker = setInitializationMarker ?? SetInitializationMarker;
        statePath = Path.Combine(
            configDirectory,
            "Usage",
            "broadcast-allowance.dat");

        Restore();
        lastTick = clock.ElapsedTicks;
    }

    internal static long WeeklyTokensForTier(int patreonTier) =>
        patreonTier switch
        {
            >= 5 => MaximumWeeklyTokens,
            4 => 4740,
            3 => 2960,
            2 => 1750,
            1 => 1030,
            _ => WeeklyTokens,
        };

    internal static double ViewerConsumptionMultiplier(
        int patreonTier,
        int viewerCount,
        int patreonViewerCount)
    {
        var actualViewers = Math.Max(0, viewerCount);
        if (actualViewers == 0) return 0d;
        var verifiedPatreonViewers = Math.Clamp(
            patreonViewerCount,
            0,
            actualViewers);
        var viewerMultiplier = 1d + Math.Min(actualViewers - 1, 2) * 0.05d +
                               Math.Max(actualViewers - 3, 0) * 0.1d;
        var discount = patreonTier > 0
            ? Math.Min(0.5d, verifiedPatreonViewers / (double)actualViewers)
            : 0d;
        return viewerMultiplier * (1d - discount);
    }

    internal static double TokensPerMinute(
        BroadcastAllowanceUsage usage)
    {
        var baseRate = usage.Method switch
        {
            BroadcastAllowanceMethod.DjLive => usage.AudioBitrateKbps switch
            {
                96 => 0.9d,
                192 => 1.8d,
                256 => 2.4d,
                320 => 3d,
                _ => 1.2d,
            },
            BroadcastAllowanceMethod.RetroGame => usage.Quality == BroadcastAllowanceQuality.Standard ? 2.6d : 1.5d,
            BroadcastAllowanceMethod.Browser => usage.Quality switch
            {
                BroadcastAllowanceQuality.High => 3d,
                BroadcastAllowanceQuality.Standard => 2.6d,
                _ => 1.7d,
            },
            BroadcastAllowanceMethod.LocalVideo => usage.Quality switch
            {
                BroadcastAllowanceQuality.High => 5d,
                BroadcastAllowanceQuality.Standard => 3.7d,
                _ => 2d,
            },
            BroadcastAllowanceMethod.Obs => ObsTokensPerMinute(
                usage.ObsBitrateMbps),
            _ => 0d,
        };

        return baseRate * (double.IsFinite(usage.ViewerMultiplier)
            ? Math.Max(0d, usage.ViewerMultiplier) : 1d);
    }

    internal static double ObsTokensPerMinute(double bitrateMbps)
    {
        if (bitrateMbps <= 0d)
        {
            // The recommended OBS profile is approximately 4.16 Mbps.
            return 4d;
        }

        if (bitrateMbps <= 1.25d) return 2.1d;
        if (bitrateMbps <= 2.5d) return 3d;
        if (bitrateMbps <= 4.5d) return 4d;
        if (bitrateMbps <= 6d) return 5d;
        if (bitrateMbps <= 8d) return 6d;

        // 6-8 Mbps costs 6 tokens/minute, then each additional 2 Mbps
        // band adds another token (over 8-10 => 7, over 10-12 => 8).
        return 6d + Math.Ceiling((bitrateMbps - 8d) / 2d);
    }

    internal bool Update(
        string? accountId,
        int patreonTier,
        BroadcastAllowanceUsage usage)
    {
        if (disposed)
        {
            return false;
        }

        RetryStorageIfDue();
        var nowTick = clock.ElapsedTicks;
        var elapsedSeconds = lastTick == 0
            ? 0d
            : (nowTick - lastTick) / (double)Stopwatch.Frequency;
        lastTick = nowTick;

        ChargeElapsed(elapsedSeconds);

        var normalizedAccountId =
            string.IsNullOrWhiteSpace(accountId)
                ? null
                : accountId.Trim();
        var nextUsage = usage;

        if (!string.Equals(
                activeAccountId,
                normalizedAccountId,
                StringComparison.Ordinal) ||
            activeUsage != nextUsage)
        {
            CommitPendingUnits(roundUp: true);
            // Persist immediately when a broadcast starts, stops, or changes
            // rate. Clean plugin reloads therefore cannot discard the final
            // partial interval, while the five-second checkpoint covers a
            // process or game crash during a long broadcast.
            Save();
            activeAccountId = normalizedAccountId;
            activeUsage = nextUsage;
        }

        if (activeAccountId is { } activeId)
        {
            RefreshWeek(GetOrCreate(activeId));
        }

        SaveIfDue();

        var totalUnits = WeeklyTokensForTier(patreonTier) * UnitsPerToken;
        return usage.Method == BroadcastAllowanceMethod.None ||
               normalizedAccountId is not { } id ||
               storageHealthy && GetRemainingUnits(id, totalUnits) > 0;
    }

    internal bool CanStart(
        string? accountId,
        int patreonTier)
    {
        RetryStorageIfDue();
        if (string.IsNullOrWhiteSpace(accountId) || !storageHealthy)
        {
            return false;
        }

        CommitPendingUnits();
        var totalUnits = WeeklyTokensForTier(patreonTier) * UnitsPerToken;
        return GetRemainingUnits(accountId.Trim(), totalUnits) > 0;
    }

    internal BroadcastAllowanceSnapshot GetSnapshot(
        string? accountId,
        int patreonTier,
        BroadcastAllowanceUsage usage)
    {
        RetryStorageIfDue();
        CommitPendingUnits();

        var rate = TokensPerMinute(usage);
        var now = SafeUtcNow();
        var reset = StartOfUtcWeek(now).AddDays(7);
        var normalizedTier = Math.Clamp(patreonTier, 0, 5);
        var totalUnits = WeeklyTokensForTier(normalizedTier) * UnitsPerToken;

        if (string.IsNullOrWhiteSpace(accountId) || !storageHealthy)
        {
            return new BroadcastAllowanceSnapshot(
                0,
                totalUnits,
                0d,
                rate,
                TimeSpan.Zero,
                reset,
                normalizedTier,
                storageHealthy);
        }

        var state = GetOrCreate(accountId.Trim());
        RefreshWeek(state);
        var remaining = Math.Max(0, totalUnits - state.UsedUnits);
        TimeSpan? estimated = rate > 0d
            ? TimeSpan.FromMinutes(
                remaining /
                (double)UnitsPerToken /
                rate)
            : null;

        return new BroadcastAllowanceSnapshot(
            remaining,
            totalUnits,
            remaining / (double)totalUnits,
            rate,
            estimated,
            StartOfUtcWeek(state.LastSeenUtc).AddDays(7),
            normalizedTier,
            storageHealthy);
    }

    internal void Flush()
    {
        RetryStorageIfDue();
        ChargeElapsed(
            (clock.ElapsedTicks - lastTick) /
            (double)Stopwatch.Frequency);
        lastTick = clock.ElapsedTicks;
        CommitPendingUnits(roundUp: true);
        Save();
    }

    private void ChargeElapsed(double seconds)
    {
        if (seconds <= 0d ||
            activeAccountId is not { } accountId ||
            activeUsage.Method == BroadcastAllowanceMethod.None)
        {
            return;
        }

        var state = GetOrCreate(accountId);
        RefreshWeek(state);
        if (state.UsedUnits >= MaximumWeeklyUnits)
        {
            return;
        }

        var rate = TokensPerMinute(activeUsage);
        pendingUnits += seconds * rate * UnitsPerToken / 60d;
        if (pendingUnits > 0d && !protectionActivated)
        {
            // Opening onboarding, signing in and viewing the balance do not activate
            // reset protection. Persist it when a broadcast first consumes allowance.
            protectionActivated = true;
            dirty = true;
            Save();
            if (storageHealthy)
                SynchronizeProtectionMarkers();
        }
    }

    private void CommitPendingUnits(bool roundUp = false)
    {
        if (pendingUnits <= 0d)
        {
            return;
        }

        if (activeAccountId is not { } accountId)
        {
            pendingUnits = 0d;
            return;
        }

        var wholeUnits = (long)(roundUp
            ? Math.Ceiling(pendingUnits)
            : Math.Floor(pendingUnits));
        if (wholeUnits <= 0)
        {
            return;
        }

        pendingUnits = roundUp
            ? 0d
            : pendingUnits - wholeUnits;
        var state = GetOrCreate(accountId);
        RefreshWeek(state);
        state.UsedUnits = Math.Min(
            MaximumWeeklyUnits,
            state.UsedUnits + wholeUnits);
        state.LastSeenUtc = SafeUtcNow(state.LastSeenUtc);
        dirty = true;
    }

    private long GetRemainingUnits(string accountId, long totalUnits)
    {
        var state = GetOrCreate(accountId);
        RefreshWeek(state);
        return Math.Max(0, totalUnits - state.UsedUnits);
    }

    private AccountAllowanceState GetOrCreate(string accountId)
    {
        if (accounts.TryGetValue(accountId, out var existing))
        {
            return existing;
        }

        var now = SafeUtcNow();
        var state = new AccountAllowanceState
        {
            AccountId = accountId,
            WeekStartUtc = StartOfUtcWeek(now),
            LastSeenUtc = now,
            UsedUnits = storageHealthy && now >= lockedUntilUtc
                ? 0
                : MaximumWeeklyUnits,
        };
        accounts[accountId] = state;
        dirty = true;
        return state;
    }

    private void RefreshWeek(AccountAllowanceState state)
    {
        var now = SafeUtcNow(state.LastSeenUtc);
        var currentWeek = StartOfUtcWeek(now);

        if (lockedUntilUtc != default && now >= lockedUntilUtc)
        {
            lockedUntilUtc = default;
            dirty = true;
        }

        // Clock rollback never grants another allowance. Moving forward into
        // a genuinely newer UTC week resets usage once.
        if (currentWeek > state.WeekStartUtc)
        {
            state.WeekStartUtc = currentWeek;
            state.UsedUnits = 0;
            dirty = true;
        }

        if (now > state.LastSeenUtc)
        {
            state.LastSeenUtc = now;
            dirty = true;
        }
    }

    private static bool HasInitializationMarker()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InitializationMarkerKey);
            return key?.GetValue(InitializationMarkerValue) is int value && value == 1;
        }
        catch (Exception)
        {
            // An unreadable marker must not turn a missing allowance record
            // into a new, unused allowance.
            AepLog.Warning("[Allowance] Could not read initialization marker.");
            return true;
        }
    }

    private void SynchronizeProtectionMarkers()
    {
        // Configuration/marker failures cannot invalidate successfully read usage.
        try
        {
            setInitializationMarker(protectionActivated);
            configuration.BroadcastAllowanceRepairs ??= new(StringComparer.Ordinal);
            var repairedMarkersChanged = false;
            foreach (var pair in repairStamps)
            {
                if (pair.Key == pendingRepairAccount) continue;
                if (!configuration.BroadcastAllowanceRepairs.TryGetValue(pair.Key, out var saved) || saved.Version < pair.Value.Version)
                {
                    configuration.BroadcastAllowanceRepairs[pair.Key] = pair.Value;
                    repairedMarkersChanged = true;
                }
            }
            if (configuration.BroadcastAllowanceInitialized != protectionActivated || markersPending || repairedMarkersChanged)
            {
                configuration.BroadcastAllowanceInitialized = protectionActivated;
                configuration.Save();
            }
            markersPending = false;
        }
        catch (Exception exception)
        {
            markersPending = true;
            ReportStorageFailure(AllowanceStorageOperation.Markers, exception, block: false);
        }
    }

    private static void SetInitializationMarker(bool activated)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            using var key = activated
                ? Registry.CurrentUser.CreateSubKey(InitializationMarkerKey)
                : Registry.CurrentUser.OpenSubKey(InitializationMarkerKey, writable: true);
            if (!activated)
            {
                key?.DeleteValue(InitializationMarkerValue, throwOnMissingValue: false);
                return;
            }
            if (key is null)
                throw new IOException("Initialization marker key could not be created.");
            key.SetValue(InitializationMarkerValue, 1, RegistryValueKind.DWord);
        }
        catch (Exception)
        {
            AepLog.Warning("[Allowance] Could not write initialization marker.");
        }
    }

    private DateTime SafeUtcNow(DateTime minimum = default)
    {
        var now = utcNow();
        return minimum != default && now < minimum
            ? minimum
            : now;
    }

    private static DateTime StartOfUtcWeek(DateTime utc)
    {
        var normalized = DateTime.SpecifyKind(utc.Date, DateTimeKind.Utc);
        var daysSinceMonday =
            ((int)normalized.DayOfWeek + 6) % 7;
        return normalized.AddDays(-daysSinceMonday);
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        var totalMinutes = Math.Max(
            0,
            (int)Math.Floor(duration.TotalMinutes));
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return hours > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{hours}h {minutes:00}m")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{minutes}m");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        try { Flush(); }
        finally
        {
            disposed = true;
            storageLease?.Dispose();
            storageLease = null;
        }
    }

    private sealed record AllowanceDocument(
        int Version,
        AccountAllowanceState[] Accounts,
        DateTime LockedUntilUtc = default,
        bool ProtectionActivated = false,
        long Revision = 0,
        Dictionary<string, AllowanceRepairStamp>? Repairs = null);

    private sealed class AccountAllowanceState
    {
        public string AccountId { get; set; } = string.Empty;
        public DateTime WeekStartUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public long UsedUnits { get; set; }
    }
}

namespace AlphaChannel.Plugin.BroadcastAllowance;

internal sealed record AllowanceRepairStamp(int Version, DateTime CompletedUtc, string Recovery);

internal sealed partial class BroadcastAllowanceService
{
    private const int OneTimeRepairVersion = 1;
    private readonly Dictionary<string, AllowanceRepairStamp> repairStamps = new(StringComparer.Ordinal);
    private string? pendingRepairAccount;

    private bool RepairAlreadyAssessed(string account) =>
        repairStamps.TryGetValue(account, out var local) && local.Version >= OneTimeRepairVersion ||
        configuration.BroadcastAllowanceRepairs is { } savedRepairs &&
        savedRepairs.TryGetValue(account, out var saved) && saved.Version >= OneTimeRepairVersion;

    internal bool NeedsOneTimeRepair(string account)
    {
        if (disposed || string.IsNullOrWhiteSpace(account)) return false;
        RetryStorageIfDue();
        if (pendingRepairAccount is not null)
        {
            if (storageHealthy) VerifyRepairAndSynchronize();
            if (pendingRepairAccount is not null) return false;
        }
        if (RepairAlreadyAssessed(account)) return false;
        if (storageHealthy && storageLoaded && SafeUtcNow() >= lockedUntilUtc)
        {
            // This rollout only repairs pre-existing problems. Mark healthy
            // accounts assessed so a later fault cannot trigger a fresh reset.
            repairStamps[account] = new(OneTimeRepairVersion, SafeUtcNow(), "Healthy");
            pendingRepairAccount = account;
            dirty = true;
            Save();
            if (storageHealthy) VerifyRepairAndSynchronize();
            return false;
        }
        return storageLease is not null &&
            (storageHealthy && storageLoaded && SafeUtcNow() < lockedUntilUtc ||
             !storageLoaded && storageFailure is AllowanceStorageFailure.Encryption or AllowanceStorageFailure.InvalidRecord);
    }

    internal bool TryOneTimeRepair(string account, int tier, BroadcastLedgerStatus ledger)
    {
        if (!NeedsOneTimeRepair(account)) return false;
        var now = SafeUtcNow();
        var week = StartOfUtcWeek(now);
        if (ledger.WeekStartAt != new DateTimeOffset(week).ToUnixTimeSeconds() ||
            ledger.LimitUnits < 0 || ledger.UsedMinimumUnits < 0 ||
            ledger.Reason is not ("allowed" or "not_broadcasting" or "allowance_exceeded")) return false;
        var total = WeeklyTokensForTier(Math.Clamp(tier, 0, 5)) * UnitsPerToken;
        var remaining = Math.Min(total, Math.Max(0, ledger.LimitUnits - ledger.UsedMinimumUnits));
        var operation = AllowanceStorageOperation.Read;
        try
        {
            // Preserve every original protected copy before changing any state.
            var directory = Path.GetDirectoryName(statePath)!;
            var backup = Path.Combine(directory, "repair-v1-" + now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            foreach (var path in Directory.GetFiles(directory, Path.GetFileName(statePath) + "*"))
            {
                if (path.EndsWith(".lock", StringComparison.Ordinal)) continue;
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var target = new FileStream(Path.Combine(backup, Path.GetFileName(path)), FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                source.CopyTo(target); target.Flush(true);
            }
            if (!storageLoaded)
            {
                accounts.Clear();
                repairStamps.Clear();
                // Other accounts sharing a lost record must be checked against
                // their own Cloudflare balance, never receive an implicit reset.
                lockedUntilUtc = week.AddDays(7);
                primaryValidated = false;
                storageLoaded = true;
            }
            accounts[account] = new AccountAllowanceState
            {
                AccountId = account, WeekStartUtc = week, LastSeenUtc = now, UsedUnits = total - remaining,
            };
            repairStamps[account] = new(OneTimeRepairVersion, now, "CloudflareCappedReset");
            pendingRepairAccount = account;
            protectionActivated = true;
            storageHealthy = true;
            nextStorageRetryUtc = default;
            dirty = true;
            Save();
            return storageHealthy && VerifyRepairAndSynchronize();
        }
        catch (Exception exception)
        {
            ReportStorageFailure(operation, exception);
            return false;
        }
    }

    private bool VerifyRepairAndSynchronize()
    {
        if (pendingRepairAccount is not { } account) return true;
        var operation = AllowanceStorageOperation.Read;
        try
        {
            var verified = ReadDocument(statePath, ref operation);
            if (verified.Repairs is not { } stamps || !stamps.TryGetValue(account, out var stamp) ||
                !repairStamps.TryGetValue(account, out var expected) || stamp != expected)
                throw new InvalidDataException();
            pendingRepairAccount = null;
            SynchronizeProtectionMarkers();
            if (stamp.Recovery == "CloudflareCappedReset")
                AepLog.Warning("[Allowance] One-time local allowance repair verified; remaining usage was capped by Cloudflare.");
            return true;
        }
        catch (Exception exception)
        {
            // Force normal validation on the next retry. A persisted stamp only
            // becomes authoritative after a subsequent successful read as well.
            storageLoaded = false;
            ReportStorageFailure(operation, exception);
            return false;
        }
    }
}

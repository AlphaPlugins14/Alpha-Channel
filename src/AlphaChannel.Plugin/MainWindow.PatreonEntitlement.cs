using AlphaChannel.Plugin.Auth;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly PatreonWorkerClient patreonWorkerClient = new();
    private readonly object patreonStateLock = new();
    private string? verifiedPatreonAccountId;
    private bool verifiedPatreonLinked;
    private int verifiedPatreonTier;
    private DateTime verifiedPatreonExpiresUtc = DateTime.MinValue;
    private string? scheduledPatreonAccountId;
    private DateTime nextPatreonRefreshUtc = DateTime.MinValue;
    private bool patreonRefreshRunning;
    private bool patreonChangePending;
    private DateTime lastCompletedPatreonConnectionUtc = DateTime.MinValue;

    // Called by Plugin's framework update, including while the main window is closed.
    internal void RefreshPatreonEntitlementIfNeeded()
    {
        UpdateHostBroadcastRepair();
        var session = CurrentSession;
        if (session is null || string.IsNullOrWhiteSpace(session.Token))
        {
            lock (patreonStateLock)
            {
                if (verifiedPatreonTier > 0)
                    patreonChangePending = true;

                verifiedPatreonAccountId = null;
                verifiedPatreonLinked = false;
                verifiedPatreonTier = 0;
                verifiedPatreonExpiresUtc = DateTime.MinValue;
                scheduledPatreonAccountId = null;
                nextPatreonRefreshUtc = DateTime.MinValue;
                lastCompletedPatreonConnectionUtc = DateTime.MinValue;
            }
            return;
        }

        lock (patreonStateLock)
        {
            if (scheduledPatreonAccountId != session.AccountId)
            {
                if (verifiedPatreonTier > 0 && verifiedPatreonAccountId != session.AccountId)
                    patreonChangePending = true;

                scheduledPatreonAccountId = session.AccountId;
                nextPatreonRefreshUtc = DateTime.MinValue;
                lastCompletedPatreonConnectionUtc = DateTime.MinValue;
            }

            if (hostBroadcastRepairTask is not null || patreonRefreshRunning || DateTime.UtcNow < nextPatreonRefreshUtc)
                return;

            patreonRefreshRunning = true;
            nextPatreonRefreshUtc = DateTime.UtcNow.AddMinutes(1);
        }

        _ = RefreshPatreonEntitlementAsync(session.AccountId, session.Token);
    }

    internal void ApplyPendingPatreonEntitlementChange()
    {
        lock (patreonStateLock)
        {
            if (!patreonChangePending)
                return;

            patreonChangePending = false;
        }

        NotifyLocalPatreonMembershipChanged();
    }

    private async Task RefreshPatreonEntitlementAsync(string accountId, string token)
    {
        var requestStartedUtc = DateTime.UtcNow;
        try
        {
            var entitlement = await patreonWorkerClient
                .GetEntitlementAsync(token, CancellationToken.None)
                .ConfigureAwait(false);
            lock (patreonStateLock)
            {
                if (scheduledPatreonAccountId != accountId ||
                    requestStartedUtc < lastCompletedPatreonConnectionUtc)
                    return;
                ApplyPatreonEntitlement(accountId, entitlement);
                ScheduleNextPatreonEntitlementCheck(accountId, entitlement);
            }
        }
        catch (Exception)
        {
            AepLog.Warning("[Patreon] Worker entitlement refresh failed.");
            lock (patreonStateLock)
            {
                if (verifiedPatreonAccountId == accountId &&
                    DateTime.UtcNow >= verifiedPatreonExpiresUtc &&
                    verifiedPatreonTier > 0)
                {
                    verifiedPatreonTier = 0;
                    patreonChangePending = true;
                }
                nextPatreonRefreshUtc = DateTime.UtcNow.AddMinutes(1);
            }
        }
        finally
        {
            lock (patreonStateLock)
            {
                if (scheduledPatreonAccountId != accountId)
                    nextPatreonRefreshUtc = DateTime.MinValue;
                patreonRefreshRunning = false;
            }
        }
    }

    private void ApplyCompletedPatreonConnection(
        string accountId, PatreonEntitlement entitlement)
    {
        lock (patreonStateLock)
        {
            lastCompletedPatreonConnectionUtc = DateTime.UtcNow;
            ApplyPatreonEntitlement(accountId, entitlement);
            ScheduleNextPatreonEntitlementCheck(accountId, entitlement);
        }
    }

    private void ScheduleNextPatreonEntitlementCheck(
        string accountId, PatreonEntitlement entitlement)
    {
        lock (patreonStateLock)
        {
            if (scheduledPatreonAccountId != accountId) return;
            var now = DateTime.UtcNow;
            if (!entitlement.Linked)
            {
                // An unlinked account checks once per sign-in. Explicit Patreon
                // connection checks its own OAuth flow and updates this schedule.
                nextPatreonRefreshUtc = DateTime.MaxValue;
                return;
            }
            var probing = entitlement.Tier == 0 && entitlement.ProbeUntil is { } probeUntil &&
                DateTimeOffset.FromUnixTimeSeconds(probeUntil).UtcDateTime > now;
            nextPatreonRefreshUtc = now.AddMinutes(probing ? 10 : 30);
            if (verifiedPatreonAccountId == accountId && verifiedPatreonTier > 0 &&
                verifiedPatreonExpiresUtc < nextPatreonRefreshUtc)
                nextPatreonRefreshUtc = verifiedPatreonExpiresUtc;
        }
    }

    private void ApplyPatreonEntitlement(string accountId, PatreonEntitlement entitlement)
    {
        if (entitlement.Tier is < 0 or > 5)
            throw new InvalidOperationException("Patreon Worker returned an invalid tier.");

        var tier = entitlement.Linked && entitlement.Entitled ? entitlement.Tier : 0;
        var expiresUtc = DateTime.MinValue;
        if (tier > 0 && entitlement.CheckedAt is { } checkedAt)
        {
            expiresUtc = DateTimeOffset.FromUnixTimeSeconds(checkedAt)
                .UtcDateTime.AddHours(24);
            if (DateTime.UtcNow >= expiresUtc)
                tier = 0;
        }
        else
        {
            tier = 0;
        }

        lock (patreonStateLock)
        {
            if (verifiedPatreonAccountId != accountId || verifiedPatreonTier != tier)
                patreonChangePending = true;

            verifiedPatreonAccountId = accountId;
            verifiedPatreonLinked = entitlement.Linked;
            verifiedPatreonTier = tier;
            verifiedPatreonExpiresUtc = expiresUtc;
        }
    }

    private int CurrentVerifiedPatreonTier()
    {
        var accountId = CurrentSession?.AccountId;
        lock (patreonStateLock)
            return accountId is not null && accountId == verifiedPatreonAccountId
                && DateTime.UtcNow < verifiedPatreonExpiresUtc
                ? verifiedPatreonTier
                : 0;
    }

    private bool CurrentPatreonLinked()
    {
        var accountId = CurrentSession?.AccountId;
        lock (patreonStateLock)
            return accountId is not null && accountId == verifiedPatreonAccountId &&
                verifiedPatreonLinked;
    }
}

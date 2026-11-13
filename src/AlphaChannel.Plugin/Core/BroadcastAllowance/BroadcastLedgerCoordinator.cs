namespace AlphaChannel.Plugin.BroadcastAllowance;

// Called on the framework thread. HTTP tasks run asynchronously; their results
// are applied on subsequent framework ticks, never on the media thread.
internal sealed class BroadcastLedgerCoordinator
{
    private static readonly TimeSpan InitialVerificationGrace = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InitialVerificationRetry = TimeSpan.FromSeconds(30);
    private readonly IBroadcastLedgerClient client;
    private readonly Func<DateTime> utcNow;
    internal BroadcastLedgerCoordinator(IBroadcastLedgerClient? client = null, Func<DateTime>? utcNow = null)
    {
        this.client = client ?? new BroadcastLedgerClient();
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }
    private string? hostAccountId;
    private string? hostToken;
    private string? hostSessionId;
    private Task<BroadcastLedgerStatus>? hostTask;
    private DateTime nextHostCheckUtc;
    private bool hostImmediateRecoveryUsed;
    private bool hostRetryRequested;
    private bool notifyAfterHostCheck;
    private bool hostAllowanceRefreshPending;
    private DateTime hostTaskStartedUtc;
    private DateTime nextHostWeeklyResetUtc;
    private string? viewerHostId;
    private string? viewerToken;
    private Task<BroadcastLedgerStatus>? viewerTask;
    private DateTime nextViewerCheckUtc;
    private DateTime? outageStartedUtc;
    private DateTime? denialStartedUtc;
    private DateTime? provisionalStartedUtc;
    private bool viewerAllowed;
    private string viewerMessage = "Checking broadcast allowance...";
    private bool viewerStateChanged;
    private bool viewerRetryRequested;
    private DateTime viewerTaskStartedUtc;

    internal string ViewerMessage => viewerMessage;
    internal bool ViewerBlocked => !viewerAllowed && viewerMessage.EndsWith("paused.", StringComparison.Ordinal);
    internal void RetryViewer() => viewerRetryRequested = true;
    internal void RetryHost() => hostRetryRequested = true;
    internal Task<BroadcastLedgerStatus> CheckOwnAllowanceAsync(string token, string accountId) =>
        client.StatusAsync(token, accountId);
    internal bool TakeHostAllowanceRefresh()
    {
        var pending = hostAllowanceRefreshPending;
        hostAllowanceRefreshPending = false;
        return pending;
    }
    internal static DateTime NextWeeklyResetUtc(DateTime now) =>
        now.Date.AddDays(7 - (((int)now.DayOfWeek + 6) % 7));

    private static DateTime BeforeWeeklyReset(DateTime now, TimeSpan delay) =>
        Min(now.Add(delay), NextWeeklyResetUtc(now));
    internal bool TakeViewerStateChanged()
    {
        var changed = viewerStateChanged;
        viewerStateChanged = false;
        return changed;
    }

    internal void UpdateHost(string? accountId, string? token, bool active)
    {
        var now = utcNow();
        if (hostAccountId != accountId || hostToken != token)
        {
            StopHost();
            hostAccountId = accountId;
            hostToken = token;
        }

        if (!active || string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(token))
        {
            StopHost();
            return;
        }
        if (nextHostWeeklyResetUtc == default) nextHostWeeklyResetUtc = NextWeeklyResetUtc(now);
        if (now >= nextHostWeeklyResetUtc)
        {
            RetryHost();
            nextHostWeeklyResetUtc = NextWeeklyResetUtc(now);
        }

        if (hostTask is { IsCompleted: true } completed)
        {
            hostTask = null;
            try
            {
                var status = completed.GetAwaiter().GetResult();
                if (!status.Allowed && now >= NextWeeklyResetUtc(hostTaskStartedUtc))
                {
                    // A denial requested before Monday must not defer checking the new week.
                    nextHostCheckUtc = now;
                }
                else if (status.Allowed)
                {
                    // Also wake blocked viewers when a stopped broadcast obtains a new lease.
                    hostAllowanceRefreshPending |= notifyAfterHostCheck || hostSessionId != status.SessionId;
                    hostSessionId = status.SessionId;
                    hostImmediateRecoveryUsed = false;
                    notifyAfterHostCheck = false;
                    nextHostCheckUtc = BeforeWeeklyReset(now, TimeSpan.FromMinutes(15));
                }
                else if (status.Reason == "not_broadcasting")
                {
                    hostSessionId = null;
                    // Recover a missing lease immediately once; repeated failures must wait.
                    nextHostCheckUtc = hostImmediateRecoveryUsed ? BeforeWeeklyReset(now, TimeSpan.FromSeconds(30)) : now;
                    hostImmediateRecoveryUsed = true;
                }
                else
                {
                    hostSessionId = null;
                    nextHostCheckUtc = BeforeWeeklyReset(now, TimeSpan.FromMinutes(5));
                }
            }
            catch (Exception)
            {
                AepLog.Warning("[BroadcastLedger] Host check failed.");
                nextHostCheckUtc = BeforeWeeklyReset(now, TimeSpan.FromSeconds(30));
            }
        }

        if (hostTask is null && hostRetryRequested)
        {
            hostRetryRequested = false;
            notifyAfterHostCheck = true;
            nextHostCheckUtc = now;
        }
        if (hostTask is null && now >= nextHostCheckUtc)
        {
            hostTaskStartedUtc = now;
            hostTask = hostSessionId is { } id
                ? client.RenewAsync(token, id)
                : client.StartAsync(token);
            nextHostCheckUtc = BeforeWeeklyReset(now, TimeSpan.FromMinutes(15));
        }
    }

    private void StopHost()
    {
        if (hostTask is { } pending && hostToken is { } pendingToken)
            _ = StopWhenFinished(pending, pendingToken);
        if (hostSessionId is { } id && hostToken is { } token)
        {
            _ = StopQuietly(token, id);
        }
        hostSessionId = null;
        hostTask = null;
        nextHostCheckUtc = DateTime.MinValue;
        hostImmediateRecoveryUsed = false;
        hostRetryRequested = false;
        notifyAfterHostCheck = false;
        hostAllowanceRefreshPending = false;
        nextHostWeeklyResetUtc = default;
    }

    private async Task StopWhenFinished(Task<BroadcastLedgerStatus> task, string token)
    {
        try
        {
            var result = await task.ConfigureAwait(false);
            if (result.SessionId is { } id)
                await StopQuietly(token, id).ConfigureAwait(false);
        }
        catch { /* A failed start or renewal has no lease to stop. */ }
    }

    private async Task StopQuietly(string token, string id)
    {
        try { await client.StopAsync(token, id).ConfigureAwait(false); }
        catch (Exception)
        { AepLog.Warning("[BroadcastLedger] Stop failed."); }
    }

    internal bool CanView(string? accountToken, string? hostId)
    {
        var now = utcNow();
        if (viewerHostId != hostId || viewerToken != accountToken)
        {
            viewerHostId = hostId;
            viewerToken = accountToken;
            viewerTask = null;
            viewerRetryRequested = false;
            nextViewerCheckUtc = DateTime.MinValue;
            outageStartedUtc = null;
            denialStartedUtc = null;
            provisionalStartedUtc = !string.IsNullOrWhiteSpace(accountToken) &&
                !string.IsNullOrWhiteSpace(hostId) ? now : null;
            viewerAllowed = provisionalStartedUtc is not null;
            viewerMessage = "Checking broadcast allowance...";
            viewerStateChanged = true;
        }
        if (string.IsNullOrWhiteSpace(accountToken) || string.IsNullOrWhiteSpace(hostId))
            return false;

        if (viewerTask is { IsCompleted: true } completed)
        {
            viewerTask = null;
            try
            {
                var result = completed.GetAwaiter().GetResult();
                outageStartedUtc = null;
                if (!result.Allowed && now >= NextWeeklyResetUtc(viewerTaskStartedUtc))
                {
                    // Keep current playback while verifying an old-week denial again.
                    nextViewerCheckUtc = now;
                }
                else if (result.Allowed && result.LeaseExpiresAt is { } expiresAt)
                {
                    provisionalStartedUtc = null;
                    denialStartedUtc = null;
                    SetViewer(true, "");
                    var lease = DateTimeOffset.FromUnixTimeSeconds(expiresAt).UtcDateTime;
                    nextViewerCheckUtc = Min(BeforeWeeklyReset(now, TimeSpan.FromMinutes(15)), lease.AddSeconds(-2));
                }
                else
                {
                    if (result.Reason == "allowance_exceeded")
                    {
                        provisionalStartedUtc = null;
                        denialStartedUtc = null;
                        SetViewer(false, "Broadcast allowance exceeded. Playback paused.");
                        nextViewerCheckUtc = BeforeWeeklyReset(now, TimeSpan.FromMinutes(5));
                    }
                    else if (provisionalStartedUtc is { } started &&
                             now - started < InitialVerificationGrace)
                    {
                        // The live URL can arrive before the host's lease.
                        // Keep playing briefly while their start request finishes.
                        nextViewerCheckUtc = BeforeWeeklyReset(now, InitialVerificationRetry);
                    }
                    else
                    {
                        provisionalStartedUtc = null;
                        if (viewerAllowed && denialStartedUtc is null)
                        {
                            denialStartedUtc = now;
                            nextViewerCheckUtc = BeforeWeeklyReset(now, TimeSpan.FromSeconds(10));
                            return viewerAllowed;
                        }
                        SetViewer(false, "Broadcast verification unavailable. Playback paused.");
                        nextViewerCheckUtc = result.Reason == "not_broadcasting"
                            ? BeforeWeeklyReset(now, TimeSpan.FromMinutes(1)) : BeforeWeeklyReset(now, TimeSpan.FromMinutes(5));
                    }
                }
            }
            catch (Exception exception)
            {
                AepLog.Warning("[BroadcastLedger] Viewer check failed.");
                if (exception is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden })
                {
                    provisionalStartedUtc = null;
                    SetViewer(false, "Broadcast authorization failed. Playback paused.");
                    nextViewerCheckUtc = BeforeWeeklyReset(now, TimeSpan.FromMinutes(5));
                    return false;
                }
                if (provisionalStartedUtc is { } started)
                {
                    if (now - started < InitialVerificationGrace)
                    {
                        nextViewerCheckUtc = BeforeWeeklyReset(now, InitialVerificationRetry);
                        return viewerAllowed;
                    }
                    provisionalStartedUtc = null;
                    SetViewer(false, "Broadcast verification unavailable. Playback paused.");
                    nextViewerCheckUtc = BeforeWeeklyReset(now, TimeSpan.FromMinutes(1));
                    return false;
                }
                if (denialStartedUtc is not null)
                {
                    SetViewer(false, "Broadcast verification unavailable. Playback paused.");
                    nextViewerCheckUtc = BeforeWeeklyReset(now, TimeSpan.FromMinutes(5));
                    return false;
                }
                outageStartedUtc ??= now;
                if (!viewerAllowed || now - outageStartedUtc >= TimeSpan.FromMinutes(30))
                    SetViewer(false, "Broadcast verification unavailable. Playback paused.");
                nextViewerCheckUtc = BeforeWeeklyReset(now, TimeSpan.FromMinutes(5));
            }
        }

        if (outageStartedUtc is { } outage && now - outage >= TimeSpan.FromMinutes(30))
            SetViewer(false, "Broadcast verification unavailable. Playback paused.");
        if (viewerTask is null && viewerRetryRequested)
        {
            viewerRetryRequested = false;
            nextViewerCheckUtc = now;
        }
        if (viewerTask is null && now >= nextViewerCheckUtc)
        {
            viewerTaskStartedUtc = now;
            viewerTask = client.StatusAsync(accountToken, hostId);
            nextViewerCheckUtc = BeforeWeeklyReset(now, TimeSpan.FromMinutes(5));
        }
        return viewerAllowed;
    }

    internal void ResetViewer()
    {
        if (viewerHostId is null) return;
        viewerHostId = null;
        viewerToken = null;
        viewerTask = null;
        viewerAllowed = false;
        viewerStateChanged = false;
        viewerRetryRequested = false;
        outageStartedUtc = null;
        denialStartedUtc = null;
        provisionalStartedUtc = null;
    }

    private void SetViewer(bool allowed, string message)
    {
        if (viewerAllowed != allowed || viewerMessage != message)
            viewerStateChanged = true;
        viewerAllowed = allowed;
        viewerMessage = message;
    }

    private static DateTime Min(DateTime first, DateTime second) =>
        first < second ? first : second;
}


using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.BroadcastAllowance;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private sealed record AllowanceRepairLookup(string Account, string Token, PatreonEntitlement Entitlement, BroadcastLedgerStatus Ledger);
    private Task<AllowanceRepairLookup?>? allowanceRepairLookup;
    private DateTime nextAllowanceRepairLookupUtc;

    private void UpdateOneTimeAllowanceRepair()
    {
        if (allowanceRepairLookup is { IsCompleted: true } completed)
        {
            allowanceRepairLookup = null;
            var result = completed.GetAwaiter().GetResult();
            if (result is not null && CurrentSession is { } current &&
                current.AccountId == result.Account && current.Token == result.Token &&
                broadcastAllowance.NeedsOneTimeRepair(current.AccountId))
            {
                try
                {
                    ApplyCompletedPatreonConnection(current.AccountId, result.Entitlement);
                    if (!AccountBroadcastRestricted && broadcastAllowance.TryOneTimeRepair(current.AccountId,
                        ConfiguredPatreonTier(), result.Ledger)) BroadcastLedger.RetryHost();
                }
                catch (Exception)
                {
                    AepLog.Warning("[Allowance] One-time repair could not apply the verified entitlement; repair will be retried.");
                }
            }
        }
        if (allowanceRepairLookup is not null || CurrentSession is not { } session ||
            !broadcastAllowance.NeedsOneTimeRepair(session.AccountId) || AccountBroadcastRestricted ||
            DateTime.UtcNow < nextAllowanceRepairLookupUtc) return;
        nextAllowanceRepairLookupUtc = DateTime.UtcNow.AddMinutes(1);
        allowanceRepairLookup = LookupAllowanceRepairAsync(session.AccountId, session.Token);
    }

    private async Task<AllowanceRepairLookup?> LookupAllowanceRepairAsync(string account, string token)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(youtubeFeedToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            var entitlement = await patreonWorkerClient.RefreshOwnEntitlementAsync(token, deadline.Token).ConfigureAwait(false);
            if (entitlement.Stale) return null;
            var ledger = await BroadcastLedger.CheckOwnAllowanceAsync(token, account).WaitAsync(deadline.Token).ConfigureAwait(false);
            return new(account, token, entitlement, ledger);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception)
        {
            AepLog.Warning("[Allowance] One-time repair verification is unavailable; existing files were preserved and will be retried.");
            return null;
        }
    }
}

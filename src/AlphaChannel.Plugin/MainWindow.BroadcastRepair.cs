using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.BroadcastAllowance;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private sealed record HostBroadcastRepairResult(PatreonEntitlement Entitlement,
        BroadcastLedgerStatus? Ledger, string? LedgerError);
    private Task<HostBroadcastRepairResult>? hostBroadcastRepairTask;
    private CancellationTokenSource? hostBroadcastRepairCts;
    private string? hostBroadcastRepairAccountId;
    private string? hostBroadcastRepairToken;
    private DateTime hostBroadcastRepairCooldownUtc;
    private string? hostBroadcastRepairMessage;
    private bool hostBroadcastRepairError;

    private void BeginHostBroadcastRepair()
    {
        if (hostBroadcastRepairTask is not null || DateTime.UtcNow < hostBroadcastRepairCooldownUtc ||
            stream.Mode != StreamMode.Hosting || CurrentSession is not { } session) return;
        hostBroadcastRepairAccountId = session.AccountId;
        hostBroadcastRepairToken = session.Token;
        hostBroadcastRepairCooldownUtc = DateTime.UtcNow.AddMinutes(1);
        hostBroadcastRepairMessage = "Checking your Patreon membership and broadcast allowance…";
        hostBroadcastRepairError = false;
        hostBroadcastRepairCts = new CancellationTokenSource();
        hostBroadcastRepairTask = CheckHostBroadcastRepairAsync(session.AccountId, session.Token, hostBroadcastRepairCts.Token);
    }

    private async Task<HostBroadcastRepairResult> CheckHostBroadcastRepairAsync(
        string accountId, string token, CancellationToken cancellationToken)
    {
        var entitlement = await patreonWorkerClient.RefreshOwnEntitlementAsync(token, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var ledger = await BroadcastLedger.CheckOwnAllowanceAsync(token, accountId).ConfigureAwait(false);
            return new(entitlement, ledger, null);
        }
        catch (Exception)
        {
            AepLog.Warning("[BroadcastAllowance] Host allowance recheck failed.");
            return new(entitlement, null, "Membership was checked, but broadcast allowance verification is temporarily unavailable.");
        }
    }

    private void CancelHostBroadcastRepair()
    {
        hostBroadcastRepairCts?.Cancel();
        hostBroadcastRepairCts?.Dispose();
        hostBroadcastRepairCts = null;
        if (hostBroadcastRepairTask is { } pending)
            _ = pending.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        hostBroadcastRepairTask = null;
        hostBroadcastRepairAccountId = null;
        hostBroadcastRepairToken = null;
        hostBroadcastRepairMessage = null;
    }

    private void UpdateHostBroadcastRepair()
    {
        if (hostBroadcastRepairAccountId is null) return;
        if (stream.Mode != StreamMode.Hosting || CurrentSession is not { } session ||
            session.AccountId != hostBroadcastRepairAccountId || session.Token != hostBroadcastRepairToken)
        {
            CancelHostBroadcastRepair();
            return;
        }
        if (hostBroadcastRepairTask is not { IsCompleted: true } completed) return;
        hostBroadcastRepairTask = null;
        hostBroadcastRepairCts?.Dispose();
        hostBroadcastRepairCts = null;
        try
        {
            var result = completed.GetAwaiter().GetResult();
            var entitlement = result.Entitlement;
            if (entitlement.Stale) throw new InvalidOperationException("Membership check returned a cached result.");
            var previousTier = ConfiguredPatreonTier();
            ApplyCompletedPatreonConnection(session.AccountId, entitlement);
            broadcastAllowance.Flush();
            BroadcastLedger.RetryHost();
            var tier = ConfiguredPatreonTier();
            var local = broadcastAllowance.GetSnapshot(session.AccountId, tier, BroadcastAllowanceUsage.None);
            hostBroadcastRepairMessage = !entitlement.Linked
                ? "Connect Patreon in Settings to verify your membership. Broadcast allowance will also be rechecked."
                : result.LedgerError is { } ledgerError ? ledgerError
                : result.Ledger?.Reason == "allowance_exceeded"
                    ? local.RemainingUnits > 0
                        ? "Your membership was refreshed, but broadcast verification still reports an allowance mismatch. Please try again shortly."
                        : "Your membership was refreshed. Your weekly broadcast allowance is still exhausted."
                : local.RemainingUnits == 0
                    ? "Your membership was refreshed. Your weekly broadcast allowance is still exhausted."
                : previousTier != tier
                    ? "Your Patreon membership and weekly allowance have been updated. Broadcast verification will also be rechecked."
                    : "Your membership is up to date. Broadcast allowance verification will be rechecked.";
            hostBroadcastRepairError = result.LedgerError is not null ||
                result.Ledger?.Reason == "allowance_exceeded" || local.RemainingUnits == 0;
        }
        catch (Exception exception)
        {
            BroadcastLedger.RetryHost();
            hostBroadcastRepairError = true;
            hostBroadcastRepairMessage = exception is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests }
                ? "Please wait a minute before checking Patreon again. Broadcast allowance will still be rechecked."
                : "Could not refresh Patreon. Please try again shortly. Broadcast allowance will still be rechecked.";
            AepLog.Warning("[BroadcastAllowance] Host repair failed.");
        }
    }

    private void DrawHostBroadcastRepair(float width)
    {
        const string label = "Broadcast blocked or recently changed Patreon tier?";
        var textWidth = width - Ui(40f);
        var messageHeight = hostBroadcastRepairMessage is { } message
            ? ImGui.CalcTextSize(message, false, textWidth).Y + Ui(10f) : 0f;
        var height = Ui(84f) + ImGui.CalcTextSize(label, false, textWidth).Y + messageHeight;
        DrawActivePartyCard("##hostBroadcastRepair", new Vector2(width, height), () =>
        {
            using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + textWidth)) ImGui.TextColored(MutedText, label);
            using (UiDisabled.Scope(hostBroadcastRepairTask is not null || DateTime.UtcNow < hostBroadcastRepairCooldownUtc))
                if (ActivePartyButton(hostBroadcastRepairTask is not null ? "Checking…##hostBroadcastRepair" : "Attempt to fix##hostBroadcastRepair",
                        new Vector2(MathF.Min(textWidth, Ui(180f)), Ui(36f)))) BeginHostBroadcastRepair();
            if (hostBroadcastRepairMessage is { } status)
                using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + textWidth))
                    ImGui.TextColored(hostBroadcastRepairError ? Gold : MutedText, status);
        });
    }
}

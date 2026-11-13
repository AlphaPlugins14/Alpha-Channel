using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string? patreonRecheckToken, patreonRecheckMessage;
    private bool patreonRecheckBusy, patreonRecheckError;
    private DateTime patreonRecheckCooldownUtc;

    private void DrawPatreonMembershipRecheck(CharacterSession session)
    {
        if (patreonRecheckToken != session.Token)
        {
            patreonRecheckToken = session.Token;
            patreonRecheckBusy = false;
            patreonRecheckMessage = null;
            patreonRecheckCooldownUtc = DateTime.MinValue;
        }
        var coolingDown = DateTime.UtcNow < patreonRecheckCooldownUtc;
        var enabled = !patreonRecheckBusy && !patreonConnectionBusy && hostBroadcastRepairTask is null && !coolingDown;
        const string label = "Recheck Patreon Membership Tier";
        using (UiFonts.PushRegularRelative(.86f))
        {
            var origin = ImGui.GetCursorScreenPos();
            var size = ImGui.CalcTextSize(label);
            var clicked = ImGui.InvisibleButton("##patreonMembershipRecheck", size);
            var hovered = ImGui.IsItemHovered();
            ImGui.GetWindowDrawList().AddText(origin, ImGui.GetColorU32(enabled
                ? hovered ? PatreonOrangeHover : PatreonOrange : MutedText), label);
            if (enabled && hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (clicked && enabled)
            {
                patreonRecheckBusy = true;
                patreonRecheckError = false;
                patreonRecheckMessage = "Checking your Patreon membership…";
                patreonRecheckCooldownUtc = DateTime.UtcNow.AddMinutes(1);
                _ = RecheckPatreonMembershipAsync(session);
            }
        }
        if (patreonRecheckMessage is { } message)
        {
            using (UiFonts.PushRegularRelative(.84f))
            using (ImRaii.TextWrapPos(0))
                ImGui.TextColored(patreonRecheckError ? Danger : MutedText, message);
        }
        if (coolingDown && !patreonRecheckBusy)
        {
            using (UiFonts.PushRegularRelative(.80f))
                ImGui.TextColored(MutedText, $"Recheck available in {Math.Max(1, (int)Math.Ceiling((patreonRecheckCooldownUtc - DateTime.UtcNow).TotalSeconds))}s.");
        }
    }

    private async Task RecheckPatreonMembershipAsync(CharacterSession session)
    {
        PatreonEntitlement? entitlement = null;
        string? error = null;
        try
        {
            entitlement = await patreonWorkerClient.RefreshOwnEntitlementAsync(session.Token, youtubeFeedToken).ConfigureAwait(false);
            if (entitlement.Stale) { entitlement = null; error = "Patreon couldn't be checked right now. Your existing status has been kept."; }
        }
        catch (OperationCanceledException) when (youtubeFeedToken.IsCancellationRequested) { return; }
        catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        { error = "Please wait a minute before rechecking Patreon again."; }
        catch
        { error = "Couldn't recheck Patreon. Your existing status has been kept. Please try again shortly."; }
        if (Volatile.Read(ref disposeStarted) != 0) return;
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (Volatile.Read(ref disposeStarted) != 0 || CurrentSession?.Token != session.Token || patreonRecheckToken != session.Token) return;
            patreonRecheckBusy = false;
            if (entitlement is null)
            {
                patreonRecheckError = true;
                patreonRecheckMessage = error;
                return;
            }
            ApplyCompletedPatreonConnection(session.AccountId, entitlement);
            RequestPendingPatreonBadges(session.AccountId);
            ApplyPendingPatreonEntitlementChange();
            BroadcastLedger.RetryHost();
            nextProfileThemeRefresh = DateTime.MinValue;
            themeEditorToken = null;
            patreonRecheckError = false;
            patreonRecheckMessage = !entitlement.Linked ? "Patreon is no longer linked. Connect Patreon to verify your membership."
                : CurrentVerifiedPatreonTier() is > 0 and var tier ? $"Membership refreshed · Tier {tier} ({PatreonTierName(tier)})."
                : "Membership rechecked. Patreon reports no active membership tier.";
        }).ConfigureAwait(false);
    }
}

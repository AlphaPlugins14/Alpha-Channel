using System.Numerics;
using AlphaChannel.Plugin.BroadcastAllowance;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly Dictionary<string, (bool Verified, DateTime Until)> accountVerification = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> accountVerificationPending = new(StringComparer.OrdinalIgnoreCase);
    private bool accountVerificationBusy, accountAccessPopup;
    private BroadcastAllowanceMethod accountAccessMethod;
    private DateTime accountVerificationFetchAt;
    private bool AccountBroadcastRestricted => CurrentSession?.IsGuest == true && CurrentVerifiedPatreonTier() == 0;

    private bool AccountVerified(string? id)
    {
        if (!Guid.TryParse(id, out _)) return false;
        if (id == CurrentSession?.AccountId) return !CurrentSession.IsGuest;
        if (!accountVerification.TryGetValue(id, out var status) || DateTime.UtcNow >= status.Until) accountVerificationPending.Add(id);
        return status.Verified;
    }
    private void DrawAccountVerification(ImDrawListPtr draw, Vector2 position, string? id, bool showTooltip = true)
    {
        if (!AccountVerified(id)) return;
        var size = ImGui.GetFontSize();
        using (UiFonts.PushIcon()) UiFonts.DrawText(draw, size * .8f, position + new Vector2(0, size * .1f),
            ImGui.GetColorU32(AccentHover), FontAwesomeIcon.CheckCircle.ToIconString());
        if (showTooltip && ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(position, position + new Vector2(size))) UiFonts.SetTooltip("Verified");
    }
    private void TickAccountVerification()
    {
        if (CurrentSession is not { } session || accountVerificationBusy || DateTime.UtcNow < accountVerificationFetchAt || accountVerificationPending.Count == 0) return;
        var ids = accountVerificationPending.Take(80).ToArray();
        foreach (var id in ids) accountVerificationPending.Remove(id);
        accountVerificationBusy = true; accountVerificationFetchAt = DateTime.UtcNow.AddSeconds(2);
        _ = Task.Run(async () => {
            AlphaChannel.Contracts.AccountGuestStatus[]? statuses = null;
            try { statuses = await authClient.GetGuestStatusesAsync(session.Token, ids, youtubeFeedToken); } catch (Exception) { }
            if (youtubeFeedToken.IsCancellationRequested) return;
            await Plugin.Framework.RunOnFrameworkThread(() => {
                accountVerificationBusy = false;
                if (statuses is null) { accountVerificationFetchAt = DateTime.UtcNow.AddSeconds(30); return; }
                if (accountVerification.Count > 4096) accountVerification.Clear();
                foreach (var status in statuses) accountVerification[status.AccountId] = (!status.IsGuest, DateTime.UtcNow.AddMinutes(5));
            });
        });
    }
    private void DrawAccountAccessPopup()
    {
        if (!accountAccessPopup) return;
        DrawEventWindow("accountAccess", "Broadcast access", UiVec(500, 310), () => {
            ImGui.TextWrapped(AccountBroadcastRequirement(accountAccessMethod));
            ImGui.Spacing();
            ImGui.TextWrapped("Verification also lets you sign in again if you lose your plugin data or change devices.");
        }, () => accountAccessPopup = false, () => {
            if (GameLayoutButton("Verify with XIV Auth", FontAwesomeIcon.CheckCircle, Ui(220), true))
            { accountAccessPopup = false; StartSignIn(CurrentSession); }
            ImGui.SameLine();
            if (GameLayoutButton("Join on Patreon", FontAwesomeIcon.Heart, Ui(180)))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PatreonMembershipUrl) { UseShellExecute = true });
        });
    }
    private static string AccountBroadcastRequirement(BroadcastAllowanceMethod method) =>
        (method switch
        {
            BroadcastAllowanceMethod.Browser => "Browser",
            BroadcastAllowanceMethod.DjLive => "DJ",
            BroadcastAllowanceMethod.RetroGame => "Retro game",
            BroadcastAllowanceMethod.LocalVideo => "Local video",
            BroadcastAllowanceMethod.Obs => "OBS",
            _ => "Live"
        }) + " broadcasting requires verification via XIVAuth or joining our Patreon.";

    private bool DrawAccountBroadcastRestriction(string type, string? message = null)
    {
        if (!AccountBroadcastRestricted) return false;
        using (Dalamud.Interface.Utility.Raii.ImRaii.TextWrapPos(0))
            ImGui.TextWrapped(message ?? type + " broadcasting requires verification via XIVAuth or joining our Patreon.");
        var available = ImGui.GetContentRegionAvail().X;
        var buttonsWidth = ImGui.CalcTextSize("Verify with XIV Auth").X + ImGui.CalcTextSize("Join on Patreon").X +
            ImGui.GetStyle().FramePadding.X * 4 + ImGui.GetStyle().ItemSpacing.X;
        if (ImGui.Button("Verify with XIV Auth")) StartSignIn(CurrentSession);
        if (buttonsWidth <= available) ImGui.SameLine();
        if (ImGui.Button("Join on Patreon"))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PatreonMembershipUrl) { UseShellExecute = true });
        return true;
    }
}

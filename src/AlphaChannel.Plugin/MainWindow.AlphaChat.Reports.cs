using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string? alphaAdminNotice;
    private AlphaChatMessage? alphaReportMessage;
    private string? alphaReportAccount;
    private int alphaReportReason;
    private string alphaReportNote = "";
    private string? alphaReportResult;
    private bool alphaReportBusy, alphaReportSubmitted;
    private static readonly string[] AlphaReportReasons = ["Harmful language", "Hate speech", "Personal details", "Spam", "Other"];

    private void RequestAlphaChatReport(long sequence)
    {
        alphaReportMessage = alphaChatMessages.FirstOrDefault(m => m.Sequence == sequence);
        alphaReportAccount = CurrentSession?.AccountId;
        alphaReportNote = ""; alphaReportReason = 0; alphaReportResult = null; alphaReportSubmitted = false;
    }

    private async void SubmitAlphaChatReport()
    {
        if (CurrentSession is not { } session || alphaReportMessage is not { } message || alphaReportBusy) return;
        alphaReportBusy = true;
        var reason = AlphaReportReasons[alphaReportReason]; var note = alphaReportNote;
        string result; var submitted=false;
        try
        {
            await AlphaChatAdminApi.RequestAsync(session.Token, "/reports", new { sequence = message.Sequence, evidence = message.Evidence, reason, note });
            result = "Report submitted. Thank you."; submitted=true;
        }
        catch (Exception) { result = "Could not submit the report. Please retry. Older messages from before this update may no longer be reportable."; }
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (Volatile.Read(ref disposeStarted) != 0) return;
            alphaReportBusy = false;
            if (CurrentSession?.AccountId == session.AccountId && alphaReportMessage == message) { alphaReportResult = result; alphaReportSubmitted=submitted; }
        });
    }

    private void DrawAlphaChatReportOverlay()
    {
        if (alphaAdminNotice is not null) {
            DrawAdminModal("Alpha Chat notice", () => { ImGui.TextWrapped(alphaAdminNotice); if (ActivePartyButton("Close", UiVec(120,36))) alphaAdminNotice=null; }); return;
        }
        if (alphaReportMessage is not { } message) return;
        if (CurrentSession?.AccountId != alphaReportAccount) { alphaReportMessage = null; return; }
        DrawAdminModal("Report message", () =>
        {
            DrawUsername(message.Sender.AccountId,message.Sender.DisplayName,Vector4.One);
            using (ImRaii.PushColor(ImGuiCol.Text, MutedText)) ImGui.TextWrapped(AlphaChatReplyPreview(message));
            ImGui.Spacing();
            using (UiDisabled.Scope(alphaReportBusy))
            {
                ImGui.SetNextItemWidth(-1);
                if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("Reason", AlphaReportReasons[alphaReportReason])))
                { for (var i = 0; i < AlphaReportReasons.Length; i++) if (ImGui.Selectable(AlphaReportReasons[i], i == alphaReportReason)) alphaReportReason = i; ImGui.EndCombo(); }
                ImGui.TextUnformatted("Additional details (optional)");
                ImGui.InputTextMultiline("##reportNote", ref alphaReportNote, 500, new Vector2(-1, Ui(80f)));
                ImGui.TextColored(MutedText, "The message and your report will be shared with authorised admins.");
                if (alphaReportResult is { } result) ImGui.TextWrapped(result);
                using (UiDisabled.Scope(alphaReportSubmitted))
                    if (ActivePartyButton(alphaReportBusy ? "Submitting…" : "Submit report", UiVec(140f, 36f), primary: true)) SubmitAlphaChatReport();
                ImGui.SameLine();
                if (ActivePartyButton("Close", UiVec(100f, 36f))) alphaReportMessage = null;
            }
        });
    }

    private void DrawAdminModal(string title, Action body)
    {
        var parent = ImGui.GetWindowPos(); var parentSize = ImGui.GetWindowSize();
        var size = Vector2.Min(UiVec(560f, 420f), parentSize - UiVec(24f, 24f));
        ImGui.SetNextWindowPos(parent); ImGui.SetNextWindowSize(parentSize); ImGui.SetNextWindowBgAlpha(0);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##adminModal", flags)) { ImGui.End(); return; }
        ImGui.GetWindowDrawList().AddRectFilled(parent, parent + parentSize, ImGui.GetColorU32(new Vector4(0, 0, 0, .58f)));
        ImGui.SetCursorScreenPos(parent + (parentSize - size) * .5f);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(12f)).Push(ImGuiStyleVar.WindowPadding, UiVec(20f, 18f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, Accent))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##adminModalPanel", size, true, ImGuiWindowFlags.AlwaysUseWindowPadding)))
            if (panel) { ImGui.TextColored(AccentHover, title); ImGui.Separator(); ImGui.Spacing(); body(); }
        ImGui.End();
    }
}

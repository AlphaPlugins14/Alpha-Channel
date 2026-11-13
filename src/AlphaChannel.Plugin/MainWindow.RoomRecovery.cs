using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string? recoveryPromptOpenedFor;

    internal void TickRoomRecovery()
    {
        if (stream.TakeLeaveAcknowledgement()) stream.CommitPartyLeaveAcknowledgement();
        if (stream.TakeRecoveryNotice())
        {
            // Preserve membership and chat. Playback/activity teardown never calls LeaveStream.
            StopMediaForRestrictedContent("The host disconnected. Waiting for them to resume the Watch Party.");
            video.Stop();
            queue.Clear();
            if (stream.RecoveryOffer is not null)
            {
                OpenUi();
                SetMinimized(false);
            }
            else if (stream.WaitingForHost)
                Plugin.ChatGui.Print("[Alpha Channel] Host disconnected. Your Watch Party will stay open for up to five minutes while they return.");
        }
        if (stream.RecoveryOffer is { } offer && recoveryPromptOpenedFor != offer.RecoveryId)
        {
            recoveryPromptOpenedFor = offer.RecoveryId;
            OpenUi();
            SetMinimized(false);
        }
        if (stream.RecoveryOffer is null) recoveryPromptOpenedFor = null;
    }

    private void DrawRoomRecoveryPrompt()
    {
        if (stream.RecoveryOffer is not { } offer) return;
        var origin = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var panelSize = new Vector2(MathF.Min(Ui(470f), size.X - Ui(32f)), Ui(240f));
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##roomRecoveryPrompt", flags)) { ImGui.End(); return; }
        ImGui.GetWindowDrawList().AddRectFilled(origin, origin + size,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.4f)));
        ImGui.SetCursorScreenPos(origin + (size - panelSize) * 0.5f);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(24f, 20f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, Accent))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##roomRecoveryPanel", panelSize, true)))
        {
            if (panel)
            {
                ImGui.TextColored(Accent, "Resume your Watch Party?");
                ImGui.Spacing();
                ImGui.TextWrapped("Your Watch Party is still open. Would you like to resume it? Choosing No closes the room for everyone.");
                ImGui.Spacing();
                var seconds = Math.Max(0, offer.ExpiresAtUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                ImGui.TextColored(MutedText, $"Time remaining: {seconds / 60}:{seconds % 60:00}");
                ImGui.Spacing();
                using (UiDisabled.Scope(!stream.IsConnected || stream.RecoveryDecisionBusy || seconds == 0))
                {
                    using (ImRaii.PushColor(ImGuiCol.Button, Accent).Push(ImGuiCol.ButtonHovered, AccentHover))
                        if (ImGui.Button("Yes, resume", UiVec(150f, 36f))) _ = stream.DecideRoomRecoveryAsync(true);
                    ImGui.SameLine();
                    if (ImGui.Button("No, close party", UiVec(150f, 36f)))
                    {
                        stream.RecordPartyLeaveIntent();
                        _ = stream.DecideRoomRecoveryAsync(false);
                    }
                }
            }
        }
        ImGui.End();
    }
}

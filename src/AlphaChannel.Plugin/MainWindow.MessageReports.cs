using AlphaChannel.Contracts;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private MessageDto? pendingDmReport;
    private string? pendingDmReportPlaintext;
    private PartyChatItem? pendingPartyReport;
    private bool pendingMessageReportFromMiniChat;

    private void QueueDmMessageReport(MessageDto message, string plaintext)
    {
        pendingPartyReport = null;
        pendingMessageReportFromMiniChat = false;
        pendingDmReport = message;
        pendingDmReportPlaintext = plaintext;
    }

    private void QueuePartyMessageReport(PartyChatItem item, bool fromMiniChat = false)
    {
        pendingDmReport = null;
        pendingDmReportPlaintext = null;
        pendingPartyReport = item;
        pendingMessageReportFromMiniChat = fromMiniChat;
    }

    private void ClearPendingMessageReport()
    {
        pendingDmReport = null;
        pendingDmReportPlaintext = null;
        pendingPartyReport = null;
        pendingMessageReportFromMiniChat = false;
    }

    private void DrawMessageReportConfirmationOverlay(bool miniChat = false)
    {
        if (pendingMessageReportFromMiniChat != miniChat)
            return;
        if (pendingDmReport is null && pendingPartyReport is null)
            return;
        if (CurrentSession is not { } session)
        {
            ClearPendingMessageReport();
            return;
        }

        var parentPosition = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var panelSize = new Vector2(
            MathF.Min(Ui(450f), parentSize.X - Ui(36f)),
            MathF.Min(Ui(255f), parentSize.Y - Ui(36f)));
        var panelPosition = parentPosition + (parentSize - panelSize) * 0.5f;

        ImGui.SetNextWindowPos(parentPosition, ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        const ImGuiWindowFlags overlayFlags = ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##messageReportConfirmationOverlay", overlayFlags))
        {
            ImGui.End();
            return;
        }

        ImGui.GetWindowDrawList().AddRectFilled(parentPosition, parentPosition + parentSize,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.62f)));
        ImGui.SetCursorScreenPos(panelPosition);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildBorderSize, Ui(1f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(22f, 20f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(8f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.035f, 0.043f, 0.074f, 0.995f)))
        using (ImRaii.PushColor(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.78f)))
        {
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##messageReportConfirmationPanel", panelSize, true,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
            {
                using var uiFontScale3548=UiFonts.PushRelative(1.15f);
                ImGui.TextColored(Vector4.One, "Report this message?");
                uiFontScale3548.Dispose();
                ImGui.Dummy(UiVec(0f, 8f));
                var imageReport = pendingDmReport is not null && Auth.GroupChatClient.ImageContent(pendingDmReportPlaintext) is not null;
                ImGui.TextWrapped(imageReport ? "The image and this message will be shared encrypted with moderators. Reported images are retained until the report is resolved or a moderator deletes the image." : "The message text and sender details will be sent to moderators for review.");
                ImGui.Dummy(UiVec(0f, 7f));
                var preview = pendingPartyReport?.Text ?? pendingDmReportPlaintext ?? string.Empty;
                if (imageReport) preview = Auth.GroupChatClient.ImageCaption(preview);
                preview = ExpandChatEmojiShortcodes(preview).Replace('\r', ' ').Replace('\n', ' ');
                if (preview.Length > 105)
                    preview = preview[..105] + "…";
                ImGui.TextColored(MutedText, $"“{preview}”");

                var buttonY = ImGui.GetWindowPos().Y + panelSize.Y - Ui(65f);
                var buttonX = ImGui.GetWindowPos().X + ImGui.GetWindowSize().X - Ui(22f + 232f);
                ImGui.SetCursorScreenPos(new Vector2(buttonX, buttonY));
                if (ImGui.Button("Cancel##messageReport", UiVec(108f, 36f)))
                    ClearPendingMessageReport();
                ImGui.SameLine(0f, Ui(8f));
                using (ImRaii.PushColor(ImGuiCol.Button, Danger)
                           .Push(ImGuiCol.ButtonHovered, new Vector4(0.86f, 0.29f, 0.36f, 1f))
                           .Push(ImGuiCol.ButtonActive, new Vector4(0.68f, 0.17f, 0.24f, 1f)))
                {
                    if (ImGui.Button("Report##confirmMessageReport", UiVec(116f, 36f)))
                    {
                        if (pendingDmReport is { } dm && pendingDmReportPlaintext is { } plaintext)
                        {
                            // The encrypted DM report includes its original plaintext and franking key.
                            ReportMessage(session, dm, plaintext);
                        }
                        else if (pendingPartyReport is { } party)
                        {
                            // Watch Party messages are plaintext; they have no DM franking key.
                            var reportMessageId = party.HasSharedMessageId ? party.Id.ToString() : null;
                            var senderId = Guid.TryParse(party.UserId, out _) ? party.UserId : null;
                            _ = Task.Run(() => reportClient.SubmitAsync(session.Token, "harassment",
                                $"Watch Party chat message from {party.Name}", senderId,
                                reportMessageId, party.Text, null));
                        }
                        ClearPendingMessageReport();
                    }
                }
            }
            ImGui.EndChild();
        }

        ImGui.End();
    }
}

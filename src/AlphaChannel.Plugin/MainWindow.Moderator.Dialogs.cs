using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawModeratorActionOverlay()
    {
        if (moderatorAction is not { } action) return;
        if (!ModeratorApproved || currentPage != HomePage.Moderator) { moderatorAction = null; return; }
        var root = ImGuiP.GetCurrentWindow().RootWindow; var origin = root.Pos; var room = root.Size;
        var desired = UiVec(530, action == "event-edit" ? 630 : action is "controls" or "group-controls" ? 510 : action is "mute" or "kick" ? 490 : 410);
        var size = Vector2.Min(desired, Vector2.Max(UiVec(1, 1), room - UiVec(36, 36)));
        var position = origin + (room - size) / 2;
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always); ImGui.SetNextWindowSize(room, ImGuiCond.Always);
        ImGui.SetNextWindowViewport(root.ViewportId); ImGui.SetNextWindowBgAlpha(0);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (!BeginWindowOverlay("##moderatorActionOverlay", flags)) { ImGui.End(); return; }
        try
        {
            ImGui.GetWindowDrawList().AddRectFilled(origin, origin + room, ImGui.GetColorU32(new Vector4(0, 0, 0, .62f)));
            if (!moderatorBusy && ImGui.IsKeyPressed(ImGuiKey.Escape)) { moderatorAction = null; return; }
            ImGui.SetCursorScreenPos(position);
            using var styles = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(13)).Push(ImGuiStyleVar.ChildBorderSize, Ui(1))
                .Push(ImGuiStyleVar.WindowPadding, UiVec(22, 20));
            using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, Accent with { W = .45f });
            using var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##moderatorActionPanel", size, true, ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar));
            if (!panel) return;
            var title = action switch
            {
                "mute" => "Mute user", "kick" => "Temporarily exclude user", "unban" => "Lift exclusion", "unmute" => "Lift mute", "warn" => "Warn user",
                "delete" or "group-delete" => "Remove message", "event-edit" => "Edit event listing", "event-remove" => "Remove event",
                "controls" => "Alpha Chat controls", "group-controls" => "Public group controls", _ => "Moderate"
            };
            ModeratorHeading(title, action is "delete" or "group-delete" or "event-remove" ? FontAwesomeIcon.Trash : FontAwesomeIcon.UserShield);
            var bodyHeight = MathF.Max(Ui(60), ImGui.GetContentRegionAvail().Y - Ui(60));
            using (var form = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##moderatorActionForm", new(0, bodyHeight), false)))
            if (form)
            {
                using var disabled = UiDisabled.Scope(moderatorBusy);
                if (moderatorTarget is not null) { ImGui.TextUnformatted(ModName(moderatorTarget)); ImGui.Spacing(); }
                if (action is "mute" or "kick")
                {
                    ImGui.TextColored(MutedText, "Duration");
                    var preset = moderatorMinutes == 15 ? 0 : moderatorMinutes == 60 ? 1 : moderatorMinutes == 1440 ? 2 : -1;
                    var previous = preset; EventSegments("modDuration", ["15 minutes", "1 hour", "24 hours"], ref preset, ImGui.GetContentRegionAvail().X);
                    if (preset != previous && preset >= 0) moderatorMinutes = new[] { 15, 60, 1440 }[preset];
                    ImGui.SetNextItemWidth(Ui(150)); ImGui.InputInt("Minutes", ref moderatorMinutes); moderatorMinutes = Math.Clamp(moderatorMinutes, 1, 1440);
                }
                if (action == "event-edit")
                {
                    ImGui.TextColored(MutedText, "Title"); ImGui.SetNextItemWidth(-1); ImGui.InputText("##modEventTitle", ref moderatorEventTitle, 100);
                    ImGui.TextColored(MutedText, "Description"); ImGui.InputTextMultiline("##modEventDescription", ref moderatorEventDescription, 1500, new(-1, Ui(140)));
                }
                if (action == "event-remove")
                {
                    ImGui.TextWrapped("The event and its attendance list will be removed. This cannot be undone.");
                    if (moderatorEvent?.SeriesId is not null) ImGui.Checkbox("Also remove future occurrences", ref moderatorFuture);
                }
                if (action is "controls" or "group-controls")
                {
                    ImGui.TextColored(MutedText, "Slow mode · seconds between messages");
                    ImGui.SetNextItemWidth(Ui(165)); ImGui.InputInt("##modSlow", ref moderatorSlow); moderatorSlow = Math.Clamp(moderatorSlow, 0, 300);
                    using (UiFonts.PushRelative(.87f)) ImGui.TextColored(MutedText, "0 turns slow mode off.");
                    ImGui.Spacing();
                    if (action == "controls") { ImGui.Checkbox("Allow posting", ref moderatorPosting); ImGui.Checkbox("Allow watch-party sharing", ref moderatorSharing); }
                    else ImGui.Checkbox("Lock new members from joining", ref moderatorJoinLocked);
                }
                ImGui.Spacing(); ImGui.TextColored(MutedText, action == "warn" ? "Warning message" : "Reason");
                ImGui.InputTextMultiline("##modReason", ref moderatorReason, 500, new(-1, Ui(85)));
                if (moderatorError is not null) ImGui.TextWrapped(moderatorError);
            }
            ImGui.Spacing();
            using (UiDisabled.Scope(moderatorBusy))
            {
                var w = (ImGui.GetContentRegionAvail().X - Ui(12)) / 2;
                if (ModeratorButton("cancelAction", "Cancel", FontAwesomeIcon.Times, w)) moderatorAction = null;
                ImGui.SameLine(0, Ui(12));
                using (UiDisabled.Scope(string.IsNullOrWhiteSpace(moderatorReason)))
                    if (ModeratorButton("confirmAction", "Confirm", FontAwesomeIcon.Check, w, primary: true, danger: action is "delete" or "group-delete" or "event-remove" or "kick")) SubmitModeratorAction();
            }
        }
        finally { ImGui.End(); }
    }
}

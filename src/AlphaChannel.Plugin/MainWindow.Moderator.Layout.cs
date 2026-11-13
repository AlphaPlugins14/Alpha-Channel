using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Text.Json;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly Vector4 ModeratorAmber = new(1f, .70f, .29f, 1f);
    private bool ModeratorWide => ImGui.GetContentRegionAvail().X >= Ui(620);

    private void ModeratorCard(string id, float height, Action draw, float width = 0, bool scroll = false)
    {
        using var styles = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1)).Push(ImGuiStyleVar.WindowPadding, UiVec(16, 14));
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        var flags = ImGuiWindowFlags.AlwaysUseWindowPadding;
        if (!scroll) flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        using var child = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##moderator" + id, new(width, height), true, flags));
        if (child) draw();
    }

    private void ModeratorHeading(string title, FontAwesomeIcon icon, Vector4? colour = null)
    {
        var origin = ImGui.GetCursorScreenPos();
        UiFonts.DrawCenteredIcon(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), origin + UiVec(8, 9),
            ImGui.GetColorU32(colour ?? AccentHover), icon.ToIconString());
        ImGui.Dummy(UiVec(18, 19)); ImGui.SameLine(0, Ui(7));
        using (UiFonts.PushRelative(1.03f, true)) ImGui.TextUnformatted(title);
        ImGui.Spacing();
    }

    private bool ModeratorButton(string id, string label, FontAwesomeIcon icon, float width = 0, bool primary = false, bool danger = false)
    {
        var w = width > 0 ? width : ImGui.CalcTextSize(label).X + Ui(label.Length == 0 ? 34 : 48);
        return AlphaChatButton("mod" + id, label, icon, w, Ui(34), primary: primary && !danger, danger: danger);
    }

    private void ModeratorBadge(string label, Vector4 colour)
    {
        var p = ImGui.GetCursorScreenPos(); var size = ImGui.CalcTextSize(label) + UiVec(16, 8);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(p, p + size, ImGui.GetColorU32(colour with { W = .13f }), Ui(6));
        draw.AddText(p + UiVec(8, 4), ImGui.GetColorU32(colour), label);
        ImGui.Dummy(size);
    }

    private static Vector4 ModeratorStatusColour(string status) => status switch
    { "Open" => ModeratorAmber, "Under review" => AccentHover, "Resolved" => Good, "Escalated" => Danger, _ => MutedText };

    private void ModeratorEmpty(FontAwesomeIcon icon, string title, string detail, float height = 138)
    {
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        var center = origin + new Vector2(width * .5f, Ui(29)); var draw = ImGui.GetWindowDrawList();
        draw.AddCircleFilled(center, Ui(23), ImGui.GetColorU32(Accent with { W = .10f }));
        UiFonts.DrawCenteredIcon(draw, Ui(22), center, ImGui.GetColorU32(AccentHover), icon.ToIconString());
        ImGui.SetCursorScreenPos(origin + new Vector2(MathF.Max(0, (width - ImGui.CalcTextSize(title).X) / 2), Ui(62)));
        ImGui.TextUnformatted(title);
        using (UiFonts.PushRelative(.88f))
        {
            var textWidth = MathF.Min(width, Ui(400));
            var lines = ImGui.CalcTextSize(detail, false, textWidth);
            ImGui.SetCursorScreenPos(origin + new Vector2(MathF.Max(0, (width - MathF.Min(lines.X, textWidth)) / 2), Ui(89)));
            using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + textWidth)) ImGui.TextColored(MutedText, detail);
        }
        ImGui.SetCursorScreenPos(origin); ImGui.Dummy(new(width, Ui(height)));
    }

    private float ModeratorWrappedHeight(string text, float width) =>
        MathF.Max(ImGui.GetTextLineHeight(), ImGui.CalcTextSize(text, false, MathF.Max(Ui(60), width)).Y);

    private void ModeratorSearch(string id, string hint, ref string value, float width = -1)
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(12, 9));
        using var border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, Ui(1));
        using var colours = ImRaii.PushColor(ImGuiCol.FrameBg, FrameBg).Push(ImGuiCol.Border, BorderSubtle);
        ImGui.SetNextItemWidth(width);
        ImGui.InputTextWithHint("##modSearch" + id, hint, ref value, 100);
    }

    private void DrawModeratorNavigation()
    {
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        var logWidth = Ui(109); var tabsWidth = width - logWidth - Ui(12); var cell = tabsWidth / 5;
        var draw = ImGui.GetWindowDrawList(); var height = Ui(44);
        draw.AddLine(origin + new Vector2(0, height), origin + new Vector2(width, height), ImGui.GetColorU32(BorderSubtle));
        for (var i = 0; i < 5; i++)
        {
            var p = origin + new Vector2(cell * i, 0); var size = new Vector2(cell, height);
            ImGui.SetCursorScreenPos(p);
            if (ImGui.InvisibleButton("##moderatorTab" + i, size) && moderatorTab != i && !moderatorBusy) SelectModeratorTab(i);
            var hovered = ImGui.IsItemHovered();
            if (hovered) { ImGui.SetMouseCursor(ImGuiMouseCursor.Hand); draw.AddRectFilled(p, p + size, ImGui.GetColorU32(Accent with { W = .07f }), Ui(5)); }
            var label = i == 1 && ModeratorOpenReports > 0 ? $"Reports ({ModeratorOpenReports})" : ModeratorTabs[i];
            var text = ImGui.CalcTextSize(label);
            draw.AddText(p + new Vector2((cell - text.X) / 2, (height - text.Y) / 2),
                ImGui.GetColorU32(moderatorTab == i ? AccentHover : hovered ? Vector4.One : MutedText), label);
            if (moderatorTab == i) draw.AddRectFilled(p + new Vector2(0, height - Ui(3)), p + size, ImGui.GetColorU32(Accent));
        }
        ImGui.SetCursorScreenPos(origin + new Vector2(width - logWidth, Ui(5)));
        if (ModeratorButton("logTab", "Activity log", FontAwesomeIcon.ClipboardList, logWidth, primary: moderatorTab == 5) && moderatorTab != 5) SelectModeratorTab(5);
        ImGui.SetCursorScreenPos(origin); ImGui.Dummy(new(width, height));
        var toolbar = ImGui.GetCursorScreenPos();
        var updated = moderatorBusy ? "Updating…" : moderatorUpdated == default ? "Ready" : "Updated " + moderatorUpdated.ToString("HH:mm");
        ImGui.SetCursorScreenPos(toolbar + new Vector2(MathF.Max(0, width - Ui(44) - ImGui.CalcTextSize(updated).X - Ui(12)), Ui(8)));
        ImGui.TextColored(MutedText, updated);
        ImGui.SetCursorScreenPos(toolbar + new Vector2(width - Ui(36), 0));
        using (UiDisabled.Scope(moderatorBusy)) if (ModeratorButton("refresh", "", FontAwesomeIcon.SyncAlt, Ui(36))) RefreshModeratorPanel();
        ImGui.SetCursorScreenPos(toolbar); ImGui.Dummy(new(width, Ui(38)));
    }

    private void SelectModeratorTab(int tab)
    {
        moderatorTab = tab; moderatorSearch = ""; moderatorSelectedReport = moderatorSelectedUser = null;
        moderatorAction = null; moderatorEvent = null; moderatorGroup = null; moderatorNote = "";
        RefreshModeratorPanel();
    }

    private void DrawModeratorPanel()
    {
        CheckModeratorAccess();
        using var styles = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(10, 12))
            .Push(ImGuiStyleVar.FrameRounding, Ui(7)).Push(ImGuiStyleVar.FramePadding, UiVec(10, 7))
            .Push(ImGuiStyleVar.PopupBorderSize, Ui(1)).Push(ImGuiStyleVar.PopupRounding, Ui(8));
        using var colours = ImRaii.PushColor(ImGuiCol.Border, BorderSubtle).Push(ImGuiCol.FrameBg, FrameBg)
            .Push(ImGuiCol.FrameBgHovered, CardBgHover).Push(ImGuiCol.CheckMark, AccentHover)
            .Push(ImGuiCol.Header, Accent with { W = .20f }).Push(ImGuiCol.HeaderHovered, Accent with { W = .13f });
        if (!ModeratorApproved)
        {
            ModeratorCard("access", Ui(210), () => ModeratorEmpty(FontAwesomeIcon.UserShield,
                moderatorChecking ? "Checking moderator access…" : "Moderator access unavailable",
                "Sign in with an account that has moderator access."));
            return;
        }
        using (UiDisabled.Scope(moderatorBusy || moderatorAction is not null)) DrawModeratorNavigation();
        if (moderatorError is not null) ImGui.TextWrapped(moderatorError);
        if (moderatorTab == 1 && cloudGroupError is not null) ImGui.TextWrapped(cloudGroupError);
        using (var body = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##moderatorBody", Vector2.Zero, false)))
        if (body)
        using (UiDisabled.Scope(moderatorBusy || moderatorAction is not null))
        {
            switch (moderatorTab)
            {
                case 0: DrawModeratorOverview(); break;
                case 1: DrawModeratorReports(); break;
                case 2: DrawModeratorUsers(); break;
                case 3: DrawModeratorChats(); break;
                case 4: DrawModeratorEvents(); break;
                case 5: DrawModeratorLog(); break;
            }
        }
        DrawModeratorActionOverlay();
    }

    private void ModeratorMetric(string id, string label, string value, FontAwesomeIcon icon, float width, Vector4? tint = null)
    {
        ModeratorCard(id, Ui(88), () =>
        {
            var p = ImGui.GetCursorScreenPos(); var room = ImGui.GetContentRegionAvail().X;
            ImGui.TextColored(MutedText, label);
            using (UiFonts.PushRelative(1.65f, true)) ImGui.TextUnformatted(value);
            UiFonts.DrawCenteredIcon(ImGui.GetWindowDrawList(), Ui(26), p + new Vector2(room - Ui(16), Ui(28)),
                ImGui.GetColorU32(tint ?? AccentHover), icon.ToIconString());
        }, width);
    }

    private void DrawModeratorOverview()
    {
        var width = ImGui.GetContentRegionAvail().X; var half = (width - Ui(12)) / 2;
        ModeratorMetric("open", "Open reports", ModeratorOpenReports.ToString(), FontAwesomeIcon.Flag, half, ModeratorOpenReports > 0 ? ModeratorAmber : AccentHover);
        ImGui.SameLine(0, Ui(12));
        ModeratorMetric("people", "People in Alpha Chat", (ModNumber(moderatorData, "active") + ModNumber(moderatorData, "background")).ToString(), FontAwesomeIcon.Comments, half);
        ModeratorMetric("parties", "Public watch parties", moderatorData.ValueKind == JsonValueKind.Object && moderatorData.TryGetProperty("rooms", out var rooms) ? rooms.GetArrayLength().ToString() : "—", FontAwesomeIcon.Users, half);
        ImGui.SameLine(0, Ui(12));
        ModeratorMetric("events", "Upcoming events", moderatorEvents.Count(e => !e.Cancelled && e.EndAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString(), FontAwesomeIcon.CalendarAlt, half);
        var pending = ModeratorReportItems().Where(r => r.Status is "Open" or "Under review").Take(3).ToArray();
        ModeratorCard("attention", pending.Length == 0 ? Ui(224) : Ui(112 + pending.Length * 72), () =>
        {
            ModeratorHeading("Needs attention", FontAwesomeIcon.Flag);
            if (pending.Length == 0) ModeratorEmpty(FontAwesomeIcon.CheckCircle, "You're all caught up", "New reports will appear here for review.", 105);
            else foreach (var report in pending) DrawModeratorReportRow(report, compact: true);
            if (ModeratorButton("reviewReports", "Review reports", FontAwesomeIcon.ArrowRight, ImGui.GetContentRegionAvail().X, primary: true)) SelectModeratorTab(1);
        });
        var sideBySide = ModeratorWide;
        var panelWidth = sideBySide ? half : width;
        ModeratorCard("activity", Ui(202), () =>
        {
            ModeratorHeading("Community activity", FontAwesomeIcon.ChartBar);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##modPeriod", moderatorDays == 1 ? "Today" : $"Last {moderatorDays} days"))
            {
                foreach (var days in new[] { 1, 7, 30 }) if (ImGui.Selectable(days == 1 ? "Today" : $"Last {days} days", days == moderatorDays))
                { moderatorDays = days; RefreshModeratorPanel(); }
                ImGui.EndCombo();
            }
            ImGui.Spacing();
            ModeratorValueRow("Messages sent", ModRows("daily").Where(d => ModText(d, "metric") == "messages").Sum(d => ModNumber(d, "value")).ToString());
            ModeratorValueRow("Parties started", ModRows("daily").Where(d => ModText(d, "metric") == "Broadcast started").Sum(d => ModNumber(d, "value")).ToString());
        }, panelWidth);
        if (sideBySide) ImGui.SameLine(0, Ui(12));
        ModeratorCard("recent", Ui(202), () =>
        {
            ModeratorHeading("Recent actions", FontAwesomeIcon.History);
            var rows = ModRows("audit").Take(2).ToArray();
            if (rows.Length == 0) ModeratorEmpty(FontAwesomeIcon.ClipboardCheck, "No recent actions", "Moderator actions will appear here.", 124);
            else foreach (var row in rows) DrawModeratorAuditRow(row, compact: true);
        }, panelWidth);
    }

    private void ModeratorValueRow(string label, string value)
    {
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        ImGui.TextColored(MutedText, label);
        ImGui.SameLine(); ImGui.SetCursorScreenPos(origin + new Vector2(width - ImGui.CalcTextSize(value).X, 0));
        ImGui.TextUnformatted(value);
    }

    private void DrawModeratorAuditRow(JsonElement row, bool compact = false)
    {
        var action = ModeratorActionLabel(ModText(row, "action"));
        using (UiFonts.PushRelative(.93f, true)) ImGui.TextWrapped(ModName(ModText(row, "actor")) + " · " + action);
        using (UiFonts.PushRelative(.85f))
        {
            ImGui.TextColored(MutedText, ModTime(ModNumber(row, "at")));
            if (!compact && ModText(row, "target").Length > 0) ImGui.TextWrapped(ModName(ModText(row, "target")));
        }
        if (!compact && ModText(row, "reason").Length > 0) ImGui.TextWrapped(ModText(row, "reason"));
        if (!compact) ImGui.Spacing();
    }

    private static string ModeratorActionLabel(string action) => action switch
    { "warn" => "Warning issued", "mute" => "User muted", "unmute" => "Mute lifted", "kick" => "User temporarily excluded", "unban" => "Exclusion lifted", "delete" => "Message removed", "controls" => "Chat controls updated", "resolve" => "Report reviewed", _ => action };

    private void DrawModeratorLog(int limit = 300)
    {
        ModeratorCard("logSearch", Ui(68), () => ModeratorSearch("log", "Search actions, people or reasons…", ref moderatorSearch));
        var rows = ModRows("audit").Where(r => (ModName(ModText(r, "actor")) + ModName(ModText(r, "target")) + ModeratorActionLabel(ModText(r, "action")) + ModText(r, "reason")).Contains(moderatorSearch, StringComparison.OrdinalIgnoreCase)).Take(limit).ToArray();
        if (rows.Length == 0)
        {
            ModeratorCard("logEmpty", Ui(236), () =>
            { ModeratorHeading("Activity log", FontAwesomeIcon.ClipboardList); ModeratorEmpty(FontAwesomeIcon.ClipboardCheck, "No actions to show", moderatorSearch.Length == 0 ? "Moderator actions will be recorded here." : "Try another name, action or reason."); });
            return;
        }
        foreach (var row in rows)
        {
            var h = Ui(126) + ModeratorWrappedHeight(ModText(row, "reason"), ImGui.GetContentRegionAvail().X - Ui(36));
            ModeratorCard("log" + ModText(row, "id"), h, () => DrawModeratorAuditRow(row));
        }
    }
}

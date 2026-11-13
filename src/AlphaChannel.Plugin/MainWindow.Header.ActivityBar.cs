using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private float HeaderBarSegmentWidth(string label) => ImGui.CalcTextSize(label).X + Ui(sharedHeaderLayout.Compact ? 32f : 48f);
    private float HeaderLeaveSegmentWidth => Ui(36f);
    private bool HeaderHasActionBar => stream.Mode is StreamMode.Hosting or StreamMode.Viewing || ShowHeaderEndActivity;
    private float HeaderActivityWidth => (stream.Mode is StreamMode.Hosting or StreamMode.Viewing
        ? HeaderPartyChipWidth + HeaderLeaveSegmentWidth : 0f) + (ShowHeaderEndActivity ? HeaderEndActivityWidth : 0f);
    private float HeaderActivityLeft => sharedHeaderLayout.ActionLeft(HeaderActivityWidth / Ui(1));

    private void DrawHeaderActivityBar()
    {
        var inParty = stream.Mode is StreamMode.Hosting or StreamMode.Viewing;
        if (!inParty && !ShowHeaderEndActivity) return;
        var origin = ImGui.GetCursorScreenPos();
        var partyWidth = inParty ? HeaderPartyChipWidth : 0f;
        var leaveWidth = inParty ? HeaderLeaveSegmentWidth : 0f;
        var width = partyWidth + leaveWidth + (ShowHeaderEndActivity ? HeaderEndActivityWidth : 0f);
        var size = new Vector2(width, Ui(30));
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(FrameBg), Ui(7));
        draw.AddRect(origin, origin + size,
            ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, .4f)), Ui(7));
        var x = origin.X;
        void Divider()
        {
            draw.AddLine(new Vector2(x, origin.Y + Ui(6)), new Vector2(x, origin.Y + Ui(24)),
                ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, .35f)));
        }
        if (inParty)
        {
            if (DrawHeaderBarSegment("party", HeaderPartyChipLabel, FontAwesomeIcon.Users, partyWidth,
                    AccentHover, "Open your Watch Party")) currentPage = HomePage.WatchAlong;
            x += partyWidth;
            Divider();
            ImGui.SetCursorScreenPos(new Vector2(x, origin.Y));
            if (DrawHeaderBarSegment("leave", "", FontAwesomeIcon.SignOutAlt, leaveWidth,
                    new Vector4(1f,.57f,.62f,1f), "Leave Watch Party", danger: true)) RequestLeaveWatchParty();
            x += leaveWidth;
            if (ShowHeaderEndActivity) Divider();
        }
        if (ShowHeaderEndActivity)
        {
            ImGui.SetCursorScreenPos(new Vector2(x, origin.Y));
            DrawHeaderEndActivity();
        }
    }

    private bool DrawHeaderBarSegment(string id, string label, FontAwesomeIcon icon, float width,
        Vector4 iconColour, string tooltip, bool danger = false, bool flashing = false)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, Ui(30));
        var clicked = ImGui.InvisibleButton("##headerBar" + id, size);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        if (hovered || flashing)
            draw.AddRectFilled(origin + Vector2.One, origin + size - Vector2.One,
                ImGui.GetColorU32(danger ? new Vector4(.4f,.08f,.14f,flashing ? .9f : .7f) : CardBgHover), Ui(5));
        var textSize = ImGui.CalcTextSize(label);
        var iconWidth = Ui(16);
        var labelGap = label.Length == 0 ? 0 : Ui(6);
        var startX = origin.X + (size.X - textSize.X - iconWidth - labelGap) * .5f;
        UiFonts.DrawCenteredIcon(draw, ImGui.GetFontSize(), new Vector2(startX + iconWidth * .5f, origin.Y + size.Y * .5f),
            ImGui.GetColorU32(iconColour), icon.ToIconString());
        draw.AddText(new Vector2(startX + iconWidth + labelGap, origin.Y + (size.Y - textSize.Y) * .5f),
            ImGui.GetColorU32(hovered || flashing ? Vector4.One : Hex(0xBDC9E5)), label);
        if (hovered) { ImGui.SetMouseCursor(ImGuiMouseCursor.Hand); UiFonts.SetTooltip(tooltip); }
        return clicked;
    }
}

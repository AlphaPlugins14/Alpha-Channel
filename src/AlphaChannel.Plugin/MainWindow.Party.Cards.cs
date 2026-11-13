using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawActivePartyCard(string id, Vector2 size, Action contents)
    {
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(12f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(20f, 18f))
            .Push(ImGuiStyleVar.ItemSpacing, UiVec(10f, 8f));
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
            .Push(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.55f));
        using var card = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(id, size, true,
            ImGuiWindowFlags.AlwaysUseWindowPadding |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse));
        if (card) contents();
    }

    private void ActivePartyPanelGap() =>
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Ui(12f) - ImGui.GetStyle().ItemSpacing.Y);

    private bool ActivePartyButton(string label, Vector2 size, bool primary = false, bool danger = false)
    {
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(8f))
            .Push(ImGuiStyleVar.FrameBorderSize, primary ? 0f : Ui(1f));
        using var colors = ImRaii.PushColor(ImGuiCol.Button,
                primary ? Accent : danger ? new Vector4(Danger.X, Danger.Y, Danger.Z, 0.22f) : FrameBg)
            .Push(ImGuiCol.ButtonHovered,
                primary ? AccentHover : danger ? new Vector4(Danger.X, Danger.Y, Danger.Z, 0.38f) : FrameBgHover)
            .Push(ImGuiCol.ButtonActive, danger ? Danger : AccentActive)
            .Push(ImGuiCol.Border, danger
                ? new Vector4(Danger.X, Danger.Y, Danger.Z, 0.4f)
                : new Vector4(Accent.X, Accent.Y, Accent.Z, 0.5f));
        return ImGui.Button(label, size);
    }

    private void DrawPartyStatusDot(Vector4 colour)
    {
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(UiVec(9, 16));
        ImGui.GetWindowDrawList().AddCircleFilled(origin + UiVec(4.5f, 8), Ui(4.5f), ImGui.GetColorU32(colour), 16);
    }

    private bool PartyLabeledButton(string id, string label, FontAwesomeIcon icon, Vector2 size, bool primary = false)
    {
        var origin = ImGui.GetCursorScreenPos();
        var clicked = ActivePartyButton("##" + id, size, primary);
        var textSize = ImGui.CalcTextSize(label);
        var glyph = icon.ToIconString();
        Vector2 iconSize;
        using (UiFonts.PushIcon()) iconSize = ImGui.CalcTextSize(glyph);
        var x = origin.X + (size.X - textSize.X - iconSize.X - Ui(8)) * .5f;
        var draw = ImGui.GetWindowDrawList();
        var ink = ImGui.GetColorU32(ImGuiCol.Text);
        using (UiFonts.PushIcon()) UiFonts.DrawText(draw, ImGui.GetFontSize(), new(x, origin.Y + (size.Y - iconSize.Y) / 2), ink, glyph);
        UiFonts.DrawText(draw, ImGui.GetFontSize(), new(x + iconSize.X + Ui(8), origin.Y + (size.Y - textSize.Y) / 2), ink, label);
        return clicked;
    }

    private bool PartyIconButton(string id, FontAwesomeIcon icon, Vector2 size, bool framed = false)
    {
        var origin = ImGui.GetCursorScreenPos();
        var clicked = framed ? ActivePartyButton("##" + id, size) : ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        if (!framed && hovered) ImGui.GetWindowDrawList().AddRectFilled(origin, origin + size, ImGui.GetColorU32(FrameBgHover), Ui(6));
        using var font = UiFonts.PushIcon();
        var glyph = icon.ToIconString(); var glyphSize = ImGui.CalcTextSize(glyph);
        UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), origin + (size - glyphSize) / 2,
            ImGui.GetColorU32(hovered ? AccentHover : MutedText), glyph);
        return clicked;
    }

}

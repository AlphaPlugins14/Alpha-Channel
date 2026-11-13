using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private Vector2 partyPopupRootPosition, partyPopupRootSize;
    private uint partyPopupRootViewport;

    private void CapturePartyPopupBounds()
    {
        var root = ImGuiP.GetCurrentWindow().RootWindow;
        if ((root.Flags & ImGuiWindowFlags.Popup) != 0) return;
        partyPopupRootPosition = root.Pos; partyPopupRootSize = root.Size; partyPopupRootViewport = root.ViewportId;
    }

    private bool BeginPartyPopup(string id, Vector2 desiredSize, bool centered = false)
    {
        if (!ImGui.IsPopupOpen(id)) return false;
        CapturePartyPopupBounds();
        var padding = Ui(12);
        var minimum = partyPopupRootPosition + new Vector2(padding);
        var room = Vector2.Max(new(1), partyPopupRootSize - new Vector2(padding * 2));
        var size = Vector2.Min(desiredSize, room);
        var maximum = minimum + room - size;
        var anchor = ImGui.GetItemRectMin();
        var position = centered ? partyPopupRootPosition + (partyPopupRootSize - size) / 2
            : new Vector2(anchor.X, ImGui.GetItemRectMax().Y + Ui(6));
        if (!centered && position.Y + size.Y > minimum.Y + room.Y) position.Y = anchor.Y - size.Y - Ui(6);
        ImGui.SetNextWindowPos(Vector2.Clamp(position, minimum, maximum), ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowViewport(partyPopupRootViewport);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.PopupBorderSize, Ui(1))
            .Push(ImGuiStyleVar.PopupRounding, Ui(9)).Push(ImGuiStyleVar.WindowPadding, UiVec(12, 10));
        using var colours = ImRaii.PushColor(ImGuiCol.PopupBg, CardBg).Push(ImGuiCol.Border, Accent);
        if (!ImGui.BeginPopup(id, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings)) return false;
        // Full-window overlays also raise their existing popups. Restore the
        // submission order here so a nested selector stays above its dialog.
        ImGuiP.BringWindowToDisplayFront(ImGuiP.GetCurrentWindow());
        return true;
    }

    private Vector2 PartyMenuSize(int rows, float width = 250) =>
        new(Ui(width), Ui(24) + rows * (ImGui.GetTextLineHeight() + Ui(10)));

    private float PartyActionRowHeight => MathF.Max(Ui(34), ImGui.GetTextLineHeight() + Ui(14));

    private bool PartyActionMenuItem(string label, FontAwesomeIcon icon, bool enabled = true, bool selected = false)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, PartyActionRowHeight);
        using var disabled = UiDisabled.Scope(!enabled);
        var clicked = ImGui.Selectable("##action" + label, selected, ImGuiSelectableFlags.None, size);
        var draw = ImGui.GetWindowDrawList();
        draw.AddLine(origin + new Vector2(Ui(34), size.Y), origin + size,
            ImGui.GetColorU32(BorderSubtle));
        var textY = (size.Y - ImGui.GetFontSize()) / 2;
        using (UiFonts.PushIcon())
            UiFonts.DrawText(draw, ImGui.GetFontSize(), origin + new Vector2(Ui(8), textY),
                ImGui.GetColorU32(enabled ? AccentHover : MutedText), icon.ToIconString());
        UiFonts.DrawText(draw, ImGui.GetFontSize(), origin + new Vector2(Ui(36), textY),
            ImGui.GetColorU32(enabled ? ImGuiCol.Text : ImGuiCol.TextDisabled), label);
        if (selected)
        {
            using var checkFont = UiFonts.PushIcon();
            UiFonts.DrawText(draw, ImGui.GetFontSize(), origin + new Vector2(size.X - Ui(22), textY),
                ImGui.GetColorU32(enabled ? AccentHover : MutedText), FontAwesomeIcon.Check.ToIconString());
        }
        return clicked;
    }

    private bool PartyPopupChoice(string id, IReadOnlyList<string> choices, ref int selected)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var origin = ImGui.GetCursorScreenPos();
        if (ActivePartyButton("##" + id, new(width, Ui(32)))) ImGui.OpenPopup(id + "Options");
        var label = selected >= 0 && selected < choices.Count ? choices[selected] : "Choose…";
        UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), origin + UiVec(10, 8),
            ImGui.GetColorU32(ImGuiCol.Text), FitPartyTextToWidth(label, width - Ui(42)));
        using (UiFonts.PushIcon()) UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(),
            origin + new Vector2(width - Ui(26), Ui(9)), ImGui.GetColorU32(AccentHover),
            Dalamud.Interface.FontAwesomeIcon.ChevronDown.ToIconString());
        var changed = false;
        if (BeginPartyPopup(id + "Options", new(width, PartyMenuSize(choices.Count).Y)))
        {
            for (var i = 0; i < choices.Count; i++)
                if (ImGui.Selectable(choices[i] + "##" + i, i == selected))
                { selected = i; changed = true; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
        return changed;
    }
}

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // Both the quick toggles and dropdown edit this same selection.
    private readonly HashSet<int> partyDirectoryVibeFilters = [];

    private void ToggleDirectoryVibeFilter(int id)
    {
        if (!partyDirectoryVibeFilters.Remove(id)) partyDirectoryVibeFilters.Add(id);
        partyDirectoryVisibleCount = PartyDirectoryInitialVisibleCount;
    }

    private void DrawPartyDirectoryVibeFilter(float width)
    {
        var summary = partyDirectoryVibeFilters.Count switch
        {
            0 => "Any",
            1 => PartyVibes.Options.First(v => partyDirectoryVibeFilters.Contains(v.Id)).Label,
            _ => $"{partyDirectoryVibeFilters.Count} selected",
        };
        ImGui.SetNextItemWidth(width);
        if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##directoryVibeFilter", $"Party vibes: {summary}"))) return;
        if (ImGui.Selectable("Any vibes", partyDirectoryVibeFilters.Count == 0))
        {
            partyDirectoryVibeFilters.Clear();
            partyDirectoryVisibleCount = PartyDirectoryInitialVisibleCount;
        }
        foreach (var (id, label) in PartyVibes.Options)
        {
            var selected = partyDirectoryVibeFilters.Contains(id);
            if (ImGui.Checkbox($"{(id == 1 ? "LFW — Looking for Watchers" : label)}##filterVibe{id}", ref selected))
                ToggleDirectoryVibeFilter(id);
        }
        ImGui.TextColored(MutedText, "Matches any selected vibe.");
        ImGui.EndCombo();
    }

    private bool DrawDirectoryVibeFilterRow(Vector2 header, float headerWidth, float countRight,
        Vector2 sortOrigin, Vector2 afterHeader)
    {
        var height = Ui(28f);
        var gap = Ui(6f);
        var lfwWidth = ImGui.CalcTextSize("LFW").X + Ui(30f);
        var requiredWidth = lfwWidth + height * 7f + gap * 7f;
        var startX = countRight + Ui(18f);
        var inlineWidth = sortOrigin.X - Ui(12f) - startX;
        var inline = inlineWidth >= requiredWidth;
        var origin = inline
            ? new Vector2(startX + (inlineWidth - requiredWidth) * .5f, sortOrigin.Y)
            : afterHeader;
        var availableWidth = inline ? requiredWidth : headerWidth;
        var position = origin;
        var draw = ImGui.GetWindowDrawList();
        var changed = false;
        foreach (var (id, label) in PartyVibes.Options)
        {
            var width = id == 1 ? lfwWidth : height;
            if (position.X > origin.X && position.X + width > origin.X + availableWidth + .5f)
                position = new Vector2(origin.X, position.Y + height + gap);
            ImGui.SetCursorScreenPos(position);
            if (ImGui.InvisibleButton($"##quickVibeFilter{id}", new Vector2(width, height)))
            {
                ToggleDirectoryVibeFilter(id);
                changed = true;
            }
            var selected = partyDirectoryVibeFilters.Contains(id);
            var hovered = ImGui.IsItemHovered();
            draw.AddRectFilled(position, position + new Vector2(width, height), ImGui.GetColorU32(selected
                ? new Vector4(Accent.X, Accent.Y, Accent.Z, hovered ? .3f : .2f)
                : hovered ? FrameBgHover : FrameBg), Ui(7f));
            draw.AddRect(position, position + new Vector2(width, height), ImGui.GetColorU32(selected ? Accent
                : new Vector4(Accent.X, Accent.Y, Accent.Z, hovered ? .6f : .3f)), Ui(7f));
            using (UiFonts.PushIcon())
            {
                var glyph = PartyVibeIcon(id).ToIconString();
                var size = Ui(13f);
                var bounds = ImGui.CalcTextSize(glyph) * (size / ImGui.GetFontSize());
                var centerX = id == 1 ? Ui(14f) : width * .5f;
                UiFonts.DrawText(draw, size, position + new Vector2(centerX - bounds.X * .5f, (height - bounds.Y) * .5f), ImGui.GetColorU32(Accent), glyph);
            }
            if (id == 1)
                draw.AddText(position + new Vector2(Ui(25f), (height - ImGui.CalcTextSize("LFW").Y) * .5f),
                    ImGui.GetColorU32(selected ? Vector4.One : MutedText), "LFW");
            if (hovered) UiFonts.SetTooltip(id == 1 ? "LFW — Looking for Watchers" : label);
            position.X += width + gap;
        }
        ImGui.SetCursorScreenPos(inline ? afterHeader : new Vector2(header.X, position.Y + height + gap));
        return changed;
    }
}

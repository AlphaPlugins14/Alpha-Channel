using AlphaChannel.Contracts;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // Draw in the existing upper-right space, without changing card height.
    // Return the width to keep the host name clear of the same strip.
    private float DrawDirectoryPartyVibes(ImDrawListPtr draw, RoomDirectoryDto room,
        Vector2 rightEdge, float availableWidth)
    {
        var selected = PartyVibes.Read(room.Description);
        var tags = PartyVibes.Options.Where(v => selected.Contains(v.Id)).ToArray();
        if (tags.Length == 0 || availableWidth < Ui(26f)) return 0f;
        var height = Ui(28f);
        var gap = Ui(6f);
        float ChipWidth(int id) => id == 1 ? ImGui.CalcTextSize("LFW").X + Ui(30f) : height;
        float StripWidth(int count, bool overflow) => tags.Take(count).Sum(v => ChipWidth(v.Id)) +
            (overflow ? MathF.Max(height, ImGui.CalcTextSize($"+{tags.Length - count}").X + Ui(12f)) : 0f) +
            MathF.Max(0, count + (overflow ? 1 : 0) - 1) * gap;
        var visible = tags.Length;
        while (visible > 0 && StripWidth(visible, visible < tags.Length) > availableWidth) visible--;
        var overflow = visible < tags.Length;
        var width = StripWidth(visible, overflow);
        var position = rightEdge - new Vector2(width, 0f);
        for (var index = 0; index < visible + (overflow ? 1 : 0); index++)
        {
            var more = index == visible;
            var tag = more ? default : tags[index];
            var label = more ? $"+{tags.Length - visible}" : tag.Id == 1 ? "LFW" : "";
            var chipWidth = more ? MathF.Max(height, ImGui.CalcTextSize(label).X + Ui(12f)) : ChipWidth(tag.Id);
            var max = position + new Vector2(chipWidth, height);
            var hovered = ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(position, max);
            draw.AddRectFilled(position, max, ImGui.GetColorU32(hovered ? FrameBgHover : FrameBg), Ui(7f));
            draw.AddRect(position, max, ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z,
                hovered ? .8f : .4f)), Ui(7f));
            if (!more)
            {
                var glyph = PartyVibeIcon(tag.Id).ToIconString();
                using (UiFonts.PushIcon())
                {
                    var iconSize = Ui(13f);
                    var bounds = ImGui.CalcTextSize(glyph) * (iconSize / ImGui.GetFontSize());
                    var centerX = tag.Id == 1 ? Ui(14f) : chipWidth * .5f;
                    UiFonts.DrawText(draw, iconSize, position + new Vector2(centerX - bounds.X * .5f, (height - bounds.Y) * .5f), ImGui.GetColorU32(Accent), glyph);
                }
            }
            if (label.Length > 0)
            {
                var bounds = ImGui.CalcTextSize(label);
                draw.AddText(position + new Vector2(more ? (chipWidth - bounds.X) * .5f : Ui(25f),
                    (height - bounds.Y) * .5f), ImGui.GetColorU32(more ? MutedText : Vector4.One), label);
            }
            if (hovered)
            {
                using var tooltipStyle = ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, Ui(10f))
                    .Push(ImGuiStyleVar.PopupBorderSize, Ui(1f))
                    .Push(ImGuiStyleVar.WindowPadding, UiVec(14f, 12f))
                    .Push(ImGuiStyleVar.ItemSpacing, UiVec(8f, 8f));
                using var tooltipColors = ImRaii.PushColor(ImGuiCol.PopupBg, CardBg)
                    .Push(ImGuiCol.Border, AccentInk(.65f));
                UiFonts.BeginTooltip();
                {
                    if (more) DrawSocialHeading("More party vibes");
                    foreach (var vibe in more ? tags.Skip(visible) : tags.Skip(index).Take(1))
                    {
                        using (UiFonts.PushIcon())
                            ImGui.TextColored(AccentHover, PartyVibeIcon(vibe.Id).ToIconString());
                        ImGui.SameLine(Ui(38f));
                        using (ImRaii.TextWrapPos(Ui(310f)))
                            DrawSocialHelper(vibe.Id == 1 ? "LFW — Looking for Watchers" : vibe.Label);
                    }
                    ImGui.EndTooltip();
                }
            }
            position.X += chipWidth + gap;
        }
        return width;
    }
}

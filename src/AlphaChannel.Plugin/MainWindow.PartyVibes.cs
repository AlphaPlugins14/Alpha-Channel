using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly HashSet<int> createRoomPartyVibes = [];

    private static FontAwesomeIcon PartyVibeIcon(int id) => id switch
    {
        1 => FontAwesomeIcon.Users,
        2 => FontAwesomeIcon.CommentDots,
        3 => FontAwesomeIcon.VolumeMute,
        4 => FontAwesomeIcon.EyeSlash,
        5 => FontAwesomeIcon.UserPlus,
        6 => FontAwesomeIcon.Coffee,
        7 => FontAwesomeIcon.Eye,
        _ => FontAwesomeIcon.Comments,
    };

    private void DrawPartyVibeChoices(float width)
    {
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Ui(6f));
        ImGui.TextColored(MutedText, "Party vibes (optional)");
        var origin = ImGui.GetCursorScreenPos();
        var position = origin;
        var height = Ui(29f);
        var gap = Ui(8f);
        var draw = ImGui.GetWindowDrawList();
        for (var index = 0; index < PartyVibes.Options.Length; index++)
        {
            var (id, optionLabel) = PartyVibes.Options[index];
            var label = id switch { 2 => "Chat welcome", 3 => "Quiet viewing", 4 => "No spoilers", 8 => "Watch & discuss", _ => optionLabel };
            var icon = PartyVibeIcon(id).ToIconString();
            var textSize = ImGui.CalcTextSize(label);
            var pillWidth = textSize.X + Ui(id == 1 ? 65f : 46f);
            if (index == 4 || position.X > origin.X && position.X + pillWidth > origin.X + width)
                position = new Vector2(origin.X, position.Y + height + Ui(6f));
            pillWidth = MathF.Min(pillWidth, width);
            ImGui.SetCursorScreenPos(position);
            var selected = createRoomPartyVibes.Contains(id);
            if (ImGui.InvisibleButton($"##partyVibe{id}", new Vector2(pillWidth, height)))
            {
                if (!createRoomPartyVibes.Remove(id)) createRoomPartyVibes.Add(id);
                selected = createRoomPartyVibes.Contains(id);
            }
            var hovered = ImGui.IsItemHovered();
            var fill = selected ? new Vector4(Accent.X, Accent.Y, Accent.Z, hovered ? .27f : .17f)
                : hovered ? FrameBgHover : FrameBg;
            var border = selected ? AccentHover : BorderSubtle;
            draw.AddRectFilled(position, position + new Vector2(pillWidth, height), ImGui.GetColorU32(fill), Ui(8f));
            draw.AddRect(position, position + new Vector2(pillWidth, height), ImGui.GetColorU32(border), Ui(8f));
            using (UiFonts.PushIcon())
            {
                var iconSize = Ui(12f);
                var iconBounds = ImGui.CalcTextSize(icon) * (iconSize / ImGui.GetFontSize());
                var iconPosition = position + new Vector2(Ui(16f) - iconBounds.X * .5f,
                    (height - iconBounds.Y) * .5f);
                UiFonts.DrawText(draw, iconSize, iconPosition, ImGui.GetColorU32(Accent), icon);
            }
            if (selected)
            {
                var at = position + new Vector2(pillWidth - Ui(14f), Ui(14f));
                draw.AddLine(at, at + UiVec(3f, 3f), ImGui.GetColorU32(Vector4.One), Ui(1.5f));
                draw.AddLine(at + UiVec(3f, 3f), at + UiVec(8f, -3f), ImGui.GetColorU32(Vector4.One), Ui(1.5f));
            }
            draw.AddText(position + new Vector2(Ui(28f), (height - textSize.Y) * .5f),
                ImGui.GetColorU32(selected ? Vector4.One : MutedText), label);
            if (id == 1)
            {
                var center = position + new Vector2(pillWidth - Ui(29f), height * .5f);
                draw.AddCircle(center, Ui(6f), ImGui.GetColorU32(AccentHover));
                var infoSize = ImGui.CalcTextSize("i");
                draw.AddText(center - infoSize * .5f, ImGui.GetColorU32(AccentHover), "i");
                if (hovered) UiFonts.SetTooltip("Looking for Watchers — let people know you're looking for company.");
            }
            position.X += pillWidth + gap;
        }
        ImGui.SetCursorScreenPos(new Vector2(origin.X, position.Y + height));
        ImGui.Dummy(Vector2.Zero);
    }
}

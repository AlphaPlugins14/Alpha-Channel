using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static void DrawArcadePlayerLabel(ImDrawListPtr draw, Vector2 position, string label, float iconPixels, float textScale = .82f)
    {
        var glyph = FontAwesomeIcon.Users.ToIconString();
        Vector2 iconSize;
        using (UiFonts.PushIcon(iconPixels))
        {
            iconSize = ImGui.CalcTextSize(glyph);
            UiFonts.DrawText(draw, ImGui.GetFontSize(), position, ImGui.GetColorU32(AccentHover), glyph);
        }
        using var font = UiFonts.PushRegularRelative(textScale);
        var textPosition = position + new Vector2(iconSize.X + Ui(6f),
            MathF.Max(0f, (iconSize.Y - ImGui.GetTextLineHeight()) * .5f));
        UiFonts.DrawText(draw, ImGui.GetFontSize(), textPosition, ImGui.GetColorU32(MutedText), label);
    }

    private static void DrawArcadeCardTitle(ImDrawListPtr draw, Vector2 position, string title, float width)
    {
        using var font = UiFonts.PushRelative(1.1f, emphasis: true);
        UiFonts.DrawText(draw, ImGui.GetFontSize(), position, ImGui.GetColorU32(Hex(0xE2E7EF)),
            ArcadeFitText(title, MathF.Max(1f, width)));
        if (ImGui.IsMouseHoveringRect(position, position + new Vector2(width, ImGui.GetTextLineHeight())))
            UiFonts.SetTooltip(title);
    }
}

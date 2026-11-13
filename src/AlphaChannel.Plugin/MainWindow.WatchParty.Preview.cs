using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{

    private void DrawWatchPartyPreviewFigure(ImDrawListPtr draw, Vector2 feet, float height)
    {
        var ink = ImGui.GetColorU32(new Vector4(0.018f, 0.020f, 0.047f, 0.95f));
        var head = feet - new Vector2(0f, height * 0.81f);
        draw.AddCircleFilled(head, height * 0.11f, ink, 16);
        var shoulders = feet - new Vector2(0f, height * 0.65f);
        var hips = feet - new Vector2(0f, height * 0.32f);
        draw.AddLine(shoulders, hips, ink, height * 0.19f);
        draw.AddLine(shoulders - new Vector2(height * 0.04f, 0f),
            shoulders + new Vector2(height * 0.23f, height * 0.27f), ink, height * 0.075f);
        draw.AddLine(shoulders, shoulders - new Vector2(height * 0.22f, -height * 0.24f),
            ink, height * 0.075f);
        draw.AddLine(hips, feet - new Vector2(height * 0.13f, 0f), ink, height * 0.09f);
        draw.AddLine(hips, feet + new Vector2(height * 0.14f, 0f), ink, height * 0.09f);
    }

    private void DrawWatchPartyPreviewReaction(ImDrawListPtr draw, Vector2 center, float radius, int index)
    {
        var reaction = Reactions[index];
        draw.AddCircleFilled(center + new Vector2(radius * 0.10f, radius * 0.12f), radius * 1.08f,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.5f)), 32);
        draw.AddCircleFilled(center, radius,
            ImGui.GetColorU32(new Vector4(0.018f, 0.025f, 0.055f, 1f)), 32);
        draw.AddCircleFilled(center, radius * 0.84f,
            ImGui.GetColorU32(reaction.BadgeColor with { W = 0.85f }), 32);
        var glyph = reaction.Icon.ToIconString();
        var fontSize = radius * 1.10f;
        using var uiIconFont11667=UiFonts.PushIcon();
        var textSize = ImGui.CalcTextSize(glyph) * (fontSize / ImGui.GetFontSize());
        uiIconFont11667.Dispose();
        UiFonts.DrawIcon(draw, fontSize, center - textSize * 0.5f, ImGui.GetColorU32(Vector4.One), glyph);
    }

    private void DrawWatchPartyPreviewChatRow(ImDrawListPtr draw, Vector2 origin, float width,
        string name, string message, Vector4 color, FontAwesomeIcon icon)
    {
        var center = origin + UiVec(9f, 12f);
        draw.AddCircleFilled(center, Ui(9f), ImGui.GetColorU32(color));
        var glyph = icon.ToIconString();
        var glyphSize = Ui(10f);
        using var uiIconFont12302=UiFonts.PushIcon();
        var textSize = ImGui.CalcTextSize(glyph) * (glyphSize / ImGui.GetFontSize());
        uiIconFont12302.Dispose();
        UiFonts.DrawIcon(draw, glyphSize, center - textSize * 0.5f, ImGui.GetColorU32(Vector4.One), glyph);
        draw.AddText(origin + UiVec(25f, 0f), ImGui.GetColorU32(color), name);
        draw.AddText(origin + UiVec(25f, 17f), ImGui.GetColorU32(Vector4.One),
            ArcadeFitText(message, width - Ui(27f)));
    }
}

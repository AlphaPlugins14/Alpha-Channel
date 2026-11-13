using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // Decorative geometry and existing font icons only; no image assets.
    private void DrawWatchPartyLandingScene(Vector2 origin, Vector2 size)
    {
        var draw = ImGui.GetWindowDrawList();
        var scale = MathF.Min(size.X / 320f, size.Y / 144f);
        var start = origin + (size - new Vector2(320, 144) * scale) * .5f;
        Vector2 P(float x, float y) => start + new Vector2(x, y) * scale;
        draw.PushClipRect(origin, origin + size, true);
        var screen = P(76, 15); var end = P(254, 103);
        for (var i = 3; i > 0; i--)
            draw.AddRect(screen - new Vector2(i * 4 * scale), end + new Vector2(i * 4 * scale),
                ImGui.GetColorU32(AccentInk(.035f)), 9 * scale, ImDrawFlags.None, 3 * scale);
        draw.AddRectFilled(screen, end, ImGui.GetColorU32(FrameBg), 8 * scale);
        draw.AddRect(screen, end, ImGui.GetColorU32(AccentHover), 8 * scale, ImDrawFlags.None, 2 * scale);
        draw.AddRect(screen + new Vector2(4 * scale), end - new Vector2(4 * scale), ImGui.GetColorU32(AccentInk(.25f)), 5 * scale);
        draw.AddTriangleFilled(P(153, 41), P(153, 73), P(181, 57), ImGui.GetColorU32(AccentHover));
        draw.AddLine(P(93, 90), P(237, 90), ImGui.GetColorU32(AccentInk(.45f)), 3 * scale);
        draw.AddLine(P(93, 90), P(139, 90), ImGui.GetColorU32(AccentHover), 3 * scale);
        draw.AddCircleFilled(P(139, 90), 4 * scale, ImGui.GetColorU32(AccentHover));
        draw.AddRectFilled(P(155, 104), P(175, 111), ImGui.GetColorU32(AccentInk(.4f)), 2 * scale);
        draw.AddRectFilled(P(136, 110), P(194, 114), ImGui.GetColorU32(AccentInk(.55f)), 2 * scale);
        Vector4[] colours = [new(.60f, .38f, .96f, 1), new(.08f, .73f, .68f, 1), new(.2f, .5f, 1, 1)];
        for (var i = 0; i < 3; i++) {
            var centre = P(105 + 65 * i, 123);
            draw.AddCircleFilled(centre + new Vector2(0, 2 * scale), 18 * scale, ImGui.GetColorU32(AccentInk(.08f)));
            draw.AddCircleFilled(centre, 16 * scale, ImGui.GetColorU32(colours[i]));
            UiFonts.DrawCenteredIcon(draw, 18 * scale, centre, ImGui.GetColorU32(Vector4.One), FontAwesomeIcon.User.ToIconString());
        }
        (Vector2 Centre, FontAwesomeIcon Icon, Vector4 Colour)[] reactions = [
            (P(52, 36), FontAwesomeIcon.Heart, new(.97f, .23f, .51f, 1)),
            (P(60, 78), FontAwesomeIcon.Smile, new(.98f, .69f, .21f, 1)),
            (P(290, 56), FontAwesomeIcon.ThumbsUp, new(.16f, .48f, 1, 1))];
        for (var i = 0; i < reactions.Length; i++) {
            var item = reactions[i]; var centre = item.Centre;
            draw.AddCircleFilled(centre, 15 * scale, ImGui.GetColorU32(item.Colour));
            UiFonts.DrawCenteredIcon(draw, 18 * scale, centre, ImGui.GetColorU32(Vector4.One), item.Icon.ToIconString());
        }
        draw.PopClipRect();
    }
}

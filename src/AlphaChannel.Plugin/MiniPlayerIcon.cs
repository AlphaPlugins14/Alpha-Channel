using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace AlphaChannel.Plugin;

internal static class MiniPlayerIcon
{
    internal static void Draw(ImDrawListPtr draw, Vector2 origin, float size, uint color)
    {
        var scale = size / 30f;
        origin.Y += 1f;
        var stroke = MathF.Max(1f, MathF.Round(1.25f * scale));
        var alignment = stroke % 2f * .5f;
        Vector2 At(float x, float y) => new(
            MathF.Round(origin.X + x * scale) + alignment,
            MathF.Round(origin.Y + y * scale) + alignment);

        draw.AddRect(At(6f, 8f), At(24f, 22f), color, 2f * scale,
            ImDrawFlags.None, stroke);
        // Use the rasterized icon font for smooth diagonals at compact sizes.
        UiFonts.DrawCenteredIcon(draw, 9f * scale, origin + new Vector2(15.5f, 15f) * scale,
            color, FontAwesomeIcon.Play.ToIconString());
    }
}

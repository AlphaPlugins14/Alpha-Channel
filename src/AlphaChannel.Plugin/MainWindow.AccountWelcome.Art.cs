using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static void DrawAccountWelcomeMediaTiles(ImDrawListPtr draw, Vector2 center, float scale, float alpha, bool narrow)
    {
        uint Colour(uint rgb, float opacity = 1) => ImGui.GetColorU32(new Vector4(
            ((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, alpha * opacity));
        Vector2 Screen(float x, float y) => center + new Vector2(x, y) * scale;
        static Vector2 Rotate(Vector2 p, float angle) => new(
            p.X * MathF.Cos(angle) - p.Y * MathF.Sin(angle),
            p.X * MathF.Sin(angle) + p.Y * MathF.Cos(angle));

        void Icon(FontAwesomeIcon icon, Vector2 at, float pixels, uint colour, float angle, float shear = 0)
        {
            var firstVertex = draw.VtxBuffer.Size;
            UiFonts.DrawCenteredIcon(draw, pixels, at, colour, icon.ToIconString());
            var vertices = draw.VtxBuffer;
            for (var index = firstVertex; index < vertices.Size; index++)
            {
                var vertex = vertices[index];
                var local = vertex.Pos - at;
                local.Y += local.X * shear;
                vertex.Pos = at + Rotate(local, angle);
                // ImDrawVert is a value type: modifying the local copy alone
                // leaves the rendered glyph unrotated. Commit it to the buffer.
                vertices[index] = vertex;
            }
        }

        // Rounded rectangles are built in local coordinates, then rotated or
        // sheared into place. The side screens are decorative and take no input.
        void RoundedPath(Vector2 at, Vector2 size, float angle, float radius, float shear = 0)
        {
            var half = size / 2;
            for (var corner = 0; corner < 4; corner++)
            {
                var sign = corner switch { 0 => new Vector2(1, -1), 1 => Vector2.One,
                    2 => new Vector2(-1, 1), _ => -Vector2.One };
                var arcCenter = sign * (half - new Vector2(radius));
                for (var step = 0; step <= 6; step++)
                {
                    var theta = (-.5f + corner * .5f + step / 12f) * MathF.PI;
                    var local = arcCenter + new Vector2(MathF.Cos(theta), MathF.Sin(theta)) * radius;
                    local.Y += local.X * shear;
                    draw.PathLineTo(at + Rotate(local, angle) * scale);
                }
            }
        }
        void Panel(Vector2 at, Vector2 size, float angle, float radius, uint fill, uint border, float thickness, float shear = 0)
        {
            RoundedPath(at, size, angle, radius, shear); draw.PathFillConvex(fill);
            RoundedPath(at, size, angle, radius, shear); draw.PathStroke(border, ImDrawFlags.Closed, thickness * scale);
        }

        // These angled, translucent play screens sit around the hero at the
        // same height as the mockup, in addition to the original moving panels.
        var sideSpread = narrow ? .53f : 1;
        for (var side = -1; side <= 1; side += 2)
        {
            for (var screen = 0; screen < 3; screen++)
            {
                var x = screen switch { 0 => 355, 1 => 406, _ => 322 };
                var y = screen switch { 0 => -65, 1 => 10, _ => 65 };
                var size = screen switch { 0 => new Vector2(86, 60), 1 => new Vector2(112, 77), _ => new Vector2(78, 51) };
                if (narrow) size *= .7f;
                var at = Screen(side * x * sideSpread, y);
                var shear = side * -.17f;
                var angle = side * -.035f;
                Panel(at, size, angle, 5, Colour(0x174479, .15f), Colour(0x3E8BCD, .36f), 1.3f, shear);
                // Use the font atlas's smooth play glyph rather than a tiny
                // hard-edged triangle, with the screen's exact shear/rotation.
                Icon(FontAwesomeIcon.Play, at, size.Y * .43f * scale,
                    Colour(0x65A4DF, .25f), angle, shear);
            }
        }

        void Tile(float x, float y, float angle, uint fill, uint rim, FontAwesomeIcon icon)
        {
            var at = Screen(x, y);
            var size = new Vector2(112, 120);
            // Broad, low-opacity outlines give a restrained halo, while the
            // crisp inner rim keeps the shapes readable on the moving panels.
            for (var i = 3; i >= 1; i--)
            {
                RoundedPath(at, size + new Vector2(i * 3), angle, 17 + i);
                draw.PathStroke(Colour(rim, .035f), ImDrawFlags.Closed, (4 + i * 2) * scale);
            }
            Panel(at + new Vector2(0, 5) * scale, size, angle, 16,
                Colour(0x050D20, .7f), Colour(0x050D20, .4f), 2);
            Panel(at, size, angle, 16, Colour(fill), Colour(rim), 3);
            Panel(at, size - new Vector2(10), angle, 12, Colour(0xFFFFFF, .025f), Colour(0xFFFFFF, .18f), 1);

            Icon(icon, at, 51 * scale, Colour(0xF3F8FF), angle);
        }

        Tile(-99, 12, -.18f, 0x2469D3, 0x73C6FF, FontAwesomeIcon.Play);
        Tile(99, 12, .18f, 0x097E9C, 0x60E1EE, FontAwesomeIcon.Gamepad);
        Tile(0, -4, 0, 0x7841CB, 0xC399FF, FontAwesomeIcon.Music);
    }
}

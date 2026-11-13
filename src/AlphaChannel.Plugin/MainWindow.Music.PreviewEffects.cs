using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawMusicPreviewStageEffects(ImDrawListPtr draw, MusicSceneEffects scene, Vector2 min, Vector2 max)
    {
        uint Colour(uint rgb, float alpha = 1) => ImGui.GetColorU32(new Vector4(WheelRgb(rgb), alpha));
        var centre = (min + max) / 2;
        var width = max.X - min.X;
        var height = max.Y - min.Y;
        if ((scene.EffectiveMask & MusicSceneEffects.Spotlight) != 0 && scene.SpotlightBrightness > 0)
        {
            var radius = height * (.65f + scene.SpotlightRange / 60f);
            var alpha = Math.Clamp(scene.SpotlightBrightness * .018f, 0, .09f);
            for (var layer = 10; layer > 0; layer--)
                draw.AddCircleFilled(centre, radius * layer / 10f, Colour(scene.SpotlightColour, alpha), 48);
        }
        // Backdrop first, so the TV and speaker cabinets mask it.
        if ((scene.Mask & 8192) != 0)
        {
            var top = min.Y - height * .68f;
            var bottom = max.Y + height * .28f;
            for (var strip = 0; strip < 30; strip++)
            {
                var x = centre.X + width * ((strip + .5f) / 30 - .5f) * 2.25f;
                var brightness = .4f + .6f * MathF.Pow(.5f + .5f * MathF.Sin(strip * 1.1f), 2);
                draw.AddRectFilled(new Vector2(x - width * .012f, top), new Vector2(x + width * .012f, bottom), Colour(scene.Curtain, brightness * .05f));
                draw.AddRectFilledMultiColor(new Vector2(x - width * .004f, top), new Vector2(x + width * .004f, bottom),
                    Colour(scene.Curtain, .03f), Colour(scene.Curtain, .03f), Colour(scene.Curtain, brightness * .38f), Colour(scene.Curtain, brightness * .38f));
            }
        }
        if ((scene.EffectiveMask & 128) != 0 || scene.IsSpooky)
        {
            // A fixed perspective view of the horizontal live grid. Offset is
            // measured in world units, using the TV's current height scale.
            var pixelsPerUnit = height / (1.2f * Math.Max(.01f, screenController.Engine.ScreenHeightScale));
            var floorY = max.Y - (scene.FloorOffset + .025f) * pixelsPerUnit;
            Vector2 TilePoint(float column, float row)
            {
                var depth = row / 8;
                var halfWidth = width * (.64f + .40f * depth);
                return new Vector2(centre.X + (column / 12 * 2 - 1) * halfWidth,
                    floorY + height * (-.10f + .55f * MathF.Pow(depth, 1.5f)));
            }
            for (var row = 0; row < 8; row++)
            for (var column = 0; column < 12; column++)
            {
                var cell = column + row * 1.7f;
                var wave = .5f + .5f * MathF.Sin(3 - cell * .7f);
                var tint = scene.IsSpooky ? ((column + row) % 2 == 0 ? 0xFF7708u : 0x9B43DDu) : WheelHex(Vector3.Lerp(WheelRgb(scene.DanceFloor), WheelRgb(MusicPreviewRainbow(cell * .08f + .1f)), .35f));
                var a = TilePoint(column + .03f, row + .03f);
                var b = TilePoint(column + .97f, row + .03f);
                var c = TilePoint(column + .97f, row + .97f);
                var d = TilePoint(column + .03f, row + .97f);
                draw.AddQuadFilled(a, b, c, d, Colour(tint, .10f + wave * .30f));
                draw.AddQuad(a, b, c, d, Colour(tint, .25f + wave * .45f), Ui(.8f));
                if (scene.IsSpooky)
                    DrawSpookyPreviewSprite(draw, (a + b + c + d) / 4, Math.Max(1, Math.Min((b.X-a.X)*.28f, (d.Y-a.Y)*.40f)), (column + row) % 2);
            }
        }
        if ((scene.Mask & 64) != 0)
            for (var side = -1; side <= 1; side += 2)
            {
                var emitter = new Vector2(centre.X + side * width * .90f, max.Y + height * .07f);
                var beamTop = new Vector2(centre.X + side * width * .46f, min.Y - height * .72f);
                for (var glow = 8; glow > 0; glow--)
                {
                    var spread = width * (.035f + glow * .015f);
                    draw.AddTriangleFilled(emitter, beamTop - new Vector2(spread, 0), beamTop + new Vector2(spread, 0), Colour(scene.Spotlights, .018f));
                }
                draw.AddCircleFilled(emitter, Ui(2), Colour(scene.Spotlights, .8f), 12);
            }
    }

    private void DrawMusicPreviewOuterEffects(ImDrawListPtr draw, MusicSceneEffects scene, Vector2 min, Vector2 max)
    {
        uint Colour(uint rgb, float alpha = 1) => ImGui.GetColorU32(new Vector4(WheelRgb(rgb), alpha));
        var centre = (min + max) / 2;
        var width = max.X - min.X;
        var height = max.Y - min.Y;
        if (scene.IsSpooky) DrawSpookyPreviewDecorations(draw, min, max);
        if ((scene.EffectiveMask & 256) != 0)
        {
            var ball = new Vector2(centre.X, min.Y - height * .43f);
            var radius = height * .14f;
            draw.AddLine(ball - new Vector2(0, radius * 2.6f), ball - new Vector2(0, radius), Colour(scene.DiscoBall, .4f), Ui(.8f));
            draw.AddCircleFilled(ball, radius, Colour(scene.DiscoBall, .25f), 48);
            // Curved rows of mirror tiles give the ball a spherical silhouette.
            for (var row = 0; row < 10; row++)
            {
                var y0 = (row / 10f * 2 - 1) * .98f;
                var y1 = ((row + 1) / 10f * 2 - 1) * .98f;
                var span0 = MathF.Sqrt(Math.Max(0, 1 - y0 * y0));
                var span1 = MathF.Sqrt(Math.Max(0, 1 - y1 * y1));
                for (var column = 0; column < 10; column++)
                {
                    var angle0 = -MathF.PI / 2 + (column + .1f) / 10 * MathF.PI;
                    var angle1 = -MathF.PI / 2 + (column + .9f) / 10 * MathF.PI;
                    var a = ball + radius * new Vector2(MathF.Sin(angle0) * span0, y0);
                    var b = ball + radius * new Vector2(MathF.Sin(angle1) * span0, y0);
                    var c = ball + radius * new Vector2(MathF.Sin(angle1) * span1, y1);
                    var d = ball + radius * new Vector2(MathF.Sin(angle0) * span1, y1);
                    var shine = .22f + .72f * MathF.Pow(.5f + .5f * MathF.Sin(column * 1.8f + row * 2.7f + 2), 4);
                    draw.AddQuadFilled(a, b, c, d, Colour(scene.DiscoBall, shine));
                }
            }
            draw.AddCircle(ball, radius, Colour(scene.DiscoBall, .5f), 48, Ui(.7f));
            for (var fleck = 0; fleck < 12; fleck++)
            {
                var angle = fleck * 2.399f + .22f;
                var point = centre + new Vector2(MathF.Cos(angle) * width * .80f, MathF.Sin(angle) * height * .88f);
                draw.AddCircleFilled(point, Ui(1), Colour(scene.DiscoBall, .65f), 8);
            }
        }
        if ((scene.Mask & 2048) != 0)
            for (var shape = 0; shape < 8; shape++)
            {
                var angle = shape * MathF.PI / 4 + .35f;
                var point = centre + new Vector2(MathF.Cos(angle) * width * .95f, MathF.Sin(angle) * height * .94f);
                var rotation = .7f + shape;
                var radius = width * .025f;
                var dx = new Vector2(MathF.Cos(rotation), MathF.Sin(rotation)) * radius;
                var dy = new Vector2(-MathF.Sin(rotation), MathF.Cos(rotation)) * radius;
                draw.AddQuad(point + dx, point + dy, point - dx, point - dy, Colour(scene.Orbits, .85f), Ui(1));
            }
    }

    private void DrawMusicPreviewNeonFrame(ImDrawListPtr draw, MusicSceneEffects scene, Vector2 min, Vector2 max)
    {
        if ((scene.Mask & 1024) == 0) return;
        uint Colour(float alpha) => ImGui.GetColorU32(new Vector4(WheelRgb(scene.NeonFrame), alpha));
        var border = (max.X - min.X) * .0225f;
        var padding = new Vector2(border, (max.Y - min.Y) * .029f);
        min -= padding;
        max += padding;
        for (var glow = 4; glow > 0; glow--)
            draw.AddRect(min, max, Colour(.04f), 0, ImDrawFlags.None, Ui(2 + glow * 2));
        draw.AddRect(min, max, Colour(.9f), 0, ImDrawFlags.None, Ui(1.5f));
    }
}

using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static void DrawProfileBadgeDesign(ImDrawListPtr draw, Vector2 origin, float size,
        ProfileBadgeDesign design, bool unlocked)
    {
        // All artwork uses a 100-unit canvas and scales with its grid cell.
        var scale = size / 100f;
        var rotation = 0f;
        Vector2 P(float x, float y)
        {
            var point = new Vector2(x, y);
            if (rotation != 0f)
            {
                var offset = point - new Vector2(50f, 45f);
                point = new Vector2(50f, 45f) + new Vector2(
                    offset.X * MathF.Cos(rotation) - offset.Y * MathF.Sin(rotation),
                    offset.X * MathF.Sin(rotation) + offset.Y * MathF.Cos(rotation));
            }
            return origin + point * scale;
        }
        uint C(float r, float g, float b)
        {
            if (unlocked) return ImGui.GetColorU32(new Vector4(r, g, b, 1f));
            // Use the same artwork in faint greyscale until its stable badge ID
            // is awarded. Unlocking changes only the palette, never the design.
            var grey = .2126f * r + .7152f * g + .0722f * b;
            // Blend each shade into the panel colour, keeping fills opaque.
            // Translucent individual primitives expose seams and accumulate
            // opacity wherever the original artwork overlaps (e.g. popcorn).
            const float visibility = .12f;
            var background = CardBg;
            return ImGui.GetColorU32(new Vector4(
                background.X + (grey - background.X) * visibility,
                background.Y + (grey - background.Y) * visibility,
                background.Z + (grey - background.Z) * visibility, 1f));
        }
        var ink = C(.035f, .07f, .13f);
        var blue = C(.22f, .57f, 1f);
        var cyan = C(.25f, .84f, .95f);
        var gold = C(1f, .73f, .23f);
        var orange = C(1f, .43f, .16f);
        var pink = C(1f, .37f, .48f);
        var purple = C(.65f, .36f, .96f);
        var silver = C(.78f, .82f, .91f);
        var cream = C(1f, .91f, .71f);

        void Rect(float x, float y, float w, float h, uint color, float rounding = 0f)
        {
            if (design == ProfileBadgeDesign.PixelWorkshop)
            {
                // Snap mirrored edges around a shared pixel center so scaling cannot
                // give the two eyes or frame sides different rasterized widths.
                var centerX = MathF.Round(P(40f, 0f).X);
                var left = centerX + MathF.Round((x - 40f) * scale);
                var right = centerX + MathF.Round((x + w - 40f) * scale);
                draw.AddRectFilled(new Vector2(left, MathF.Round(P(0f, y).Y)),
                    new Vector2(right, MathF.Round(P(0f, y + h).Y)), color);
                return;
            }
            if (rotation == 0f)
            {
                draw.AddRectFilled(P(x, y), P(x + w, y + h), color, rounding * scale);
                return;
            }
            var radius = MathF.Min(rounding, MathF.Min(w, h) * .5f);
            for (var corner = 0; corner < 4; corner++)
            {
                var cx = corner is 0 or 3 ? x + w - radius : x + radius;
                var cy = corner < 2 ? y + h - radius : y + radius;
                for (var step = 0; step <= 6; step++)
                {
                    var angle = (corner + step / 6f) * MathF.PI * .5f;
                    draw.PathLineTo(P(cx + MathF.Cos(angle) * radius, cy + MathF.Sin(angle) * radius));
                }
            }
            draw.PathFillConvex(color);
        }
        void Circle(float x, float y, float radius, uint color) =>
            draw.AddCircleFilled(P(x, y), radius * scale, color, 24);
        void Ellipse(float x, float y, float rx, float ry, uint color)
        {
            for (var i = 0; i < 32; i++)
            {
                var angle = i * MathF.Tau / 32f;
                draw.PathLineTo(P(x + MathF.Cos(angle) * rx, y + MathF.Sin(angle) * ry));
            }
            draw.PathFillConvex(color);
        }
        void Line(float x1, float y1, float x2, float y2, uint color, float thickness = 3f)
        {
            draw.AddLine(P(x1, y1), P(x2, y2), color, thickness * scale);
            Circle(x1, y1, thickness * .5f, color);
            Circle(x2, y2, thickness * .5f, color);
        }
        void Triangle(float x1, float y1, float x2, float y2, float x3, float y3, uint color) =>
            draw.AddTriangleFilled(P(x1, y1), P(x2, y2), P(x3, y3), color);
        void Quad(float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4, uint color) =>
            draw.AddQuadFilled(P(x1, y1), P(x2, y2), P(x3, y3), P(x4, y4), color);
        void Arc(float x, float y, float radius, float start, float end, uint color, float thickness = 3f)
        {
            for (var i = 0; i <= 16; i++)
            {
                var angle = start + (end - start) * i / 16f;
                draw.PathLineTo(P(x + MathF.Cos(angle) * radius, y + MathF.Sin(angle) * radius));
            }
            draw.PathStroke(color, ImDrawFlags.None, thickness * scale);
        }
        void Smile(float x, float y, uint color, float faceScale = 1f)
        {
            Circle(x - 10f * faceScale, y, 2.7f * faceScale, color);
            Circle(x + 10f * faceScale, y, 2.7f * faceScale, color);
            Arc(x, y + 4f * faceScale, 5f * faceScale, .15f, MathF.PI - .15f, color, 2.5f * faceScale);
        }
        void Heart(float x, float y, float radius, uint color)
        {
            Circle(x - radius * .45f, y - radius * .3f, radius * .58f, color);
            Circle(x + radius * .45f, y - radius * .3f, radius * .58f, color);
            Triangle(x - radius, y - radius * .05f, x + radius, y - radius * .05f,
                x, y + radius * 1.1f, color);
        }
        void Star(float x, float y, float radius, uint color)
        {
            for (var i = 0; i < 10; i++)
            {
                var a = -MathF.PI * .5f + i * MathF.PI / 5f;
                var b = a + MathF.PI / 5f;
                var ra = i % 2 == 0 ? radius : radius * .45f;
                var rb = i % 2 == 0 ? radius * .45f : radius;
                Triangle(x, y, x + MathF.Cos(a) * ra, y + MathF.Sin(a) * ra,
                    x + MathF.Cos(b) * rb, y + MathF.Sin(b) * rb, color);
            }
        }
        void Tv(uint body, bool face = false)
        {
            Line(47, 29, 31, 10, ink, 7);
            Line(47, 29, 66, 10, ink, 7);
            Line(47, 29, 31, 10, body, 4);
            Line(47, 29, 66, 10, body, 4);
            Circle(31, 10, 5, body);
            Circle(66, 10, 5, body);
            Rect(21, 77, 12, 12, ink, 3);
            Rect(65, 77, 12, 12, ink, 3);
            Rect(23, 77, 8, 10, body, 2);
            Rect(67, 77, 8, 10, body, 2);
            Rect(10, 25, 78, 59, ink, 13);
            Rect(13, 28, 72, 53, body, 10);
            Rect(21, 37, 49, 35, ink, 6);
            Circle(77, 45, 3, ink);
            Circle(77, 57, 3, ink);
            if (face) Smile(45, 51, cyan);
        }
        void Bucket(float x, float y, float width, float height, bool face)
        {
            var bottom = width * .73f;
            Quad(x - 2, y - 2, x + width + 2, y - 2, x + (width + bottom) * .5f + 2, y + height + 2,
                x + (width - bottom) * .5f - 2, y + height + 2, ink);
            Quad(x, y, x + width, y, x + (width + bottom) * .5f, y + height,
                x + (width - bottom) * .5f, y + height, pink);
            for (var i = 0; i < 2; i++)
            {
                var topX = x + width * (.18f + i * .4f);
                var bottomX = x + (width - bottom) * .5f + bottom * (.18f + i * .4f);
                Quad(topX, y, topX + width * .14f, y, bottomX + bottom * .14f, y + height,
                    bottomX, y + height, cream);
            }
            if (face) Smile(x + width * .5f, y + height * .32f, ink);
        }
        void Ghost(float x, float y, float width, float height)
        {
            Circle(x + width * .5f, y + width * .5f, width * .5f + 2, ink);
            Rect(x - 2, y + width * .45f, width + 4, height - width * .45f, ink, 4);
            Circle(x + width * .5f, y + width * .5f, width * .5f, cream);
            Rect(x, y + width * .45f, width, height - width * .45f - 4, cream);
            for (var i = 0; i < 3; i++) Circle(x + width * (i + .5f) / 3f, y + height - 5, width / 6f + 2, ink);
            Rect(x, y + width * .45f, width, height - width * .45f - 4, cream);
            for (var i = 0; i < 3; i++) Circle(x + width * (i + .5f) / 3f, y + height - 5, width / 6f, cream);
        }
        void Ribbon(float x, float y, float direction)
        {
            // Notched tails rather than rectangular hanging strips.
            Triangle(x, y, x + 18, y, x + 18 + direction, y + 28, blue);
            Triangle(x, y, x + 18 + direction, y + 28, x + 9 + direction, y + 21, blue);
            Triangle(x, y, x + 9 + direction, y + 21, x + direction, y + 28, blue);
        }

        switch (design)
        {
            case ProfileBadgeDesign.SofaSquad:
                // Three pals sit behind the couch, with popcorn in front.
                Circle(27, 30, 14, ink); Circle(50, 25, 16, ink); Circle(73, 30, 14, ink);
                Circle(27, 30, 11, cream); Circle(50, 25, 13, cream); Circle(73, 30, 11, cream);
                Smile(27, 29, ink, .5f); Smile(50, 24, ink, .6f); Smile(73, 29, ink, .5f);
                // The cushion tops overlap the lower heads rather than leaving a gap.
                Rect(12, 37, 29, 41, ink, 7); Rect(59, 37, 29, 41, ink, 7);
                Rect(15, 40, 23, 35, purple, 5); Rect(62, 40, 23, 35, purple, 5);
                Rect(36, 33, 28, 45, ink, 7); Rect(39, 36, 22, 39, purple, 5);
                Rect(11, 78, 12, 10, ink, 3); Rect(77, 78, 12, 10, ink, 3);
                Rect(14, 80, 7, 6, cyan, 2); Rect(79, 80, 7, 6, cyan, 2);
                Rect(7, 65, 86, 17, ink, 5); Rect(10, 68, 80, 11, purple, 3);
                Rect(2, 51, 22, 29, ink, 10); Rect(76, 51, 22, 29, ink, 10);
                Rect(5, 54, 16, 23, purple, 8); Rect(79, 54, 16, 23, purple, 8);
                Circle(43, 61, 7, ink); Circle(51, 57, 8, ink); Circle(59, 61, 7, ink);
                Circle(43, 61, 5, cream); Circle(51, 57, 6, cream); Circle(59, 61, 5, cream);
                Bucket(36, 65, 29, 28, face: false);
                break;
            case ProfileBadgeDesign.BigPremiere:
                Rect(7, 13, 86, 69, ink, 3);
                Rect(11, 17, 78, 61, cyan, 1);
                Smile(50, 47, ink, 1.15f);
                // Parted curtains retain a simple, readable cinema silhouette.
                Quad(10, 16, 30, 16, 23, 42, 10, 52, pink);
                Quad(90, 16, 70, 16, 77, 42, 90, 52, pink);
                Quad(10, 52, 22, 46, 27, 78, 10, 78, pink);
                Quad(90, 52, 78, 46, 73, 78, 90, 78, pink);
                Line(30, 17, 23, 42, ink, 2); Line(23, 42, 10, 52, ink, 2);
                Line(70, 17, 77, 42, ink, 2); Line(77, 42, 90, 52, ink, 2);
                Line(11, 52, 23, 47, ink, 3); Line(89, 52, 77, 47, ink, 3);
                Line(21, 52, 24, 75, ink, 1.5f); Line(79, 52, 76, 75, ink, 1.5f);
                // A filled swag hangs across the top, not a loose line on the screen.
                for (var i = 0; i <= 24; i++)
                {
                    var angle = i * MathF.PI / 24f;
                    draw.PathLineTo(P(50 + MathF.Cos(angle) * 23, 16 + MathF.Sin(angle) * 12));
                }
                draw.PathFillConvex(pink);
                Arc(50, 12, 17, .45f, MathF.PI-.45f, ink, 1.8f);
                for (var i = 0; i < 3; i++)
                {
                    var x = 27 + i * 23;
                    Circle(x, 75, 10, ink); Circle(x, 75, 8, cream);
                    Rect(x-12, 81, 24, 16, ink, 7);
                    Rect(x-10, 83, 20, 14, C(.24f,.43f,.68f), 5);
                    Rect(x-7, 86, 14, 11, C(.14f,.25f,.43f), 3);
                }
                break;
            case ProfileBadgeDesign.ChatBuddies:
                // Draw bubble tails first so they join the rounded bodies cleanly.
                Triangle(14, 65, 12, 81, 34, 71, ink);
                Triangle(17, 65, 16, 76, 31, 71, cyan);
                Triangle(78, 67, 90, 81, 89, 59, ink);
                Triangle(79, 66, 86, 75, 86, 62, purple);
                Ellipse(69, 55, 29, 26, ink); Ellipse(69, 55, 26, 23, purple);
                Ellipse(30, 51, 29, 27, ink); Ellipse(30, 51, 26, 24, cyan);
                // Put the front bubble's whole face inside its own silhouette.
                Smile(29, 48, ink, .9f); Smile(74, 53, ink, .85f);
                Heart(52, 17, 12, ink); Heart(52, 17, 9, pink);
                break;
            case ProfileBadgeDesign.ControllerCrunch:
                rotation = .12f;
                // Overlapping opaque lobes make a rounded gamepad outline.
                Ellipse(23, 60, 21, 31, ink); Ellipse(77, 60, 21, 31, ink);
                Rect(21, 27, 58, 43, ink, 13);
                Ellipse(23, 60, 18, 28, blue); Ellipse(77, 60, 18, 28, blue);
                Rect(21, 30, 58, 37, blue, 10);
                // A shorter centre panel leaves the two grips clearly separated.
                Rect(19, 44, 8, 24, ink, 1); Rect(11, 52, 24, 8, ink, 1);
                Circle(77, 44, 5, gold); Circle(67, 54, 5, gold);
                Circle(87, 54, 5, ink); Circle(77, 64, 5, pink);
                Smile(48, 49, ink, .75f);
                rotation = 0f;
                // Single straight filled bars avoid the dotted appearance of tiny capped lines.
                Quad(29, 8, 33, 6, 39, 18, 35, 20, orange);
                Quad(49, 5, 53, 6, 50, 20, 46, 19, orange);
                Quad(69, 9, 72, 12, 62, 22, 59, 19, orange);
                break;
            case ProfileBadgeDesign.ArcadeBuddy:
                // Cabinet, display, sloped control deck and lower coin-door panel.
                Quad(20, 14, 80, 14, 77, 66, 23, 66, ink);
                Quad(24, 17, 76, 17, 73, 63, 27, 63, purple);
                Rect(18, 6, 64, 17, ink, 3); Rect(22, 9, 56, 11, purple, 2);
                Line(27, 12, 73, 12, C(.78f,.53f,1f), 2);
                Rect(29, 26, 42, 32, ink, 4);
                Rect(33, 30, 34, 24, C(.08f,.21f,.37f), 2);
                Smile(50, 39, cyan);
                Quad(23, 60, 77, 60, 91, 78, 9, 78, ink);
                Quad(27, 63, 73, 63, 85, 74, 15, 74, purple);
                Rect(9, 74, 82, 22, ink, 2); Rect(13, 78, 74, 14, purple, 1);
                Rect(20, 80, 60, 9, C(.46f,.24f,.75f), 1);
                Line(29, 61, 29, 70, ink, 5); Line(29, 61, 29, 69, pink, 3);
                Circle(29, 57, 7, ink); Circle(29, 57, 5, pink);
                Ellipse(54, 69, 7, 5, ink); Ellipse(73, 69, 7, 5, ink);
                Ellipse(54, 68, 5, 3, gold); Ellipse(73, 68, 5, 3, gold);
                break;
            case ProfileBadgeDesign.VinylPal:
                Circle(46, 50, 39, ink);
                Circle(46, 50, 36, C(.10f,.18f,.30f));
                // Sparse concentric grooves, leaving a clear face at the centre.
                for (var i = 0; i < 3; i++)
                {
                    var radius = 25 + i * 3;
                    Arc(46, 50, radius, -2.8f, -1.05f, C(.20f,.31f,.46f), 1.8f);
                    Arc(46, 50, radius, .35f, 2.05f, C(.20f,.31f,.46f), 1.8f);
                }
                Circle(46, 50, 19, ink); Circle(46, 50, 17, gold);
                Smile(46, 49, ink, .9f);
                Line(87, 20, 80, 66, ink, 8); Line(87, 20, 80, 66, cyan, 4);
                Circle(87, 20, 6, ink); Circle(87, 20, 4, cyan);
                Quad(77, 61, 86, 66, 73, 83, 65, 77, ink);
                Quad(78, 65, 82, 67, 73, 79, 69, 76, cyan);
                // Three musical notes are simple stems and round noteheads.
                Circle(8, 29, 4, pink); Line(11, 29, 11, 17, pink, 2.5f);
                Line(11, 17, 17, 20, pink, 2.5f);
                Circle(10, 86, 4, pink); Line(13, 86, 13, 75, pink, 2.5f);
                Line(13, 75, 18, 78, pink, 2.5f);
                Circle(91, 57, 4, pink); Line(94, 57, 94, 45, pink, 2.5f);
                Line(94, 45, 98, 48, pink, 2.5f);
                break;
            case ProfileBadgeDesign.TvMechanic:
                Tv(cyan, face: true);
                Line(66, 83, 87, 53, ink, 13);
                Line(66, 83, 87, 53, silver, 8);
                Circle(87, 48, 11, ink);
                Circle(87, 48, 8, silver);
                Triangle(85, 37, 99, 44, 88, 49, ink);
                Circle(66, 83, 2, ink);
                break;
            case ProfileBadgeDesign.CodeChip:
                for (var i = 0; i < 3; i++)
                {
                    var offset = 29 + i * 19;
                    Rect(offset, 5, 8, 16, cyan, 2);
                    Rect(offset, 79, 8, 16, cyan, 2);
                    Rect(5, offset, 16, 8, cyan, 2);
                    Rect(79, offset, 16, 8, cyan, 2);
                }
                Rect(17, 17, 66, 66, ink, 10);
                Rect(20, 20, 60, 60, blue, 8);
                Rect(29, 29, 42, 42, ink, 5);
                Line(40, 41, 33, 50, cyan);
                Line(33, 50, 40, 59, cyan);
                Line(60, 41, 67, 50, cyan);
                Line(67, 50, 60, 59, cyan);
                Line(54, 38, 46, 62, cyan);
                break;
            case ProfileBadgeDesign.PixelWorkshop:
                // Connected stair-step aerials and corners keep the silhouette pixel-shaped.
                for (var i = 0; i < 3; i++)
                {
                    Rect(17 + i * 7, 5 + i * 7, 9, 9, ink);
                    Rect(54 - i * 7, 5 + i * 7, 9, 9, ink);
                    Rect(19 + i * 7, 7 + i * 7, 7, 7, cyan);
                    Rect(54 - i * 7, 7 + i * 7, 7, 7, cyan);
                }
                Rect(34, 26, 12, 10, blue);
                Rect(7, 39, 66, 43, ink);
                Rect(13, 33, 54, 54, ink);
                Rect(10, 41, 60, 38, blue);
                Rect(16, 36, 48, 48, blue);
                Rect(17, 44, 46, 31, ink);
                Rect(28, 56, 6, 6, cyan);
                Rect(46, 56, 6, 6, cyan);
                Rect(15, 82, 11, 9, blue);
                Rect(54, 82, 11, 9, blue);
                // A broad stepped blade and solid handle read clearly at badge size.
                Rect(81, 18, 10, 13, ink);
                Rect(84, 14, 4, 6, silver);
                Rect(83, 20, 6, 11, silver);
                Rect(84, 30, 4, 33, silver);
                Rect(77, 60, 18, 31, ink);
                Rect(80, 63, 12, 25, gold);
                Rect(83, 69, 5, 15, ink);
                Rect(82, 88, 8, 5, gold);
                break;
            case ProfileBadgeDesign.HeartTv:
                Tv(gold);
                Heart(45, 53, 12, pink);
                Heart(85, 17, 7, pink);
                break;
            case ProfileBadgeDesign.HeartTicket:
                Ribbon(31, 63, -9);
                Ribbon(51, 63, 9);
                Rect(9, 21, 82, 48, ink, 4);
                Rect(12, 24, 76, 42, gold, 2);
                Circle(11, 45, 6, ink);
                Circle(89, 45, 6, ink);
                Circle(10, 45, 4, ImGui.GetColorU32(CardBg));
                Circle(90, 45, 4, ImGui.GetColorU32(CardBg));
                draw.AddRect(P(23, 30), P(77, 60), C(.55f, .31f, .08f), 0, ImDrawFlags.None, 2 * scale);
                Heart(50, 44, 12, pink);
                break;
            case ProfileBadgeDesign.PopcornPal:
                Circle(30, 35, 14, ink); Circle(51, 25, 17, ink); Circle(72, 35, 14, ink);
                Circle(30, 35, 11, cream); Circle(51, 25, 14, cream); Circle(72, 35, 11, cream);
                Circle(45, 39, 10, cream); Circle(62, 40, 10, cream);
                Bucket(18, 45, 64, 46, face: true);
                break;
            case ProfileBadgeDesign.FirstBroadcast:
                Tv(blue);
                for (var i = 0; i <= 16; i++)
                {
                    var angle = MathF.PI + i * MathF.PI / 16f;
                    draw.PathLineTo(P(45 + MathF.Cos(angle) * 12, 65 + MathF.Sin(angle) * 12));
                }
                draw.PathFillConvex(gold);
                Line(45, 42, 45, 47, gold, 2);
                Line(29, 47, 33, 51, gold, 2);
                Line(61, 47, 57, 51, gold, 2);
                Star(69, 11, 10, gold);
                break;
            case ProfileBadgeDesign.LaunchTv:
                rotation = .23f;
                Triangle(27, 35, 9, 63, 30, 58, orange);
                Triangle(72, 35, 91, 63, 70, 58, orange);
                Triangle(37, 66, 63, 66, 50, 92, orange);
                Triangle(44, 66, 56, 66, 50, 83, gold);
                Line(49, 24, 43, 10, cyan, 3);
                Circle(43, 10, 4, cyan);
                Line(57, 24, 67, 12, cyan, 3);
                Circle(67, 12, 4, cyan);
                Rect(24, 22, 52, 46, ink, 8);
                Rect(27, 25, 46, 40, cyan, 6);
                Rect(34, 32, 32, 26, ink, 4);
                Smile(50, 43, cream);
                rotation = 0f;
                Circle(18, 87, 9, cyan); Circle(28, 89, 7, cyan);
                Circle(76, 88, 9, cyan); Circle(87, 90, 6, cyan);
                break;
            case ProfileBadgeDesign.FoundersPin:
                Ribbon(29, 64, -8);
                Ribbon(53, 64, 8);
                Rect(17, 26, 66, 44, ink, 9);
                Rect(20, 29, 60, 38, silver, 7);
                Rect(29, 36, 42, 23, ink, 4);
                Smile(50, 44, silver);
                Star(50, 18, 13, gold);
                break;
            case ProfileBadgeDesign.PumpkinTv:
                Line(48, 28, 30, 10, orange, 4);
                Line(48, 28, 65, 10, orange, 4);
                Circle(30, 10, 5, orange); Circle(65, 10, 5, orange);
                Rect(24, 79, 9, 10, orange, 2); Rect(68, 79, 9, 10, orange, 2);
                Ellipse(34, 55, 23, 31, ink); Ellipse(65, 55, 23, 31, ink);
                Ellipse(35, 55, 21, 28, orange); Ellipse(64, 55, 21, 28, orange);
                Ellipse(49, 54, 24, 30, orange);
                var pumpkinRib = C(.76f, .29f, .07f);
                Arc(47, 55, 28, -2.05f, -1.35f, pumpkinRib, 2);
                Arc(52, 55, 28, -1.8f, -1.1f, pumpkinRib, 2);
                Arc(47, 55, 28, 1.35f, 2.05f, pumpkinRib, 2);
                Arc(52, 55, 28, 1.1f, 1.8f, pumpkinRib, 2);
                Rect(23, 39, 47, 32, ink, 7);
                Triangle(29, 52, 35, 43, 41, 52, gold);
                Triangle(49, 52, 55, 43, 61, 52, gold);
                Arc(45, 56, 8, 0, MathF.PI, gold, 4);
                Rect(42, 57, 4, 5, gold);
                Circle(77, 46, 2.5f, ink); Circle(77, 58, 2.5f, ink);
                Quad(76, 27, 80, 12, 94, 9, 88, 24, purple);
                break;
            case ProfileBadgeDesign.MovieGhost:
                Ghost(10, 8, 59, 80);
                Rect(20, 34, 20, 15, purple, 3);
                Rect(45, 34, 20, 15, purple, 3);
                Rect(24, 38, 12, 7, ink, 1);
                Rect(49, 38, 12, 7, ink, 1);
                Line(39, 40, 45, 40, purple, 3);
                Line(13, 40, 20, 40, purple, 3);
                Arc(42, 53, 6, 0, MathF.PI, ink, 2.5f);
                Circle(77, 56, 7, cream); Circle(86, 54, 6, cream); Circle(92, 59, 5, cream);
                Bucket(68, 61, 28, 27, face: false);
                break;
            case ProfileBadgeDesign.HauntedTape:
                // Loose tape curls behind the case, then turns back under it.
                draw.AddBezierCubic(P(85, 61), P(108, 68), P(101, 99), P(77, 95), ink, 8 * scale);
                draw.AddBezierCubic(P(77, 95), P(63, 88), P(58, 85), P(49, 96), ink, 8 * scale);
                draw.AddBezierCubic(P(85, 61), P(108, 68), P(101, 99), P(77, 95), orange, 5 * scale);
                draw.AddBezierCubic(P(77, 95), P(63, 88), P(58, 85), P(49, 96), orange, 5 * scale);
                // A broad domed head peeks over the tape; the body stays hidden.
                Ellipse(49, 24, 20, 19, ink);
                Ellipse(49, 24, 18, 17, cream);
                Rect(31, 24, 36, 18, cream);
                Ellipse(43, 20, 2.7f, 3.6f, ink);
                Ellipse(56, 20, 2.7f, 3.6f, ink);
                Rect(8, 34, 81, 47, ink, 6);
                Rect(11, 37, 75, 41, purple, 4);
                Line(15, 42, 82, 42, ink, 2);
                Rect(19, 48, 59, 25, ink, 5);
                Circle(32, 60, 9.5f, cream); Circle(65, 60, 9.5f, cream);
                Circle(32, 60, 8, cyan); Circle(65, 60, 8, cyan);
                Circle(32, 60, 4, ink); Circle(65, 60, 4, ink);
                Rect(37, 49, 23, 23, ink, 1);
                Rect(39, 53, 19, 15, C(.12f, .19f, .31f), 1);
                Circle(30, 35, 5, ink); Circle(68, 35, 5, ink);
                Circle(30, 35, 4.5f, cream); Circle(68, 35, 4.5f, cream);
                break;
        }
    }
}

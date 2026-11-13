using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly uint[] ArcadeMosaicColours =
        [0x55C9F5, 0xF35BAF, 0xFCBE45, 0x7B59DE, 0x65D985, 0xEDE5FF, 0xF06959, 0x3355A2, 0xCC9BEC, 0x102038];

    private static bool DrawArcadePreviewArtwork(ImDrawListPtr draw, ActivityPreviewKind kind, Vector2 origin, Vector2 size)
    {
        if (kind is not (ActivityPreviewKind.Pixels or ActivityPreviewKind.Whiteboard or ActivityPreviewKind.Pictionary or
            ActivityPreviewKind.PictionaryTeams or ActivityPreviewKind.Quiz or ActivityPreviewKind.RacePreview or ActivityPreviewKind.Poker or
            ActivityPreviewKind.Roulette or ActivityPreviewKind.LuckyWheel or ActivityPreviewKind.Bingo or ActivityPreviewKind.BlastParty or ActivityPreviewKind.SkyRail or ActivityPreviewKind.SlotMachine))
            return false;
        var background = kind == ActivityPreviewKind.RacePreview ? Hex(0x35204B) :
            kind == ActivityPreviewKind.Poker ? Hex(0x0A2524) : Hex(0x060C16);
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(background), Ui(6f));
        var min = origin + size * .07f;
        var extent = size * .86f;
        Vector2 P(float x, float y) => min + extent * new Vector2(x, y);
        void Rect(float x, float y, float w, float h, uint rgb) =>
            draw.AddRectFilled(P(x, y), P(x + w, y + h), ImGui.GetColorU32(Hex((int)rgb)));
        void Sprite(string bitmap, int columns, Vector2 start, float pixel, uint rgb)
        {
            var ink = ImGui.GetColorU32(Hex((int)rgb));
            for (var i = 0; i < bitmap.Length; i++)
                if (bitmap[i] == '1')
                {
                    var p = start + new Vector2(i % columns, i / columns) * pixel;
                    draw.AddRectFilled(p, p + new Vector2(pixel), ink);
                }
        }
        void PixelLine(float x, float y, float endX, float endY, uint rgb)
        {
            var start = P(x, y);
            var end = P(endX, endY);
            var pixel = extent.X / 32f;
            var steps = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(start, end) / pixel));
            for (var i = 0; i <= steps; i++)
            {
                var p = min + Vector2.Round((Vector2.Lerp(start, end, i / (float)steps) - min) / pixel) * pixel;
                draw.AddRectFilled(p, p + new Vector2(pixel), ImGui.GetColorU32(Hex((int)rgb)));
            }
        }
        if (kind == ActivityPreviewKind.Pixels)
        {
            const int grid = 20;
            Rect(0, 0, 1, 1, 0x0B1527);
            var gridInk = ImGui.GetColorU32(Hex(0x19273D));
            for (var i = 0; i <= grid; i++)
            {
                var t = i / (float)grid;
                draw.AddLine(P(t, 0), P(t, 1), gridInk, Ui(.5f));
                draw.AddLine(P(0, t), P(1, t), gridInk, Ui(.5f));
            }
            void Cell(int x, int y, uint colour) =>
                Rect((x + .10f) / grid, (y + .10f) / grid, .80f / grid, .80f / grid, colour);

            // Fixed scattered contributions: the unfinished board never flickers or animates.
            for (var y = 0; y < grid; y++)
            for (var x = 0; x < grid; x++)
            {
                if (x is >= 1 and <= 9 && y is >= 1 and <= 8 ||
                    x is >= 10 and <= 18 && y is >= 10 and <= 18 || x == 8 && y == 13) continue;
                var hash = (uint)(x * 374761393 + y * 668265263);
                hash = (hash ^ (hash >> 13)) * 1274126177u;
                hash ^= hash >> 16;
                if (hash % 5 == 0) Cell(x, y, ArcadeMosaicColours[(hash >> 8) % 7]);
            }
            const string heart = "0110110" + "1111111" + "1111111" + "0111110" + "0011100" + "0001000";
            for (var i = 0; i < heart.Length; i++)
                if (heart[i] == '1') Cell(2 + i % 7, 2 + i / 7, i < 14 ? 0xFF8AC4u : 0xF35BAFu);

            const string sprout = "110000011" + "111000111" + "011101110" + "001111100" +
                "000111000" + "000010000" + "000010000" + "001111100";
            for (var i = 0; i < sprout.Length; i++)
                if (sprout[i] == '1') Cell(10 + i % 9, 11 + i / 9,
                    i / 9 == 7 ? 0xBC8A57u : i % 9 < 4 ? 0x8AE7A5u : 0x4DCB80u);

            Cell(5, 13, 0x55C9F5); Cell(6, 13, 0x55C9F5); Cell(6, 14, 0x55C9F5);
            draw.AddRect(P(8f / grid, 13f / grid), P(9f / grid, 14f / grid),
                ImGui.GetColorU32(Hex(0x55B8FF)), 0, ImDrawFlags.None, Ui(1f));
        }
        else if (kind == ActivityPreviewKind.RacePreview)
        {
            for (var lane = 0; lane < 6; lane++)
            {
                var y = .20f + lane * .133f;
                draw.AddLine(P(.03f, y + .09f), P(.89f, y + .09f), ImGui.GetColorU32(Hex(0x674879)), Ui(1f));
                var start = P(.16f + lane * .10f, y);
                var pixel = extent.X * .022f;
                Sprite("000110000011100011111001111100111111000101000010100", 7, start - new Vector2(0, pixel * 3),
                    pixel, ArcadeMosaicColours[lane switch { 0 => 2, 1 => 0, 2 => 6, 3 => 4, 4 => 1, _ => 3 }]);
            }
            for (var y = 0; y < 24; y++)
            for (var x = 0; x < 2; x++)
                Rect(.91f + x * .035f, .045f + y * .038f, .035f, .038f, (x + y) % 2 == 0 ? 0xF5F2FFu : 0x1D152Du);
        }
        else if (kind == ActivityPreviewKind.Poker)
        {
            for (var i = 0; i < 7; i++)
            for (var row = 0; row < 2; row++)
            {
                var y = row == 0 ? .06f : .83f;
                Sprite("111101101111", 3, P(.055f + i * .13f, y), extent.X * .027f, 0xE6B85B);
            }
            const int grid = 32;
            for (var y = 0; y < grid; y++)
            for (var x = 0; x < grid; x++)
            {
                var dx = ((x + .5f) / grid - .5f) / .47f;
                var dy = ((y + .5f) / grid - .5f) / .27f;
                var distance = dx * dx + dy * dy;
                if (distance > 1f) continue;
                var colour = distance > .81f ? 0xF4C75Eu : distance > .66f ? 0x23584Eu : 0x167355u;
                Rect(x / (float)grid, y / (float)grid, 1f / grid, 1f / grid, colour);
            }
            for (var i = 0; i < 5; i++)
            {
                var left = .24f + i * .108f;
                Rect(left, .375f, .084f, .27f, 0x183E39);
                Rect(left, .36f, .084f, .26f, 0xFFF0CC);
                Rect(left + .018f, .39f, .025f, .035f, i % 2 == 0 ? 0xE05C72u : 0x33425Bu);
                Rect(left + .03f, .48f, .025f, .035f, i % 2 == 0 ? 0xE05C72u : 0x33425Bu);
            }
        }
        else if (kind is ActivityPreviewKind.Roulette or ActivityPreviewKind.LuckyWheel)
        {
            // Flat, deliberately chunky sectors replace the fine radial lines of the game preview.
            const int grid = 30;
            for (var y = 0; y < grid; y++)
            for (var x = 0; x < grid; x++)
            {
                var delta = new Vector2(x + .5f - grid / 2f, y + .5f - grid / 2f);
                var radius = delta.Length() / (grid / 2f);
                if (radius > .93f) continue;
                var angle = (MathF.Atan2(delta.Y, delta.X) + MathF.Tau) % MathF.Tau;
                uint colour;
                if (radius > .84f) colour = 0xF4C75E;
                else if (kind == ActivityPreviewKind.LuckyWheel)
                {
                    var sector = (int)(angle / MathF.Tau * 8);
                    colour = ArcadeMosaicColours[sector];
                    if (radius < .15f) colour = 0xFFF2C2;
                }
                else
                {
                    var sector = (int)(angle / MathF.Tau * 16);
                    colour = radius > .59f ? sector == 0 ? 0x42C98Au : sector % 2 == 0 ? 0xE55070u : 0x18253Bu
                        : radius > .52f ? 0xF4C75Eu : radius < .16f ? 0xF4C75Eu : 0x10263Au;
                }
                Rect(x / (float)grid, y / (float)grid, 1f / grid, 1f / grid, colour);
            }
            if (kind == ActivityPreviewKind.LuckyWheel)
            {
                Sprite("111111111011111001110000100", 5, P(.415f, -.015f), extent.X * .034f, 0xFFF4CC);
                Rect(.46f, .455f, .08f, .08f, 0xF4C75E);
            }
            else
            {
                Rect(.78f, .20f, .065f, .065f, 0xFFFFFF);
                Rect(.80f, .18f, .025f, .025f, 0xFFFFFF);
                PixelLine(.5f, .5f, .64f, .36f, 0xF4C75E);
            }
        }
        else if (kind == ActivityPreviewKind.Bingo)
        {
            string[] letters = ["110101110101110", "111010010010111", "101111111111101", "111100101101111", "111101101101111"];
            for (var i = 0; i < 5; i++)
            {
                var x = .02f + i * .196f;
                Rect(x, .04f, .175f, .22f, ArcadeMosaicColours[i]);
                Sprite(letters[i], 3, P(x + .035f, .068f), extent.X * .035f, 0x101A30);
            }
            for (var y = 0; y < 3; y++)
            for (var x = 0; x < 5; x++)
            {
                var left = .04f + x * .19f;
                var top = .34f + y * .20f;
                var marked = (x + y * 3) % 4 == 1;
                Rect(left, top, .15f, .15f, marked ? 0x294769u : 0x14243Bu);
                draw.AddRect(P(left, top), P(left + .15f, top + .15f), ImGui.GetColorU32(Hex(0x456586)), Ui(1f));
                if (marked) DrawArcadeStar(draw, P(left + .075f, top + .075f), extent.X * .045f, ImGui.GetColorU32(Gold));
                else Rect(left + .055f, top + .055f, .04f, .04f, 0xB6CAE6);
            }
        }
        else if (kind == ActivityPreviewKind.SlotMachine)
        {
            Rect(.08f, .04f, .80f, .92f, 0x512944);
            Rect(.12f, .08f, .72f, .16f, 0xE8AF46);
            Rect(.17f, .11f, .62f, .10f, 0x231C33);
            for (var i = 0; i < 3; i++)
            {
                var left = .14f + i * .23f;
                Rect(left, .31f, .20f, .43f, 0xFFF0CF);
                Rect(left, .31f, .20f, .04f, 0xD8BA96);
                Rect(left, .70f, .20f, .04f, 0xD8BA96);
                Sprite("111001001001001", 3, P(.24f + i * .20f, .125f), extent.X * .015f, 0xF4C75E);
            }
            Sprite("110111111110", 3, P(.165f, .44f), extent.X * .043f, 0xF26788);
            Sprite("0010001110111110111000100", 5, P(.39f, .415f), extent.X * .033f, 0xEAAB30);
            Sprite("111000101010100", 3, P(.635f, .42f), extent.X * .038f, 0xA163D5);
            PixelLine(.20f, .43f, .26f, .38f, 0x46A86F);
            Rect(.15f, .82f, .66f, .06f, 0xF4C75E);
            Rect(.90f, .26f, .035f, .39f, 0x8FAACB);
            Rect(.875f, .22f, .09f, .10f, 0xF26788);
        }
        else if (kind == ActivityPreviewKind.BlastParty)
        {
            for (var y = 0; y < 9; y++) for (var x = 0; x < 15; x++)
            {
                var wall = x is 0 or 14 || y is 0 or 8 || x % 2 == 0 && y % 2 == 0;
                Rect(x / 15f, y / 9f, .062f, .105f, wall ? 0x344A68u : (x * 11 + y * 7) % 5 < 3 ? 0xA47850u : 0x162A3Du);
            }
            Rect(.075f, .13f, .035f, .065f, 0x6BD8FF);
            Rect(.87f, .80f, .035f, .065f, 0xFF83B8);
            Rect(.47f, .43f, .035f, .065f, 0xFFCF70);
        }
        else if (kind == ActivityPreviewKind.SkyRail)
        {
            // A little shooting-gallery scene, rather than four disconnected rings.
            Rect(.09f, .13f, .025f, .025f, 0x52698F); Rect(.85f, .45f, .025f, .025f, 0x52698F);
            Rect(.69f, .08f, .025f, .025f, 0x52698F); Rect(.10f, .69f, .025f, .025f, 0x52698F);
            Sprite("0101011111111111010101010", 5, P(.10f, .22f), extent.X * .037f, 0xF26788);
            Sprite("0101011111111111010101010", 5, P(.70f, .17f), extent.X * .037f, 0xF4C75E);
            PixelLine(.45f, .08f, .60f, .08f, 0x55C9F5); PixelLine(.60f, .08f, .60f, .25f, 0x55C9F5);
            PixelLine(.60f, .25f, .45f, .25f, 0x55C9F5); PixelLine(.45f, .25f, .45f, .08f, 0x55C9F5);
            Rect(.51f, .14f, .035f, .035f, 0xFFF0CF);
            Rect(.48f, .42f, .025f, .12f, 0x55C9F5); Rect(.55f, .34f, .025f, .09f, 0x55C9F5);
            Sprite("000010000000111000001111100011111110111111111000111000000111000001010100010000010", 9,
                P(.28f, .59f), extent.X * .047f, 0x76CFFF);
            Rect(.465f, .75f, .07f, .09f, 0xF4C75E);
        }
        else if (kind == ActivityPreviewKind.Quiz)
        {
            for (var i = 0; i < 4; i++)
            {
                var x = .04f + i % 2 * .50f;
                var y = .04f + i / 2 * .50f;
                var colour = ArcadeMosaicColours[i == 0 ? 0 : i == 1 ? 6 : i == 2 ? 4 : 2];
                Rect(x + .03f, y, .36f, .42f, colour);
                Rect(x, y + .03f, .42f, .36f, colour);
                Rect(x + .03f, y + .03f, .36f, .36f, 0x101D30);
                var letter = i switch
                {
                    0 => "010101111101101",
                    1 => "110101110101110",
                    2 => "111100100100111",
                    _ => "110101101101110",
                };
                Sprite(letter, 3, P(x + .155f, y + .115f), extent.X * .044f, 0x050C16);
                Sprite(letter, 3, P(x + .144f, y + .10f), extent.X * .044f, colour);
            }
        }
        else if (kind == ActivityPreviewKind.Whiteboard)
        {
            const uint purple = 0xC9ADFF;
            PixelLine(.12f, .76f, .12f, .43f, purple); PixelLine(.12f, .43f, .20f, .31f, purple);
            PixelLine(.20f, .31f, .20f, .14f, purple); PixelLine(.20f, .14f, .35f, .29f, purple);
            PixelLine(.35f, .29f, .52f, .29f, purple); PixelLine(.52f, .29f, .64f, .14f, purple);
            PixelLine(.64f, .14f, .64f, .38f, purple); PixelLine(.64f, .38f, .72f, .49f, purple);
            PixelLine(.72f, .49f, .72f, .73f, purple); PixelLine(.72f, .73f, .59f, .86f, purple);
            PixelLine(.59f, .86f, .35f, .86f, purple); PixelLine(.35f, .86f, .35f, .97f, purple);
            Rect(.28f, .48f, .05f, .07f, purple); Rect(.52f, .48f, .05f, .07f, purple);
            PixelLine(.39f, .64f, .46f, .64f, purple);
            Sprite("0101011111111110111000100", 5, P(.73f, .58f), extent.X * .035f, 0xF35BAF);
            Rect(.79f, .12f, .13f, .13f, 0xFCBE45);
            PixelLine(.84f, .03f, .84f, .07f, 0xFCBE45); PixelLine(.72f, .17f, .75f, .17f, 0xFCBE45);
            PixelLine(.96f, .17f, .99f, .17f, 0xFCBE45); PixelLine(.84f, .31f, .84f, .35f, 0xFCBE45);
        }
        else
        {
            const string cat = "001000100011101110011111110111111111111111111111111111011111110001000100";
            if (kind == ActivityPreviewKind.PictionaryTeams)
            {
                Rect(.49f, .04f, .02f, .92f, 0x6E819E);
                Sprite(cat, 9, P(.05f, .37f), extent.X * .038f, 0xFF6688);
                Sprite(cat, 9, P(.59f, .37f), extent.X * .038f, 0x55C9F5);
                for (var side = 0; side < 2; side++)
                {
                    var x = .05f + side * .54f;
                    Rect(x + .076f, .522f, .038f, .038f, 0x182238);
                    Rect(x + .228f, .522f, .038f, .038f, 0x182238);
                }
            }
            else
            {
                var pixel = extent.X * .066f;
                var start = P(.20f, .30f);
                Sprite(cat, 9, start, pixel, 0xFCBE45);
                draw.AddRectFilled(start + new Vector2(pixel * 2, pixel * 4), start + new Vector2(pixel * 3, pixel * 5), ImGui.GetColorU32(Hex(0x182238)));
                draw.AddRectFilled(start + new Vector2(pixel * 6, pixel * 4), start + new Vector2(pixel * 7, pixel * 5), ImGui.GetColorU32(Hex(0x182238)));
                Sprite("111000101110100000100", 3, P(.06f, .06f), extent.X * .042f, 0xEDE5FF);
                Sprite("111000101110100000100", 3, P(.81f, .06f), extent.X * .042f, 0xEDE5FF);
            }
        }
        return true;
    }
}

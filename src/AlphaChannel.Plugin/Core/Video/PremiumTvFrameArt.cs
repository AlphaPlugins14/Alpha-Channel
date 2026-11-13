using System.Numerics;

namespace AlphaChannel.Plugin.Video;

// Code-drawn badge-style artwork. The UI and world renderer share these pixels.
// Coordinates are on a 1000x720 canvas around a clear 680x408 picture.
internal static class PremiumTvFrameArt
{
    internal const int Width = 1000, Height = 720;
    internal const float ExtentX = 1000f / 680, ExtentY = 720f / 408;
    private static readonly Lazy<Task<byte[]>>[] Images = Enumerable.Range(0, Enum.GetValues<TvFrameStyle>().Length)
        .Select(i => new Lazy<Task<byte[]>>(() => Task.Run(() => Render((TvFrameStyle)i)))).ToArray();

    internal static bool IsPremium(TvFrameStyle style) => style >= TvFrameStyle.Catnap && style <= TvFrameStyle.RaidNight;
    internal static Task<byte[]> GetPixels(TvFrameStyle style) => Images[(int)style].Value;
    private static readonly object VariantGate = new();
    private static readonly Dictionary<TvFrameSelection, Task<byte[]>> Variants = new();
    private static readonly Queue<TvFrameSelection> VariantOrder = new();
    internal static Task<byte[]> GetPixels(TvFrameSelection frame)
    {
        var key = TvFrameAppearance.TextureKey(frame);
        if (!TvFrameAppearance.SupportsColour(key.Style)) return GetPixels(key.Style);
        lock (VariantGate)
        {
            if (Variants.TryGetValue(key, out var cached)) return cached;
            var task = Task.Run(() => Render(key));
            Variants.Add(key, task); VariantOrder.Enqueue(key);
            // Bound retained CPU images while custom colour pickers produce many variants.
            while (VariantOrder.Count > 16) Variants.Remove(VariantOrder.Dequeue());
            return task;
        }
    }

    internal static byte[] Render(TvFrameStyle style) => Render(new TvFrameSelection(style));
    internal static byte[] Render(TvFrameSelection selection)
    {
        selection = selection.Normalized();
        var style = selection.Style;
        var accent = selection.Colour;
        var c = new Canvas();
        switch (style)
        {
            case TvFrameStyle.Catnap:
                Border(c, 0x785342, 0xF5D49A);
                Cat(c, 224, 110, 0xF3CB8C, false); Cat(c, 766, 110, 0x686574, true);
                break;
            case TvFrameStyle.LittleSpectators:
                Border(c, 0x24406A, 0x5DD7EF);
                Robot(c, 242, 105, 0x42C8EC); Robot(c, 762, 105, 0xF36B95);
                Popcorn(c, 500, 112);
                break;
            case TvFrameStyle.DragonsHoard:
                Border(c, 0x8C5727, 0xFFCD58);
                Dragon(c, 247, 106);
                foreach (var coin in new[] { (427f, 589f), (444f, 599f), (565f, 589f), (582f, 599f) })
                { c.Ellipse(coin.Item1, coin.Item2, 12, 5, 0xA56E32); c.Ellipse(coin.Item1, coin.Item2 - 3, 12, 4, 0xF3C968); }
                Gem(c, 458, 583, 17, 0x40BCE7); Gem(c, 500, 588, 23, 0xEB7192); Gem(c, 547, 583, 17, 0x72CF82);
                break;
            case TvFrameStyle.Botanical:
                Border(c, 0x315E3A, 0x9CCE71);
                Vine(c, false); Vine(c, true);
                Flower(c, 190, 118, 14); Flower(c, 880, 229, 13, 0xF1D6CE);
                Flower(c, 798, 122, 11); Flower(c, 805, 591, 12, 0xF1D6CE);
                Flower(c, 120, 445, 9);
                break;
            case TvFrameStyle.Celestial:
                c.Glow = true;
                Border(c, Shade(accent, .32f), accent, 10);
                foreach (var x in new[] { 120f, 880f })
                {
                    Star(c, x, 424, 18, Tint(accent, .25f));
                }
                Star(c, 320, 130, 13, Tint(accent, .35f)); Star(c, 680, 130, 13, Tint(accent, .35f)); Star(c, 500, 127, 9, Tint(accent, .35f));
                c.Glow = false;
                break;
            case TvFrameStyle.Storybook:
                c.Rect(126, 126, 748, 472, 0x6C3B31, 18);
                c.Rect(136, 130, 728, 454, 0xB77B55, 13);
                c.Poly(0xF4DDB2, 145, 139, 185, 124, 420, 126, 500, 145, 579, 126, 817, 124, 855, 139, 855, 575, 509, 590, 500, 582, 490, 590, 145, 575);
                for (var i = 0; i < 3; i++) c.Rect(148, 573 + i * 4, 704, 2, 0xBA9369);
                c.Line(0x987044, 3, 500, 130, 500, 156); c.Ellipse(500, 586, 24, 11, 0x945244);
                c.Poly(0xDB5971, 279, 574, 310, 574, 310, 632, 294, 619, 279, 632);
                c.Poly(0x4779B8, 695, 574, 725, 574, 725, 628, 710, 615, 695, 628);
                break;
            case TvFrameStyle.GrandCinema:
                Curtain(c, false, accent); Curtain(c, true, accent);
                Border(c, 0x242C3C, 0x5D6675, 17);
                for (var x = 0; x < 2; x++) for (var y = 0; y < 2; y++)
                {
                    var xx = x == 0 ? 138 : 818; var yy = y == 0 ? 133 : 545;
                    c.Rect(xx, y == 0 ? yy : yy + 27, 44, 15, 0xBBC4CC, 3);
                    c.Rect(x == 0 ? xx : xx + 29, yy, 15, 42, 0xBBC4CC, 3);
                    c.Ellipse(xx + 8, y == 0 ? yy + 7 : yy + 35, 3, 3, 0x4F5969);
                }
                break;
            case TvFrameStyle.RoyalGallery:
                Border(c, 0x694C31, 0xE6AD43, 20);
                c.Rect(143, 139, 714, 442, 0x805B2C, 3); c.Rect(149, 145, 702, 430, 0xF2D278, 3);
                foreach (var x in new[] { 145f, 855f }) foreach (var y in new[] { 138f, 580f })
                {
                    Gem(c, x, y, 23, accent);
                }
                Gem(c, 500, 137, 13, Tint(accent, .25f)); Gem(c, 500, 582, 13, Tint(accent, .25f));
                break;
            case TvFrameStyle.ArcadeDeluxe:
                TechBorder(c, 0x273649, 0x65718B);
                c.Poly(0x263348, 350, 144, 376, 96, 626, 96, 652, 144);
                c.Poly(0x0B172B, 377, 130, 391, 107, 610, 107, 626, 130);
                foreach (var x in new[] { 136f, 864f })
                {
                    c.Glow = true;
                    c.Rect(x - 7, 204, 14, 75, Tint(accent, .20f), 5); c.Rect(x - 7, 314, 14, 75, accent, 5);
                    Speaker(c, x, 553, 35, accent);
                    c.Glow = false;
                }
                for (var i = 0; i < 3; i++) { c.Ellipse(455 + i * 43, 577, 12, 8, new uint[] { 0xF77599, 0xFFCF5A, 0x61D8AD }[i]); }
                break;
            case TvFrameStyle.RetroDeluxe:
                c.Line(0xC0B59B, 5, 491, 128, 449, 78); c.Line(0xC0B59B, 5, 509, 128, 551, 78);
                c.Ellipse(449, 78, 5, 5, 0xECE0C7); c.Ellipse(551, 78, 5, 5, 0xECE0C7);
                c.Rect(137, 131, 765, 461, 0x543B31, 34); c.Rect(145, 139, 749, 445, 0xAD7853, 28);
                c.Rect(150, 145, 698, 430, 0xE4C29A, 25); c.Rect(156, 151, 686, 418, 0x3B3536, 20);
                foreach (var y in new[] { 227f, 313f })
                { c.Ellipse(871, y, 22, 22, 0x493C35); c.Ellipse(871, y, 17, 17, 0xD5C6AA); c.Line(0x655B4F, 3, 865, y - 13, 877, y + 13); }
                for (var i = 0; i < 5; i++) c.Rect(856, 399 + i * 13, 29, 5, 0x5C4131, 2);
                c.Ellipse(870, 535, 7, 7, 0x79E784);
                break;
            case TvFrameStyle.BroadcastBooth:
                TechBorder(c, 0x233347, 0x64798A);
                c.Rect(382, 107, 236, 47, 0x596879, 7); c.Rect(391, 114, 218, 32, 0x4F1826, 3);
                c.Text("ON AIR", 421, 120, 3, 0xFF797B);
                Microphone(c, 137, 528);
                c.Glow = true;
                c.Rect(221, 139, 82, 6, accent, 3); c.Rect(697, 139, 82, 6, accent, 3);
                for (var i = 0; i < 3; i++) c.Ellipse(748 + i * 24, 586, 6, 6, accent);
                c.Glow = false;
                break;
            case TvFrameStyle.MidnightScreening:
                c.Glow = true;
                Border(c, Shade(accent, .40f), accent);
                c.Glow = false;
                Bat(c, 214, 120); Bat(c, 788, 120);
                Candle(c, 121, 546); Candle(c, 879, 546);
                break;
            case TvFrameStyle.PumpkinPatch:
                Ghost(c, 500, 111);
                Border(c, 0x71367C, 0xF1A54D);
                Pumpkin(c, 113, 560, 45); Pumpkin(c, 887, 560, 45);
                break;
            case TvFrameStyle.WinterLodge:
                Border(c, 0x694A3E, 0xBE926B);
                Tree(c, 220, 122, .8f); Tree(c, 790, 122, .8f); Tree(c, 132, 575, .7f);
                c.Line(0xDDEAF6, 22, 140, 145, 180, 133, 229, 147, 275, 139);
                c.Line(0xDDEAF6, 22, 725, 141, 772, 147, 820, 132, 860, 145);
                c.Line(0x5E7460, 2, 280, 135, 382, 146, 500, 151, 617, 146, 720, 135);
                for (var i = 0; i < 7; i++) c.Ellipse(300 + i * 66, 147 + MathF.Sin(i * .52f) * 5, 7, 9, new uint[] { 0xF87899, 0xFFE072, 0x68DBB5, 0x61C8F6, 0xC987F2, 0xFFCA6B, 0xF87899 }[i]);
                Snowman(c, 865, 550);
                break;
            case TvFrameStyle.Celebration:
                Border(c, Shade(accent, .45f), accent);
                Balloon(c, 124, 144, 0xEF76B0); Balloon(c, 159, 91, 0x75B8FA); Balloon(c, 866, 105, 0xFFD05B);
                c.Poly(Shade(accent, .65f), 405, 581, 375, 580, 385, 598, 373, 618, 422, 612);
                c.Poly(Shade(accent, .65f), 595, 581, 625, 580, 615, 598, 627, 618, 578, 612);
                c.Rect(404, 576, 192, 34, Tint(accent, .15f), 7);
                c.Glow = true;
                Star(c, 179, 576, 25, 0xFFD16A); Star(c, 821, 576, 25, 0xFFD16A);
                c.Glow = false;
                for (var i = 0; i < 12; i++)
                { var x = 105 + i * 70; var y = i % 2 == 0 ? 98 : 619; c.Poly(i % 2 == 0 ? 0x57D7DDu : 0xC480EEu, x, y, x + 8, y - 3, x + 15, y + 11, x + 7, y + 14); }
                break;
            case TvFrameStyle.RaidNight:
                TechBorder(c, 0x283B50, 0x7C94A6);
                c.Glow = true;
                foreach (var x in new[] { 147f, 853f }) foreach (var y in new[] { 143f, 578f }) Gem(c, x, y, 28, accent);
                c.Glow = false;
                c.Poly(0xD9AD56, 467, 94, 500, 82, 533, 94, 528, 131, 500, 155, 472, 131);
                c.Poly(0x376A96, 475, 99, 500, 91, 525, 99, 520, 126, 500, 144, 480, 126);
                c.Poly(0xF8D277, 487, 112, 494, 118, 500, 107, 507, 118, 514, 112, 510, 128, 491, 128);
                break;
        }
        if (TvFrameAppearance.SupportsGlow(style) && selection.Glow != TvFrameGlow.Off)
            c.AddGlow(style == TvFrameStyle.Celebration ? 0xFFD16Au : accent, selection.Glow);
        // The video always retains its full area, even beneath a character's paws.
        for (var y = 156; y < 564; y++) Array.Clear(c.Pixels, (y * Width + 160) * 4, 680 * 4);
        return c.Pixels;
    }

    private static uint Shade(uint colour, float strength) => Mix(colour, 0, 1 - strength);
    private static uint Tint(uint colour, float amount) => Mix(colour, 0xFFFFFF, amount);
    private static uint Mix(uint a, uint b, float t)
    {
        uint result = 0;
        for (var shift = 0; shift <= 16; shift += 8)
            result |= (uint)MathF.Round(((a >> shift) & 255) * (1 - t) + ((b >> shift) & 255) * t) << shift;
        return result;
    }

    private static void Border(Canvas c, uint dark, uint light, float thickness = 14)
    {
        c.Rect(141, 137, 718, 446, 0x182030, 19);
        c.Rect(145, 141, 710, 438, dark, 16);
        c.Rect(150, 146, 700, 428, light, 13);
        c.Rect(150 + thickness * .35f, 146 + thickness * .35f, 700 - thickness * .7f, 428 - thickness * .7f, dark, 10);
    }

    private static void Cat(Canvas c, float x, float y, uint colour, bool sleepy)
    {
        c.Poly(0x392E3D, x - 43, y + 11, x - 47, y - 48, x - 12, y - 31, x + 19, y - 32, x + 46, y - 47, x + 43, y + 12);
        c.Poly(colour, x - 38, y + 11, x - 42, y - 40, x - 11, y - 24, x + 19, y - 25, x + 41, y - 39, x + 38, y + 12);
        c.Poly(0xDC8F9C, x - 36, y - 31, x - 34, y - 8, x - 16, y - 20);
        c.Poly(0xDC8F9C, x + 34, y - 31, x + 33, y - 8, x + 20, y - 20);
        c.Ellipse(x, y, 44, 33, colour); Face(c, x, y + 4, sleepy);
        foreach (var dx in new[] { -34, 34 }) { c.Ellipse(x + dx, 145, 13, 10, 0x392E3D); c.Ellipse(x + dx, 143, 11, 8, colour); }
        c.Line(0x574651, 2, x - 38, y + 8, x - 22, y + 10); c.Line(0x574651, 2, x + 22, y + 10, x + 38, y + 8);
    }

    private static void Face(Canvas c, float x, float y, bool sleepy = false)
    {
        if (sleepy)
        { c.Line(0x202539, 3, x - 20, y - 3, x - 15, y, x - 10, y - 3); c.Line(0x202539, 3, x + 10, y - 3, x + 15, y, x + 20, y - 3); }
        else { c.Ellipse(x - 16, y - 4, 4, 6, 0x202539); c.Ellipse(x + 16, y - 4, 4, 6, 0x202539); }
        c.Ellipse(x - 27, y + 6, 5, 3, 0xECA6A2); c.Ellipse(x + 27, y + 6, 5, 3, 0xECA6A2);
        c.Line(0x202539, 2.5f, x - 6, y + 10, x, y + 14, x + 6, y + 10);
    }

    private static void Robot(Canvas c, float x, float y, uint colour)
    {
        c.Line(colour, 3, x - 8, y - 30, x - 20, y - 53); c.Line(colour, 3, x + 8, y - 30, x + 20, y - 53);
        c.Ellipse(x - 20, y - 53, 5, 5, colour); c.Ellipse(x + 20, y - 53, 5, 5, colour);
        c.Rect(x - 40, y - 30, 80, 57, 0x182B45, 10); c.Rect(x - 36, y - 26, 72, 49, colour, 8);
        c.Rect(x - 29, y - 19, 58, 34, 0x172A43, 6);
        c.Ellipse(x - 13, y - 3, 4, 6, colour); c.Ellipse(x + 13, y - 3, 4, 6, colour);
        c.Line(colour, 2, x - 5, y + 5, x, y + 8, x + 5, y + 5);
        c.Ellipse(x - 34, 144, 10, 8, colour); c.Ellipse(x + 34, 144, 10, 8, colour);
    }

    private static void Popcorn(Canvas c, float x, float y)
    {
        // Match Popcorn Pal's three large cream puffs, with a taller centre
        // and overlapping lower lobes rather than a flat row of little kernels.
        c.Ellipse(x - 21, y - 29, 15, 15, 0x6A4B35);
        c.Ellipse(x + 1, y - 39, 18, 18, 0x6A4B35);
        c.Ellipse(x + 23, y - 29, 15, 15, 0x6A4B35);
        c.Ellipse(x - 21, y - 29, 12, 12, 0xFFE6AA);
        c.Ellipse(x + 1, y - 39, 15, 15, 0xFFE6AA);
        c.Ellipse(x + 23, y - 29, 12, 12, 0xFFE6AA);
        c.Ellipse(x - 5, y - 24, 11, 11, 0xFFE6AA);
        c.Ellipse(x + 13, y - 23, 11, 11, 0xFFE6AA);
        c.Poly(0xF1E2B3, x - 34, y - 18, x + 34, y - 18, x + 25, y + 34, x - 25, y + 34);
        for (var i = -1; i <= 1; i++) c.Poly(0xE66C83, x + i * 18 - 5, y - 18, x + i * 18 + 5, y - 18, x + i * 13 + 5, y + 34, x + i * 13 - 5, y + 34);
        c.Ellipse(x - 11, y, 3, 4, 0x33324A); c.Ellipse(x + 11, y, 3, 4, 0x33324A);
    }

    private static void Dragon(Canvas c, float x, float y)
    {
        // A little perched dragon, with a compact tail belonging to its body.
        c.Line(0x2E604A, 18, x + 59, y + 27, x + 86, y + 22, x + 105, y + 8, x + 108, y - 9, x + 99, y - 17, x + 89, y - 11);
        c.Line(0x7DBA68, 11, x + 59, y + 26, x + 86, y + 21, x + 104, y + 7, x + 105, y - 8, x + 99, y - 12, x + 91, y - 9);
        c.Ellipse(x + 39, y + 26, 44, 17, 0x32684D); c.Ellipse(x + 39, y + 23, 40, 15, 0x76AD5E);
        for (var i = 0; i < 3; i++)
            c.Poly(0xE5C678, x + 34 + i * 16, y + 9, x + 39 + i * 16, y - 1 + i * 2, x + 46 + i * 16, y + 11);
        foreach (var sign in new[] { -1, 1 })
        {
            c.Poly(0x315D50, x + sign * 31, y + 11, x + sign * 64, y - 39,
                x + sign * 91, y - 17, x + sign * 72, y - 15, x + sign * 70, y + 5,
                x + sign * 53, y - 1, x + sign * 47, y + 24);
            c.Poly(0x6BAF80, x + sign * 39, y + 8, x + sign * 64, y - 28,
                x + sign * 78, y - 20, x + sign * 65, y - 18, x + sign * 64, y - 3,
                x + sign * 51, y - 8, x + sign * 46, y + 13);
            c.Line(0x407F65, 2, x + sign * 37, y + 12, x + sign * 64, y - 29);
            c.Poly(0x9A783F, x + sign * 16, y - 25, x + sign * 24, y - 56, x + sign * 33, y - 49, x + sign * 32, y - 19);
            c.Poly(0xF6D58A, x + sign * 20, y - 26, x + sign * 25, y - 50, x + sign * 29, y - 46, x + sign * 28, y - 22);
        }
        c.Ellipse(x, y, 44, 36, 0x32674D); c.Ellipse(x, y - 1, 40, 33, 0x86BB72);
        c.Ellipse(x - 9, y - 17, 17, 10, 0x98C987);
        foreach (var dx in new[] { -16f, 16f })
        {
            c.Ellipse(x + dx, y - 2, 8, 10, 0xF7E9B4); c.Ellipse(x + dx + 1, y - 1, 4, 7, 0x263E3C);
            c.Ellipse(x + dx - 1, y - 4, 1.8f, 2.2f, 0xFFFFFF);
            c.Line(0x44794F, 2.5f, x + dx - 7, y - 15, x + dx + 6, y - 16);
        }
        c.Ellipse(x, y + 20, 35, 20, 0x578F59); c.Ellipse(x, y + 17, 34, 18, 0xADD38A);
        c.Ellipse(x - 13, y + 12, 3, 4, 0x59875A); c.Ellipse(x + 13, y + 12, 3, 4, 0x59875A);
        c.Line(0x456D4C, 2, x - 12, y + 23, x - 4, y + 27, x + 5, y + 27, x + 13, y + 23);
        foreach (var dx in new[] { -30f, 32f })
        {
            c.Ellipse(x + dx, 145, 15, 10, 0x32674D); c.Ellipse(x + dx, 142, 13, 8, 0x91BF77);
            for (var i = -1; i <= 1; i++) c.Ellipse(x + dx + i * 7, 148, 2, 3, 0xF1DDA4);
        }
    }

    private static void Vine(Canvas c, bool right)
    {
        var x = right ? 866f : 134f;
        var stem = new float[50];
        for (var i = 0; i <= 24; i++)
        {
            var t = i / 24f;
            stem[i * 2] = x + MathF.Sin(t * MathF.Tau + (right ? 1 : 0)) * 6;
            stem[i * 2 + 1] = 557 - t * 406;
        }
        c.Line(0x355F43, 6, stem); c.Line(0x7AA76A, 2.5f, stem);
        var heights = right ? new[] { 197f, 262, 307, 382, 443, 520 } : new[] { 211f, 278, 348, 398, 468, 527 };
        for (var i = 0; i < heights.Length; i++)
        {
            var y = heights[i]; var sx = x + MathF.Sin((557 - y) / 406 * MathF.Tau + (right ? 1 : 0)) * 6;
            Leaf(c, sx, y, -2.45f, right ? 25 : 34, 8 + i % 3, i % 2 == 0 ? 0x76A96Au : 0x568B5Bu);
            Leaf(c, sx, y + 16, -.80f, right ? 34 : 25, 8 + (i + 1) % 3, i % 2 == 0 ? 0x98BE7Au : 0x6FA171u);
        }
        var sign = right ? -1 : 1;
        c.Line(0x6D995F, 3, x, 157, x + sign * 24, 137, x + sign * 67, 126, x + sign * 116, 130);
        for (var i = 0; i < 4; i++)
            Leaf(c, x + sign * (24 + i * 27), 134, right ? -2.5f : -.65f, 25 - i * 2, 7, i % 2 == 0 ? 0x97BD78u : 0x659960u);
        if (right)
        {
            c.Line(0x6D995F, 3, 869, 548, 858, 580, 825, 594, 764, 589);
            Leaf(c, 850, 585, 2.5f, 26, 8, 0x91B779); Leaf(c, 780, 590, 3.5f, 25, 8, 0x6F9C62);
        }
    }

    private static void Leaf(Canvas c, float x, float y, float angle, float length, float width, uint colour)
    {
        var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle)); var normal = new Vector2(-direction.Y, direction.X);
        var points = new List<float>();
        foreach (var side in new[] { 1, -1 }) for (var i = 0; i <= 12; i++)
        {
            var t = side == 1 ? i / 12f : 1 - i / 12f;
            var p = new Vector2(x, y) + direction * (length * t) + normal * (MathF.Sin(t * MathF.PI) * width * side);
            points.Add(p.X); points.Add(p.Y);
        }
        c.Poly(colour, points.ToArray());
        c.Line(0x477C50, 1.2f, x, y, x + direction.X * length * .78f, y + direction.Y * length * .78f);
    }

    private static void Flower(Canvas c, float x, float y, float r, uint petals = 0xF5E9C6)
    {
        for (var i = 0; i < 5; i++) { var a = i * MathF.Tau / 5; c.Ellipse(x + MathF.Cos(a) * r, y + MathF.Sin(a) * r, r * .65f, r * .65f, petals); }
        c.Ellipse(x, y, r * .61f, r * .61f, 0xD9AD5B);
        c.Ellipse(x, y, r * .48f, r * .48f, 0xF4BD5D);
        c.Ellipse(x - r * .12f, y - r * .14f, r * .18f, r * .18f, 0xFFE1A1);
    }

    private static void Moon(Canvas c, float x, float y, float r)
    {
        // A crescent polygon leaves the surroundings transparent instead of painting a background disc.
        var pts = new List<float>();
        for (var i = 0; i <= 24; i++) { var a = .65f + i * (MathF.Tau - 1.3f) / 24; pts.Add(x + MathF.Cos(a) * r); pts.Add(y + MathF.Sin(a) * r); }
        for (var i = 0; i <= 24; i++) { var a = -1.25f - i * (MathF.Tau - 2.5f) / 24; pts.Add(x + r * .53f + MathF.Cos(a) * r * .87f); pts.Add(y + MathF.Sin(a) * r * .87f); }
        c.Poly(0xF4CE69, pts.ToArray());
    }

    private static void Gem(Canvas c, float x, float y, float r, uint colour)
    {
        c.Poly(0x413746, x, y - r - 3, x + r + 3, y, x, y + r + 3, x - r - 3, y);
        c.Poly(colour, x, y - r, x + r, y, x, y + r, x - r, y);
        c.Poly(0xD5F1E4, x, y - r + 4, x + r * .4f, y, x, y + r * .25f, x - r * .4f, y);
    }

    private static void Star(Canvas c, float x, float y, float r, uint colour)
    {
        c.Poly(colour, x, y - r, x + r * .24f, y - r * .24f, x + r, y, x + r * .24f, y + r * .24f,
            x, y + r, x - r * .24f, y + r * .24f, x - r, y, x - r * .24f, y - r * .24f);
    }

    private static void Curtain(Canvas c, bool right, uint colour)
    {
        float X(float x) => right ? 1000 - x : x;
        c.Poly(Shade(colour, .50f), X(142), 142, X(117), 154, X(96), 286, X(122), 346, X(99), 559, X(141), 572);
        c.Poly(colour, X(132), 154, X(117), 234, X(108), 284, X(133), 319, X(118), 555, X(140), 560);
        c.Line(Tint(colour, .35f), 3, X(126), 199, X(113), 274, X(133), 311);
        c.Line(0xD9AF62, 5, X(110), 342, X(136), 344);
    }

    private static void TechBorder(Canvas c, uint dark, uint edge)
    {
        c.Poly(0x142233, 150, 131, 850, 131, 875, 157, 875, 562, 850, 590, 150, 590, 125, 562, 125, 157);
        c.Poly(edge, 155, 137, 845, 137, 868, 161, 868, 559, 844, 583, 156, 583, 132, 559, 132, 161);
        c.Poly(dark, 159, 145, 841, 145, 858, 165, 858, 554, 837, 575, 163, 575, 142, 554, 142, 165);
        foreach (var x in new[] { 141f, 859f }) for (var i = 0; i < 3; i++) c.Line(edge, 3, x - 5, 249 + i * 70, x + 5, 268 + i * 70);
    }

    private static void Speaker(Canvas c, float x, float y, float r, uint accent)
    {
        c.Glow = false;
        c.Ellipse(x, y, r + 3, r + 3, 0x15253A); c.Ellipse(x, y, r, r, 0x65768F);
        c.Ellipse(x, y, r * .79f, r * .79f, 0x18283B);
        c.Glow = true;
        c.Ellipse(x, y, r * .5f, r * .5f, accent);
        c.Glow = false;
        c.Ellipse(x, y, r * .32f, r * .32f, 0x244B65);
    }

    private static void Microphone(Canvas c, float x, float y)
    {
        c.Rect(x - 15, y - 45, 30, 53, 0x8999AF, 13);
        for (var row = 0; row < 4; row++) for (var col = 0; col < 3; col++) c.Ellipse(x - 8 + col * 8, y - 34 + row * 9, 2, 2, 0x293B50);
        c.Line(0x7F94AA, 4, x - 21, y - 4, x - 18, y + 16, x, y + 22, x + 18, y + 16, x + 21, y - 4);
        c.Line(0xA8B9C8, 5, x, y + 22, x, y + 44); c.Rect(x - 26, y + 43, 52, 6, 0x546D83, 3);
    }

    private static void Bat(Canvas c, float x, float y)
    {
        foreach (var sign in new[] { -1, 1 })
            c.Poly(0x7653AD, x + sign * 10, y + 3, x + sign * 39, y - 29, x + sign * 69, y - 14, x + sign * 51, y - 7,
                x + sign * 52, y + 19, x + sign * 35, y + 9, x + sign * 25, y + 32);
        c.Poly(0x533C80, x - 15, y, x - 15, y - 28, x, y - 13, x + 15, y - 28, x + 15, y);
        c.Ellipse(x, y + 3, 21, 24, 0x8C63BF); c.Ellipse(x - 7, y, 3, 5, 0xFFD678); c.Ellipse(x + 7, y, 3, 5, 0xFFD678);
    }

    private static void Candle(Canvas c, float x, float y)
    {
        c.Rect(x - 13, y - 24, 26, 52, 0xEADAB5, 5); c.Rect(x - 21, y + 26, 42, 7, 0x7A738D, 3);
        c.Line(0x867353, 3, x, y - 25, x, y - 36);
        c.Poly(0xF3AD4E, x, y - 75, x + 13, y - 53, x + 10, y - 38, x, y - 32, x - 10, y - 39, x - 12, y - 52);
        c.Ellipse(x, y - 45, 6, 10, 0xFFE6A0);
    }

    private static void Pumpkin(Canvas c, float x, float y, float r)
    {
        c.Line(0x7B9753, 8, x, y - r + 3, x + 1, y - r - 18, x + 12, y - r - 22);
        c.Ellipse(x - r * .4f, y, r * .66f, r * .84f, 0xD48135); c.Ellipse(x + r * .4f, y, r * .66f, r * .84f, 0xD48135);
        c.Ellipse(x, y, r * .65f, r, 0xF2A442);
        c.Poly(0x65412E, x - r * .57f, y - 1, x - r * .31f, y - r * .36f, x - r * .09f, y - 1);
        c.Poly(0x65412E, x + r * .09f, y - 1, x + r * .31f, y - r * .36f, x + r * .57f, y - 1);
        c.Line(0x65412E, 5, x - r * .42f, y + r * .3f, x, y + r * .52f, x + r * .42f, y + r * .3f);
    }

    private static void Ghost(Canvas c, float x, float y)
    {
        c.Ellipse(x, y, 34, 35, 0xEDF1FA);
        c.Poly(0xEDF1FA, x - 34, y, x + 34, y, x + 34, y + 40, x + 18, y + 32, x + 4, y + 42, x - 12, y + 32, x - 29, y + 41);
        c.Ellipse(x - 12, y - 1, 4, 7, 0x3C3F64); c.Ellipse(x + 12, y - 1, 4, 7, 0x3C3F64);
        c.Ellipse(x - 35, 146, 10, 7, 0xEDF1FA); c.Ellipse(x + 35, 146, 10, 7, 0xEDF1FA);
    }

    private static void Tree(Canvas c, float x, float y, float s)
    {
        c.Rect(x - 5 * s, y - 2 * s, 10 * s, 23 * s, 0x775648);
        for (var i = 0; i < 3; i++) c.Poly(i % 2 == 0 ? 0x3E805Eu : 0x599768u,
            x, y - (90 - i * 25) * s, x + (24 + i * 10) * s, y - (52 - i * 25) * s, x - (24 + i * 10) * s, y - (52 - i * 25) * s);
    }

    private static void Snowman(Canvas c, float x, float y)
    {
        c.Ellipse(x, y + 17, 33, 37, 0xD8E8F6); c.Ellipse(x, y - 27, 26, 26, 0xF0F5FA);
        c.Ellipse(x - 8, y - 32, 3, 4, 0x34455E); c.Ellipse(x + 8, y - 32, 3, 4, 0x34455E);
        c.Poly(0xF1A35B, x - 1, y - 24, x + 20, y - 20, x - 1, y - 16);
        c.Rect(x - 26, y - 5, 53, 11, 0xCF5D7A, 3); c.Rect(x + 9, y + 1, 12, 29, 0xCF5D7A, 3);
        c.Ellipse(x - 5, y + 18, 3, 3, 0x34455E); c.Ellipse(x - 5, y + 35, 3, 3, 0x34455E);
    }

    private static void Balloon(Canvas c, float x, float y, uint colour)
    {
        c.Line(0xB8BFCB, 2, x, y + 38, x + 13, y + 89, x + 3, y + 129);
        c.Ellipse(x, y, 26, 35, colour); c.Ellipse(x - 8, y - 12, 5, 9, 0xFCE9D9);
        c.Poly(colour, x, y + 31, x - 6, y + 42, x + 6, y + 42);
    }

    // Small antialiased shape rasterizer; artwork is generated only once per design.
    private sealed class Canvas
    {
        internal readonly byte[] Pixels = new byte[Width * Height * 4];
        private readonly float[] glowMask = new float[Width * Height];
        internal bool Glow;

        internal void AddGlow(uint colour, TvFrameGlow glow)
        {
            var radius = glow == TvFrameGlow.Bright ? 12 : 6;
            var mask = Blur(Blur(glowMask, radius), radius);
            var strength = glow == TvFrameGlow.Bright ? .75f : .35f;
            for (var i = 0; i < mask.Length; i++)
            {
                var at = i * 4;
                var oldAlpha = Pixels[at + 3] / 255f;
                var halo = mask[i] * strength * (1 - oldAlpha);
                var combined = oldAlpha + halo;
                if (halo < .001f || combined <= 0) continue;
                for (var channel = 0; channel < 3; channel++)
                {
                    var value = (colour >> ((2 - channel) * 8)) & 255;
                    Pixels[at + channel] = (byte)MathF.Round((Pixels[at + channel] * oldAlpha + value * halo) / combined);
                }
                Pixels[at + 3] = (byte)MathF.Round(combined * 255);
            }
        }

        private static float[] Blur(float[] source, int radius)
        {
            var horizontal = new float[source.Length];
            var result = new float[source.Length];
            var divisor = radius * 2 + 1;
            for (var y = 0; y < Height; y++)
            {
                float sum = 0;
                for (var x = 0; x <= radius; x++) sum += source[y * Width + x];
                for (var x = 0; x < Width; x++)
                {
                    horizontal[y * Width + x] = MathF.Max(0, sum / divisor);
                    if (x - radius >= 0) sum -= source[y * Width + x - radius];
                    if (x + radius + 1 < Width) sum += source[y * Width + x + radius + 1];
                }
            }
            for (var x = 0; x < Width; x++)
            {
                float sum = 0;
                for (var y = 0; y <= radius; y++) sum += horizontal[y * Width + x];
                for (var y = 0; y < Height; y++)
                {
                    result[y * Width + x] = MathF.Max(0, sum / divisor);
                    if (y - radius >= 0) sum -= horizontal[(y - radius) * Width + x];
                    if (y + radius + 1 < Height) sum += horizontal[(y + radius + 1) * Width + x];
                }
            }
            return result;
        }
        private void Paint(float left, float top, float right, float bottom, uint rgb, Func<float, float, float> distance)
        {
            var minX = Math.Clamp((int)MathF.Floor(left - 1), 0, Width);
            var minY = Math.Clamp((int)MathF.Floor(top - 1), 0, Height);
            var maxX = Math.Clamp((int)MathF.Ceiling(right + 1), 0, Width);
            var maxY = Math.Clamp((int)MathF.Ceiling(bottom + 1), 0, Height);
            for (var y = minY; y < maxY; y++) for (var x = minX; x < maxX; x++)
            {
                var alpha = Math.Clamp(.5f - distance(x + .5f, y + .5f), 0, 1);
                if (alpha <= 0) continue;
                var at = (y * Width + x) * 4;
                glowMask[y * Width + x] = (Glow ? alpha : 0) + glowMask[y * Width + x] * (1 - alpha);
                var oldAlpha = Pixels[at + 3] / 255f;
                var combined = alpha + oldAlpha * (1 - alpha);
                for (var channel = 0; channel < 3; channel++)
                {
                    var value = (rgb >> ((2 - channel) * 8)) & 255;
                    Pixels[at + channel] = (byte)MathF.Round((value * alpha + Pixels[at + channel] * oldAlpha * (1 - alpha)) / combined);
                }
                Pixels[at + 3] = (byte)MathF.Round(combined * 255);
            }
        }
        internal void Rect(float x, float y, float w, float h, uint rgb, float r = 0)
        {
            Paint(x, y, x + w, y + h, rgb, (px, py) =>
            {
                var q = Vector2.Abs(new Vector2(px - x - w / 2, py - y - h / 2)) - new Vector2(w / 2 - r, h / 2 - r);
                return Vector2.Max(q, Vector2.Zero).Length() + MathF.Min(MathF.Max(q.X, q.Y), 0) - r;
            });
        }
        internal void Ellipse(float x, float y, float rx, float ry, uint rgb) =>
            Paint(x - rx, y - ry, x + rx, y + ry, rgb, (px, py) =>
                (new Vector2((px - x) / rx, (py - y) / ry).Length() - 1) * MathF.Min(rx, ry));
        internal void Line(uint rgb, float width, params float[] points)
        {
            for (var i = 0; i < points.Length - 2; i += 2)
            {
                var a = new Vector2(points[i], points[i + 1]); var b = new Vector2(points[i + 2], points[i + 3]);
                var delta = b - a; var length = MathF.Max(delta.LengthSquared(), .001f); var r = width / 2;
                Paint(MathF.Min(a.X, b.X) - r, MathF.Min(a.Y, b.Y) - r, MathF.Max(a.X, b.X) + r, MathF.Max(a.Y, b.Y) + r, rgb,
                    (x, y) => { var p = new Vector2(x, y) - a; return (p - delta * Math.Clamp(Vector2.Dot(p, delta) / length, 0, 1)).Length() - r; });
            }
        }
        internal void Arc(float x, float y, float radius, float from, float to, uint rgb, float width)
        {
            var points = new float[42];
            for (var i = 0; i <= 20; i++) { var a = from + (to - from) * i / 20; points[i * 2] = x + MathF.Cos(a) * radius; points[i * 2 + 1] = y + MathF.Sin(a) * radius; }
            Line(rgb, width, points);
        }
        internal void Poly(uint rgb, params float[] xy)
        {
            var points = Enumerable.Range(0, xy.Length / 2).Select(i => new Vector2(xy[i * 2], xy[i * 2 + 1])).ToArray();
            Paint(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y), rgb, (x, y) =>
            {
                var p = new Vector2(x, y); var inside = false; var distance = float.MaxValue;
                for (var i = 0; i < points.Length; i++)
                {
                    var a = points[i]; var b = points[(i + 1) % points.Length]; var d = b - a;
                    if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
                    var t = Math.Clamp(Vector2.Dot(p - a, d) / MathF.Max(d.LengthSquared(), .001f), 0, 1);
                    distance = MathF.Min(distance, Vector2.DistanceSquared(p, a + d * t));
                }
                return MathF.Sqrt(distance) * (inside ? -1 : 1);
            });
        }
        internal void Text(string text, float x, float y, float scale, uint rgb)
        {
            foreach (var letter in text)
            {
                var rows = letter switch
                {
                    'A' => "01110100011000111111100011000110001", 'L' => "10000100001000010000100001000011111",
                    'P' => "11110100011000111110100001000010000", 'H' => "10001100011000111111100011000110001",
                    'O' => "01110100011000110001100011000101110", 'N' => "10001110011010110011100011000110001",
                    'I' => "11111001000010000100001000010011111", 'R' => "11110100011000111110101001001010001", _ => "",
                };
                for (var i = 0; i < rows.Length; i++) if (rows[i] == '1') Rect(x + i % 5 * scale, y + i / 5 * scale, scale, scale, rgb);
                x += 6 * scale;
            }
        }
    }
}

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawSkyRailIronVulture(byte[] pixels, int width, int height, int x, int y, DateTime now)
    {
        var flashing = skyRailShots.Any(item => item.Shot.TargetId == SkyRailBossTargetId &&
            (now - item.ShownUtc).TotalSeconds is >= SkyRailFireballFlightSeconds and < SkyRailFireballFlightSeconds + 0.14);
        var elapsed = (now - skyRailWaveStartsUtc).TotalSeconds;
        var armour = flashing ? 0xFFF0CEu : 0xAB5945u;
        var light = flashing ? 0xFFFFFFu : 0xD39568u;
        const uint edge = 0xE2B961, dark = 0x382333;
        var charge = elapsed % 2 >= 1.65;
        foreach (var side in new[] { -1, 1 })
        {
            // Swept wings have stepped metal feathers and a heavy gold leading edge.
            RaceTriangle(pixels, width, height, x + side * 26, y - 22,
                x + side * 102, y - 36, x + side * 65, y + 41, dark);
            RaceTriangle(pixels, width, height, x + side * 29, y - 19,
                x + side * 96, y - 31, x + side * 64, y + 32, armour);
            RaceTriangle(pixels, width, height, x + side * 29, y - 19,
                x + side * 96, y - 31, x + side * 61, y - 5, light);
            RaceLine(pixels, width, height, x + side * 26, y - 22, x + side * 102, y - 36, edge, 4);
            for (var feather = 0; feather < 3; feather++)
            {
                var dx = 46 + feather * 15;
                RaceLine(pixels, width, height, x + side * dx, y - 9,
                    x + side * (dx - 9), y + 28 - feather * 10, dark, 4);
                FillRoulettePrototypeCircle(pixels, width, height, x + side * dx, y - 20, 2, edge);
            }
            // Shoulder vents glow as the vulture prepares its missile volley.
            FillQuizTvPanel(pixels, width, height, x + side * 58 - 8, y + 5, 16, 16, dark, edge);
            FillPictionaryPixels(pixels, width, height, x + side * 58 - 4, y + 9, 8, 8,
                charge ? 0xFFF4CBu : 0xE46D37u);
        }
        RaceTriangle(pixels, width, height, x - 35, y + 37, x + 35, y + 37, x, y + 82, dark);
        RaceTriangle(pixels, width, height, x - 25, y + 38, x + 25, y + 38, x, y + 72, armour);
        RaceLine(pixels, width, height, x, y + 48, x, y + 70, edge, 3);
        FillQuizTvPanel(pixels, width, height, x - 47, y - 32, 94, 92, armour, dark);
        FillPictionaryPixels(pixels, width, height, x - 39, y - 27, 78, 8, light);
        // A clearly framed chest reactor is the two-damage weak point.
        FillQuizTvPanel(pixels, width, height, x - 40, y, 80, 50, dark, edge);
        DrawRoulettePrototypeRectOutline(pixels, width, height, x - 35, y + 5, 70, 40, 0x82533E, 2);
        var core = charge || Math.Sin(elapsed * 4) > 0 ? 0xFFF0AFu : 0xF3A647u;
        RaceTriangle(pixels, width, height, x, y + 9, x - 15, y + 25, x + 15, y + 25, core);
        RaceTriangle(pixels, width, height, x - 15, y + 25, x + 15, y + 25, x, y + 41, 0xD97532);
        // Angular helmet, amber eyes and a hooked beak give a bird silhouette.
        FillQuizTvPanel(pixels, width, height, x - 29, y - 73, 58, 46, armour, dark);
        RaceTriangle(pixels, width, height, x - 26, y - 70, x + 26, y - 70, x, y - 87, light);
        RaceLine(pixels, width, height, x - 22, y - 57, x - 6, y - 51, core, 5);
        RaceLine(pixels, width, height, x + 6, y - 51, x + 22, y - 57, core, 5);
        RaceLine(pixels, width, height, x - 25, y - 63, x - 5, y - 56, dark, 4);
        RaceLine(pixels, width, height, x + 5, y - 56, x + 25, y - 63, dark, 4);
        RaceTriangle(pixels, width, height, x - 11, y - 44, x + 11, y - 44, x, y - 22, edge);
        RaceLine(pixels, width, height, x, y - 40, x, y - 26, 0x9C6439, 2);
    }

    private static void DrawSkyRailCanyonMissile(byte[] pixels, int width, int height, int x, int y, int size, DateTime now)
    {
        var half = Math.Max(5, size / 3);
        var flicker = (int)(now.TimeOfDay.TotalSeconds * 14) % 3;
        RaceTriangle(pixels, width, height, x - half, y - size, x + half, y - size,
            x, y - size - 12 - flicker * 3, 0xF27535);
        RaceTriangle(pixels, width, height, x - half / 2, y - size, x + half / 2, y - size,
            x, y - size - 9, 0xFFE0A1);
        FillQuizTvPanel(pixels, width, height, x - half, y - size, half * 2, size + size / 2, 0xA34E3C, 0xE2B961);
        RaceTriangle(pixels, width, height, x - half, y + size / 2, x + half, y + size / 2, x, y + size, 0xD39A6C);
        RaceTriangle(pixels, width, height, x - half, y, x - size, y - size / 2, x - half, y - size / 2, 0x683A3B);
        RaceTriangle(pixels, width, height, x + half, y, x + size, y - size / 2, x + half, y - size / 2, 0x683A3B);
        for (var puff = 0; puff < 3; puff++)
            FillPictionaryPixels(pixels, width, height, x - 3 + (puff % 2 == 0 ? -4 : 4),
                y - size - 22 - puff * 9 - flicker, 5 + puff * 2, 5 + puff * 2, 0x877374);
    }
}

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static void DrawSkyRailWardenGear(byte[] pixels, int width, int height, int x, int y, int radius, double turn, uint brass)
    {
        for (var tooth = 0; tooth < 12; tooth++)
        {
            var angle = turn + tooth * Math.PI / 6;
            RaceLine(pixels, width, height, x + (int)(Math.Cos(angle) * (radius - 4)),
                y + (int)(Math.Sin(angle) * (radius - 4)), x + (int)(Math.Cos(angle) * (radius + 6)),
                y + (int)(Math.Sin(angle) * (radius + 6)), brass, Math.Max(5, radius / 5));
        }
        FillRoulettePrototypeCircle(pixels, width, height, x, y, radius, brass);
        FillRoulettePrototypeCircle(pixels, width, height, x, y, radius - 7, 0x493A36);
        DrawRoulettePrototypeRing(pixels, width, height, x, y, radius - 13, radius - 10, 0xD2AC6B);
        for (var spoke = 0; spoke < 6; spoke++)
        {
            var angle = turn + spoke * Math.PI / 3;
            RaceLine(pixels, width, height, x, y, x + (int)(Math.Cos(angle) * (radius - 10)),
                y + (int)(Math.Sin(angle) * (radius - 10)), brass, 4);
        }
        FillRoulettePrototypeCircle(pixels, width, height, x, y, Math.Max(5, radius / 5), 0x292832);
    }

    private void DrawSkyRailClockworkWarden(byte[] pixels, int width, int height, int x, int y, DateTime now)
    {
        var flashing = skyRailShots.Any(item => item.Shot.TargetId == SkyRailBossTargetId &&
            (now - item.ShownUtc).TotalSeconds is >= SkyRailFireballFlightSeconds and < SkyRailFireballFlightSeconds + 0.14);
        var elapsed = (now - skyRailWaveStartsUtc).TotalSeconds;
        var brass = flashing ? 0xFFF1CDu : 0xB88A4Bu;
        var armour = flashing ? 0xDFDBCDu : 0x535052u;
        const uint dark = 0x27252E, gold = 0xE1B96A;
        DrawSkyRailWardenGear(pixels, width, height, x, y, 73, elapsed * 0.35, brass);
        foreach (var side in new[] { -1, 1 })
        {
            DrawSkyRailWardenGear(pixels, width, height, x + side * 78, y + 3, 25, -elapsed * 0.9, brass);
            FillQuizTvPanel(pixels, width, height, x + side * 51 - 8, y - 30, 16, 57, dark, gold);
            for (var vent = 0; vent < 3; vent++)
                FillPictionaryPixels(pixels, width, height, x + side * 51 - 4, y - 22 + vent * 13, 8, 5, 0x74DBD5);
        }
        FillQuizTvPanel(pixels, width, height, x - 43, y - 47, 86, 102, armour, dark);
        FillQuizTvPanel(pixels, width, height, x - 35, y - 40, 70, 29, dark, brass);
        RaceLine(pixels, width, height, x - 27, y - 30, x - 7, y - 23, 0x74DBD5, 5);
        RaceLine(pixels, width, height, x + 7, y - 23, x + 27, y - 30, 0x74DBD5, 5);
        RaceLine(pixels, width, height, x - 31, y - 37, x - 5, y - 29, dark, 5);
        RaceLine(pixels, width, height, x + 5, y - 29, x + 31, y - 37, dark, 5);
        // The square furnace frame marks the two-damage hit region.
        FillQuizTvPanel(pixels, width, height, x - 34, y - 2, 68, 48, dark, gold);
        var flame = Math.Sin(elapsed * 12) > 0 ? 0xFFD170u : 0xE99042u;
        FillPictionaryPixels(pixels, width, height, x - 27, y + 5, 54, 34, 0x823E2D);
        RaceTriangle(pixels, width, height, x - 21, y + 35, x + 21, y + 35, x + 3, y + 8, flame);
        RaceTriangle(pixels, width, height, x - 10, y + 35, x + 10, y + 35, x - 2, y + 18, 0xFFF0AF);
        for (var bar = 0; bar < 4; bar++)
            FillPictionaryPixels(pixels, width, height, x - 23 + bar * 15, y + 5, 3, 34, dark);
        foreach (var dx in new[] { -37, 37 })
            foreach (var dy in new[] { -42, 50 })
                FillRoulettePrototypeCircle(pixels, width, height, x + dx, y + dy, 2, gold);
    }

    private static void DrawSkyRailBrassRocket(byte[] pixels, int width, int height, int x, int y, int size, DateTime now)
    {
        var half = Math.Max(5, size / 3);
        var flicker = (int)(now.TimeOfDay.TotalSeconds * 12) % 3;
        RaceTriangle(pixels, width, height, x - half, y - size, x + half, y - size,
            x, y - size - 12 - flicker * 3, 0xEE9943);
        FillQuizTvPanel(pixels, width, height, x - half, y - size, half * 2, size + size / 2, 0x997342, 0xE1B96A);
        RaceTriangle(pixels, width, height, x - half, y + size / 2, x + half, y + size / 2, x, y + size, 0x74DBD5);
        foreach (var dy in new[] { -size + 4, 0 })
        {
            RaceLine(pixels, width, height, x - half, y + dy, x + half, y + dy, 0xD9B77A, 2);
            FillPictionaryPixels(pixels, width, height, x - 1, y + dy - 1, 3, 3, 0x493A36);
        }
        for (var puff = 0; puff < 3; puff++)
            FillPictionaryPixels(pixels, width, height, x - 4 + (puff % 2 == 0 ? -3 : 3),
                y - size - 24 - puff * 10 - flicker, 7 + puff * 2, 5 + puff * 2, 0xA4AAA5);
    }
}

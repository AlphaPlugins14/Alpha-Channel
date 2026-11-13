namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawSkyRailNeonEnforcer(byte[] pixels, int width, int height, int x, int y, DateTime now)
    {
        var flashing = skyRailShots.Any(item => item.Shot.TargetId == SkyRailBossTargetId &&
            (now - item.ShownUtc).TotalSeconds is >= SkyRailFireballFlightSeconds and < SkyRailFireballFlightSeconds + 0.14);
        var elapsed = (now - skyRailWaveStartsUtc).TotalSeconds;
        var armour = flashing ? 0xD7DDFFu : 0x302C53u;
        var cyan = flashing ? 0xFFFFFFu : 0x66DFF5u;
        var charge = elapsed % 2 >= 1.65;
        var magenta = charge ? 0xFFC2F3u : 0xEC58CDu;
        const uint dark = 0x10182D;
        // Broad angular chassis, with two separate engine pods and antenna fins.
        foreach (var side in new[] { -1, 1 })
        {
            RaceTriangle(pixels, width, height, x + side * 39, y - 42,
                x + side * 89, y - 18, x + side * 39, y + 34, armour);
            RaceLine(pixels, width, height, x + side * 39, y - 42, x + side * 89, y - 18, cyan, 3);
            FillQuizTvPanel(pixels, width, height, x + side * 80 - 15, y - 26, 30, 62, dark, cyan);
            FillPictionaryPixels(pixels, width, height, x + side * 80 - 10, y - 19, 20, 41, armour);
            for (var vent = 0; vent < 3; vent++)
                FillPictionaryPixels(pixels, width, height, x + side * 80 - 7, y - 13 + vent * 11, 14, 4, magenta);
            RaceTriangle(pixels, width, height, x + side * 80 - 9, y + 37,
                x + side * 80 + 9, y + 37, x + side * 80, y + 51 + (int)(Math.Sin(elapsed * 16) * 4), magenta);
            RaceTriangle(pixels, width, height, x + side * 28, y - 47,
                x + side * 56, y - 73, x + side * 48, y - 32, armour);
            RaceLine(pixels, width, height, x + side * 28, y - 47, x + side * 56, y - 73, cyan, 3);
            FillPictionaryPixels(pixels, width, height, x + side * 56 - 3, y - 76, 6, 6, magenta);
        }
        FillQuizTvPanel(pixels, width, height, x - 48, y - 49, 96, 99, armour, cyan);
        RaceTriangle(pixels, width, height, x - 47, y - 48, x + 47, y - 48, x, y - 65, armour);
        RaceLine(pixels, width, height, x - 47, y - 48, x, y - 65, cyan, 3);
        RaceLine(pixels, width, height, x, y - 65, x + 47, y - 48, cyan, 3);
        FillQuizTvPanel(pixels, width, height, x - 40, y - 38, 80, 26, dark, 0x66548D);
        RaceLine(pixels, width, height, x - 31, y - 29, x - 5, y - 20, magenta, 5);
        RaceLine(pixels, width, height, x + 5, y - 20, x + 31, y - 29, magenta, 5);
        RaceLine(pixels, width, height, x - 33, y - 35, x - 3, y - 26, dark, 4);
        RaceLine(pixels, width, height, x + 3, y - 26, x + 33, y - 35, dark, 4);
        // Gold frame clearly marks the square two-damage power core.
        FillQuizTvPanel(pixels, width, height, x - 34, y - 4, 68, 46, dark, 0xD6B456);
        DrawRoulettePrototypeRectOutline(pixels, width, height, x - 28, y + 2, 56, 34, cyan, 2);
        var pulse = charge || Math.Sin(elapsed * 5) > 0 ? 0xC1F8FFu : cyan;
        FillPictionaryPixels(pixels, width, height, x - 12, y + 8, 24, 22, pulse);
        FillPictionaryPixels(pixels, width, height, x - 6, y + 13, 12, 12, magenta);
        foreach (var dx in new[] { -42, 42 })
            foreach (var dy in new[] { -43, 44 })
                FillPictionaryPixels(pixels, width, height, x + dx - 2, y + dy - 2, 4, 4, 0xA28AC7);
    }

    private static void DrawSkyRailNeonTorpedo(byte[] pixels, int width, int height, int x, int y, int size, DateTime now)
    {
        var half = Math.Max(5, size / 3);
        FillQuizTvPanel(pixels, width, height, x - half, y - size, half * 2, size + size / 2, 0x302C53, 0xEC58CD);
        RaceTriangle(pixels, width, height, x - half, y + size / 2, x + half, y + size / 2, x, y + size, 0x66DFF5);
        FillPictionaryPixels(pixels, width, height, x - 2, y - size + 4, 4, size, 0x9DEFFF);
        var flicker = (int)(now.TimeOfDay.TotalSeconds * 12) % 3;
        RaceTriangle(pixels, width, height, x - half, y - size, x + half, y - size,
            x, y - size - 10 - flicker * 3, 0xFF83DF);
        for (var segment = 0; segment < 3; segment++)
            FillPictionaryPixels(pixels, width, height, x - 3, y - size - 22 - segment * 10 - flicker,
                6, 4, segment % 2 == 0 ? 0xEC58CDu : 0x66DFF5u);
    }
}

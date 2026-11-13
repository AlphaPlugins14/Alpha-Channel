namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawSkyRailCrystalSentinel(byte[] pixels, int width, int height, int x, int y, DateTime now)
    {
        var flashing = skyRailShots.Any(item => item.Shot.TargetId == SkyRailBossTargetId &&
            (now - item.ShownUtc).TotalSeconds is >= SkyRailFireballFlightSeconds and < SkyRailFireballFlightSeconds + 0.14);
        var elapsed = (now - skyRailWaveStartsUtc).TotalSeconds;
        var charge = elapsed % 2 >= 1.65;
        var rim = flashing ? 0xFFFFFFu : 0x78DDEAu;
        // Four separate shards surround a chunky, faceted central crystal.
        foreach (var side in new[] { -1, 1 })
        foreach (var level in new[] { -1, 1 })
        {
            var twitch = charge ? (int)(Math.Sin(elapsed * 32) * 3) : 0;
            DrawSkyRailCrystalGem(pixels, width, height, x + side * (76 + twitch), y + level * 42,
                18, 34, rim, flashing);
        }
        DrawSkyRailCrystalGem(pixels, width, height, x, y, 70, 94, rim, flashing);
        // The framed core is exactly the two-damage hit region.
        FillQuizTvPanel(pixels, width, height, x - 45, y - 25, 90, 60, 0x19192F, 0xD6B456);
        DrawRoulettePrototypeRectOutline(pixels, width, height, x - 41, y - 21, 82, 52, 0x7554A6, 2);
        var pulse = (Math.Sin(elapsed * 4) + 1) / 2;
        var glow = flashing || charge ? 0xFFFFFFu : pulse > 0.5 ? 0xA7F7FFu : 0x57C9E0u;
        RaceLine(pixels, width, height, x - 31, y - 8, x - 9, y + 1, glow, 6);
        RaceLine(pixels, width, height, x + 9, y + 1, x + 31, y - 8, glow, 6);
        RaceLine(pixels, width, height, x - 34, y - 15, x - 7, y - 4, 0x0D1229, 4);
        RaceLine(pixels, width, height, x + 7, y - 4, x + 34, y - 15, 0x0D1229, 4);
        RaceTriangle(pixels, width, height, x, y + 8, x - 9, y + 20, x + 9, y + 20, glow);
        RaceLine(pixels, width, height, x - 16, y + 26, x + 16, y + 26, 0x9E80C8, 3);
    }

    private static void DrawSkyRailCrystalGem(byte[] pixels, int width, int height, int x, int y,
        int halfWidth, int halfHeight, uint rim, bool flashing = false)
    {
        RaceTriangle(pixels, width, height, x, y - halfHeight, x - halfWidth, y, x, y + halfHeight,
            flashing ? 0xD6CBFFu : 0x7554A6u);
        RaceTriangle(pixels, width, height, x, y - halfHeight, x + halfWidth, y, x, y + halfHeight,
            flashing ? 0xA8CDEAu : 0x392B60u);
        RaceTriangle(pixels, width, height, x, y - halfHeight, x - halfWidth, y, x + halfWidth / 3, y - halfHeight / 4,
            flashing ? 0xFFFFFFu : 0x9B79CDu);
        RaceLine(pixels, width, height, x, y - halfHeight, x - halfWidth, y, rim, 3);
        RaceLine(pixels, width, height, x - halfWidth, y, x, y + halfHeight, 0xA17DD2, 3);
        RaceLine(pixels, width, height, x, y + halfHeight, x + halfWidth, y, rim, 3);
        RaceLine(pixels, width, height, x + halfWidth, y, x, y - halfHeight, rim, 3);
    }

    private static void DrawSkyRailCrystalRocket(byte[] pixels, int width, int height, int x, int y, int size, DateTime now)
    {
        DrawSkyRailCrystalGem(pixels, width, height, x, y, Math.Max(6, size / 2), size, 0x78DDEA);
        RaceLine(pixels, width, height, x, y + size / 3, x, y + size, 0xC1FFFF, 3);
        for (var spark = 0; spark < 3; spark++)
        {
            var shift = (int)(now.TimeOfDay.TotalSeconds * 12) % 4;
            var sx = x + (spark % 2 == 0 ? -1 : 1) * (5 + spark * 3);
            var sy = y - size - 6 - spark * 9 - shift;
            RaceLine(pixels, width, height, sx - 2, sy, sx + 2, sy, 0xA7EFFF, 1);
            RaceLine(pixels, width, height, sx, sy - 2, sx, sy + 2, 0xA7EFFF, 1);
        }
    }
}

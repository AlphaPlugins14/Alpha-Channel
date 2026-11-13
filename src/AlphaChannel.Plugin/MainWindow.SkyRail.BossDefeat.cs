namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private const double SkyRailBossExplosionSeconds = 1.1;
    private DateTime skyRailBossDefeatedUtc = DateTime.MinValue;

    private bool SkyRailBossExploding(DateTime now) => skyRailMap is 0 or 1 or 2 or 3 or 4 &&
        skyRailBossDefeatedUtc != DateTime.MinValue &&
        (now - skyRailBossDefeatedUtc).TotalSeconds is >= 0 and < SkyRailBossExplosionSeconds;

    private void DrawSkyRailBossDefeat(byte[] pixels, int width, int height, DateTime now)
    {
        if (!SkyRailBossExploding(now)) return;
        var age = (now - skyRailBossDefeatedUtc).TotalSeconds;
        var position = SkyRailBossPosition(skyRailBossDefeatedUtc, skyRailWaveStartsUtc);
        var x = (int)position.X;
        var y = (int)position.Y;
        // Freeze the defeated body while overlapping bursts break it apart.
        if (age < 0.7)
        {
            if (skyRailMap == 0) DrawSkyRailStormcloud(pixels, width, height, x, y, now);
            else if (skyRailMap == 2) DrawSkyRailIronVulture(pixels, width, height, x, y, now);
            else if (skyRailMap == 3) DrawSkyRailNeonEnforcer(pixels, width, height, x, y, now);
            else if (skyRailMap == 4) DrawSkyRailClockworkWarden(pixels, width, height, x, y, now);
            else DrawSkyRailCrystalSentinel(pixels, width, height, x, y, now);
        }
        (int X, int Y, double Delay)[] bursts =
            [(-48, -26, 0), (37, 13, 0.1), (-21, 32, 0.2), (25, -43, 0.3), (0, 0, 0.45), (-57, 9, 0.55), (48, -12, 0.65)];
        foreach (var burst in bursts)
        {
            var progress = (age - burst.Delay) / 0.45;
            if (progress is < 0 or >= 1) continue;
            var radius = 7 + (int)(progress * 22);
            var bx = x + burst.X;
            var by = y + burst.Y;
            // Four-pixel blocks make round bursts visibly arcade-like rather than smooth circles.
            for (var dy = -radius; dy <= radius; dy += 4)
            for (var dx = -radius; dx <= radius; dx += 4)
            {
                var distance = dx * dx + dy * dy;
                if (distance > radius * radius) continue;
                if (progress > 0.6 && distance < radius * radius * (progress - 0.6) * 2) continue;
                var colour = distance < radius * radius / 5 ? 0xFFF5C9u :
                    distance < radius * radius * 0.65 ? 0xFFD052u : 0xF06A39u;
                FillPictionaryPixels(pixels, width, height, bx + dx, by + dy, 4, 4, colour);
            }
            for (var spark = 0; spark < 6; spark++)
            {
                var angle = spark * Math.PI / 3 + burst.Delay * 4;
                var reach = radius + 5 + (int)(progress * 12);
                FillPictionaryPixels(pixels, width, height, bx + (int)(Math.Cos(angle) * reach),
                    by + (int)(Math.Sin(angle) * reach), 4, 4, 0xFFD052);
            }
        }
    }
}

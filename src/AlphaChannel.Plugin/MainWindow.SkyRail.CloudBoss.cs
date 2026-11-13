namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawSkyRailStormcloud(byte[] pixels, int width, int height, int x, int y, DateTime now)
    {
        var flashing = skyRailShots.Any(item => item.Shot.TargetId == SkyRailBossTargetId &&
            (now - item.ShownUtc).TotalSeconds is >= SkyRailFireballFlightSeconds and < SkyRailFireballFlightSeconds + 0.14);
        var charge = (now - skyRailWaveStartsUtc).TotalSeconds % 2 >= 1.65;
        var rim = flashing ? 0xFFFFFFu : 0x9BDDF7u;
        var body = flashing ? 0xD8EAF7u : 0x485E79u;
        var light = flashing ? 0xFFFFFFu : 0x718BA5u;
        var eyes = charge ? 0xFFFAD5u : 0xFFD139u;
        (int X, int Y, int Radius)[] lobes = [(-65, 8, 32), (-43, -23, 35), (-6, -40, 42), (37, -22, 37), (66, 10, 31)];

        // Chunky dark edging, an icy rim and several shaded lobes give the
        // cloud the same outlined, layered treatment as the arcade targets.
        foreach (var lobe in lobes)
            FillRoulettePrototypeCircle(pixels, width, height, x + lobe.X + 2, y + lobe.Y + 5, lobe.Radius + 7, 0x111A30);
        foreach (var lobe in lobes)
            FillRoulettePrototypeCircle(pixels, width, height, x + lobe.X, y + lobe.Y, lobe.Radius + 3, rim);
        FillPictionaryPixels(pixels, width, height, x - 72, y + 8, 144, 41, rim);
        foreach (var lobe in lobes)
            FillRoulettePrototypeCircle(pixels, width, height, x + lobe.X, y + lobe.Y, lobe.Radius, body);
        FillPictionaryPixels(pixels, width, height, x - 69, y + 8, 138, 38, body);
        foreach (var lobe in lobes)
        {
            RaceLine(pixels, width, height, x + lobe.X - lobe.Radius / 2, y + lobe.Y - lobe.Radius / 2,
                x + lobe.X - lobe.Radius / 4, y + lobe.Y - lobe.Radius * 3 / 4, light, 4);
            RaceLine(pixels, width, height, x + lobe.X - lobe.Radius / 4, y + lobe.Y - lobe.Radius * 3 / 4,
                x + lobe.X + lobe.Radius / 4, y + lobe.Y - lobe.Radius * 3 / 4, light, 4);
        }

        // Gold-trimmed metal mask anchors the face instead of letting it float
        // on a flat cloud. Corner rivets and vents echo the cockpit's hardware.
        FillQuizTvPanel(pixels, width, height, x - 57, y - 15, 114, 58, 0x1C2A40, 0xD6B456);
        FillPictionaryPixels(pixels, width, height, x - 49, y - 9, 98, 4, 0x61758F);
        foreach (var dx in new[] { -51, 51 })
            foreach (var dy in new[] { -9, 36 })
                FillRoulettePrototypeCircle(pixels, width, height, x + dx, y + dy, 2, 0xE9E4C8);
        RaceLine(pixels, width, height, x - 39, y + 1, x - 12, y + 10, 0xA57725, 12);
        RaceLine(pixels, width, height, x + 12, y + 10, x + 39, y + 1, 0xA57725, 12);
        RaceLine(pixels, width, height, x - 37, y + 1, x - 14, y + 9, eyes, 6);
        RaceLine(pixels, width, height, x + 14, y + 9, x + 37, y + 1, eyes, 6);
        RaceLine(pixels, width, height, x - 42, y - 6, x - 9, y + 5, 0x0B1326, 5);
        RaceLine(pixels, width, height, x + 9, y + 5, x + 42, y - 6, 0x0B1326, 5);
        for (var index = 0; index < 3; index++)
        {
            FillPictionaryPixels(pixels, width, height, x - 39 + index * 7, y + 25, 3, 9, 0x61758F);
            FillPictionaryPixels(pixels, width, height, x + 22 + index * 7, y + 25, 3, 9, 0x61758F);
        }
        RaceLine(pixels, width, height, x - 10, y + 25, x + 5, y + 20, eyes, 4);
        RaceLine(pixels, width, height, x + 5, y + 20, x - 3, y + 35, eyes, 4);
        RaceLine(pixels, width, height, x - 3, y + 35, x + 10, y + 30, eyes, 4);

        // Short electric prongs make the silhouette read as a storm enemy.
        var flicker = (int)(now.TimeOfDay.TotalSeconds * 8) % 2;
        foreach (var dx in new[] { -61, 61 })
        {
            var end = y + 69 + flicker * 5;
            RaceLine(pixels, width, height, x + dx, y + 42, x + dx - 7, y + 57, 0x317FB0, 9);
            RaceLine(pixels, width, height, x + dx - 7, y + 57, x + dx + 4, y + 54, 0x317FB0, 9);
            RaceLine(pixels, width, height, x + dx + 4, y + 54, x + dx - 5, end, 0x317FB0, 9);
            RaceLine(pixels, width, height, x + dx, y + 42, x + dx - 7, y + 57, eyes, 3);
            RaceLine(pixels, width, height, x + dx - 7, y + 57, x + dx + 4, y + 54, eyes, 3);
            RaceLine(pixels, width, height, x + dx + 4, y + 54, x + dx - 5, end, eyes, 3);
        }
    }

    private static void DrawSkyRailStormBolt(byte[] pixels, int width, int height, int x, int y, int size, DateTime now)
    {
        (int X, int Y)[] points = [(x + size / 3, y - size), (x - size / 2, y),
            (x + size / 4, y - size / 7), (x - size / 3, y + size)];
        var thickness = Math.Max(2, size / 7);
        for (var index = 0; index < points.Length - 1; index++)
        {
            var a = points[index]; var b = points[index + 1];
            RaceLine(pixels, width, height, a.X, a.Y, b.X, b.Y, 0x256A9B, thickness + 6);
            RaceLine(pixels, width, height, a.X, a.Y, b.X, b.Y, 0x74DFFF, thickness + 3);
            RaceLine(pixels, width, height, a.X, a.Y, b.X, b.Y, 0xFFD139, thickness);
        }
        var spark = (int)(now.TimeOfDay.TotalSeconds * 12) % 2 == 0;
        RaceLine(pixels, width, height, x + size / 3, y - size - 4,
            x + size / 2, y - size - 12, spark ? 0xFFF5D8u : 0x74DFFFu, 2);
    }
}

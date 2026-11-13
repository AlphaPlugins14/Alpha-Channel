namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // The map is part of the host snapshot. Keep these indices stable for the relay.
    private static readonly string[] SkyRailMapNames =
        ["THE CLOUD RUN", "CRYSTAL CAVERN", "SUNSET CANYON", "NEON FLYOVER", "CLOCKWORK RUN"];

    private void DrawSkyRailMap(byte[] pixels, int width, int height, DateTime now)
    {
        var seconds = skyRailPhase == SkyRailPhase.Playing
            ? Math.Max(0d, (now - skyRailWaveStartsUtc).TotalSeconds)
            : now.TimeOfDay.TotalSeconds;
        switch (skyRailMap)
        {
            case 1: DrawSkyRailCrystalCavern(pixels, width, height, seconds); break;
            case 2: DrawSkyRailSunsetCanyon(pixels, width, height, seconds); break;
            case 3: DrawSkyRailNeonFlyover(pixels, width, height, seconds); break;
            case 4: DrawSkyRailClockworkRun(pixels, width, height, seconds); break;
            default: DrawSkyRailCloudRun(pixels, width, height, seconds); break;
        }
    }

    private static void DrawSkyRailCloudRun(byte[] pixels, int width, int height, double seconds)
    {
        FillQuizTvPanel(pixels, width, height, 25, 100, 1230, 475, 0x193359, 0x665393);
        FillPictionaryPixels(pixels, width, height, 28, 103, 1224, 175, 0x192F52);
        FillPictionaryPixels(pixels, width, height, 28, 278, 1224, 150, 0x203C62);
        FillPictionaryPixels(pixels, width, height, 28, 428, 1224, 144, 0x173D60);
        for (var cloud = 0; cloud < 7; cloud++)
        {
            var cloudX = 60 + (cloud * 218 + (int)(seconds * (cloud % 2 == 0 ? 8 : 5))) % 1380 - 130;
            var cloudY = 160 + cloud * 73 % 225;
            FillRoulettePrototypeCircle(pixels, width, height, cloudX, cloudY, 31, 0x294568);
            FillRoulettePrototypeCircle(pixels, width, height, cloudX + 35, cloudY + 4, 26, 0x294568);
            FillRoulettePrototypeCircle(pixels, width, height, cloudX - 31, cloudY + 8, 22, 0x294568);
        }
        FillPictionaryPixels(pixels, width, height, 28, 503, 1224, 69, 0x12344F);
        DrawSkyRailRoute(pixels, width, height, seconds, 390, 0x407B94, 0x447F9B);
    }

    private static void DrawSkyRailCrystalCavern(byte[] pixels, int width, int height, double seconds)
    {
        FillQuizTvPanel(pixels, width, height, 25, 100, 1230, 475, 0x201936, 0xA677DB);
        FillPictionaryPixels(pixels, width, height, 28, 103, 1224, 190, 0x17142F);
        FillPictionaryPixels(pixels, width, height, 28, 293, 1224, 150, 0x292145);
        FillPictionaryPixels(pixels, width, height, 28, 443, 1224, 129, 0x392759);
        for (var side = 0; side < 2; side++)
        for (var crystal = 0; crystal < 6; crystal++)
        {
            var near = crystal / 5f;
            var x = side == 0 ? 46 + crystal * 54 : 1234 - crystal * 54;
            var baseY = 548 - crystal * 35;
            var size = 86 - crystal * 11;
            var tipX = x + (side == 0 ? size / 3 : -size / 3);
            RaceTriangle(pixels, width, height, x - size / 2, baseY,
                x + size / 2, baseY, tipX, baseY - size * 2, 0x49366E);
            RaceTriangle(pixels, width, height, x, baseY, tipX, baseY - size * 2,
                x + (side == 0 ? size / 2 : -size / 2), baseY, 0x7554A6);
            RaceLine(pixels, width, height, tipX, baseY - size * 2, x, baseY,
                crystal % 2 == 0 ? 0xB48DDFu : 0x71C7DCu, 2);
        }
        for (var mote = 0; mote < 18; mote++)
        {
            var x = 45 + (mote * 197 + (int)(seconds * (mote % 3 + 1) * 7)) % 1190;
            var y = 135 + mote * 71 % 300;
            FillPictionaryPixels(pixels, width, height, x, y, 3, 3, 0x9F8BCE);
        }
        DrawSkyRailRoute(pixels, width, height, seconds, 385, 0x8567BA, 0xA58BD2);
    }

    private static void DrawSkyRailSunsetCanyon(byte[] pixels, int width, int height, double seconds)
    {
        FillQuizTvPanel(pixels, width, height, 25, 100, 1230, 475, 0x743849, 0xDA8756);
        FillPictionaryPixels(pixels, width, height, 28, 103, 1224, 125, 0x733444);
        FillPictionaryPixels(pixels, width, height, 28, 228, 1224, 115, 0xAE5146);
        FillPictionaryPixels(pixels, width, height, 28, 343, 1224, 100, 0xDA7047);
        FillPictionaryPixels(pixels, width, height, 28, 443, 1224, 129, 0x7D3940);
        FillRoulettePrototypeCircle(pixels, width, height, 640, 333, 60, 0xF7B85E);
        FillPictionaryPixels(pixels, width, height, 580, 333, 120, 60, 0xDA7047);
        for (var side = 0; side < 2; side++)
        for (var layer = 0; layer < 5; layer++)
        {
            var x = side == 0 ? 28 + layer * 77 : 1252 - (layer + 1) * 77;
            var y = 280 + layer * 49;
            var cliffWidth = 170 - layer * 16;
            var cliffHeight = 290 - layer * 34;
            FillPictionaryPixels(pixels, width, height, x, y, cliffWidth, cliffHeight,
                layer % 2 == 0 ? 0x693049u : 0x8E3E46u);
            FillPictionaryPixels(pixels, width, height, x, y, cliffWidth, 9, 0xE5774Au);
            FillPictionaryPixels(pixels, width, height,
                side == 0 ? x + cliffWidth - 10 : x, y + 9, 10, cliffHeight - 9, 0xB85842);
        }
        for (var dust = 0; dust < 9; dust++)
        {
            var x = 40 + (dust * 191 + (int)(seconds * 13)) % 1190;
            var y = 365 + dust * 37 % 170;
            RaceLine(pixels, width, height, x, y, x + 25, y - 3, 0xE69A65, 1);
        }
        DrawSkyRailRoute(pixels, width, height, seconds, 395, 0xD6A37A, 0xE6B189);
    }

    private static void DrawSkyRailNeonFlyover(byte[] pixels, int width, int height, double seconds)
    {
        FillQuizTvPanel(pixels, width, height, 25, 100, 1230, 475, 0x141632, 0xA057DB);
        FillPictionaryPixels(pixels, width, height, 28, 103, 1224, 245, 0x151735);
        FillPictionaryPixels(pixels, width, height, 28, 348, 1224, 224, 0x20234B);
        for (var building = 0; building < 18; building++)
        {
            var x = 30 + building * 70;
            var h = 65 + (building * 79 % 145);
            FillPictionaryPixels(pixels, width, height, x, 440 - h, 54, h + 130,
                building % 2 == 0 ? 0x1C2247u : 0x252454u);
            var neon = building % 3 == 0 ? 0xEF58D2u : 0x4ACDF2u;
            FillPictionaryPixels(pixels, width, height, x + 8, 440 - h + 17, 38, 3, neon);
            for (var row = 0; row < 5; row++)
                FillPictionaryPixels(pixels, width, height, x + 12 + row % 2 * 18,
                    440 - h + 32 + row * 18, 5, 7, neon);
        }
        for (var side = 0; side < 2; side++)
        for (var block = 0; block < 4; block++)
        {
            var x = side == 0 ? 28 + block * 80 : 1172 - block * 80;
            var y = 380 + block * 30;
            FillPictionaryPixels(pixels, width, height, x, y, 110, 580 - y, 0x121B3Bu);
            FillPictionaryPixels(pixels, width, height, x, y + 8, 110, 5,
                block % 2 == 0 ? 0x49CDEEu : 0xEA5CCEu);
        }
        DrawSkyRailRoute(pixels, width, height, seconds, 395, 0x24BFE8, 0x39D8F4);
    }

    private static void DrawSkyRailClockworkRun(byte[] pixels, int width, int height, double seconds)
    {
        FillQuizTvPanel(pixels, width, height, 25, 100, 1230, 475, 0x292635, 0xC18B4C);
        FillPictionaryPixels(pixels, width, height, 28, 103, 1224, 315, 0x2A2730);
        FillPictionaryPixels(pixels, width, height, 28, 418, 1224, 154, 0x39323A);
        for (var arch = 0; arch < 4; arch++)
        {
            var left = 60 + arch * 125;
            var right = 1220 - arch * 125;
            var top = 145 + arch * 67;
            var bottom = 568 - arch * 36;
            var thickness = 16 - arch * 3;
            FillPictionaryPixels(pixels, width, height, left, top, thickness, bottom - top, 0x795736);
            FillPictionaryPixels(pixels, width, height, right - thickness, top, thickness, bottom - top, 0x795736);
            RaceLine(pixels, width, height, left, top + 105, 640, top - 20, 0xAA8046, thickness);
            RaceLine(pixels, width, height, 640, top - 20, right, top + 105, 0xAA8046, thickness);
            foreach (var x in new[] { left + 9, right - 9 })
            {
                FillPictionaryPixels(pixels, width, height, x - 6, top + 150, 12, 30, 0x287A80);
                FillPictionaryPixels(pixels, width, height, x - 3, top + 153, 6, 24, 0x74DBD5);
            }
        }
        foreach (var x in new[] { 85, 1195 })
        {
            DrawRoulettePrototypeRing(pixels, width, height, x, 400, 80, 92, 0x755334);
            DrawRoulettePrototypeRing(pixels, width, height, x, 400, 45, 55, 0xA17741);
            for (var spoke = 0; spoke < 8; spoke++)
            {
                var angle = spoke * Math.PI / 4d + seconds * 0.12d;
                RaceLine(pixels, width, height, x, 400,
                    x + (int)(Math.Cos(angle) * 84), 400 + (int)(Math.Sin(angle) * 84),
                    0x835E39, 5);
            }
            FillRoulettePrototypeCircle(pixels, width, height, x, 400, 21, 0x3D3233);
        }
        DrawSkyRailRoute(pixels, width, height, seconds, 385, 0x987747, 0xC7A368);
    }

    private static void DrawSkyRailRoute(byte[] pixels, int width, int height,
        double seconds, int horizon, uint railColour, uint stripeColour)
    {
        for (var route = 0; route < 7; route++)
            DrawWhiteboardLine(pixels, width, height,
                new Vector2(640f / width, (float)horizon / height),
                new Vector2((30f + route * 203f) / width, 571f / height), 2, railColour);
        for (var stripe = 0; stripe < 6; stripe++)
        {
            var depth = (float)((seconds * 0.15d + stripe / 6d) % 1d);
            var near = depth * depth;
            var y = horizon + near * (571 - horizon);
            var halfWidth = 10f + near * 612f;
            DrawWhiteboardLine(pixels, width, height,
                new Vector2((640f - halfWidth) / width, y / height),
                new Vector2((640f + halfWidth) / width, y / height),
                Math.Max(1, (int)(1f + near * 3f)), stripeColour);
        }
    }
}

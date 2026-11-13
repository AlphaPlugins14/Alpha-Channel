namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private byte[]? skyRailWarningBackdrop;
    private double skyRailStormFadeFrom;
    private double skyRailStormFadeTarget;
    private DateTime skyRailStormFadeStartedUtc = DateTime.MinValue;

    private void ResetSkyRailStormFade()
    {
        skyRailStormFadeFrom = skyRailStormFadeTarget = 0;
        skyRailStormFadeStartedUtc = DateTime.MinValue;
    }

    private void DrawSkyRailBossArrivalWarning(byte[] pixels, int width, int height, DateTime now)
    {
        if (!SkyRailBossWarning(now)) return;
        var elapsed = Math.Max(0, (now - skyRailWaveStartsUtc).TotalSeconds);
        var fade = 1 - SkyRailRules.BossWarningOpacity(elapsed);
        if (fade > 0)
        {
            if (skyRailWarningBackdrop?.Length != pixels.Length) skyRailWarningBackdrop = new byte[pixels.Length];
            Buffer.BlockCopy(pixels, 0, skyRailWarningBackdrop, 0, pixels.Length);
        }
        var pulse = (Math.Sin(elapsed * Math.PI * 2) + 1) / 2;
        var red = (uint)(100 + pulse * 110) << 16 | 0x2638u;
        DrawRoulettePrototypeRectOutline(pixels, width, height, 27, 105, 1225, 464, red, 4 + (int)(pulse * 6));

        FillQuizTvPanel(pixels, width, height, 150, 213, 980, 104, 0x321C2D, red);
        DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 230,
            "WARNING! WARNING!", 4, 0xFF808A);
        DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 274,
            "ALL PILOTS PREPARE TO DEFEND TOGETHER!", 3, 0xFFF0D5);
        if (fade > 0 && skyRailWarningBackdrop is { } backdrop)
        {
            for (var py = 105; py < Math.Min(height, 570); py++)
            for (var px = 27; px < Math.Min(width, 1252); px++)
            {
                var offset = (py * width + px) * 4;
                for (var channel = 0; channel < 3; channel++)
                    pixels[offset + channel] = (byte)(pixels[offset + channel] * (1 - fade) + backdrop[offset + channel] * fade);
            }
        }
    }

    private void DrawSkyRailStormAtmosphere(byte[] pixels, int width, int height, DateTime now)
    {
        if (skyRailMap is not (0 or 1 or 2 or 3 or 4)) { ResetSkyRailStormFade(); return; }
        var stormActive = skyRailPhase == SkyRailPhase.Playing && (skyRailBossHealth > 0 || SkyRailBossExploding(now)) && !SkyRailBossWarning(now);
        var progress = Math.Clamp((now - skyRailStormFadeStartedUtc).TotalSeconds / 0.6, 0, 1);
        var eased = progress * progress * (3 - 2 * progress);
        var fade = skyRailStormFadeFrom + (skyRailStormFadeTarget - skyRailStormFadeFrom) * eased;
        var target = stormActive ? 1d : 0d;
        if (target != skyRailStormFadeTarget)
        {
            skyRailStormFadeFrom = fade;
            skyRailStormFadeTarget = target;
            skyRailStormFadeStartedUtc = now;
        }
        if (fade <= 0) return;
        var elapsed = Math.Max(0, (now - skyRailWaveStartsUtc).TotalSeconds - SkyRailRules.BossWarningTotalSeconds);
        // Dim just the scenery. Targets, boss, aiming reticle and cockpit stay legible.
        var brightness = 100 - (int)(fade * 42);
        for (var y = 105; y < Math.Min(height, 570); y++)
        for (var x = 27; x < Math.Min(width, 1252); x++)
        {
            var offset = (y * width + x) * 4;
            pixels[offset] = (byte)(pixels[offset] * (brightness - (skyRailMap == 2 ? (int)(fade * 14) : 0)) / 100);
            pixels[offset + 1] = (byte)(pixels[offset + 1] * (brightness - (skyRailMap == 2 ? (int)(fade * 6) : 0)) / 100);
            pixels[offset + 2] = (byte)(pixels[offset + 2] * brightness / 100);
        }
        if (skyRailMap == 4)
        {
            var pulse = fade * (0.7 + (Math.Sin(elapsed * 3) + 1) * 0.15);
            var lampColour = ((uint)(116 * pulse) << 16) | ((uint)(219 * pulse) << 8) | (uint)(213 * pulse);
            for (var arch = 0; arch < 4; arch++)
            {
                var top = 145 + arch * 67;
                foreach (var lx in new[] { 69 + arch * 125, 1211 - arch * 125 })
                    FillPictionaryPixels(pixels, width, height, lx - 3, top + 153, 6, 24, lampColour);
            }
            var sparkColour = ((uint)(244 * fade) << 16) | ((uint)(180 * fade) << 8) | (uint)(78 * fade);
            for (var spark = 0; spark < 12; spark++)
            {
                var sx = 60 + spark * 97;
                var sy = 170 + (int)((elapsed * (45 + spark % 3 * 13) + spark * 61) % 350);
                if (Math.Sin(elapsed * 7 + spark) < 0.2) continue;
                RaceLine(pixels, width, height, sx, sy, sx - 2, sy - 5, sparkColour, 2);
            }
            return;
        }
        if (skyRailMap == 3)
        {
            var pulse = 0.65 + (Math.Sin(elapsed * 3) + 1) * 0.175;
            var intensity = fade * pulse;
            var neonCyan = ((uint)(74 * intensity) << 16) | ((uint)(205 * intensity) << 8) | (uint)(242 * intensity);
            var neonPink = ((uint)(239 * intensity) << 16) | ((uint)(88 * intensity) << 8) | (uint)(210 * intensity);
            for (var building = 0; building < 18; building++)
            {
                var bx = 30 + building * 70;
                var bh = 65 + building * 79 % 145;
                var neon = building % 3 == 0 ? neonPink : neonCyan;
                FillPictionaryPixels(pixels, width, height, bx + 8, 440 - bh + 17, 38, 3, neon);
                for (var row = 0; row < 5; row++)
                    FillPictionaryPixels(pixels, width, height, bx + 12 + row % 2 * 18,
                        440 - bh + 32 + row * 18, 5, 7, neon);
            }
            for (var side = 0; side < 2; side++)
            {
                // Thin, subdued searchlight outlines sweep behind targets and the boss.
                var bx = side == 0 ? 178 : 1102;
                var tip = 640 + (int)(Math.Sin(elapsed * 0.65 + side * Math.PI) * 380);
                var beam = ((uint)(27 * fade) << 16) | ((uint)(62 * fade) << 8) | (uint)(89 * fade);
                RaceLine(pixels, width, height, bx, 375, tip - 30, 155, beam, 2);
                RaceLine(pixels, width, height, bx, 375, tip + 30, 155, beam, 2);
                for (var block = 0; block < 4; block++)
                {
                    var rx = side == 0 ? 28 + block * 80 : 1172 - block * 80;
                    FillPictionaryPixels(pixels, width, height, rx, 388 + block * 30, 110, 5,
                        block % 2 == 0 ? neonCyan : neonPink);
                }
            }
            return;
        }
        if (skyRailMap == 2)
        {
            // Dust and embers drift across a red-tinted canyon, fading with the scenery.
            var emberColour = ((uint)(210 * fade) << 16) | ((uint)(114 * fade) << 8) | (uint)(45 * fade);
            var dustColour = ((uint)(140 * fade) << 16) | ((uint)(99 * fade) << 8) | (uint)(83 * fade);
            for (var mote = 0; mote < 22; mote++)
            {
                var sx = 40 + (mote * 181 + (int)(elapsed * (18 + mote % 3 * 7))) % 1195;
                var sy = 170 + (mote * 73 + (int)(elapsed * 9)) % 360;
                if (mote % 3 == 0)
                    FillPictionaryPixels(pixels, width, height, sx, sy, 3, 3, emberColour);
                else
                    RaceLine(pixels, width, height, sx, sy, sx + 7, sy - 2, dustColour, 1);
            }
            return;
        }
        if (skyRailMap == 1)
        {
            // Restore a softly pulsing glow along the existing cavern crystal seams.
            var pulse = (Math.Sin(elapsed * 2.5) + 1) / 2;
            var glow = (uint)(35 + fade * (65 + pulse * 55));
            var crystalColour = (glow / 2 << 16) | (glow << 8) | Math.Min(255u, glow + 40);
            for (var side = 0; side < 2; side++)
            for (var crystal = 0; crystal < 6; crystal++)
            {
                var cx = side == 0 ? 46 + crystal * 54 : 1234 - crystal * 54;
                var baseY = 548 - crystal * 35;
                var size = 86 - crystal * 11;
                var tipX = cx + (side == 0 ? size / 3 : -size / 3);
                RaceLine(pixels, width, height, tipX, baseY - size * 2, cx, baseY, crystalColour, 3);
            }
            for (var mote = 0; mote < 14; mote++)
            {
                var sx = 55 + (mote * 193 + (int)(elapsed * 9)) % 1170;
                var sy = 145 + (mote * 79 + (int)(elapsed * 6)) % 370;
                var sparkle = Math.Sin(elapsed * 3 + mote) > 0.65;
                FillPictionaryPixels(pixels, width, height, sx, sy, 2, 2, crystalColour);
                if (sparkle)
                {
                    RaceLine(pixels, width, height, sx - 3, sy, sx + 3, sy, crystalColour, 1);
                    RaceLine(pixels, width, height, sx, sy - 3, sx, sy + 3, crystalColour, 1);
                }
            }
            return;
        }
        // Deterministic timing keeps distant lightning consistent for every pilot.
        if (!stormActive) return;
        var cycle = (int)(elapsed / 4);
        var flashAge = elapsed % 4;
        if (elapsed < 1 || flashAge > 0.22) return;
        var boltX = 130 + (cycle * 317 + skyRailWave * 71) % 730;
        var boltY = 158 + cycle % 3 * 17;
        var colour = flashAge < 0.1 ? 0xC4E6FFu : 0x547FA3u;
        RaceLine(pixels, width, height, boltX, boltY, boltX - 28, boltY + 49, colour, 3);
        RaceLine(pixels, width, height, boltX - 28, boltY + 49, boltX + 6, boltY + 40, colour, 3);
        RaceLine(pixels, width, height, boltX + 6, boltY + 40, boltX - 19, boltY + 94, colour, 3);
        RaceLine(pixels, width, height, boltX - 14, boltY + 27, boltX - 44, boltY + 42, colour, 2);
    }
}

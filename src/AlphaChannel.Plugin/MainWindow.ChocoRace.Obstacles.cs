using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawChocoRaceObstaclePixels(byte[] pixels, int lane, int laneTop,
        int courseTravel, int cameraX, int duckX, DateTime now)
    {
        if (chocoRacePhase != ChocoRacePhase.Racing) return;

        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        var elapsed = Math.Max(0d, (now - chocoRaceStartedUtc).TotalSeconds);
        foreach (var obstacle in chocoRaceObstacles.Where(obstacle => obstacle.Lane == lane))
        {
            // Meteors are timed effects over the racers, not pickups on the grass.
            if (obstacle.IsMeteor) continue;
            if (elapsed < obstacle.HitSeconds)
            {
                var x = 152 + (int)Math.Round(courseTravel * obstacle.CoursePosition) - cameraX;
                if (x is < 113 or > 1070) continue;
                if (obstacle.IsChest)
                {
                    // Single gold-trimmed chest, resting just above the track baseline.
                    FillQuizTvPanel(pixels, width, height, x - 16, laneTop + 19,
                        32, 23, 0x593520, 0xE9AD43);
                    FillPictionaryPixels(pixels, width, height, x - 13, laneTop + 22,
                        26, 6, 0xB66B2D);
                    FillPictionaryPixels(pixels, width, height, x - 13, laneTop + 29,
                        26, 10, 0x754421);
                    FillPictionaryPixels(pixels, width, height, x - 13, laneTop + 28,
                        26, 3, 0xE9AD43);
                    FillPictionaryPixels(pixels, width, height, x - 3, laneTop + 28,
                        6, 7, 0xFFE188);
                    FillPictionaryPixels(pixels, width, height, x - 1, laneTop + 31,
                        2, 2, 0x83531C);
                }
                else if (obstacle.IsBoost)
                {
                    // A blue pickup tile with a white arrow pointing down the track.
                    FillQuizTvPanel(pixels, width, height, x - 14, laneTop + 12,
                        28, 28, 0x298EDE, 0xD0F4FF);
                    RaceLine(pixels, width, height, x - 8, laneTop + 26,
                        x + 8, laneTop + 26, 0xFFFFFF, 3);
                    RaceLine(pixels, width, height, x + 2, laneTop + 19,
                        x + 9, laneTop + 26, 0xFFFFFF, 3);
                    RaceLine(pixels, width, height, x + 2, laneTop + 33,
                        x + 9, laneTop + 26, 0xFFFFFF, 3);
                }
                else if (obstacle.IsSlowTile)
                {
                    // Red pickup tile: the arrow points back toward the start.
                    FillQuizTvPanel(pixels, width, height, x - 14, laneTop + 12,
                        28, 28, 0xB82C42, 0xFFD2D9);
                    RaceLine(pixels, width, height, x - 8, laneTop + 26,
                        x + 8, laneTop + 26, 0xFFFFFF, 3);
                    RaceLine(pixels, width, height, x - 9, laneTop + 26,
                        x - 2, laneTop + 19, 0xFFFFFF, 3);
                    RaceLine(pixels, width, height, x - 9, laneTop + 26,
                        x - 2, laneTop + 33, 0xFFFFFF, 3);
                }
                else if (obstacle.IsPuddle)
                {
                    // A shallow blue puddle sits on the grass, clear of the lane marker.
                    FillPictionaryPixels(pixels, width, height, x - 13, laneTop + 33, 26, 3, 0x3484B1);
                    FillPictionaryPixels(pixels, width, height, x - 18, laneTop + 36, 36, 5, 0x3484B1);
                    FillPictionaryPixels(pixels, width, height, x - 12, laneTop + 41, 24, 2, 0x276989);
                    FillPictionaryPixels(pixels, width, height, x - 9, laneTop + 35, 16, 2, 0x83D8EA);
                    FillPictionaryPixels(pixels, width, height, x + 9, laneTop + 39, 5, 1, 0x83D8EA);
                }
                else
                {
                    // The small faceted stone has a light top and dark base.
                    FillPictionaryPixels(pixels, width, height, x - 13, laneTop + 37, 26, 5, 0x333A3D);
                    RaceTriangle(pixels, width, height,
                        x - 12, laneTop + 37, x - 4, laneTop + 26, x + 2, laneTop + 37, 0x8C9698);
                    RaceTriangle(pixels, width, height,
                        x - 3, laneTop + 37, x + 5, laneTop + 28, x + 13, laneTop + 37, 0x626C70);
                    FillPictionaryPixels(pixels, width, height, x - 6, laneTop + 33, 5, 2, 0xC9CFCA);
                }
                continue;
            }

            if (obstacle.IsChest || elapsed >= obstacle.HitSeconds + obstacle.SlowSeconds) continue;
            if (obstacle.IsBoost)
            {
                RaceLine(pixels, width, height, duckX - 45, laneTop + 24,
                    duckX - 29, laneTop + 24, 0x65D9FF, 2);
                RaceLine(pixels, width, height, duckX - 38, laneTop + 32,
                    duckX - 25, laneTop + 32, 0xD0F4FF, 2);
                continue;
            }
            if (obstacle.Dodged) continue;
            DrawPictionaryPixelTextCentered(pixels, width, height, duckX, laneTop + 4,
                "SLOW", 1, obstacle.IsSlowTile ? 0xFF7285u : 0xF6BC4Eu);
            if (elapsed > obstacle.HitSeconds + 0.5d) continue;
            var particle = obstacle.IsSlowTile ? 0xFF7285u : obstacle.IsPuddle ? 0x83D8EAu : 0xC9CFCAu;
            FillPictionaryPixels(pixels, width, height, duckX - 12, laneTop + 27, 3, 3, particle);
            FillPictionaryPixels(pixels, width, height, duckX + 11, laneTop + 23, 3, 3, particle);
            FillPictionaryPixels(pixels, width, height, duckX + 17, laneTop + 31, 2, 2, particle);
        }
    }

    private void DrawChocoRaceMeteorPixels(byte[] pixels, int lane, int laneTop,
        int duckX, DateTime now)
    {
        if (chocoRacePhase != ChocoRacePhase.Racing) return;
        var elapsed = (now - chocoRaceStartedUtc).TotalSeconds;
        var meteor = chocoRaceObstacles.FirstOrDefault(obstacle =>
            obstacle.Lane == lane && obstacle.IsMeteor);
        if (meteor is null) return;
        var sinceDrop = elapsed - (meteor.HitSeconds - ChocoRaceMeteorFallSeconds);
        var sinceImpact = elapsed - meteor.HitSeconds;
        if (sinceDrop < 0d || sinceImpact >= meteor.SlowSeconds) return;

        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        if (sinceImpact < 0d)
        {
            // Start about 20% farther above the choco and keep the descent speed similar.
            var fall = Math.Clamp(sinceDrop / ChocoRaceMeteorFallSeconds, 0d, 1d);
            var rockY = laneTop - 21 + (int)Math.Round(43d * fall);
            RaceTriangle(pixels, width, height,
                duckX - 7, rockY - 10, duckX + 5, rockY - 17,
                duckX + 2, rockY - 6, 0xFF6A2A);
            RaceTriangle(pixels, width, height,
                duckX - 13, rockY - 6, duckX - 3, rockY - 14,
                duckX + 4, rockY - 2, 0xFFC04F);
            RaceCircle(pixels, width, height, duckX, rockY, 12,
                0xB94D2A, true);
            RaceCircle(pixels, width, height, duckX - 3, rockY - 3, 5,
                0xF78B36, true);
        }
        else if (sinceImpact < 0.34d)
        {
            // A short expanding red-orange blast bursts over the struck choco.
            var blast = sinceImpact / 0.34d;
            var blastY = laneTop + 22;
            var ringRadius = 10 + (int)Math.Round(19d * blast);
            RaceCircle(pixels, width, height, duckX, blastY,
                Math.Max(4, 13 - (int)Math.Round(9d * blast)), 0xD74334, true);
            RaceCircle(pixels, width, height, duckX, blastY,
                ringRadius, 0xFF6B37, false, 3);
            if (sinceImpact < 0.12d)
                RaceCircle(pixels, width, height, duckX, blastY, 6,
                    0xFFC260, true);
            for (var ray = 0; ray < 8; ray++)
            {
                var angle = ray * Math.PI / 4d;
                var inner = ringRadius + 3;
                var outer = inner + 5 + (int)Math.Round(5d * blast);
                RaceLine(pixels, width, height,
                    duckX + (int)Math.Round(Math.Cos(angle) * inner),
                    blastY + (int)Math.Round(Math.Sin(angle) * inner),
                    duckX + (int)Math.Round(Math.Cos(angle) * outer),
                    blastY + (int)Math.Round(Math.Sin(angle) * outer),
                    ray % 2 == 0 ? 0xFF9B45u : 0xEA4738u, 2);
            }
        }
        if (sinceImpact >= 0d)
            DrawPictionaryPixelTextCentered(pixels, width, height, duckX,
                laneTop + 1, "SLOW", 1, 0xFF8A4C);
    }

    private int ChocoRaceJumpOffset(int lane, DateTime now)
    {
        if (chocoRacePhase != ChocoRacePhase.Racing) return 0;
        var elapsed = (now - chocoRaceStartedUtc).TotalSeconds;
        const double jumpStart = 0.33d, jumpDuration = 0.72d;
        foreach (var obstacle in chocoRaceObstacles)
        {
            if (obstacle.Lane != lane || !obstacle.Dodged) continue;
            var progress = (elapsed - obstacle.HitSeconds + jumpStart) / jumpDuration;
            if (progress is >= 0d and <= 1d)
                return (int)Math.Round(10d * Math.Sin(Math.PI * progress));
        }
        return 0;
    }
}

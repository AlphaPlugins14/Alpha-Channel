using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly uint[] SkyRailTargetColours =
        [0x56C8FA, 0xE5EBFA, 0xFFD268, 0xF45769];
    private const double SkyRailFireballFlightSeconds = 0.42d;
    private const double SkyRailImpactRingSeconds = 0.34d;
    private const double SkyRailHitFadeSeconds = 0.35d;

    private static string SkyRailShortName(string? name)
    {
        var value = (string.IsNullOrWhiteSpace(name) ? "PLAYER" : name.Trim()).ToUpperInvariant();
        return value.Length > 11 ? value[..11] : value;
    }

    private void ApplySkyRailTvOverlay(byte[] pixels, int width, int height)
    {
        const uint dark = 0x0C1020, panel = 0x171729, purple = 0x9866EC;
        const uint gold = 0xFFD268, white = 0xF4F3FC, muted = 0xB7B9D1;
        var now = DateTime.UtcNow;
        FillPictionaryPixels(pixels, width, height, 0, 0, width, height, dark);
        FillQuizTvPanel(pixels, width, height, 7, 7, 1266, 706, 0x0D1325, purple);
        DrawRoulettePrototypeRectOutline(pixels, width, height, 13, 13, 1254, 694, 0xBF9655, 2);

        FillQuizTvPanel(pixels, width, height, 25, 24, 1230, 65, 0x21192E, 0x654E82);
        DrawPictionaryPixelText(pixels, width, height, 46, 47, "ALPHA CHANNEL", 3, white);
        DrawPictionaryPixelText(pixels, width, height, 315, 35, "AIR FORCE EX", 5, gold);
        DrawPictionaryPixelText(pixels, width, height, 712, 53, "TARGET DASH", 2, muted);
        FillQuizTvPanel(pixels, width, height, 872, 36, 150, 43, 0x101C2D, 0x6B86AE);
        DrawPictionaryPixelTextCentered(pixels, width, height, 947, 50,
            $"ROUND {skyRailRound:00}", 2, 0x9DDDFF);
        FillQuizTvPanel(pixels, width, height, 1031, 36, 205, 43, 0x101C2D, gold);
        var timer = skyRailPhase == SkyRailPhase.Lobby ? "WAITING" :
            skyRailPhase == SkyRailPhase.Countdown ? $"START {SkyRailSecondsLeft:00}" :
            SkyRailBossRetreating(now) ? "RETREATING" :
            skyRailPhase == SkyRailPhase.Playing && skyRailBossHealth > 0 ?
                $"BOSS {skyRailBossHealth:00} {SkyRailBossSecondsLeft:00}S" :
            skyRailPhase == SkyRailPhase.Playing ? $"WAVE {skyRailWave:00}  {SkyRailSecondsLeft:00}" :
            "RESULTS";
        DrawPictionaryPixelTextCentered(pixels, width, height, 1133, 49, timer, 2, gold);

        DrawSkyRailMap(pixels, width, height, now);
        DrawSkyRailStormAtmosphere(pixels, width, height, now);

        FillQuizTvPanel(pixels, width, height, 45, 118, 240, 35, 0x141C31, 0x77A8C3);
        DrawPictionaryPixelText(pixels, width, height, 59, 129,
            SkyRailMapNames[skyRailMap],
            2, white);

        if (skyRailPhase == SkyRailPhase.Playing)
        {
            foreach (var target in skyRailTargets.OrderByDescending(target => target.Id))
                DrawSkyRailTarget(pixels, width, height, target, now);
            if (skyRailBossHealth > 0 && !SkyRailBossWarning(now))
                DrawSkyRailBoss(pixels, width, height, now);
            if (!SkyRailBossRetreating(now))
                foreach (var rocket in skyRailRockets)
                    DrawSkyRailRocket(pixels, width, height, rocket, now);
            DrawSkyRailBossDefeat(pixels, width, height, now);
        }

        DrawSkyRailScoreboard(pixels, width, height);
        DrawSkyRailProjectiles(pixels, width, height, now);
        var hover = skyRailPageHover ?? skyRailTvHover;
        if (skyRailPhase == SkyRailPhase.Playing && !skyRailSettingsOpen && SkyRailCanFire && hover is { } point &&
            point.X is >= 0 and <= 1 && point.Y is >= 0 and <= 1)
        {
            var x = (int)(point.X * width);
            var y = (int)(point.Y * height);
            if (x is >= 80 and <= 1195 && y is >= 130 and <= 565)
                DrawSkyRailReticle(pixels, width, height, x, y);
        }

        DrawSkyRailBossArrivalWarning(pixels, width, height, now);
        DrawSkyRailLocalDamage(pixels, width, height, now);
        if (skyRailPhase == SkyRailPhase.Lobby)
            DrawSkyRailLobby(pixels, width, height);
        else if (skyRailPhase == SkyRailPhase.Countdown)
        {
            FillQuizTvPanel(pixels, width, height, 409, 217, 462, 237, 0x151A32, gold);
            DrawPictionaryPixelTextCentered(pixels, width, height, 640, 249,
                "PILOTS LOCKED IN", 3, white);
            DrawPictionaryPixelTextCentered(pixels, width, height, 640, 304,
                SkyRailSecondsLeft.ToString(), 11, gold);
            DrawPictionaryPixelTextCentered(pixels, width, height, 640, 407,
                "GET READY TO FIRE!", 2, 0x9DDDFF);
        }
        else if (skyRailPhase == SkyRailPhase.Results)
            DrawSkyRailResults(pixels, width, height);
        else if (skyRailPhase == SkyRailPhase.Playing && !skyRailLocked.Contains(LocalSkyRailId))
        {
            FillQuizTvPanel(pixels, width, height, 364, 521, 552, 38, 0x201A2E, gold);
            DrawPictionaryPixelTextCentered(pixels, width, height, 640, 531,
                "SPECTATING - JOIN NEXT ROUND", 2, gold);
        }
        DrawSkyRailDashboard(pixels, width, height, panel, gold, white, muted);
        DrawSkyRailSettingsButton(pixels);
        DrawSkyRailSettingsOverlay(pixels);
    }

    private void DrawSkyRailTarget(byte[] pixels, int width, int height,
        SkyRailTarget target, DateTime now)
    {
        var pose = SkyRailTargetPose(target, now, skyRailWaveStartsUtc, skyRailTargets.Count);
        if (!pose.Visible) return;
        var position = pose.Position;
        var x = (int)position.X;
        var y = (int)position.Y;
        var radius = (int)pose.Radius;
        var alreadyHit = skyRailHitKeys.Contains($"{LocalSkyRailId}|{target.Id}");
        if (alreadyHit && skyRailLocalHitAt.TryGetValue(target.Id, out var hitUtc) && now >= hitUtc)
        {
            var fade = Math.Clamp((now - hitUtc).TotalSeconds / SkyRailHitFadeSeconds, 0d, 1d);
            if (fade >= 1d) return;
            var fadingRadius = Math.Max(2, (int)(radius * (1d - 0.65d * fade)));
            var grey = fade < 0.4d ? 0xA5B2C2u : fade < 0.7d ? 0x718299u : 0x40566Eu;
            FillRoulettePrototypeCircle(pixels, width, height, x, y, fadingRadius + 4, grey);
            FillRoulettePrototypeCircle(pixels, width, height, x, y, Math.Max(1, fadingRadius - 3), 0x243950);
            return;
        }
        var kind = Math.Clamp(target.Kind, 0, 3);
        var rim = SkyRailTargetColours[kind];
        var trim = kind switch { 0 => 0xA9E7FFu, 1 => 0xFFF4DCu, 2 => 0xFFE6A1u, _ => 0xFFA18Au };
        var face = kind switch { 0 => 0x526F88u, 1 => 0x91A6B5u, 2 => 0xA88359u, _ => 0x813437u };
        var finCount = kind == 3 ? 8 : 4;
        var turn = (now - skyRailWaveStartsUtc).TotalSeconds * 0.35d + target.Offset;
        for (var fin = 0; fin < finCount; fin++)
        {
            var angle = turn + fin * Math.PI * 2d / finCount;
            var spread = kind == 3 ? 0.13d : 0.23d;
            SkyRailTargetFin(pixels, width, height, x, y, radius, angle, spread,
                kind == 3 ? 0x9A292Eu : 0x9A612Eu, rim);
        }
        FillRoulettePrototypeCircle(pixels, width, height, x, y, radius + 5, 0x16283C);
        DrawRoulettePrototypeRing(pixels, width, height, x, y, radius - 1, radius + 3, trim);
        FillRoulettePrototypeCircle(pixels, width, height, x, y, Math.Max(2, radius - 2), 0x403C43);
        DrawRoulettePrototypeRing(pixels, width, height, x, y,
            Math.Max(1, radius - 8), Math.Max(2, radius - 5), rim);
        FillRoulettePrototypeCircle(pixels, width, height, x, y, Math.Max(2, radius - 9), face);
        if (kind == 3)
            DrawSkyRailBombFace(pixels, width, height, x, y, radius);
        else
            DrawSkyRailStar(pixels, width, height, x, y, radius, trim);
    }

    private static void SkyRailTargetFin(byte[] pixels, int width, int height,
        int x, int y, int radius, double angle, double spread, uint shadow, uint highlight)
    {
        var tip = radius + 15;
        var root = radius + 1;
        RaceTriangle(pixels, width, height,
            x + (int)(Math.Cos(angle) * tip), y + (int)(Math.Sin(angle) * tip),
            x + (int)(Math.Cos(angle - spread) * root), y + (int)(Math.Sin(angle - spread) * root),
            x + (int)(Math.Cos(angle + spread) * root), y + (int)(Math.Sin(angle + spread) * root), shadow);
        RaceLine(pixels, width, height,
            x + (int)(Math.Cos(angle) * (radius + 3)), y + (int)(Math.Sin(angle) * (radius + 3)),
            x + (int)(Math.Cos(angle) * (tip - 3)), y + (int)(Math.Sin(angle) * (tip - 3)),
            highlight, Math.Max(1, radius / 12));
    }

    private static void DrawSkyRailStar(byte[] pixels, int width, int height,
        int x, int y, int radius, uint colour)
    {
        var outer = Math.Max(3, radius / 2);
        var inner = Math.Max(2, radius / 5);
        for (var point = 0; point < 10; point++)
        {
            var angle = -Math.PI / 2d + point * Math.PI / 5d;
            var nextAngle = angle + Math.PI / 5d;
            var first = point % 2 == 0 ? outer : inner;
            var second = point % 2 == 0 ? inner : outer;
            RaceTriangle(pixels, width, height, x, y,
                x + (int)(Math.Cos(angle) * first), y + (int)(Math.Sin(angle) * first),
                x + (int)(Math.Cos(nextAngle) * second), y + (int)(Math.Sin(nextAngle) * second), colour);
        }
    }

    private static void DrawSkyRailBombFace(byte[] pixels, int width, int height,
        int x, int y, int radius)
    {
        var size = Math.Max(3, radius / 3);
        FillRoulettePrototypeCircle(pixels, width, height, x, y, size + 2, 0xF18764);
        FillRoulettePrototypeCircle(pixels, width, height, x, y, size, 0x54202Bu);
        if (radius < 18) return;
        // Inward-sloping brows and a frown distinguish penalty targets from
        // the friendly-looking bonus targets, even at smaller sizes.
        RaceLine(pixels, width, height, x - size / 2 - 1, y - size / 2,
            x - 2, y - 1, 0xFFD9A1, 2);
        RaceLine(pixels, width, height, x + 2, y - 1,
            x + size / 2 + 1, y - size / 2, 0xFFD9A1, 2);
        FillPictionaryPixels(pixels, width, height, x - size / 2, y, 2, 2, 0xFFD9A1);
        FillPictionaryPixels(pixels, width, height, x + size / 2 - 1, y, 2, 2, 0xFFD9A1);
        RaceLine(pixels, width, height, x - size / 2, y + size / 2,
            x, y + Math.Max(1, size / 4), 0xFF9C72, 2);
        RaceLine(pixels, width, height, x, y + Math.Max(1, size / 4),
            x + size / 2, y + size / 2, 0xFF9C72, 2);
    }

    private static void DrawSkyRailReticle(byte[] pixels, int width, int height, int x, int y)
    {
        const uint orange = 0xFF7431, highlight = 0xFFB663;
        for (var segment = 0; segment < 4; segment++)
        {
            var centerAngle = segment * Math.PI / 2d;
            SkyRailTargetFin(pixels, width, height, x, y, 22,
                centerAngle, 0.25d, 0xAE3C24, orange);
            for (var step = 0; step <= 18; step++)
            {
                var angle = centerAngle + 0.28d + step * 0.055d;
                var arcX = x + (int)(Math.Cos(angle) * 31d);
                var arcY = y + (int)(Math.Sin(angle) * 31d);
                FillPictionaryPixels(pixels, width, height, arcX - 1, arcY - 1, 3, 3, orange);
            }
        }
        DrawRoulettePrototypeRing(pixels, width, height, x, y, 14, 16, orange);
        DrawRoulettePrototypeRing(pixels, width, height, x, y, 5, 7, highlight);
        FillRoulettePrototypeCircle(pixels, width, height, x, y, 2, orange);
    }

    private void DrawSkyRailScoreboard(byte[] pixels, int width, int height)
    {
        const uint white = 0xF4F3FC, gold = 0xFFD268, muted = 0xB7B9D1;
        FillQuizTvPanel(pixels, width, height, 1010, 118, 220, 165, 0x171B30, 0x627EAB);
        DrawPictionaryPixelText(pixels, width, height, 1025, 134, "TOP PILOTS", 2, gold);
        FillPictionaryPixels(pixels, width, height, 1025, 159, 190, 2, 0x516A92);
        var leaders = skyRailPlayers.Values.OrderByDescending(player => player.Score)
            .ThenBy(player => player.Name, StringComparer.Ordinal).Take(3).ToArray();
        for (var index = 0; index < leaders.Length; index++)
        {
            var player = leaders[index];
            var y = 178 + index * 31;
            DrawPictionaryPixelText(pixels, width, height, 1024, y,
                SkyRailRules.Rank(player.Score, skyRailPlayers.Values.Select(p => p.Score)).ToString(), 2, player.Score == leaders[0].Score ? gold : muted);
            DrawPictionaryPixelText(pixels, width, height, 1055, y,
                SkyRailShortName(player.Name)[..Math.Min(8, SkyRailShortName(player.Name).Length)], 2, white);
            DrawPictionaryPixelText(pixels, width, height, 1168, y,
                Math.Min(9999, player.Score).ToString("0000"), 2, index == 0 ? gold : white);
        }
    }

    private void DrawSkyRailProjectiles(byte[] pixels, int width, int height, DateTime now)
    {
        foreach (var (shot, shown) in skyRailShots)
        {
            var seconds = (now - shown).TotalSeconds;
            if (seconds < 0d || seconds > SkyRailFireballFlightSeconds + SkyRailImpactRingSeconds)
                continue;
            var impact = SkyRailProjectileImpactPoint(shot, shown.AddSeconds(SkyRailFireballFlightSeconds));
            if (seconds < SkyRailFireballFlightSeconds)
            {
                var progress = (float)(seconds / SkyRailFireballFlightSeconds);
                progress = 1f - (1f - progress) * (1f - progress);
                var x = (int)(640 + (impact.X - 640) * progress);
                var y = (int)(576 + (impact.Y - 576) * progress);
                var tail = Math.Max(0f, progress - 0.09f);
                var tailX = (int)(640 + (impact.X - 640) * tail);
                var tailY = (int)(576 + (impact.Y - 576) * tail);
                DrawWhiteboardLine(pixels, width, height,
                    new Vector2(tailX / (float)width, tailY / (float)height),
                    new Vector2(x / (float)width, y / (float)height), 9, 0xFF923B);
                FillRoulettePrototypeCircle(pixels, width, height, x, y, 10, 0xFF6B2E);
                FillRoulettePrototypeCircle(pixels, width, height, x, y, 6, 0xFFD170);
                FillRoulettePrototypeCircle(pixels, width, height, x, y, 3, 0xFFF2C5);
            }
            else if (shot.TargetId != -1)
            {
                var ringProgress = (seconds - SkyRailFireballFlightSeconds) / SkyRailImpactRingSeconds;
                var radius = 8 + (int)(ringProgress * 27d);
                var impactX = (int)impact.X;
                var impactY = (int)impact.Y;
                FillRoulettePrototypeCircle(pixels, width, height, impactX, impactY, radius,
                    shot.TargetId == SkyRailBossTargetId ? 0xB685F5u :
                    shot.Points >= 0 ? 0xFF8A46u : 0xF75562u);
                FillRoulettePrototypeCircle(pixels, width, height, impactX, impactY,
                    Math.Max(1, radius - 5), 0x1E3450);
                if (shot.TargetId >= 0)
                    DrawPictionaryPixelTextCentered(pixels, width, height, impactX,
                        impactY - radius - 24, shot.Points >= 0 ? $"+{shot.Points}" :
                            shot.Points.ToString(), 2, shot.Points >= 0 ? 0xFFD268u : 0xFF7285u);
            }
        }
    }

    private Vector2 SkyRailProjectileImpactPoint(SkyRailShot shot, DateTime impactUtc)
    {
        if (shot.TargetId < -1)
            return shot.ImpactX > 0 && shot.ImpactY > 0
                ? new Vector2(shot.ImpactX, shot.ImpactY) : new Vector2(shot.X, shot.Y);
        if (shot.TargetId < 0 || skyRailWaveStartsUtc == DateTime.MinValue)
            return new Vector2(shot.X, shot.Y);
        var target = skyRailTargets.FirstOrDefault(candidate => candidate.Id == shot.TargetId);
        if (target is null) return new Vector2(shot.X, shot.Y);
        var passUtc = skyRailWaveStartsUtc.AddSeconds(
            0.2d + target.Id % 100 * SkyRailTargetSpawnIntervalSeconds(skyRailTargets.Count) +
            SkyRailTargetTravelSeconds - 0.05d);
        var pose = SkyRailTargetPose(target, impactUtc < passUtc ? impactUtc : passUtc,
            skyRailWaveStartsUtc, skyRailTargets.Count);
        return pose.Visible ? pose.Position : new Vector2(shot.X, shot.Y);
    }

    private void DrawSkyRailLobby(byte[] pixels, int width, int height)
    {
        const uint white = 0xF4F3FC, gold = 0xFFD268, muted = 0xB7B9D1;
        FillQuizTvPanel(pixels, width, height, 355, 159, 570, 405, 0x171B30, gold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 178,
            "READY FOR TAKEOFF?", 4, gold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 225,
            $"{skyRailReady.Count} PILOT{(skyRailReady.Count == 1 ? "" : "S")} READY", 3, white);
        var readyNames = skyRailPlayers.Values.Where(player => skyRailReady.Contains(player.Id))
            .Select(player => SkyRailShortName(player.Name)).Take(6).ToArray();
        for (var index = 0; index < readyNames.Length; index++)
            DrawPictionaryPixelTextCentered(pixels, width, height, 475 + index % 3 * 165, 263 + index / 3 * 20,
                readyNames[index], 2, muted);
        var canChangeMap = IsLocalActivityHost && skyRailCustomRules;
        var mapHover = skyRailPageHover ?? skyRailTvHover;
        for (var side = 0; side < 2; side++)
        {
            var arrowX = side == 0 ? 410 : 826;
            var hovered = canChangeMap && mapHover is { } uv &&
                uv.X * width >= arrowX && uv.X * width < arrowX + 44 &&
                uv.Y * height >= 310 && uv.Y * height < 354;
            FillQuizTvPanel(pixels, width, height, arrowX, 310, 44, 44,
                hovered ? 0x64458Bu : 0x302E43u, canChangeMap ? 0xB78AF7u : 0x555166u);
            // Draw chevrons directly; the pixel font has no < or > glyphs.
            var tipX = arrowX + (side == 0 ? 16 : 28);
            var tailX = arrowX + (side == 0 ? 28 : 16);
            var arrowColour = canChangeMap ? white : 0x68677Cu;
            DrawRoulettePrototypeLine(pixels, width, height, tailX, 322, tipX, 332, arrowColour, 3);
            DrawRoulettePrototypeLine(pixels, width, height, tipX, 332, tailX, 342, arrowColour, 3);
        }
        FillQuizTvPanel(pixels, width, height, 454, 310, 372, 44, 0x101C2A, 0x749DBC);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 324,
            skyRailMapChoice < 0 ? "RANDOM" : SkyRailMapNames[skyRailMapChoice], 2, gold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 373,
            skyRailMapChoice < 0 ? "RANDOM ROUTE AT TAKEOFF" : "SELECTED FLIGHT ROUTE", 2, muted);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 399,
            $"YOUR BEST: {Plugin.Cfg.SkyRailPersonalBestScores?.GetValueOrDefault(LocalSkyRailId) ?? 0}", 2, muted);
        FillQuizTvPanel(pixels, width, height, 460, 420, 360, 60,
            skyRailReady.Contains(LocalSkyRailId) ? 0x263E3Fu : 0x315D4Bu,
            skyRailReady.Contains(LocalSkyRailId) ? 0x55896Eu : 0x76E4A0u);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 436,
            skyRailReady.Contains(LocalSkyRailId) ? "READY / UNREADY" : "JOIN GAME / READY", 3,
            white);
        FillQuizTvPanel(pixels, width, height, 460, 495, 360, 60,
            IsLocalActivityHost && skyRailReady.Count > 0 ? 0x64458Bu : 0x302E43u,
            IsLocalActivityHost && skyRailReady.Count > 0 ? 0xB78AF7u : 0x555166u);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 511,
            IsLocalActivityHost ? "HOST: START ROUND" : "WAITING FOR HOST",
            3, white);
    }

    private void DrawSkyRailResults(byte[] pixels, int width, int height)
    {
        const uint gold = 0xFFD268, white = 0xF4F3FC;
        FillQuizTvPanel(pixels, width, height, 355, 159, 570, 405, 0x171B30, gold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 180, "FLIGHT COMPLETE!", 4, gold);
        var leaders = skyRailPlayers.Values.OrderByDescending(player => player.Score)
            .ThenBy(player => player.Name, StringComparer.Ordinal).ToArray();
        var top = leaders.FirstOrDefault()?.Score ?? 0;
        var tied = leaders.Count(p => p.Score == top) > 1;
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 228,
            top == 0 ? "FINAL SCORES" : tied ? "SHARED VICTORY!" : SkyRailShortName(leaders[0].Name) + " WINS!", 2, gold);
        var pages = Math.Max(1, (leaders.Length + 4) / 5);
        var offset = ((int)(DateTime.UtcNow.TimeOfDay.TotalSeconds / 8) % pages) * 5;
        for (var index = offset; index < Math.Min(leaders.Length, offset + 5); index++)
        {
            var player = leaders[index];
            var rank = SkyRailRules.Rank(player.Score, leaders.Select(p => p.Score));
            var y = 272 + (index - offset) * 40;
            var colour = player.Score == top && top > 0 ? gold : white;
            DrawPictionaryPixelText(pixels, width, height, 390, y,
                $"{rank}. {SkyRailShortName(player.Name)}", 3, colour);
            DrawPictionaryPixelText(pixels, width, height, 780, y,
                player.Score.ToString(), 3, colour);
            if (player.PersonalBest)
                DrawPictionaryPixelText(pixels, width, height, 780, y + 24, "PERSONAL BEST", 1, 0x76E4A0);
        }
        if (IsLocalActivityHost)
        {
            var hover = skyRailPageHover ?? skyRailTvHover;
            var hovered = hover is { } uv && uv.X * width is >= 460 and <= 820 &&
                uv.Y * height is >= 495 and <= 555;
            BingoButton(pixels, width, height, 460, 495, 360, 60, "PLAY AGAIN", hovered, false, true);
        }
        else
            DrawPictionaryPixelTextCentered(pixels, width, height, 640, 512,
                "WAITING FOR THE HOST", 2, 0xA6DAEE);
    }

    private void DrawSkyRailDashboard(byte[] pixels, int width, int height,
        uint panel, uint gold, uint white, uint muted)
    {
        FillQuizTvPanel(pixels, width, height, 25, 585, 1230, 117, panel, 0xB78D59);
        FillQuizTvPanel(pixels, width, height, 36, 596, 300, 94, 0x111D2E, 0x555E88);
        DrawPictionaryPixelText(pixels, width, height, 52, 609, "YOUR SCORE", 2, muted);
        var local = skyRailPlayers.GetValueOrDefault(LocalSkyRailId);
        DrawPictionaryPixelText(pixels, width, height, 52, 637,
            (local?.Score ?? 0).ToString("0000"), 5, gold);
        DrawPictionaryPixelText(pixels, width, height, 211, 647,
            $"BONUS X{SkyRailRules.Multiplier(local?.Combo ?? 0)}", 2, 0xF9A4D9);

        var fraction = skyRailPhase == SkyRailPhase.Playing
            ? Math.Clamp((skyRailWave - 1 +
                (float)(DateTime.UtcNow - skyRailWaveStartsUtc).TotalSeconds / skyRailWaveSeconds)
                / skyRailWaveCount, 0f, 1f)
            : skyRailPhase == SkyRailPhase.Results ? 1f : 0f;
        DrawSkyRailCockpit(pixels, width, height, fraction, gold, muted);

        FillQuizTvPanel(pixels, width, height, 870, 596, 374, 94, 0x111D2E, 0x555E88);
        DrawPictionaryPixelText(pixels, width, height, 886, 609, "TARGETS", 2, muted);
        for (var index = 0; index < 4; index++)
        {
            var x = 908 + index * 89;
            FillRoulettePrototypeCircle(pixels, width, height, x, 649, 11,
                SkyRailTargetColours[index]);
            DrawPictionaryPixelTextCentered(pixels, width, height, x, 672,
                index switch { 0 => "+10", 1 => "+25", 2 => "+50", _ => "-100" },
                2, SkyRailTargetColours[index]);
        }
    }

    private void DrawSkyRailCockpit(byte[] pixels, int width, int height,
        float progress, uint gold, uint muted)
    {
        const int centerX = 640, centerY = 746;
        // The circular bezel sits partly below the screen, like a small arcade flight console.
        FillRoulettePrototypeCircle(pixels, width, height, centerX, centerY, 190, 0x080D18);
        DrawRoulettePrototypeRing(pixels, width, height, centerX, centerY, 181, 189, 0x89633D);
        DrawRoulettePrototypeRing(pixels, width, height, centerX, centerY, 177, 180, 0xE0B86D);
        FillRoulettePrototypeCircle(pixels, width, height, centerX, centerY, 175, 0x1A2230);
        DrawRoulettePrototypeRing(pixels, width, height, centerX, centerY, 160, 164, 0x3A5367);

        RaceTriangle(pixels, width, height, 452, 632, 488, 596, 489, 662, 0x57412F);
        RaceTriangle(pixels, width, height, 828, 632, 792, 596, 791, 662, 0x57412F);
        RaceLine(pixels, width, height, 458, 629, 485, 607, gold, 2);
        RaceLine(pixels, width, height, 822, 629, 795, 607, gold, 2);

        FillQuizTvPanel(pixels, width, height, 494, 605, 292, 84, 0x0F1D30, 0xAA8559);
        var bossWave = skyRailPhase == SkyRailPhase.Playing && skyRailBossHealth > 0;
        DrawPictionaryPixelTextCentered(pixels, width, height, centerX, 612,
            bossWave ? "BOSS WAVE" : "FLIGHT PROGRESS", 2,
            bossWave ? 0xFF808Au : muted);
        FillQuizTvPanel(pixels, width, height, 514, 640, 252, 14,
            bossWave ? 0x47212Du : 0x27364Cu, bossWave ? 0xE85866u : 0x657D98u);
        FillPictionaryPixels(pixels, width, height, 517, 643,
            (int)(246 * (bossWave ? skyRailBossHealth / (float)skyRailBossMaxHealth : progress)),
            8, bossWave ? 0xE94A5Eu : 0x9E77F2u);
        if (bossWave)
            DrawSkyRailSkull(pixels, width, height, 509, 664);
        else
            FillRoulettePrototypeCircle(pixels, width, height,
                517 + (int)(246 * progress), 647, 6, gold);
        DrawPictionaryPixelTextCentered(pixels, width, height, centerX, 663,
            bossWave ? $"{skyRailBossHealth} HEALTH LEFT" :
                $"{skyRailWave} OF {skyRailWaveCount} WAVES", 2,
            bossWave ? 0xFF808Au : 0x9DDDFFu);

        var lampNow = DateTime.UtcNow;
        var repairing = SkyRailRepairing(lampNow);
        var damage = skyRailLocalDamage.Hits(lampNow);
        var lampIndex = 0;
        foreach (var x in new[] { 469, 811 })
        for (var lamp = 0; lamp < 3; lamp++)
        {
            var y = 620 + lamp * 22;
            var blink = (lampNow.Ticks / TimeSpan.TicksPerMillisecond / 90) % 2 == 0;
            var hitFlash = skyRailLocalDamage.LampFlashing(lampIndex, lampNow);
            var damaged = lampIndex++ < damage;
            var light = repairing ? (blink ? 0xFF6577u : 0x641F2Bu) :
                hitFlash ? (blink ? 0xF4F3FCu : 0x61C5E8u) : damaged ? 0xE94A5Eu : 0x61C5E8u;
            FillRoulettePrototypeCircle(pixels, width, height, x, y, 8, 0x9A7545);
            FillRoulettePrototypeCircle(pixels, width, height, x, y, 5, light);
            FillRoulettePrototypeCircle(pixels, width, height, x - 1, y - 1, 2,
                damaged ? 0xFFB6BFu : 0xD5F5FFu);
        }
    }

    private void DrawSkyRailBoss(byte[] pixels, int width, int height, DateTime now)
    {
        var position = SkyRailBossPosition(now, skyRailWaveStartsUtc);
        var retreating = SkyRailBossRetreating(now);
        if (retreating)
        {
            position = SkyRailBossPosition(skyRailPhaseEndsUtc.AddSeconds(30), skyRailWaveStartsUtc);
            var progress = SkyRailRules.BossRetreatProgress(now, skyRailPhaseEndsUtc);
            position.Y -= 620 * progress * progress;
        }
        var x = (int)position.X;
        var y = (int)position.Y;
        // Clip retreating artwork to the battle view so it flies behind the header.
        byte[]? protectedHeader = null;
        if (retreating)
        {
            protectedHeader = new byte[106 * width * 4];
            Buffer.BlockCopy(pixels, 0, protectedHeader, 0, protectedHeader.Length);
        }
        if (skyRailMap == 0)
            DrawSkyRailStormcloud(pixels, width, height, x, y, now);
        else if (skyRailMap == 1)
            DrawSkyRailCrystalSentinel(pixels, width, height, x, y, now);
        else if (skyRailMap == 2)
            DrawSkyRailIronVulture(pixels, width, height, x, y, now);
        else if (skyRailMap == 3)
            DrawSkyRailNeonEnforcer(pixels, width, height, x, y, now);
        else if (skyRailMap == 4)
            DrawSkyRailClockworkWarden(pixels, width, height, x, y, now);
        else
        {
            const int radius = 102;
            var turn = (now - skyRailWaveStartsUtc).TotalSeconds * 0.28d;
            for (var fin = 0; fin < 8; fin++)
                SkyRailTargetFin(pixels, width, height, x, y, radius,
                    turn + fin * Math.PI / 4d, 0.15d, 0x493075, 0xB77CF5);
            FillRoulettePrototypeCircle(pixels, width, height, x, y, radius + 4, 0x312344);
            DrawRoulettePrototypeRing(pixels, width, height, x, y, radius - 2, radius + 2, 0xE5B7FF);
            FillRoulettePrototypeCircle(pixels, width, height, x, y, radius - 5, 0x68428F);
            DrawRoulettePrototypeRing(pixels, width, height, x, y, 65, 72, 0xAE80D9);
            FillRoulettePrototypeCircle(pixels, width, height, x, y, 62, 0x4C2D6E);
            FillRoulettePrototypeCircle(pixels, width, height, x - 24, y - 12, 11, 0xD8FA91);
            FillRoulettePrototypeCircle(pixels, width, height, x + 24, y - 12, 11, 0xD8FA91);
            FillRoulettePrototypeCircle(pixels, width, height, x - 23, y - 11, 5, 0x1E1532);
            FillRoulettePrototypeCircle(pixels, width, height, x + 25, y - 11, 5, 0x1E1532);
            RaceLine(pixels, width, height, x - 25, y + 28, x, y + 38, 0xD9A9F5, 5);
            RaceLine(pixels, width, height, x, y + 38, x + 25, y + 28, 0xD9A9F5, 5);
        }

        if (protectedHeader is not null)
        {
            Buffer.BlockCopy(protectedHeader, 0, pixels, 0, protectedHeader.Length);
            return;
        }
        var barX = x - 110;
        var barY = y - 132;
        FillQuizTvPanel(pixels, width, height, barX, barY, 220, 18, 0x172A28, 0x72E77F);
        FillPictionaryPixels(pixels, width, height, barX + 3, barY + 3,
            (int)(214f * skyRailBossHealth / skyRailBossMaxHealth), 12, 0x53D66F);
        DrawPictionaryPixelTextCentered(pixels, width, height, x, barY - 19,
            $"BOSS {skyRailBossHealth}/{skyRailBossMaxHealth}", 2, 0xD9F2D5);
    }

    private void DrawSkyRailRocket(byte[] pixels, int width, int height,
        SkyRailRocket rocket, DateTime now)
    {
        if (rocket.DestroyAtUnixMs > 0 &&
            new DateTimeOffset(now).ToUnixTimeMilliseconds() >= rocket.DestroyAtUnixMs) return;
        var pose = SkyRailRocketPose(rocket, now);
        if (!pose.Visible) return;
        var x = (int)pose.Position.X;
        var y = (int)pose.Position.Y;
        var size = (int)pose.Radius;
        if (skyRailMap == 0)
        {
            DrawSkyRailStormBolt(pixels, width, height, x, y, size, now);
            return;
        }
        if (skyRailMap == 1)
        {
            DrawSkyRailCrystalRocket(pixels, width, height, x, y, size, now);
            return;
        }
        if (skyRailMap == 2)
        {
            DrawSkyRailCanyonMissile(pixels, width, height, x, y, size, now);
            return;
        }
        if (skyRailMap == 3)
        {
            DrawSkyRailNeonTorpedo(pixels, width, height, x, y, size, now);
            return;
        }
        if (skyRailMap == 4)
        {
            DrawSkyRailBrassRocket(pixels, width, height, x, y, size, now);
            return;
        }
        RaceTriangle(pixels, width, height,
            x, y + size, x - size, y - size / 2, x + size, y - size / 2, 0xCF4B84);
        RaceTriangle(pixels, width, height,
            x, y + size - 5, x - size / 2, y - size / 3,
            x + size / 2, y - size / 3, 0xF3A3CE);
        FillRoulettePrototypeCircle(pixels, width, height, x, y, Math.Max(4, size / 3), 0x472342);
        RaceTriangle(pixels, width, height,
            x, y - size - 13, x - 5, y - size / 3, x + 5, y - size / 3, 0xFFB24D);
    }

    private static void DrawSkyRailSkull(byte[] pixels, int width, int height, int x, int y)
    {
        FillRoulettePrototypeCircle(pixels, width, height, x, y, 10, 0xF06A76);
        FillPictionaryPixels(pixels, width, height, x - 7, y + 5, 14, 7, 0xF06A76);
        FillPictionaryPixels(pixels, width, height, x - 6, y - 3, 4, 4, 0x4A1A2B);
        FillPictionaryPixels(pixels, width, height, x + 2, y - 3, 4, 4, 0x4A1A2B);
        FillPictionaryPixels(pixels, width, height, x - 3, y + 8, 2, 4, 0x4A1A2B);
        FillPictionaryPixels(pixels, width, height, x + 1, y + 8, 2, 4, 0x4A1A2B);
    }
}

using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private sealed record SkyRailRocket(int Id, long LaunchedUnixMs, int StartX, int StartY,
        int FanOffset, long DestroyAtUnixMs = 0);

    private int skyRailBossMaxHealth = 70;
    private const int SkyRailBossTargetId = -2;
    private const int SkyRailRocketTargetBase = -1000;
    private const double SkyRailRocketTravelSeconds = 3.6d;
    private readonly List<SkyRailRocket> skyRailRockets = [];
    private readonly List<int> skyRailEscapedRocketIds = [];
    private readonly byte[] skyRailPagePixels = new byte[WhiteboardRenderer.Width * WhiteboardRenderer.Height * 4];
    private readonly List<(DateTime At, int Damage)> skyRailPendingBossImpacts = [];
    private bool skyRailBossAppeared;
    private int skyRailBossHealth;
    private int skyRailNextRocketId;
    private DateTime skyRailNextRocketRollUtc;
    private bool SkyRailBossRetreating(DateTime now) => skyRailPhase == SkyRailPhase.Playing &&
        skyRailBossHealth > 0 && now >= skyRailPhaseEndsUtc.AddSeconds(30);
    private bool SkyRailBossRetreatAnimation(DateTime now) => SkyRailBossRetreating(now) &&
        SkyRailRules.BossRetreatProgress(now, skyRailPhaseEndsUtc) < 1;
    private bool SkyRailBossWarning(DateTime now) => skyRailPhase == SkyRailPhase.Playing &&
        SkyRailRules.BossArriving(now, skyRailWaveStartsUtc, skyRailBossHealth);

    private void ResetSkyRailBossRound()
    {
        ResetSkyRailLocalDamage();
        skyRailEscapedRocketIds.Clear();
        skyRailBossDefeatedUtc = DateTime.MinValue;
        ResetSkyRailStormFade();
        ResetSkyRailBossAlert();
        skyRailBossAppeared = false;
        skyRailBossHealth = 0;
        skyRailBossMaxHealth = 70;
        skyRailNextRocketId = 0;
        skyRailNextRocketRollUtc = DateTime.MinValue;
        skyRailRockets.Clear();
        skyRailPendingBossImpacts.Clear();
    }

    private static Vector2 SkyRailBossPosition(DateTime now, DateTime waveStartsUtc)
    {
        var elapsed = Math.Max(0d, (now - waveStartsUtc).TotalSeconds);
        return new Vector2(640f + (float)Math.Sin(elapsed * 0.72d) * 135f,
            335f + (float)Math.Sin(elapsed * 1.13d + 0.7d) * 35f);
    }

    private static (Vector2 Position, float Radius, bool Visible) SkyRailRocketPose(
        SkyRailRocket rocket, DateTime now)
    {
        var launched = DateTimeOffset.FromUnixTimeMilliseconds(rocket.LaunchedUnixMs).UtcDateTime;
        var age = (now - launched).TotalSeconds;
        if (age < 0d || age >= SkyRailRocketTravelSeconds)
            return (Vector2.Zero, 0f, false);
        var progress = (float)(age / SkyRailRocketTravelSeconds);
        var depth = MathF.Pow(progress, 1.2f);
        var x = rocket.StartX + (640f - rocket.StartX) * depth +
            rocket.FanOffset * MathF.Sin(progress * MathF.PI);
        var y = rocket.StartY + (564f - rocket.StartY) * depth;
        return (new Vector2(x, y), 12f + 18f * depth, true);
    }

    private void UpdateSkyRailBoss(DateTime now)
    {
        if (skyRailBossHealth <= 0 || SkyRailBossWarning(now)) return;
        if (SkyRailBossRetreating(now))
        {
            skyRailRockets.Clear();
            skyRailPendingBossImpacts.Clear();
            return;
        }
        var resolvedHits = skyRailPendingBossImpacts.Where(impact => now >= impact.At).Sum(impact => impact.Damage);
        skyRailPendingBossImpacts.RemoveAll(impact => now >= impact.At);
        if (resolvedHits > 0)
        {
            skyRailBossHealth = Math.Max(0, skyRailBossHealth - resolvedHits);
            if (skyRailBossHealth == 0)
            {
                if (skyRailMap is 0 or 1 or 2 or 3 or 4) skyRailBossDefeatedUtc = now;
                skyRailRockets.Clear();
                // Award the shared defeat once, on the transition to zero health.
                // Retreating on timeout never reaches this branch.
                foreach (var playerId in skyRailLocked)
                    if (skyRailPlayers.TryGetValue(playerId, out var player))
                        skyRailPlayers[playerId] = player with { Score = player.Score + 400 };
            }
            skyRailRevision++;
            SendSkyRailState();
        }
        if (skyRailBossHealth <= 0) return;
        while (now >= skyRailNextRocketRollUtc)
        {
            var rollAt = skyRailNextRocketRollUtc;
            skyRailNextRocketRollUtc = skyRailNextRocketRollUtc.AddSeconds(2.5);
            if (Random.Shared.NextDouble() >= 0.4d) continue;
            var count = Random.Shared.Next(1, 4);
            if (skyRailLocked.Count == 0) continue;
            var origin = SkyRailBossPosition(rollAt, skyRailWaveStartsUtc);
            for (var index = 0; index < count; index++)
            {
                var offset = (index - (count - 1) / 2f) * 65f;
                skyRailRockets.Add(new SkyRailRocket(++skyRailNextRocketId,
                    new DateTimeOffset(rollAt).ToUnixTimeMilliseconds(),
                    (int)origin.X + (int)offset, (int)origin.Y + 80,
                    (int)offset));
            }
            skyRailRevision++;
            SendSkyRailState();
        }
        for (var index = skyRailRockets.Count - 1; index >= 0; index--)
        {
            var rocket = skyRailRockets[index];
            if (rocket.DestroyAtUnixMs > 0)
            {
                if (new DateTimeOffset(now).ToUnixTimeMilliseconds() < rocket.DestroyAtUnixMs)
                    continue;
                skyRailRockets.RemoveAt(index);
                skyRailRevision++;
                SendSkyRailState();
                continue;
            }
            var launched = DateTimeOffset.FromUnixTimeMilliseconds(rocket.LaunchedUnixMs).UtcDateTime;
            if (now < launched.AddSeconds(SkyRailRocketTravelSeconds)) continue;
            skyRailRockets.RemoveAt(index);
            skyRailEscapedRocketIds.Add(rocket.Id);
            UpdateSkyRailLocalDamage(now);
            foreach (var playerId in skyRailLocked)
                if (skyRailPlayers.TryGetValue(playerId, out var player))
                    skyRailPlayers[playerId] = player with
                        { Score = Math.Max(0, player.Score - 200), Combo = 0 };
            skyRailRevision++;
            SendSkyRailState();
        }
    }

    private int TryHitSkyRailBossOrRocket(Vector2 shot, DateTime now, out Vector2 impactPoint)
    {
        impactPoint = shot;
        if (SkyRailBossRetreating(now)) return -1;
        for (var index = skyRailRockets.Count - 1; index >= 0; index--)
        {
            var rocket = skyRailRockets[index];
            if (rocket.DestroyAtUnixMs > 0) continue;
            var pose = SkyRailRocketPose(rocket, now);
            if (!pose.Visible || Vector2.Distance(shot, pose.Position) > pose.Radius + 12f)
                continue;
            var future = SkyRailRocketPose(rocket, now.AddSeconds(SkyRailFireballFlightSeconds));
            if (!future.Visible) continue;
            impactPoint = future.Position;
            skyRailRockets[index] = rocket with
                { DestroyAtUnixMs = new DateTimeOffset(now.AddSeconds(SkyRailFireballFlightSeconds))
                    .ToUnixTimeMilliseconds() };
            skyRailRevision++;
            return SkyRailRocketTargetBase - rocket.Id;
        }
        if (SkyRailBossWarning(now) || skyRailBossHealth - skyRailPendingBossImpacts.Sum(impact => impact.Damage) <= 0) return -1;
        var boss = SkyRailBossPosition(now, skyRailWaveStartsUtc);
        if (Vector2.Distance(shot, boss) > 109f) return -1;
        impactPoint = SkyRailBossPosition(now.AddSeconds(SkyRailFireballFlightSeconds),
            skyRailWaveStartsUtc);
        var offset = shot - boss;
        var damage = SkyRailRules.BossHitDamage(skyRailMap, offset.X, offset.Y);
        skyRailPendingBossImpacts.Add((now.AddSeconds(SkyRailFireballFlightSeconds), damage));
        return SkyRailBossTargetId;
    }
}

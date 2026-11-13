using System.Globalization;
using System.Text;
using System.Text.Json;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool skyRailHostSnapshotRejectedLogged;
    private enum SkyRailPhase { Inactive, Lobby, Countdown, Playing, Results }
    private sealed record SkyRailPlayer(string Id, string Name, int Score, int Combo, bool PersonalBest = false);
    private sealed record SkyRailTarget(int Id, int Kind, int X, int Y, int Radius,
        int Drift, double Speed, double Offset);
    private sealed record SkyRailSnapshot(string SessionId, int Revision, int Round,
        SkyRailPhase Phase, long PhaseEndsUnixMs, int Wave, long WaveStartsUnixMs,
        string[] ReadyIds, string[] LockedIds, SkyRailPlayer[] Players, SkyRailTarget[] Targets,
        int Map, bool BossAppeared, int BossHealth, SkyRailRocket[] Rockets, int WaveCount = 8, int BossMode = 0, int BossMaxHealth = 70, long BossDefeatedUnixMs = 0, int WaveSeconds = 10, int[]? EscapedRocketIds = null, bool CustomRules = false, int MapChoice = -1);
    private sealed record SkyRailShot(string SessionId, int Round, string Id, string PlayerId, int X, int Y,
        int TargetId, int Points, int Score, int Combo, long FiredUnixMs,
        int ImpactX = 0, int ImpactY = 0, int BossHealth = 0, int Wave = 0, int Revision = 0);

    private int skyRailWaveCount = 8;
    private int skyRailBossMode;
    private bool skyRailCustomRules;
    private int skyRailCustomWaves = 8;
    private int skyRailMapChoice;
    private int skyRailCustomBossMode;
    private int skyRailWaveSeconds = 15;
    private const double SkyRailTargetTravelSeconds = 4.8d;
    private double SkyRailTargetSpawnIntervalSeconds(int targetCount) =>
        (skyRailWaveSeconds - SkyRailTargetTravelSeconds - 0.4d) /
        Math.Max(1, targetCount - 1);
    private readonly Dictionary<string, SkyRailPlayer> skyRailPlayers = new(StringComparer.Ordinal);
    private readonly HashSet<string> skyRailReady = new(StringComparer.Ordinal);
    private readonly HashSet<string> skyRailLocked = new(StringComparer.Ordinal);
    private readonly HashSet<string> skyRailHitKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<int, DateTime> skyRailLocalHitAt = new();
    private readonly HashSet<string> skyRailShotIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> skyRailLastShotByPlayer = new(StringComparer.Ordinal);
    private readonly List<(SkyRailShot Shot, DateTime ShownUtc)> skyRailShots = [];
    private readonly List<SkyRailTarget> skyRailTargets = [];
    private string? skyRailSessionId;
    private string? skyRailWhiteboardSessionId;
    private SkyRailPhase skyRailPhase;
    private DateTime skyRailPhaseEndsUtc = DateTime.MaxValue;
    private DateTime skyRailWaveStartsUtc;
    private DateTime skyRailLastSnapshotUtc;
    private DateTime skyRailLastFrameUtc;
    private DateTime skyRailLastLocalFireUtc;
    private int skyRailRound = 1;
    private int skyRailWave;
    private int skyRailRevision;
    private int skyRailMap;
    private Vector2? skyRailTvHover;
    private Vector2? skyRailPageHover;
    private WhiteboardRenderer? skyRailPageRenderer;

    internal bool SkyRailActive => skyRailSessionId is not null && skyRailPhase != SkyRailPhase.Inactive;
    internal bool SkyRailTvControlsInteractive => SkyRailActive;
    private string LocalSkyRailId => CurrentSession?.AccountId ?? stream.LocalUserId ?? "local";
    private bool SkyRailCanFire => SkyRailActive && skyRailPhase == SkyRailPhase.Playing &&
        skyRailLocked.Contains(LocalSkyRailId) && !SkyRailRepairing(DateTime.UtcNow) &&
        !SkyRailBossRetreating(DateTime.UtcNow);
    private int SkyRailSecondsLeft => skyRailPhaseEndsUtc == DateTime.MaxValue ? 0 :
        Math.Max(0, (int)Math.Ceiling((skyRailPhaseEndsUtc - DateTime.UtcNow).TotalSeconds));
    private int SkyRailBossSecondsLeft => skyRailPhaseEndsUtc == DateTime.MaxValue ? 0 :
        Math.Max(0, (int)Math.Ceiling((skyRailPhaseEndsUtc.AddSeconds(30) - DateTime.UtcNow).TotalSeconds));

    private void OpenSkyRailPage()
    {
        if (!CanLaunchActivity(ActiveActivityKind.SkyRail)) return;
        currentPage = HomePage.SkyRail;
        lastActivitiesPage = HomePage.SkyRail;
    }

    private void StartSkyRailGame()
    {
        if (!IsLocalActivityHost || CurrentSession is null ||
            ActiveActivity != ActiveActivityKind.None) return;
        skyRailSessionId = Guid.NewGuid().ToString("N");
        skyRailPhase = SkyRailPhase.Lobby;
        skyRailRound = 1;
        skyRailRevision = 1;
        skyRailWave = 0;
        skyRailMapChoice = skyRailCustomRules ? 0 : -1;
        skyRailMap = 0;
        skyRailWaveCount = skyRailCustomRules ? Math.Clamp(skyRailCustomWaves, 4, 12) : 8;
        skyRailBossMode = skyRailCustomRules ? Math.Clamp(skyRailCustomBossMode, 0, 2) : 1;
        skyRailWaveSeconds = skyRailCustomRules ? 10 : 15;
        ResetSkyRailPersonalBestRound();
        skyRailLastLocalFireUtc = DateTime.MinValue;
        skyRailWaveStartsUtc = DateTime.MinValue;
        skyRailPhaseEndsUtc = DateTime.MaxValue;
        skyRailReady.Clear();
        skyRailLocked.Clear();
        skyRailPlayers.Clear();
        skyRailTargets.Clear();
        skyRailHitKeys.Clear();
        skyRailLocalHitAt.Clear();
        skyRailShotIds.Clear();
        skyRailShots.Clear();
        skyRailLastShotByPlayer.Clear();
        ResetSkyRailBossRound();
        StartHostWhiteboard(WhiteboardMode.Solo, ActiveActivityKind.SkyRail);
        skyRailWhiteboardSessionId = whiteboardSessionId;
        whiteboardTvDirty = true;
        SendSkyRailState();
    }

    private void StopSkyRailGame()
    {
        if (IsLocalActivityHost && skyRailSessionId is { } session)
            _ = stream.SendSkyRailCommandAsync("X|" + session);
        if (IsLocalActivityHost && whiteboardSessionId is { } canvas &&
            string.Equals(canvas, skyRailWhiteboardSessionId, StringComparison.Ordinal))
            SendAndApplyWhiteboardCommand("X|" + canvas);
        ResetSkyRailLocal();
    }

    private void ResetSkyRailLocal()
    {
        CaptureArcadeScoresBeforeReset();
        skyRailViewerSnapshotLogged = skyRailViewerFrameLogged = skyRailHostSnapshotRejectedLogged = false;
        RetireArcadePageRenderer(ref skyRailPageRenderer);
        ResetRemainingRelay("sky-rail");
        ResetSkyRailPersonalBestRound();
        ResetSkyRailSettingsUi();
        ResetSkyRailAudio();
        skyRailSessionId = null;
        skyRailWhiteboardSessionId = null;
        skyRailPhase = SkyRailPhase.Inactive;
        skyRailPhaseEndsUtc = DateTime.MaxValue;
        skyRailWaveStartsUtc = DateTime.MinValue;
        skyRailWave = 0;
        skyRailMap = 0;
        skyRailReady.Clear();
        skyRailLocked.Clear();
        skyRailPlayers.Clear();
        skyRailTargets.Clear();
        skyRailHitKeys.Clear();
        skyRailLocalHitAt.Clear();
        skyRailShotIds.Clear();
        skyRailShots.Clear();
        skyRailLastShotByPlayer.Clear();
        ResetSkyRailBossRound();
        skyRailTvHover = skyRailPageHover = null;
        whiteboardTvDirty = true;
    }

    internal void UpdateSkyRail()
    {
        if (stream.Mode == StreamMode.None)
        {
            stream.IncomingSkyRailCommands.Clear();
        }
        while (stream.IncomingSkyRailCommands.TryDequeue(out var incoming))
        {
            if (IsLocalActivityHost)
                ReceiveArcadeServerCommand("sky-rail", incoming.UserId, incoming.Command);
            else
                ReceiveArcadeServerCommand("sky-rail", incoming.UserId, incoming.Command);
        }
        UpdateActivityRelay("sky-rail");
        if (!SkyRailActive) return;
        var now = DateTime.UtcNow;
        UpdateSkyRailLocalDamage(now);
        if (IsLocalActivityHost)
        {
            if (!whiteboardActive ||
                !string.Equals(whiteboardSessionId, skyRailWhiteboardSessionId, StringComparison.Ordinal))
            {
                StopSkyRailGame();
                return;
            }
            PruneSkyRailParticipants();
            if (skyRailPhase == SkyRailPhase.Countdown && now >= skyRailPhaseEndsUtc)
                BeginSkyRailWave(now, 1);
            else if (skyRailPhase == SkyRailPhase.Playing)
            {
                UpdateSkyRailBoss(now);
                if (skyRailLocked.Count == 0 || !SkyRailBossExploding(now) && !SkyRailBossRetreatAnimation(now) && SkyRailRules.WaveComplete(now, skyRailPhaseEndsUtc, skyRailBossHealth))
                {
                    skyRailBossHealth = 0;
                    skyRailRockets.Clear();
                    skyRailPendingBossImpacts.Clear();
                    if (skyRailLocked.Count > 0 && skyRailWave < skyRailWaveCount)
                        BeginSkyRailWave(now, skyRailWave + 1);
                    else
                    {
                        skyRailPhase = SkyRailPhase.Results;
                        skyRailPhaseEndsUtc = DateTime.MaxValue;
                        skyRailRevision++;
                        SendSkyRailState();
                    }
                }
            }
            if (now - skyRailLastSnapshotUtc >= TimeSpan.FromSeconds(3))
                SendSkyRailState();
        }
        UpdateSkyRailPersonalBest();
        UpdateSkyRailAudio(now);
        skyRailShots.RemoveAll(item => now - item.ShownUtc >
            TimeSpan.FromSeconds(SkyRailFireballFlightSeconds + SkyRailImpactRingSeconds));
        if (now - skyRailLastFrameUtc >= TimeSpan.FromMilliseconds(50))
        {
            skyRailLastFrameUtc = now;
            var showPage = IsOpen && currentPage == HomePage.SkyRail;
            var showTv = ShouldRenderArcadeTv;
            if (!showPage && !showTv) return;
            var frameStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var pixels = skyRailPagePixels;
            ApplySkyRailTvOverlay(pixels, WhiteboardRenderer.Width, WhiteboardRenderer.Height);
            PlaybackRenderDiagnostics.RecordArcadeRender("Air Force EX",
                System.Diagnostics.Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds);
            if (showPage)
            {
                if (skyRailPageRenderer is null && !screenController.Engine.TryInitializeGraphics()) return;
                skyRailPageRenderer ??= DxHandler.RunOnRenderThreadAndWait(
                    "AlphaChannel.SkyRailPage.Create",
                    () => new WhiteboardRenderer("AlphaChannel.SkyRailPage"),
                    TimeSpan.FromSeconds(30));
                skyRailPageRenderer.Upload(pixels);
            }
            if (showTv)
            {
                screenController.Engine.UpdateWhiteboard(pixels);
                if (stream.Mode == StreamMode.Viewing && !skyRailViewerFrameLogged)
                {
                    skyRailViewerFrameLogged = true;
                    
                }
            }
            whiteboardTvDirty = false;
        }
    }

    private void BeginSkyRailWave(DateTime now, int wave)
    {
        skyRailPhase = SkyRailPhase.Playing;
        skyRailWave = wave;
        skyRailEscapedRocketIds.Clear();
        skyRailWaveStartsUtc = now;
        skyRailBossDefeatedUtc = DateTime.MinValue;
        skyRailPhaseEndsUtc = now.AddSeconds(skyRailWaveSeconds);
        skyRailTargets.Clear();
        skyRailLocalHitAt.Clear();
        skyRailRockets.Clear();
        skyRailPendingBossImpacts.Clear();
        skyRailNextRocketId = 0;
        var random = new Random(HashCode.Combine(skyRailSessionId, skyRailRound, wave));
        if (!skyRailBossAppeared && skyRailBossMode != 2 && wave >= 2 && wave <= skyRailWaveCount - 2 &&
            (skyRailBossMode == 1 ? wave == skyRailWaveCount / 2 : random.NextDouble() < 0.15d))
        {
            skyRailBossAppeared = true;
            skyRailBossMaxHealth = SkyRailRules.BossHealthForPlayers(skyRailLocked.Count);
            skyRailBossHealth = skyRailBossMaxHealth;
            skyRailNextRocketRollUtc = now.AddSeconds(SkyRailRules.BossWarningTotalSeconds + 2);
        }
        else skyRailBossHealth = 0;
        var kinds = new List<int>(18);
        for (var index = 0; index < 5; index++)
        {
            var roll = random.Next(100);
            kinds.Add(roll < 49 ? 0 : roll < 74 ? 1 : roll < 88 ? 2 : 3);
        }
        if (kinds.All(kind => kind == 3)) kinds[random.Next(kinds.Count)] = random.Next(3);
        kinds.RemoveAll(kind => kind == 3);
        var baseNonRedCount = kinds.Count;
        var extraNonRedCount = (int)Math.Round(baseNonRedCount * 0.5d, MidpointRounding.AwayFromZero);
        for (var index = 0; index < extraNonRedCount; index++)
        {
            var roll = random.Next(88);
            kinds.Add(roll < 49 ? 0 : roll < 74 ? 1 : 2);
        }
        var redCount = random.Next(3, 11);
        for (var index = 0; index < redCount; index++) kinds.Add(3);
        for (var index = kinds.Count - 1; index > 0; index--)
        {
            var swap = random.Next(index + 1);
            (kinds[index], kinds[swap]) = (kinds[swap], kinds[index]);
        }
        var flightPaths = new (int X, int Y)[]
        {
            (135, 405), (255, 285), (360, 195), (460, 385), (560, 255),
            (670, 170), (790, 320), (910, 220), (1025, 375), (1135, 285),
            (195, 210), (315, 395), (435, 300), (555, 405), (685, 300),
            (805, 405), (935, 315), (1065, 200),
        };
        for (var index = flightPaths.Length - 1; index > 0; index--)
        {
            var swap = random.Next(index + 1);
            (flightPaths[index], flightPaths[swap]) = (flightPaths[swap], flightPaths[index]);
        }
        for (var index = 0; index < kinds.Count; index++)
        {
            var kind = kinds[index];
            skyRailTargets.Add(new SkyRailTarget(wave * 100 + index, kind,
                flightPaths[index].X + random.Next(-45, 46),
                flightPaths[index].Y + random.Next(-30, 31),
                kind == 2 ? 39 : 34,
                35 + random.Next(0, 31),
                0.9 + random.NextDouble() * 0.9,
                random.NextDouble() * Math.PI * 2));
        }
        skyRailRevision++;
        SendSkyRailState();
    }

    private void BeginNextSkyRailLobby()
    {
        if (!IsLocalActivityHost || skyRailPhase != SkyRailPhase.Results) return;
        ObserveArcadeScores();
        skyRailRound++;
        ResetSkyRailPersonalBestRound();
        skyRailPhase = SkyRailPhase.Lobby;
        skyRailPhaseEndsUtc = DateTime.MaxValue;
        skyRailWave = 0;
        skyRailShots.Clear();
        skyRailLastLocalFireUtc = DateTime.MinValue;
        skyRailWaveStartsUtc = DateTime.MinValue;
        skyRailReady.Clear();
        skyRailLocked.Clear();
        skyRailPlayers.Clear();
        skyRailTargets.Clear();
        skyRailHitKeys.Clear();
        skyRailLocalHitAt.Clear();
        skyRailShotIds.Clear();
        skyRailLastShotByPlayer.Clear();
        ResetSkyRailBossRound();
        skyRailRevision++;
        SendSkyRailState();
    }

    private void SkyRailReadyUp()
    {
        if (!SkyRailActive || skyRailPhase != SkyRailPhase.Lobby) return;
        var ready = !skyRailReady.Contains(LocalSkyRailId);
        if (IsLocalActivityHost)
            AcceptSkyRailReady(LocalSkyRailId, ready);
        else
            SendArcadePlayerAction("sky-rail", $"R|{skyRailSessionId}|{skyRailRound}|{(ready ? 1 : 0)}");
    }

    private void AcceptSkyRailReady(string playerId, bool ready)
    {
        if (!IsLocalActivityHost || skyRailPhase != SkyRailPhase.Lobby ||
            (playerId != LocalSkyRailId &&
             !stream.Roster.Any(player => player.UserId == playerId))) return;
        if (!ready)
        {
            if (!skyRailReady.Remove(playerId)) return;
            skyRailPlayers.Remove(playerId);
            skyRailRevision++;
            SendSkyRailState();
            return;
        }
        if (!skyRailReady.Add(playerId)) return;
        var name = playerId == LocalSkyRailId ? LocalPictionaryDisplayName() :
            stream.Roster.FirstOrDefault(player => player.UserId == playerId)?.DisplayName ?? "Player";
        skyRailPlayers[playerId] = new SkyRailPlayer(playerId, name, 0, 0);
        skyRailRevision++;
        SendSkyRailState();
    }

    private void ChangeSkyRailLobbyMap(int direction)
    {
        if (!IsLocalActivityHost || !skyRailCustomRules || skyRailPhase != SkyRailPhase.Lobby) return;
        skyRailMapChoice = SkyRailRules.NextLobbyMap(skyRailMapChoice, direction, SkyRailMapNames.Length);
        skyRailMap = SkyRailRules.StartMap(true, skyRailMapChoice,
            Random.Shared.Next(SkyRailMapNames.Length), SkyRailMapNames.Length);
        skyRailRevision++;
        whiteboardTvDirty = true;
        SendSkyRailState();
    }

    private void StartSkyRailCountdown()
    {
        if (!IsLocalActivityHost || skyRailPhase != SkyRailPhase.Lobby ||
            skyRailReady.Count == 0) return;
        skyRailMap = SkyRailRules.StartMap(skyRailCustomRules, skyRailMapChoice,
            Random.Shared.Next(SkyRailMapNames.Length), SkyRailMapNames.Length);
        skyRailLocked.Clear();
        skyRailLocked.UnionWith(skyRailReady);
        skyRailPhase = SkyRailPhase.Countdown;
        skyRailPhaseEndsUtc = DateTime.UtcNow.AddSeconds(6);
        skyRailRevision++;
        SendSkyRailState();
    }

    private (Vector2 Position, float Radius, bool Visible) SkyRailTargetPose(SkyRailTarget target, DateTime now,
        DateTime waveStartsUtc, int targetCount)
    {
        var age = (now - waveStartsUtc).TotalSeconds -
            (0.2d + target.Id % 100 * SkyRailTargetSpawnIntervalSeconds(targetCount));
        if (age < 0d || age >= SkyRailTargetTravelSeconds)
            return (Vector2.Zero, 0f, false);
        var progress = (float)(age / SkyRailTargetTravelSeconds);
        var depth = MathF.Pow(progress, 1.35f);
        var phase = age * target.Speed + target.Offset;
        var startX = 640f + (target.X - 640f) * 0.55f;
        var startY = 165f + (target.Y - 180f) * 0.35f;
        var sway = (float)(Math.Sin(phase) - Math.Sin(target.Offset));
        var weave = (float)Math.Sin(phase * 0.55d + target.Offset) *
            target.Drift * depth * 0.18f;
        var x = Math.Clamp(startX + (target.X - startX) * depth +
            sway * target.Drift * (0.3f + 0.6f * depth) + weave, 100f, 1180f);
        var y = startY + (target.Y - startY) * depth +
            120f * depth * depth * depth * depth +
            (float)Math.Sin(phase * 0.7d) * target.Drift * depth * 0.08f;
        var radius = 9f + target.Radius * depth;
        return (new Vector2(x, y), radius, true);
    }

    private void FireSkyRail(Vector2 uv)
    {
        if (skyRailSettingsOpen || !SkyRailCanFire || uv.X is < 0 or > 1 || uv.Y is < 0 or > 1) return;
        var now = DateTime.UtcNow;
        if (now - skyRailLastLocalFireUtc < TimeSpan.FromMilliseconds(450)) return;
        var x = Math.Clamp((int)(uv.X * WhiteboardRenderer.Width), 80, 1195);
        var y = Math.Clamp((int)(uv.Y * WhiteboardRenderer.Height), 130, 565);
        if (y is < 130 or > 565) return;
        if (skyRailBossHealth <= 0 && now >= skyRailPhaseEndsUtc) return;
        skyRailLastLocalFireUtc = now;
        var id = Guid.NewGuid().ToString("N");
        var shot = new SkyRailShot(skyRailSessionId!, skyRailRound,
            id, LocalSkyRailId, x, y, -1, 0, 0, 0,
            new DateTimeOffset(now).ToUnixTimeMilliseconds());
        ShowSkyRailShot(shot);
        if (IsLocalActivityHost)
            AcceptSkyRailShot(LocalSkyRailId, id, x, y);
        else
            SendArcadePlayerAction("sky-rail",
                $"F|{skyRailSessionId}|{skyRailRound}|{skyRailWave}|{id}|{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}");
    }

    private void AcceptSkyRailShot(string playerId, string shotId, int x, int y)
    {
        if (!IsLocalActivityHost || skyRailPhase != SkyRailPhase.Playing ||
            !skyRailLocked.Contains(playerId) || !Guid.TryParseExact(shotId, "N", out _) ||
            x is < 80 or > 1195 || y is < 130 or > 565 ||
            !skyRailShotIds.Add(shotId)) return;
        var now = DateTime.UtcNow;
        if (SkyRailRules.WaveComplete(now, skyRailPhaseEndsUtc, skyRailBossHealth)) return;
        if (skyRailLastShotByPlayer.TryGetValue(playerId, out var last) &&
            now - last < TimeSpan.FromMilliseconds(450)) return;
        skyRailLastShotByPlayer[playerId] = now;
        if (!skyRailPlayers.TryGetValue(playerId, out var player)) return;
        var specialTargetId = TryHitSkyRailBossOrRocket(new Vector2(x, y), now,
            out var impactPoint);
        SkyRailTarget? hit = null;
        var best = float.MaxValue;
        if (specialTargetId == -1)
        foreach (var target in skyRailTargets)
        {
            if (skyRailHitKeys.Contains($"{playerId}|{target.Id}")) continue;
            var pose = SkyRailTargetPose(target, now, skyRailWaveStartsUtc, skyRailTargets.Count);
            if (!pose.Visible) continue;
            var distance = Vector2.Distance(new Vector2(x, y), pose.Position);
            if (distance <= pose.Radius + 14f && distance < best)
            {
                best = distance;
                hit = target;
            }
        }
        var (points, score, combo) = SkyRailRules.ScoreShot(player.Score, player.Combo,
            hit?.Kind ?? -1, specialTargetId != -1);
        skyRailPlayers[playerId] = player with { Score = score, Combo = combo };
        if (hit is not null) skyRailHitKeys.Add($"{playerId}|{hit.Id}");
        skyRailRevision++;
        var shot = new SkyRailShot(skyRailSessionId!, skyRailRound,
            shotId, playerId, x, y, specialTargetId == -1 ? hit?.Id ?? -1 : specialTargetId,
            points, score, combo, new DateTimeOffset(now).ToUnixTimeMilliseconds(),
            (int)impactPoint.X, (int)impactPoint.Y, skyRailBossHealth, skyRailWave, skyRailRevision);
        ShowSkyRailShot(shot);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(shot)));
        if (!SendRemainingEvent("sky-rail", "E|" + encoded)) _ = stream.SendSkyRailCommandAsync("E|" + encoded);
        whiteboardTvDirty = true;
    }

    private void ShowSkyRailShot(SkyRailShot shot)
    {
        skyRailShots.RemoveAll(item => item.Shot.Id == shot.Id);
        var shownUtc = DateTime.UtcNow;
        skyRailShots.Add((shot, shownUtc));
        if (shot.PlayerId == LocalSkyRailId && shot.TargetId >= 0)
            skyRailLocalHitAt[shot.TargetId] = shownUtc.AddSeconds(SkyRailFireballFlightSeconds);
        whiteboardTvDirty = true;
    }

    private void HandleSkyRailContribution(string senderId, string command)
    {
        var parts = command.Split('|');
        if (parts.Length == 4 && parts[0] == "P" &&
            int.TryParse(parts[2], out var bestRound) && int.TryParse(parts[3], out var bestScore))
        {
            AcceptSkyRailPersonalBest(senderId, parts[1], bestRound, bestScore);
            return;
        }
        if (parts.Length == 4 && parts[0] == "R" && parts[1] == skyRailSessionId &&
            int.TryParse(parts[2], out var readyRound) && readyRound == skyRailRound && parts[3] is "0" or "1")
            AcceptSkyRailReady(senderId, parts[3] == "1");
        else if (parts.Length == 7 && parts[0] == "F" &&
                 int.TryParse(parts[2], out var round) && int.TryParse(parts[3], out var wave) &&
                 skyRailSessionId is { } session &&
                 SkyRailRules.CurrentContribution(parts[1], round, wave, session, skyRailRound, skyRailWave) &&
                 int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) &&
                 int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            AcceptSkyRailShot(senderId, parts[4], x, y);
    }

    private void SendSkyRailState()
    {
        if (!IsLocalActivityHost || skyRailSessionId is null) return;
        UpdateAdditionalArcadePoints();
        var snapshot = new SkyRailSnapshot(skyRailSessionId, skyRailRevision, skyRailRound,
            skyRailPhase,
            skyRailPhaseEndsUtc == DateTime.MaxValue ? 0 :
                new DateTimeOffset(skyRailPhaseEndsUtc).ToUnixTimeMilliseconds(),
            skyRailWave,
            skyRailWaveStartsUtc == DateTime.MinValue ? 0 :
                new DateTimeOffset(skyRailWaveStartsUtc).ToUnixTimeMilliseconds(),
            skyRailReady.ToArray(), skyRailLocked.ToArray(), skyRailPlayers.Values.ToArray(),
            skyRailTargets.ToArray(), skyRailMap, skyRailBossAppeared, skyRailBossHealth,
            skyRailRockets.ToArray(), skyRailWaveCount, skyRailBossMode, skyRailBossMaxHealth,
            skyRailBossDefeatedUtc == DateTime.MinValue ? 0 : new DateTimeOffset(skyRailBossDefeatedUtc).ToUnixTimeMilliseconds(), skyRailWaveSeconds, skyRailEscapedRocketIds.ToArray(), skyRailCustomRules, skyRailMapChoice);
        skyRailLastSnapshotUtc = DateTime.UtcNow;
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot)));
        if (TrySendRemainingState("sky-rail", "S|" + encoded))
        {
            var rejected = GameRelay("sky-rail")?.SnapshotRejected == true;
            if (rejected && !skyRailHostSnapshotRejectedLogged)
                AepLog.Warning("[Air Force EX] Host snapshot rejected before sending; phase=[redacted], wave=[redacted], revision=[redacted].");
            skyRailHostSnapshotRejectedLogged = rejected;
            return;
        }
        _ = stream.SendArcadeSnapshotAsync(StreamClient.SkyRailCommandPrefix, "S|" + encoded,
            $"{skyRailSessionId}|{skyRailRound}|{skyRailPhase}|{skyRailWave}|{skyRailBossAppeared}|{skyRailBossDefeatedUtc.Ticks}");
    }

    private void ApplySkyRailCommand(string command)
    {
        if (command.StartsWith("X|", StringComparison.Ordinal))
        {
            if (skyRailSessionId is null || command[2..] == skyRailSessionId)
                ResetSkyRailLocal();
            return;
        }
        if (command.StartsWith("E|", StringComparison.Ordinal))
        {
            try
            {
                var shot = JsonSerializer.Deserialize<SkyRailShot>(
                    Encoding.UTF8.GetString(Convert.FromBase64String(command[2..])));
                if (shot is null || shot.SessionId != skyRailSessionId ||
                    shot.Round != skyRailRound || shot.Wave != 0 && shot.Wave != skyRailWave ||
                    shot.Revision != 0 && shot.Revision < skyRailRevision ||
                    !skyRailLocked.Contains(shot.PlayerId) || !skyRailShotIds.Add(shot.Id)) return;
                skyRailRevision = Math.Max(skyRailRevision, shot.Revision);
                ShowSkyRailShot(shot);
                if (shot.PlayerId == LocalSkyRailId && shot.TargetId >= 0)
                    skyRailHitKeys.Add($"{shot.PlayerId}|{shot.TargetId}");
                if (shot.TargetId == SkyRailBossTargetId)
                    skyRailBossHealth = Math.Min(skyRailBossHealth, shot.BossHealth);
                else if (shot.TargetId <= SkyRailRocketTargetBase)
                {
                    var rocketIndex = skyRailRockets.FindIndex(rocket => rocket.Id ==
                        SkyRailRocketTargetBase - shot.TargetId);
                    if (rocketIndex >= 0)
                        skyRailRockets[rocketIndex] = skyRailRockets[rocketIndex] with
                        {
                            DestroyAtUnixMs = shot.FiredUnixMs +
                                (long)(SkyRailFireballFlightSeconds * 1000d),
                        };
                }
                if (skyRailPlayers.TryGetValue(shot.PlayerId, out var player))
                    skyRailPlayers[shot.PlayerId] = player with
                        { Score = shot.Score, Combo = shot.Combo };
            }
            catch (Exception) { }
            return;
        }
        if (!command.StartsWith("S|", StringComparison.Ordinal)) return;
        try
        {
            var snapshot = JsonSerializer.Deserialize<SkyRailSnapshot>(
                Encoding.UTF8.GetString(Convert.FromBase64String(command[2..])));
            if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.SessionId) || snapshot.Round < 1 ||
                !Enum.IsDefined(snapshot.Phase) || snapshot.WaveCount is < 4 or > 12 || snapshot.BossMode is < 0 or > 2 ||
                snapshot.Wave < 0 || snapshot.Wave > snapshot.WaveCount || snapshot.WaveSeconds is not (10 or 15) ||
                snapshot.SessionId == skyRailSessionId && snapshot.Revision < skyRailRevision) return;
            if (snapshot.SessionId != skyRailSessionId || snapshot.Round != skyRailRound)
            {
                ResetSkyRailPersonalBestRound();
                skyRailShots.Clear();
                skyRailShotIds.Clear();
                skyRailHitKeys.Clear();
                skyRailLocalHitAt.Clear();
                skyRailLastLocalFireUtc = DateTime.MinValue;
            }
            skyRailSessionId = snapshot.SessionId;
            skyRailRevision = snapshot.Revision;
            skyRailRound = snapshot.Round;
            skyRailPhase = snapshot.Phase;
            skyRailPhaseEndsUtc = snapshot.PhaseEndsUnixMs == 0 ? DateTime.MaxValue :
                DateTimeOffset.FromUnixTimeMilliseconds(snapshot.PhaseEndsUnixMs).UtcDateTime;
            skyRailWave = snapshot.Wave;
            skyRailWaveCount = snapshot.WaveCount;
            skyRailWaveSeconds = snapshot.WaveSeconds;
            skyRailBossMode = snapshot.BossMode;
            skyRailMap = Math.Clamp(snapshot.Map, 0, SkyRailMapNames.Length - 1);
            skyRailCustomRules = snapshot.CustomRules;
            skyRailMapChoice = snapshot.CustomRules ? Math.Clamp(snapshot.MapChoice, -1, SkyRailMapNames.Length - 1) : -1;
            skyRailBossDefeatedUtc = snapshot.BossDefeatedUnixMs == 0 ? DateTime.MinValue :
                DateTimeOffset.FromUnixTimeMilliseconds(snapshot.BossDefeatedUnixMs).UtcDateTime;
            skyRailBossAppeared = snapshot.BossAppeared;
            skyRailBossMaxHealth = Math.Clamp(snapshot.BossMaxHealth, 50, 100000);
            skyRailBossHealth = Math.Clamp(snapshot.BossHealth, 0, skyRailBossMaxHealth);
            skyRailRockets.Clear();
            skyRailRockets.AddRange(snapshot.Rockets ?? []);
            skyRailEscapedRocketIds.Clear();
            skyRailEscapedRocketIds.AddRange(snapshot.EscapedRocketIds ?? []);
            skyRailWaveStartsUtc = snapshot.WaveStartsUnixMs == 0 ? DateTime.MinValue :
                DateTimeOffset.FromUnixTimeMilliseconds(snapshot.WaveStartsUnixMs).UtcDateTime;
            skyRailReady.Clear();
            skyRailReady.UnionWith(snapshot.ReadyIds ?? []);
            skyRailLocked.Clear();
            skyRailLocked.UnionWith(snapshot.LockedIds ?? []);
            skyRailPlayers.Clear();
            foreach (var player in snapshot.Players ?? [])
                skyRailPlayers[player.Id] = player;
            skyRailTargets.Clear();
            skyRailTargets.AddRange(snapshot.Targets ?? []);
            UpdateSkyRailLocalDamage(DateTime.UtcNow);
            UpdateAdditionalArcadePoints();
            ObserveArcadeScores();
            whiteboardTvDirty = true;
        }
        catch (Exception) { }
    }

    private void DrawSkyRailPage()
    {
        lastActivitiesPage = HomePage.SkyRail;
        if (!SkyRailActive)
        {
            if (stream.Mode == StreamMode.Viewing && ActivityFromCanvasUrl(stream.CurrentRoomState?.Url) == ActiveActivityKind.SkyRail)
                ImGui.TextWrapped(CurrentSession is null ? "Sign in to join Air Force EX." : "Waiting for the host's Air Force EX game state…");
            DrawSkyRailSetupPage();
            return;
        }
        var available = ImGui.GetContentRegionAvail();
        var width = MathF.Min(available.X,
            MathF.Max(Ui(260f), (available.Y - Ui(90f)) * 16f / 9f));
        var size = new Vector2(width, width * 9f / 16f);
        var origin = ImGui.GetCursorScreenPos() +
            new Vector2(MathF.Max(0, (available.X - width) * 0.5f), 0);
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##skyRailPreview", size);
        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            var uv = (ImGui.GetIO().MousePos - origin) / size;
            SetSkyRailPageHover(uv);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                SubmitSkyRailTvControlAt(uv);
            if (IsSkyRailTvControlAt(uv)) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        else SetSkyRailPageHover(null);
        UpdateSkyRailVolumePointer((ImGui.GetIO().MousePos - origin) / size,
            ImGui.IsMouseDown(ImGuiMouseButton.Left),
            hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left), ref skyRailPageVolumeDragging);
        var draw = ImGui.GetWindowDrawList();
        if (skyRailPageRenderer?.PreviewHandle is { } handle && handle != nint.Zero)
            draw.AddImage(new ImTextureID(handle), origin, origin + size);
        else
            draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(CardBg));
        ImGui.SetCursorScreenPos(origin + new Vector2(0, size.Y + Ui(12f)));
        ImGui.PushTextWrapPos();
        ImGui.TextColored(MutedText, skyRailPhase switch
        {
            SkyRailPhase.Lobby => $"{skyRailReady.Count} ready. Host starts when everyone is set.",
            SkyRailPhase.Countdown => $"Locked in. Starting in {SkyRailSecondsLeft} seconds.",
            SkyRailPhase.Playing => SkyRailBossRetreating(DateTime.UtcNow) ? "The boss is retreating. Prepare for the next wave." : SkyRailRepairing(DateTime.UtcNow) ? "Airship systems damaged. Repairing.. Weapons temporarily offline." : SkyRailCanFire ? skyRailBossHealth > 0
                ? $"Boss encounter: {SkyRailBossSecondsLeft} seconds left. Shoot the boss and intercept its rockets."
                : "Click blue, silver and gold targets to score; avoid red targets." :
                "Spectating this round. You can join the next round.",
            SkyRailPhase.Results => "Flight complete. Results stay here until the host chooses to play again or ends the game.",
            _ => string.Empty,
        });
        ImGui.PopTextWrapPos();
        if (IsLocalActivityHost)
        {
            if (skyRailPhase == SkyRailPhase.Results)
            {
                if (ImGui.Button("Play again##skyRailReplay")) BeginNextSkyRailLobby();
                ImGui.SameLine();
            }
            if (ImGui.Button("End Air Force EX##skyRailEnd")) StopSkyRailGame();
        }
    }

    internal bool IsSkyRailTvControlAt(Vector2 uv)
    {
        if (!SkyRailActive || uv.X is < 0 or > 1 || uv.Y is < 0 or > 1) return false;
        var x = uv.X * WhiteboardRenderer.Width;
        var y = uv.Y * WhiteboardRenderer.Height;
        if (TryGetSkyRailSettingsTarget(x, y, out _)) return true;
        if (skyRailSettingsOpen) return false;
        if (skyRailPhase == SkyRailPhase.Results)
            return IsLocalActivityHost && x is >= 460 and <= 820 && y is >= 495 and <= 555;
        if (skyRailPhase == SkyRailPhase.Lobby)
            return (IsLocalActivityHost && skyRailCustomRules && y is >= 310 and < 354 &&
                    (x is >= 410 and < 454 || x is >= 826 and < 870)) ||
                (x is >= 460 and <= 820 &&
                 (y is >= 420 and <= 480 ||
                  y is >= 495 and <= 555 && IsLocalActivityHost &&
                  skyRailReady.Count > 0));
        return SkyRailCanFire && x is >= 80 and <= 1195 && y is >= 130 and <= 565;
    }

    internal void SetSkyRailTvHover(Vector2? uv)
    {
        skyRailTvHover = uv;
    }

    private void SetSkyRailPageHover(Vector2? uv)
    {
        skyRailPageHover = uv;
    }

    internal void SubmitSkyRailTvControlAt(Vector2 uv)
    {
        if (!IsSkyRailTvControlAt(uv)) return;
        var x = uv.X * WhiteboardRenderer.Width;
        var y = uv.Y * WhiteboardRenderer.Height;
        if (TryGetSkyRailSettingsTarget(x, y, out var target))
        {
            HandleSkyRailSettingsTarget(target, uv);
            return;
        }
        if (skyRailPhase == SkyRailPhase.Results) BeginNextSkyRailLobby();
        else if (skyRailPhase == SkyRailPhase.Lobby)
        {
            if (y is >= 310 and < 354) ChangeSkyRailLobbyMap(x < 640 ? -1 : 1);
            else if (y <= 480) SkyRailReadyUp();
            else StartSkyRailCountdown();
        }
        else FireSkyRail(uv);
    }
}

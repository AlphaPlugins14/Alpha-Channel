using System.Text;
using System.Text.Json;
using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private enum ChocoRacePhase { Inactive, WaitingForBet, Betting, Racing, Results }
    private enum ChocoRaceWeather { Dry, Wet, Rainstorms }
    private enum ChocoRaceTerrain { Smooth, Rough, Hazardous }

    private sealed record ChocoRacer(string Name, int ColourIndex, double Speed, double Odds,
        int PaceSeed, double FinishSeconds, bool PrefersWetWeather = false);
    private sealed record ChocoRacePlayer(string Id, string Name, int Chips);
    private sealed record ChocoRaceBet(string PlayerId, int Lane, int Amount);
    private sealed record ChocoRaceLedgerBet(string PlayerId, int Round, int Lane, string ChocoName,
        int Amount, int Position, int Return);
    private sealed record ChocoRaceObstacle(int Lane, bool IsPuddle, double CoursePosition,
        double HitSeconds, double SlowSeconds, double SpeedMultiplier, bool Dodged,
        bool IsBoost = false, bool IsSlowTile = false, bool IsChest = false, bool IsMeteor = false);
    private sealed record ChocoRacePayout(string PlayerId, string Name, int Stake, int Return);
    private sealed record ChocoRaceSnapshot(
        string SessionId, string WhiteboardSessionId, int Revision, int Round,
        ChocoRacePhase Phase, long PhaseEndsUnixMs, long RaceStartedUnixMs,
        long RoundStartedUnixMs, int WinnerLane,
        double CourseDistance,
        ChocoRacer[] Racers, ChocoRaceObstacle[] Obstacles, ChocoRacePlayer[] Players, ChocoRaceBet[] Bets,
        ChocoRaceLedgerBet[] LedgerBets, ChocoRacePayout[] Payouts, string[] RefilledPlayerIds,
        ChocoRaceWeather Weather, ChocoRaceTerrain Terrain, long HostSentUnixMs,
        int BettingSeconds = ChocoRaceBettingSeconds, int StartingChips = ChocoRaceStartingChips,
        bool RefillEnabled = true, bool TopThreePays = false);

    private const int ChocoRaceStartingChips = 1000;
    private const int ChocoRaceBettingSeconds = 12;
    private const int ChocoRaceReadyBettingSeconds = 5;
    private const int ChocoRaceResultsSeconds = 11;
    private const double ChocoRaceIntroSeconds = 5d;
    private const double ChocoRaceTargetWinnerSeconds = 26d;
    private const double ChocoRacePaceIntervalSeconds = 1.25d;
    private const double ChocoRaceFinishHoldSeconds = 2.5d;
    private const double ChocoRaceTierSpeedInfluence = 0.18d;
    private const double ChocoRaceHiddenSpeedVariation = 0.3d;
    private const double ChocoRaceObstacleJumpChance = 0.35d;
    private static readonly double[] ChocoRacePuddleChanceByWeather = [0.05d, 0.30d, 0.45d];
    private static readonly double[] ChocoRaceRockChanceByTerrain = [0.05d, 0.30d, 0.45d];
    private const double ChocoRaceBoostChancePerLane = 0.20d;
    private const double ChocoRaceBoostDurationSeconds = 2d;
    private const double ChocoRaceBoostSpeedMultiplier = 1.35d;
    private const double ChocoRaceSlowTileChancePerLane = 0.20d;
    private const double ChocoRaceSlowTileDurationSeconds = 2d;
    private const double ChocoRaceSlowTileSpeedMultiplier = 0.65d;
    private const double ChocoRacePuddleSpeedMultiplier = 0.65d;
    private const double ChocoRaceWetPuddleSlowdownReduction = 0.10d;
    private const double ChocoRaceWetPuddleSpeedMultiplier =
        1d - (1d - ChocoRacePuddleSpeedMultiplier) * (1d - ChocoRaceWetPuddleSlowdownReduction);
    private const double ChocoRaceTreasureChancePerRace = 0.35d;
    private const double ChocoRaceMeteorFallSeconds = 0.66d;
    private const double ChocoRaceMeteorDurationSeconds = 3d;
    private const double ChocoRaceMeteorSpeedMultiplier = 0.4d;
    // Displayed x-to-1 odds pay x times the stake as profit, plus the original stake.
    private static readonly (int Min, int Max)[] ChocoRaceOddsBySpeedTier =
    [
        (171, 200), (146, 170), (121, 145), (106, 120), (86, 105),
        (61, 85), (46, 60), (36, 45), (26, 35), (15, 25),
    ];
    private static readonly int[] ChocoRaceChipValues = [10, 50, 100, 500, 1000];
    private static readonly string[] ChocoRaceNames =
    [
        "Goldie", "Sky", "Berry", "Snow", "Plum", "Shadow", "Waffle", "Turbo",
        "Biscuit", "Comet", "Pecks", "Mochi", "Nugget", "Pip", "Sunny", "Sprout",
        "Doodle", "Marshmallow", "Rocket", "Buttons", "Fizz", "Pebble", "Pudding", "Zippy",
    ];

    private string? chocoRaceSessionId;
    private string? chocoRaceWhiteboardSessionId;
    private ChocoRacePhase chocoRacePhase;
    private DateTime chocoRacePhaseEndsUtc = DateTime.MaxValue;
    private DateTime chocoRaceStartedUtc = DateTime.MinValue;
    private DateTime chocoRaceRoundStartedUtc = DateTime.MinValue;
    private DateTime chocoRaceLastFrameUtc = DateTime.MinValue;
    private TimeSpan chocoRaceHostClockOffset;
    private int chocoRaceRevision;
    private readonly HashSet<string> chocoRaceRetiredSessionIds = new(StringComparer.Ordinal);
    private readonly Queue<string> chocoRaceRetiredSessionOrder = new();
    private int chocoRaceRound = 1;
    private int chocoRaceBettingSeconds = ChocoRaceBettingSeconds;
    private int chocoRaceStartingChips = ChocoRaceStartingChips;
    private bool chocoRaceRefillEnabled = true;
    private bool chocoRaceTopThreePays;
    private int chocoRaceCustomBettingSeconds = ChocoRaceBettingSeconds;
    private int chocoRaceCustomStartingChips = ChocoRaceStartingChips;
    private bool chocoRaceCustomRefillEnabled = true;
    private bool chocoRaceCustomTopThreePays;
    private int pendingChocoRaceBettingSeconds = ChocoRaceBettingSeconds;
    private int pendingChocoRaceStartingChips = ChocoRaceStartingChips;
    private bool pendingChocoRaceRefillEnabled = true;
    private bool pendingChocoRaceTopThreePays;
    private ChocoRaceWeather chocoRaceWeather;
    private ChocoRaceTerrain chocoRaceTerrain;
    private int chocoRaceWinnerLane = -1;
    private double chocoRaceCourseDistance = 40d;
    private int chocoRaceSelectedChip;
    private DateTime chocoRaceNotEnoughChipsUntilUtc = DateTime.MinValue;
    private readonly int[] chocoRaceDraftByLane = new int[6];
    private string? chocoRacePendingBetId;
    private DateTime chocoRacePendingBetSentUtc;
    private int chocoRaceSelectedLane = -1;
    private int chocoRaceHoveredLane = -1;
    private int chocoRaceHoveredBetLane = -1;
    private int chocoRaceHoveredControlTarget = -1;
    private int chocoRaceTvHoveredTarget = -1;
    private int chocoRacePageHoveredTarget = -1;
    private int chocoRaceLastClickedTarget = -1;
    private DateTime chocoRaceLastClickUtc = DateTime.MinValue;
    private string? chocoRaceLastEmoteSessionId;
    private int chocoRaceLastEmoteRound;
    private bool chocoRaceDirty;
    private WhiteboardRenderer? chocoRacePageRenderer;
    private ChocoRacer[] chocoRaceRacers = [];
    private readonly List<ChocoRaceObstacle> chocoRaceObstacles = [];
    private readonly Dictionary<string, ChocoRacePlayer> chocoRacePlayers = new(StringComparer.Ordinal);
    private readonly List<ChocoRaceBet> chocoRaceBets = [];
    private readonly List<ChocoRaceLedgerBet> chocoRaceLedgerBets = [];
    private readonly List<ChocoRacePayout> chocoRacePayouts = [];
    private readonly HashSet<string> chocoRaceRefilledPlayerIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> chocoRaceHandledBetIds = new(StringComparer.Ordinal);
    private int ChocoRaceResultDurationSeconds =>
        Math.Max(ChocoRaceResultsSeconds, 2 * (chocoRacePayouts.Count + 1) - 1);

    private bool ChocoRaceActive => racePreviewActive && chocoRaceSessionId is not null;
    internal bool IsChocoRaceActive => ChocoRaceActive;
    private DateTime ChocoRaceNowUtc => DateTime.UtcNow + chocoRaceHostClockOffset;
    private bool ChocoRaceBetsOpen => chocoRacePhase == ChocoRacePhase.WaitingForBet ||
        chocoRacePhase == ChocoRacePhase.Betting && ChocoRaceNowUtc < chocoRacePhaseEndsUtc;
    // Keep the TV input routed to Choco RaceBets during the splash so clicks
    // cannot fall through to another activity or the game world.
    internal bool ChocoRaceTvControlsInteractive => ChocoRaceActive;
    private string LocalChocoRaceUserId => CurrentSession?.AccountId ?? stream.LocalUserId ?? "local";

    private void StartChocoRace()
    {
        if (!IsLocalActivityHost || CurrentSession is null || ChocoRaceActive ||
            !CanLaunchActivity(ActiveActivityKind.ChocoRaceBets))
            return;

        chocoRaceBettingSeconds = Math.Clamp(pendingChocoRaceBettingSeconds, 5, 60);
        chocoRaceStartingChips = Math.Clamp(pendingChocoRaceStartingChips, 100, 10000);
        chocoRaceRefillEnabled = pendingChocoRaceRefillEnabled;
        chocoRaceTopThreePays = pendingChocoRaceTopThreePays;

        chocoRaceSessionId = Guid.NewGuid().ToString("N");
        chocoRacePhase = ChocoRacePhase.WaitingForBet;
        chocoRacePhaseEndsUtc = DateTime.MaxValue;
        chocoRaceStartedUtc = DateTime.MinValue;
        chocoRaceRoundStartedUtc = DateTime.UtcNow;
        chocoRaceRevision = 1;
        chocoRaceRound = 1;
        chocoRaceWinnerLane = -1;
        chocoRaceCourseDistance = 40d;
        ResetChocoRaceDraft();
        chocoRaceSelectedLane = -1;
        chocoRaceTvHoveredTarget = -1;
        chocoRacePageHoveredTarget = -1;
        chocoRaceHoveredControlTarget = -1;
        chocoRaceLastClickedTarget = -1;
        chocoRaceLastEmoteSessionId = null;
        chocoRaceLastEmoteRound = 0;
        chocoRaceResultsWinnersPage = 0;
        ResetChocoRaceSettingsUi();
        chocoRaceHelpOpen = false;
        ResetChocoRaceAudio();
        ResetChocoRaceIntroState();
        chocoRacePlayers.Clear();
        chocoRaceBets.Clear();
        chocoRaceObstacles.Clear();
        chocoRaceLedgerBets.Clear();
        chocoRacePayouts.Clear();
        chocoRaceRefilledPlayerIds.Clear();
        chocoRaceHandledBetIds.Clear();
        ResetChocoRaceScoreboard();
        PrepareChocoRaceRacers();
        PrepareChocoRaceConditions();
        chocoRacePlayers[LocalChocoRaceUserId] =
            new ChocoRacePlayer(LocalChocoRaceUserId, LocalPictionaryDisplayName(), chocoRaceStartingChips);
        AddChocoRaceRosterPlayers();

        StartHostWhiteboard(WhiteboardMode.Solo, ActiveActivityKind.ChocoRaceBets);
        chocoRaceWhiteboardSessionId = whiteboardSessionId;
        racePreviewActive = true;
        chocoRaceDirty = true;
        whiteboardTvDirty = true;
        SendChocoRaceState();
    }

    private void StopChocoRace()
    {
        if (IsLocalActivityHost && chocoRaceSessionId is not null)
            _ = stream.SendChocoRaceCommandAsync("X|" + chocoRaceSessionId);
        if (IsLocalActivityHost && chocoRaceWhiteboardSessionId is not null &&
            string.Equals(chocoRaceWhiteboardSessionId, whiteboardSessionId, StringComparison.Ordinal))
            SendAndApplyWhiteboardCommand($"X|{whiteboardSessionId}");
        ResetChocoRaceLocal();
    }

    private void ResetChocoRaceLocal(bool preserveRelay = false)
    {
        CaptureArcadeScoresBeforeReset("choco-race");
        if (!preserveRelay) DisposeActivityRelay("choco-race");
        if (chocoRaceSessionId is { } oldSessionId &&
            chocoRaceRetiredSessionIds.Add(oldSessionId))
        {
            chocoRaceRetiredSessionOrder.Enqueue(oldSessionId);
            if (chocoRaceRetiredSessionOrder.Count > 16)
                chocoRaceRetiredSessionIds.Remove(chocoRaceRetiredSessionOrder.Dequeue());
        }
        racePreviewActive = false;
        chocoRaceSessionId = null;
        chocoRaceWhiteboardSessionId = null;
        chocoRacePhase = ChocoRacePhase.Inactive;
        chocoRacePhaseEndsUtc = DateTime.MaxValue;
        chocoRaceStartedUtc = DateTime.MinValue;
        chocoRaceRoundStartedUtc = DateTime.MinValue;
        chocoRaceWinnerLane = -1;
        chocoRaceCourseDistance = 40d;
        chocoRaceBettingSeconds = ChocoRaceBettingSeconds;
        chocoRaceStartingChips = ChocoRaceStartingChips;
        chocoRaceRefillEnabled = true;
        chocoRaceTopThreePays = false;
        chocoRaceRevision = 0;
        chocoRaceHostClockOffset = TimeSpan.Zero;
        chocoRaceWeather = default;
        chocoRaceTerrain = default;
        chocoRaceSelectedLane = -1;
        chocoRaceHoveredLane = -1;
        chocoRaceHoveredBetLane = -1;
        chocoRaceTvHoveredTarget = -1;
        chocoRacePageHoveredTarget = -1;
        chocoRaceHoveredControlTarget = -1;
        chocoRaceLastClickedTarget = -1;
        chocoRaceLastEmoteSessionId = null;
        chocoRaceLastEmoteRound = 0;
        chocoRaceResultsWinnersPage = 0;
        ResetChocoRaceDraft();
        ResetChocoRaceSettingsUi();
        chocoRaceHelpOpen = false;
        ResetChocoRaceAudio();
        ResetChocoRaceIntroState();
        chocoRaceRacers = [];
        chocoRacePlayers.Clear();
        chocoRaceBets.Clear();
        chocoRaceObstacles.Clear();
        chocoRaceLedgerBets.Clear();
        chocoRacePayouts.Clear();
        chocoRaceRefilledPlayerIds.Clear();
        chocoRaceHandledBetIds.Clear();
        ResetChocoRaceScoreboard();
        DisposeChocoRacePageRenderer();
        chocoRaceDirty = true;
    }

    private void PrepareChocoRaceRacers()
    {
        var names = ChocoRaceNames.OrderBy(_ => Random.Shared.Next()).Take(6).ToArray();
        var colours = Enumerable.Range(0, 6).OrderBy(_ => Random.Shared.Next()).ToArray();
        var speedTiers = Enumerable.Range(0, ChocoRaceOddsBySpeedTier.Length)
            .OrderBy(_ => Random.Shared.Next()).Take(6).ToArray();
        chocoRaceRacers = new ChocoRacer[6];
        var oddsTenths = new int[chocoRaceRacers.Length];
        for (var lane = 0; lane < 6; lane++)
        {
            var tier = speedTiers[lane];
            var (minOdds, maxOdds) = ChocoRaceOddsBySpeedTier[tier];
            var speed = (7 + tier * 2) / 10d;
            oddsTenths[lane] = Random.Shared.Next(minOdds, maxOdds + 1);
            chocoRaceRacers[lane] = new ChocoRacer(names[lane], colours[lane], speed, oddsTenths[lane] / 10d,
                0, 0d, Random.Shared.Next(2) == 0);
        }

        // Keep the relative odds spread while ensuring a bet on every lane cannot
        // guarantee a profit solely because every payout is above 5:1.
        var reductionTenths = Math.Max(0, oddsTenths.Min() - 50);
        if (reductionTenths > 0)
            for (var lane = 0; lane < chocoRaceRacers.Length; lane++)
                chocoRaceRacers[lane] = chocoRaceRacers[lane] with
                {
                    Odds = (oddsTenths[lane] - reductionTenths) / 10d,
                };
    }

    private void PrepareChocoRaceConditions()
    {
        chocoRaceWeather = (ChocoRaceWeather)Random.Shared.Next(3);
        chocoRaceTerrain = (ChocoRaceTerrain)Random.Shared.Next(3);
    }

    private bool AddChocoRaceRosterPlayers()
    {
        var changed = false;
        foreach (var participant in stream.Roster)
        {
            if (chocoRacePlayers.ContainsKey(participant.UserId)) continue;
            chocoRacePlayers[participant.UserId] = new ChocoRacePlayer(
                participant.UserId,
                string.IsNullOrWhiteSpace(participant.DisplayName) ? "Player" : participant.DisplayName,
                chocoRaceStartingChips);
            changed = true;
        }
        return changed;
    }

    private bool TryShortenChocoRaceCountdown()
    {
        if (!IsLocalActivityHost || chocoRacePhase != ChocoRacePhase.Betting ||
            !chocoRaceBets.Any(bet => bet.PlayerId == LocalChocoRaceUserId) ||
            stream.Roster.Any(player => !chocoRaceBets.Any(bet => bet.PlayerId == player.UserId)))
            return false;

        var fiveSecondsFromNow = DateTime.UtcNow.AddSeconds(ChocoRaceReadyBettingSeconds);
        if (chocoRacePhaseEndsUtc <= fiveSecondsFromNow)
            return false;

        chocoRacePhaseEndsUtc = fiveSecondsFromNow;
        chocoRaceDirty = true;
        return true;
    }

    private void SendChocoRaceState()
    {
        if (!IsLocalActivityHost || chocoRaceSessionId is null ||
            chocoRaceWhiteboardSessionId is null) return;
        var snapshot = new ChocoRaceSnapshot(
            chocoRaceSessionId, chocoRaceWhiteboardSessionId, chocoRaceRevision, chocoRaceRound,
            chocoRacePhase,
            chocoRacePhaseEndsUtc == DateTime.MaxValue ? 0 : new DateTimeOffset(chocoRacePhaseEndsUtc).ToUnixTimeMilliseconds(),
            chocoRaceStartedUtc == DateTime.MinValue ? 0 : new DateTimeOffset(chocoRaceStartedUtc).ToUnixTimeMilliseconds(),
            chocoRaceRoundStartedUtc == DateTime.MinValue ? 0 : new DateTimeOffset(chocoRaceRoundStartedUtc).ToUnixTimeMilliseconds(),
            chocoRaceWinnerLane, chocoRaceCourseDistance, chocoRaceRacers, chocoRaceObstacles.ToArray(),
            chocoRacePlayers.Values.ToArray(),
            chocoRaceBets.ToArray(), chocoRaceLedgerBets.ToArray(),
            chocoRacePayouts.ToArray(), chocoRaceRefilledPlayerIds.ToArray(),
            chocoRaceWeather, chocoRaceTerrain,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), chocoRaceBettingSeconds,
            chocoRaceStartingChips, chocoRaceRefillEnabled, chocoRaceTopThreePays);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot)));
        if (TrySendActivityThroughCloudflare("choco-race", "S|" + encoded)) return;
        _ = stream.SendArcadeSnapshotAsync(StreamClient.ChocoRaceCommandPrefix, "S|" + encoded,
            $"{chocoRaceSessionId}|{chocoRaceRound}|{chocoRacePhase}|{chocoRaceStartedUtc.Ticks}");
    }

    private void ApplyChocoRaceCommand(string command)
    {
        if (command.StartsWith("X|", StringComparison.Ordinal))
        {
            if (command[2..] == chocoRaceSessionId) ResetChocoRaceLocal();
            return;
        }
        if (!command.StartsWith("S|", StringComparison.Ordinal)) return;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(command[2..]));
            var snapshot = JsonSerializer.Deserialize<ChocoRaceSnapshot>(json);
            if (snapshot is null || snapshot.Racers.Length != 6 ||
                chocoRaceRetiredSessionIds.Contains(snapshot.SessionId) ||
                snapshot.SessionId == chocoRaceSessionId && snapshot.Revision <= chocoRaceRevision) return;
            var newSession = snapshot.SessionId != chocoRaceSessionId;
            var newRound = !newSession && snapshot.Round != chocoRaceRound;
            if (newSession) ResetChocoRaceLocal(preserveRelay: true);
            if (snapshot.HostSentUnixMs > 0)
                chocoRaceHostClockOffset =
                    DateTimeOffset.FromUnixTimeMilliseconds(snapshot.HostSentUnixMs).UtcDateTime -
                    DateTime.UtcNow;
            if (newRound)
            {
                chocoRaceSelectedLane = -1;
                ResetChocoRaceDraft();
                chocoRaceResultsWinnersPage = 0;
                chocoRaceStartPlayedThisRound = false;
                chocoRaceMeteorPlayedThisRound = false;
                chocoRaceFanfarePlayedThisRound = false;
            }
            chocoRaceSessionId = snapshot.SessionId;
            chocoRaceWhiteboardSessionId = snapshot.WhiteboardSessionId;
            chocoRaceRevision = snapshot.Revision;
            chocoRaceRound = snapshot.Round;
            chocoRaceWeather = snapshot.Weather;
            chocoRaceTerrain = snapshot.Terrain;
            chocoRaceBettingSeconds = Math.Clamp(snapshot.BettingSeconds, 5, 60);
            chocoRaceStartingChips = Math.Clamp(snapshot.StartingChips, 100, 10000);
            chocoRaceRefillEnabled = snapshot.RefillEnabled;
            chocoRaceTopThreePays = snapshot.TopThreePays;
            chocoRacePhase = snapshot.Phase;
            if (snapshot.Phase is ChocoRacePhase.Racing or ChocoRacePhase.Results)
                chocoRaceSelectedLane = -1;
            chocoRacePhaseEndsUtc = snapshot.PhaseEndsUnixMs == 0 ? DateTime.MaxValue :
                DateTimeOffset.FromUnixTimeMilliseconds(snapshot.PhaseEndsUnixMs).UtcDateTime;
            chocoRaceStartedUtc = snapshot.RaceStartedUnixMs == 0 ? DateTime.MinValue :
                DateTimeOffset.FromUnixTimeMilliseconds(snapshot.RaceStartedUnixMs).UtcDateTime;
            chocoRaceRoundStartedUtc = snapshot.RoundStartedUnixMs == 0 ? DateTime.MinValue :
                DateTimeOffset.FromUnixTimeMilliseconds(snapshot.RoundStartedUnixMs).UtcDateTime;
            chocoRaceWinnerLane = snapshot.WinnerLane;
            chocoRaceCourseDistance = snapshot.CourseDistance > 0d ? snapshot.CourseDistance : 40d;
            chocoRaceRacers = snapshot.Racers;
            chocoRaceObstacles.Clear(); chocoRaceObstacles.AddRange(snapshot.Obstacles ?? []);
            chocoRacePlayers.Clear();
            foreach (var player in snapshot.Players) chocoRacePlayers[player.Id] = player;
            chocoRaceBets.Clear(); chocoRaceBets.AddRange(snapshot.Bets);
            if (chocoRaceBets.Any(bet => bet.PlayerId == LocalChocoRaceUserId) ||
                snapshot.Phase is ChocoRacePhase.Racing or ChocoRacePhase.Results)
                ResetChocoRaceDraft();
            chocoRaceLedgerBets.Clear(); chocoRaceLedgerBets.AddRange(snapshot.LedgerBets ?? []);
            chocoRacePayouts.Clear(); chocoRacePayouts.AddRange(snapshot.Payouts);
            chocoRaceRefilledPlayerIds.Clear();
            foreach (var id in snapshot.RefilledPlayerIds) chocoRaceRefilledPlayerIds.Add(id);
            AwardChocoRaceActivityPoints();
            racePreviewActive = true;
            chocoRaceDirty = true;
            whiteboardTvDirty = true;
            TryPlayChocoRaceResultEmote();
        }
        catch (Exception)
        {
            AepLog.Warning("[ChocoRace] Ignored invalid state.");
        }
    }

    private void HandleChocoRaceContribution(string senderId, string command)
    {
        if (!IsLocalActivityHost || chocoRaceSessionId is null ||
            !stream.Roster.Any(player => player.UserId == senderId)) return;
        var parts = command.Split('|');
        if (parts.Length != 5 || parts[0] != "B" || parts[1] != chocoRaceSessionId ||
            !int.TryParse(parts[2], out var round) || round != chocoRaceRound ||
            !Guid.TryParseExact(parts[3], "N", out _)) return;
        var spread = new int[6];
        var entries = parts[4].Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length is < 1 or > 6) return;
        foreach (var entry in entries)
        {
            var pair = entry.Split(':');
            if (pair.Length != 2 || !int.TryParse(pair[0], out var lane) || lane is < 0 or >= 6 ||
                !int.TryParse(pair[1], out var amount) || amount < 10 || amount % 10 != 0 ||
                spread[lane] != 0) return;
            spread[lane] = amount;
        }
        HostAcceptChocoRaceBet(senderId, spread, parts[3]);
    }

    private void SubmitChocoRaceBet()
    {
        if (chocoRaceSessionId is null || !ChocoRaceCanPlaceDraft ||
            !chocoRacePlayers.TryGetValue(LocalChocoRaceUserId, out var player) ||
            player.Chips < ChocoRaceDraftTotal) return;
        var betId = Guid.NewGuid().ToString("N");
        if (IsLocalActivityHost)
            HostAcceptChocoRaceBet(LocalChocoRaceUserId, chocoRaceDraftByLane, betId);
        else if (stream.Mode == StreamMode.Viewing)
        {
            chocoRacePendingBetId = betId;
            chocoRacePendingBetSentUtc = DateTime.UtcNow;
            if (!SendArcadePlayerAction("choco-race", ChocoRaceDraftPayload(betId))) chocoRacePendingBetId = null;
        }
        chocoRaceDirty = true;
    }

    private void HostAcceptChocoRaceBet(string playerId, int[] spread, string betId)
    {
        var total = spread.Sum(amount => (long)amount);
        if (!IsLocalActivityHost || chocoRaceSessionId is null ||
            !ChocoRaceBetsOpen ||
            spread.Length != 6 || total <= 0 || total > int.MaxValue ||
            spread.Any(amount => amount < 0 || amount % 10 != 0) ||
            !chocoRacePlayers.TryGetValue(playerId, out var player) || player.Chips < total ||
            chocoRaceBets.Any(bet => bet.PlayerId == playerId) ||
            !chocoRaceHandledBetIds.Add(betId)) return;
        chocoRacePlayers[playerId] = player with { Chips = player.Chips - (int)total };
        for (var lane = 0; lane < 6; lane++)
        {
            var amount = spread[lane];
            if (amount == 0) continue;
            chocoRaceBets.Add(new ChocoRaceBet(playerId, lane, amount));
            chocoRaceLedgerBets.Add(new ChocoRaceLedgerBet(playerId, chocoRaceRound, lane,
                chocoRaceRacers[lane].Name, amount, 0, 0));
        }
        var playerHistory = chocoRaceLedgerBets.FindAll(bet => bet.PlayerId == playerId);
        foreach (var oldBet in playerHistory.Take(Math.Max(0, playerHistory.Count - 10)))
            chocoRaceLedgerBets.Remove(oldBet);
        if (playerId == LocalChocoRaceUserId) ResetChocoRaceDraft();
        if (chocoRacePhase == ChocoRacePhase.WaitingForBet)
        {
            chocoRacePhase = ChocoRacePhase.Betting;
            chocoRacePhaseEndsUtc = DateTime.UtcNow.AddSeconds(chocoRaceBettingSeconds);
        }
        TryShortenChocoRaceCountdown();
        chocoRaceRevision++;
        chocoRaceDirty = true;
        SendChocoRaceState();
    }

    private void BeginChocoRace()
    {
        if (chocoRaceBets.Count == 0)
        {
            chocoRacePhase = ChocoRacePhase.WaitingForBet;
            chocoRacePhaseEndsUtc = DateTime.MaxValue;
            return;
        }
        chocoRacePhase = ChocoRacePhase.Racing;
        chocoRaceSelectedLane = -1;
        chocoRaceStartedUtc = DateTime.UtcNow;
        // Do not reveal the random pace or race-day form before bets close.
        for (var lane = 0; lane < chocoRaceRacers.Length; lane++)
            chocoRaceRacers[lane] = chocoRaceRacers[lane] with { PaceSeed = Random.Shared.Next() };
        var fastestNominalSpeed = chocoRaceRacers.Max(racer =>
            ChocoRaceEffectiveSpeed(racer) * ChocoRaceFormFactor(racer.PaceSeed));
        chocoRaceCourseDistance = fastestNominalSpeed * ChocoRaceTargetWinnerSeconds;
        chocoRaceObstacles.Clear();
        for (var lane = 0; lane < chocoRaceRacers.Length; lane++)
        {
            var trackEvents = new List<(double Position, bool IsPuddle, bool IsBoost, bool IsSlowTile, bool Dodged)>();
            double PickTrackPosition(double min, double max)
            {
                var position = min + Random.Shared.NextDouble() * (max - min);
                for (var attempt = 0; attempt < 16 &&
                     trackEvents.Any(trackEvent => Math.Abs(trackEvent.Position - position) < 0.035d); attempt++)
                    position = min + Random.Shared.NextDouble() * (max - min);
                return position;
            }
            for (var chance = 0; chance < 3; chance++)
            {
                var puddleChance = ChocoRacePuddleChanceByWeather[(int)chocoRaceWeather];
                var rockChance = ChocoRaceRockChanceByTerrain[(int)chocoRaceTerrain];
                var puddlePassed = Random.Shared.NextDouble() < puddleChance;
                var rockPassed = Random.Shared.NextDouble() < rockChance;
                if (!puddlePassed && !rockPassed) continue;
                // Three opportunities spread across the longer course.
                var position = chance switch
                {
                    0 => PickTrackPosition(0.20d, 0.36d),
                    1 => PickTrackPosition(0.42d, 0.58d),
                    _ => PickTrackPosition(0.66d, 0.82d),
                };
                var isPuddle = puddlePassed && (!rockPassed || Random.Shared.Next(2) == 0);
                var dodged = Random.Shared.NextDouble() < ChocoRaceObstacleJumpChance;
                trackEvents.Add((position, isPuddle, false, false, dodged));
            }
            if (Random.Shared.NextDouble() < ChocoRaceBoostChancePerLane)
                trackEvents.Add((PickTrackPosition(0.35d, 0.49d), false, true, false, false));
            if (Random.Shared.NextDouble() < ChocoRaceBoostChancePerLane)
                trackEvents.Add((PickTrackPosition(0.77d, 0.91d), false, true, false, false));
            if (Random.Shared.NextDouble() < ChocoRaceSlowTileChancePerLane)
                trackEvents.Add((PickTrackPosition(0.08d, 0.18d), false, false, true, false));
            if (Random.Shared.NextDouble() < ChocoRaceSlowTileChancePerLane)
                trackEvents.Add((PickTrackPosition(0.53d, 0.67d), false, false, true, false));
            // Crossing times include every earlier slow or boost on this lane.
            foreach (var trackEvent in trackEvents.OrderBy(trackEvent => trackEvent.Position))
            {
                var hitSeconds = CalculateChocoRaceTimeForDistance(chocoRaceRacers[lane],
                    chocoRaceCourseDistance * trackEvent.Position, ChocoRaceObstaclesForLane(lane));
                chocoRaceObstacles.Add(new ChocoRaceObstacle(lane, trackEvent.IsPuddle,
                    trackEvent.Position, hitSeconds,
                    trackEvent.IsBoost ? ChocoRaceBoostDurationSeconds :
                        trackEvent.IsSlowTile ? ChocoRaceSlowTileDurationSeconds : trackEvent.IsPuddle ? 3d : 2.5d,
                    trackEvent.IsBoost ? ChocoRaceBoostSpeedMultiplier :
                        trackEvent.IsSlowTile ? ChocoRaceSlowTileSpeedMultiplier : trackEvent.IsPuddle
                            ? chocoRaceRacers[lane].PrefersWetWeather ? ChocoRaceWetPuddleSpeedMultiplier : ChocoRacePuddleSpeedMultiplier
                            : 0.55d,
                    trackEvent.Dodged, trackEvent.IsBoost, trackEvent.IsSlowTile));
            }
        }
        PrepareChocoRaceTreasureChest();
        for (var lane = 0; lane < chocoRaceRacers.Length; lane++)
            chocoRaceRacers[lane] = chocoRaceRacers[lane] with
            {
                FinishSeconds = CalculateChocoRaceTimeForDistance(chocoRaceRacers[lane],
                    chocoRaceCourseDistance, ChocoRaceObstaclesForLane(lane)),
            };
        chocoRaceWinnerLane = Enumerable.Range(0, chocoRaceRacers.Length)
            .MinBy(lane => chocoRaceRacers[lane].FinishSeconds);
        chocoRacePhaseEndsUtc = chocoRaceStartedUtc.AddSeconds(ChocoRaceRaceEndSeconds());
        chocoRaceRevision++;
        chocoRaceDirty = true;
        SendChocoRaceState();
    }

    private void SettleChocoRace()
    {
        chocoRacePayouts.Clear();
        chocoRaceRefilledPlayerIds.Clear();
        var finishOrder = Enumerable.Range(0, chocoRaceRacers.Length)
            .OrderBy(lane => chocoRaceRacers[lane].FinishSeconds).ToArray();
        for (var index = 0; index < chocoRaceLedgerBets.Count; index++)
        {
            var bet = chocoRaceLedgerBets[index];
            if (bet.Round != chocoRaceRound) continue;
            var returned = ChocoRaceReturnForPosition(bet.Amount,
                chocoRaceRacers[bet.Lane].Odds, Array.IndexOf(finishOrder, bet.Lane));
            chocoRaceLedgerBets[index] = bet with
            {
                Position = Array.IndexOf(finishOrder, bet.Lane) + 1,
                Return = returned,
            };
        }
        foreach (var group in chocoRaceBets.GroupBy(bet => bet.PlayerId))
        {
            if (!chocoRacePlayers.TryGetValue(group.Key, out var player)) continue;
            var stake = group.Sum(bet => bet.Amount);
            var returned = group.Sum(bet => ChocoRaceReturnForPosition(bet.Amount,
                chocoRaceRacers[bet.Lane].Odds, Array.IndexOf(finishOrder, bet.Lane)));
            var chips = player.Chips + returned;
            if (chocoRaceRefillEnabled && chips < ChocoRaceChipValues[0])
            {
                chips = chocoRaceStartingChips;
                chocoRaceRefilledPlayerIds.Add(group.Key);
            }
            chocoRacePlayers[group.Key] = player with { Chips = chips };
            chocoRacePayouts.Add(new ChocoRacePayout(group.Key, player.Name, stake, returned));
        }
        chocoRacePhase = ChocoRacePhase.Results;
        chocoRacePhaseEndsUtc = DateTime.UtcNow.AddSeconds(ChocoRaceResultDurationSeconds);
        AwardChocoRaceActivityPoints();
        chocoRaceRevision++;
        chocoRaceDirty = true;
        TryPlayChocoRaceResultEmote();
        SendChocoRaceState();
    }

    private static int ChocoRaceWinningReturn(int stake, double odds) =>
        stake + (int)Math.Round(stake * odds, MidpointRounding.AwayFromZero);

    private int ChocoRaceReturnForPosition(int stake, double odds, int position)
    {
        var fullReturn = ChocoRaceWinningReturn(stake, odds);
        return position switch
        {
            0 => fullReturn,
            1 when chocoRaceTopThreePays => (int)Math.Round(fullReturn * 0.30d, MidpointRounding.AwayFromZero),
            2 when chocoRaceTopThreePays => (int)Math.Round(fullReturn * 0.15d, MidpointRounding.AwayFromZero),
            _ => 0,
        };
    }

    private void BeginNextChocoRaceRound()
    {
        ResetChocoRaceDraft();
        chocoRaceRound++;
        chocoRaceBets.Clear();
        chocoRaceObstacles.Clear();
        chocoRacePayouts.Clear();
        chocoRaceRefilledPlayerIds.Clear();
        chocoRaceHandledBetIds.Clear();
        chocoRaceWinnerLane = -1;
        chocoRaceCourseDistance = 40d;
        chocoRaceStartedUtc = DateTime.MinValue;
        chocoRaceRoundStartedUtc = DateTime.UtcNow;
        chocoRacePhase = ChocoRacePhase.WaitingForBet;
        chocoRacePhaseEndsUtc = DateTime.MaxValue;
        chocoRaceSelectedLane = -1;
        chocoRaceResultsWinnersPage = 0;
        chocoRaceStartPlayedThisRound = false;
        chocoRaceMeteorPlayedThisRound = false;
        chocoRaceFanfarePlayedThisRound = false;
        PrepareChocoRaceRacers();
        PrepareChocoRaceConditions();
        chocoRaceRevision++;
        chocoRaceDirty = true;
        SendChocoRaceState();
    }

    private double ChocoRaceProgress(int lane, DateTime now)
    {
        if (chocoRaceStartedUtc == DateTime.MinValue ||
            chocoRacePhase is not (ChocoRacePhase.Racing or ChocoRacePhase.Results)) return 0d;
        var racer = chocoRaceRacers[lane];
        // Freeze the remaining chocos where they were when the third finisher ended the race.
        var elapsed = chocoRacePhase == ChocoRacePhase.Results
            ? ChocoRaceRaceEndSeconds()
            : Math.Max(0d, (now - chocoRaceStartedUtc).TotalSeconds);
        if (elapsed >= racer.FinishSeconds) return 1d;
        return Math.Clamp(ChocoRaceDistanceTravelled(racer, elapsed, ChocoRaceObstaclesForLane(lane)) /
            chocoRaceCourseDistance, 0d, 1d);
    }

    private double ChocoRaceRaceEndSeconds()
    {
        var thirdFinishSeconds = chocoRaceRacers
            .OrderBy(racer => racer.FinishSeconds).ElementAt(2).FinishSeconds;
        return Math.Max(thirdFinishSeconds,
            chocoRaceRacers[chocoRaceWinnerLane].FinishSeconds + ChocoRaceFinishHoldSeconds);
    }

    private ChocoRaceObstacle[] ChocoRaceObstaclesForLane(int lane) =>
        chocoRaceObstacles.Where(obstacle => obstacle.Lane == lane).ToArray();

    private double CalculateChocoRaceTimeForDistance(ChocoRacer racer, double targetDistance,
        IReadOnlyList<ChocoRaceObstacle>? obstacles = null)
    {
        var upper = 16d;
        while (ChocoRaceDistanceTravelled(racer, upper, obstacles) < targetDistance) upper *= 2d;
        var lower = 0d;
        for (var step = 0; step < 36; step++)
        {
            var middle = (lower + upper) / 2d;
            if (ChocoRaceDistanceTravelled(racer, middle, obstacles) >= targetDistance) upper = middle;
            else lower = middle;
        }
        return upper;
    }

    private double ChocoRaceDistanceTravelled(ChocoRacer racer, double elapsed,
        IReadOnlyList<ChocoRaceObstacle>? obstacles = null)
    {
        var distance = 0d;
        var baseSpeed = ChocoRaceEffectiveSpeed(racer) * ChocoRaceFormFactor(racer.PaceSeed);
        var remaining = Math.Max(0d, elapsed);
        for (var segment = 0; remaining > 0d; segment++)
        {
            var duration = Math.Min(remaining, ChocoRacePaceIntervalSeconds);
            var from = ChocoRacePaceFactor(racer.PaceSeed, segment);
            var to = ChocoRacePaceFactor(racer.PaceSeed, segment + 1);
            static double PaceArea(double startPace, double endPace, double time) =>
                startPace * time + (endPace - startPace) * time * time /
                (2d * ChocoRacePaceIntervalSeconds);
            var area = PaceArea(from, to, duration);
            if (obstacles is { Count: > 0 })
            {
                var segmentStart = segment * ChocoRacePaceIntervalSeconds;
                var boundaries = new List<double> { 0d, duration };
                foreach (var obstacle in obstacles)
                {
                    if (obstacle.Dodged || obstacle.SlowSeconds <= 0d) continue;
                    var effectStart = Math.Clamp(obstacle.HitSeconds - segmentStart, 0d, duration);
                    var effectEnd = Math.Clamp(obstacle.HitSeconds + obstacle.SlowSeconds - segmentStart,
                        0d, duration);
                    if (effectEnd <= effectStart) continue;
                    boundaries.Add(effectStart);
                    boundaries.Add(effectEnd);
                }
                boundaries.Sort();
                area = 0d;
                for (var boundary = 1; boundary < boundaries.Count; boundary++)
                {
                    var start = boundaries[boundary - 1];
                    var end = boundaries[boundary];
                    if (end <= start) continue;
                    var midpoint = segmentStart + (start + end) / 2d;
                    var speedMultiplier = 1d;
                    foreach (var obstacle in obstacles)
                        if (!obstacle.Dodged && midpoint >= obstacle.HitSeconds &&
                            midpoint < obstacle.HitSeconds + obstacle.SlowSeconds)
                            speedMultiplier *= obstacle.SpeedMultiplier;
                    area += speedMultiplier * (PaceArea(from, to, end) - PaceArea(from, to, start));
                }
            }
            distance += baseSpeed * area;
            remaining -= duration;
        }
        return distance;
    }

    private static double ChocoRacePaceFactor(int seed, int segment)
    {
        return 0.65d + ChocoRaceRandomUnit(seed, segment) * 0.7d;
    }

    private double ChocoRaceEffectiveSpeed(ChocoRacer racer)
    {
        // Odds still favour higher tiers, but adjacent tiers can swap actual speed.
        // This adjustment is seeded only after bets close, alongside the race pace.
        var variation = (ChocoRaceRandomUnit(racer.PaceSeed, 779) * 2d - 1d)
            * ChocoRaceHiddenSpeedVariation;
        var weatherAdjustment = racer.PrefersWetWeather ? chocoRaceWeather switch
        {
            ChocoRaceWeather.Wet => 0.3d,
            ChocoRaceWeather.Rainstorms => 0.6d,
            _ => 0d,
        } : chocoRaceWeather switch
        {
            ChocoRaceWeather.Dry => 0.3d,
            ChocoRaceWeather.Wet => -0.3d,
            _ => -0.6d,
        };
        return 1d + (racer.Speed + variation + weatherAdjustment - 1.6d) *
            ChocoRaceTierSpeedInfluence;
    }

    private static double ChocoRaceFormFactor(int seed)
    {
        // Persistent form creates occasional upsets without changing the quoted speed tier.
        var first = Math.Max(1e-12d, ChocoRaceRandomUnit(seed, 777));
        var second = ChocoRaceRandomUnit(seed, 778);
        var normal = Math.Sqrt(-2d * Math.Log(first)) * Math.Cos(2d * Math.PI * second);
        return Math.Clamp(Math.Exp(0.25d * normal), 0.75d, 1.3d);
    }

    private static double ChocoRaceRandomUnit(int seed, int segment)
    {
        var value = unchecked((uint)seed + (uint)segment * 0x9E3779B9u);
        value ^= value >> 16;
        value = unchecked(value * 0x7FEB352Du);
        value ^= value >> 15;
        value = unchecked(value * 0x846CA68Bu);
        value ^= value >> 16;
        return value / (double)uint.MaxValue;
    }

    private int ChocoRaceSecondsLeft => chocoRacePhaseEndsUtc == DateTime.MaxValue ? 0 :
        Math.Max(0, (int)Math.Ceiling((chocoRacePhaseEndsUtc - ChocoRaceNowUtc).TotalSeconds));

    private void TryPlayChocoRaceResultEmote()
    {
        if (chocoRacePhase != ChocoRacePhase.Results || chocoRaceSessionId is null ||
            chocoRaceLastEmoteSessionId == chocoRaceSessionId && chocoRaceLastEmoteRound == chocoRaceRound)
            return;
        chocoRaceLastEmoteSessionId = chocoRaceSessionId;
        chocoRaceLastEmoteRound = chocoRaceRound;
        if (!Plugin.Cfg.ChocoRaceEmotesEnabled) return;

        var localResult = chocoRacePayouts.FirstOrDefault(payout => payout.PlayerId == LocalChocoRaceUserId);
        if (localResult is null) return;
        var net = localResult.Return - localResult.Stake;
        if (net == 0) return;
        var emotes = net > 0 ? RouletteWinEmotes : RouletteLoseEmotes;
        try
        {
            Plugin.ExecuteChatCommand(emotes[Random.Shared.Next(emotes.Length)] + " motion");
        }
        catch (Exception)
        {
            AepLog.Warning("[ChocoRace] Could not play the result emote.");
        }
    }

    private string ChocoRaceTicker()
    {
        if (chocoRacePhase != ChocoRacePhase.Results || chocoRaceWinnerLane is < 0 or >= 6)
            return chocoRacePhase switch
            {
                ChocoRacePhase.WaitingForBet => "FIRST BET STARTS THE CLOCK",
                ChocoRacePhase.Betting => "PLACE YOUR BETS",
                ChocoRacePhase.Racing => "AND THEY ARE OFF!",
                _ => "CHOCO RACEBETS",
            };

        var resultDuration = ChocoRaceResultDurationSeconds;
        var resultStep = (resultDuration - ChocoRaceSecondsLeft) / 2;
        if (resultStep <= 0) return $"{chocoRaceRacers[chocoRaceWinnerLane].Name.ToUpperInvariant()} WINS!";
        var outcomes = chocoRacePayouts.ToArray();
        if (outcomes.Length == 0) return "NO BETS THIS RACE";
        var payout = outcomes[(resultStep - 1) % outcomes.Length];
        var name = payout.Name.ToUpperInvariant();
        if (name.Length > 18) name = name[..18];
        var net = payout.Return - payout.Stake;
        return net > 0 ? $"{name} WON {net} CHIPS" :
            net < 0 ? $"{name} LOST {-net} CHIPS" : $"{name} BROKE EVEN";
    }

    internal bool IsChocoRaceTvControlAt(Vector2 uv) =>
        ChocoRaceTvIntroBlocksInput
            ? uv.X is >= 0f and <= 1f && uv.Y is >= 0f and <= 1f
            : chocoRaceTvVolumeDragging || TryGetChocoRaceTvTarget(uv, out _);

    private bool IsChocoRacePageControlAt(Vector2 uv) =>
        !ChocoRacePageIntroBlocksInput &&
        (chocoRacePageVolumeDragging || TryGetChocoRaceTvTarget(uv, out _));

    internal void SetChocoRaceTvHover(Vector2? uv) =>
        SetChocoRaceHover(ChocoRaceTvIntroBlocksInput ? null : uv, ref chocoRaceTvHoveredTarget);

    private void SetChocoRacePageHover(Vector2? uv) =>
        SetChocoRaceHover(ChocoRacePageIntroBlocksInput ? null : uv, ref chocoRacePageHoveredTarget);

    private void SetChocoRaceHover(Vector2? uv, ref int hoveredTarget)
    {
        var target = uv is { } point && TryGetChocoRaceTvTarget(point, out var hitTarget)
            ? hitTarget : -1;
        if (target == hoveredTarget) return;
        hoveredTarget = target;
        var activeTarget = chocoRacePageHoveredTarget >= 0
            ? chocoRacePageHoveredTarget : chocoRaceTvHoveredTarget;
        chocoRaceHoveredControlTarget = activeTarget;
        chocoRaceHoveredLane = activeTarget is >= 0 and < 6 ? activeTarget : -1;
        chocoRaceHoveredBetLane = activeTarget is >= 500 and < 506 ? activeTarget - 500 : -1;
        chocoRaceDirty = true;
    }

    internal void SubmitChocoRaceTvControlAt(Vector2 uv)
    {
        if (ChocoRaceTvIntroBlocksInput) return;
        SubmitChocoRaceControlAt(uv);
    }

    private void SubmitChocoRacePageControlAt(Vector2 uv)
    {
        if (ChocoRacePageIntroBlocksInput) return;
        SubmitChocoRaceControlAt(uv);
    }

    private void SubmitChocoRaceControlAt(Vector2 uv)
    {
        if (!TryGetChocoRaceTvTarget(uv, out var target)) return;
        chocoRaceLastClickedTarget = target;
        chocoRaceLastClickUtc = ChocoRaceNowUtc;
        if (target is 730 or 731) HandleChocoRaceHelpTarget(target);
        else if (target is 700 or 701 or 710 or 711 or 712 or 720)
            HandleChocoRaceSettingsTarget(target, uv);
        else if (target is 600 or 601) HandleChocoRaceResultsTarget(target);
        else if (target is >= 300 and < 500) HandleChocoRaceScoreboardTarget(target);
        else if (target is >= 0 and < 6) AddChocoRaceDraftChip(target);
        else if (target is >= 100 and < 105 && ChocoRaceCanEditDraft)
            SelectChocoRaceChip(ChocoRaceChipValues[target - 100]);
        else if (target == 200) SubmitChocoRaceBet();
        else if (target == 220) ClearChocoRaceDraft();
        chocoRaceDirty = true;
    }

    private bool TryGetChocoRaceTvTarget(Vector2 uv, out int target)
    {
        target = -1;
        if (!ChocoRaceActive) return false;
        var x = uv.X * WhiteboardRenderer.Width;
        var y = uv.Y * WhiteboardRenderer.Height;
        if (TryGetChocoRaceHelpTarget(x, y, out target)) return true;
        if (chocoRaceHelpOpen) return false;
        if (TryGetChocoRaceSettingsTarget(x, y, out target)) return true;
        if (chocoRaceSettingsOpen) return false;
        if (x is >= 67 and < 272 && y is >= 43 and < 94)
        {
            target = 300;
            return true;
        }
        if (chocoRaceScoreboardOpen)
        {
            if (chocoRaceHistoryPlayerId is not null)
            {
                if (x is >= 1090 and < 1229 && y is >= 143 and < 186)
                {
                    target = 301;
                    return true;
                }
                return false;
            }
            if (x is >= 1102 and < 1218 && y is >= 253 and < 624)
            {
                var slot = (int)((y - 253) / 53);
                if (slot < RaceLedgerRowsPerPage &&
                    chocoRaceScoreboardPage * RaceLedgerRowsPerPage + slot < chocoRacePlayers.Count &&
                    y < 253 + slot * 53 + 51)
                {
                    target = 400 + slot;
                    return true;
                }
            }
            if (y is >= 640 and < 678)
            {
                var maxPage = chocoRacePlayers.Count == 0 ? 0 :
                    (chocoRacePlayers.Count - 1) / RaceLedgerRowsPerPage;
                if (x is >= 1012 and < 1117 && chocoRaceScoreboardPage > 0)
                {
                    target = 310;
                    return true;
                }
                if (x is >= 1126 and < 1231 && chocoRaceScoreboardPage < maxPage)
                {
                    target = 311;
                    return true;
                }
            }
            return false;
        }
        if (chocoRacePhase == ChocoRacePhase.Results)
        {
            if (TryGetChocoRaceResultsTarget(x, y, out target)) return true;
            return false;
        }
        if ((chocoRacePhase is ChocoRacePhase.WaitingForBet or ChocoRacePhase.Betting) &&
            x is >= 545 and < 795 && y is >= 194 and < 310)
            return false;
        for (var lane = 0; lane < chocoRaceRacers.Length; lane++)
        {
            if (!chocoRaceBets.Any(bet => bet.Lane == lane)) continue;
            var center = ChocoRaceChipStackCenter(lane);
            if (Vector2.Distance(new Vector2(x, y), center) > 17f) continue;
            target = 500 + lane;
            return true;
        }
        if (y is >= 460 and < 483)
        {
            for (var lane = 0; lane < chocoRaceRacers.Length; lane++)
            {
                if (Math.Abs(x - ChocoRaceWeatherIconX(lane)) > 11) continue;
                target = 800 + lane;
                return true;
            }
        }
        if (ChocoRaceCanEditDraft && y is >= 143 and < 431 && x is >= 28 and < 1120)
        {
            target = Math.Clamp((int)((y - 143) / 48), 0, 5);
            return true;
        }
        if (ChocoRaceCanEditDraft && y is >= 457 and < 536 && x is >= 17 and < 1012)
        {
            target = Math.Clamp((int)((x - 17) / 165), 0, 5);
            return true;
        }
        if (ChocoRaceCanEditDraft && y is >= 581 and < 640)
        {
            for (var index = 0; index < 5; index++)
            {
                if (Vector2.Distance(new Vector2(x, y), new Vector2(309 + index * 91, 610)) > 36f) continue;
                target = 100 + index;
                return true;
            }
        }
        if (x is >= 1160 and < 1250 && y is >= 549 and < 579 && ChocoRaceCanEditDraft &&
            ChocoRaceDraftTotal > 0)
        {
            target = 220;
            return true;
        }
        if (x is >= 779 and < 974 && y is >= 581 and < 637 &&
            (ChocoRaceDraftTotal > 0 || ChocoRaceLocalBetLocked))
        {
            target = 221;
            return true;
        }
        if (x is >= 985 and < 1250 && y is >= 607 and < 637)
        {
            if (!ChocoRaceCanPlaceDraft) return false;
            target = 200;
            return true;
        }
        return false;
    }
}

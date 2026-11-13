using AlphaChannel.Plugin.ActivityPoints;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // At most one recent result per activity/account is checked again in memory.
    // The protected points service also deduplicates events across reloads.
    private readonly Dictionary<string, string> observedArcadePointEvents = new();

    private void AwardArcadeResult(string game, string eventId, long units, bool spin = false)
    {
        if (CurrentSession?.AccountId is not { } account || !activityPoints.StorageHealthy) return;
        var key = account + ":" + game;
        if (observedArcadePointEvents.GetValueOrDefault(key) == eventId) return;
        var gained = spin ? activityPoints.AwardCounted(account, eventId, "lucky-wheel-spins", 1, 30)
            : activityPoints.Award(account, eventId, units);
        if (!activityPoints.StorageHealthy) return;
        // Account switches cannot grow this cache indefinitely.
        if (observedArcadePointEvents.Count > 64) observedArcadePointEvents.Clear();
        observedArcadePointEvents[key] = eventId;
        if (gained <= 0) return;
        recentActivityPointsAward = gained;
        recentActivityPointsAwardUtc = DateTime.UtcNow;
        recentActivityPointsAccountId = account;
    }

    private void UpdateAdditionalArcadePoints()
    {
        if (CurrentSession?.AccountId is not { } account) return;
        if (teamsPhase == TeamsPhase.Finished && teamsSession is { } teams && TeamsLocalColour is 0 or 1)
            AwardArcadeResult("teams", $"teams:{teams}:complete",
                ArcadePointRewards.TeamScore(TeamsLocalColour == 0 ? teamsRedScore : teamsBlueScore));
        if (bingoPhase == BingoPhase.Results && bingoSessionId is { } bingo && bingoWinnerIds.Contains(account))
            AwardArcadeResult("bingo", $"bingo:{bingo}:{bingoRound}", ArcadePointRewards.Bingo);
        if (skyRailSessionId is { } flight && skyRailLocked.Contains(account))
        {
            if (skyRailBossDefeatedUtc != DateTime.MinValue)
                AwardArcadeResult("flight-boss", $"flight:{flight}:{skyRailRound}:boss", ActivityPointsService.UnitsPerPoint);
            if (skyRailPhase == SkyRailPhase.Results && skyRailWave >= skyRailWaveCount && skyRailPlayers.TryGetValue(account, out var pilot))
                AwardArcadeResult("flight-score", $"flight:{flight}:{skyRailRound}:score", ArcadePointRewards.FlightScore(pilot.Score));
        }
        if (slotMachinePhase == SlotMachinePhase.Results && slotMachineSessionId is { } slots && slotMachineActivePlayerId == account)
            AwardArcadeResult("slots", $"slots:{slots}:{slotMachineTurn}", ArcadePointRewards.SlotSpin(slotMachinePayout, SlotMachineRules.SpinCost));
        if (pokerPhase == PokerPhase.Showdown && pokerSessionId is { } poker && LocalPokerPlayer is { InHand: true, HandProfit: { } profit })
            AwardArcadeResult("poker", $"poker:{poker}:{pokerHand}", ArcadePointRewards.PokerHand(profit));
        if (luckyWheelState is { Spinning: false, Spin: > 0, SpinnerId: { } spinner } wheel && spinner == account)
            AwardArcadeResult("wheel", $"wheel:{wheel.SessionId}:{wheel.StartedUnixMs}", 0, spin: true);
    }
}

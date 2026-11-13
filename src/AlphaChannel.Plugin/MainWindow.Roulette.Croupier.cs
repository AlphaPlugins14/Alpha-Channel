namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool RouletteBetsOpen => RouletteActive &&
        (roulettePhase == RoulettePhase.Betting || !rouletteCroupierMode && roulettePhase == RoulettePhase.WaitingForBet);
    private bool LocalRouletteCanBet => RouletteBetsOpen && !(rouletteCroupierMode && IsLocalActivityHost);
    private bool CanAcceptRouletteBet(string playerId) => IsLocalActivityHost && RouletteBetsOpen &&
        !(rouletteCroupierMode && playerId == LocalRouletteUserId);

    private bool CanBeginRouletteSpin => IsLocalActivityHost && RouletteActive &&
        roulettePhase == (rouletteCroupierMode ? RoulettePhase.BetsClosed : RoulettePhase.Betting);

    private void ToggleRouletteCroupierBets()
    {
        if (!IsLocalActivityHost || !RouletteActive || !rouletteCroupierMode || RouletteResultsScreenVisible(DateTime.UtcNow)) return;
        if (roulettePhase is RoulettePhase.WaitingForBet or RoulettePhase.Results)
        {
            // Keep the settled round visible until the croupier opens the next table.
            BeginRouletteWaitingRound(announce: false);
            roulettePhase = RoulettePhase.Betting;
        }
        else if (roulettePhase == RoulettePhase.Betting)
            roulettePhase = RoulettePhase.BetsClosed;
        else return;
        roulettePhaseEndsUtc = DateTime.MaxValue;
        rouletteRevision++;
        whiteboardTvDirty = true;
        SendRouletteState();
    }

    private void UpdateRouletteRound(DateTime now)
    {
        if (!IsLocalActivityHost || !RouletteActive) return;
        if (!rouletteCroupierMode && roulettePhase == RoulettePhase.Betting && now >= roulettePhaseEndsUtc)
            BeginRouletteSpin();
        else if (roulettePhase == RoulettePhase.Spinning && now >= roulettePhaseEndsUtc)
            SettleRouletteSpin();
        else if (!rouletteCroupierMode && roulettePhase == RoulettePhase.Results && now >= roulettePhaseEndsUtc)
            BeginRouletteWaitingRound();
    }
}

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly Dictionary<string, RouletteBetState[]> roulettePreviousBets = new(StringComparer.Ordinal);
    private int roulettePreviousBetRound;

    private void RememberRouletteRoundBets()
    {
        roulettePreviousBets.Clear();
        foreach (var group in rouletteBets.GroupBy(bet => bet.PlayerId))
            roulettePreviousBets[group.Key] = group.ToArray();
        roulettePreviousBetRound = rouletteRoundNumber;
    }

    private bool CanRepeatRouletteBet(string playerId) => RouletteBetsOpen &&
        roulettePreviousBetRound == rouletteRoundNumber - 1 &&
        roulettePreviousBets.ContainsKey(playerId) &&
        !rouletteBets.Any(bet => bet.PlayerId == playerId);

    private void RepeatRouletteBet()
    {
        if (!LocalRouletteCanBet || rouletteSessionId is null || !CanRepeatRouletteBet(LocalRouletteUserId)) return;
        var total = roulettePreviousBets[LocalRouletteUserId].Sum(bet => (long)bet.Amount);
        if (!roulettePlayers.TryGetValue(LocalRouletteUserId, out var player) || player.Chips < total)
        {
            rouletteNotEnoughChipsUntilUtc = DateTime.UtcNow.AddSeconds(2.5);
            whiteboardTvDirty = true;
            return;
        }
        if (IsLocalActivityHost) HostRepeatRouletteBet(LocalRouletteUserId, rouletteRoundNumber);
        else SendArcadePlayerAction("roulette", $"R|{rouletteSessionId}|{rouletteRoundNumber}");
    }

    private void HostRepeatRouletteBet(string playerId, int round)
    {
        // The host restores its own accepted round history. The client supplies
        // neither targets nor amounts, and a duplicate cannot place a second set.
        if (round != rouletteRoundNumber || !CanAcceptRouletteBet(playerId) ||
            !CanRepeatRouletteBet(playerId) || !roulettePlayers.TryGetValue(playerId, out var player)) return;
        var bets = roulettePreviousBets[playerId];
        var total = bets.Sum(bet => (long)bet.Amount);
        if (total <= 0 || total > player.Chips || bets.Any(bet => bet.Amount <= 0 || !IsValidRouletteTarget(bet.Target))) return;
        roulettePlayers[playerId] = player with { Chips = player.Chips - (int)total };
        rouletteBets.AddRange(bets.Select(bet => bet with { PlayerName = player.Name }));
        var ledger = EnsureRouletteLedgerPlayer(playerId);
        rouletteLedger[playerId] = ledger with
        {
            HasJoinedTable = true,
            LastBet = bets.Length > 1 ? $"{total} on multiple" : $"{total} on {FormatRouletteTarget(bets[0].Target)}",
            LastChange = -(int)total,
            ChangeKind = RouletteLedgerChangeKind.Bet,
            ChangeText = $"-{total} previous bets repeated",
        };
        if (roulettePhase == RoulettePhase.WaitingForBet)
        {
            roulettePhase = RoulettePhase.Betting;
            roulettePhaseEndsUtc = DateTime.UtcNow.AddSeconds(rouletteBettingSeconds);
        }
        rouletteRevision++;
        whiteboardTvDirty = true;
        SendRouletteState();
    }
}

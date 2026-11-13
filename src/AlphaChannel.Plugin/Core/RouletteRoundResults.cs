namespace AlphaChannel.Plugin.Core;

internal static class RouletteRoundResults
{
    internal const int BigWinProfitMultiplier = 20;
    internal sealed record Player(string Id, string Name, int Chips);
    internal sealed record Bet(string PlayerId, int Amount);
    internal sealed record Payout(string PlayerId, int Amount);
    internal sealed record Result(string Id, string Name, int Chips, long Stake, long Profit)
    {
        internal bool BigWinner => Stake > 0 && Profit >= Stake * BigWinProfitMultiplier;
        internal double ProfitMultiplier => Stake > 0 ? (double)Profit / Stake : 0;
    }

    internal static Result[] Calculate(IEnumerable<Player> players, IEnumerable<Bet> bets, IEnumerable<Payout> payouts)
    {
        var stakes = bets.GroupBy(bet => bet.PlayerId).ToDictionary(group => group.Key, group => group.Sum(bet => (long)bet.Amount));
        var winnings = payouts.GroupBy(payout => payout.PlayerId).ToDictionary(group => group.Key, group => group.Sum(payout => (long)payout.Amount));
        return players.Select(player => new Result(player.Id, player.Name, player.Chips,
                stakes.GetValueOrDefault(player.Id), winnings.GetValueOrDefault(player.Id) - stakes.GetValueOrDefault(player.Id)))
            .OrderByDescending(result => result.Profit)
            .ThenBy(result => result.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

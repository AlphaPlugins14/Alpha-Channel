namespace AlphaChannel.Plugin.ActivityPoints;

internal static class ArcadePointRewards
{
    internal static long TeamScore(int score) => Math.Max(0L, score) * ActivityPointsService.UnitsPerPoint;
    internal static long Bingo => 2 * ActivityPointsService.UnitsPerPoint;
    internal static long FlightScore(int score) => Math.Max(0L, score) / 2000 * ActivityPointsService.UnitsPerPoint;
    internal static long SlotSpin(int payout, int stake) => Math.Max(0L, (long)payout - stake) / 100 * ActivityPointsService.UnitsPerPoint;
    internal static long PokerHand(int profit) => Math.Max(0L, profit) / 1000 * ActivityPointsService.UnitsPerPoint;
}

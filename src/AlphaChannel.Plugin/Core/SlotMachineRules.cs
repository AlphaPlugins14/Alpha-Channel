namespace AlphaChannel.Plugin;

internal static class SlotMachineRules
{
    internal const int StartingCoins = 100;
    internal const int SpinCost = 5;
    internal const int JackpotPayout = 250;
    internal const int JackpotSymbol = 13;
    internal static readonly int[] Symbols = [5, 11, 13, 17, 38, 40, 50, 55];
    // Gentle 30:23 weighting from the lowest triple payout to the jackpot.
    private static readonly int[] SymbolWeights = [30, 29, 23, 28, 27, 26, 25, 24];
    internal const int TotalSymbolWeight = 212;
    internal static int RollingSymbol(int seed, int column, int reelPosition)
    {
        // Mix every position across the full range instead of adjacent weighted buckets.
        var hash = unchecked((uint)seed ^ (uint)column * 0x9E3779B9u ^ (uint)reelPosition * 0x85EBCA6Bu);
        hash ^= hash >> 16;
        hash = unchecked(hash * 0x7FEB352Du);
        hash ^= hash >> 15;
        hash = unchecked(hash * 0x846CA68Bu);
        hash ^= hash >> 16;
        return SymbolForRoll((int)(hash % TotalSymbolWeight));
    }
    internal static int SymbolForRoll(int roll)
    {
        if (roll < 0 || roll >= TotalSymbolWeight) throw new ArgumentOutOfRangeException(nameof(roll));
        for (var index = 0; index < Symbols.Length; index++)
        {
            if (roll < SymbolWeights[index]) return Symbols[index];
            roll -= SymbolWeights[index];
        }
        throw new InvalidOperationException("Slot symbol weights do not match their total.");
    }
    internal static (int Pair, int Triple) PayoutForSymbol(int symbol) => symbol switch
    {
        5 => (18, 60), 11 => (19, 70), 13 => (33, JackpotPayout),
        17 => (20, 80), 38 => (21, 90), 40 => (22, 100),
        50 => (23, 110), 55 => (24, 120),
        _ => (0, 0),
    };
    internal static bool IsTriple(int[] symbols) => ValidSymbols(symbols) &&
        symbols[3] == symbols[4] && symbols[4] == symbols[5];
    internal static bool ValidSymbols(int[]? symbols) => symbols is { Length: 9 } && symbols.All(Symbols.Contains);
    internal static (int Payout, string Label) Evaluate(int[] symbols)
    {
        if (!ValidSymbols(symbols)) return (0, "Invalid spin");
        var a = symbols[3]; var b = symbols[4]; var c = symbols[5];
        if (a == b && b == c)
            return a == JackpotSymbol ? (JackpotPayout, "Jackpot!") : (PayoutForSymbol(a).Triple, "Triple win!");
        if (a == b || a == c) return (PayoutForSymbol(a).Pair, "Pair win!");
        if (b == c) return (PayoutForSymbol(b).Pair, "Pair win!");
        return (0, "No match");
    }
}

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static string SlotShortName(string? name, int length = 12)
    {
        var value = string.IsNullOrWhiteSpace(name) ? "PLAYER" : name.Trim().ToUpperInvariant();
        return value[..Math.Min(length, value.Length)];
    }

    private void DrawSlotLedger(byte[] pixels, int width, int height)
    {
        const uint gold = 0xFFDB83, white = 0xFFF0D2, muted = 0xCDBDD9;
        FillQuizTvPanel(pixels, width, height, 80, 100, 1120, 560, 0x171325, gold);
        DrawPictionaryPixelText(pixels, width, height, 105, 125, "STARFALL PLAYER LEDGER", 4, gold);
        BingoButton(pixels, width, height, 1090, 125, 90, 45, "CLOSE", false, false, true);
        foreach (var (x, label) in new[] { (110, "PLAYER"), (315, "COINS"), (427, "SPINS"),
                     (521, "WINS"), (617, "NET"), (737, "BEST"), (833, "REFILLS") })
            DrawPictionaryPixelText(pixels, width, height, x, 187, label, 2, muted);
        var players = slotMachinePlayers.Values.OrderByDescending(p => p.Won - p.Spent)
            .ThenBy(p => p.Name, StringComparer.Ordinal).ToArray();
        var pages = Math.Max(1, (players.Length + 6) / 7);
        slotMachineLedgerPage = Math.Clamp(slotMachineLedgerPage, 0, pages - 1);
        DrawPictionaryPixelText(pixels, width, height, 1000, 187,
            $"PAGE {slotMachineLedgerPage + 1}/{pages}", 2, muted);
        for (var i = 0; i < Math.Min(7, players.Length - slotMachineLedgerPage * 7); i++)
        {
            var p = players[slotMachineLedgerPage * 7 + i];
            var y = 228 + i * 44;
            FillQuizTvPanel(pixels, width, height, 100, y - 8, 1080, 38, p.Id == LocalSlotPlayerId ? 0x342445u : 0x222039u, 0x453759);
            DrawUsernamePixelText(pixels, width, height, 110, y, SlotShortName(p.Name), 2, white,p.Id);
            DrawPictionaryPixelText(pixels, width, height, 315, y, p.Coins.ToString(), 2, gold);
            DrawPictionaryPixelText(pixels, width, height, 427, y, p.Spins.ToString(), 2, white);
            DrawPictionaryPixelText(pixels, width, height, 521, y, p.Wins.ToString(), 2, white);
            DrawPictionaryPixelText(pixels, width, height, 617, y, (p.Won - p.Spent).ToString("+0;-0;0"), 2, p.Won >= p.Spent ? 0x75E8A8u : 0xFF8B94u);
            DrawPictionaryPixelText(pixels, width, height, 737, y, p.Best.ToString(), 2, gold);
            DrawPictionaryPixelText(pixels, width, height, 833, y, p.Refills.ToString(), 2, muted);
        }
        DrawPictionaryPixelText(pixels, width, height, 110, 580, "NET = PAYOUTS MINUS SPIN COSTS", 2, muted);
        DrawPictionaryPixelText(pixels, width, height, 110, 612, "PLAY COINS ONLY - RESET WHEN ACTIVITY ENDS", 2, muted);
        BingoButton(pixels, width, height, 850, 580, 150, 50, "PREV", false, false, slotMachineLedgerPage > 0);
        BingoButton(pixels, width, height, 1030, 580, 150, 50, "NEXT", false, false, slotMachineLedgerPage < pages - 1);
    }

    private void DrawSlotCoinRain(byte[] pixels, int width, int height)
    {
        if (slotMachinePhase != SlotMachinePhase.Results || !SlotMachineRules.IsTriple(slotMachineSymbols)) return;
        var elapsed = (DateTime.UtcNow - slotMachinePhaseStartedUtc).TotalSeconds;
        if (elapsed is < 0 or > 5) return;
        var jackpot = slotMachinePayout == SlotMachineRules.JackpotPayout;
        for (var i = 0; i < (jackpot ? 100 : 45); i++)
        {
            var seed = unchecked((uint)(slotMachineSpinSeed + i * 104729));
            var age = elapsed - i % 9 * (jackpot ? .06 : .12);
            if (age < 0) continue;
            var x = 190 + (int)(seed % 900) + (int)(Math.Sin(age * 3 + i) * 20);
            var y = -35 + (int)(age * (jackpot ? 300 + seed % 160 : 140 + seed % 100));
            if (y > 685) continue;
            FillRoulettePrototypeCircle(pixels, width, height, x, y, jackpot ? 12 : 9, 0xB37A32);
            FillRoulettePrototypeCircle(pixels, width, height, x, y, jackpot ? 10 : 7, 0xFFDB83);
            DrawRoulettePrototypeLine(pixels, width, height, x, y - 4, x, y + 4, 0xFFF0D2, 2);
        }
    }
}

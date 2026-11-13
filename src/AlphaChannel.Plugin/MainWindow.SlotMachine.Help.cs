namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool slotMachineHelpOpen;

    private void ToggleSlotMachineHelp()
    {
        slotMachineHelpOpen = !slotMachineHelpOpen;
        slotMachineLedgerOpen = false;
        whiteboardTvDirty = true;
    }

    private void DrawSlotMachineHelp(byte[] pixels, int width, int height)
    {
        const uint gold = 0xFFDB83, cream = 0xFFF0D2, muted = 0xCDBDD9;
        FillQuizTvPanel(pixels, width, height, 180, 90, 920, 590, 0x171325, gold);
        DrawPictionaryPixelText(pixels, width, height, 210, 114, "SYMBOL PAYOUTS", 3, gold);
        BingoButton(pixels, width, height, 960, 110, 100, 45, "CLOSE", false, false, true);
        DrawPictionaryPixelText(pixels, width, height, 210, 158, "ONLY THE MIDDLE ROW PAYS", 2, muted);
        DrawPictionaryPixelText(pixels, width, height, 280, 194, "SYMBOL", 2, muted);
        DrawPictionaryPixelText(pixels, width, height, 770, 194, "PAIR", 2, muted);
        DrawPictionaryPixelText(pixels, width, height, 930, 194, "TRIPLE", 2, muted);
        var payoutSymbols = SlotMachineRules.Symbols.OrderBy(symbol => SlotMachineRules.PayoutForSymbol(symbol).Triple).ToArray();
        for (var index = 0; index < payoutSymbols.Length; index++)
        {
            var symbol = payoutSymbols[index];
            var y = 232 + index * 38;
            FillQuizTvPanel(pixels, width, height, 205, y - 7, 870, 32,
                index % 2 == 0 ? 0x292039u : 0x211B30u, 0x453759);
            DrawSlotPixelSymbol(pixels, width, height, symbol, 241, y + 8, 12);
            var name = SlotSymbolNames[symbol - 1].Split(" · ")[0].ToUpperInvariant();
            DrawPictionaryPixelText(pixels, width, height, 280, y, name, 2, cream);
            var payout = SlotMachineRules.PayoutForSymbol(symbol);
            DrawPictionaryPixelText(pixels, width, height, 770, y, payout.Pair.ToString(), 2, gold);
            DrawPictionaryPixelText(pixels, width, height, 930, y, payout.Triple.ToString(), 2, gold);
        }
        DrawPictionaryPixelText(pixels, width, height, 210, 632, "PAYOUTS ARE TOTAL COINS RETURNED; SPIN COST: 5", 2, muted);
        DrawPictionaryPixelText(pixels, width, height, 210, 654, "THREE GIL SYMBOLS AWARD THE 250-COIN JACKPOT", 2, gold);
    }
}

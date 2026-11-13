using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool chocoRaceHelpOpen;

    private bool TryGetChocoRaceHelpTarget(float x, float y, out int target)
    {
        target = -1;
        if (x is >= 1058 and < 1100 && y is >= 73 and < 105)
        {
            target = 730;
            return true;
        }
        if (chocoRaceHelpOpen && x is >= 913 and < 956 && y is >= 183 and < 217)
        {
            target = 731;
            return true;
        }
        return false;
    }

    private void HandleChocoRaceHelpTarget(int target)
    {
        chocoRaceHelpOpen = target == 730 && !chocoRaceHelpOpen;
        if (chocoRaceHelpOpen) chocoRaceSettingsOpen = false;
        chocoRaceDirty = true;
    }

    private void DrawChocoRaceHelpButton(byte[] pixels)
    {
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        const uint gold = 0xF6BC4E, purple = 0x8959ED, white = 0xF5F3FB;
        var hovered = chocoRaceHoveredControlTarget == 730;
        if (hovered)
            FillQuizTvPanel(pixels, width, height, 1055, 70, 48, 38, gold, white);
        FillQuizTvPanel(pixels, width, height, 1058, 73, 42, 32,
            hovered || chocoRaceHelpOpen ? 0x64418Eu : 0x21162Fu,
            hovered ? gold : purple);
        DrawPictionaryPixelTextCentered(pixels, width, height, 1079, 78, "?", 3, white);
    }

    private void DrawChocoRaceHelpOverlay(byte[] pixels)
    {
        if (!chocoRaceHelpOpen) return;
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        const uint gold = 0xF6BC4E, purple = 0x8959ED, white = 0xF5F3FB, muted = 0xB9A9CF;
        FillQuizTvPanel(pixels, width, height, 301, 167, 678, 385, 0x17131E, purple);
        FillPictionaryPixels(pixels, width, height, 319, 220, 642, 2, gold);
        DrawPictionaryPixelText(pixels, width, height, 325, 185, "HOW TO PLAY", 3, gold);
        FillQuizTvPanel(pixels, width, height, 913, 183, 43, 34,
            chocoRaceHoveredControlTarget == 731 ? 0x64418Eu : 0x21162Fu,
            chocoRaceHoveredControlTarget == 731 ? gold : purple);
        DrawPictionaryPixelTextCentered(pixels, width, height, 934, 192, "X", 2, white);

        DrawPictionaryPixelText(pixels, width, height, 325, 237,
            "CHOOSE CHIPS AND BACK THE CHOCOS YOU LIKE.", 2, white);
        DrawPictionaryPixelText(pixels, width, height, 325, 262,
            chocoRaceTopThreePays ? "FIRST THREE FINISHERS CAN PAY OUT." :
                "FIRST ACROSS THE FINISH LINE WINS.", 2, white);

        var rules = new[]
        {
            "BET ON MULTIPLE CHOCOS IN ONE SPREAD.",
            "PLACE BET LOCKS YOUR SPREAD FOR THE RACE.",
            "FIRST PLACED BET STARTS THE RACE COUNTDOWN.",
            "UNPLACED CHIPS CLEAR WHEN BETTING CLOSES.",
            chocoRaceRefillEnabled
                ? $"BELOW 10 CHIPS? REFILL TO {chocoRaceStartingChips}."
                : "NO REFILL WHEN CHIPS RUN OUT.",
        };
        for (var index = 0; index < rules.Length; index++)
        {
            var y = 299 + index * 29;
            RaceCircle(pixels, width, height, 331, y + 7, 3, gold);
            DrawPictionaryPixelText(pixels, width, height, 346, y, rules[index], 2, muted);
        }

        FillPictionaryPixels(pixels, width, height, 319, 450, 642, 2, gold);
        DrawPictionaryPixelText(pixels, width, height, 325, 463,
            "ODDS PAY PROFIT PLUS YOUR ORIGINAL STAKE.", 2, gold);
        DrawPictionaryPixelText(pixels, width, height, 325, 489,
            chocoRaceTopThreePays ? "2ND PAYS 30 PERCENT OF NORMAL RETURN." :
                "100 AT 1.5:1 = 150 PROFIT + 100 STAKE.", 2, white);
        DrawPictionaryPixelText(pixels, width, height, 325, 515,
            chocoRaceTopThreePays ? "3RD PAYS 15 PERCENT OF NORMAL RETURN." :
                "TOTAL PAID: 250. OTHER CHOCO BETS LOSE.", 2, white);
    }
}

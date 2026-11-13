using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private const int ChocoRaceWinnersPerPage = 12;
    private int chocoRaceResultsWinnersPage;

    private ChocoRacePayout[] ChocoRaceOtherWinners() => chocoRacePayouts
        .Where(payout => payout.PlayerId != LocalChocoRaceUserId && payout.Return > payout.Stake)
        .OrderByDescending(payout => payout.Return - payout.Stake)
        .ThenBy(payout => payout.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private bool TryGetChocoRaceResultsTarget(float x, float y, out int target)
    {
        target = -1;
        var winners = ChocoRaceOtherWinners();
        var maxPage = winners.Length == 0 ? 0 : (winners.Length - 1) / ChocoRaceWinnersPerPage;
        if (y is < 577 or >= 610) return false;
        if (x is >= 907 and < 1002 && chocoRaceResultsWinnersPage > 0)
        {
            target = 600;
            return true;
        }
        if (x is >= 1009 and < 1104 && chocoRaceResultsWinnersPage < maxPage)
        {
            target = 601;
            return true;
        }
        return false;
    }

    private void HandleChocoRaceResultsTarget(int target)
    {
        var winners = ChocoRaceOtherWinners();
        var maxPage = winners.Length == 0 ? 0 : (winners.Length - 1) / ChocoRaceWinnersPerPage;
        if (target == 600) chocoRaceResultsWinnersPage = Math.Max(0, chocoRaceResultsWinnersPage - 1);
        if (target == 601) chocoRaceResultsWinnersPage = Math.Min(maxPage, chocoRaceResultsWinnersPage + 1);
        chocoRaceDirty = true;
    }

    private void DrawChocoRaceResultsOverlayPixels(byte[] pixels)
    {
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        if (chocoRaceRacers.Length < 6) return;
        const uint background = 0x17131E, panel = 0x21162F, gold = 0xF6BC4E;
        const uint white = 0xF5F3FB, muted = 0xB9A9CF, green = 0x65D795;
        var colours = new uint[] { 0xF5CB48, 0x56BFEF, 0xF27B99, 0xF5F4EE, 0xB478E9, 0x474A55 };
        var wings = new uint[] { 0xF9DE79, 0xA9E6FF, 0xFFACBB, 0xFFFFFF, 0xD4A8FA, 0x737887 };
        var places = Enumerable.Range(0, 6)
            .OrderBy(lane => chocoRaceRacers[lane].FinishSeconds)
            .ThenBy(lane => lane).Take(3).ToArray();
        var winners = ChocoRaceOtherWinners();
        var maxPage = winners.Length == 0 ? 0 : (winners.Length - 1) / ChocoRaceWinnersPerPage;
        chocoRaceResultsWinnersPage = Math.Clamp(chocoRaceResultsWinnersPage, 0, maxPage);
        var biggestWin = chocoRacePayouts.Count == 0 ? 0 :
            chocoRacePayouts.Max(payout => payout.Return - payout.Stake);

        FillQuizTvPanel(pixels, width, height, 151, 128, 978, 501, background, 0x8959ED);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 145, "PHOTO FINISH", 5, gold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 188,
            $"RACE {chocoRaceRound:00}  /  THE PODIUM", 2, muted);

        // Gold winner in the middle, with silver and bronze on either side.
        var podiumX = new[] { 381, 640, 899 };
        var podiumOrder = new[] { 1, 0, 2 };
        var podiumHeight = new[] { 62, 84, 48 };
        for (var slot = 0; slot < 3; slot++)
        {
            var position = podiumOrder[slot];
            var lane = places[position];
            var racer = chocoRaceRacers[lane];
            var colour = colours[Math.Clamp(racer.ColourIndex, 0, colours.Length - 1)];
            var wing = wings[Math.Clamp(racer.ColourIndex, 0, wings.Length - 1)];
            var center = podiumX[slot];
            var top = 347 - podiumHeight[slot];
            FillQuizTvPanel(pixels, width, height, center - 101, top, 202,
                podiumHeight[slot], panel, position == 0 ? gold : position == 1 ? 0xC7CEDBu : 0xC98960u);
            DrawPictionaryPixelTextCentered(pixels, width, height, center, top + 11,
                position == 0 ? "1ST" : position == 1 ? "2ND" : "3RD", 3, gold);
            DrawRaceDuck(pixels, width, height, center, top - 1, colour, wing,
                racer.ColourIndex == 5 ? white : 0x232331);
            DrawPictionaryPixelTextCentered(pixels, width, height, center, top - 58,
                $"#{lane + 1} {racer.Name.ToUpperInvariant()}", 2, white);
        }

        var local = chocoRacePayouts.FirstOrDefault(payout => payout.PlayerId == LocalChocoRaceUserId);
        var localNet = local is null ? 0 : local.Return - local.Stake;
        FillQuizTvPanel(pixels, width, height, 181, 359, 918, 51, panel,
            localNet > 0 ? green : gold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 372,
            localNet > 0 ? $"YOU WON +{localNet} CHIPS" : "NO WIN", 3,
            localNet > 0 ? green : gold);
        if (localNet > 0 && localNet == biggestWin) DrawChocoRaceWinnerStar(pixels, 879, 384);

        DrawPictionaryPixelText(pixels, width, height, 184, 422,
            $"OTHER WINNERS  {winners.Length}", 2, gold);
        FillPictionaryPixels(pixels, width, height, 181, 447, 918, 2, 0x8959ED);
        if (winners.Length == 0)
            DrawPictionaryPixelTextCentered(pixels, width, height, 640, 490,
                "NO OTHER WINNERS THIS RACE", 2, muted);
        else
        {
            var first = chocoRaceResultsWinnersPage * ChocoRaceWinnersPerPage;
            for (var index = first; index < Math.Min(first + ChocoRaceWinnersPerPage, winners.Length); index++)
            {
                var slot = index - first;
                var column = slot % 4;
                var row = slot / 4;
                var x = 181 + column * 232;
                var y = 457 + row * 36;
                var payout = winners[index];
                FillQuizTvPanel(pixels, width, height, x, y, 222, 31, panel, 0x67458D);
                if (payout.Return - payout.Stake == biggestWin)
                    DrawChocoRaceWinnerStar(pixels, x + 16, y + 16);
                DrawUsernamePixelText(pixels, width, height, x + 31, y + 8,
                    RaceLedgerText(payout.Name, 10), 2, white,payout.PlayerId);
                DrawPictionaryPixelText(pixels, width, height, x + 153, y + 8,
                    $"+{payout.Return - payout.Stake}", 2, green);
            }
        }
        DrawPictionaryPixelText(pixels, width, height, 184, 590,
            $"PAGE {chocoRaceResultsWinnersPage + 1}/{maxPage + 1}", 2, muted);
        if (maxPage > 0)
        {
            DrawChocoRaceLedgerButton(pixels, 907, 577, 95, 33, "PREV", 600,
                chocoRaceResultsWinnersPage > 0);
            DrawChocoRaceLedgerButton(pixels, 1009, 577, 95, 33, "NEXT", 601,
                chocoRaceResultsWinnersPage < maxPage);
        }
    }

    private static void DrawChocoRaceWinnerStar(byte[] pixels, int x, int y)
    {
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        const uint gold = 0xF6BC4E;
        RaceLine(pixels, width, height, x, y - 9, x + 5, y + 7, gold, 2);
        RaceLine(pixels, width, height, x + 5, y + 7, x - 9, y - 3, gold, 2);
        RaceLine(pixels, width, height, x - 9, y - 3, x + 9, y - 3, gold, 2);
        RaceLine(pixels, width, height, x + 9, y - 3, x - 5, y + 7, gold, 2);
        RaceLine(pixels, width, height, x - 5, y + 7, x, y - 9, gold, 2);
    }
}

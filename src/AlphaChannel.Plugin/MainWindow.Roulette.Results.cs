using AlphaChannel.Plugin.Core;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private DateTime rouletteResultsStartedUtc = DateTime.MinValue;
    private bool RouletteResultsScreenVisible(DateTime now) => roulettePhase == RoulettePhase.Results &&
        rouletteResultsStartedUtc != DateTime.MinValue && now < rouletteResultsStartedUtc.AddSeconds(RouletteResultsSeconds);

    private void DrawRouletteRoundResults(byte[] pixels, int width, int height, DateTime now)
    {
        if (!RouletteResultsScreenVisible(now)) return;
        var results = RouletteRoundResults.Calculate(
            roulettePlayers.Values.Select(player => new RouletteRoundResults.Player(player.Id, player.Name, player.Chips)),
            rouletteBets.Select(bet => new RouletteRoundResults.Bet(bet.PlayerId, bet.Amount)),
            rouletteWinners.Select(winner => new RouletteRoundResults.Payout(winner.PlayerId, winner.Winnings)));
        var bigWinners = results.Where(result => result.BigWinner).ToArray();
        var elapsed = Math.Max(0, (now - rouletteResultsStartedUtc).TotalSeconds);
        const uint ivory = 0xF8F5FF, gold = 0xD6B456;
        FillPictionaryPixels(pixels, width, height, 24, 82, width - 48, height - 96, 0x181123);
        DrawRoulettePrototypeRectOutline(pixels, width, height, 24, 82, width - 48, height - 96, 0x8B55F6, 3);
        DrawPictionaryPixelText(pixels, width, height, 54, 104, $"ROUND {rouletteRoundNumber} RESULTS", 4, gold);
        var number = RoulettePrototypeNumbers[roulettePrototypeResultIndex];
        DrawPictionaryPixelText(pixels, width, height, 884, 111, $"{number} {RouletteColourName(number)}", 3, ivory);

        var tableY = 171;
        if (bigWinners.Length > 0)
        {
            FillPictionaryPixels(pixels, width, height, 44, 158, width - 88, 146, 0x342548);
            DrawRoulettePrototypeRectOutline(pixels, width, height, 44, 158, width - 88, 146, gold, 2);
            // Draw the rain behind the names, keeping it within the celebration section.
            for (var index = 0; index < 28; index++)
            {
                var x = 62 + index * 83 % (width - 124);
                var y = 172 + (int)((elapsed * (52 + index % 5 * 13) + index * 37) % 113);
                FillRoulettePrototypeCircle(pixels, width, height, x, y, 9, 0x9B7139);
                DrawRoulettePrototypeRing(pixels, width, height, x, y, 5, 8, gold);
            }
            DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 171,
                $"BIG WINNERS - {RouletteRoundResults.BigWinProfitMultiplier}X+ PROFIT", 3, gold);
            var featuredPage = (int)(elapsed / 2) % Math.Max(1, (bigWinners.Length + 1) / 2);
            foreach (var (winner, index) in bigWinners.Skip(featuredPage * 2).Take(2).Select((winner, index) => (winner, index)))
            {
                var label = $"{RouletteResultName(winner.Name, 22)}  +{winner.Profit}  ({winner.ProfitMultiplier:0.#}X PROFIT)";
                DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 215 + index * 36, label, 3, ivory);
            }
            tableY = 327;
        }

        DrawPictionaryPixelText(pixels, width, height, 60, tableY, "PLAYER", 2, 0xBCA0FF);
        DrawPictionaryPixelTextCentered(pixels, width, height, 863, tableY, "ROUND PROFIT", 2, 0xBCA0FF);
        DrawPictionaryPixelTextCentered(pixels, width, height, 1100, tableY, "TOTAL CHIPS", 2, 0xBCA0FF);
        var rowsPerPage = Math.Max(1, (height - tableY - 83) / 36);
        var pageCount = Math.Max(1, (results.Length + rowsPerPage - 1) / rowsPerPage);
        var page = (int)(elapsed / 2) % pageCount;
        foreach (var (result, index) in results.Skip(page * rowsPerPage).Take(rowsPerPage).Select((result, index) => (result, index)))
        {
            var y = tableY + 29 + index * 36;
            FillPictionaryPixels(pixels, width, height, 44, y - 5, width - 88, 33, index % 2 == 0 ? 0x25203Au : 0x1E192Eu);
            var refilled = rouletteRefilledPlayerIds.Contains(result.Id);
            DrawUsernamePixelText(pixels, width, height, 60, y, RouletteResultName(result.Name, refilled ? 20 : 30), 3, ivory,result.Id);
            if (refilled)
                DrawPictionaryPixelText(pixels, width, height, 450, y + 4,
                    $"CHIPS RESET TO {rouletteStartingChips}", 2, gold);
            var profitColour = result.Profit > 0 ? 0x24D991u : result.Profit < 0 ? 0xF14C62u : 0xBCA0FFu;
            var profit = result.Profit > 0 ? $"+{result.Profit}" : result.Profit.ToString();
            DrawPictionaryPixelTextCentered(pixels, width, height, 863, y, profit, 3, profitColour);
            DrawPictionaryPixelTextCentered(pixels, width, height, 1100, y, result.Chips.ToString(), 3, gold);
        }
        if (pageCount > 1)
            DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, height - 30,
                $"PAGE {page + 1}/{pageCount}", 2, 0xBCA0FF);
    }

    private static string RouletteResultName(string name, int maxCharacters) =>
        name.Length <= maxCharacters ? name : name[..(maxCharacters - 3)] + "...";
}

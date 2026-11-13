using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private const uint RaceLedgerBackground = 0x17131E;
    private const uint RaceLedgerPanel = 0x21162F;
    private const uint RaceLedgerGold = 0xF6BC4E;
    private const uint RaceLedgerMuted = 0xB9A9CF;
    private const uint RaceLedgerWhite = 0xF5F3FB;
    private const uint RaceLedgerGreen = 0x65D795;
    private const uint RaceLedgerRed = 0xF27B99;
    private const int RaceLedgerRowsPerPage = 7;

    private bool chocoRaceScoreboardOpen;
    private string? chocoRaceHistoryPlayerId;
    private int chocoRaceScoreboardPage;

    private void ResetChocoRaceScoreboard()
    {
        chocoRaceScoreboardOpen = false;
        chocoRaceHistoryPlayerId = null;
        chocoRaceScoreboardPage = 0;
    }

    private ChocoRacePlayer[] ChocoRaceScoreboardPlayers() => chocoRacePlayers.Values
        .OrderByDescending(player => player.Chips)
        .ThenBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private void HandleChocoRaceScoreboardTarget(int target)
    {
        switch (target)
        {
            case 300:
                chocoRaceScoreboardOpen = !chocoRaceScoreboardOpen;
                chocoRaceHistoryPlayerId = null;
                break;
            case 301:
                chocoRaceHistoryPlayerId = null;
                break;
            case 310:
                chocoRaceScoreboardPage = Math.Max(0, chocoRaceScoreboardPage - 1);
                break;
            case 311:
                chocoRaceScoreboardPage++;
                break;
            case >= 400 and < 400 + RaceLedgerRowsPerPage:
            {
                var players = ChocoRaceScoreboardPlayers();
                var index = chocoRaceScoreboardPage * RaceLedgerRowsPerPage + target - 400;
                if (index >= 0 && index < players.Length)
                    chocoRaceHistoryPlayerId = players[index].Id;
                break;
            }
        }
        chocoRaceDirty = true;
    }

    private void DrawChocoRaceScoreboardButton(byte[] pixels)
    {
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        if (chocoRaceHoveredControlTarget == 300)
            FillQuizTvPanel(pixels, width, height, 64, 40, 211, 57,
                RaceLedgerGold, RaceLedgerWhite);
        FillQuizTvPanel(pixels, width, height, 67, 43, 205, 51,
            chocoRaceHoveredControlTarget == 300 ? 0x64418Eu : RaceLedgerPanel,
            chocoRaceHoveredControlTarget == 300 ? RaceLedgerWhite : RaceLedgerGold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 169, 58,
            chocoRaceScoreboardOpen ? "RACE VIEW" : "SCOREBOARD", 3, RaceLedgerGold);
    }

    private void DrawChocoRaceScoreboardPixels(byte[] pixels, DateTime now)
    {
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        FillQuizTvPanel(pixels, width, height, 17, 124, 1246, 568,
            RaceLedgerBackground, 0x8959ED);
        if (chocoRaceHistoryPlayerId is { } playerId)
        {
            DrawChocoRaceHistoryPixels(pixels, playerId);
            return;
        }

        var players = ChocoRaceScoreboardPlayers();
        var maxPage = players.Length == 0 ? 0 : (players.Length - 1) / RaceLedgerRowsPerPage;
        chocoRaceScoreboardPage = Math.Clamp(chocoRaceScoreboardPage, 0, maxPage);
        DrawPictionaryPixelText(pixels, width, height, 43, 148, "RACE LEDGER", 4, RaceLedgerGold);
        DrawPictionaryPixelText(pixels, width, height, 937, 158,
            $"{players.Length} PLAYERS  ROUND {chocoRaceRound}", 2, RaceLedgerMuted);
        FillPictionaryPixels(pixels, width, height, 40, 197, 1200, 2, RaceLedgerGold);

        FillQuizTvPanel(pixels, width, height, 40, 207, 1200, 43, RaceLedgerPanel, RaceLedgerGold);
        DrawPictionaryPixelText(pixels, width, height, 85, 221, "PLAYER", 2, RaceLedgerGold);
        DrawPictionaryPixelText(pixels, width, height, 287, 221, "CHIPS", 2, RaceLedgerGold);
        DrawPictionaryPixelText(pixels, width, height, 420, 221, "LAST BET", 2, RaceLedgerGold);
        DrawPictionaryPixelText(pixels, width, height, 680, 221, "PLACE", 2, RaceLedgerGold);
        DrawPictionaryPixelText(pixels, width, height, 836, 221, "RESULT", 2, RaceLedgerGold);
        DrawPictionaryPixelText(pixels, width, height, 1100, 221, "HISTORY", 2, RaceLedgerGold);
        foreach (var column in new[] { 276, 390, 647, 817, 1080 })
            FillPictionaryPixels(pixels, width, height, column, 209, 1, 39, RaceLedgerGold);

        for (var slot = 0; slot < RaceLedgerRowsPerPage; slot++)
        {
            var playerIndex = chocoRaceScoreboardPage * RaceLedgerRowsPerPage + slot;
            if (playerIndex >= players.Length) break;
            DrawChocoRaceScoreboardRow(pixels, players[playerIndex], slot, now);
        }

        FillPictionaryPixels(pixels, width, height, 40, 629, 1200, 2, RaceLedgerGold);
        var first = players.Length == 0 ? 0 : chocoRaceScoreboardPage * RaceLedgerRowsPerPage + 1;
        var last = Math.Min(players.Length, first + RaceLedgerRowsPerPage - 1);
        DrawPictionaryPixelText(pixels, width, height, 49, 651,
            $"SHOWING {first}-{last} OF {players.Length}  /  CLICK HISTORY FOR LAST 10 BETS", 2,
            RaceLedgerMuted);
        DrawChocoRaceLedgerButton(pixels, 1012, 640, 105, 38, "PREV", 310,
            chocoRaceScoreboardPage > 0);
        DrawChocoRaceLedgerButton(pixels, 1126, 640, 105, 38, "NEXT", 311,
            chocoRaceScoreboardPage < maxPage);
    }

    private void DrawChocoRaceScoreboardRow(byte[] pixels, ChocoRacePlayer player, int slot, DateTime now)
    {
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        var y = 253 + slot * 53;
        FillPictionaryPixels(pixels, width, height, 40, y, 1200, 52,
            slot % 2 == 0 ? 0x211923u : 0x1C1721u);
        FillPictionaryPixels(pixels, width, height, 40, y + 51, 1200, 1, 0x6E5370);
        if (player.Id == LocalChocoRaceUserId)
            FillPictionaryPixels(pixels, width, height, 40, y, 4, 51, RaceLedgerGold);
        FillQuizTvPanel(pixels, width, height, 52, y + 10, 32, 32, RaceLedgerPanel,
            player.Id == LocalChocoRaceUserId ? RaceLedgerGold : 0x8959ED);
        DrawPictionaryPixelTextCentered(pixels, width, height, 68, y + 17,
            RaceLedgerText(player.Name, 1), 2, RaceLedgerWhite);
        DrawUsernamePixelText(pixels, width, height, 93, y + 18,
            RaceLedgerText(player.Name, 14), 2, RaceLedgerWhite,player.Id);
        var latest = chocoRaceLedgerBets.LastOrDefault(bet => bet.PlayerId == player.Id);
        if (latest is null)
        {
            DrawPictionaryPixelText(pixels, width, height, 295, y + 18,
                "WAITING FOR FIRST BET", 2, RaceLedgerMuted);
            DrawChocoRaceLedgerButton(pixels, 1102, y + 8, 116, 35,
                "HISTORY", 400 + slot, true);
            return;
        }

        foreach (var column in new[] { 276, 390, 647, 817, 1080 })
            FillPictionaryPixels(pixels, width, height, column, y + 1, 1, 50, 0x6E5370);
        DrawPictionaryPixelText(pixels, width, height, 289, y + 18,
            player.Chips.ToString(), 2, RaceLedgerGold);
        DrawPictionaryPixelText(pixels, width, height, 407, y + 18,
            $"{latest.Amount} ON #{latest.Lane + 1}", 2, RaceLedgerWhite);
        DrawPictionaryPixelText(pixels, width, height, 662, y + 18,
            ChocoRaceBetPlace(latest, now), 2, RaceLedgerMuted);
        var result = ChocoRaceBetResult(latest);
        DrawPictionaryPixelText(pixels, width, height, 833, y + 18,
            result, 2, latest.Position == 0 ? RaceLedgerMuted :
                latest.Return > 0 ? RaceLedgerGreen : RaceLedgerRed);
        DrawChocoRaceLedgerButton(pixels, 1102, y + 8, 116, 35,
            "HISTORY", 400 + slot, true);
    }

    private void DrawChocoRaceHistoryPixels(byte[] pixels, string playerId)
    {
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        var name = chocoRacePlayers.TryGetValue(playerId, out var player) ? player.Name : "PLAYER";
        DrawUsernamePixelText(pixels, width, height, 43, 148,
            RaceLedgerText(name, 18), 4, RaceLedgerGold,playerId);
        DrawPictionaryPixelText(pixels, width, height, 44, 188,
            "LAST 10 BETS  /  NEWEST FIRST", 2, RaceLedgerMuted);
        DrawChocoRaceLedgerButton(pixels, 1090, 143, 139, 43, "BACK", 301, true);

        FillQuizTvPanel(pixels, width, height, 40, 217, 1200, 38, RaceLedgerPanel, RaceLedgerGold);
        string[] headers = ["ROUND", "BET", "CHOCO", "FINISH", "RESULT", "RETURN"];
        int[] columns = [61, 210, 345, 641, 830, 1087];
        for (var index = 0; index < headers.Length; index++)
            DrawPictionaryPixelText(pixels, width, height, columns[index], 228,
                headers[index], 2, RaceLedgerGold);
        var bets = chocoRaceLedgerBets.Where(bet => bet.PlayerId == playerId)
            .TakeLast(10).Reverse().ToArray();
        if (bets.Length == 0)
        {
            DrawPictionaryPixelTextCentered(pixels, width, height, 640, 420,
                "NO BETS PLACED YET", 3, RaceLedgerMuted);
        }
        for (var index = 0; index < bets.Length; index++)
        {
            var bet = bets[index];
            var y = 258 + index * 37;
            FillPictionaryPixels(pixels, width, height, 40, y, 1200, 36,
                index % 2 == 0 ? 0x211923u : 0x1C1721u);
            FillPictionaryPixels(pixels, width, height, 40, y + 35, 1200, 1, 0x6E5370);
            DrawPictionaryPixelText(pixels, width, height, 61, y + 10, bet.Round.ToString(), 2, RaceLedgerMuted);
            DrawPictionaryPixelText(pixels, width, height, 210, y + 10, bet.Amount.ToString(), 2, RaceLedgerGold);
            DrawPictionaryPixelText(pixels, width, height, 345, y + 10,
                $"#{bet.Lane + 1} {RaceLedgerText(bet.ChocoName, 17)}", 2, RaceLedgerWhite);
            DrawPictionaryPixelText(pixels, width, height, 641, y + 10,
                bet.Position == 0 ? "-" : $"{RaceLedgerPlace(bet.Position)} / 6", 2, RaceLedgerMuted);
            DrawPictionaryPixelText(pixels, width, height, 830, y + 10,
                ChocoRaceBetResult(bet), 2, bet.Position == 0 ? RaceLedgerMuted :
                    bet.Return > 0 ? RaceLedgerGreen : RaceLedgerRed);
            DrawPictionaryPixelText(pixels, width, height, 1087, y + 10,
                bet.Position == 0 ? "-" : bet.Return.ToString(), 2,
                bet.Return > 0 ? RaceLedgerGreen : RaceLedgerMuted);
        }
        FillPictionaryPixels(pixels, width, height, 40, 640, 1200, 2, RaceLedgerGold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 656,
            "EACH CHIP PLACEMENT IS LISTED SEPARATELY", 2, RaceLedgerMuted);
    }

    private void DrawChocoRaceLedgerButton(byte[] pixels, int x, int y, int buttonWidth, int buttonHeight,
        string label, int target, bool enabled)
    {
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        var hovered = enabled && target == chocoRaceHoveredControlTarget;
        if (hovered)
            FillQuizTvPanel(pixels, width, height, x - 2, y - 2, buttonWidth + 4, buttonHeight + 4,
                RaceLedgerGold, RaceLedgerWhite);
        FillQuizTvPanel(pixels, width, height, x, y, buttonWidth, buttonHeight,
            hovered ? 0x64418Eu : RaceLedgerPanel,
            enabled ? hovered ? RaceLedgerWhite : RaceLedgerGold : 0x6E5370);
        DrawPictionaryPixelTextCentered(pixels, width, height, x + buttonWidth / 2,
            y + (buttonHeight - 14) / 2, label, 2,
            hovered ? RaceLedgerWhite : enabled ? RaceLedgerGold : RaceLedgerMuted);
    }

    private string ChocoRaceBetPlace(ChocoRaceLedgerBet bet, DateTime now)
    {
        if (bet.Round == chocoRaceRound && chocoRacePhase == ChocoRacePhase.Racing)
        {
            var order = Enumerable.Range(0, chocoRaceRacers.Length)
                .OrderByDescending(lane => ChocoRaceProgress(lane, now)).ToArray();
            return $"{RaceLedgerPlace(Array.IndexOf(order, bet.Lane) + 1)} LIVE";
        }
        return bet.Position == 0 ? "-" : $"{RaceLedgerPlace(bet.Position)} / 6";
    }

    private static string ChocoRaceBetResult(ChocoRaceLedgerBet bet) => bet.Position == 0
        ? "PENDING"
        : bet.Return > 0 ? $"WON +{bet.Return - bet.Amount}" : $"LOST -{bet.Amount}";

    private static string RaceLedgerPlace(int place) => place switch
    {
        1 => "1ST", 2 => "2ND", 3 => "3RD", _ => $"{place}TH",
    };

    private static string RaceLedgerText(string? value, int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(value)) return "PLAYER";
        var characters = value.ToUpperInvariant().Take(maxCharacters)
            .Select(character => character <= 127 ? character : '?').ToArray();
        return new string(characters);
    }
}

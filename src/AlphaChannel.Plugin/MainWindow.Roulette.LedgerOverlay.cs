namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool rouletteLedgerOverlayOpen;
    private string? rouletteLedgerOverlayHistoryPlayerId;
    private int rouletteLedgerOverlayPage;
    private int rouletteLedgerOverlayDetailPage;

    private void ResetRouletteLedgerOverlay()
    {
        rouletteLedgerOverlayOpen = false;
        rouletteLedgerOverlayHistoryPlayerId = null;
        rouletteLedgerOverlayPage = 0;
        rouletteLedgerOverlayDetailPage = 0;
    }

    private List<RoulettePlayerState> GetRouletteLedgerOverlayPlayers() => roulettePlayers.Values
        .OrderByDescending(player => player.Id == LocalRouletteUserId)
        .ThenByDescending(player => EnsureRouletteLedgerPlayer(player.Id).HasJoinedTable)
        .ThenBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private void HandleRouletteLedgerOverlayTarget(int target)
    {
        switch (target)
        {
            case 114:
                rouletteLedgerOverlayOpen = !rouletteLedgerOverlayOpen;
                if (rouletteLedgerOverlayOpen)
                {
                    rouletteHelpOpen = false;
                    rouletteSettingsOpen = false;
                }
                else rouletteLedgerOverlayHistoryPlayerId = null;
                break;
            case 115:
                ResetRouletteLedgerOverlay();
                break;
            case 116:
                if (rouletteLedgerOverlayHistoryPlayerId is null)
                    rouletteLedgerOverlayPage = Math.Max(0, rouletteLedgerOverlayPage - 1);
                else rouletteLedgerOverlayDetailPage = Math.Max(0, rouletteLedgerOverlayDetailPage - 1);
                break;
            case 117:
                if (rouletteLedgerOverlayHistoryPlayerId is null)
                    rouletteLedgerOverlayPage = Math.Min((GetRouletteLedgerOverlayPlayers().Count - 1) / 6,
                        rouletteLedgerOverlayPage + 1);
                else if (rouletteLedger.TryGetValue(rouletteLedgerOverlayHistoryPlayerId, out var detail))
                    rouletteLedgerOverlayDetailPage = Math.Min((detail.RoundDetails.Length - 1) / 8,
                        rouletteLedgerOverlayDetailPage + 1);
                break;
            case 130:
                rouletteLedgerOverlayHistoryPlayerId = null;
                rouletteLedgerOverlayDetailPage = 0;
                break;
            default:
                if (target is >= 120 and < 126)
                {
                    var index = rouletteLedgerOverlayPage * 6 + target - 120;
                    var players = GetRouletteLedgerOverlayPlayers();
                    if (index < players.Count)
                    {
                        rouletteLedgerOverlayHistoryPlayerId = players[index].Id;
                        rouletteLedgerOverlayDetailPage = 0;
                    }
                }
                break;
        }
        whiteboardTvDirty = true;
    }

    private bool TryGetRouletteLedgerOverlayTarget(float x, float y, out int target)
    {
        target = -1;
        if (!rouletteLedgerOverlayOpen) return false;
        if (x is >= 1125 and < 1180 && y is >= 125 and < 172) { target = 115; return true; }
        if (rouletteLedgerOverlayHistoryPlayerId is not null &&
            x is >= 104 and < 244 && y is >= 127 and < 170) { target = 130; return true; }
        if (y is >= 581 and < 621)
        {
            var itemCount = rouletteLedgerOverlayHistoryPlayerId is null
                ? GetRouletteLedgerOverlayPlayers().Count
                : rouletteLedger.GetValueOrDefault(rouletteLedgerOverlayHistoryPlayerId)?.RoundDetails.Length ?? 0;
            var pageSize = rouletteLedgerOverlayHistoryPlayerId is null ? 6 : 8;
            var lastPage = Math.Max(0, (itemCount - 1) / pageSize);
            var currentPage = rouletteLedgerOverlayHistoryPlayerId is null
                ? rouletteLedgerOverlayPage : rouletteLedgerOverlayDetailPage;
            if (currentPage > 0 && x is >= 980 and < 1060) { target = 116; return true; }
            if (currentPage < lastPage && x is >= 1080 and < 1160) { target = 117; return true; }
        }
        if (rouletteLedgerOverlayHistoryPlayerId is null &&
            x is >= 1103 and < 1165 && y is >= 219 and < 561)
        {
            var slot = (int)((y - 219) / 57);
            if (slot is >= 0 and < 6 &&
                rouletteLedgerOverlayPage * 6 + slot < GetRouletteLedgerOverlayPlayers().Count)
            {
                target = 120 + slot;
                return true;
            }
        }
        return false;
    }

    private void DrawRouletteLedgerOverlay(byte[] pixels, int width, int height)
    {
        const uint gold = 0xD6B456, white = 0xF8F5FF, purple = 0x8B55F6;
        const int buttonX = 606, buttonY = 14, buttonW = 168, buttonH = 42;
        var buttonHovered = rouletteHoveredTarget == 114 || roulettePageHoveredTarget == 114;
        FillPictionaryPixels(pixels, width, height, buttonX, buttonY, buttonW, buttonH,
            buttonHovered || rouletteLedgerOverlayOpen ? 0x4A2B75u : 0x251A38u);
        DrawRoulettePrototypeRectOutline(pixels, width, height, buttonX, buttonY, buttonW, buttonH,
            buttonHovered || rouletteLedgerOverlayOpen ? purple : gold, 2);
        DrawPictionaryPixelTextCentered(pixels, width, height, buttonX + buttonW / 2, buttonY + 12,
            "LEDGER", 3, white);
        if (!rouletteLedgerOverlayOpen) return;

        FillPictionaryPixels(pixels, width, height, 80, 112, 1120, 520, 0x073321);
        DrawRoulettePrototypeRectOutline(pixels, width, height, 80, 112, 1120, 520, purple, 3);
        FillPictionaryPixels(pixels, width, height, 83, 115, 1114, 61, 0x181123);
        DrawPictionaryPixelText(pixels, width, height, 106, 133,
            rouletteLedgerOverlayHistoryPlayerId is null ? "TABLE LEDGER" : "ROUND DETAILS", 4, white);
        DrawRouletteLedgerOverlayButton(pixels, width, height, 1125, 125, 55, 47, "X", 115);

        if (rouletteLedgerOverlayHistoryPlayerId is null)
            DrawRouletteLedgerOverlayRows(pixels, width, height);
        else DrawRouletteLedgerOverlayHistory(pixels, width, height);
    }

    private void DrawRouletteLedgerOverlayRows(byte[] pixels, int width, int height)
    {
        const uint gold = 0xD6B456, white = 0xF8F5FF, muted = 0xBCA0FF;
        var players = GetRouletteLedgerOverlayPlayers();
        var pageCount = Math.Max(1, (players.Count + 5) / 6);
        rouletteLedgerOverlayPage = Math.Clamp(rouletteLedgerOverlayPage, 0, pageCount - 1);
        DrawPictionaryPixelText(pixels, width, height, 850, 140,
            $"{players.Count} PLAYERS  ROUND {rouletteRoundNumber}", 2, muted);
        FillPictionaryPixels(pixels, width, height, 101, 183, 1078, 36, 0x05291C);
        DrawRoulettePrototypeRectOutline(pixels, width, height, 101, 183, 1078, 36, gold, 1);
        DrawPictionaryPixelText(pixels, width, height, 125, 194, "PLAYER", 2, gold);
        DrawPictionaryPixelText(pixels, width, height, 385, 194, "CHIPS", 2, gold);
        DrawPictionaryPixelText(pixels, width, height, 555, 194, "LAST BET", 2, gold);
        DrawPictionaryPixelText(pixels, width, height, 825, 194, "LAST CHANGE", 2, gold);
        DrawPictionaryPixelText(pixels, width, height, 1105, 194, "INFO", 2, gold);

        for (var slot = 0; slot < 6; slot++)
        {
            var index = rouletteLedgerOverlayPage * 6 + slot;
            if (index >= players.Count) break;
            var player = players[index];
            var ledger = EnsureRouletteLedgerPlayer(player.Id);
            var y = 219 + slot * 57;
            FillPictionaryPixels(pixels, width, height, 101, y, 1078, 57,
                slot % 2 == 0 ? 0x0B402Bu : 0x073824u);
            FillPictionaryPixels(pixels, width, height, 101, y + 56, 1078, 1, 0x77632Fu);
            DrawUsernamePixelText(pixels, width, height, 121, y + 17,
                ClipRouletteLedgerOverlayText(player.Name, 18), 2, white,player.Id);
            if (player.Id == LocalRouletteUserId)
                DrawPictionaryPixelText(pixels, width, height, 325, y + 17, "YOU", 2, muted);
            DrawPictionaryPixelText(pixels, width, height, 385, y + 17,
                ledger.HasJoinedTable ? player.Chips.ToString() : "WAITING", 2, gold);
            if (ledger.HasJoinedTable)
            {
                DrawPictionaryPixelText(pixels, width, height, 555, y + 17,
                    ClipRouletteLedgerOverlayText(ledger.LastBet, 21), 2, white);
                var changeColour = ledger.ChangeKind switch
                {
                    RouletteLedgerChangeKind.Win or RouletteLedgerChangeKind.TransferIn => 0x24D991u,
                    RouletteLedgerChangeKind.Loss or RouletteLedgerChangeKind.Bet or
                        RouletteLedgerChangeKind.TransferOut => 0xF14C62u,
                    _ => gold,
                };
                DrawPictionaryPixelText(pixels, width, height, 825, y + 17,
                    ClipRouletteLedgerOverlayText(ledger.ChangeText, 22), 2, changeColour);
            }
            var hover = rouletteHoveredTarget == 120 + slot || roulettePageHoveredTarget == 120 + slot;
            DrawRouletteLedgerOverlayButton(pixels, width, height, 1105, y + 7, 58, 42,
                "LOG", 120 + slot, hover);
        }
        DrawPictionaryPixelText(pixels, width, height, 105, 593,
            $"PAGE {rouletteLedgerOverlayPage + 1}/{pageCount}", 2, muted);
        if (rouletteLedgerOverlayPage > 0)
            DrawRouletteLedgerOverlayButton(pixels, width, height, 980, 581, 80, 40, "PREV", 116);
        if (rouletteLedgerOverlayPage + 1 < pageCount)
            DrawRouletteLedgerOverlayButton(pixels, width, height, 1080, 581, 80, 40, "NEXT", 117);
    }

    private void DrawRouletteLedgerOverlayHistory(byte[] pixels, int width, int height)
    {
        const uint gold = 0xD6B456, white = 0xF8F5FF, muted = 0xBCA0FF;
        var player = roulettePlayers.GetValueOrDefault(rouletteLedgerOverlayHistoryPlayerId!);
        var ledger = rouletteLedger.GetValueOrDefault(rouletteLedgerOverlayHistoryPlayerId!);
        if (player is null || ledger is null)
        {
            DrawPictionaryPixelText(pixels, width, height, 110, 214, "PLAYER NO LONGER AT TABLE", 3, white);
            return;
        }
        DrawRouletteLedgerOverlayButton(pixels, width, height, 104, 127, 140, 43, "BACK", 130);
        DrawUsernamePixelText(pixels, width, height, 105, 190,
            ClipRouletteLedgerOverlayText(player.Name, 25), 3, gold,player.Id);
        DrawPictionaryPixelText(pixels, width, height, 720, 194,
            $"ROUND {ledger.DetailRound}  CHIPS {player.Chips}", 2, muted);
        var pageCount = Math.Max(1, (ledger.RoundDetails.Length + 7) / 8);
        if (ledger.RoundDetails.Length == 0)
            DrawPictionaryPixelTextCentered(pixels, width, height, 640, 344,
                "NO COMPLETED ROUND DETAILS YET", 3, white);
        else
        {
            rouletteLedgerOverlayDetailPage = Math.Clamp(rouletteLedgerOverlayDetailPage, 0, pageCount - 1);
            for (var slot = 0; slot < 8; slot++)
            {
                var index = rouletteLedgerOverlayDetailPage * 8 + slot;
                if (index >= ledger.RoundDetails.Length) break;
                var detail = ledger.RoundDetails[index];
                var y = 229 + slot * 43;
                FillPictionaryPixels(pixels, width, height, 105, y, 1070, 40,
                    slot % 2 == 0 ? 0x0B402Bu : 0x073824u);
                DrawPictionaryPixelText(pixels, width, height, 125, y + 11,
                    $"{detail.Amount} ON {ClipRouletteLedgerOverlayText(detail.Target, 28)}", 2, white);
                DrawPictionaryPixelText(pixels, width, height, 835, y + 11,
                    detail.Won ? $"WON +{detail.Payout}" : $"LOST -{detail.Amount}", 2,
                    detail.Won ? 0x24D991u : 0xF14C62u);
            }
            DrawPictionaryPixelText(pixels, width, height, 105, 593,
                $"PAGE {rouletteLedgerOverlayDetailPage + 1}/{pageCount}", 2, muted);
        }
        var totalBet = ledger.RoundDetails.Sum(detail => detail.Amount);
        var totalPayout = ledger.RoundDetails.Sum(detail => detail.Payout);
        DrawPictionaryPixelText(pixels, width, height, 360, 593,
            $"BET {totalBet}  RETURN {totalPayout}", 2, gold);
        if (rouletteLedgerOverlayDetailPage > 0)
            DrawRouletteLedgerOverlayButton(pixels, width, height, 980, 581, 80, 40, "PREV", 116);
        if (rouletteLedgerOverlayDetailPage + 1 < pageCount)
            DrawRouletteLedgerOverlayButton(pixels, width, height, 1080, 581, 80, 40, "NEXT", 117);
    }

    private void DrawRouletteLedgerOverlayButton(byte[] pixels, int width, int height,
        int x, int y, int w, int h, string label, int target, bool forcedHover = false)
    {
        var hovered = forcedHover || rouletteHoveredTarget == target || roulettePageHoveredTarget == target;
        FillPictionaryPixels(pixels, width, height, x, y, w, h,
            hovered ? 0x4A2B75u : 0x181123u);
        DrawRoulettePrototypeRectOutline(pixels, width, height, x, y, w, h,
            hovered ? 0xBCA0FFu : 0xD6B456u, 2);
        DrawPictionaryPixelTextCentered(pixels, width, height, x + w / 2, y + (h - 16) / 2,
            label, 2, 0xF8F5FF);
    }

    private static string ClipRouletteLedgerOverlayText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        var text = value.ToUpperInvariant();
        return text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";
    }
}

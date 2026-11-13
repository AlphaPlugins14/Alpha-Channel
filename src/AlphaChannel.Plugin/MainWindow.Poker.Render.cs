namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private const uint PokerNight = 0x130D20;
    private const uint PokerPlum = 0x271B36;
    private const uint PokerPanel = 0x30243F;
    private const uint PokerGold = 0xF3C66E;
    private const uint PokerBrass = 0xA8793F;
    private const uint PokerCream = 0xFFF0D2;
    private const uint PokerMuted = 0xCBB9D8;
    private const uint PokerCyan = 0x8BE4E3;
    private const uint PokerFelt = 0x07543F;
    private const uint PokerFeltDark = 0x04382D;
    private const uint PokerCard = 0xFFF4DE;
    private const uint PokerRed = 0xAE3950;
    private const uint PokerInk = 0x292139;

    private void ApplyPokerTvOverlay(byte[] pixels, int width, int height, bool showPrivate = false)
    {
        // TVs are rendered locally: show only this account's private hand.
        // PokerVisibleSeatCards still hides every opponent until showdown.
        var hasLocalPrivateCards = pokerLocalCardsHand == pokerHand &&
            pokerLocalFirst >= 0 && pokerLocalSecond >= 0;
        var soloPrivate = PokerSoloPrivateCardsVisible;
        var showLocalCards = showPrivate || hasLocalPrivateCards;
        FillPictionaryPixels(pixels, width, height, 0, 0, width, height, PokerNight);
        for (var y = 0; y < height; y += 36)
            FillPictionaryPixels(pixels, width, height, 0, y, width, 13, 0x1A1029);

        DrawRoulettePrototypeRectOutline(pixels, width, height, 8, 8, width - 16, height - 16, PokerBrass, 4);
        DrawRoulettePrototypeRectOutline(pixels, width, height, 19, 19, width - 38, height - 38, PokerGold, 2);
        for (var x = 42; x < width - 20; x += 35)
        {
            FillRoulettePrototypeCircle(pixels, width, height, x, 20, 3, PokerGold);
            FillRoulettePrototypeCircle(pixels, width, height, x, height - 20, 3, PokerGold);
        }
        for (var y = 48; y < height - 30; y += 33)
        {
            FillRoulettePrototypeCircle(pixels, width, height, 20, y, 3, PokerGold);
            FillRoulettePrototypeCircle(pixels, width, height, width - 20, y, 3, PokerGold);
        }
        DrawPokerSuit(pixels, width, height, 66, 228, 12, 'D', 0x85345F);
        DrawPokerSuit(pixels, width, height, 1211, 228, 12, 'H', 0x85345F);
        DrawPokerSuit(pixels, width, height, 67, 404, 12, 'C', 0x684076);
        DrawPokerSuit(pixels, width, height, 1212, 404, 12, 'S', 0x684076);

        FillQuizTvPanel(pixels, width, height, 33, 37, 1214, 55, PokerPlum, PokerGold);
        DrawPictionaryPixelText(pixels, width, height, 56, 55, "ALPHA CHANNEL", 3, PokerCream);
        DrawSlotPixelDiamond(pixels, width, height, 304, 64, 8, 13, PokerGold);
        DrawPictionaryPixelText(pixels, width, height, 332, 46, "POKER EX", 6, PokerGold);
        DrawPictionaryPixelText(pixels, width, height, 739, 57, "TEXAS HOLD'EM", 2, PokerMuted);
        if (pokerPhase is PokerPhase.Countdown or PokerPhase.Playing)
            DrawPictionaryPixelText(pixels, width, height, 971, 57,
                $"{PokerSecondsLeft:00} SEC", 2, PokerGold);
        FillQuizTvPanel(pixels, width, height, 1116, 47, 45, 37, 0x17303D, PokerBrass);
        FillQuizTvPanel(pixels, width, height, 1173, 47, 45, 37, 0x17303D, PokerBrass);
        DrawPictionaryPixelTextCentered(pixels, width, height, 1139, 53, "?", 3, PokerCream);
        DrawPokerGear(pixels, width, height, 1195, 65);

        FillPokerEllipse(pixels, width, height, 640, 388, 432, 188, PokerGold);
        FillPokerEllipse(pixels, width, height, 640, 388, 425, 181, PokerBrass);
        FillPokerEllipse(pixels, width, height, 640, 388, 417, 173, PokerFeltDark);
        FillPokerEllipse(pixels, width, height, 640, 388, 410, 166, PokerFelt);
        DrawPokerEllipseOutline(pixels, width, height, 640, 388, 383, 148, 0x126B53);
        DrawPokerEllipseOutline(pixels, width, height, 640, 388, 357, 130, 0x0B614A);
        foreach (var (x, y) in new[] { (340, 367), (946, 367), (343, 478), (943, 478) })
            DrawSlotPixelDiamond(pixels, width, height, x, y, 6, 10, 0x167459);
        FillRoulettePrototypeCircle(pixels, width, height, 354, 407, 20, 0x126B53);
        FillRoulettePrototypeCircle(pixels, width, height, 926, 407, 20, 0x126B53);
        DrawSlotPixelDiamond(pixels, width, height, 354, 407, 8, 12, 0x238564);
        DrawSlotPixelDiamond(pixels, width, height, 926, 407, 8, 12, 0x238564);
        DrawPictionaryPixelTextCentered(pixels, width, height, 354, 439, "GOLD SAUCER", 1, 0x238564);
        DrawPictionaryPixelTextCentered(pixels, width, height, 926, 439, "GOLD SAUCER", 1, 0x238564);

        FillQuizTvPanel(pixels, width, height, 460, 224, 230, 51, PokerPlum, PokerGold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 575, 238, "HOUSE DEALER", 3, PokerGold);
        FillQuizTvPanel(pixels, width, height, 700, 229, 157, 41, 0x17303D, PokerBrass);
        DrawPictionaryPixelTextCentered(pixels, width, height, 779, 243, $"HAND {pokerHand:00}", 2, PokerCyan);

        DrawPokerChip(pixels, width, height, 411, 321, 16, 0xAE4173);
        DrawPokerChip(pixels, width, height, 438, 327, 16, 0x347B9C);
        DrawPokerChip(pixels, width, height, 842, 327, 16, 0x347B9C);
        DrawPokerChip(pixels, width, height, 869, 321, 16, 0xAE4173);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 306, $"POT {pokerPot:N0} CHIPS", 4, PokerGold);
        FillPictionaryPixels(pixels, width, height, 430, 345, 420, 2, PokerBrass);
        for (var index = 0; index < 5 && !CanDealPokerFromLobby; index++)
        {
            var cardX = 424 + index * 87;
            if (index < pokerBoard.Count)
            {
                var elapsed = (DateTime.UtcNow - pokerRevealUtc).TotalSeconds -
                    (index >= pokerBoard.Count - (pokerBoard.Count == 3 ? 3 : 1) ?
                        (index - (pokerBoard.Count == 3 ? 0 : pokerBoard.Count - 1)) * .12 : -1);
                if (elapsed is >= 0 and < .18)
                    DrawPokerCardBack(pixels, width, height, cardX + 25, 359, 26, 108);
                else
                    DrawPokerPixelCard(pixels, width, height, cardX, 359, 76, 108,
                        PokerRank(pokerBoard[index]), PokerSuit(pokerBoard[index]), false);
            }
            else DrawPokerCardBack(pixels, width, height, cardX, 359, 76, 108);
        }
        if (CanDealPokerFromLobby)
        {
            FillQuizTvPanel(pixels, width, height, PokerDealButtonX, PokerDealButtonY,
                PokerDealButtonWidth, PokerDealButtonHeight, 0x7040A8, PokerGold);
            DrawRoulettePrototypeRectOutline(pixels, width, height,
                PokerDealButtonX + 4, PokerDealButtonY + 4,
                PokerDealButtonWidth - 8, PokerDealButtonHeight - 8, PokerGold, 2);
            DrawPictionaryPixelTextCentered(pixels, width, height, 640,
                PokerDealButtonY + 28, "DEAL CARDS", 4, PokerCream);
        }
        if (soloPrivate && LocalPokerPlayer?.InHand == true && pokerLocalCardsHand == pokerHand &&
            pokerLocalFirst >= 0 && pokerLocalSecond >= 0)
        {
            FillQuizTvPanel(pixels, width, height, 256, 346, 161, 122, PokerPlum, PokerGold);
            DrawPictionaryPixelTextCentered(pixels, width, height, 336, 352, "YOUR HAND", 1, PokerGold);
            DrawPokerPixelCard(pixels, width, height, 267, 367, 65, 91,
                PokerRank(pokerLocalFirst), PokerSuit(pokerLocalFirst), false);
            DrawPokerPixelCard(pixels, width, height, 341, 367, 65, 91,
                PokerRank(pokerLocalSecond), PokerSuit(pokerLocalSecond), false);
        }

        var localTurn = LocalPokerPlayer?.Seat == pokerActingSeat && pokerPhase == PokerPhase.Playing;
        var owed = Math.Max(0, pokerCurrentBet - (LocalPokerPlayer?.Bet ?? 0));
        if (localTurn && !hasLocalPrivateCards)
        {
            FillQuizTvPanel(pixels, width, height, 390, 487, 495, 42, PokerPlum, PokerGold);
            DrawPictionaryPixelTextCentered(pixels, width, height, 637, 498,
                "WAITING FOR YOUR PRIVATE CARDS", 2, PokerCream);
        }
        else if (localTurn)
        {
            var canRaise = LocalPokerPlayer is { } me && !me.Acted && me.Stack >= owed + pokerLastRaise;
            var canAllIn = LocalPokerPlayer is { } player && (!player.Acted || player.Stack <= owed);
            DrawPokerActionButton(pixels, width, height, 390, 487, "FOLD", 0x713549, 120);
            DrawPokerActionButton(pixels, width, height, 515, 487, owed == 0 ? "CHECK" : $"CALL {owed}", 0x5A344A, 120);
            DrawPokerActionButton(pixels, width, height, 640, 487,
                pokerCurrentBet == 0 ? $"BET {pokerLastRaise}" : $"RAISE +{pokerLastRaise}",
                canRaise ? 0x5A344Au : PokerPanel, 120);
            DrawPokerActionButton(pixels, width, height, 765, 487, "ALL IN",
                canAllIn ? 0x5A344Au : PokerPanel, 120);
            DrawPictionaryPixelTextCentered(pixels, width, height, 640, 534,
                "YOUR CARDS ARE AT YOUR SEAT",
                1, PokerCream);
        }
        else
        {
            FillQuizTvPanel(pixels, width, height, 390, 487, 495, 42, PokerPlum, PokerGold);
            var status = pokerPhase == PokerPhase.Countdown ? $"DEALING IN {PokerSecondsLeft}" :
                pokerPhase == PokerPhase.Playing ? $"{pokerMessage} - {PokerSecondsLeft} SEC" : pokerMessage;
            DrawPictionaryPixelTextCentered(pixels, width, height, 637, 498, status.ToUpperInvariant(), 2, PokerCream);
        }
        FillQuizTvPanel(pixels, width, height, 238, 548, 804, 30, PokerPlum, PokerBrass);
        DrawPictionaryPixelTextCentered(pixels, width, height, 366, 556, $"{pokerPlayers.Count(p => p.Id != PokerBotId)} / 16 SEATS FILLED", 2, PokerCyan);
        DrawSlotPixelDiamond(pixels, width, height, 532, 563, 5, 8, PokerGold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 556, $"BLINDS {PokerSmallBlind} / {PokerBigBlind}", 2, PokerCream);
        DrawSlotPixelDiamond(pixels, width, height, 746, 563, 5, 8, PokerGold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 898, 556,
            pokerPhase == PokerPhase.Playing ? pokerStreet.ToString().ToUpperInvariant() : pokerMessage.ToUpperInvariant(), 2, PokerMuted);

        for (var index = 0; index < 6; index++)
            DrawPokerPixelSeat(pixels, width, height, 52 + index * 198, 105, 188, 91, index, showLocalCards);
        DrawPokerPixelSeat(pixels, width, height, 27, 265, 205, 91, 6, showLocalCards);
        DrawPokerPixelSeat(pixels, width, height, 27, 431, 205, 91, 7, showLocalCards);
        DrawPokerPixelSeat(pixels, width, height, 1048, 265, 205, 91, 8, showLocalCards);
        DrawPokerPixelSeat(pixels, width, height, 1048, 431, 205, 91, 9, showLocalCards);
        for (var index = 0; index < 6; index++)
            DrawPokerPixelSeat(pixels, width, height, 52 + index * 198, 590, 188, 91, index + 10, showLocalCards);
        if (pokerPreviewOverlay != PokerPreviewOverlay.None)
            DrawPokerPreviewOverlay(pixels, width, height);
    }

    private void DrawPokerPixelSeat(byte[] pixels, int width, int height,
        int x, int y, int seatWidth, int seatHeight, int index, bool showPrivate)
    {
        var seat = pokerPlayers.FirstOrDefault(p => p.Seat == index);
        var active = index == pokerActingSeat;
        var occupied = seat is not null;
        FillQuizTvPanel(pixels, width, height, x, y, seatWidth, seatHeight,
            active ? 0x493251u : PokerPanel, active ? PokerGold : PokerBrass);
        FillPictionaryPixels(pixels, width, height, x + 8, y + 33, seatWidth - 16, 1, PokerBrass);
        var label = occupied ? $"{index + 1:00} {seat!.Name.ToUpperInvariant()}" : $"{index + 1:00} OPEN SEAT";
        DrawPictionaryPixelText(pixels, width, height, x + 9, y + 9, label, label.Length > 10 ? 1 : 2,
            occupied ? PokerCream : PokerMuted);
        if (!occupied)
        {
            DrawPictionaryPixelText(pixels, width, height, x + 12, y + 54,
                LocalPokerPlayer is null ? "SIT TO PLAY" : "OPEN SEAT", 2, PokerMuted);
            DrawSlotPixelDiamond(pixels, width, height, x + seatWidth - 25, y + 65,
                9, 13, PokerGold);
            return;
        }
        var stack = seat!.Stack.ToString();
        DrawPictionaryPixelText(pixels, width, height,
            x + seatWidth - 10 - stack.Length * 12, y + 9, stack, 2, PokerCyan);
        if (PokerVisibleSeatCards(seat, showPrivate) is { } cards)
        {
            DrawPokerPixelCard(pixels, width, height, x + 10, y + 43, 31, 39,
                PokerRank(cards.First), PokerSuit(cards.First), true);
            DrawPokerPixelCard(pixels, width, height, x + 47, y + 43, 31, 39,
                PokerRank(cards.Second), PokerSuit(cards.Second), true);
        }
        else if (seat.InHand)
        {
            DrawPokerCardBack(pixels, width, height, x + 10, y + 43, 31, 39);
            DrawPokerCardBack(pixels, width, height, x + 47, y + 43, 31, 39);
        }
        DrawPictionaryPixelText(pixels, width, height, x + 86, y + 47, "BET " + seat.Bet, 2, PokerGold);
        var state = active ? "YOUR TURN" : seat.State;
        DrawPictionaryPixelText(pixels, width, height, x + 86, y + 68, state,
            state.Length > 7 ? 1 : 2,
            active ? PokerGold : PokerMuted);
        if (index == pokerButton)
        {
            FillRoulettePrototypeCircle(pixels, width, height, x + seatWidth - 17, y + 72, 11, PokerGold);
            DrawPictionaryPixelTextCentered(pixels, width, height,
                x + seatWidth - 17, y + 65, "D", 2, PokerPlum);
        }
        if (active)
            DrawSlotPixelTriangle(pixels, width, height,
                x + seatWidth / 2 - 9, y - 8,
                x + seatWidth / 2 + 9, y - 8,
                x + seatWidth / 2, y - 1, PokerGold);
    }

    private static string PokerRank(int card)
    {
        if (card < 0) return "?";
        var rank = card % 13 + 2;
        return rank switch
        {
            14 => "A", 13 => "K", 12 => "Q", 11 => "J", 10 => "10", var n => n.ToString(),
        };
    }
    private static char PokerSuit(int card) => card < 0 ? 'S' : "SHDC"[card / 13];

    private static void DrawPokerPixelCard(byte[] pixels, int width, int height,
        int x, int y, int cardWidth, int cardHeight, string rank, char suit, bool small)
    {
        FillQuizTvPanel(pixels, width, height, x, y, cardWidth, cardHeight, PokerCard, PokerGold);
        var colour = suit is 'H' or 'D' ? PokerRed : PokerInk;
        if (small)
        {
            DrawPictionaryPixelText(pixels, width, height, x + 3, y + 3, rank, rank.Length == 2 ? 1 : 2, colour);
            DrawPokerSuit(pixels, width, height, x + cardWidth / 2, y + 29, 5, suit, colour);
        }
        else
        {
            DrawPictionaryPixelText(pixels, width, height, x + 7, y + 7, rank, 3, colour);
            var suitRadius = Math.Clamp(cardWidth / 6, 9, 12);
            DrawPokerSuit(pixels, width, height, x + cardWidth / 2,
                y + cardHeight / 2 - 2, suitRadius, suit, colour);
            DrawPictionaryPixelText(pixels, width, height,
                x + cardWidth - 7 - rank.Length * 18, y + cardHeight - 29, rank, 3, colour);
        }
    }

    private static void DrawPokerCardBack(byte[] pixels, int width, int height,
        int x, int y, int cardWidth, int cardHeight)
    {
        FillQuizTvPanel(pixels, width, height, x, y, cardWidth, cardHeight, 0x5A243E, PokerGold);
        DrawRoulettePrototypeRectOutline(pixels, width, height,
            x + 3, y + 3, cardWidth - 6, cardHeight - 6, PokerCream, 1);
        for (var py = y + 5; py < y + cardHeight - 5; py++)
        for (var px = x + 5; px < x + cardWidth - 5; px++)
            if ((px + py) % 10 < 3)
                FillPictionaryPixels(pixels, width, height, px, py, 1, 1, 0xBE5664);
        DrawSlotPixelDiamond(pixels, width, height,
            x + cardWidth / 2, y + cardHeight / 2, 5, 7, PokerGold);
    }

    private static void DrawPokerActionButton(byte[] pixels, int width, int height,
        int x, int y, string label, uint fill, int buttonWidth = 148)
    {
        FillQuizTvPanel(pixels, width, height, x, y, buttonWidth, 42, fill, PokerGold);
        DrawPictionaryPixelTextCentered(pixels, width, height,
            x + buttonWidth / 2, y + (label.Length > 6 ? 14 : 11),
            label, label.Length > 6 ? 2 : 3, PokerCream);
    }

    private static void DrawPokerGear(byte[] pixels, int width, int height, int x, int y)
    {
        FillRoulettePrototypeCircle(pixels, width, height, x, y, 11, PokerGold);
        for (var angle = 0; angle < 360; angle += 45)
        {
            var radians = angle * Math.PI / 180d;
            var cx = x + (int)(13 * Math.Cos(radians));
            var cy = y + (int)(13 * Math.Sin(radians));
            FillPictionaryPixels(pixels, width, height, cx - 2, cy - 2, 5, 5, PokerGold);
        }
        FillRoulettePrototypeCircle(pixels, width, height, x, y, 5, 0x17303D);
    }

    private void DrawPokerPreviewOverlay(byte[] pixels, int width, int height)
    {
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = (byte)(pixels[offset] * .36f);
            pixels[offset + 1] = (byte)(pixels[offset + 1] * .36f);
            pixels[offset + 2] = (byte)(pixels[offset + 2] * .36f);
        }
        FillQuizTvPanel(pixels, width, height, 335, 170, 610, 360, PokerPlum, PokerGold);
        DrawRoulettePrototypeRectOutline(pixels, width, height,
            344, 179, 592, 342, PokerBrass, 2);
        FillQuizTvPanel(pixels, width, height, 873, 184, 40, 35, 0x693346, PokerGold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 893, 192, "X", 3, PokerCream);

        if (pokerPreviewOverlay == PokerPreviewOverlay.Help)
        {
            DrawPictionaryPixelTextCentered(pixels, width, height,
                640, 215, "POKER TABLE HELP", 4, PokerGold);
            DrawPictionaryPixelText(pixels, width, height, 393, 286,
                "16 SEATS - EMPTY SEATS ARE SKIPPED", 2, PokerCream);
            DrawPictionaryPixelText(pixels, width, height, 393, 327,
                "HOLE CARDS USE RED STRIPED BACKS", 2, PokerCream);
            DrawPictionaryPixelText(pixels, width, height, 393, 368,
                "CHECK, CALL, RAISE, FOLD OR ALL IN", 2, PokerCream);
            DrawPictionaryPixelText(pixels, width, height, 393, 409,
                $"5 SECOND DEAL - {PokerTurnSeconds} SECOND TURNS", 2, PokerCream);
            DrawPictionaryPixelTextCentered(pixels, width, height, 640, 479,
                pokerBotEnabled ? "ONE PLAYER FACES THE HOUSE BOT" : "TWO HUMAN PLAYERS REQUIRED", 2, PokerMuted);
        }
        else
        {
            DrawPictionaryPixelTextCentered(pixels, width, height,
                640, 215, "TABLE SETTINGS", 4, PokerGold);
            DrawPictionaryPixelTextCentered(pixels, width, height, 640, 292,
                "TABLE RULES", 2, PokerMuted);
            DrawPictionaryPixelTextCentered(pixels, width, height, 640, 353,
                $"STACK {pokerStartingChips} - BLINDS {PokerSmallBlind} / {PokerBigBlind}", 2, PokerCream);
            DrawPictionaryPixelTextCentered(pixels, width, height, 640, 428,
                "LIVE PLAYER CARDS WILL BE PRIVATE", 2, PokerMuted);
            FillQuizTvPanel(pixels, width, height, 440, 455, 400, 45, PokerPlum, PokerGold);
            DrawPictionaryPixelTextCentered(pixels, width, height, 640, 470,
                Plugin.Cfg.PokerMusicEnabled ? "JAZZ MUSIC: ON" : "JAZZ MUSIC: OFF", 2, PokerCream);
        }
    }

    private static void DrawPokerSuit(byte[] pixels, int width, int height,
        int x, int y, int radius, char suit, uint colour)
    {
        switch (suit)
        {
            case 'D':
                DrawSlotPixelDiamond(pixels, width, height, x, y, radius, radius + 3, colour);
                break;
            case 'H':
                FillRoulettePrototypeCircle(pixels, width, height, x - radius / 2, y - radius / 3,
                    Math.Max(2, radius / 2), colour);
                FillRoulettePrototypeCircle(pixels, width, height, x + radius / 2, y - radius / 3,
                    Math.Max(2, radius / 2), colour);
                DrawSlotPixelTriangle(pixels, width, height,
                    x - radius, y - radius / 3, x + radius, y - radius / 3,
                    x, y + radius + 3, colour);
                break;
            case 'C':
                var clubLobe = Math.Max(3, radius * 3 / 5);
                FillRoulettePrototypeCircle(pixels, width, height,
                    x, y - radius * 2 / 3, clubLobe, colour);
                FillRoulettePrototypeCircle(pixels, width, height,
                    x - radius * 2 / 3, y + radius / 4, clubLobe, colour);
                FillRoulettePrototypeCircle(pixels, width, height,
                    x + radius * 2 / 3, y + radius / 4, clubLobe, colour);
                FillPictionaryPixels(pixels, width, height,
                    x - Math.Max(2, radius / 5), y + radius / 3,
                    Math.Max(4, radius * 2 / 5), radius, colour);
                DrawSlotPixelTriangle(pixels, width, height,
                    x, y + radius / 2,
                    x - radius / 2, y + radius + 3,
                    x + radius / 2, y + radius + 3, colour);
                break;
            case 'S':
                DrawSlotPixelTriangle(pixels, width, height,
                    x, y - radius - 4, x - radius, y + radius / 3,
                    x + radius, y + radius / 3, colour);
                FillRoulettePrototypeCircle(pixels, width, height,
                    x - radius / 2, y + radius / 3, Math.Max(3, radius * 3 / 4), colour);
                FillRoulettePrototypeCircle(pixels, width, height,
                    x + radius / 2, y + radius / 3, Math.Max(3, radius * 3 / 4), colour);
                FillPictionaryPixels(pixels, width, height,
                    x - Math.Max(2, radius / 5), y + radius / 2,
                    Math.Max(4, radius * 2 / 5), radius, colour);
                DrawSlotPixelTriangle(pixels, width, height,
                    x, y + radius / 2,
                    x - radius / 2, y + radius + 3,
                    x + radius / 2, y + radius + 3, colour);
                break;
        }
    }

    private static void DrawPokerChip(byte[] pixels, int width, int height,
        int x, int y, int radius, uint fill)
    {
        FillRoulettePrototypeCircle(pixels, width, height, x, y, radius, PokerGold);
        FillRoulettePrototypeCircle(pixels, width, height, x, y, radius - 3, fill);
        DrawRoulettePrototypeRing(pixels, width, height, x, y, radius - 7, radius - 6, PokerCream);
    }

    private static void FillPokerEllipse(byte[] pixels, int width, int height,
        int centerX, int centerY, int radiusX, int radiusY, uint colour)
    {
        for (var y = Math.Max(0, centerY - radiusY); y <= Math.Min(height - 1, centerY + radiusY); y++)
        {
            var fraction = (double)(y - centerY) / radiusY;
            var halfWidth = (int)(radiusX * Math.Sqrt(Math.Max(0d, 1d - fraction * fraction)));
            FillPictionaryPixels(pixels, width, height,
                centerX - halfWidth, y, halfWidth * 2 + 1, 1, colour);
        }
    }

    private static void DrawPokerEllipseOutline(byte[] pixels, int width, int height,
        int centerX, int centerY, int radiusX, int radiusY, uint colour)
    {
        for (var angle = 0; angle < 360; angle++)
        {
            var radians = angle * Math.PI / 180d;
            var x = centerX + (int)(radiusX * Math.Cos(radians));
            var y = centerY + (int)(radiusY * Math.Sin(radians));
            FillRoulettePrototypeCircle(pixels, width, height, x, y, 1, colour);
        }
    }
}

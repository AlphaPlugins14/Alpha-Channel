using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

// Roulette's TV renderer remains isolated here so the activity can still be removed cleanly.
internal sealed partial class MainWindow
{
    private static readonly int[] RoulettePrototypeNumbers =
    [
        0, 32, 15, 19, 4, 21, 2, 25, 17, 34, 6, 27, 13, 36, 11, 30, 8, 23,
        10, 5, 24, 16, 33, 1, 20, 14, 31, 9, 22, 18, 29, 7, 28, 12, 35, 3, 26,
    ];

    private static readonly HashSet<int> RoulettePrototypeRedNumbers =
    [1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36];

    private static readonly uint[] RoulettePocketColours = RoulettePrototypeNumbers
        .Select(number => number == 0 ? 0x08784Eu : RoulettePrototypeRedNumbers.Contains(number) ? 0xB9273Du : 0x11151Du)
        .ToArray();

    private const double RoulettePrototypeDurationSeconds = 10.5d;
    private const double RoulettePrototypeFullTurn = Math.PI * 2d;
    private bool roulettePrototypeActive;
    private bool roulettePrototypeSettled;
    private DateTime roulettePrototypeStartedUtc;
    private DateTime roulettePrototypeLastFrameUtc = DateTime.MinValue;
    private int roulettePrototypeResultIndex;
    private double roulettePrototypeWheelStartAngle;
    private double roulettePrototypeBallStartAngle;
    private byte[]? roulettePrototypePixels;

    private void OpenRoulettePrototypePage()
    {
        if (!CanLaunchActivity(ActiveActivityKind.Roulette))
            return;
        lastActivitiesPage = HomePage.Roulette;
        currentPage = HomePage.Roulette;
    }

    internal void UpdateRoulettePrototype()
    {
        while (stream.IncomingRouletteCommands.TryDequeue(out var incoming))
        {
            ReceiveArcadeServerCommand("roulette", incoming.UserId, incoming.Command);
        }
        UpdateActivityRelay("roulette");
        if (!RouletteActive) return;
        UpdateRouletteMusic();
        rouletteRollSoundPlayer.SetPlaying(
            Plugin.Cfg.RouletteSoundEffectsEnabled && roulettePhase == RoulettePhase.Spinning,
            Plugin.Cfg.Muted ? 0 : Plugin.Cfg.Volume);

        if (stream.Mode == StreamMode.Hosting &&
            (!whiteboardActive || !string.Equals(whiteboardSessionId, rouletteWhiteboardSessionId, StringComparison.Ordinal)))
        {
            StopRouletteGame();
            return;
        }

        if (IsLocalActivityHost)
        {
            var before = roulettePlayers.Count;
            AddRouletteRosterPlayers();
            if (before != roulettePlayers.Count) { rouletteRevision++; SendRouletteState(); }
            UpdateRouletteRound(DateTime.UtcNow);
        }

        var now = DateTime.UtcNow;
        if ((now - roulettePrototypeLastFrameUtc).TotalMilliseconds < 33d)
            return;
        roulettePrototypeLastFrameUtc = now;
        if (ShouldRenderArcadeTv || IsOpen && currentPage == HomePage.Roulette)
        {
            var frameStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var pixels = BuildRoulettePrototypePixels(now);
            PlaybackRenderDiagnostics.RecordArcadeRender("Roulette",
                System.Diagnostics.Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds);
            screenController.Engine.UpdateWhiteboard(pixels);
        }
        whiteboardTvDirty = false;
    }

    private byte[] BuildRoulettePrototypePixels(DateTime now)
    {
        const int width = WhiteboardRenderer.Width;
        const int height = WhiteboardRenderer.Height;
        roulettePrototypePixels ??= new byte[width * height * 4];
        var pixels = roulettePrototypePixels;

        FillPictionaryPixels(pixels, width, height, 0, 0, width, height, 0x063E2A);
        for (var feltY = 76; feltY < height; feltY += 18)
        {
            FillPictionaryPixels(pixels, width, height, 0, feltY, width, 1, 0x073824);
        }
        FillPictionaryPixels(pixels, width, height, 0, 0, width, 68, 0x181123);
        FillPictionaryPixels(pixels, width, height, 0, 68, width, 3, 0x8B55F6);
        DrawPictionaryPixelText(pixels, width, height, 30, 20, "ALPHA CHANNEL ROULETTE", 4, 0xF8F5FF);

        var elapsed = roulettePhase == RoulettePhase.Spinning
            ? Math.Max(0d, (now - rouletteSpinStartedUtc).TotalSeconds)
            : RoulettePrototypeDurationSeconds;
        var progress = roulettePhase == RoulettePhase.Spinning
            ? Math.Clamp(elapsed / RoulettePrototypeDurationSeconds, 0d, 1d)
            : 1d;
        var easeOut = 1d - Math.Pow(1d - progress, 4d);
        var sectorAngle = RoulettePrototypeFullTurn / RoulettePrototypeNumbers.Length;
        const double pocketOffset = -Math.PI / 2d;
        var finalWheelAngle = roulettePrototypeWheelStartAngle + RoulettePrototypeFullTurn * 4.35d;
        var wheelAngle = roulettePrototypeWheelStartAngle + (finalWheelAngle - roulettePrototypeWheelStartAngle) * easeOut;
        var targetBallAngle = finalWheelAngle + pocketOffset + (roulettePrototypeResultIndex + 0.5d) * sectorAngle;
        while (targetBallAngle > roulettePrototypeBallStartAngle - RoulettePrototypeFullTurn * 6d)
        {
            targetBallAngle -= RoulettePrototypeFullTurn;
        }
        var ballAngle = roulettePrototypeBallStartAngle + (targetBallAngle - roulettePrototypeBallStartAngle) * easeOut;
        var dropProgress = SmoothRoulettePrototype(Math.Clamp((progress - 0.61d) / 0.39d, 0d, 1d));
        var ballRadius = 263d + (214d - 263d) * dropProgress;
        if (dropProgress > 0d && progress < 1d)
        {
            ballRadius += Math.Sin(dropProgress * Math.PI * 7d) * 10d * (1d - dropProgress);
        }

        const int centerX = 300;
        const int centerY = 350;
        DrawRoulettePrototypeWheel(pixels, width, height, centerX, centerY, wheelAngle);

        var ballX = centerX + (int)Math.Round(Math.Cos(ballAngle) * ballRadius);
        var ballY = centerY + (int)Math.Round(Math.Sin(ballAngle) * ballRadius);
        FillRoulettePrototypeCircle(pixels, width, height, ballX + 4, ballY + 5, 15, 0x090A0F);
        FillRoulettePrototypeCircle(pixels, width, height, ballX, ballY, 14, 0xD8B858);
        FillRoulettePrototypeCircle(pixels, width, height, ballX, ballY, 10, 0xFFFFFF);
        FillRoulettePrototypeCircle(pixels, width, height, ballX - 3, ballY - 3, 3, 0xFFFFFF);

        DrawRoulettePrototypeStatus(pixels, width, height, progress, elapsed, now);
        DrawRouletteRoundResults(pixels, width, height, now);
        DrawRouletteHelp(pixels, width, height);
        DrawRouletteSettings(pixels, width, height);
        DrawRouletteLedgerOverlay(pixels, width, height);
        return pixels;
    }

    private void DrawRouletteHelp(byte[] pixels, int width, int height)
    {
        const uint gold = 0xD6B456;
        const int buttonX = 1218;
        const int buttonY = 10;
        const int buttonSize = 48;
        var hovered = rouletteHoveredTarget == 106 || roulettePageHoveredTarget == 106;
        FillPictionaryPixels(pixels, width, height, buttonX, buttonY, buttonSize, buttonSize,
            hovered || rouletteHelpOpen ? 0x4A2B75u : 0x251A38u);
        DrawRoulettePrototypeRectOutline(pixels, width, height, buttonX, buttonY, buttonSize, buttonSize,
            rouletteHelpOpen ? 0xBCA0FFu : gold, 2);
        DrawPictionaryPixelTextCentered(pixels, width, height, buttonX + buttonSize / 2, buttonY + 10,
            rouletteHelpOpen ? "X" : "?", 4, 0xF8F5FF);

        if (!rouletteHelpOpen) return;

        const int panelX = 300;
        const int panelY = 126;
        const int panelWidth = 680;
        const int panelHeight = 474;
        FillPictionaryPixels(pixels, width, height, panelX, panelY, panelWidth, panelHeight, 0x181123);
        DrawRoulettePrototypeRectOutline(pixels, width, height, panelX, panelY, panelWidth, panelHeight, 0x8B55F6, 4);
        FillPictionaryPixels(pixels, width, height, panelX + 4, panelY + 72, panelWidth - 8, 3, gold);

        DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, panelY + 22,
            "HOW TO PLAY", 5, gold);
        DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, panelY + 92,
            "PICK A CHIP AND PLACE ONE OR MORE BETS", 3, 0xF8F5FF);
        DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, panelY + 124,
            rouletteCroupierMode ? "THE CROUPIER CLOSES BETS AND SPINS THE WHEEL" : "BETS CLOSE WHEN THE TIMER REACHES ZERO", 2, 0xBCA0FF);

        DrawPictionaryPixelText(pixels, width, height, panelX + 42, panelY + 174,
            "TOTAL RETURN", 3, gold);
        DrawPictionaryPixelText(pixels, width, height, panelX + 42, panelY + 222,
            "SINGLE NUMBER", 3, 0xFFF5D8);
        DrawPictionaryPixelText(pixels, width, height, panelX + 508, panelY + 222,
            "36X", 3, gold);
        DrawPictionaryPixelText(pixels, width, height, panelX + 42, panelY + 266,
            "DOZEN OR 2 TO 1 COLUMN", 3, 0xFFF5D8);
        DrawPictionaryPixelText(pixels, width, height, panelX + 520, panelY + 266,
            "3X", 3, gold);
        DrawPictionaryPixelText(pixels, width, height, panelX + 42, panelY + 310,
            "COLOUR / ODD / EVEN / HIGH / LOW", 2, 0xFFF5D8);
        DrawPictionaryPixelText(pixels, width, height, panelX + 520, panelY + 306,
            "2X", 3, gold);

        FillPictionaryPixels(pixels, width, height, panelX + 30, panelY + 356, panelWidth - 60, 72, 0x073E2A);
        DrawRoulettePrototypeRectOutline(pixels, width, height, panelX + 30, panelY + 356,
            panelWidth - 60, 72, gold, 2);
        DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, panelY + 370,
            rouletteRefillEnabled ? "CHIP REFILL: ON" : "CHIP REFILL: OFF", 3,
            rouletteRefillEnabled ? 0x24D991u : 0xF14C62u);
        DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, panelY + 402,
            rouletteRefillEnabled ? $"AT ZERO YOUR BALANCE RESETS TO {rouletteStartingChips}" : "WHEN YOU RUN OUT, YOUR CHIPS ARE NOT RESET",
            2, 0xF8F5FF);
        DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, panelY + 446,
            "SELECT X IN THE TOP RIGHT TO CLOSE", 2, 0xBCA0FF);
    }

    private void DrawRoulettePrototypeWheel(byte[] pixels, int width, int height, int centerX, int centerY, double wheelAngle)
    {
        const int outerRadius = 245;
        const int pocketInnerRadius = 174;
        const int numberRadius = 212;
        var sectorAngle = RoulettePrototypeFullTurn / RoulettePrototypeNumbers.Length;
        const double pocketOffset = -Math.PI / 2d;

        FillRoulettePrototypeCircle(pixels, width, height, centerX, centerY, outerRadius + 14, 0x5C431B);
        FillRoulettePrototypeCircle(pixels, width, height, centerX, centerY, outerRadius + 9, 0xD9B85B);
        FillRoulettePrototypeCircle(pixels, width, height, centerX, centerY, outerRadius, 0x161A20);

        RoulettePocketRaster.Draw(pixels, width, height, centerX, centerY, wheelAngle, RoulettePocketColours);

        for (var index = 0; index < RoulettePrototypeNumbers.Length; index++)
        {
            var boundary = wheelAngle + pocketOffset + index * sectorAngle;
            DrawRoulettePrototypeLine(
                pixels, width, height,
                centerX + (int)(Math.Cos(boundary) * pocketInnerRadius),
                centerY + (int)(Math.Sin(boundary) * pocketInnerRadius),
                centerX + (int)(Math.Cos(boundary) * outerRadius),
                centerY + (int)(Math.Sin(boundary) * outerRadius),
                0xD6B456, 2);

            var middle = boundary + sectorAngle / 2d;
            var labelX = centerX + (int)(Math.Cos(middle) * numberRadius);
            var labelY = centerY + (int)(Math.Sin(middle) * numberRadius);
            DrawPictionaryPixelTextCentered(
                pixels, width, height, labelX, labelY - 7,
                RoulettePrototypeNumbers[index].ToString(), 2, 0xFFF9E8);
        }

        DrawRoulettePrototypeRing(pixels, width, height, centerX, centerY, pocketInnerRadius - 4, pocketInnerRadius + 3, 0xD6B456);
        FillRoulettePrototypeCircle(pixels, width, height, centerX, centerY, 164, 0x251A16);
        FillRoulettePrototypeCircle(pixels, width, height, centerX, centerY, 142, 0x7A5523);
        FillRoulettePrototypeCircle(pixels, width, height, centerX, centerY, 136, 0x171B22);
        DrawRoulettePrototypeRing(pixels, width, height, centerX, centerY, 94, 100, 0xD6B456);
        FillRoulettePrototypeCircle(pixels, width, height, centerX, centerY, 88, 0x251A16);
        FillRoulettePrototypeCircle(pixels, width, height, centerX, centerY, 49, 0xD6B456);
        FillRoulettePrototypeCircle(pixels, width, height, centerX, centerY, 40, 0x171B22);
        var spinReady = CanBeginRouletteSpin && rouletteCroupierMode;
        DrawPictionaryPixelTextCentered(pixels, width, height, centerX, centerY - (spinReady ? 10 : 14),
            spinReady ? "SPIN" : "AC", spinReady ? 3 : 4, spinReady ? 0xFFF5D8u : 0xD6B456u);
        if (rouletteCroupierMode && CanBeginRouletteSpin)
            DrawRoulettePrototypeRing(pixels, width, height, centerX, centerY, 94, 100,
                rouletteHoveredTarget == 132 || roulettePageHoveredTarget == 132 ? 0xFFF5D8u : 0x8B55F6u);
    }

    private void DrawRoulettePrototypeStatus(byte[] pixels, int width, int height, double progress, double elapsed, DateTime now)
    {
        const uint gold = 0xD6B456;
        const uint ivory = 0xFFF5D8;
        const uint panel = 0x073321;
        const int tableX = 610;
        const int zeroWidth = 52;
        const int cellWidth = 44;
        const int cellHeight = 58;
        const int oddsWidth = 60;
        const int numberX = tableX + zeroWidth;
        const int gridY = 182;
        const int numbersWidth = cellWidth * 12;
        const int tableWidth = zeroWidth + numbersWidth + oddsWidth;
        var number = RoulettePrototypeNumbers[roulettePrototypeResultIndex];

        DrawPictionaryPixelText(pixels, width, height, 814, 25, rouletteCroupierMode && IsLocalActivityHost ? "CROUPIER" : "YOUR CHIPS", 3, 0xBCA0FF);
        var localChips = roulettePlayers.GetValueOrDefault(LocalRouletteUserId)?.Chips ?? rouletteStartingChips;
        DrawPictionaryPixelText(pixels, width, height, 1015, 17, rouletteCroupierMode && IsLocalActivityHost ? "--" : localChips.ToString(), 5, gold);

        FillPictionaryPixels(pixels, width, height, tableX, 88, tableWidth, 76, panel);
        DrawRoulettePrototypeRectOutline(pixels, width, height, tableX, 88, tableWidth, 76, gold, 2);
        var secondsRemaining = roulettePhaseEndsUtc == DateTime.MaxValue ? 0 : Math.Max(0, (int)Math.Ceiling((roulettePhaseEndsUtc - now).TotalSeconds));
        if (!rouletteCroupierMode)
        {
            FillPictionaryPixels(pixels, width, height, 26, 82, 148, 78, 0x073321);
            DrawRoulettePrototypeRectOutline(pixels, width, height, 26, 82, 148, 78, gold, 2);
            DrawPictionaryPixelTextCentered(pixels, width, height, 100, 94,
                roulettePhase == RoulettePhase.Results ? "NEXT ROUND" : roulettePhase == RoulettePhase.WaitingForBet ? "WAITING" : "TIME LEFT", 2, 0xC9CFE0);
            DrawPictionaryPixelTextCentered(pixels, width, height, 100, 119,
                roulettePhaseEndsUtc == DateTime.MaxValue ? "--" : $"{secondsRemaining:00}", 5, gold);
        }
        var showResult = roulettePhase == RoulettePhase.Results;
        if (rouletteCroupierMode && IsLocalActivityHost && roulettePhase is RoulettePhase.WaitingForBet or RoulettePhase.Betting or RoulettePhase.Results)
        {
            var hovered = rouletteHoveredTarget == 131 || roulettePageHoveredTarget == 131;
            // Raised purple controls distinguish actions from the green status panels.
            FillPictionaryPixels(pixels, width, height, tableX + 12, 97, tableWidth - 24, 64, 0x181123);
            FillPictionaryPixels(pixels, width, height, tableX + 12, 92, tableWidth - 24, 64,
                hovered ? 0x8051C7u : 0x623AA1u);
            DrawRoulettePrototypeRectOutline(pixels, width, height, tableX + 12, 92, tableWidth - 24, 64,
                hovered ? ivory : 0xBCA0FFu, 2);
            DrawPictionaryPixelTextCentered(pixels, width, height, tableX + tableWidth / 2, 100,
                roulettePhase == RoulettePhase.Betting ? "CLOSE BETS" : "OPEN BETS", 4, ivory);
            DrawPictionaryPixelTextCentered(pixels, width, height, tableX + tableWidth / 2, 136,
                roulettePhase == RoulettePhase.Betting ? "CLICK TO CLOSE BETTING" : "CLICK TO OPEN BETTING", 2, 0xE4D4FF);
        }
        else if (showResult)
        {
            var numberColour = number == 0 ? 0x24D991u : RoulettePrototypeRedNumbers.Contains(number) ? 0xF14C62u : 0xF7F2E8u;
            var colourName = number == 0 ? "GREEN" : RoulettePrototypeRedNumbers.Contains(number) ? "RED" : "BLACK";
            DrawPictionaryPixelText(pixels, width, height, tableX + 24, 112, "WINNING NUMBER", 4, gold);
            FillRoulettePrototypeCircle(pixels, width, height, tableX + 430, 126, 29,
                number == 0 ? 0x075F43u : RoulettePrototypeRedNumbers.Contains(number) ? 0x821D31u : 0x080A0Fu);
            DrawRoulettePrototypeRing(pixels, width, height, tableX + 430, 126, 29, 32, gold);
            DrawPictionaryPixelTextCentered(pixels, width, height, tableX + 430, number >= 10 ? 111 : 108,
                number.ToString(), number >= 10 ? 4 : 5, numberColour);
            DrawPictionaryPixelText(pixels, width, height, tableX + 476, 112, colourName, 4, numberColour);

        }
        else
        {
            DrawPictionaryPixelTextCentered(pixels, width, height, tableX + tableWidth / 2, 112,
                roulettePhase is RoulettePhase.Spinning or RoulettePhase.BetsClosed ? "NO MORE BETS" : RouletteBetsOpen ? "BETTING OPEN" : "BETTING CLOSED", 4, gold);
            FillPictionaryPixels(pixels, width, height, tableX + 4, 158, tableWidth - 8, 3, 0x252C43);
            FillPictionaryPixels(pixels, width, height, tableX + 4, 158,
                roulettePhase == RoulettePhase.Betting && !rouletteCroupierMode ? (int)((tableWidth - 8) * Math.Clamp((roulettePhaseEndsUtc - now).TotalSeconds / rouletteBettingSeconds, 0d, 1d)) : 0, 3, 0x8B55F6);
        }

        DrawRoulettePrototypeBettingGrid(
            pixels, width, height, tableX, numberX, gridY, zeroWidth, cellWidth, cellHeight,
            oddsWidth, numbersWidth, number, showResult, gold, ivory);
        DrawRoulettePlacedBets(pixels, width, height);
        if (RouletteBetsOpen && now < rouletteNotEnoughChipsUntilUtc)
            DrawPictionaryPixelTextCentered(pixels, width, height, tableX + tableWidth / 2, 475,
                "NOT ENOUGH CHIPS", 2, 0xF14C62);
        else if (RouletteBetsOpen && (roulettePageHoveredBetTarget ?? rouletteHoveredBetTarget) is { } hoveredBet &&
                 IsValidRouletteTarget(hoveredBet))
        {
            var returnMultiplier = RoulettePayoutMultiplier(hoveredBet);
            DrawPictionaryPixelTextCentered(pixels, width, height, tableX + tableWidth / 2, 475,
                $"{FormatRouletteTarget(hoveredBet).ToUpperInvariant()} - PAYS {returnMultiplier - 1}:1 ({returnMultiplier}X RETURN)",
                2, 0xFFF5D8);
        }
        if (rouletteCroupierMode && IsLocalActivityHost)
        {
            FillPictionaryPixels(pixels, width, height, tableX, 508, tableWidth, 102, panel);
            DrawRoulettePrototypeRectOutline(pixels, width, height, tableX, 508, tableWidth, 102, gold, 2);
            DrawPictionaryPixelTextCentered(pixels, width, height, tableX + tableWidth / 2, 527, "CROUPIER MODE", 3, gold);
            DrawPictionaryPixelTextCentered(pixels, width, height, tableX + tableWidth / 2, 564, "YOU RUN THE TABLE - PLAYERS PLACE THE BETS", 2, ivory);
        }
        else DrawRoulettePrototypeChipRail(pixels, width, height, tableX, 508, tableWidth, !LocalRouletteCanBet, gold, now);
        DrawRouletteBetTooltip(pixels, width, height, gold);

        FillPictionaryPixels(pixels, width, height, 18, 628, width - 36, 78, 0x2C1016);
        DrawRoulettePrototypeRectOutline(pixels, width, height, 18, 628, width - 36, 78, gold, 3);
        if (showResult)
        {
            var localUserId = LocalRouletteUserId;
            var localWin = rouletteWinners.FirstOrDefault(winner => winner.PlayerId == localUserId)?.Winnings ?? 0;
            var localStake = rouletteBets.Where(bet => bet.PlayerId == localUserId).Sum(bet => bet.Amount);
            var refilled = rouletteRefilledPlayerIds.Contains(LocalRouletteUserId);
            var headline = refilled ? $"CHIPS REFILLED TO {rouletteStartingChips}" :
                localWin > 0 ? $"YOU WON {localWin} CHIPS" :
                localStake > 0 ? $"YOU LOST {localStake} CHIPS" : $"{number} {RouletteColourName(number)}";
            if (localWin > 0 && !refilled)
                DrawRouletteResultWithNet(pixels, width, height, 30, 510, 650, headline, localWin - localStake, gold);
            else
                DrawPictionaryPixelTextCentered(pixels, width, height, refilled ? 270 : 300,
                    refilled ? 655 : 650, headline, refilled ? 3 : 4, localStake > 0 && !refilled ? 0xF14C62u : 0xF8F5FFu);
            FillPictionaryPixels(pixels, width, height, 524, 642, 2, 50, gold);
            if (rouletteWinners.Count == 0)
                DrawPictionaryPixelTextCentered(pixels, width, height, 890, 656, "NO WINNING BETS THIS ROUND", 3, 0xFFF5D8);
            else
            {
                var winner = rouletteWinners[(int)(DateTime.UtcNow.TimeOfDay.TotalSeconds / 2) % rouletteWinners.Count];
                var stake = rouletteBets.Where(bet => bet.PlayerId == winner.PlayerId).Sum(bet => bet.Amount);
                DrawRouletteResultWithNet(pixels, width, height, 540, width - 24, 656,
                    $"{winner.PlayerName.ToUpperInvariant()} WON {winner.Winnings} CHIPS", winner.Winnings - stake, ivory);
            }
        }
        else
        {
            var message = roulettePhase switch
            {
                RoulettePhase.WaitingForBet => rouletteCroupierMode ? "WAITING FOR THE CROUPIER TO OPEN BETS" : "COUNTDOWN BEGINS AFTER 1ST BET",
                RoulettePhase.BetsClosed => "NO MORE BETS - READY TO SPIN",
                RoulettePhase.Spinning => "NO MORE BETS - WHEEL SPINNING",
                _ => "PLACE YOUR BETS",
            };
            DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 649, message, message.Length > 25 ? 3 : 5, gold);
            DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 687,
                roulettePhase == RoulettePhase.BetsClosed
                    ? IsLocalActivityHost ? "CLICK THE WHEEL TO SPIN" : "WAITING FOR THE CROUPIER TO SPIN"
                    : roulettePhase == RoulettePhase.Spinning ? "THE BALL IS IN MOTION"
                    : rouletteCroupierMode && IsLocalActivityHost ? "USE THE BUTTON ABOVE THE BETTING BOARD"
                    : "SELECT A CHIP AND CHOOSE A SPACE", 2, 0xBCA0FF);
        }
    }

    private static void DrawRouletteResultWithNet(byte[] pixels, int width, int height,
        int left, int right, int y, string headline, int net, uint headlineColour)
    {
        var change = net > 0 ? $"↑{net}" : net < 0 ? $"↓{-net}" : "EVEN";
        var changeColour = net > 0 ? 0x24D991u : net < 0 ? 0xF14C62u : 0xC9CFE0u;
        var scale = 3;
        var gap = 12;
        var headlineWidth = Math.Max(0, headline.Length * 6 * scale - scale);
        var changeWidth = Math.Max(0, change.Length * 6 * scale - scale);
        if (headlineWidth + gap + changeWidth > right - left)
        {
            scale = 2;
            gap = 8;
            headlineWidth = Math.Max(0, headline.Length * 6 * scale - scale);
            changeWidth = Math.Max(0, change.Length * 6 * scale - scale);
        }
        var x = left + (right - left - headlineWidth - gap - changeWidth) / 2;
        DrawPictionaryPixelText(pixels, width, height, x, y, headline, scale, headlineColour);
        DrawPictionaryPixelText(pixels, width, height, x + headlineWidth + gap, y, change, scale, changeColour);
    }

    private static string RouletteColourName(int number) => number == 0 ? "GREEN" : RoulettePrototypeRedNumbers.Contains(number) ? "RED" : "BLACK";

    private void DrawRoulettePrototypeBettingGrid(
        byte[] pixels, int width, int height, int tableX, int numberX, int gridY, int zeroWidth,
        int cellWidth, int cellHeight, int oddsWidth, int numbersWidth, int winningNumber,
        bool showResult, uint gold, uint ivory)
    {
        var totalHeight = cellHeight * 3;
        var hoveredBetTarget = RouletteBetsOpen ? roulettePageHoveredBetTarget ?? rouletteHoveredBetTarget : null;
        FillPictionaryPixels(pixels, width, height, tableX, gridY, zeroWidth, totalHeight, 0x08784E);
        DrawRoulettePrototypeRectOutline(pixels, width, height, tableX, gridY, zeroWidth, totalHeight,
            gold, hoveredBetTarget == "N:0" ? 4 : 2);
        if (hoveredBetTarget is not null && RouletteBetWins(hoveredBetTarget, 0))
            DrawRoulettePrototypeRectOutline(pixels, width, height, tableX + 5, gridY + 5,
                zeroWidth - 10, totalHeight - 10, RoulettePayoutHoverBorderColour(0x08784E), 2);
        DrawPictionaryPixelTextCentered(pixels, width, height, tableX + zeroWidth / 2, gridY + 64, "0", 6, ivory);

        for (var column = 0; column < 12; column++)
        for (var row = 0; row < 3; row++)
        {
            var value = (column + 1) * 3 - row;
            var cellX = numberX + column * cellWidth;
            var cellY = gridY + row * cellHeight;
            var cellColour = RoulettePrototypeRedNumbers.Contains(value) ? 0xB9273Du : 0x11151Du;
            FillPictionaryPixels(pixels, width, height, cellX, cellY, cellWidth, cellHeight, cellColour);
            DrawRoulettePrototypeRectOutline(pixels, width, height, cellX, cellY, cellWidth, cellHeight,
                gold, hoveredBetTarget == $"N:{value}" ? 3 : 1);
            if (hoveredBetTarget is not null && RouletteBetWins(hoveredBetTarget, value))
                DrawRoulettePrototypeRectOutline(pixels, width, height, cellX + 4, cellY + 4,
                    cellWidth - 8, cellHeight - 8, RoulettePayoutHoverBorderColour(cellColour), 2);
            DrawPictionaryPixelTextCentered(pixels, width, height, cellX + cellWidth / 2,
                cellY + 19, value.ToString(), value >= 10 ? 3 : 4, ivory);
        }

        var oddsX = numberX + numbersWidth;
        for (var row = 0; row < 3; row++)
        {
            var cellY = gridY + row * cellHeight;
            FillPictionaryPixels(pixels, width, height, oddsX, cellY, oddsWidth, cellHeight, 0x07543A);
            DrawRoulettePrototypeRectOutline(pixels, width, height, oddsX, cellY, oddsWidth, cellHeight,
                gold, hoveredBetTarget == $"COL{3 - row}" ? 3 : 1);
            var endLabel = row == 0 ? "3-36" : row == 1 ? "2-35" : "1-34";
            DrawPictionaryPixelTextCentered(pixels, width, height, oddsX + oddsWidth / 2, cellY + 22, endLabel, 2, ivory);
        }

        if (showResult)
        {
            if (winningNumber == 0)
            {
                DrawRoulettePrototypeRectOutline(pixels, width, height, tableX + 3, gridY + 3,
                    zeroWidth - 6, totalHeight - 6, 0xFFF0A0, 4);
            }
            else
            {
                var column = (winningNumber - 1) / 3;
                var row = winningNumber % 3 == 0 ? 0 : winningNumber % 3 == 2 ? 1 : 2;
                var cellX = numberX + column * cellWidth;
                var cellY = gridY + row * cellHeight;
                DrawRoulettePrototypeRectOutline(pixels, width, height, cellX + 2, cellY + 2,
                    cellWidth - 4, cellHeight - 4, 0xFFF0A0, 4);
                FillRoulettePrototypeCircle(pixels, width, height, cellX + cellWidth / 2, cellY + cellHeight - 10, 8, 0xD6B456);
                FillRoulettePrototypeCircle(pixels, width, height, cellX + cellWidth / 2, cellY + cellHeight - 10, 5, 0xF8F5FF);
            }
        }

        var dozenY = gridY + totalHeight;
        for (var index = 0; index < 3; index++)
        {
            var cellX = numberX + index * numbersWidth / 3;
            var cellW = numbersWidth / 3;
            FillPictionaryPixels(pixels, width, height, cellX, dozenY, cellW, 48, 0x07543A);
            DrawRoulettePrototypeRectOutline(pixels, width, height, cellX, dozenY, cellW, 48,
                gold, hoveredBetTarget == $"D{index + 1}" ? 3 : 1);
            DrawPictionaryPixelTextCentered(pixels, width, height, cellX + cellW / 2, dozenY + 14,
                index == 0 ? "1ST 12" : index == 1 ? "2ND 12" : "3RD 12", 3, ivory);
        }

        var outsideY = dozenY + 48;
        string[] labels = ["1 - 18", "EVEN", "RED", "BLACK", "ODD", "19 - 36"];
        for (var index = 0; index < labels.Length; index++)
        {
            var cellW = numbersWidth / labels.Length;
            var cellX = numberX + index * cellW;
            FillPictionaryPixels(pixels, width, height, cellX, outsideY, cellW, 44, 0x07543A);
            var outsideTarget = index switch { 0 => "LOW", 5 => "HIGH", _ => labels[index] };
            DrawRoulettePrototypeRectOutline(pixels, width, height, cellX, outsideY, cellW, 44,
                gold, hoveredBetTarget == outsideTarget ? 3 : 1);
            var labelColour = labels[index] == "RED" ? 0xF14C62u : ivory;
            DrawPictionaryPixelTextCentered(pixels, width, height, cellX + cellW / 2, outsideY + 15,
                labels[index], 2, labelColour);
        }
    }

    private void DrawRoulettePrototypeChipRail(
        byte[] pixels, int width, int height, int x, int y, int railWidth, bool dimmed, uint gold, DateTime now)
    {
        FillPictionaryPixels(pixels, width, height, x, y, railWidth, 104, 0x052E20);
        DrawRoulettePrototypeRectOutline(pixels, width, height, x, y, railWidth, 104, 0x47705D, 2);
        DrawPictionaryPixelText(pixels, width, height, x + 18, y + 12, "SELECT CHIP", 3, dimmed ? 0x777F76u : 0xFFF5D8u);
        var repeatEnabled = !dimmed && CanRepeatRouletteBet(LocalRouletteUserId);
        var repeatHovered = repeatEnabled && (rouletteHoveredTarget == 107 || roulettePageHoveredTarget == 107);
        FillPictionaryPixels(pixels, width, height, x + 8, y + 72, 112, 24,
            repeatHovered ? 0x623AA1u : 0x181123u);
        DrawRoulettePrototypeRectOutline(pixels, width, height, x + 8, y + 72, 112, 24,
            repeatEnabled ? gold : 0x4D587Au, 1);
        DrawPictionaryPixelTextCentered(pixels, width, height, x + 64, y + 77,
            "REPEAT", 2, repeatEnabled ? 0xFFF5D8u : 0x777F76u);
        int[] values = [1, 5, 25, 100, 500];
        uint[] colours = [0xD9DDE4, 0xC62C3E, 0x159553, 0x11151D, 0x6B3B91];
        for (var index = 0; index < values.Length; index++)
        {
            var centerX = x + 170 + index * 68;
            var centerY = y + 57;
            var selected = RouletteChipValues[index] == rouletteSelectedChip && !dimmed;
            var hovered = !dimmed && (rouletteHoveredTarget == 100 + index || roulettePageHoveredTarget == 100 + index);
            var pulse = hovered ? 1 + (int)Math.Round((1d + Math.Sin(now.TimeOfDay.TotalSeconds * 9d)) * 0.5d) : 0;
            if (selected)
            {
                DrawRoulettePrototypeRing(pixels, width, height, centerX, centerY, 31 + pulse, 36 + pulse, gold);
            }
            FillRoulettePrototypeCircle(pixels, width, height, centerX, centerY, 29 + pulse, colours[index]);
            DrawRoulettePrototypeRing(pixels, width, height, centerX, centerY, 20 + pulse, 24 + pulse, 0xF4E9D0);
            FillRoulettePrototypeCircle(pixels, width, height, centerX, centerY, 18 + pulse, colours[index]);
            var text = values[index].ToString();
            DrawPictionaryPixelTextCentered(pixels, width, height, centerX, centerY - 7,
                text, text.Length >= 3 ? 2 : 3, index == 0 ? 0x161820u : 0xFFF5D8u);
        }
        var clearHovered = !dimmed && (rouletteHoveredTarget == 105 || roulettePageHoveredTarget == 105);
        FillPictionaryPixels(pixels, width, height, x + railWidth - 126, y + 28, 110, 54,
            clearHovered ? 0x392053u : 0x181123u);
        DrawRoulettePrototypeRectOutline(pixels, width, height, x + railWidth - 126, y + 28, 110, 54,
            dimmed ? 0x4D587Au : clearHovered ? gold : 0x8B55F6u, clearHovered ? 3 : 2);
        DrawPictionaryPixelTextCentered(pixels, width, height, x + railWidth - 71, y + 48,
            "CLEAR", 2, dimmed ? 0x777F76u : 0xF8F5FFu);
    }

    private void DrawRoulettePlacedBets(byte[] pixels, int width, int height)
    {
        foreach (var group in rouletteBets.GroupBy(bet => bet.Target))
        {
            if (!TryGetRouletteTargetCenter(group.Key, out var center)) continue;
            var total = group.Sum(bet => bet.Amount);
            var colour = total switch { <= 1 => 0xD9DDE4u, <= 5 => 0xC62C3Eu, <= 25 => 0x159553u, <= 100 => 0x11151Du, _ => 0x6B3B91u };
            FillRoulettePrototypeCircle(pixels, width, height, (int)center.X + 2, (int)center.Y + 3, 14, 0x080A0F);
            FillRoulettePrototypeCircle(pixels, width, height, (int)center.X, (int)center.Y, 13, colour);
            DrawRoulettePrototypeRing(pixels, width, height, (int)center.X, (int)center.Y, 9, 11, 0xFFF5D8);
            DrawPictionaryPixelTextCentered(pixels, width, height, (int)center.X, (int)center.Y - 5,
                total.ToString(), total >= 100 ? 1 : 2, 0xFFFFFF);
        }
    }

    private void DrawRouletteBetTooltip(byte[] pixels, int width, int height, uint gold)
    {
        var hoveredBetTarget = roulettePageHoveredBetTarget ?? rouletteHoveredBetTarget;
        if (hoveredBetTarget is null ||
            !TryGetRouletteTargetCenter(hoveredBetTarget, out var center)) return;
        var players = rouletteBets
            .Where(bet => bet.Target == hoveredBetTarget)
            .GroupBy(bet => new { bet.PlayerId, bet.PlayerName })
            .Select(group => (group.Key.PlayerName, Amount: group.Sum(bet => bet.Amount)))
            .OrderByDescending(entry => entry.Amount)
            .ThenBy(entry => entry.PlayerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (players.Length == 0) return;

        const int boxWidth = 270;
        var visibleCount = Math.Min(players.Length, 8);
        var boxHeight = 38 + visibleCount * 24 + (players.Length > visibleCount ? 22 : 0);
        var x = (int)center.X + 20;
        if (x + boxWidth > width - 12) x = (int)center.X - boxWidth - 20;
        x = Math.Clamp(x, 12, width - boxWidth - 12);
        var y = (int)center.Y - boxHeight - 18;
        if (y < 76) y = (int)center.Y + 24;
        y = Math.Clamp(y, 76, height - boxHeight - 12);

        FillPictionaryPixels(pixels, width, height, x, y, boxWidth, boxHeight, 0x14101D);
        DrawRoulettePrototypeRectOutline(pixels, width, height, x, y, boxWidth, boxHeight, gold, 2);
        DrawPictionaryPixelText(pixels, width, height, x + 14, y + 10, "BETS ON THIS SPACE", 2, gold);
        for (var index = 0; index < visibleCount; index++)
        {
            var entry = players[index];
            var name = entry.PlayerName.Length > 18 ? entry.PlayerName[..18] : entry.PlayerName;
            DrawPictionaryPixelText(pixels, width, height, x + 14, y + 36 + index * 24,
                $"{name}: {entry.Amount}", 2, 0xFFF5D8);
        }
        if (players.Length > visibleCount)
            DrawPictionaryPixelText(pixels, width, height, x + 14, y + 36 + visibleCount * 24,
                $"+{players.Length - visibleCount} MORE", 2, 0xBCA0FF);
    }

    private static bool TryGetRouletteTargetCenter(string target, out Vector2 center)
    {
        center = default;
        if (target.StartsWith("N:", StringComparison.Ordinal) && int.TryParse(target[2..], out var number))
        {
            if (number == 0) { center = new Vector2(636, 269); return true; }
            var column = (number - 1) / 3;
            var row = number % 3 == 0 ? 0 : number % 3 == 2 ? 1 : 2;
            center = new Vector2(662 + column * 44 + 22, 182 + row * 58 + 29); return true;
        }
        center = target switch
        {
            "COL3" => new Vector2(1220, 211), "COL2" => new Vector2(1220, 269), "COL1" => new Vector2(1220, 327),
            "D1" => new Vector2(750, 380), "D2" => new Vector2(926, 380), "D3" => new Vector2(1102, 380),
            "LOW" => new Vector2(706, 426), "EVEN" => new Vector2(794, 426), "RED" => new Vector2(882, 426),
            "BLACK" => new Vector2(970, 426), "ODD" => new Vector2(1058, 426), "HIGH" => new Vector2(1146, 426), _ => default,
        };
        return center != default;
    }

    private static void DrawRoulettePrototypeActivityPreview(ImDrawListPtr drawList, Vector2 panelMin, Vector2 panelMax)
    {
        var center = (panelMin + panelMax) * 0.5f;
        var radius = Math.Min(panelMax.X - panelMin.X, panelMax.Y - panelMin.Y) * 0.38f;
        var gold = ImGui.GetColorU32(new Vector4(0.84f, 0.68f, 0.28f, 1f));
        var red = ImGui.GetColorU32(new Vector4(0.78f, 0.16f, 0.25f, 1f));
        var dark = ImGui.GetColorU32(new Vector4(0.08f, 0.09f, 0.13f, 1f));
        var green = ImGui.GetColorU32(new Vector4(0.05f, 0.55f, 0.34f, 1f));
        drawList.AddCircleFilled(center, radius + Ui(4f), gold, 48);
        drawList.AddCircleFilled(center, radius, dark, 48);
        for (var index = 0; index < 16; index++)
        {
            var angle = -MathF.PI / 2f + index * MathF.PI * 2f / 16f;
            var pocket = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius * 0.78f;
            drawList.AddCircleFilled(pocket, Math.Max(Ui(2.5f), radius * 0.10f), index == 0 ? green : index % 2 == 0 ? red : dark, 12);
        }
        drawList.AddCircle(center, radius * 0.58f, gold, 32, Ui(2f));
        drawList.AddCircleFilled(center, radius * 0.18f, gold, 24);
        var ballAngle = -0.58f;
        var ball = center + new Vector2(MathF.Cos(ballAngle), MathF.Sin(ballAngle)) * radius * 0.88f;
        drawList.AddCircleFilled(ball, Math.Max(Ui(3f), radius * 0.11f), ImGui.GetColorU32(Vector4.One), 16);
    }

    private static double SmoothRoulettePrototype(double value) => value * value * (3d - 2d * value);

    private static void SetRoulettePrototypePixel(byte[] pixels, int width, int height, int x, int y, uint colour)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height)
        {
            return;
        }
        var offset = (y * width + x) * 4;
        pixels[offset] = (byte)(colour & 0xFF);
        pixels[offset + 1] = (byte)((colour >> 8) & 0xFF);
        pixels[offset + 2] = (byte)((colour >> 16) & 0xFF);
        pixels[offset + 3] = 255;
    }

    private static void FillRoulettePrototypeCircle(byte[] pixels, int width, int height, int centerX, int centerY, int radius, uint colour)
    {
        var radiusSquared = radius * radius;
        for (var y = Math.Max(0, centerY - radius); y <= Math.Min(height - 1, centerY + radius); y++)
        for (var x = Math.Max(0, centerX - radius); x <= Math.Min(width - 1, centerX + radius); x++)
        {
            var dx = x - centerX;
            var dy = y - centerY;
            if (dx * dx + dy * dy <= radiusSquared)
            {
                SetRoulettePrototypePixel(pixels, width, height, x, y, colour);
            }
        }
    }

    private static void DrawRoulettePrototypeRing(byte[] pixels, int width, int height, int centerX, int centerY, int innerRadius, int outerRadius, uint colour)
    {
        var innerSquared = innerRadius * innerRadius;
        var outerSquared = outerRadius * outerRadius;
        for (var y = Math.Max(0, centerY - outerRadius); y <= Math.Min(height - 1, centerY + outerRadius); y++)
        for (var x = Math.Max(0, centerX - outerRadius); x <= Math.Min(width - 1, centerX + outerRadius); x++)
        {
            var dx = x - centerX;
            var dy = y - centerY;
            var distance = dx * dx + dy * dy;
            if (distance >= innerSquared && distance <= outerSquared)
            {
                SetRoulettePrototypePixel(pixels, width, height, x, y, colour);
            }
        }
    }

    private static void DrawRoulettePrototypeLine(byte[] pixels, int width, int height, int x0, int y0, int x1, int y1, uint colour, int thickness)
    {
        var dx = Math.Abs(x1 - x0);
        var sx = x0 < x1 ? 1 : -1;
        var dy = -Math.Abs(y1 - y0);
        var sy = y0 < y1 ? 1 : -1;
        var error = dx + dy;
        while (true)
        {
            FillRoulettePrototypeCircle(pixels, width, height, x0, y0, thickness, colour);
            if (x0 == x1 && y0 == y1)
            {
                break;
            }
            var doubled = error * 2;
            if (doubled >= dy)
            {
                error += dy;
                x0 += sx;
            }
            if (doubled <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    private static uint RoulettePayoutHoverBorderColour(uint background)
    {
        // This renderer writes opaque RGB pixels, so blend the cyan border with
        // each cell's background to give it the appearance of 50% opacity.
        const uint cyan = 0x8BE4E3;
        var red = (((cyan >> 16) & 255) + ((background >> 16) & 255)) / 2;
        var green = (((cyan >> 8) & 255) + ((background >> 8) & 255)) / 2;
        var blue = ((cyan & 255) + (background & 255)) / 2;
        return (red << 16) | (green << 8) | blue;
    }

    private static void DrawRoulettePrototypeRectOutline(byte[] pixels, int width, int height, int x, int y, int rectWidth, int rectHeight, uint colour, int thickness)
    {
        FillPictionaryPixels(pixels, width, height, x, y, rectWidth, thickness, colour);
        FillPictionaryPixels(pixels, width, height, x, y + rectHeight - thickness, rectWidth, thickness, colour);
        FillPictionaryPixels(pixels, width, height, x, y, thickness, rectHeight, colour);
        FillPictionaryPixels(pixels, width, height, x + rectWidth - thickness, y, thickness, rectHeight, colour);
    }
}

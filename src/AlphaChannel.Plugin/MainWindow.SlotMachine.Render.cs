using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void ApplySlotMachineTvOverlay(byte[] pixels, int width, int height)
    {
        const uint night = 0x0C0E20, cabinet = 0x2A1A3D, panel = 0x1B1730;
        const uint brass = 0xDBA656, gold = 0xFFDB83, cream = 0xFFF0D2;
        FillPictionaryPixels(pixels, width, height, 0, 0, width, height, night);
        for (var star = 0; star < 42; star++)
        {
            var x = (star * 997 + 47) % width;
            var y = (star * 431 + 19) % height;
            FillPictionaryPixels(pixels, width, height, x, y, 2, 2, 0x56486C);
        }
        FillQuizTvPanel(pixels, width, height, 170, 25, 940, 677, cabinet, 0x654A78);
        DrawRoulettePrototypeRectOutline(pixels, width, height, 183, 38, 914, 650, brass, 6);
        DrawRoulettePrototypeRectOutline(pixels, width, height, 198, 53, 884, 620, gold, 2);
        FillQuizTvPanel(pixels, width, height, 300, 56, 680, 108, 0x38234C, gold);
        for (var light = 0; light < 21; light++)
        {
            var x = 318 + light * 32;
            FillRoulettePrototypeCircle(pixels, width, height, x, 67, 3, gold);
            FillRoulettePrototypeCircle(pixels, width, height, x, 151, 3, gold);
        }
        var jackpot = slotMachinePayout == SlotMachineRules.JackpotPayout &&
            slotMachinePhase is SlotMachinePhase.Results or SlotMachinePhase.Idle;
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 83,
            jackpot ? "BIG WIN! 250 COINS!" : "GOLD SAUCER", 3, jackpot ? gold : cream);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 116,
            jackpot ? SlotShortName(slotMachineWins.FirstOrDefault()?.Name ?? SlotActivePlayerName).ToUpperInvariant() + " - JACKPOT!" : "STARFALL SLOTS",
            jackpot ? 3 : 4, gold);

        FillQuizTvPanel(pixels, width, height, 454, 177, 372, 88, 0x161A2D, gold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 194, "JACKPOT PRIZE", 2, 0xE8BB77);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 224, $"{SlotMachineRules.JackpotPayout} COINS", 3, cream);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 273,
            "MIDDLE ROW PAYS - HELP FOR SYMBOL PAYOUTS", 2, 0xDEC5A9);

        // The face-on coin is cabinet art. A separate edge-on coin is pushed into
        // the horizontal slot during insertion; neither represents an MGP charge.
        FillQuizTvPanel(pixels, width, height, 226, 322, 105, 235, panel, brass);
        DrawPictionaryPixelTextCentered(pixels, width, height, 278, 345, "INSERT", 2, gold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 278, 366, "COIN", 2, gold);
        FillRoulettePrototypeCircle(pixels, width, height, 278, 416, 23, 0xB27A39);
        FillRoulettePrototypeCircle(pixels, width, height, 278, 416, 19, gold);
        DrawSlotPixelDiamond(pixels, width, height, 278, 416, 9, 12, cream);
        FillQuizTvPanel(pixels, width, height, 245, 466, 65, 28, 0x343547, 0xAAB0C0);
        if (slotMachinePhase == SlotMachinePhase.CoinInserting)
        {
            var progress = Math.Clamp((DateTime.UtcNow - slotMachinePhaseStartedUtc).TotalMilliseconds /
                SlotCoinDuration.TotalMilliseconds, 0d, 1d);
            var edgeY = 463 + (int)(16d * progress);
            var halfWidth = 17 - (int)(4d * progress);
            var halfHeight = Math.Max(1, 5 - (int)(3d * progress));
            FillRoulettePrototypeCircle(pixels, width, height,
                278 - halfWidth, edgeY, halfHeight, 0x9D642D);
            FillRoulettePrototypeCircle(pixels, width, height,
                278 + halfWidth, edgeY, halfHeight, 0x9D642D);
            FillPictionaryPixels(pixels, width, height, 278 - halfWidth, edgeY - halfHeight,
                halfWidth * 2, halfHeight * 2 + 1, 0x9D642D);
            FillPictionaryPixels(pixels, width, height, 278 - halfWidth, edgeY - 2,
                halfWidth * 2, 3, gold);
            FillPictionaryPixels(pixels, width, height, 278 - halfWidth + 3, edgeY - halfHeight,
                Math.Max(1, halfWidth * 2 - 6), 2, cream);
        }
        // Draw the dark opening last so the coin visibly disappears behind it.
        FillPictionaryPixels(pixels, width, height, 260, 478, 36, 6, 0x080A13);
        DrawPictionaryPixelTextCentered(pixels, width, height, 278, 521, "PLAY", 2, 0xCDBDD9);

        // The handle visibly descends before reel motion begins.
        FillQuizTvPanel(pixels, width, height, 969, 338, 86, 220, panel, brass);
        FillQuizTvPanel(pixels, width, height, 1000, 377, 24, 155, 0x090C1B, 0x927883);
        var handleY = 323;
        if (slotMachinePhase == SlotMachinePhase.HandlePull)
        {
            var progress = Math.Clamp((DateTime.UtcNow - slotMachinePhaseStartedUtc).TotalMilliseconds /
                SlotHandleDuration.TotalMilliseconds, 0d, 1d);
            handleY += (int)(132d * Math.Sin(progress * Math.PI / 2d));
        }
        DrawRoulettePrototypeLine(pixels, width, height, 1012, 488, 1012, handleY, 0xD7DBE7, 5);
        FillRoulettePrototypeCircle(pixels, width, height, 1012, handleY, 27, 0xF4B3A4);
        FillRoulettePrototypeCircle(pixels, width, height, 1012, handleY, 22, 0xA24261);
        DrawPictionaryPixelTextCentered(pixels, width, height, 1012, 565, "PULL", 2, gold);

        FillQuizTvPanel(pixels, width, height, 344, 293, 592, 258, 0x111529, gold);
        for (var column = 0; column < 3; column++)
        {
            var left = 357 + column * 191;
            FillQuizTvPanel(pixels, width, height, left, 305, 181, 234, 0xF1E5CC, 0xA78A65);
            if (SlotMachineColumnSpinning(column, DateTime.UtcNow))
            {
                const int reelWidth = 175, reelHeight = 228;
                var reelPixels = new byte[reelWidth * reelHeight * 4];
                FillPictionaryPixels(reelPixels, reelWidth, reelHeight,
                    0, 0, reelWidth, reelHeight, 0xE1D4BC);
                var elapsed = Math.Max(0d, (DateTime.UtcNow - slotMachinePhaseStartedUtc).TotalSeconds);
                var position = elapsed * 5d;
                var step = (int)position;
                var offset = (int)((position - step) * 76d);
                for (var virtualRow = -1; virtualRow <= 3; virtualRow++)
                {
                    var top = virtualRow * 76 + offset;
                    FillPictionaryPixels(reelPixels, reelWidth, reelHeight,
                        0, top, reelWidth, 74, virtualRow % 2 == 0 ? 0xFFF1D2u : 0xE1D4BCu);
                    var sample = SlotMachineRollingSymbol(column, virtualRow, step);
                    DrawSlotPixelSymbol(reelPixels, reelWidth, reelHeight, sample, 87, top + 36, 29);
                    DrawPictionaryPixelText(reelPixels, reelWidth, reelHeight, 5, top + 6,
                        sample.ToString("00"), 1, 0x665B64);
                    FillPictionaryPixels(reelPixels, reelWidth, reelHeight,
                        0, top + 74, reelWidth, 2, 0xB39C78);
                }
                for (var y = 0; y < reelHeight; y++)
                    Buffer.BlockCopy(reelPixels, y * reelWidth * 4, pixels,
                        ((308 + y) * width + left + 3) * 4, reelWidth * 4);
            }
            else
            {
                for (var row = 0; row < 3; row++)
                {
                    var top = 308 + row * 76;
                    FillPictionaryPixels(pixels, width, height, left + 3, top, 175, 74,
                        row == 1 ? 0xFFF1D2u : 0xE1D4BCu);
                    if (row < 2)
                        FillPictionaryPixels(pixels, width, height, left + 3, top + 74, 175, 2, 0xB39C78);
                    var sample = slotMachineSymbols[row * 3 + column];
                    DrawSlotPixelSymbol(pixels, width, height, sample, left + 90, top + 36, 29);
                    DrawPictionaryPixelText(pixels, width, height, left + 6, top + 6,
                        sample.ToString("00"), 1, 0x665B64);
                }
            }
        }
        DrawRoulettePrototypeRectOutline(pixels, width, height, 351, 383, 578, 78, gold, 4);
        DrawSlotPixelTriangle(pixels, width, height, 345, 420, 345, 444, 329, 432, gold);
        DrawSlotPixelTriangle(pixels, width, height, 935, 420, 935, 444, 951, 432, gold);

        FillQuizTvPanel(pixels, width, height, 229, 585, 822, 83, 0x111629, brass);
        FillQuizTvPanel(pixels, width, height, 244, 596, 220, 59, 0x23213C, 0x74607D);
        DrawPictionaryPixelTextCentered(pixels, width, height, 354, 607, "YOUR COINS", 2, 0xCDBDD9);
        DrawPictionaryPixelTextCentered(pixels, width, height, 354, 630,
            (slotMachinePlayers.GetValueOrDefault(LocalSlotPlayerId)?.Coins ?? SlotMachineRules.StartingCoins).ToString(), 3, gold);
        FillQuizTvPanel(pixels, width, height, 474, 596, 360, 59, 0x753354, gold);
        var prompt = slotMachinePhase switch
        {
            SlotMachinePhase.Idle => "INSERT COIN",
            SlotMachinePhase.CoinInserting => "INSERTING...",
            SlotMachinePhase.Ready => "PULL HANDLE",
            SlotMachinePhase.HandlePull => "PULLING...",
            SlotMachinePhase.Results => slotMachinePayout > 0 ? $"+{slotMachinePayout} COINS!" : "NO MATCH",
            _ => "REELS SPINNING",
        };
        DrawPictionaryPixelTextCentered(pixels, width, height, 654, 602,
            slotMachineActivePlayerId is not null ? SlotShortName(SlotActivePlayerName) + "'S TURN" :
                slotMachineShared ? $"SHARED - {slotMachineQueue.Count} QUEUED" : "HOST-ONLY PLAY", 2, gold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 654, 632, prompt, 2, cream);
        FillQuizTvPanel(pixels, width, height, 844, 596, 192, 59, 0x23213C, 0x74607D);
        // Keep the settled result visible while the coin slot is ready for the next player.
        var showResult = slotMachinePhase == SlotMachinePhase.Results ||
            slotMachinePhase == SlotMachinePhase.Idle && !string.IsNullOrEmpty(slotMachineResult);
        DrawPictionaryPixelTextCentered(pixels, width, height, 940, 608, showResult ? slotMachineResult.ToUpperInvariant().Replace(" WIN", "") : "SPIN COST", 2, cream);
        DrawPictionaryPixelTextCentered(pixels, width, height, 940, 631, showResult ? slotMachinePayout.ToString() : "5 COINS", 2, gold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 5,
            "PLAY COINS ONLY - NO GIL OR MGP SPENT OR AWARDED", 2, 0xD2BDD4);
        var localHover = slotMachinePageHover ?? slotMachineTvHover;
        BingoButton(pixels, width, height, 1120, 35, 130, 55, "LEDGER",
            localHover is { } lh && lh.X * width >= 1120 && lh.Y * height is >= 35 and <= 90, false, true);
        BingoButton(pixels, width, height, 1120, 105, 130, 55, "HELP",
            localHover is { } hh && hh.X * width is >= 1120 and <= 1250 && hh.Y * height is >= 105 and <= 160, false, true);
        if (slotMachinePhase == SlotMachinePhase.Results && slotMachinePayout > 0)
            DrawRoulettePrototypeRectOutline(pixels, width, height, 351, 383, 578, 78,
                SlotMachineRules.IsTriple(slotMachineSymbols) ? 0xFFF0D2u : 0x75E8A8u, 5);
        DrawSlotCoinRain(pixels, width, height);
        if (slotMachineLedgerOpen) DrawSlotLedger(pixels, width, height);
        if (slotMachineHelpOpen) DrawSlotMachineHelp(pixels, width, height);
        if (!slotMachineLedgerOpen && !slotMachineHelpOpen && CanOperateSlotMachine && localHover is { } hover)
        {
            var hoverX = hover.X * width;
            var hoverY = hover.Y * height;
            if (!LocalSlotTurn && !slotMachineQueue.Contains(LocalSlotPlayerId) &&
                hoverX is >= 238 and <= 345 && hoverY is >= 325 and <= 548)
                DrawRoulettePrototypeRectOutline(pixels, width, height, 224, 320, 109, 239, cream, 3);
            else if (LocalSlotTurn && slotMachinePhase == SlotMachinePhase.Ready &&
                     hoverX is >= 968 and <= 1055 && hoverY is >= 265 and <= 570)
                DrawRoulettePrototypeRectOutline(pixels, width, height, 967, 336, 90, 224, cream, 3);
        }
    }

    private bool SlotMachineColumnSpinning(int column, DateTime now)
    {
        if (slotMachinePhase != SlotMachinePhase.Spinning) return false;
        var elapsed = Math.Max(0d, (now - slotMachinePhaseStartedUtc).TotalSeconds);
        return elapsed < 2.2d + column * 0.6d;
    }

    private int SlotMachineRollingSymbol(int column, int virtualRow, int step)
    {
        // A symbol keeps its identity while scrolling across a row boundary.
        return SlotMachineRules.RollingSymbol(slotMachineSpinSeed, column, virtualRow - step);
    }

    private static void DrawSlotPixelTriangle(byte[] pixels, int width, int height,
        int x1, int y1, int x2, int y2, int x3, int y3, uint color)
    {
        var minX = Math.Max(0, Math.Min(x1, Math.Min(x2, x3)));
        var maxX = Math.Min(width - 1, Math.Max(x1, Math.Max(x2, x3)));
        var minY = Math.Max(0, Math.Min(y1, Math.Min(y2, y3)));
        var maxY = Math.Min(height - 1, Math.Max(y1, Math.Max(y2, y3)));
        var area = (long)(x2 - x1) * (y3 - y1) - (long)(y2 - y1) * (x3 - x1);
        if (area == 0) return;
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            var a = (long)(x2 - x1) * (y - y1) - (long)(y2 - y1) * (x - x1);
            var b = (long)(x3 - x2) * (y - y2) - (long)(y3 - y2) * (x - x2);
            var c = (long)(x1 - x3) * (y - y3) - (long)(y1 - y3) * (x - x3);
            if ((a >= 0 && b >= 0 && c >= 0) || (a <= 0 && b <= 0 && c <= 0))
            {
                var offset = (y * width + x) * 4;
                pixels[offset] = (byte)(color & 0xFF);
                pixels[offset + 1] = (byte)((color >> 8) & 0xFF);
                pixels[offset + 2] = (byte)((color >> 16) & 0xFF);
                pixels[offset + 3] = 255;
            }
        }
    }

    private static void DrawSlotPixelDiamond(byte[] pixels, int width, int height,
        int x, int y, int halfWidth, int halfHeight, uint color)
    {
        DrawSlotPixelTriangle(pixels, width, height, x, y - halfHeight, x - halfWidth, y,
            x + halfWidth, y, color);
        DrawSlotPixelTriangle(pixels, width, height, x, y + halfHeight, x - halfWidth, y,
            x + halfWidth, y, color);
    }

    private static void DrawSlotPixelSymbol(byte[] pixels, int width, int height,
        int sample, int cx, int cy, int radius)
    {
        int X(float value) => cx + (int)(value * radius);
        int Y(float value) => cy + (int)(value * radius);
        void C(float x, float y, float r, uint color) =>
            FillRoulettePrototypeCircle(pixels, width, height, X(x), Y(y),
                Math.Max(1, (int)(r * radius)), color);
        void L(float x1, float y1, float x2, float y2, uint color, int thick = 2) =>
            DrawRoulettePrototypeLine(pixels, width, height, X(x1), Y(y1), X(x2), Y(y2), color, thick);
        void T(float x1, float y1, float x2, float y2, float x3, float y3, uint color) =>
            DrawSlotPixelTriangle(pixels, width, height,
                X(x1), Y(y1), X(x2), Y(y2), X(x3), Y(y3), color);
        void B(float x1, float y1, float x2, float y2, uint color) =>
            FillPictionaryPixels(pixels, width, height, X(x1), Y(y1),
                Math.Max(1, X(x2) - X(x1)), Math.Max(1, Y(y2) - Y(y1)), color);
        void D(float x, float y, float rw, float rh, uint color) =>
            DrawSlotPixelDiamond(pixels, width, height, X(x), Y(y),
                Math.Max(1, (int)(rw * radius)), Math.Max(1, (int)(rh * radius)), color);
        const uint ink = 0x273047, white = 0xFFF8EE, gold = 0xFFD16D;
        switch (sample)
        {
            case 5: // Cactuar
                L(-.21f, .38f, -.45f, .96f, 0x66CE5B, 4);
                L(.21f, .38f, .45f, .96f, 0x66CE5B, 4);
                L(-.26f, -.10f, -.67f, .21f, 0x66CE5B, 5);
                L(-.67f, .21f, -.67f, -.12f, 0x66CE5B, 5);
                L(.26f, -.10f, .67f, -.32f, 0x66CE5B, 5);
                L(.67f, -.32f, .67f, -.56f, 0x66CE5B, 5);
                B(-.35f, -.52f, .35f, .43f, 0x66CE5B);
                C(0, -.52f, .35f, 0x66CE5B);
                C(0, .43f, .30f, 0x66CE5B);
                C(-.13f, -.18f, .055f, ink);
                C(.13f, -.18f, .055f, ink);
                B(-.07f, -.01f, .07f, .22f, ink);
                break;
            case 11: // Blue aetheryte
                D(0, 0, .52f, .88f, 0x76D9F3);
                T(0, -.88f, -.52f, 0, 0, .12f, 0xE8FBFF);
                T(0, -.88f, .52f, 0, 0, .12f, 0x528DD8);
                break;
            case 13: // Gil
                C(0, 0, .74f, 0xB77839);
                C(0, 0, .61f, gold);
                DrawRoulettePrototypeRing(pixels, width, height, cx, cy,
                    (int)(.45f * radius), (int)(.50f * radius), 0xFFF3B8);
                D(0, 0, .21f, .32f, white);
                break;
            case 17: // Tonberry lantern
                T(0, -.86f, -.67f, .76f, .67f, .76f, 0x4C9E72);
                C(0, -.43f, .48f, 0x80BB76);
                C(-.17f, -.44f, .06f, gold);
                C(.17f, -.44f, .06f, gold);
                L(.45f, .31f, .77f, .17f, 0x70553D);
                B(.66f, .04f, .95f, .47f, gold);
                B(.71f, .10f, .90f, .40f, 0xFFF1A1);
                break;
            case 27: // Triple Triad card
                B(-.53f, -.79f, .53f, .79f, 0x5D519B);
                B(-.43f, -.68f, .43f, .68f, gold);
                B(-.36f, -.60f, .36f, .60f, 0x3E417A);
                D(0, -.12f, .31f, .39f, 0x8FE3F7);
                for (var i = -1; i <= 1; i++) C(i * .21f, .47f, .07f, gold);
                break;
            case 38: // Flower Goobbue
                C(0, .13f, .68f, 0xA89A83);
                C(-.49f, .58f, .22f, 0xA89A83);
                C(.49f, .58f, .22f, 0xA89A83);
                B(-.57f, -.45f, .57f, -.25f, 0x4D8F5C);
                for (var i = -1; i <= 1; i++)
                    T(i * .30f, -.35f, i * .30f - .14f, -.76f,
                        i * .30f + .15f, -.39f, 0x6AC665);
                C(.22f, -.72f, .13f, 0xF391C8);
                C(.22f, -.72f, .05f, gold);
                C(-.28f, -.02f, .06f, ink);
                C(.28f, -.02f, .06f, ink);
                B(-.39f, .28f, .39f, .46f, 0x635851);
                break;
            case 40: // Blossom Korpokkur
                C(0, .30f, .56f, 0x9ED17A);
                L(0, -.22f, 0, -.64f, 0x569C59, 3);
                T(0, -.53f, -.78f, -.88f, -.18f, -.39f, 0x6CBC68);
                T(0, -.53f, .78f, -.88f, .18f, -.39f, 0x6CBC68);
                C(0, -.74f, .16f, 0xF5A3CB);
                C(-.21f, .25f, .055f, ink);
                C(.21f, .25f, .055f, ink);
                T(-.08f, .40f, .08f, .40f, 0, .50f, gold);
                break;
            case 43: // Fire materia
                C(0, 0, .73f, 0xA75A5F);
                C(0, 0, .61f, 0xFA8058);
                DrawRoulettePrototypeRing(pixels, width, height, cx, cy,
                    (int)(.44f * radius), (int)(.49f * radius), white);
                D(0, 0, .23f, .34f, white);
                C(-.28f, -.28f, .09f, 0xFFF3D7);
                break;
            case 46: // Ornate coffer
                const uint chest = 0x67538C;
                B(-.70f, -.20f, .70f, .66f, chest);
                C(-.45f, -.30f, .29f, chest);
                C(.45f, -.30f, .29f, chest);
                B(-.74f, -.43f, .74f, -.04f, chest);
                L(-.68f, -.04f, .68f, -.04f, gold, 3);
                L(-.50f, -.43f, -.50f, .64f, gold, 3);
                L(.50f, -.43f, .50f, .64f, gold, 3);
                B(-.13f, -.12f, .13f, .28f, gold);
                C(0, .07f, .05f, ink);
                D(0, -.67f, .14f, .17f, 0xCE98F2);
                break;
            case 50: // Rainbow glamour prism
                D(0, 0, .51f, .79f, 0x8FE5F4);
                T(0, -.79f, -.51f, 0, 0, .17f, white);
                T(0, -.79f, .51f, 0, 0, .17f, 0xF6CD70);
                T(-.51f, 0, 0, .79f, 0, .17f, 0x74C9D8);
                T(.51f, 0, 0, .79f, 0, .17f, 0x8DD785);
                D(-.66f, .43f, .07f, .12f, white);
                D(.66f, .43f, .07f, .12f, white);
                break;
            case 55: // Fat Chocobo
                C(0, .29f, .64f, gold);
                C(0, -.39f, .40f, gold);
                T(-.25f, -.59f, -.47f, -.96f, .02f, -.66f, gold);
                T(.02f, -.62f, .06f, -.99f, .31f, -.64f, gold);
                C(-.18f, -.41f, .055f, ink);
                C(.18f, -.41f, .055f, ink);
                T(-.18f, -.19f, .18f, -.19f, 0, .03f, 0xEC985A);
                T(-.33f, .25f, -.77f, .06f, -.52f, .55f, gold);
                T(.33f, .25f, .77f, .06f, .52f, .55f, gold);
                break;
        }
    }
}

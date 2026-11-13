using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private DateTime chocoRacePageIntroStartedUtc = DateTime.MinValue;
    private DateTime chocoRaceTvIntroStartedUtc = DateTime.MinValue;
    private bool chocoRacePageWasVisible;
    private bool chocoRaceTvWasVisible;
    private bool chocoRaceViewerTvWasEnabled;

    private static bool ChocoRaceIntroActive(DateTime now, DateTime startedUtc) =>
        startedUtc != DateTime.MinValue &&
        (now - startedUtc).TotalSeconds < ChocoRaceIntroSeconds;

    // A newly opened surface has no start time until UpdateRacePreview runs.
    // Keep its controls blocked during that first frame too.
    private bool ChocoRacePageIntroBlocksInput => ChocoRaceActive &&
        (chocoRacePageIntroStartedUtc == DateTime.MinValue ||
         ChocoRaceIntroActive(DateTime.UtcNow, chocoRacePageIntroStartedUtc));

    private bool ChocoRaceTvIntroBlocksInput => ChocoRaceActive &&
        (chocoRaceTvIntroStartedUtc == DateTime.MinValue ||
         ChocoRaceIntroActive(DateTime.UtcNow, chocoRaceTvIntroStartedUtc));

    private void ResetChocoRaceIntroState()
    {
        chocoRacePageIntroStartedUtc = DateTime.MinValue;
        chocoRaceTvIntroStartedUtc = DateTime.MinValue;
        chocoRacePageWasVisible = false;
        chocoRaceTvWasVisible = false;
        chocoRaceViewerTvWasEnabled = false;
    }

    private void UpdateChocoRaceIntroVisibility(bool showPage, bool showTv, DateTime now)
    {
        if (showPage && !chocoRacePageWasVisible)
            chocoRacePageIntroStartedUtc = now;
        if (showTv && (!chocoRaceTvWasVisible ||
                       stream.Mode == StreamMode.Viewing && ViewerTvEnabled && !chocoRaceViewerTvWasEnabled))
            chocoRaceTvIntroStartedUtc = now;
        chocoRacePageWasVisible = showPage;
        chocoRaceTvWasVisible = showTv;
        chocoRaceViewerTvWasEnabled = ViewerTvEnabled;
    }

    private void ApplyChocoRaceIntroPixels(byte[] racePixels, DateTime now, DateTime startedUtc)
    {
        if (!ChocoRaceIntroActive(now, startedUtc)) return;

        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        var elapsed = Math.Max(0d, (now - startedUtc).TotalSeconds);
        var introPixels = new byte[racePixels.Length];
        DrawChocoRaceIntroPixels(introPixels, elapsed);

        // Lift the attract screen away during its final beat to reveal the live table.
        var slide = Math.Clamp((elapsed - (ChocoRaceIntroSeconds - 0.3d)) / 0.3d, 0d, 1d);
        var visibleRows = (int)Math.Round(height * (1d - slide));
        var stride = width * 4;
        Buffer.BlockCopy(introPixels, (height - visibleRows) * stride,
            racePixels, 0, visibleRows * stride);
    }

    private void DrawChocoRaceIntroPixels(byte[] pixels, double elapsed)
    {
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        const uint background = 0x17131E, purple = 0x8959ED, gold = 0xF6BC4E;
        const uint white = 0xF5F3FB, grass = 0x114321, brightGrass = 0x33783A;
        var colours = new uint[] { 0xF5CB48, 0x56BFEF, 0xF27B99, 0xF5F4EE, 0xB478E9, 0x474A55 };
        var wings = new uint[] { 0xF9DE79, 0xA9E6FF, 0xFFACBB, 0xFFFFFF, 0xD4A8FA, 0x737887 };
        var badges = new uint[] { 0xC93C50, 0x397CD3, 0x20965B, 0xF1C343, 0x9252D5, 0x6C717B };

        FillPictionaryPixels(pixels, width, height, 0, 0, width, height, background);
        FillQuizTvPanel(pixels, width, height, 10, 10, 1260, 700, background, purple);
        FillPictionaryPixels(pixels, width, height, 19, 164, 1242, 3, purple);
        FillPictionaryPixels(pixels, width, height, 19, 696, 1242, 3, purple);
        for (var corner = 0; corner < 2; corner++)
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
        {
            if ((row + column + (int)(elapsed * 5d)) % 2 != 0) continue;
            var x = corner == 0 ? 28 + column * 13 : 1213 + column * 13;
            FillPictionaryPixels(pixels, width, height, x, 24 + row * 13, 13, 13, purple);
        }

        const string title = "CHOCO RACEBETS";
        var titleLetters = Math.Clamp((int)(elapsed * title.Length / 0.7d) + 1, 0, title.Length);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 52,
            title[..titleLetters], 6, gold);
        if (elapsed >= 0.55d)
        {
            DrawRaceDuckLarge(pixels, width, height, 263, 135);
            DrawRaceDuckLarge(pixels, width, height, 1017, 135);
        }
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 123,
            "SIX CHOCOBOS. ONE GLORIOUS WINNER.", 2, gold);

        // The gates appear first, then the chocos pop into them one by one.
        for (var lane = 0; lane < 6; lane++)
        {
            var center = 155 + lane * 177;
            FillPictionaryPixels(pixels, width, height, center - 52, 322, 4, 189, 0x5DB867);
            FillPictionaryPixels(pixels, width, height, center + 48, 322, 4, 189, 0x5DB867);
            FillQuizTvPanel(pixels, width, height, center - 49, 264, 98, 63,
                badges[lane], 0x83D18A);
            DrawPictionaryPixelTextCentered(pixels, width, height, center, 276,
                (lane + 1).ToString(), 4, white);

            var arrival = 0.8d + lane * 0.16d;
            if (elapsed < arrival) continue;
            var pop = Math.Clamp((elapsed - arrival) / 0.3d, 0d, 1d);
            var hop = 14d * Math.Sin(Math.PI * pop);
            if (lane == 2 && elapsed is >= 1.85d and < 2.3d)
                hop += 10d * Math.Sin(Math.PI * (elapsed - 1.85d) / 0.45d);
            if (lane == 4 && elapsed is >= 2.2d and < 2.65d)
                hop += 10d * Math.Sin(Math.PI * (elapsed - 2.2d) / 0.45d);
            DrawRaceDuck(pixels, width, height, center, 509 - (int)Math.Round(hop),
                colours[lane], wings[lane], lane == 5 ? white : 0x232331);
            if (hop >= 5d)
            {
                RaceLine(pixels, width, height, center + 33, 464,
                    center + 38, 458, gold, 2);
                RaceLine(pixels, width, height, center + 40, 472,
                    center + 47, 469, gold, 2);
            }
        }

        FillQuizTvPanel(pixels, width, height, 73, 509, 1052, 42, grass, 0x5DB867);
        for (var tuft = 0; tuft < 48; tuft++)
        {
            var x = 84 + tuft * 22;
            var y = 515 + tuft % 3 * 7;
            FillPictionaryPixels(pixels, width, height, x, y, 3, 5, brightGrass);
            FillPictionaryPixels(pixels, width, height, x + 4, y - 3, 2, 5, brightGrass);
        }

        FillQuizTvPanel(pixels, width, height, 1139, 277, 105, 260, background, gold);
        var chant = new[] { "KWEH", "BET", "CHEER", "REPEAT" };
        for (var line = 0; line < chant.Length; line++)
        {
            DrawPictionaryPixelTextCentered(pixels, width, height, 1191,
                302 + line * 50, chant[line], 2, gold);
            if (line == chant.Length - 1) continue;
            var arrowY = 328 + line * 50;
            FillPictionaryPixels(pixels, width, height, 1188, arrowY - 5, 6, 6, purple);
            RaceTriangle(pixels, width, height,
                1182, arrowY, 1200, arrowY, 1191, arrowY + 10, purple);
        }
        DrawRaceDuck(pixels, width, height, 1191, 525,
            0xF5CB48, 0xF9DE79, 0x232331);

        FillQuizTvPanel(pixels, width, height, 34, 586, 1212, 105, background, gold);
        var message = elapsed < 1.85d ? "GET READY!" : "READY? PLACE YOUR BETS!";
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 617,
            message, elapsed < 1.85d ? 5 : 4, gold);
        for (var side = 0; side < 2; side++)
        for (var row = 0; row < 2; row++)
        for (var column = 0; column < 4; column++)
        {
            if ((row + column) % 2 != 0) continue;
            var x = side == 0 ? 61 + column * 18 : 1147 + column * 18;
            FillPictionaryPixels(pixels, width, height, x, 623 + row * 18, 18, 18, gold);
        }
    }
}

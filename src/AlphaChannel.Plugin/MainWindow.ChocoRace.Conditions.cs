using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawChocoRaceConditionsPixels(byte[] pixels)
    {
        if (chocoRacePhase is not (ChocoRacePhase.WaitingForBet or ChocoRacePhase.Betting))
            return;

        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        const uint panel = 0x211923, border = 0xF6BC4E, muted = 0xB9A9CF;
        FillQuizTvPanel(pixels, width, height, 545, 194, 250, 116, panel, border);
        FillPictionaryPixels(pixels, width, height, 555, 251, 230, 2, 0x674786);

        if (chocoRaceWeather == ChocoRaceWeather.Dry)
            DrawChocoRaceSunIcon(pixels, 574, 228);
        else
        {
            // Match the shallow, layered puddle drawn on the race track.
            FillPictionaryPixels(pixels, width, height, 561, 225, 26, 3, 0x3484B1);
            FillPictionaryPixels(pixels, width, height, 556, 228, 36, 5, 0x3484B1);
            FillPictionaryPixels(pixels, width, height, 562, 233, 24, 2, 0x276989);
            FillPictionaryPixels(pixels, width, height, 565, 227, 16, 2, 0x83D8EA);
            FillPictionaryPixels(pixels, width, height, 583, 231, 5, 1, 0x83D8EA);
        }
        DrawChocoRaceConditionLabel(pixels, 601, 201, "WEATHER", muted);
        DrawPictionaryPixelText(pixels, width, height, 601, 221,
            chocoRaceWeather.ToString().ToUpperInvariant(), 2, 0x9FE5F4);

        // Small faceted rock beside the terrain reading.
        FillPictionaryPixels(pixels, width, height, 559, 281, 29, 4, 0x343B40);
        RaceTriangle(pixels, width, height,
            560, 281, 570, 268, 576, 281, 0x9BA3A2);
        RaceTriangle(pixels, width, height,
            572, 281, 580, 271, 588, 281, 0x6C7879);
        DrawChocoRaceConditionLabel(pixels, 601, 257, "TERRAIN", muted);
        DrawPictionaryPixelText(pixels, width, height, 601, 277,
            chocoRaceTerrain.ToString().ToUpperInvariant(), 2, border);
    }

    private static void DrawChocoRaceConditionLabel(byte[] pixels, int x, int y,
        string text, uint colour)
    {
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        static int Scaled(int coordinate) => (coordinate * 3 + 1) / 2;
        foreach (var character in text)
        {
            var rows = PictionaryGlyph(character);
            for (var row = 0; row < rows.Length; row++)
            for (var column = 0; column < 5; column++)
            {
                if ((rows[row] & (1 << (4 - column))) == 0) continue;
                FillPictionaryPixels(pixels, width, height,
                    x + Scaled(column), y + Scaled(row),
                    Scaled(column + 1) - Scaled(column),
                    Scaled(row + 1) - Scaled(row), colour);
            }
            x += Scaled(6);
        }
    }
}

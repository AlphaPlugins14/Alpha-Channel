using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static int ChocoRaceWeatherIconX(int lane) => 74 + lane * 165;
    private const int ChocoRaceWeatherIconY = 471;

    private void DrawChocoRaceWeatherIcon(byte[] pixels, int lane)
    {
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        var x = ChocoRaceWeatherIconX(lane);
        var y = ChocoRaceWeatherIconY;
        if (chocoRaceRacers[lane].PrefersWetWeather)
        {
            RaceTriangle(pixels, width, height,
                x, y - 9, x - 7, y + 2, x + 7, y + 2, 0x56BFEF);
            RaceCircle(pixels, width, height, x, y + 2, 6, 0x56BFEF);
            FillPictionaryPixels(pixels, width, height, x - 3, y - 1, 2, 4, 0xB9EEFF);
        }
        else
            DrawChocoRaceSunIcon(pixels, x, y);
    }

    private static void DrawChocoRaceSunIcon(byte[] pixels, int x, int y)
    {
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        RaceCircle(pixels, width, height, x, y, 5, 0xF6BC4E);
        for (var ray = 0; ray < 8; ray++)
        {
            var angle = ray * Math.PI / 4d;
            RaceLine(pixels, width, height,
                x + (int)Math.Round(Math.Cos(angle) * 7d),
                y + (int)Math.Round(Math.Sin(angle) * 7d),
                x + (int)Math.Round(Math.Cos(angle) * 10d),
                y + (int)Math.Round(Math.Sin(angle) * 10d), 0xF6BC4E, 2);
        }
    }

    private void DrawChocoRaceWeatherPreferenceTooltip(byte[] pixels)
    {
        var lane = chocoRaceHoveredControlTarget - 800;
        if (lane is < 0 or >= 6 || chocoRaceScoreboardOpen || chocoRaceSettingsOpen ||
            chocoRaceHelpOpen || chocoRacePhase == ChocoRacePhase.Results) return;

        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        const int boxWidth = 252;
        var x = Math.Clamp(ChocoRaceWeatherIconX(lane) - 40, 18, width - boxWidth - 18);
        const int y = 406;
        FillQuizTvPanel(pixels, width, height, x, y, boxWidth, 39, 0x21162F, 0xF6BC4E);
        DrawPictionaryPixelText(pixels, width, height, x + 12, y + 12,
            chocoRaceRacers[lane].PrefersWetWeather
                ? "Prefers wet weather" : "Prefers dry weather", 2, 0xFFF5D8);
    }
}

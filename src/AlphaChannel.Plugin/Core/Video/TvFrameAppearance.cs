namespace AlphaChannel.Plugin.Video;

internal static class TvFrameAppearance
{
    internal static bool SupportsColour(TvFrameStyle style) => style is
        TvFrameStyle.Neon or TvFrameStyle.Arcade or TvFrameStyle.ArcadeDeluxe or TvFrameStyle.Celestial or
        TvFrameStyle.RaidNight or TvFrameStyle.BroadcastBooth or TvFrameStyle.MidnightScreening or
        TvFrameStyle.Celebration or TvFrameStyle.RoyalGallery or TvFrameStyle.GrandCinema;

    internal static bool SupportsGlow(TvFrameStyle style) => style is
        TvFrameStyle.Neon or TvFrameStyle.ArcadeDeluxe or TvFrameStyle.Celestial or TvFrameStyle.RaidNight or
        TvFrameStyle.BroadcastBooth or TvFrameStyle.MidnightScreening or TvFrameStyle.Celebration;

    internal static TvFrameSelection TextureKey(TvFrameSelection frame)
    {
        frame = frame.Normalized();
        return frame with { Colour = SupportsColour(frame.Style) ? frame.Colour : 0,
            Glow = SupportsGlow(frame.Style) ? frame.Glow : TvFrameGlow.Off };
    }

    internal static string ColourHelp(TvFrameStyle style) => style switch
    {
        TvFrameStyle.RoyalGallery => "Changes the jewels.",
        TvFrameStyle.GrandCinema => "Changes the curtains.",
        TvFrameStyle.RaidNight => "Changes the crystals.",
        TvFrameStyle.ArcadeDeluxe => "Changes the lights and speaker rings.",
        TvFrameStyle.BroadcastBooth => "Changes the lights and accent strips.",
        TvFrameStyle.Celebration => "Changes the ribbon and border.",
        TvFrameStyle.Celestial => "Changes the border and stars.",
        TvFrameStyle.MidnightScreening => "Changes the border accent.",
        _ => SupportsColour(style) ? "Choose a colour or use the picker." : "Not available for this frame.",
    };
}

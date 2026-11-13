namespace AlphaChannel.Plugin.Video;

internal static class TvFrameAccess
{
    internal static bool CanRender(bool audioOnly, bool musicScenePlaying) => !audioOnly && !musicScenePlaying;
    internal static bool RequiresPatreon(TvFrameStyle style) =>
        Enum.IsDefined(style) && style > TvFrameStyle.Arcade;

    internal static bool CanSelect(TvFrameStyle style, int verifiedTier) =>
        Enum.IsDefined(style) && (!RequiresPatreon(style) || verifiedTier >= 1);

    internal static TvFrameSelection AllowedPersonalFrame(TvFrameSelection? saved, int verifiedTier)
    {
        var frame = (saved ?? new()).Normalized();
        return CanSelect(frame.Style, verifiedTier) ? frame : frame with { Style = TvFrameStyle.None };
    }

    // An initial pending verification hides premium artwork, but must not erase
    // a paying member's saved preference. Confirmed loss/expiry persists None.
    internal static bool ShouldReset(TvFrameSelection? saved, int verifiedTier, bool membershipResolved) =>
        membershipResolved && verifiedTier < 1 && RequiresPatreon(saved?.Style ?? TvFrameStyle.None);
}

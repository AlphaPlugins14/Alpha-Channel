using System.Globalization;

namespace AlphaChannel.Plugin.Video;

public enum TvFrameStyle
{
    None, Minimal, Neon, Cinema, RetroTv, Ornate, Arcade,
    Catnap, LittleSpectators, DragonsHoard, Botanical, Celestial, Storybook,
    GrandCinema, RoyalGallery, ArcadeDeluxe, RetroDeluxe, BroadcastBooth,
    MidnightScreening, PumpkinPatch, WinterLodge, Celebration, RaidNight,
}
public enum TvFrameGlow { Off, Subtle, Bright }

public sealed record TvFrameSelection(TvFrameStyle Style = TvFrameStyle.None, uint Colour = 0x9B55FF, TvFrameGlow Glow = TvFrameGlow.Subtle)
{
    internal TvFrameSelection Normalized() => new(Enum.IsDefined(Style) ? Style : TvFrameStyle.None,
        Colour & 0xFFFFFF, Enum.IsDefined(Glow) ? Glow : TvFrameGlow.Subtle);
    internal static TvFrameSelection Resolve(TvFrameSelection? personal, bool viewer, bool followHost, string? reason) =>
        viewer && followHost ? FromReason(reason) : (personal ?? new()).Normalized();

    // The relay already caches/replays Reason. A small versioned tag also
    // works for an idle room, without putting presentation data in a media URL.
    private const string Tag = ";FRAME1:";
    internal static string LocationPart(string? reason) => (reason ?? "TV1:?").Split(';')[0];
    internal string EncodeReason(string? reason)
    {
        var value = Normalized();
        return $"{LocationPart(reason)}{Tag}{(int)value.Style},{value.Colour:X6},{(int)value.Glow}" +
            (reason?.EndsWith(";TV-HELD", StringComparison.Ordinal) == true ? ";TV-HELD" : "");
    }
    internal static TvFrameSelection FromReason(string? reason)
    {
        if (reason is null || reason.Length > 256) return new();
        var start = reason.IndexOf(Tag, StringComparison.Ordinal);
        if (start < 0) return new();
        var parts = reason[(start + Tag.Length)..].Split(';')[0].Split(',');
        return parts.Length == 3 && int.TryParse(parts[0], out var style) &&
            uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var colour) &&
            int.TryParse(parts[2], out var glow)
            ? new TvFrameSelection((TvFrameStyle)style, colour, (TvFrameGlow)glow).Normalized() : new();
    }
}

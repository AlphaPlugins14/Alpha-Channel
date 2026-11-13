using Dalamud.Interface;

namespace AlphaChannel.Plugin;

internal static class UiInternationalGlyphRanges
{
    // Include Korean independently of the viewer's client language. Keep the
    // existing Japanese coverage and one final ImGui range terminator.
    internal static readonly ushort[] Cjk =
    [
        .. GlyphRangesJapanese.GlyphRanges.ToArray()[..^1],
        0x1100, 0x11ff, // Hangul Jamo
        0x3131, 0x318e, // Hangul compatibility Jamo
        0xac00, 0xd7a3, // Every modern precomposed Hangul syllable
        0,
    ];
}

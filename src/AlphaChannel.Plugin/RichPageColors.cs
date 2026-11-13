using System.Numerics;

namespace AlphaChannel.Plugin;

// Rich hub surfaces were designed against Blue + Midnight. Keep that reference
// intact, then derive the same tonal steps from each background and accent.
// Artwork and semantic colours (gold badges, error/success) do not use this map.
internal readonly struct RichPageColors(ThemeColors colors, UiBackground background, bool wallpaperVisible)
{
    private static readonly ThemeColors Reference = ThemeCatalog.Get(UiTheme.Blue, UiBackground.Midnight);
    private bool MidnightSurfaces => background is UiBackground.Midnight or UiBackground.Custom;

    internal Vector4 Surface(int referenceRgb)
    {
        var reference = Rgb(referenceRgb);
        if (MidnightSurfaces) return Backing(reference);
        var ratio = Brightness(reference) / Brightness(Reference.CardBg);
        var mapped = new Vector4(colors.CardBg.X * ratio, colors.CardBg.Y * ratio, colors.CardBg.Z * ratio, 1);
        return Backing(Vector4.Clamp(mapped, Vector4.Zero, Vector4.One));
    }

    internal Vector4 Tint(int referenceRgb)
    {
        var reference = Rgb(referenceRgb);
        if (colors.Accent == Reference.Accent) return reference;
        var source = Hsv(reference);
        var baseline = Hsv(Reference.Accent);
        var accent = Hsv(colors.Accent);
        return FromHsv(source.X + accent.X - baseline.X,
            Math.Clamp(source.Y * accent.Y / baseline.Y, 0, 1),
            Math.Clamp(source.Z * accent.Z / baseline.Z, 0, 1));
    }

    internal Vector4 Selected(int referenceRgb)
    {
        var tint = Tint(referenceRgb);
        var selected = MidnightSurfaces ? tint : Vector4.Lerp(Surface(referenceRgb), tint, .45f);
        // Brighter backgrounds must not turn a tinted hover fill into a light
        // patch beneath white labels. Scale RGB together to retain its hue.
        if (Luminance(selected) > .14f)
        {
            var low = 0f;
            var high = 1f;
            for (var i = 0; i < 12; i++)
            {
                var amount = (low + high) * .5f;
                if (Luminance(selected * amount) > .14f) high = amount; else low = amount;
            }
            selected *= low;
        }
        return Backing(selected);
    }

    // Unlike large transparent shell surfaces, these small text-heavy cards
    // retain enough dark backing over arbitrary wallpapers to remain readable.
    private Vector4 Backing(Vector4 value) => value with { W = wallpaperVisible ? .94f : 1f };
    private static float Brightness(Vector4 value) => value.X * .2126f + value.Y * .7152f + value.Z * .0722f;
    private static float Luminance(Vector4 value)
    {
        static float Linear(float x) => x <= .04045f ? x / 12.92f : MathF.Pow((x + .055f) / 1.055f, 2.4f);
        return Linear(value.X) * .2126f + Linear(value.Y) * .7152f + Linear(value.Z) * .0722f;
    }
    private static Vector4 Rgb(int value) => new((value >> 16 & 255) / 255f, (value >> 8 & 255) / 255f, (value & 255) / 255f, 1);
    private static Vector3 Hsv(Vector4 color)
    {
        var max = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
        var min = MathF.Min(color.X, MathF.Min(color.Y, color.Z));
        var delta = max - min;
        var hue = delta <= 0 ? 0 : max == color.X ? (color.Y - color.Z) / delta :
            max == color.Y ? (color.Z - color.X) / delta + 2 : (color.X - color.Y) / delta + 4;
        return new Vector3((hue / 6 + 1) % 1, max == 0 ? 0 : delta / max, max);
    }
    private static Vector4 FromHsv(float hue, float saturation, float value)
    {
        hue = (hue % 1 + 1) % 1;
        var chroma = value * saturation;
        var x = chroma * (1 - MathF.Abs(hue * 6 % 2 - 1));
        var offset = value - chroma;
        var rgb = (int)(hue * 6) switch
        {
            0 => new Vector3(chroma, x, 0), 1 => new Vector3(x, chroma, 0),
            2 => new Vector3(0, chroma, x), 3 => new Vector3(0, x, chroma),
            4 => new Vector3(x, 0, chroma), _ => new Vector3(chroma, 0, x)
        };
        return new Vector4(rgb + new Vector3(offset), 1);
    }
}

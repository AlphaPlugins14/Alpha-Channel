using System.Numerics;

namespace AlphaChannel.Plugin.Auth;

internal static class AlphaChatNameEffects
{
    internal sealed record Preset(string Id, string Label, bool CustomColour, string Description);
    internal static readonly Preset[] Presets =
    [
        new("solid", "Solid colour", true, "Your chosen colour, without an effect."),
        new("candy-gradient", "Candy Gradient", false, "A soft pink, lavender and sky-blue gradient."),
        new("ocean-gradient", "Ocean Mist", false, "Deep blue fades through turquoise into seafoam."),
        new("sunset-gradient", "Sunset Fade", false, "Warm coral, golden peach and violet."),
        new("aurora-gradient", "Aurora Drift", false, "An animated ribbon of mint, teal and violet."),
        new("rainbow-flow", "Rainbow Flow", false, "A slowly moving rainbow across your name."),
        new("rainbow-glow", "Rainbow Glow", false, "A moving rainbow with a soft coloured glow."),
        new("firelight", "Firelight", false, "A gently flickering amber and red glow."),
        new("starlight", "Starlight", false, "Icy blue and silver with a travelling shimmer."),
        new("satin-gradient", "Satin Fade", true, "Your colour fades into a pale satin highlight."),
        new("shimmer", "Silk Shimmer", true, "A smooth highlight moves through your chosen colour."),
        new("neon-glow", "Neon Halo", true, "Your chosen colour with a steady soft glow."),
        new("pulse-glow", "Breathing Glow", true, "A glow that gently brightens and fades.")
    ];
    internal static string Normalize(string? effect)
    {
        effect = effect switch { null => "solid", "gradient" => "satin-gradient", "animated" => "shimmer", "glow" => "neon-glow", _ => effect };
        foreach (var preset in Presets) if (preset.Id == effect) return effect;
        throw new ArgumentException("Invalid username effect.");
    }
    internal static string Effective(string? effect, long verifiedUntil, long now)
    {
        if (verifiedUntil <= now) return "solid";
        try { return Normalize(effect); } catch (ArgumentException) { return "solid"; }
    }
    internal static bool UsesCustomColour(string effect) => Presets.First(p => p.Id == Normalize(effect)).CustomColour;
    private static readonly int[] Candy = [0xFF91C8, 0xBE9AFF, 0x8EDBFF];
    private static readonly int[] Ocean = [0x579FFF, 0x43CDD0, 0xACF3DA];
    private static readonly int[] Sunset = [0xFF796E, 0xFFC77D, 0xC38FFF];
    private static readonly int[] Aurora = [0x7FFFD2, 0x56CED6, 0xB197FF];
    private static readonly int[] Fire = [0xFF6557, 0xFFAA51, 0xFFE7A0];
    private static readonly int[] Stars = [0x8DBEFF, 0xCADBFF, 0xFFFFFF];
    private static Vector4 Rgb(int value) => new((value >> 16 & 255) / 255f, (value >> 8 & 255) / 255f, (value & 255) / 255f, 1f);
    private static Vector4 Gradient(float x, int[] palette)
    {
        var scaled = Math.Clamp(x, 0f, 1f) * (palette.Length - 1);
        var index = Math.Min((int)scaled, palette.Length - 2);
        return Vector4.Lerp(Rgb(palette[index]), Rgb(palette[index + 1]), scaled - index);
    }
    private static Vector4 Rainbow(float hue)
    {
        hue -= MathF.Floor(hue);
        var x = hue * 6f; var part = x - MathF.Floor(x);
        var rise = .25f + .75f * part; var fall = 1f - .75f * part;
        return (int)x switch
        {
            0 => new(1f, rise, .25f, 1f), 1 => new(fall, 1f, .25f, 1f),
            2 => new(.25f, 1f, rise, 1f), 3 => new(.25f, fall, 1f, 1f),
            4 => new(rise, .25f, 1f, 1f), _ => new(1f, .25f, fall, 1f)
        };
    }
    internal static (Vector4 Colour, float Glow) Sample(string effect, Vector4 colour, float x, double time)
    {
        var phase = (float)(time * 1.3 % (Math.PI * 2));
        var wave = .5f + .5f * MathF.Sin(x * MathF.PI * 2 - phase);
        var highlight = Vector4.Lerp(colour, Vector4.One, .65f);
        return effect switch
        {
            "candy-gradient" => (Gradient(x, Candy), 0f),
            "ocean-gradient" => (Gradient(x, Ocean), 0f),
            "sunset-gradient" => (Gradient(x, Sunset), 0f),
            "aurora-gradient" => (Gradient(wave, Aurora), 0f),
            "rainbow-flow" => (Rainbow(x * .7f + (float)(time / 12 % 1)), 0f),
            "rainbow-glow" => (Rainbow(x * .7f + (float)(time / 12 % 1)), .8f),
            "firelight" => (Gradient(.5f + .3f * MathF.Sin(phase * 2 + x * 4) + .15f * MathF.Sin(phase * 3 - x * 7), Fire), .45f + wave * .4f),
            "starlight" => (Gradient(MathF.Pow(wave, 5), Stars), .25f + MathF.Pow(wave, 5) * .5f),
            "satin-gradient" => (Vector4.Lerp(colour, highlight, x), 0f),
            "shimmer" => (Vector4.Lerp(colour, highlight, MathF.Pow(wave, 3)), 0f),
            "neon-glow" => (colour, .85f),
            "pulse-glow" => (colour, .2f + (.5f + .5f * MathF.Sin(phase)) * .8f),
            _ => (colour, 0f)
        };
    }
}

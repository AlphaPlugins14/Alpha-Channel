using System.Globalization;

namespace AlphaChannel.Plugin.Video;

// Persisted locally; only compact effects metadata travels with the audio URL.
public sealed record MusicSceneEffects
{
    public int Mask { get; init; }
    public uint Background { get; init; } = 0x05030D;
    public uint Speakers { get; init; } = 0xAD38FF;
    public uint Lasers { get; init; } = 0x20FF6B;
    public uint Halo { get; init; } = 0x9E24FF;
    public uint Leds { get; init; } = 0x00DDFF;
    public uint Rings { get; init; } = 0xAD38FF;
    public uint Sparkles { get; init; } = 0xB34DFF;
    public bool RainbowLeds { get; init; } = true;
    public uint Spotlights { get; init; } = 0x66CCFF;
    public uint DanceFloor { get; init; } = 0xFF44CC;
    public uint DiscoBall { get; init; } = 0xDDDDFF;
    public uint NeonFrame { get; init; } = 0x00DDFF;
    public uint Orbits { get; init; } = 0xFFCC44;
    public uint Curtain { get; init; } = 0x8833FF;
    public float FloorOffset { get; init; }
    public uint SpotlightColour { get; init; } = 0xFFFFFF;
    public float SpotlightBrightness { get; init; } = 1;
    public float SpotlightRange { get; init; } = 15;
    internal const int Spotlight = 32768;
    internal const int SpookyTime = 16384;
    internal const int AllFeatures = 63 | 64 | 128 | 256 | 1024 | 2048 | 8192 | SpookyTime | Spotlight;
    internal bool IsSpooky => (Mask & SpookyTime) != 0;
    // Retain the user's regular floor choice for when Halloween mode is switched off.
    internal int EffectiveMask => IsSpooky ? Mask & ~128 : Mask;
    internal static MusicSceneEffects Default { get; } = new();
    internal bool HasRenderedEffects => (EffectiveMask & ~Spotlight) != 0;
    internal bool AnyEnabled => (Mask & AllFeatures) != 0;
    internal MusicSceneEffects Normalized() => this with { Mask = Mask & AllFeatures,
        Background = Background & 0xFFFFFF, Speakers = Speakers & 0xFFFFFF,
        Lasers = Lasers & 0xFFFFFF, Halo = Halo & 0xFFFFFF, Leds = Leds & 0xFFFFFF,
        Rings = Rings & 0xFFFFFF, Sparkles = Sparkles & 0xFFFFFF,
        Spotlights = Spotlights & 0xFFFFFF, DanceFloor = DanceFloor & 0xFFFFFF,
        DiscoBall = DiscoBall & 0xFFFFFF, NeonFrame = NeonFrame & 0xFFFFFF,
        Orbits = Orbits & 0xFFFFFF, Curtain = Curtain & 0xFFFFFF,
        SpotlightColour = SpotlightColour & 0xFFFFFF,
        SpotlightBrightness = float.IsFinite(SpotlightBrightness) ? Math.Clamp(SpotlightBrightness, 0, 5) : 1,
        SpotlightRange = float.IsFinite(SpotlightRange) ? Math.Clamp(SpotlightRange, 1, 60) : 15,
        FloorOffset = float.IsFinite(FloorOffset) ? Math.Clamp(FloorOffset, -5, 5) : 0 };
    internal static MusicSceneEffects Parse(string? url)
    {
        var fragment = url?.IndexOf('#') ?? -1;
        if (fragment < 0) return Default;
        foreach (var part in url![(fragment + 1)..].Split('&'))
        {
            if (!part.StartsWith("scene=", StringComparison.OrdinalIgnoreCase)) continue;
            var values = part[6..].Split(',');
            if (values.Length != 9 || values.Any(v => v.Length > 6)) return Default;
            var parsed = new uint[9];
            for (var i = 0; i < values.Length; i++)
                if (!uint.TryParse(values[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed[i])) return Default;
            var spotlightEnabled = (parsed[0] & Spotlight) != 0;
            var scene = new MusicSceneEffects { Mask = (int)(parsed[0] & (AllFeatures & ~Spotlight)), Background = parsed[1],
                Speakers = parsed[2], Lasers = parsed[3], Halo = parsed[4], Leds = parsed[5],
                Rings = parsed[6], Sparkles = parsed[7], RainbowLeds = parsed[8] != 0 }.Normalized();
            var stage = url[(fragment + 1)..].Split('&').FirstOrDefault(v => v.StartsWith("stage=", StringComparison.OrdinalIgnoreCase));
            if (stage == null) return scene;
            var extra = stage[6..].Split(',');
            // Read both the compact retained effects and the legacy scene format.
            if (extra.Length is not (7 or 10 or 14) || extra.Any(v => v.Length > 16)) return scene;
            var colourCount = extra.Length == 14 ? 8 : 6;
            var colours = new uint[colourCount];
            for (var i = 0; i < colourCount; i++)
                if (extra[i].Length > 6 || !uint.TryParse(extra[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out colours[i])) return scene;
            var offsetIndex = extra.Length == 14 ? 9 : 6;
            if (!float.TryParse(extra[offsetIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var offset) || !float.IsFinite(offset)) return scene;
            scene = (scene with { Spotlights = colours[0], DanceFloor = colours[1], DiscoBall = colours[2],
                NeonFrame = colours[extra.Length == 14 ? 4 : 3], Orbits = colours[extra.Length == 14 ? 5 : 4],
                Curtain = colours[extra.Length == 14 ? 7 : 5], FloorOffset = offset }).Normalized();
            if (extra.Length == 10 && extra[7].Length <= 6 &&
                uint.TryParse(extra[7], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var spotlightColour) &&
                float.TryParse(extra[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var brightness) && float.IsFinite(brightness) &&
                float.TryParse(extra[9], NumberStyles.Float, CultureInfo.InvariantCulture, out var range) && float.IsFinite(range))
                scene = (scene with { Mask = spotlightEnabled ? scene.Mask | Spotlight : scene.Mask,
                    SpotlightColour = spotlightColour, SpotlightBrightness = brightness, SpotlightRange = range }).Normalized();
            return scene;
        }
        return Default;
    }
    internal static string AddToUrl(string url, MusicSceneEffects effects)
    {
        var e = effects.Normalized();
        var split = url.IndexOf('#');
        var clean = split < 0 ? url : url[..split];
        var parts = split < 0 ? new List<string>() : url[(split + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("scene=", StringComparison.OrdinalIgnoreCase) && !p.StartsWith("stage=", StringComparison.OrdinalIgnoreCase)).ToList();
        parts.Add($"scene={e.Mask:X},{e.Background:X6},{e.Speakers:X6},{e.Lasers:X6},{e.Halo:X6},{e.Leds:X6},{e.Rings:X6},{e.Sparkles:X6},{(e.RainbowLeds ? 1 : 0)}");
        parts.Add(FormattableString.Invariant($"stage={e.Spotlights:X6},{e.DanceFloor:X6},{e.DiscoBall:X6},{e.NeonFrame:X6},{e.Orbits:X6},{e.Curtain:X6},{e.FloorOffset:R},{e.SpotlightColour:X6},{e.SpotlightBrightness:R},{e.SpotlightRange:R}"));
        return clean + "#" + string.Join("&", parts);
    }
}

public sealed record SavedMusicScene
{
    public string Name { get; init; } = "";
    // A descriptor, never a playable media URL; stores the existing text/font/theme metadata.
    public string Presentation { get; init; } = "music-scene://preset#vis=classicbars&theme=purple";
}

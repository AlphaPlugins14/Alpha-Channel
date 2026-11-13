namespace AlphaChannel.Plugin;

internal enum LuckyWheelColourPreset { Custom, Rainbow, AlternatingAccent, PluginAccent }

// Custom colours follow option IDs, so reordering or changing presets cannot lose them.
internal sealed class LuckyWheelColours
{
    private readonly Dictionary<string, uint> custom = new(StringComparer.Ordinal);
    internal LuckyWheelColourPreset Selected { get; private set; }
    internal void Reset(LuckyWheelDesign design)
    {
        custom.Clear();
        RememberCustom(design);
    }
    internal void RememberCustom(LuckyWheelDesign design)
    {
        Selected = LuckyWheelColourPreset.Custom;
        foreach (var segment in design.Segments) custom[segment.Id] = segment.Colour;
    }
    internal void Select(LuckyWheelColourPreset preset, LuckyWheelDesign design, uint accent, uint alternate)
    {
        if (Selected == LuckyWheelColourPreset.Custom) RememberCustom(design);
        Selected = preset;
        design.Segments = design.Segments.Select((segment, i) => segment with
        {
            Colour = preset switch
            {
                LuckyWheelColourPreset.Custom => custom.GetValueOrDefault(segment.Id, segment.Colour),
                LuckyWheelColourPreset.AlternatingAccent => i % 2 == 0 ? accent : alternate,
                LuckyWheelColourPreset.PluginAccent => accent,
                _ => LuckyWheelRules.Rainbow[i % LuckyWheelRules.Rainbow.Length],
            }
        }).ToArray();
    }
}

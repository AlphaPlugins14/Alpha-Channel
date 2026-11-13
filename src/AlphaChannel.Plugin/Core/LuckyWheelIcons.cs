namespace AlphaChannel.Plugin;

internal sealed record LuckyWheelIcon(string Name, string Glyph);
internal static class LuckyWheelIcons
{
    internal static readonly LuckyWheelIcon[] All =
    [
        new("Star", "\uf005"), new("Heart", "\uf004"), new("Gift", "\uf06b"), new("Music", "\uf001"),
        new("Gamepad", "\uf11b"), new("Trophy", "\uf091"), new("Dice", "\uf522"), new("Crown", "\uf521"),
        new("Bolt", "\uf0e7"), new("Fire", "\uf06d"), new("Moon", "\uf186"), new("Sun", "\uf185"),
        new("Gem", "\uf3a5"), new("Flag", "\uf024"), new("Camera", "\uf030"), new("Paw", "\uf1b0"),
    ];
    internal static bool Valid(string? name) => name is null || All.Any(icon => icon.Name == name);
    internal static string? Glyph(string? name) => All.FirstOrDefault(icon => icon.Name == name)?.Glyph;
}

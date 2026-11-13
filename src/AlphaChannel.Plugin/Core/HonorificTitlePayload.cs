using System.Numerics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AlphaChannel.Plugin;

internal static class HonorificTitlePayload
{
    internal static bool Equivalent(string first, string second)
    {
        if (string.Equals(first, second, StringComparison.Ordinal)) return true;
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        try { return JToken.DeepEquals(JToken.Parse(first), JToken.Parse(second)); }
        catch (JsonException) { return false; }
    }

    internal static string Create(string title, string textColor, string glowColor) =>
        // Use the same serializer and nullable vector contract as Honorific's IPC.
        JsonConvert.SerializeObject(new TitleData
        {
            Title = title,
            IsPrefix = false,
            Color = ParseColor(textColor),
            Glow = ParseColor(glowColor),
        });

    internal static bool Matches(string json, string title, string textColor, string glowColor)
    {
        var received = JsonConvert.DeserializeObject<TitleData>(json);
        return received is not null && received.Title == title && !received.IsPrefix &&
            received.Color is { } color && Vector3.Distance(color, ParseColor(textColor)) < 0.0001f &&
            received.Glow is { } glow && Vector3.Distance(glow, ParseColor(glowColor)) < 0.0001f;
    }

    private sealed class TitleData
    {
        public string? Title { get; set; }
        public bool IsPrefix { get; set; }
        public Vector3? Color { get; set; }
        public Vector3? Glow { get; set; }
    }

    private static Vector3 ParseColor(string hex) => new(
        Convert.ToByte(hex.Substring(1, 2), 16) / 255f,
        Convert.ToByte(hex.Substring(3, 2), 16) / 255f,
        Convert.ToByte(hex.Substring(5, 2), 16) / 255f);
}

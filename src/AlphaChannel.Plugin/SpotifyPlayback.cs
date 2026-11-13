using System.Globalization;

namespace AlphaChannel.Plugin;

internal sealed class SpotifyPlayback(SpotifySnapshot sample)
{
    internal string Text(string key, string fallback = "") => sample.Fields.TryGetValue(key, out var value) &&
        !string.IsNullOrWhiteSpace(value) && value != "Not supplied" && !value.StartsWith("Unavailable (") ? value : fallback;
    internal bool Flag(string key) => Text(key).Equals("True", StringComparison.OrdinalIgnoreCase);
    internal bool Connected => Text("Session / Selected application ID").Length > 0;
    internal bool Playing => Text("Playback / Status") == "Playing";
    internal bool Enabled(string control) => Connected && Flag("Controls / " + control + " enabled");
    internal double Duration => Time("Timeline / Reported duration");
    internal double Start => Time("Timeline / Start");
    internal double Position(DateTime utcNow)
    {
        var position = Time("Timeline / Position") - Start;
        if (Playing && DateTimeOffset.TryParse(Text("Timeline / Last position update (UTC)"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var updated))
            position += Math.Clamp((utcNow - updated.UtcDateTime).TotalSeconds, 0, 15);
        return Math.Clamp(position, 0, Math.Max(0, Duration));
    }
    private double Time(string key) => TimeSpan.TryParse(Text(key), CultureInfo.InvariantCulture, out var value) ? value.TotalSeconds : 0;
    internal static string Clock(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
}

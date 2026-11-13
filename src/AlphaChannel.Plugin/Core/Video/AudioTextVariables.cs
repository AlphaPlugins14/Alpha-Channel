using System.Text.RegularExpressions;

namespace AlphaChannel.Plugin.Video;

internal static class AudioTextVariables
{
    internal static readonly (string Name, string Description)[] Definitions =
    [
        ("nowplaying", "Current song or station name"),
        ("nextsong", "Prepared deck song, otherwise the next queued song"),
        ("listeners", "Total Watch Party members, including the host"),
        ("viewers", "Watch Party members excluding the host"),
        ("broadcastname", "Your broadcast name"),
        ("status", "Ready, Connecting, Live, Playing or Paused"),
        ("decka", "Song loaded into Deck A"),
        ("deckb", "Song loaded into Deck B"),
        ("elapsed", "Current song's playback position"),
        ("duration", "Current song's total duration"),
        ("remaining", "Time remaining in the current song"),
        ("queuecount", "Number of songs remaining in the queue"),
        ("livetime", "Time since broadcasting started")
    ];
    private static readonly Regex Placeholder = new(@"\{([a-z]+)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string Resolve(string template, IReadOnlyDictionary<string, string> values) =>
        AudioScreenTextSelection.SanitizeText(Placeholder.Replace(template,
            match => values.TryGetValue(match.Groups[1].Value.ToLowerInvariant(), out var value) ? value : match.Value));
}

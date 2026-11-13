using System.Globalization;

namespace AlphaChannel.Plugin.Core;

// Small versioned room snapshot, carried by the existing authenticated relay.
// Countdowns may advertise a reserved signalling URL; running sessions name the authored video.
internal sealed record HalloweenSession(Guid Id, long EndsAt, bool Running, int Version = 1)
{
    private const string Prefix = "HWS2:";
    private const string CompletionPrefix = "HWC1:";
    internal const string CountdownLabel = "TRICK OR TREAT";
    internal string Encode(string reason)
    {
        var parts = reason.Split(';').Where(p => !p.StartsWith(Prefix, StringComparison.Ordinal)).ToList();
        var held = parts.Remove("TV-HELD");
        parts.Add($"{Prefix}{Id:N}:{EndsAt}:{(Running ? "R" : "C")}" + (Version == 2 ? ":2" : ""));
        if (held) parts.Add("TV-HELD");
        return string.Join(';', parts);
    }
    internal static string Clear(string reason) => string.Join(';', reason.Split(';').Where(p => !p.StartsWith(Prefix, StringComparison.Ordinal)));
    internal static string MarkCompleted(string reason, Guid? id)
    {
        var parts = Clear(reason).Split(';').Where(p => !p.StartsWith(CompletionPrefix, StringComparison.Ordinal)).ToList();
        var held = parts.Remove("TV-HELD");
        if (id is { } completed) parts.Add(CompletionPrefix + completed.ToString("N"));
        if (held) parts.Add("TV-HELD");
        return string.Join(';', parts);
    }
    internal static bool WasCompleted(string? reason, Guid id) => reason is { Length: <= 256 } &&
        reason.Split(';').Count(p => p.StartsWith(CompletionPrefix, StringComparison.Ordinal)) == 1 &&
        reason.Split(';').Contains(CompletionPrefix + id.ToString("N"), StringComparer.Ordinal);
    internal static HalloweenSession? Parse(string? reason)
    {
        if (reason is not { Length: <= 256 }) return null;
        var tags = reason.Split(';').Where(p => p.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
        if (tags.Length != 1) return null;
        var fields = tags[0].Split(':');
        return fields.Length is 4 or 5 && (fields.Length == 4 || fields[4] is "1" or "2") && Guid.TryParseExact(fields[1], "N", out var id) && id != Guid.Empty &&
            long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var end) &&
            end is > 0 and < 253402300799000 && fields[3] is "R" or "C"
            ? new(id, end, fields[3] == "R", fields.Length == 5 && fields[4] == "2" ? 2 : 1) : null;
    }
}

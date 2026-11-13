using System.Text.RegularExpressions;

namespace AlphaChannel.Plugin;

// Log callers supply fixed operation summaries, never exception bodies or user data.
// Typed graphics/audio diagnostics additionally allow enums and numeric/boolean state.
// Redaction is an additional safeguard; source regression checks enforce this boundary.
internal sealed class ReleaseLogPolicy
{
    private static readonly Regex PrivateLocation = new(
        @"(?i)(?:\b(?:https?|rtmps?|wss?|file)://[^\s]+|\b[A-Z]:[\\/][^\r\n]+|\\\\[^\r\n]+|(?<!\w)/(?:Users|home|tmp|var|opt|_)/[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private readonly Dictionary<string, DateTime> lastPrinted = new(StringComparer.Ordinal);
    private readonly Func<DateTime> clock;

    internal ReleaseLogPolicy(Func<DateTime>? clock = null) => this.clock = clock ?? (() => DateTime.UtcNow);

    internal bool TryAccept(string message, out string safeMessage)
    {
        safeMessage = Sanitize(message);
        var now = clock();
        lock (lastPrinted)
        {
            if (lastPrinted.TryGetValue(safeMessage, out var previous) && now - previous < TimeSpan.FromMinutes(1))
                return false;
            if (lastPrinted.Count >= 512) lastPrinted.Clear();
            lastPrinted[safeMessage] = now;
            return true;
        }
    }

    internal static string Sanitize(string message)
    {
        try
        {
            return PrivateLocation.Replace(message, "[redacted]").Replace('\r', ' ').Replace('\n', ' ');
        }
        catch (RegexMatchTimeoutException)
        {
            return "[AlphaChannel] Diagnostic details omitted.";
        }
    }
}

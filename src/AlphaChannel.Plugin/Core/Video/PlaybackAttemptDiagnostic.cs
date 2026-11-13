using System.Text.RegularExpressions;

namespace AlphaChannel.Plugin.Video;

internal enum PlaybackAttemptStage { RouteSelected, LoadRequested, MediaLoaded, ResolverResult, ResolverException, PlaybackStarted, ProviderStarting, ProviderReady }
internal enum PlaybackAttemptRoute { Native, Android, AndroidProbe, PoToken, ResolvedUrl }
internal enum PlaybackAccessReason { Unknown, Forbidden, Unauthorized, RateLimited, BotCheck, AgeRestricted, TokenRejected, Expired }

// Only allowlisted enums, numbers and flags reach production logs. Never retain raw input.
internal sealed record PlaybackAttemptDiagnostic(PlaybackAttemptStage Stage, PlaybackAttemptRoute Route,
    int Attempt, int Renderer, int Load, bool Account, bool? ProviderRunning, bool Reused,
    bool Success, int? HttpStatus = null, PlaybackAccessReason Access = PlaybackAccessReason.Unknown)
{
    internal string Format() => $"[Playback/Attempt] stage={Stage} route={Route} attempt={Attempt} renderer={Renderer} load={Load} account={Account} providerRunning={ProviderRunning?.ToString() ?? "unknown"} reused={Reused} success={Success} http={HttpStatus?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} access={Access}";
}

internal static class PlaybackAccessDiagnostic
{
    private static readonly Regex Status = new(
        @"\b(?:HTTP(?:/\d(?:\.\d)?)?\s+(?:(?:error|status|status\s+code)\s*[:=]?\s*)?|server\s+returned\s+|status\s+code\s*[:=]?\s*)([45]\d{2})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(25));

    internal static int? HttpStatus(string? message)
    {
        try
        {
            var match = Status.Match(message ?? "");
            return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    internal static PlaybackAccessReason Reason(string? message)
    {
        var text = (message ?? "").ToLowerInvariant();
        if (text.Contains("not a bot") || text.Contains("bot check")) return PlaybackAccessReason.BotCheck;
        if (text.Contains("age-restricted") || text.Contains("age restricted") || text.Contains("confirm your age")) return PlaybackAccessReason.AgeRestricted;
        if ((text.Contains("po token") || text.Contains("po-token") || text.Contains("potoken")) &&
            (text.Contains("invalid") || text.Contains("rejected") || text.Contains("missing"))) return PlaybackAccessReason.TokenRejected;
        if (text.Contains("expired")) return PlaybackAccessReason.Expired;
        return HttpStatus(message) switch { 401 => PlaybackAccessReason.Unauthorized, 403 => PlaybackAccessReason.Forbidden,
            429 => PlaybackAccessReason.RateLimited, _ => PlaybackAccessReason.Unknown };
    }
}

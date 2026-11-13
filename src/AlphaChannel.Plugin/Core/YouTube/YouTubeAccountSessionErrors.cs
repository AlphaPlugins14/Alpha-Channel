namespace AlphaChannel.Plugin.YouTube;

internal sealed class YouTubeSessionExpiredException : Exception
{
    internal YouTubeSessionExpiredException()
        : base("YouTube rejected the saved account cookies. Reconnect your YouTube account.")
    {
    }
}

internal static class YouTubeAccountSessionErrors
{
    internal static bool IndicatesExpiredCookies(string output) =>
        output.Contains("account cookies are no longer valid", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("cookies are no longer valid", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("cookies have been invalidated", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("cookies have expired", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("cookies are expired", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("authentication cookies are invalid", StringComparison.OrdinalIgnoreCase);
}

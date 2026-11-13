namespace AlphaChannel.Plugin.Video;

internal static class InternetArchiveMediaDisplay
{
    internal const string FallbackTitle = "Internet Archive Video";
    internal static bool IsArchiveUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && (uri.Host.Equals("archive.org", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.EndsWith(".archive.org", StringComparison.OrdinalIgnoreCase));

    internal static string Title(string? url, string? title) =>
        !string.IsNullOrWhiteSpace(title) && !title.Contains("://", StringComparison.Ordinal) &&
        !title.TrimStart().StartsWith("www.", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(title, url, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(title.Trim(), "Watch Party media", StringComparison.OrdinalIgnoreCase)
            ? title : FallbackTitle;
}

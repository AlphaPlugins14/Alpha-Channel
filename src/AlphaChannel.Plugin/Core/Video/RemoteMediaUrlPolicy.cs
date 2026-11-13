namespace AlphaChannel.Plugin.Video;

// A party host may supply a web media URL, never a local filename or an MPV
// protocol/device expression. Public/private destinations, ports, signed query
// strings and Alpha Channel presentation fragments remain compatible here.
// This is a protocol boundary, not DNS/redirect/playlist network isolation.
internal static class RemoteMediaUrlPolicy
{
    internal static bool IsWebMediaUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && !url.Any(char.IsControl) &&
        (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host);
}

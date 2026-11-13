namespace AlphaChannel.Plugin;

internal static class DiscordActivityPrivacy
{
    internal static bool IsContent(string? url, string kind) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Query.TrimStart('?').Split('&').Any(part =>
            string.Equals(part, "ac_content=" + kind, StringComparison.OrdinalIgnoreCase));

    internal static string? PublicTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        title = title.Trim();
        if (title.Contains("://", StringComparison.Ordinal) || title.Contains('\\') ||
            title.StartsWith('/') || title.Contains("?key=", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("token=", StringComparison.OrdinalIgnoreCase)) return null;
        title = new string(title.Where(c => !char.IsControl(c)).ToArray());
        if (title.Length > 120) title = title[..120];
        if (title.Length > 0 && char.IsHighSurrogate(title[^1])) title = title[..^1];
        return title.Length >= 2 ? title : null;
    }
}

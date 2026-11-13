namespace AlphaChannel.Plugin;

internal static class RelayRadioUrl
{
    internal static bool IsMatch(string? value, string activeOrigin, string originalOrigin)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var url) ||
            !Uri.TryCreate(activeOrigin, UriKind.Absolute, out var active) ||
            !Uri.TryCreate(originalOrigin, UriKind.Absolute, out var original) ||
            (url.Host != active.Host && url.Host != original.Host) || url.UserInfo.Length != 0)
            return false;
        return url.Scheme == "https" && url.IsDefaultPort &&
                   url.AbsolutePath.StartsWith("/radio-stream/radio/", StringComparison.OrdinalIgnoreCase) ||
               url.Scheme == "http" && url.Port == 8000 &&
                   url.AbsolutePath.StartsWith("/radio/", StringComparison.OrdinalIgnoreCase);
    }
}

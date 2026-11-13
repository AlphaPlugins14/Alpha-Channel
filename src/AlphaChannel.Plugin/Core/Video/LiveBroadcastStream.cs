namespace AlphaChannel.Plugin.Video;

// Both transports identify the same live relay media. Keep playback, room sync,
// startup/retry and directory classification consistent after the HTTPS move.
internal static class LiveBroadcastStream
{
    internal const string ConnectingMessage = "Connecting to live broadcast...";
    internal static bool AppliesTo(string? url) => TryParse(url, out _);
#if DEBUG
    internal static bool DebugUseLegacyHttp;
    internal static bool DebugUseLegacyUpload;
    internal static string DebugPlaybackUrl(string url) => DebugUseLegacyHttp && TryParse(url, out var uri) && uri!.Scheme == "https"
        ? new UriBuilder(uri) { Scheme = "http", Port = 8888 }.Uri.AbsoluteUri : url;
#endif
    internal static string PublishServer(string host)
    {
#if DEBUG
        if (DebugUseLegacyUpload) return $"rtmp://{host}:1935/live";
#endif
        return $"rtmps://{host}:1936/live";
    }

    internal static string RenewPlaybackSession(string url)
    {
        var uri = new UriBuilder(url);
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.Split('=', 2)[0].Equals("broadcast", StringComparison.OrdinalIgnoreCase));
        uri.Query = string.Join("&", query.Append($"broadcast={Guid.NewGuid():N}"));
        return uri.Uri.AbsoluteUri;
    }

    internal static bool TryParse(string? url, out Uri? uri)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0) return false;
        if (!uri.IdnHost.Equals(RelayServerOrigins.CurrentHost, StringComparison.OrdinalIgnoreCase) &&
            !uri.IdnHost.Equals(RelayServerOrigins.OriginalHost, StringComparison.OrdinalIgnoreCase)) return false;
        return (uri.Port == 8888 || uri.Scheme == "https" && uri.IsDefaultPort) &&
            uri.AbsolutePath.StartsWith("/live/", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.EndsWith("/index.m3u8", StringComparison.OrdinalIgnoreCase);
    }
}

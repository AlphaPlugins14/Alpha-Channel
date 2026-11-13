namespace AlphaChannel.Plugin.Video;

internal static class YouTubeHttpPlaybackPolicy
{
    // Some GoogleVideo audio streams accept bounded ranges but reject the
    // open-ended request used by a normal native player (HTTP 403).
    internal const int RequestBytes = 1024 * 1024;

    internal static bool Apply(bool youtube, Func<string[], int> command)
    {
        var size = youtube ? RequestBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";
        // New MPV builds use curl; FFmpeg remains the alternate network backend.
        // Reset both on every load so this policy never leaks into live radio.
        var curl = command(["set", "curl-max-request-size", size, null!]);
        var lavf = command(["set", "stream-lavf-o", youtube
            ? $"request_size={size},initial_request_size={size}" : "", null!]);
        return curl >= 0 && lavf >= 0;
    }
}

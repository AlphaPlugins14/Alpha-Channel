using System.Globalization;

namespace AlphaChannel.Plugin.Video;

internal readonly record struct LiveBroadcastBufferPolicy(int StartIndex, int WaitSeconds, bool BufferInitially, bool IsLive)
{
    internal static int NormalizeExtra(int seconds) => seconds is 5 or 10 or 20 ? seconds : 0;

    internal static LiveBroadcastBufferPolicy For(string url, int extraSeconds, bool browserReady)
    {
        if (!LiveBroadcastStream.TryParse(url, out var uri)) return new(-1, 1, false, false);
        var query = uri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        var browser = query.Contains("ac_content=web-browsing", StringComparer.OrdinalIgnoreCase);
        var game = query.Contains("ac_content=retro-games", StringComparer.OrdinalIgnoreCase) ||
                   query.Contains("ac_content=retro-game", StringComparer.OrdinalIgnoreCase);
        var extra = NormalizeExtra(extraSeconds);
        return new(game ? -2 : -3, (game ? 1 : browser ? 2 : 3) + extra,
            extra > 0 || !browser && !game || browser && browserReady, true);
    }

    // Per-file options are automatically restored by MPV when the next item
    // loads. A reused player must not carry live buffering into YouTube/radio.
    internal string LoadOptions
    {
        get
        {
            var options = "cache-pause-wait=" + WaitSeconds.ToString(CultureInfo.InvariantCulture) +
                ",cache-pause-initial=" + (BufferInitially ? "yes" : "no");
            if (!IsLive) return options;
            var ahead = Math.Max(30, WaitSeconds + 5).ToString(CultureInfo.InvariantCulture);
            return options + ",cache=yes,cache-pause=yes,cache-secs=" + ahead +
                ",demuxer-readahead-secs=" + ahead + ",demuxer-max-bytes=256MiB";
        }
    }
}

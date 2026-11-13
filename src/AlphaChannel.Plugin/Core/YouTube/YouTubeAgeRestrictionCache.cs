namespace AlphaChannel.Plugin.Video;

// Local evidence only. Never infer age restrictions from generic login/bot failures.
internal sealed class YouTubeAgeRestrictionCache
{
    private readonly object gate = new();
    private readonly HashSet<string> videos = new(StringComparer.Ordinal);

    internal void Restore(IEnumerable<string> ids)
    {
        lock (gate) foreach (var id in ids) if (ValidId(id)) videos.Add(id);
    }
    internal bool Contains(string? url) => VideoId(url) is { } id && ContainsId(id);
    private bool ContainsId(string id) { lock (gate) return videos.Contains(id); }
    internal bool Record(string? url, string? failure, bool usableConnection)
    {
        if (usableConnection || !IsAgeRestrictedError(failure) || VideoId(url) is not { } id) return false;
        lock (gate) videos.Add(id);
        return true;
    }
    internal static bool IsAgeRestrictedError(string? failure)
    {
        if (string.IsNullOrWhiteSpace(failure)) return false;
        foreach (var line in failure.Split('\n'))
        {
            var text = line.ToLowerInvariant();
            if (text.Contains("not age-restricted") || text.Contains("not age restricted")) continue;
            if (text.Contains("sign in to confirm your age") || text.Contains("sign in to confirm age") ||
                text.Contains("sign in to verify your age")) return true;
            // Format-availability warnings and a title containing these words are
            // not evidence that playback was blocked.
            if (text.Contains("warning:") || text.Contains("may be age") || text.Contains("some formats may be missing") || text.Contains("video title:")) continue;
            if (text.Contains("this video is age-restricted") || text.Contains("this video is age restricted") ||
                text.Trim() is "age_restricted" or "age-restricted video" or "age restricted video") return true;
        }
        return false;
    }
    internal static string? VideoId(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var host = uri.IdnHost.ToLowerInvariant();
        string? id = null;
        if (host is "youtu.be" or "www.youtu.be") id = uri.AbsolutePath.Trim('/').Split('/')[0];
        else if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com" or
                 "youtube-nocookie.com" or "www.youtube-nocookie.com")
        {
            var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (path.Length == 2 && path[0] is "shorts" or "embed" or "live") id = path[1];
            else if (uri.AbsolutePath == "/watch")
                foreach (var part in uri.Query.TrimStart('?').Split('&'))
                    if (part.StartsWith("v=", StringComparison.Ordinal)) { id = part[2..]; break; }
        }
        if (id is not null)
        {
            try { id = Uri.UnescapeDataString(id); }
            catch (UriFormatException) { return null; }
        }
        return ValidId(id) ? id : null;
    }
    private static bool ValidId(string? id) => id is { Length: 11 } && id.All(c =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');
}

// Room/content transitions or a host restart at the beginning rearm warnings;
// local decoder retries and repeated heartbeats do not.
internal sealed class YouTubeAgeRestrictionWarningScope
{
    private string? scope;
    private bool shown;
    private double? lastHostPosition;
    internal void Observe(string? playbackScope, double? hostPosition = null)
    {
        if (scope != playbackScope)
        { scope = playbackScope; shown = false; }
        else if (lastHostPosition > 5 && hostPosition is >= 0 and <= 1)
            shown = false;
        lastHostPosition = hostPosition is { } value && double.IsFinite(value) ? value : null;
    }
    internal bool ShouldShow(bool party)
    {
        if (!party) return true;
        if (shown) return false;
        shown = true;
        return true;
    }
}

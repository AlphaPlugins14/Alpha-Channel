using System.Net.Http;
using System.Text.RegularExpressions;

namespace AlphaChannel.Plugin.Video;

internal static class BrowserHlsStartup
{
    internal static bool AppliesTo(string url) => LiveBroadcastStream.TryParse(url, out var uri) &&
        uri!.Query.Contains("ac_content=web-browsing", StringComparison.OrdinalIgnoreCase);

    internal static async Task<bool> WaitAsync(HttpClient http, string url, CancellationToken cancellation,
        TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    var root = new Uri(url);
                    var master = await http.GetStringAsync(root, deadline.Token).ConfigureAwait(false);
                    var playlists = ResolvePlaylists(root, master);
                    if (playlists.Count == 0)
                    {
                        if (HasRealStartingWindow(master)) return true;
                    }
                    else
                    {
                        // MediaMTX's master includes separate video/audio lists.
                        // Both must be ready; neither partial LL-HLS parts nor
                        // EXT-X-GAP placeholders count as completed segments.
                        var ready = true;
                        foreach (var playlist in playlists)
                        {
                            var media = await http.GetStringAsync(playlist, deadline.Token).ConfigureAwait(false);
                            ready &= HasRealStartingWindow(media);
                        }
                        if (ready) return true;
                    }
                }
                catch (HttpRequestException) { /* Playlist publication may still be starting. */ }
                catch (UriFormatException) { return false; }
                await Task.Delay(250, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return false; }
    }

    internal static bool HasRealStartingWindow(string playlist)
    {
        var consecutive = 0;
        var hasDuration = false;
        var gap = false;
        foreach (var line in Lines(playlist))
        {
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal)) hasDuration = true;
            else if (line == "#EXT-X-GAP") gap = true;
            else if (line.Length > 0 && line[0] != '#' && hasDuration)
            {
                var path = line.Split('?')[0];
                consecutive = gap || path.EndsWith("gap.mp4", StringComparison.OrdinalIgnoreCase) ? 0 : consecutive + 1;
                hasDuration = gap = false;
            }
        }
        return consecutive >= 3;
    }

    private static List<Uri> ResolvePlaylists(Uri root, string playlist)
    {
        var result = new HashSet<Uri>();
        var variant = false;
        foreach (var line in Lines(playlist))
        {
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) variant = true;
            else if (variant && line.Length > 0 && line[0] != '#')
            {
                result.Add(new Uri(root, line));
                variant = false;
            }
            else if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal) &&
                     line.Contains("TYPE=AUDIO", StringComparison.Ordinal))
            {
                var uri = Regex.Match(line, "(?:[:,])URI=\"([^\"]+)\"");
                if (uri.Success) result.Add(new Uri(root, uri.Groups[1].Value));
            }
        }
        return result.Take(8).ToList();
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.TrimEntries);
}

using System.Globalization;

namespace AlphaChannel.Plugin.Video;

// Samples the bytes and declared durations of recent HLS segments. This measures
// the stream which actually reached MediaMTX, so allowance accounting is not
// based on a client-side OBS setting that can be stale or misleading.
internal static class HlsBitrateProbe
{
    private const int SegmentSampleCount = 3;
    private const long MaxSegmentBytes = 32L * 1024L * 1024L;

    internal static async Task<double> MeasureMbpsAsync(
        string playlistUrl,
        CancellationToken cancellationToken)
    {
        using var http = Net.PluginHttpClients.CreateProbeClient();
        var playlistUri = new Uri(playlistUrl);
        var playlist = await http.GetStringAsync(
            playlistUri,
            cancellationToken);

        var mediaPlaylistUri = ResolveMediaPlaylist(
            playlistUri,
            playlist);
        if (mediaPlaylistUri != playlistUri)
        {
            playlist = await http.GetStringAsync(
                mediaPlaylistUri,
                cancellationToken);
        }

        var segments = ParseSegments(mediaPlaylistUri, playlist)
            .TakeLast(SegmentSampleCount)
            .ToArray();
        if (segments.Length == 0)
        {
            return 0d;
        }

        long totalBytes = 0;
        double totalSeconds = 0d;
        foreach (var segment in segments)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                segment.Uri);
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var bytes = response.Content.Headers.ContentLength;
            if (bytes is null)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(
                    cancellationToken);
                var buffer = new byte[64 * 1024];
                long readTotal = 0;
                while (readTotal < MaxSegmentBytes)
                {
                    var read = await stream.ReadAsync(
                        buffer.AsMemory(
                            0,
                            (int)Math.Min(buffer.Length, MaxSegmentBytes - readTotal)),
                        cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    readTotal += read;
                }

                bytes = readTotal;
            }

            totalBytes += Math.Max(0L, bytes.Value);
            totalSeconds += segment.DurationSeconds;
        }

        return totalSeconds > 0d
            ? totalBytes * 8d / totalSeconds / 1_000_000d
            : 0d;
    }

    private static Uri ResolveMediaPlaylist(
        Uri playlistUri,
        string playlist)
    {
        var lines = Lines(playlist);
        for (var index = 0; index < lines.Length - 1; index++)
        {
            if (!lines[index].StartsWith(
                    "#EXT-X-STREAM-INF",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            for (var next = index + 1; next < lines.Length; next++)
            {
                if (lines[next].Length == 0 || lines[next][0] == '#')
                {
                    continue;
                }

                return new Uri(playlistUri, lines[next]);
            }
        }

        return playlistUri;
    }

    private static IEnumerable<Segment> ParseSegments(
        Uri playlistUri,
        string playlist)
    {
        var lines = Lines(playlist);
        for (var index = 0; index < lines.Length - 1; index++)
        {
            if (!lines[index].StartsWith(
                    "#EXTINF:",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawDuration = lines[index][8..].Split(',')[0];
            if (!double.TryParse(
                    rawDuration,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var duration) ||
                duration <= 0d)
            {
                continue;
            }

            for (var next = index + 1; next < lines.Length; next++)
            {
                if (lines[next].Length == 0 || lines[next][0] == '#')
                {
                    continue;
                }

                yield return new Segment(
                    new Uri(playlistUri, lines[next]),
                    duration);
                break;
            }
        }
    }

    private static string[] Lines(string playlist) =>
        playlist.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries);

    private readonly record struct Segment(
        Uri Uri,
        double DurationSeconds);
}

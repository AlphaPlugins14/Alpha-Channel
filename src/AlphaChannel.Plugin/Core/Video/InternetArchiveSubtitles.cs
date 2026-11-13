using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Video;

internal sealed record InternetArchiveSubtitle(string Language, string Name, string Url);

internal static class InternetArchiveSubtitles
{
    private static readonly ConcurrentDictionary<string, Task<IReadOnlyList<InternetArchiveSubtitle>>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsArchiveVideoUrl(string? url) => TryParseVideoUrl(url, out _, out _);

    internal static IReadOnlyList<InternetArchiveSubtitle>? GetTracks(string url)
    {
        if (!TryParseVideoUrl(url, out var identifier, out var videoName))
            return [];

        var task = Cache.GetOrAdd(url, _ => FetchAsync(identifier, videoName));
        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    private static bool TryParseVideoUrl(string? url, out string identifier, out string videoName)
    {
        identifier = videoName = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !(uri.Host.Equals("archive.org", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("www.archive.org", StringComparison.OrdinalIgnoreCase)))
            return false;

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Length > 3 ||
            !(parts[0].Equals("download", StringComparison.OrdinalIgnoreCase) ||
              parts[0].Equals("details", StringComparison.OrdinalIgnoreCase)) ||
            (parts[0].Equals("download", StringComparison.OrdinalIgnoreCase) && parts.Length != 3))
            return false;

        identifier = Uri.UnescapeDataString(parts[1]);
        if (parts.Length == 3) videoName = Uri.UnescapeDataString(parts[2]);
        return !string.IsNullOrWhiteSpace(identifier);
    }

    private static async Task<IReadOnlyList<InternetArchiveSubtitle>> FetchAsync(
        string identifier, string videoName)
    {
        try
        {
            using var http = PluginHttpClients.CreateMetadataClient();
            using var response = await http.GetAsync(
                "https://archive.org/metadata/" + Uri.EscapeDataString(identifier)).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("files", out var files) ||
                files.ValueKind != JsonValueKind.Array)
                return [];

            var videoStem = Path.GetFileNameWithoutExtension(videoName);
            if (string.IsNullOrWhiteSpace(videoStem))
            {
                // A /details/{id} URL names an item rather than a file. Only
                // infer the video when every playable file shares one stem.
                var videoStems = files.EnumerateArray()
                    .Select(file => file.TryGetProperty("name", out var value) &&
                                    value.ValueKind == JsonValueKind.String ? value.GetString() : null)
                    .Where(name => name is not null &&
                                   (name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                                    name.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ||
                                    name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
                                    name.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase)))
                    .Select(name => Path.GetFileNameWithoutExtension(name!))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(2).ToArray();
                if (videoStems.Length != 1) return [];
                videoStem = videoStems[0];
            }
            var tracks = new List<InternetArchiveSubtitle>();
            foreach (var file in files.EnumerateArray())
            {
                if (!file.TryGetProperty("name", out var nameValue) ||
                    nameValue.ValueKind != JsonValueKind.String)
                    continue;
                var name = nameValue.GetString();
                if (string.IsNullOrWhiteSpace(name) ||
                    !(name.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase) ||
                      name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)))
                    continue;

                var subtitleStem = Path.GetFileNameWithoutExtension(name);
                if (!subtitleStem.Equals(videoStem, StringComparison.OrdinalIgnoreCase) &&
                    !subtitleStem.StartsWith(videoStem + ".", StringComparison.OrdinalIgnoreCase))
                    continue;

                var language = subtitleStem.Length == videoStem.Length
                    ? "und"
                    : subtitleStem[(videoStem.Length + 1)..];
                var displayName = language == "und" ? "Subtitles" : language;
                try { displayName = CultureInfo.GetCultureInfo(language).EnglishName; }
                catch (CultureNotFoundException) { }
                tracks.Add(new InternetArchiveSubtitle(
                    language, displayName,
                    "https://archive.org/download/" + Uri.EscapeDataString(identifier) + "/" +
                    Uri.EscapeDataString(name)));
            }

            return tracks.OrderBy(track => track.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(track => track.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception)
        {
            AepLog.Warning("[InternetArchive] Caption lookup failed.");
            return [];
        }
    }
}

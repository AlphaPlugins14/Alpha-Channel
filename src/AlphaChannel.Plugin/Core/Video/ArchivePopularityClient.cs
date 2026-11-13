using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using AlphaChannel.Plugin.Net;
using AlphaChannel.Plugin.YouTube;
namespace AlphaChannel.Plugin.Video;
internal sealed record ArchivePopularSubmission(string VideoId, string EpisodeKey, string EventId,
    double WatchedSeconds, double? DurationSeconds, long QualifiedAtUnix);
internal sealed class ArchivePopularityClient(Func<HttpClient>? clientFactory = null)
{
    internal const string WorkerUrl = "https://archive-popular.example.invalid";
    private readonly Func<HttpClient> createClient = clientFactory ?? PluginHttpClients.CreateMetadataClient;
    internal static bool ValidId(string? id) => id is {Length: > 0 and <= 100} && Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);
    internal static (string Id, string Episode)? Identity(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !(uri.Host == "archive.org" || uri.Host.EndsWith(".archive.org", StringComparison.OrdinalIgnoreCase))) return null;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var itemIndex = parts.Length >= 3 && parts[0] is "download" or "serve" or "items" ? 1
            : parts.Length >= 4 && parts[1] == "items" && parts[0].All(char.IsAsciiDigit) ? 2 : -1;
        if (itemIndex < 0) return null;
        var id = Uri.UnescapeDataString(parts[itemIndex]);
        var episode = Uri.UnescapeDataString(string.Join("/", parts.Skip(itemIndex + 1))).Normalize();
        return ValidId(id) && episode.Length is > 0 and <= 1024 && !episode.Any(char.IsControl) ? (id, episode) : null;
    }
    internal static bool IsArchiveUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo) && (uri.Host == "archive.org" || uri.Host.EndsWith(".archive.org", StringComparison.OrdinalIgnoreCase));
    internal async Task<string[]> PopularAsync(CancellationToken token)
    {
        using var http = createClient();
        var feed = await http.GetFromJsonAsync<Feed>(WorkerUrl + "/api/archive/popular", token).ConfigureAwait(false);
        if (feed?.Videos is null || feed.Videos.Length > 200 || feed.Videos.Any(v => v is null || !ValidId(v.VideoId) || !double.IsFinite(v.Score) || v.Score < 0))
            throw new InvalidDataException("Invalid Archive Popular feed.");
        return feed.Videos.Select(v => v.VideoId).Distinct(StringComparer.Ordinal).ToArray();
    }
    internal async Task<PopularSubmitResult> SubmitAsync(string accountToken, ArchivePopularSubmission submission, CancellationToken token)
    {
        using var http = createClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accountToken);
        using var response = await http.PostAsJsonAsync(WorkerUrl + "/api/archive/play", submission, token).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            var reply = await response.Content.ReadFromJsonAsync<Reply>(cancellationToken: token).ConfigureAwait(false);
            return reply?.Accepted == true || reply?.Duplicate == true ? new(true, reply.Accepted, reply.Duplicate, status, null)
                : new(false, false, false, status, "invalid_response");
        }
        return new(status is >= 400 and < 500 && status is not (408 or 429), false, false, status, "submission_failed");
    }
    private sealed record Feed(Entry[] Videos);
    private sealed record Entry(string VideoId, double Score);
    private sealed record Reply(bool Accepted, bool Duplicate);
}

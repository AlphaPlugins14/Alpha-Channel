using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AlphaChannel.Plugin.Net;
using AlphaChannel.Plugin.YouTube;

namespace AlphaChannel.Plugin.Video;



internal sealed record DailymotionVideo(string VideoId, string? Title = null, string? ChannelName = null,
    string? ThumbnailUrl = null, double? DurationSeconds = null, long? ViewCount = null)
{
    internal VideoSearchEntry ToEntry() => new(Title ?? "Dailymotion video", $"https://www.dailymotion.com/video/{VideoId}",
        ChannelName ?? "Dailymotion", DurationSeconds is > 0 and <= 31536000 ? TimeSpan.FromSeconds(DurationSeconds.Value) : null, ThumbnailUrl, ViewCount);
}

internal sealed class DailymotionClient(Func<HttpClient>? clientFactory = null)
{
    private readonly Func<HttpClient> createClient = clientFactory ?? PluginHttpClients.CreateMetadataClient;
    private readonly ConcurrentDictionary<string, DailymotionVideo> cache = new(StringComparer.Ordinal);
    internal static bool ValidId(string? id) => id is { Length: > 0 and <= 32 } && Regex.IsMatch(id, "^[A-Za-z0-9]+$", RegexOptions.CultureInvariant);
    internal static string? VideoId(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? id = uri.Host.Equals("dai.ly", StringComparison.OrdinalIgnoreCase) && path.Length == 1 ? path[0]
            : (uri.Host.Equals("dailymotion.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.dailymotion.com", StringComparison.OrdinalIgnoreCase)) && path.Length >= 2 && path[0] == "video" ? path[1].Split('_')[0] : null;
        return ValidId(id) ? id : null;
    }
    private void Remember(DailymotionVideo video)
    {
        cache[video.VideoId] = video;
        while (cache.Count > 500 && cache.Keys.FirstOrDefault() is { } key) cache.TryRemove(key, out _);
    }
    internal async Task<DailymotionVideo[]> SearchAsync(string query, CancellationToken token)
    {
        if (VideoId(query) is { } id) return [await GetVideoAsync(id, token)];
        using var http = createClient();
        using var document = JsonDocument.Parse(await http.GetStringAsync("https://api.dailymotion.com/videos?search=" + Uri.EscapeDataString(query)
            + "&limit=50&localization=en_GB&sort=relevance&fields=id,title,owner.screenname,thumbnail_url,duration,views_total", token));
        if (!document.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid Dailymotion response.");
        var results = list.EnumerateArray().Take(50).Select(Parse).Where(v => v is not null).Cast<DailymotionVideo>().ToArray();
        foreach (var video in results) Remember(video);
        return results;
    }
    internal async Task<DailymotionVideo> GetVideoAsync(string id, CancellationToken token)
    {
        if (!ValidId(id)) throw new ArgumentException("Invalid Dailymotion video ID.");
        if (cache.TryGetValue(id, out var cached)) return cached;
        using var http = createClient();
        using var document = JsonDocument.Parse(await http.GetStringAsync($"https://api.dailymotion.com/video/{id}?fields=id,title,owner.screenname,thumbnail_url,duration,views_total", token));
        var video = Parse(document.RootElement) ?? throw new InvalidDataException("Invalid Dailymotion video.");
        if (video.VideoId != id) throw new InvalidDataException("Mismatched Dailymotion video.");
        Remember(video);
        return video;
    }
    private static DailymotionVideo? Parse(JsonElement item)
    {
        string? Text(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var id = Text("id");
        if (!ValidId(id)) return null;
        double? duration = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds is >= 0 and <= 31536000 ? seconds : null;
        long? views = item.TryGetProperty("views_total", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var count) && count >= 0 ? count : null;
        return new(id!, Text("title"), Text("owner.screenname"), SafeThumbnail(Text("thumbnail_url")), duration, views);
    }
    private static string? SafeThumbnail(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.Host == "dmcdn.net" || uri.Host.EndsWith(".dmcdn.net", StringComparison.OrdinalIgnoreCase) || uri.Host == "dailymotion.com" || uri.Host.EndsWith(".dailymotion.com", StringComparison.OrdinalIgnoreCase)) ? value : null;

    internal async Task<DailymotionVideo[]> PopularAsync(CancellationToken token)
    {
        using var http = createClient();
        var feed = await http.GetFromJsonAsync<Feed>(PopularWorkerClient.WorkerUrl + "/api/dailymotion/popular", token);
        if (feed?.Videos is null || feed.Videos.Length > 200 || feed.Videos.Any(v => v is null || !ValidId(v.VideoId) ||
            v.Title?.Length > 512 || v.ChannelName?.Length > 256 || v.ViewCount is < 0 ||
            v.DurationSeconds is { } d && (!double.IsFinite(d) || d is < 0 or > 31536000))) throw new InvalidDataException("Invalid Dailymotion feed.");
        return feed.Videos.DistinctBy(v => v.VideoId).Select(v => v with { ThumbnailUrl = SafeThumbnail(v.ThumbnailUrl) }).ToArray();
    }
    internal async Task<PopularSubmitResult> SubmitAsync(string accountToken, PopularSubmission submission, CancellationToken token)
    {
        DailymotionVideo? metadata = null;
        try { metadata = await GetVideoAsync(submission.VideoId, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { /* A metadata outage must not discard a qualified play. */ }
        using var http = createClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accountToken);
        using var response = await http.PostAsJsonAsync(PopularWorkerClient.WorkerUrl + "/api/dailymotion/play", new
        { submission.VideoId, submission.EventId, submission.WatchedSeconds, submission.DurationSeconds, submission.QualifiedAtUnix, metadata }, token);
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            var reply = await response.Content.ReadFromJsonAsync<Reply>(cancellationToken: token);
            if (reply?.Accepted == true || reply?.Duplicate == true) return new(true, reply.Accepted, reply.Duplicate, status, null);
            return new(false, false, false, status, "invalid_response");
        }
        return new(status is >= 400 and < 500 && status is not (408 or 429), false, false, status, "submission_failed");
    }
    private sealed record Feed(DailymotionVideo[] Videos);
    private sealed record Reply(bool Accepted, bool Duplicate);
}

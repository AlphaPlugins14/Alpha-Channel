using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.YouTube;

internal sealed class PopularWorkerClient
{
    private readonly Func<HttpClient> createClient;
    internal PopularWorkerClient(Func<HttpClient>? createClient = null) =>
        this.createClient = createClient ?? CreateClient;
    internal const string WorkerUrl = "https://popular.example.invalid";
    internal static bool ValidVideoId(string? id) =>
        id is { Length: 11 } && Regex.IsMatch(id, "^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant);

    internal async Task<PopularFeedVideo[]> GetAsync(CancellationToken token)
    {
        using var http = createClient();
        using var response = await http.GetAsync("/api/popular", token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new PopularWorkerException((int)response.StatusCode, await ErrorCodeAsync(response, token).ConfigureAwait(false));
        var body = await response.Content.ReadFromJsonAsync<PopularResponse>(
            cancellationToken: token).ConfigureAwait(false);
        if (body?.Videos is null || body.Videos.Length > 200 ||
            body.Videos.Any(item => item is null || !ValidVideoId(item.VideoId) || !ValidCount(item.ViewCount, item.ViewCountObservedAtUnix) ||
                !ValidMetadata(item.Metadata, item.VideoId)))
            throw new InvalidOperationException("Invalid Popular response.");
        return body.Videos.DistinctBy(item => item.VideoId, StringComparer.Ordinal).ToArray();
    }

    internal static bool ValidCount(long? count, long? observedAt) =>
        count is null ? observedAt is null : count >= 0 && count <= 9_007_199_254_740_991 &&
        (observedAt ?? 0) >= 0 && (observedAt ?? 0) <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60;

    internal static bool ValidMetadata(PopularVideoMetadata? metadata, string id)
    {
        if (metadata is null) return true;
        static bool Text(string? text, int limit) => text is null || text.Length <= limit && !text.Any(char.IsControl);
        return Text(metadata.Title, 512) && Text(metadata.ChannelName, 256) && Text(metadata.ChannelId, 128) &&
            Text(metadata.ThumbnailUrl, 2048) && (metadata.ThumbnailUrl is null ||
                Uri.TryCreate(metadata.ThumbnailUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                Regex.IsMatch(uri.Host, @"^(i\d*\.ytimg\.com|img\.youtube\.com)$") && string.IsNullOrEmpty(uri.UserInfo) &&
                (uri.AbsolutePath.StartsWith("/vi/" + id + "/", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/vi_webp/" + id + "/", StringComparison.Ordinal))) &&
            (metadata.DurationSeconds is null || double.IsFinite(metadata.DurationSeconds.Value) && metadata.DurationSeconds is >= 0 and <= 31536000) &&
            ValidCount(metadata.ViewCount, metadata.ViewCountObservedAtUnix) &&
            (metadata.MetadataObservedAtUnix ?? 0) >= 0 && (metadata.MetadataObservedAtUnix ?? 0) <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60;
    }

    internal async Task<PopularMetadataReply> ClaimMetadataAsync(string accountToken, string videoId,
        PopularVideoMetadata? cachedMetadata, CancellationToken token) =>
        await MetadataRequestAsync(accountToken, videoId, "/api/metadata/claim", new {videoId,
            cachedMetadata = ValidMetadata(cachedMetadata, videoId) ? cachedMetadata : null}, token).ConfigureAwait(false);

    internal async Task<PopularMetadataReply> ReportMetadataAsync(string accountToken, string videoId, string leaseId,
        PopularVideoMetadata? metadata, CancellationToken token) =>
        await MetadataRequestAsync(accountToken, videoId, "/api/metadata", new {videoId, leaseId, metadata}, token).ConfigureAwait(false);

    private async Task<PopularMetadataReply> MetadataRequestAsync(string accountToken, string videoId, string path,
        object payload, CancellationToken token)
    {
        if (!ValidVideoId(videoId)) throw new ArgumentException("Invalid metadata ID.");
        using var http = createClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accountToken);
        using var response = await http.PostAsJsonAsync(path, payload, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new PopularWorkerException((int)response.StatusCode, await ErrorCodeAsync(response, token).ConfigureAwait(false));
        var reply = await response.Content.ReadFromJsonAsync<PopularMetadataReply>(cancellationToken: token).ConfigureAwait(false);
        if (reply is null || !ValidMetadata(reply.Metadata, videoId) || reply.RefreshAfterUnix < 0 ||
            path.EndsWith("/claim", StringComparison.Ordinal) && (reply.State is not ("available" or "busy" or "claimed") ||
                reply.State == "available" && reply.Metadata?.IsComplete != true ||
                reply.State == "claimed" && (reply.LeaseId is not {Length:32} || reply.LeaseId.Any(c => !char.IsAsciiHexDigit(c)))) ||
            !path.EndsWith("/claim", StringComparison.Ordinal) && !reply.Updated)
            throw new InvalidDataException("Invalid metadata response.");
        return reply;
    }

    internal async Task<PopularCountClaim> ClaimViewCountAsync(string accountToken, string videoId,
        long? cachedCount, long? observedAt, CancellationToken token)
    {
        if (!ValidVideoId(videoId) || !ValidCount(cachedCount, observedAt)) throw new ArgumentException("Invalid count claim.");
        using var http = createClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accountToken);
        using var response = await http.PostAsJsonAsync("/api/view-count/claim", new
        { videoId, cachedViewCount = cachedCount, cachedViewCountObservedAtUnix = observedAt }, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new PopularWorkerException((int)response.StatusCode, await ErrorCodeAsync(response, token).ConfigureAwait(false));
        var claim = await response.Content.ReadFromJsonAsync<PopularCountClaim>(cancellationToken: token).ConfigureAwait(false);
        if (claim is null || !ValidCount(claim.ViewCount, claim.ViewCountObservedAtUnix) ||
            claim.State is not ("available" or "busy" or "claimed") ||
            claim.State == "available" && claim.ViewCount is null ||
            claim.State == "claimed" && (claim.LeaseId is not { Length: 32 } || claim.LeaseId.Any(c => !char.IsAsciiHexDigit(c))))
            throw new InvalidDataException("Invalid count claim response.");
        return claim;
    }

    internal async Task ReportViewCountAsync(string accountToken, string videoId, string leaseId,
        long? count, long? observedAt, CancellationToken token)
    {
        if (!ValidVideoId(videoId) || !ValidCount(count, observedAt)) throw new ArgumentException("Invalid view count.");
        using var http = createClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accountToken);
        using var response = await http.PostAsJsonAsync("/api/view-count", new
        { videoId, leaseId, viewCount = count, viewCountObservedAtUnix = observedAt ?? (count is not null ? 0L : (long?)null) }, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<CountReportResponse>(cancellationToken: token).ConfigureAwait(false);
        if (result?.Updated != true) throw new InvalidDataException("Unconfirmed view-count report.");
    }

    // Permanent rejection drops this event; network failures/429/5xx retry.
    internal async Task<PopularSubmitResult> SubmitAsync(string accountToken, PopularSubmission submission,
        CancellationToken token)
    {
        using var http = createClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accountToken);
        using var response = await http.PostAsJsonAsync("/api/play", submission, token).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            try
            {
                var reply = await response.Content.ReadFromJsonAsync<PlayResponse>(
                    cancellationToken: token).ConfigureAwait(false);
                if (reply?.Accepted == true) return new(true, true, false, status, null);
                if (reply is { Accepted: false, Duplicate: true }) return new(true, false, true, status, null);
            }
            catch (JsonException) { }
            catch (NotSupportedException) { }
            return new(false, false, false, status, "invalid_response");
        }
        var permanent = status is >= 400 and < 500 &&
            response.StatusCode != HttpStatusCode.TooManyRequests &&
            response.StatusCode != HttpStatusCode.RequestTimeout;
        return new(permanent, false, false, status, await ErrorCodeAsync(response, token).ConfigureAwait(false));
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response, CancellationToken token)
    {
        // Only fixed protocol codes reach logs/UI. Never expose arbitrary
        // response text (which might include credentials or proxy diagnostics).
        try
        {
            var reply = await response.Content.ReadFromJsonAsync<PlayResponse>(
                cancellationToken: token).ConfigureAwait(false);
            return reply?.Error switch
            {
                "invalid_play" or "not_qualified" or "configuration_required" or "rate_limited" or
                "invalid_timestamp" or "expired_play" or "unauthorized" or
                "temporarily_unavailable" or "invalid_request" or "not_found" or
                "account_service_unavailable" or "invalid_account_response" or
                "d1_write_failed" or "d1_read_failed" => reply.Error,
                _ => "worker_error",
            };
        }
        catch (JsonException) { return "non_json_response"; }
        catch (NotSupportedException) { return "non_json_response"; }
    }

    private static HttpClient CreateClient()
    {
        var http = PluginHttpClients.CreateMetadataClient();
        http.BaseAddress = new Uri(WorkerUrl);
        http.Timeout = TimeSpan.FromSeconds(10);
        return http;
    }

    private sealed record PopularResponse(PopularFeedVideo[] Videos);
    private sealed record CountReportResponse(bool Updated);
    private sealed record PlayResponse(bool? Accepted, bool? Duplicate, string? Error);
}

internal sealed record PopularSubmission(string VideoId, string EventId,
    double WatchedSeconds, double? DurationSeconds, long QualifiedAtUnix,
    long? ViewCount = null, long? ViewCountObservedAtUnix = null, PopularVideoMetadata? Metadata = null);

internal sealed record PopularFeedVideo(string VideoId, long? ViewCount = null, long? ViewCountObservedAtUnix = null,
    PopularVideoMetadata? Metadata = null, long MetadataRefreshAfterUnix = 0);
internal sealed record PopularVideoMetadata(string? Title = null, string? ChannelName = null, string? ChannelId = null,
    string? ThumbnailUrl = null, double? DurationSeconds = null, DateTimeOffset? UploadDate = null,
    long? ViewCount = null, long? ViewCountObservedAtUnix = null, long? MetadataObservedAtUnix = null)
{
    internal bool IsComplete => !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(ChannelName) &&
        !string.IsNullOrWhiteSpace(ChannelId) && !string.IsNullOrWhiteSpace(ThumbnailUrl) && UploadDate is not null && ViewCount is not null;
}
internal sealed record PopularMetadataReply(string? State = null, string? LeaseId = null, PopularVideoMetadata? Metadata = null,
    long RefreshAfterUnix = 0, bool Updated = false);
internal sealed record PopularCountClaim(string State, string? LeaseId = null, long? ViewCount = null, long? ViewCountObservedAtUnix = null);

internal sealed record PopularSubmitResult(bool Completed, bool Accepted, bool Duplicate,
    int HttpStatus, string? ErrorCode);

internal sealed class PopularWorkerException(int status, string errorCode, int retryAfterSeconds = 0)
    : Exception($"HTTP {status}: {errorCode}")
{
    internal int HttpStatus { get; } = status;
    internal string ErrorCode { get; } = errorCode;
    internal int RetryAfterSeconds { get; } = retryAfterSeconds;
}

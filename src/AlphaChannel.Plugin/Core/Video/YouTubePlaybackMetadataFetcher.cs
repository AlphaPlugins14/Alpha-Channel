using System.Text.Json;
using AlphaChannel.Plugin.YouTube;

namespace AlphaChannel.Plugin.Video;

// Presentation metadata for pasted links does not need the player/stream API.
// Keep its in-flight requests separate from full discovery enrichment: a slow
// full lookup must not prevent the small oEmbed response reaching playback.
internal sealed class YouTubePlaybackMetadataFetcher(
    HttpClient http, Func<CancellationToken, Task<IDisposable>> acquire,
    YouTubeMetadataCache? cache = null, Action<YouTubeFetchDiagnostic>? reportFailure = null,
    Func<bool>? isPaused = null)
{
    private readonly YouTubeMetadataCache cache = cache ?? YouTubeMetadataCache.Shared;
    private readonly YouTubeMetadataCache requests = new();

    internal async Task<VideoSearchEntry?> GetAsync(string videoId, CancellationToken token,
        YouTubeFetchSection section = YouTubeFetchSection.Background)
    {
        token.ThrowIfCancellationRequested();
        if (videoId.Length != 11 || videoId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            return null;
        if (cache.TryGet(videoId, out var known) && known is not null && YouTubeMetadataCache.HasPlaybackMetadata(known))
            return known;
        var result = await requests.GetOrCreateAsync(videoId, async request =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var stage = YouTubeFetchStage.SchedulerWait;
            request.SetCancellationContext(error => new YouTubeFetchCancelledException(YouTubeFetchMethod.OEmbed, stage,
                stage == YouTubeFetchStage.SchedulerWait && isPaused?.Invoke() == true, error));
            try
            {
                using var lease = await acquire(deadline.Token).ConfigureAwait(false);
                stage = YouTubeFetchStage.Fetching;
                var url = "https://www.youtube.com/watch?v=" + videoId;
                using var response = await http.GetAsync("https://www.youtube.com/oembed?format=json&url=" +
                    Uri.EscapeDataString(url), deadline.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
                using var json = JsonDocument.Parse(body);
                var title = json.RootElement.GetProperty("title").GetString();
                if (!YouTubeMetadataCache.HasTitle(title)) throw new InvalidDataException();
                var author = json.RootElement.TryGetProperty("author_name", out var value) ? value.GetString() : null;
                return new VideoSearchEntry(title!, url, author ?? string.Empty, null,
                    "https://i.ytimg.com/vi/" + videoId + "/hqdefault.jpg");
            }
            catch (OperationCanceledException error) when (!request.Token.IsCancellationRequested)
            {
                reportFailure?.Invoke(YouTubeFetchDiagnostic.Classify(section, YouTubeFetchData.TitleAndChannel, YouTubeFetchMethod.OEmbed,
                    request.AnnotateCancellation(error)));
                return null;
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or InvalidDataException)
            {
                reportFailure?.Invoke(YouTubeFetchDiagnostic.Classify(section, YouTubeFetchData.TitleAndChannel, YouTubeFetchMethod.OEmbed, error)
                    with { Stage = stage });
                return null;
            }
        }, token, 0).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (result is not null) cache.Store(videoId, result, replace: false);
        return cache.TryGet(videoId, out known) ? known : result;
    }
}

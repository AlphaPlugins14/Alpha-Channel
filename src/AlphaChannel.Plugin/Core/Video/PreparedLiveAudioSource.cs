namespace AlphaChannel.Plugin.Video;

// The studio readiness check and decoder share one listener connection. Never
// discard a verified stream and assume the next GET will reach the same source.
internal sealed class PreparedLiveAudioSource : IDisposable
{
    private static readonly Dictionary<string, PreparedLiveAudioSource> Pending = new(StringComparer.Ordinal);
    private readonly HttpClient http;
    private readonly HttpResponseMessage response;
    private readonly Stream audio;
    private readonly byte[] prefix;
    private PreparedLiveAudioSource(HttpClient http, HttpResponseMessage response, Stream audio, byte[] prefix)
    { this.http = http; this.response = response; this.audio = audio; this.prefix = prefix; }

    internal static async Task<PreparedLiveAudioSource?> OpenAsync(string url, Func<HttpClient> factory, CancellationToken token)
    {
        var http = factory();
        HttpResponseMessage? response = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            response = await http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not { } type ||
                !type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            {
                
                response.Dispose(); http.Dispose(); return null;
            }
            var audio = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var prefix = new byte[1024];
            await audio.ReadExactlyAsync(prefix, timeout.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new PreparedLiveAudioSource(http, response, audio, prefix);
        }
        catch { response?.Dispose(); http.Dispose(); return null; }
    }
    internal static void Store(string url, PreparedLiveAudioSource source)
    {
        lock (Pending) { if (Pending.Remove(url, out var old)) old.Dispose(); Pending[url] = source; }
    }
    internal static PreparedLiveAudioSource? Take(string url)
    {
        lock (Pending) return Pending.Remove(url, out var source) ? source : null;
    }
    internal static void Discard(string url) => Take(url)?.Dispose();
    internal async Task CopyToAsync(Stream output, CancellationToken token)
    {
        await output.WriteAsync(prefix, token).ConfigureAwait(false);
        await audio.CopyToAsync(output, token).ConfigureAwait(false);
    }
    public void Dispose() { audio.Dispose(); response.Dispose(); http.Dispose(); }
}

namespace AlphaChannel.Plugin.Video;

internal static class YouTubePoTokenRefresh
{
    // Clears cached tokens/minters, not the provider process or any active stream.
    // The following extraction also disables yt-dlp's own on-disk cache.
    internal static async Task InvalidateAsync(HttpClient client, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:4416/invalidate_caches");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}

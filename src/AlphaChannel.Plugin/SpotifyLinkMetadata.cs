using System.Net.Http;
using System.IO;
using System.Text.Json;

namespace AlphaChannel.Plugin;

internal static class SpotifyLinkMetadata
{
    internal static async Task<string> TitleAsync(string input)
    {
        if (!SpotifyLinks.TryParse(input, out var uri)) throw new ArgumentException("Invalid Spotify link.");
        var parts = uri.Split(':');
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var address = "https://open.spotify.com/" + parts[1] + "/" + parts[2];
        using var response = await http.GetAsync("https://open.spotify.com/oembed?url=" + Uri.EscapeDataString(address),
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var bounded = new MemoryStream();
        var bytes = new byte[4096];
        while (true)
        {
            var count = await body.ReadAsync(bytes, timeout.Token);
            if (count == 0) break;
            if (bounded.Length + count > 65536) throw new IOException("Spotify metadata was too large.");
            bounded.Write(bytes, 0, count);
        }
        using var metadata = JsonDocument.Parse(bounded.ToArray());
        var title = SpotifyLinks.Clean(metadata.RootElement.GetProperty("title").GetString(), 512).Trim();
        if (title.Length == 0) throw new IOException("Spotify did not provide a title.");
        return title;
    }
}

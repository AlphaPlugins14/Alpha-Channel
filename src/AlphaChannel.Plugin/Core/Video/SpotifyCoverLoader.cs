using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Video;

// Only retrieves opaque IDs from our own relay. Never follows host-supplied URLs.
internal sealed class SpotifyCoverLoader : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private string id = "", encoded = "";
    private Task? pending;
    private DateTime retryAfter;

    internal string? Get(string nextId, Action refresh)
    {
        lock (gate)
        {
            if (stop.IsCancellationRequested) return null;
            if (nextId != id) { id = nextId; encoded = ""; retryAfter = default; }
            if (encoded.Length > 0) return encoded;
            if (!Guid.TryParseExact(nextId, "N", out _) || pending is { IsCompleted: false } || DateTime.UtcNow < retryAfter) return null;
            retryAfter = DateTime.UtcNow.AddSeconds(15);
            pending = Task.Run(async () =>
            {
                try
                {
                    using var http = PluginHttpClients.CreateRedirectlessApiClient(Plugin.Cfg);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(8));
                    using var response = await http.GetAsync("/spotify-artwork/" + nextId, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength is > 524288) return;
                    using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
                    using var output = new MemoryStream();
                    var buffer = new byte[8192];
                    while (true)
                    {
                        var count = await body.ReadAsync(buffer, timeout.Token);
                        if (count == 0) break;
                        if (output.Length + count > 524288) return;
                        output.Write(buffer, 0, count);
                    }
                    var bytes = output.ToArray();
                    var info = SixLabors.ImageSharp.Image.Identify(bytes);
                    if (info.Width > 512 || info.Height > 512) return;
                    lock (gate)
                    {
                        if (stop.IsCancellationRequested || id != nextId) return;
                        encoded = Convert.ToBase64String(bytes);
                    }
                    refresh();
                }
                catch { /* Keep the normal middle text when artwork is unavailable. */ }
            });
            return null;
        }
    }
    public void Dispose() { lock (gate) { stop.Cancel(); encoded = ""; id = ""; } }
}

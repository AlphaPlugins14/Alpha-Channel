using System.Net.Http;
using System.Net.Http.Json;
using AlphaChannel.Plugin.Net;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace AlphaChannel.Plugin;

// One temporary lease, serialized requests, and no local files.
internal sealed class SpotifyCoverPublisher : IDisposable
{
    private readonly object gate = new();
    private Task? work;
    private string? desired, token, published, leaseToken;
    private string id = "";
    private DateTime nextAttempt;
    private bool disposed;
    internal string Id { get { lock (gate) return desired == published && token == leaseToken ? id : ""; } }

    internal void Update(string? image, string? bearer)
    {
        lock (gate)
        {
            if (disposed) return;
            if (desired != image || token != bearer) nextAttempt = default;
            desired = image; token = bearer;
            if (work is { IsCompleted: false } || DateTime.UtcNow < nextAttempt) return;
            work = Task.Run(SyncAsync);
        }
    }

    private async Task SyncAsync()
    {
        string? image, bearer, previousToken; string previousId;
        lock (gate) { image = disposed ? null : desired; bearer = token; previousToken = leaseToken; previousId = id; }
        try
        {
            if (previousId.Length > 0 && (image is null || image != published || bearer != previousToken))
            {
                using var oldHttp = PluginHttpClients.CreateRedirectlessApiClient(Plugin.Cfg, previousToken);
                oldHttp.Timeout = TimeSpan.FromSeconds(8);
                using var deletion = await oldHttp.DeleteAsync("/spotify-artwork/me/" + previousId);
                lock (gate) { id = ""; published = leaseToken = null; }
                previousId = "";
            }
            if (string.IsNullOrEmpty(image) || string.IsNullOrEmpty(bearer)) return;
            using var http = PluginHttpClients.CreateRedirectlessApiClient(Plugin.Cfg, bearer);
            http.Timeout = TimeSpan.FromSeconds(8);
            if (previousId.Length > 0)
            {
                using var renewal = await http.PostAsync("/spotify-artwork/me/" + previousId + "/touch", null);
                if (renewal.IsSuccessStatusCode) return;
                lock (gate) { id = ""; published = null; }
            }
            if (image.Length > 700000) return;
            var bytes = Convert.FromBase64String(image);
            var info = Image.Identify(bytes);
            if (info.Width > 1024 || info.Height > 1024) return;
            using var cover = Image.Load(new SixLabors.ImageSharp.Formats.DecoderOptions { MaxFrames = 1 }, bytes);
            if (cover.Width > 512 || cover.Height > 512)
                cover.Mutate(c => c.Resize(new ResizeOptions { Size = new Size(512, 512), Mode = ResizeMode.Max }));
            using var output = new MemoryStream();
            await cover.SaveAsPngAsync(output);
            if (output.Length > 524288) return;
            using var body = new ByteArrayContent(output.ToArray());
            body.Headers.ContentType = new("image/png");
            using var response = await http.PutAsync("/spotify-artwork/me/", body);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<CoverResponse>();
            if (result is null || !Guid.TryParseExact(result.Id, "N", out _)) return;
            lock (gate) { id = result.Id; published = image; leaseToken = bearer; }
        }
        catch { /* Solo art remains available if the server cannot host a cover. Leases expire automatically. */ }
        finally
        {
            lock (gate) nextAttempt = desired != image || token != bearer ? default : DateTime.UtcNow.AddSeconds(30);
        }
    }

    private sealed record CoverResponse(string Id);
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; desired = null;
            var previous = work;
            work = Task.Run(async () => { if (previous is not null) await previous; await SyncAsync(); });
        }
    }
}

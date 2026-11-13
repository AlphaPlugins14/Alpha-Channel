using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin.Core;

internal sealed record ChatPreviewResult(ChatLinkMetadata? Page, VideoSearchEntry? Video, bool Loading);

internal sealed class ChatLinkPreviewCache : IDisposable
{
    private readonly Dictionary<string, ChatPreviewResult> entries = new(StringComparer.Ordinal);
    private readonly HashSet<Task> loads = [];
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim requests = new(2);
    private readonly HttpClient http = new(new ChatPreviewHttpHandler()) { Timeout = TimeSpan.FromSeconds(12) };
    private bool disposed;

    internal ChatPreviewResult Get(ChatLink link, Func<string, CancellationToken, Task<VideoSearchEntry?>> videoLookup)
    {
        lock (gate)
        {
            if (disposed) return new(null, null, false);
            if (entries.TryGetValue(link.Url, out var result)) return result;
            if (entries.Count >= 128)
            {
                var retired = entries.FirstOrDefault(e => !e.Value.Loading).Key;
                if (retired is null) return new(null, null, false);
                entries.Remove(retired);
            }
            entries[link.Url] = result = new(null, null, true);
            var load = LoadAsync(link, videoLookup);
            loads.Add(load);
            _ = load.ContinueWith(completed => { lock (gate) loads.Remove(completed); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return result;
        }
    }

    private async Task LoadAsync(ChatLink link, Func<string, CancellationToken, Task<VideoSearchEntry?>> videoLookup)
    {
        var result = new ChatPreviewResult(null, null, false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var entered = false;
        try
        {
            await requests.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            if (link.YouTubeId is { } id)
                result = result with { Video = await videoLookup("https://www.youtube.com/watch?v=" + id, timeout.Token).ConfigureAwait(false) };
            else
            {
                using var response = await http.GetAsync(link.Url, timeout.Token).ConfigureAwait(false);
                if (response.Content.Headers.ContentType?.MediaType is "text/html" or "application/xhtml+xml")
                    result = result with { Page = ChatLinkPreview.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false),
                        response.RequestMessage!.RequestUri!) };
            }
        }
        catch (Exception) { /* Keep the original URL usable if preview retrieval fails. */ }
        finally
        {
            if (entered) requests.Release();
            lock (gate) if (!disposed) entries[link.Url] = result;
        }
    }

    public void Dispose()
    {
        Task[] pending;
        lock (gate) { if (disposed) return; disposed = true; entries.Clear(); pending = loads.ToArray(); }
        lifetime.Cancel();
        http.Dispose();
        // Release synchronization resources only after in-flight work exits.
        _ = Task.WhenAll(pending).ContinueWith(_ => { lifetime.Dispose(); requests.Dispose(); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}

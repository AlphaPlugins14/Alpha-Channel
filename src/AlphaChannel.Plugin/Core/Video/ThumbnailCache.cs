using System.Collections.Concurrent;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Bindings.ImGui;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Video;

// Queue entries and search results share a bounded texture cache by URL.
// Recently drawn textures are protected from eviction until draw commands finish.
// A null entry is an in-flight load or a failed load awaiting its retry cooldown.
// Callers skip drawing until a texture is available.
// ConcurrentDictionary, not a plain Dictionary - LoadAsync's continuation after the awaits below
// resumes on an arbitrary thread pool thread, not necessarily the main thread Get() is called
// from every frame, so this is a real cross-thread read/write, not just a style preference.
internal sealed class ThumbnailCache : IDisposable
{
    private readonly ConcurrentDictionary<string, IDalamudTextureWrap?> cache = new();
    private readonly ConcurrentDictionary<string, Task> pendingLoads = new();
    private readonly ConcurrentDictionary<(string Url, int Size), IDalamudTextureWrap?> avatarCache = new();
    private readonly ConcurrentDictionary<(string Url, int Size), Task> pendingAvatarLoads = new();
    private readonly ConcurrentDictionary<string, int> generations = new();
    private readonly ConcurrentDictionary<(string Url, int Size), int> avatarGenerations = new();
    private int nextGeneration;
    private readonly Dictionary<string,long> retryAfter = new();
    private readonly Dictionary<(string Url,int Size),long> avatarRetryAfter = new();
    private readonly HashSet<Task> allLoads = new();
    private readonly HttpClient http;
    private readonly CancellationTokenSource lifetime = new();
    private volatile bool disposed;
    private readonly object publicationGate = new();
    private readonly TextureCacheBudget<string> thumbnailBudget = new(96, frameClock: () => ImGui.GetFrameCount());
    private readonly TextureCacheBudget<(string Url, int Size)> avatarBudget = new(64, frameClock: () => ImGui.GetFrameCount());

    private readonly long retryDelay;
    internal ThumbnailCache(HttpClient? client = null,TimeSpan? failureRetryDelay = null)
    {
        http=client??PluginHttpClients.CreateMetadataClient();
        retryDelay=(long)(failureRetryDelay??TimeSpan.FromSeconds(30)).TotalMilliseconds;
    }

    public IDalamudTextureWrap? Get(string? url)
    {
        lock (publicationGate)
        {
            if (disposed || string.IsNullOrEmpty(url))
            {
                return null;
            }

            if (!thumbnailBudget.Admit(url, key =>
            {
                if (cache.TryRemove(key, out var retired)) retired?.Dispose();
                generations.TryRemove(key, out _); retryAfter.Remove(key);
            })) return null;
            if (cache.TryGetValue(url, out var wrap))
            {
                if (wrap is not null || !retryAfter.TryGetValue(url,out var retry) || Environment.TickCount64 < retry) return wrap;
                retryAfter.Remove(url); cache.TryRemove(url,out _);
            }

            cache[url] = null;
            var generation = generations.GetOrAdd(url, _ => Interlocked.Increment(ref nextGeneration));
            var load =
                LoadAsync(url, generation, lifetime.Token);

            pendingLoads[url] = load;
            allLoads.Add(load);
            _ = load.ContinueWith(
                completedTask =>
                {
                    lock (publicationGate) allLoads.Remove(completedTask);
                    pendingLoads.TryRemove(new KeyValuePair<string, Task>(url, completedTask));
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return null;
        }
    }

    // ImGui's image draw shrinks large textures in a single GPU sample. Prefilter avatars at
    // their actual on-screen size so small circular portraits retain detail instead of blurring.
    public IDalamudTextureWrap? GetAvatar(string? url, float diameter)
    {
        lock (publicationGate)
        {
        if (disposed || string.IsNullOrEmpty(url))
            return null;

        var requested = Math.Clamp((int)MathF.Ceiling(diameter), 16, 256);
        var size = requested <= 32 ? 32 : requested <= 64 ? 64 : requested <= 128 ? 128 : 256;
        var key = (url, size);
        if (!avatarBudget.Admit(key, retiredKey =>
        {
            if (avatarCache.TryRemove(retiredKey, out var retired)) retired?.Dispose();
            avatarGenerations.TryRemove(retiredKey, out _); avatarRetryAfter.Remove(retiredKey);
        })) return null;
        if (avatarCache.TryGetValue(key, out var wrap)) {
            if (wrap is not null || !avatarRetryAfter.TryGetValue(key,out var retry) || Environment.TickCount64 < retry) return wrap;
            avatarRetryAfter.Remove(key); avatarCache.TryRemove(key,out _);
        }
        if (!avatarCache.TryAdd(key, null))
            return avatarCache.TryGetValue(key, out wrap) ? wrap : null;

        var generation = avatarGenerations.GetOrAdd(key, _ => Interlocked.Increment(ref nextGeneration));
        var load = LoadAvatarAsync(key, generation, lifetime.Token);
        pendingAvatarLoads[key] = load;
        allLoads.Add(load);
        _ = load.ContinueWith(
            completedTask =>
            {
                lock (publicationGate) allLoads.Remove(completedTask);
                pendingAvatarLoads.TryRemove(new KeyValuePair<(string Url, int Size), Task>(key, completedTask));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return null;
        }
    }

    public void Invalidate(string? url)
    {
        lock (publicationGate)
        {
        if (disposed || string.IsNullOrEmpty(url))
        {
            return;
        }

        if (cache.TryRemove(url, out var wrap))
        {
            wrap?.Dispose();
        }
        thumbnailBudget.Remove(url); retryAfter.Remove(url);

        generations.TryRemove(url, out _);
        foreach (var key in avatarCache.Keys.Where(key => key.Url == url))
        {
            if (avatarCache.TryRemove(key, out var avatarWrap))
                avatarWrap?.Dispose();
            avatarBudget.Remove(key); avatarRetryAfter.Remove(key);
            avatarGenerations.TryRemove(key, out _);
        }
        }
    }

    private async Task LoadAvatarAsync((string Url, int Size) key, int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var sourceBytes = await BoundedImageDecode.DownloadAsync(http, key.Url, cancellationToken).ConfigureAwait(false);
            using var work = await BoundedImageDecode.EnterAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            using var image = BoundedImageDecode.Load(sourceBytes, key.Size, key.Size);
            image.Mutate(context => context.Resize(new ResizeOptions
            {
                Size = new Size(key.Size, key.Size),
                Mode = ResizeMode.Crop,
                Sampler = KnownResamplers.Lanczos3,
            }));
            using var pngStream = new MemoryStream();
            await image.SaveAsync(pngStream, new PngEncoder(), cancellationToken).ConfigureAwait(false);
            work.Dispose();
            var wrap = await Plugin.TextureProvider.CreateFromImageAsync(pngStream.ToArray()).ConfigureAwait(false);

            lock (publicationGate)
            {
            if (disposed || cancellationToken.IsCancellationRequested ||
                !avatarGenerations.TryGetValue(key, out var currentGeneration) || currentGeneration != generation ||
                !avatarCache.TryGetValue(key, out _))
            {
                wrap.Dispose();
                return;
            }

            if (!avatarCache.TryUpdate(key, wrap, null))
                wrap.Dispose();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal during plugin shutdown.
        }
        catch (Exception)
        {
            lock (publicationGate) if (!disposed && avatarGenerations.TryGetValue(key,out var current) && current == generation) avatarRetryAfter[key] = Environment.TickCount64 + retryDelay;
        }
    }

    private async Task LoadAsync(string url, int generation, CancellationToken cancellationToken)
    {
        try
        {
            byte[] sourceBytes;
            try { sourceBytes = await BoundedImageDecode.DownloadAsync(http, url, cancellationToken).ConfigureAwait(false); }
            catch (HttpRequestException error) when (error.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone &&
                YouTubeThumbnailFallback(url) is not null)
            {
                sourceBytes = await BoundedImageDecode.DownloadAsync(http, YouTubeThumbnailFallback(url)!, cancellationToken).ConfigureAwait(false);
            }
            using var work = await BoundedImageDecode.EnterAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // Thumbnail URLs (YouTube, Twitch) are JPEG. Dalamud's CreateFromImageAsync only
            // documents/reliably supports .tex and .png - handing it a JPEG directly fails with
            // "the file is not a TexFile" every time. Re-encode to PNG ourselves first via
            // ImageSharp, already a dependency for the title-banner texture rendering.
            using var image = BoundedImageDecode.Load(sourceBytes, 512, 512);
            using var pngStream = new MemoryStream();
            await image.SaveAsync(pngStream, new PngEncoder(), cancellationToken).ConfigureAwait(false);
            pngStream.Position = 0;
            work.Dispose();

            var wrap = await Plugin.TextureProvider.CreateFromImageAsync(pngStream.ToArray()).ConfigureAwait(false);

            lock (publicationGate)
            {
            if (disposed || cancellationToken.IsCancellationRequested ||
                !generations.TryGetValue(url, out var currentGeneration) || currentGeneration != generation)
            {
                wrap.Dispose();
                return;
            }

            if (!cache.TryUpdate(url, wrap, null)) wrap.Dispose();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal during plugin shutdown.
        }
        catch (Exception)
        {
            lock (publicationGate) if (!disposed && generations.TryGetValue(url,out var current) && current == generation) retryAfter[url] = Environment.TickCount64 + retryDelay;
        }
    }

    private static string? YouTubeThumbnailFallback(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute,out var uri) || uri.Scheme!="https" ||
            !System.Text.RegularExpressions.Regex.IsMatch(uri.Host,@"^(i\d*\.ytimg\.com|img\.youtube\.com)$")) return null;
        var match=System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath,@"^/(?:vi|vi_webp)/([A-Za-z0-9_-]{11})/[^/]+$");
        if (!match.Success) return null;
        var fallback="https://i.ytimg.com/vi/"+match.Groups[1].Value+"/hqdefault.jpg";
        return uri.GetLeftPart(UriPartial.Path)==fallback ? null : fallback;
    }

    public void Dispose()
    {
        Task[] loads;
        lock (publicationGate)
        {
        if (disposed)
        {
            return;
        }

        disposed = true;
        loads = allLoads.ToArray();
        allLoads.Clear();
        }
        lifetime.Cancel();
        http.Dispose();

        try
        {
            Task.WaitAll(
                loads,
                TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            
        }

        foreach (var wrap in cache.Values)
        {
            wrap?.Dispose();
        }

        foreach (var wrap in avatarCache.Values)
        {
            wrap?.Dispose();
        }

        cache.Clear();
        pendingLoads.Clear();
        avatarCache.Clear();
        pendingAvatarLoads.Clear();
        generations.Clear();
        avatarGenerations.Clear(); retryAfter.Clear(); avatarRetryAfter.Clear();
        thumbnailBudget.Clear();
        avatarBudget.Clear();
        // A provider upload may outlive the three-second shutdown wait; its drain
        // continuation owns the cancellation source until every load finishes.
        _ = Task.WhenAll(loads).ContinueWith(_ => lifetime.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}

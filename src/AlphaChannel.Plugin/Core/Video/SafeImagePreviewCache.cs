using System.Collections.Concurrent;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Bindings.ImGui;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace AlphaChannel.Plugin.Video;

internal enum ImagePreviewState
{
    Empty,
    Loading,
    Ready,
    Failed
}

internal readonly record struct ImagePreviewResult(
    ImagePreviewState State,
    IDalamudTextureWrap? Texture,
    string? Error);

/// <summary>
/// Secure preview cache for user-supplied image URLs.
///
/// Every image passes through the injected bounded downloader (approved TV
/// hosts by default, public chat hosts for linked images). Loading and failure are represented
/// separately so the UI does not show an endless loading state.
/// </summary>
internal class SafeImagePreviewCache : IDisposable
{
    private sealed record CacheEntry(
        ImagePreviewState State,
        IDalamudTextureWrap? Texture,
        string? Error);

    private readonly ConcurrentDictionary<string, CacheEntry> cache =
        new(
            StringComparer.Ordinal);

    private readonly IDisposable downloader;
    private readonly Func<string, CancellationToken, Task<SafeImageDownloadResult>> download;
    private readonly Func<string, bool> validate;
    private readonly int decodeSize;

    internal SafeImagePreviewCache() : this(new SafeImageDownloader()) { }

    private SafeImagePreviewCache(SafeImageDownloader source) : this(source, source.DownloadAsync,
        url => ImageHostPolicy.TryValidateUrl(url, out _, out _), 512) { }

    internal SafeImagePreviewCache(IDisposable owner,
        Func<string, CancellationToken, Task<SafeImageDownloadResult>> download,
        Func<string, bool> validate, int decodeSize)
    {
        downloader = owner;
        this.download = download;
        this.validate = validate;
        this.decodeSize = decodeSize;
    }

    private readonly CancellationTokenSource disposalCancellation =
        new();

    private bool disposed;
    private readonly object publicationGate = new();
    private readonly TextureCacheBudget<string> budget = new(32, frameClock: () => ImGui.GetFrameCount());
    private readonly HashSet<Task> pending = new();

    internal ImagePreviewResult Get(
        string? url)
    {
        lock (publicationGate)
        {
        if (disposed) return new(ImagePreviewState.Empty, null, null);
        if (string.IsNullOrWhiteSpace(
                url))
        {
            return new ImagePreviewResult(
                ImagePreviewState.Empty,
                null,
                null);
        }

        var cleanUrl =
            url.Trim();

        if (!validate(cleanUrl))
        {
            return new ImagePreviewResult(
                ImagePreviewState.Failed,
                null,
                "This image URL is not supported.");
        }

        if (!budget.Admit(cleanUrl, key =>
            {
                if (cache.TryRemove(key, out var retired)) retired.Texture?.Dispose();
            })) return new(ImagePreviewState.Loading, null, null);

        if (cache.TryGetValue(
                cleanUrl,
                out var existing))
        {
            return ToResult(
                existing);
        }

        var loading =
            new CacheEntry(
                ImagePreviewState.Loading,
                null,
                null);

        if (cache.TryAdd(
                cleanUrl,
                loading))
        {
            var task = LoadAsync(cleanUrl, loading, disposalCancellation.Token);
            pending.Add(task);
            _ = task.ContinueWith(completed =>
            {
                lock (publicationGate)
                    pending.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        if (cache.TryGetValue(
                cleanUrl,
                out existing))
        {
            return ToResult(
                existing);
        }

        return new ImagePreviewResult(
            ImagePreviewState.Loading,
            null,
            null);
        }
    }

    internal void Invalidate(
        string? url)
    {
        lock (publicationGate)
        {
        if (string.IsNullOrWhiteSpace(
                url))
        {
            return;
        }

        budget.Remove(url.Trim());
        if (cache.TryRemove(
                url.Trim(),
                out var removed))
        {
            removed.Texture?.Dispose();
        }
        }
    }

    private async Task LoadAsync(
        string url, CacheEntry loading, CancellationToken token)
    {
        try
        {
            var result =
                await BoundedImageDecode.DownloadAsync(ct => download(url,ct),token)
                    .ConfigureAwait(false);

            if (!result.IsSuccess ||
                result.Data is null)
            {
                SetFailure(
                    url,
                    loading,
                    result.Error ??
                    "The image could not be downloaded.");

                return;
            }

            using var work = await BoundedImageDecode.EnterAsync(token).ConfigureAwait(false);
            using var image =
                BoundedImageDecode.Load(result.Data, decodeSize, decodeSize);

            using var pngStream =
                new MemoryStream();

            await image
                .SaveAsync(
                    pngStream,
                    new PngEncoder(),
                    token)
                .ConfigureAwait(false);
            work.Dispose();

            var texture =
                await Plugin.TextureProvider
                    .CreateFromImageAsync(
                        pngStream.ToArray())
                    .ConfigureAwait(false);

            lock (publicationGate)
            {
                if (disposed || token.IsCancellationRequested ||
                    !cache.TryGetValue(url, out var current) || !ReferenceEquals(current, loading))
                {
                    texture.Dispose();
                    return;
                }
                cache[url] = new(ImagePreviewState.Ready, texture, null);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal plugin shutdown.
        }
        catch (Exception)
        {
            {}

            SetFailure(
                url,
                loading,
                "The image preview could not be created.");
        }
    }

    private void SetFailure(
        string url,
        CacheEntry loading,
        string error)
    {
        lock (publicationGate)
        {
            if (!disposed && cache.TryGetValue(url, out var current) && ReferenceEquals(current, loading))
                cache[url] = new(ImagePreviewState.Failed, null, error);
        }
    }

    private static ImagePreviewResult ToResult(
        CacheEntry entry)
    {
        return new ImagePreviewResult(
            entry.State,
            entry.Texture,
            entry.Error);
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

        disposed =
            true;

        foreach (var entry in cache.Values)
        {
            entry.Texture?.Dispose();
        }

        cache.Clear();
        budget.Clear();
        loads = pending.ToArray();
        pending.Clear();
        }
        disposalCancellation.Cancel();
        downloader.Dispose();
        // Do not block the UI on a texture-provider upload. Late results are
        // rejected under the publication lock; the token source stays valid
        // until every worker using it has exited.
        _ = Task.WhenAll(loads).ContinueWith(_ => disposalCancellation.Dispose(),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}

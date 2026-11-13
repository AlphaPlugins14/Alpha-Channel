using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AlphaChannel.Plugin.Video;

// Shared across caches and full-screen images so several visible cards cannot
// multiply full-resolution decode allocations without a bound.
internal static class BoundedImageDecode
{
    internal const int MaximumDownloadBytes = 15 * 1024 * 1024;
    private static readonly SemaphoreSlim Workers = new(2, 2);
    private static readonly SemaphoreSlim Downloads = new(8, 8);
    private static readonly SixLabors.ImageSharp.Configuration Configuration = CreateConfiguration();

    private static SixLabors.ImageSharp.Configuration CreateConfiguration()
    {
        var configuration = SixLabors.ImageSharp.Configuration.Default.Clone();
        configuration.MaxDegreeOfParallelism = 2;
        configuration.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
        {
            AllocationLimitMegabytes = 256,
            MaximumPoolSizeMegabytes = 32,
        });
        return configuration;
    }

    internal static async Task<IDisposable> EnterAsync(CancellationToken token)
    {
        await Workers.WaitAsync(token).ConfigureAwait(false);
        return new Lease();
    }

    internal static IDisposable Enter(CancellationToken token = default)
    {
        Workers.Wait(token);
        return new Lease();
    }

    private sealed class Lease : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) Workers.Release(); }
    }

    internal static DecoderOptions Options(int width, int height) => new()
    {
        Configuration = Configuration,
        MaxFrames = 1,
        SkipMetadata = true,
        TargetSize = new Size(width, height),
    };

    internal static Image<Bgra32> Load(byte[] data, int width, int height)
    {
        if (data.Length > MaximumDownloadBytes) throw new InvalidDataException("Image exceeds the download limit.");
        var options = Options(width, height);
        var info = Image.Identify(options, data);
        if (info is null || info.Width <= 0 || info.Height <= 0 ||
            info.Width > 8192 || info.Height > 8192 || (long)info.Width * info.Height > 40_000_000)
            throw new InvalidDataException("Image dimensions exceed the safety limit.");
        return Image.Load<Bgra32>(options, data);
    }

    internal static Task<byte[]> LoadLocalPngAsync(string path, CancellationToken token) => Task.Run(async () =>
    {
        using var lease = await EnterAsync(token).ConfigureAwait(false);
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length > MaximumDownloadBytes) throw new InvalidDataException("Image exceeds the file limit.");
        using var bytes = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + read > MaximumDownloadBytes) throw new InvalidDataException("Image exceeds the file limit.");
            bytes.Write(buffer, 0, read);
        }
        token.ThrowIfCancellationRequested();
        using var image = Load(bytes.ToArray(), 1920, 1080);
        if (image.Width > 1920 || image.Height > 1080)
            image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(1920, 1080), Mode = ResizeMode.Max }));
        token.ThrowIfCancellationRequested();
        using var png = new MemoryStream();
        await image.SaveAsPngAsync(png, token).ConfigureAwait(false);
        return png.ToArray();
    }, token);

    internal static async Task<byte[]> DownloadAsync(HttpClient http, string url, CancellationToken token)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));token=timeout.Token;
        await Downloads.WaitAsync(token).ConfigureAwait(false);
        try {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumDownloadBytes)
            throw new InvalidDataException("Image exceeds the download limit.");
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > MaximumDownloadBytes) throw new InvalidDataException("Image exceeds the download limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
        } finally { Downloads.Release(); }
    }
    internal static async Task<T> DownloadAsync<T>(Func<CancellationToken,Task<T>> download,CancellationToken token)
    {
        await Downloads.WaitAsync(token).ConfigureAwait(false);
        try {return await download(token).ConfigureAwait(false);}
        finally {Downloads.Release();}
    }
}

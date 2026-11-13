using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AlphaChannel.Plugin.Video;

internal static class AvatarImagePreparation
{
    internal const int MaximumSourceBytes = 5 * 1024 * 1024;
    internal const int MaximumEdge = 512;
    internal const int MaximumUploadBytes = 1024 * 1024;

    internal static Task<byte[]> PrepareAsync(string path, CancellationToken token = default) => Task.Run(async () =>
    {
        using var lease = await BoundedImageDecode.EnterAsync(token).ConfigureAwait(false);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.Asynchronous);
        if (file.Length == 0 || file.Length > MaximumSourceBytes)
            throw new InvalidDataException("Choose a PNG, JPG or WebP image up to 5 MB.");
        using var input = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await file.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (input.Length + read > MaximumSourceBytes) throw new InvalidDataException("Image exceeds 5 MB.");
            input.Write(buffer, 0, read);
        }
        var bytes = input.ToArray();
        var format = Image.DetectFormat(bytes);
        if (format.Name.ToUpperInvariant() is not ("PNG" or "JPEG" or "WEBP"))
            throw new InvalidDataException("Choose a PNG, JPG or WebP image.");
        var configuration = BoundedImageDecode.Options(MaximumEdge, MaximumEdge).Configuration;
        var options = new DecoderOptions { Configuration = configuration, MaxFrames = 1, SkipMetadata = false };
        var info = Image.Identify(options, bytes);
        if (info is null || info.Width <= 0 || info.Height <= 0 || info.Width > 8192 || info.Height > 8192 ||
            (long)info.Width * info.Height > 40_000_000)
            throw new InvalidDataException("Image exceeds 8192 pixels or 40 megapixels.");
        using var image = Image.Load<Rgba32>(options, bytes);
        image.Mutate(x => x.AutoOrient());
        Resize(image, MaximumEdge);
        // Copy pixels only: no GPS, EXIF, comments or extra animation frames.
        var pixels = new Rgba32[image.Width * image.Height];
        image.CopyPixelDataTo(pixels);
        using var clean = Image.LoadPixelData<Rgba32>(pixels, image.Width, image.Height);
        // Retain the colour profile so removing personal metadata does not change
        // how colour-managed clients interpret the original image's colours.
        clean.Metadata.IccProfile = image.Metadata.IccProfile?.DeepClone();
        Array.Clear(pixels);
        using var output = new MemoryStream();
        await clean.SaveAsPngAsync(output, token).ConfigureAwait(false);
        if (output.Length > MaximumUploadBytes)
        {
            // Fully noisy RGBA at 512² can exceed 1 MiB by PNG overhead alone.
            // Fit the existing server without lossy encoding or palette reduction.
            // Even incompressible 500² RGBA has space for PNG framing below 1 MiB.
            Resize(clean, 500);
            output.SetLength(0);
            output.Position = 0;
            await clean.SaveAsPngAsync(output, token).ConfigureAwait(false);
        }
        if (output.Length > MaximumUploadBytes) throw new InvalidDataException("Prepared avatar exceeds the upload limit.");
        return output.ToArray();
    }, token);

    private static void Resize(Image<Rgba32> image, int edge)
    {
        if (image.Width <= edge && image.Height <= edge) return;
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(edge, edge), Mode = ResizeMode.Max,
            Sampler = KnownResamplers.Lanczos3, Compand = true
        }));
    }
}

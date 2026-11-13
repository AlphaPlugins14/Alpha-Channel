using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AlphaChannel.Plugin.Video;

internal sealed record PreparedChatImage(byte[] Bytes, int Width, int Height);
internal static class ChatImagePreparation
{
    internal const int MaximumSourceBytes = 20 * 1024 * 1024;
    internal static async Task<PreparedChatImage> PrepareAsync(string path, CancellationToken token = default)
    {
        using var lease = await BoundedImageDecode.EnterAsync(token).ConfigureAwait(false);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.Asynchronous);
        if (file.Length > MaximumSourceBytes) throw new InvalidOperationException("Choose an image up to 20 MB. PNG, JPEG and WebP are supported.");
        using var input = new MemoryStream(); var buffer = new byte[16384]; int read;
        while ((read = await file.ReadAsync(buffer, token).ConfigureAwait(false)) > 0) {
            if (input.Length + read > MaximumSourceBytes) throw new InvalidOperationException("Image exceeds 20 MB.");
            input.Write(buffer, 0, read);
        }
        var bytes = input.ToArray();
        try {
            var format = Image.DetectFormat(bytes);
            if (format.Name.ToUpperInvariant() is not ("PNG" or "JPEG" or "WEBP")) throw new InvalidOperationException("Choose a PNG, JPEG or WebP image.");
            var options = BoundedImageDecode.Options(3840, 3840);
            var info = Image.Identify(options, bytes);
            if (info is null || info.Width > 8192 || info.Height > 8192 || (long)info.Width * info.Height > 40_000_000)
                throw new InvalidOperationException("Image exceeds 8192 pixels or 40 megapixels. Full-screen 4K screenshots are supported.");
            // Read orientation before discarding metadata, then copy pixels into a fresh image.
            var factor = Math.Min(1d, 3840d / Math.Max(info.Width, info.Height));
            var target = new Size(Math.Max(1,(int)(info.Width * factor)), Math.Max(1,(int)(info.Height * factor)));
            var orientedOptions = new SixLabors.ImageSharp.Formats.DecoderOptions { Configuration = options.Configuration, MaxFrames = 1, SkipMetadata = false, TargetSize = target };
            using var decoded = Image.Load<Rgba32>(orientedOptions, bytes);
            decoded.Mutate(x => x.AutoOrient());
            if (decoded.Width > 3840 || decoded.Height > 3840)
                decoded.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(3840, 3840), Mode = ResizeMode.Max, Sampler = KnownResamplers.Lanczos3 }));
            var pixels = new Rgba32[decoded.Width * decoded.Height]; decoded.CopyPixelDataTo(pixels);
            using var clean = Image.LoadPixelData<Rgba32>(pixels, decoded.Width, decoded.Height);
            Array.Clear(pixels);
            int[] qualities = [88,84,80,84,80,80];
            for (var attempt = 0; attempt < qualities.Length; attempt++) {
                token.ThrowIfCancellationRequested();
                using var output = new MemoryStream();
                await clean.SaveAsync(output, new WebpEncoder { Quality = qualities[attempt], FileFormat = WebpFileFormatType.Lossy }, token).ConfigureAwait(false);
                if (output.Length <= Auth.GroupChatClient.MaxImageBytes) return new(output.ToArray(), clean.Width, clean.Height);
                if (attempt is 2 or 4) clean.Mutate(x => x.Resize(new ResizeOptions { Size = new Size((int)(clean.Width * .85), (int)(clean.Height * .85)), Mode = ResizeMode.Max }));
            }
            throw new InvalidOperationException("Image could not be compressed below 3 MB. Please choose a smaller image.");
        } finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
}

using AlphaChannel.Plugin.Core;
using SixLabors.ImageSharp;

namespace AlphaChannel.Plugin.Video;

// External chat media uses the same DNS-pinned transport as webpage previews.
// No cookies, credentials, R2 uploads, or private-group media keys are involved.
internal sealed class ChatLinkedImageDownloader : IDisposable
{
    private readonly HttpClient http;

    internal ChatLinkedImageDownloader(HttpMessageHandler? transport = null)
    {
        var handler = transport is null ? new ChatPreviewHttpHandler() : new ChatPreviewHttpHandler(transport);
        handler.ImageByteLimit = BoundedImageDecode.MaximumDownloadBytes;
        http = new(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    internal static bool TryValidateUrl(string url, out string? error)
    {
        error = ChatLinkPreview.IsDirectImage(url) ? null : "Use a direct PNG, JPEG, WebP, GIF or BMP image link.";
        return error is null;
    }

    internal async Task<SafeImageDownloadResult> DownloadAsync(string url, CancellationToken token)
    {
        if (!TryValidateUrl(url, out var error)) return SafeImageDownloadResult.Failed(error!);
        try
        {
            using var response = await http.GetAsync(url, token).ConfigureAwait(false);
            var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (type is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif" or "image/bmp" or "image/x-ms-bmp"))
                return SafeImageDownloadResult.Failed("The link did not return a supported image.");
            var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            var format = Image.DetectFormat(bytes)?.Name.ToUpperInvariant();
            if (format is not ("PNG" or "JPEG" or "WEBP" or "GIF" or "BMP"))
                return SafeImageDownloadResult.Failed("The file is not a supported image.");
            var info = Image.Identify(BoundedImageDecode.Options(1024, 1024), bytes);
            if (info is null || info.Width <= 0 || info.Height <= 0 || info.Width > 8192 || info.Height > 8192 ||
                (long)info.Width * info.Height > 40_000_000)
                return SafeImageDownloadResult.Failed("Image dimensions exceed the safety limit.");
            return new(bytes, response.RequestMessage?.RequestUri, null);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            return SafeImageDownloadResult.Failed("Image unavailable or exceeds the 15 MB safety limit.");
        }
    }

    public void Dispose() => http.Dispose();
}

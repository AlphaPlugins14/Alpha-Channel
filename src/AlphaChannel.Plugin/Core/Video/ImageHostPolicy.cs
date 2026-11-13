using System.Net;

namespace AlphaChannel.Plugin.Video;

/// <summary>
/// Applies the first layer of validation to remotely hosted images.
///
/// The image downloader must additionally validate DNS results, redirects,
/// response size, file signatures and decoded dimensions. Host approval alone
/// is not sufficient protection.
/// </summary>
internal static class ImageHostPolicy
{
    internal const long MaximumDownloadBytes =
        15L * 1024L * 1024L;

    internal const int MaximumWidth =
        8192;

    internal const int MaximumHeight =
        8192;

    internal const long MaximumPixels =
        40_000_000L;

    internal const int MaximumRedirects =
        4;

    internal static readonly TimeSpan DownloadTimeout =
        TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> AllowedHosts =
      new(
          StringComparer.OrdinalIgnoreCase)
      {
        "i.imgur.com",
        "i.postimg.cc",
        "myimgs.org",
        "cdn.myimgs.org",
        "cdn.imgpile.com"
      };

    internal static IReadOnlyCollection<string> SupportedHosts =>
        AllowedHosts;

    internal static bool TryValidateUrl(
        string? value,
        out Uri? uri,
        out string? error)
    {
        uri =
            null;

        error =
            null;

        if (string.IsNullOrWhiteSpace(
                value))
        {
            error =
                "Enter an image URL.";

            return false;
        }

        if (!Uri.TryCreate(
                value.Trim(),
                UriKind.Absolute,
                out var parsed))
        {
            error =
                "The image URL is not valid.";

            return false;
        }

        if (!string.Equals(
                parsed.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
        {
            error =
                "Image URLs must use HTTPS.";

            return false;
        }

        if (!string.IsNullOrEmpty(
                parsed.UserInfo))
        {
            error =
                "Image URLs cannot contain a username or password.";

            return false;
        }

        if (!parsed.IsDefaultPort &&
            parsed.Port != 443)
        {
            error =
                "Image URLs must use the standard HTTPS port.";

            return false;
        }

        if (IPAddress.TryParse(
                parsed.Host,
                out _))
        {
            error =
                "Direct IP-address image URLs are not supported.";

            return false;
        }

        if (!AllowedHosts.Contains(
          parsed.IdnHost))
        {
            error =
                "This image host is not supported. " +
                "Use MyImgs, ImgPile, Imgur, or Postimages.";

            return false;
        }

        if (string.Equals(
         parsed.IdnHost,
         "myimgs.org",
         StringComparison.OrdinalIgnoreCase))
        {
            const string suppliedPathPrefix =
                "/storage/images/";

            if (!parsed.AbsolutePath.StartsWith(
                    suppliedPathPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                error =
                    "Use the direct image link provided by MyImgs.";

                return false;
            }

            var imagePath =
                parsed.AbsolutePath[
                    suppliedPathPrefix.Length..];

            var cdnUriBuilder =
                new UriBuilder(
                    Uri.UriSchemeHttps,
                    "cdn.myimgs.org")
                {
                    Path =
                        $"/images/{imagePath}",

                    Query =
                        parsed.Query.TrimStart(
                            '?')
                };

            parsed =
                cdnUriBuilder.Uri;
        }
        else if (string.Equals(
                     parsed.IdnHost,
                     "cdn.myimgs.org",
                     StringComparison.OrdinalIgnoreCase) &&
                 !parsed.AbsolutePath.StartsWith(
                     "/images/",
                     StringComparison.OrdinalIgnoreCase))
        {
            error =
                "Use a direct MyImgs CDN image link.";

            return false;
        }

        if (string.Equals(
                parsed.IdnHost,
                "cdn.imgpile.com",
                StringComparison.OrdinalIgnoreCase) &&
            !parsed.AbsolutePath.StartsWith(
                "/f/",
                StringComparison.OrdinalIgnoreCase))
        {
            error =
                "Use the direct image link provided by ImgPile.";

            return false;
        }

        uri =
            parsed;

        return true;
    }

    /// <summary>
    /// Returns false for loopback, private, link-local, multicast and other
    /// addresses that a shared watch-party URL must never contact.
    /// </summary>
    internal static bool IsPublicAddress(
        IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (b.Length == 16)
            // Only globally routable unicast; also exclude documentation and transition ranges.
            return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 1 && b[2] == 0x0d && b[3] == 0xb8) &&
                !(b[0] == 0x20 && b[1] == 2) && !(b[0] == 0x20 && b[1] == 1 && b[2] == 0 && b[3] == 0);
        return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
            !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) &&
            !(b[0] == 192 && (b[1] == 168 || b[1] == 0 || b[1] == 2)) &&
            !(b[0] == 100 && b[1] >= 64 && b[1] <= 127) &&
            !(b[0] == 198 && b[1] is 18 or 19 || b[0] == 198 && b[1] == 51 && b[2] == 100) &&
            !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }
}
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace AlphaChannel.Plugin.Core;

internal sealed record ChatLink(string Url, string? YouTubeId);
internal sealed record ChatLinkMetadata(string Title, string Site, string Description, string? ImageUrl);

internal static class ChatLinkPreview
{
    private static readonly Regex Links = new(@"(?i)\b(?:https?://|www\.)[^\s<>]+", RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    internal static ChatLink[] Extract(string text) => Links.Matches(text)
        .Select(m => m.Value.TrimEnd('.', ',', '!', '?', ';', ':', ')', ']'))
        .Select(s => s.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + s : s)
        .Where(s => TryPublicUrl(s, out _)).Distinct(StringComparer.Ordinal)
        .Take(3).Select(s => new ChatLink(new Uri(s).AbsoluteUri, YouTubeId(new Uri(s)))).ToArray();

    // Inspect the path separately so signed CDN URLs and fragments still work.
    internal static bool IsDirectImage(string url)
    {
        if (!TryPublicUrl(url, out var uri)) return false;
        var extension = Path.GetExtension(Uri.UnescapeDataString(uri!.AbsolutePath));
        return extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp";
    }

    internal static string? YouTubeId(Uri uri)
    {
        var host = uri.IdnHost.ToLowerInvariant();
        string? id = null;
        if (host is "youtu.be" or "www.youtu.be") id = uri.AbsolutePath.Trim('/').Split('/')[0];
        else if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com" or
                 "youtube-nocookie.com" or "www.youtube-nocookie.com")
        {
            var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (path.Length >= 2 && path[0] is "shorts" or "embed" or "live") id = path[1];
            else if (uri.AbsolutePath == "/watch")
                id = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
                    .FirstOrDefault(p => p.Length == 2 && p[0] == "v")?.ElementAtOrDefault(1);
        }
        return id is { Length: 11 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? id : null;
    }

    internal static bool TryPublicUrl(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https") ||
            !parsed.IsDefaultPort || parsed.UserInfo.Length > 0 || parsed.HostNameType != UriHostNameType.Dns ||
            !parsed.IdnHost.Contains('.') || parsed.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            parsed.IdnHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return false;
        uri = parsed;
        return true;
    }

    internal static bool IsPublicAddress(IPAddress address)
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

    internal static ChatLinkMetadata Parse(string html, Uri page)
    {
        using var document = new HtmlParser().ParseDocument(html);
        var tags = document.QuerySelectorAll("meta");
        string Meta(params string[] names)
        {
            foreach (var name in names)
                foreach (var tag in tags)
                    if (string.Equals(tag.GetAttribute("property") ?? tag.GetAttribute("name"), name, StringComparison.OrdinalIgnoreCase) &&
                        tag.GetAttribute("content") is { } content && !string.IsNullOrWhiteSpace(content)) return content;
            return "";
        }
        var title = Clean(Meta("og:title", "twitter:title"), 240);
        if (title.Length == 0) title = Clean(document.Title, 240);
        if (title.Length == 0) title = page.IdnHost;
        var image = Meta("og:image", "twitter:image");
        string? imageUrl = Uri.TryCreate(page, image, out var imageUri) && image.Length > 0 &&
            TryPublicUrl(imageUri.AbsoluteUri, out _) ? imageUri.AbsoluteUri : null;
        var site = Clean(Meta("og:site_name"), 80);
        return new(title, site.Length == 0 ? page.IdnHost : site,
            Clean(Meta("og:description", "twitter:description", "description"), 360), imageUrl);
    }

    private static string Clean(string? text, int limit)
    {
        var result = string.Join(" ", (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        result = new string(result.Where(c => !char.IsControl(c)).ToArray());
        return result.Length <= limit ? result : result[..limit] + "…";
    }
}

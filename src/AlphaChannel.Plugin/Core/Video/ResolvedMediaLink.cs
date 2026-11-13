using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AlphaChannel.Plugin.Video;

// Share a compact choice on the original page, rather than an expiring CDN URL
// or browser credentials. Every player refreshes the chosen stream and headers.
internal static class ResolvedMediaLink
{
    private static readonly ConcurrentDictionary<string, (DateTime Created, ResolvedMediaRequest Media)> Recent = new();
    internal const string Marker = "#alphachannel-media-v1=";
    internal sealed record Selection(string Page, string UrlHash, string PathHash, bool Original);
    private sealed record Context(string UrlHash, string PathHash, bool Original, string Fragment);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
    private static string Path(string url)
    {
        var uri = new Uri(url);
        var volatileKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "expire", "expires", "signature", "sig", "policy", "key-pair-id", "hdnts", "hdnea" };
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !volatileKeys.Contains(Uri.UnescapeDataString(part.Split('=')[0])))
            .OrderBy(part => part, StringComparer.Ordinal);
        return uri.GetLeftPart(UriPartial.Path) + "?" + string.Join("&", query);
    }

    internal static string Encode(string page, ResolvedMediaRequest media)
    {
        var context = new Context(Hash(media.Url!), Hash(Path(media.Url!)), media.Url == page, new Uri(page).Fragment);
        var link = page.Split('#')[0] + Marker + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(context));
        Recent[link] = (DateTime.UtcNow, media);
        foreach (var old in Recent.OrderBy(p => p.Value.Created).Take(Math.Max(0, Recent.Count - 32))) Recent.TryRemove(old.Key, out _);
        return link;
    }

    internal static ResolvedMediaRequest? TakeRecent(string link) =>
        Recent.TryRemove(link, out var recent) && DateTime.UtcNow - recent.Created < TimeSpan.FromMinutes(1) ? recent.Media : null;

    internal static bool TryDecode(string link, out Selection selection)
    {
        selection = null!;
        var index = link.IndexOf(Marker, StringComparison.Ordinal);
        if (index < 0 || link.Length - index > 8192) return false;
        try
        {
            if (!Uri.TryCreate(link[..index], UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
            var context = JsonSerializer.Deserialize<Context>(Convert.FromBase64String(link[(index + Marker.Length)..]));
            if (context is null || !ValidHash(context.UrlHash) || !ValidHash(context.PathHash) ||
                context.Fragment is null || context.Fragment.Length > 2048 ||
                context.Fragment.Length > 0 && !context.Fragment.StartsWith('#')) return false;
            selection = new(link[..index] + context.Fragment, context.UrlHash, context.PathHash, context.Original);
            return true;
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException) { return false; }
    }

    private static bool ValidHash(string? hash) => hash is { Length: 24 } && hash.All(Uri.IsHexDigit);

    internal static ResolvedMediaRequest? Match(Selection selection, IReadOnlyList<ResolvedMediaRequest> candidates)
    {
        var exact = candidates.FirstOrDefault(c => c.Url is not null && Hash(c.Url) == selection.UrlHash);
        if (exact is not null) return exact;
        // CDN signatures can expire. Reuse a path only when it identifies one
        // candidate unambiguously; otherwise ask instead of guessing another clip.
        var paths = candidates.Where(c => c.Url is not null && Hash(Path(c.Url)) == selection.PathHash).ToArray();
        return paths.Length == 1 ? paths[0] : null;
    }
}

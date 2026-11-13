using System.Net;
using System.Text.RegularExpressions;
using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

// Only explicit player membership groups formats. Similar names, durations or
// hosts are not enough to conclude that two streams contain the same video.
internal static class WebVideoHtmlMetadata
{
    private static readonly TimeSpan RegexLimit = TimeSpan.FromSeconds(1);
    private static readonly Regex Players = new(@"<video\b(?<attrs>[^>]*)>(?<body>[\s\S]*?)</video\s*>", RegexOptions.IgnoreCase, RegexLimit);
    private static readonly Regex Sources = new(@"<source\b[^>]*>", RegexOptions.IgnoreCase, RegexLimit);
    private static readonly Regex PageTitle = new(@"<title\b[^>]*>(?<title>[^<]*)</title\s*>", RegexOptions.IgnoreCase, RegexLimit);

    internal static Dictionary<string, ResolvedMediaRequest> Parse(string html, Uri page)
    {
        var result = new Dictionary<string, ResolvedMediaRequest>(StringComparer.Ordinal);
        var pageTitle = WebUtility.HtmlDecode(PageTitle.Match(html).Groups["title"].Value).Trim();
        var index = 0;
        foreach (Match player in Players.Matches(html))
        {
            var attrs = player.Groups["attrs"].Value;
            var title = Attribute(attrs, "title") ?? Attribute(attrs, "aria-label");
            var poster = Absolute(Attribute(attrs, "poster"), page);
            var group = page + ":video:" + index++;
            var sourceTags = Sources.Matches(player.Groups["body"].Value).Cast<Match>().Select(m => m.Value).Prepend(attrs);
            foreach (var source in sourceTags)
            {
                var url = Absolute(Attribute(source, "src"), page);
                if (url is null) continue;
                var metadata = new ResolvedMediaRequest(url, null, "HTML player")
                {
                    VideoGroup = group,
                    ThumbnailUrl = poster,
                    Title = string.IsNullOrWhiteSpace(title) ? string.IsNullOrWhiteSpace(pageTitle) ? null : pageTitle : title,
                    TitleIsPage = string.IsNullOrWhiteSpace(title),
                    Format = Attribute(source, "type")
                };
                if (!result.TryAdd(url, metadata) && result[url].VideoGroup != group)
                    result[url] = result[url] with { VideoGroup = null };
            }
        }
        return result;
    }

    private static string? Absolute(string? value, Uri page) => !string.IsNullOrWhiteSpace(value) &&
        Uri.TryCreate(page, WebUtility.HtmlDecode(value.Replace("\\/", "/", StringComparison.Ordinal)), out var uri)
        ? ResolvedMediaRequest.HttpUrl(uri.ToString()) : null;

    private static string? Attribute(string html, string name)
    {
        var match = Regex.Match(html, @"(?<![\w:-])" + name + @"\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))",
            RegexOptions.IgnoreCase, RegexLimit);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["v"].Value).Trim() : null;
    }

    internal static IReadOnlyList<IReadOnlyList<ResolvedMediaRequest>> Group(IReadOnlyList<ResolvedMediaRequest> candidates) =>
        candidates.GroupBy(c => c.VideoGroup is { } group ? "player:" + group : "url:" + c.Url, StringComparer.Ordinal)
            .Select(g => (IReadOnlyList<ResolvedMediaRequest>)g.ToArray()).ToArray();
}

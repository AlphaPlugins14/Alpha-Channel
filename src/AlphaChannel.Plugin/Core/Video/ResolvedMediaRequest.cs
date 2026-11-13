using System.Text.Json;

namespace AlphaChannel.Plugin.Video;

internal sealed record ResolvedMediaRequest(string? Url, string? Error, string? Method = null,
    IReadOnlyDictionary<string, string>? Headers = null)
{
    internal string? Title { get; init; }
    internal bool IsGeneric { get; init; }
    internal string? WebpageUrl { get; init; }
    internal double? DurationSeconds { get; init; }
    internal IReadOnlyList<ResolvedMediaRequest>? Alternatives { get; init; }
    internal string? ThumbnailUrl { get; init; }
    internal int? Width { get; init; }
    internal int? Height { get; init; }
    internal bool? HasVideo { get; init; }
    internal bool? HasAudio { get; init; }
    internal string? Format { get; init; }
    internal string? VideoGroup { get; init; }
    internal bool TitleIsPage { get; init; }

    public static implicit operator ResolvedMediaRequest((string? Url, string? Error, string? Method) value) =>
        new(value.Url, value.Error, value.Method);
    public static implicit operator ResolvedMediaRequest((string? Url, string? Error) value) => new(value.Url, value.Error);

    internal static ResolvedMediaRequest FromYtDlpJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                var videos = entries.EnumerateArray().Select(e => FromYtDlpJson(e.GetRawText())).Where(e => e.Url is not null).ToArray();
                if (videos.Length > 0) return videos[0] with { IsGeneric = true, Alternatives = videos.Skip(1).ToArray() };
            }
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("url", out var value) || value.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
                return new(null, "yt-dlp did not return a playable HTTP stream.");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("http_headers", out var fields) && fields.ValueKind == JsonValueKind.Object)
                foreach (var field in fields.EnumerateObject())
                    if (field.Value.ValueKind == JsonValueKind.String && ValidHeader(field.Name, field.Value.GetString()!))
                        headers[field.Name] = field.Value.GetString()!;
            var extractor = root.TryGetProperty("extractor_key", out var key) && key.ValueKind == JsonValueKind.String ? key.GetString() : null;
            return new(url.ToString(), null, "yt-dlp", headers)
            {
                Title = root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : null,
                WebpageUrl = root.TryGetProperty("webpage_url", out var page) && page.ValueKind == JsonValueKind.String ? page.GetString() : null,
                DurationSeconds = root.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds / 2 ? seconds : null,
                ThumbnailUrl = HttpUrl(String(root, "thumbnail")),
                Width = PositiveInt(root, "width"),
                Height = PositiveInt(root, "height"),
                HasVideo = CodecPresent(root, "vcodec"),
                HasAudio = CodecPresent(root, "acodec"),
                Format = String(root, "ext"),
                TitleIsPage = string.IsNullOrWhiteSpace(extractor) || extractor.Equals("Generic", StringComparison.OrdinalIgnoreCase) || extractor.Equals("HTML5MediaEmbed", StringComparison.OrdinalIgnoreCase),
                IsGeneric = string.IsNullOrWhiteSpace(extractor) || extractor.Equals("Generic", StringComparison.OrdinalIgnoreCase) ||
                    extractor.Equals("HTML5MediaEmbed", StringComparison.OrdinalIgnoreCase)
            };
        }
        catch (JsonException) { return new(null, "yt-dlp returned invalid stream metadata."); }
    }

    private static string? String(JsonElement value, string name) => value.TryGetProperty(name, out var field) &&
        field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static int? PositiveInt(JsonElement value, string name) => value.TryGetProperty(name, out var field) &&
        field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var number) && number is > 0 and <= 32768 ? number : null;
    private static bool? CodecPresent(JsonElement value, string name) => String(value, name) is { Length: > 0 } codec &&
        !codec.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? !codec.Equals("none", StringComparison.OrdinalIgnoreCase) : null;
    internal static string? HttpUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 ? uri.ToString() : null;

    internal static bool ValidHeader(string name, string value) => (name.ToLowerInvariant() is
        "user-agent" or "accept" or "accept-language" or "referer" or "origin" or
        "sec-fetch-mode" or "sec-fetch-site" or "sec-fetch-dest") &&
        value.Length <= 8192 && !value.Any(char.IsControl);

    // Append one field per command: commas/backslashes in values stay literal.
    // Always clear first, including ordinary loads that have no resolver headers.
    internal static bool ApplyHeaders(IReadOnlyDictionary<string, string>? headers, Func<string[], int> command)
    {
        if (command(["change-list", "http-header-fields", "clr", "", null!]) < 0) return false;
        if (headers is null) return true;
        foreach (var header in headers)
            if (ValidHeader(header.Key, header.Value) &&
                command(["change-list", "http-header-fields", "append", header.Key + ": " + header.Value, null!]) < 0)
            {
                command(["change-list", "http-header-fields", "clr", "", null!]);
                return false;
            }
        return true;
    }
}

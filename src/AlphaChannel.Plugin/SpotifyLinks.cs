namespace AlphaChannel.Plugin;

public sealed record SpotifySavedLink
{
    private string uri = "", name = "", artist = "", title = "";

    public SpotifySavedLink(string Uri, string Name, string Artist = "")
    {
        this.Uri = Uri;
        this.Name = Name;
        this.Artist = Artist;
    }

    // Dalamud uses Newtonsoft: missing constructor arguments can arrive as null,
    // despite optional defaults. Explicit nulls also need normalizing on init.
    public string Uri { get => uri; init => uri = value ?? ""; }
    public string Name { get => name; init => name = value ?? ""; }
    public string Artist { get => artist; init => artist = value ?? ""; }
    public string Title { get => title; init => title = value ?? ""; }
    public bool CustomName { get; init; }
}

internal static class SpotifyLinks
{
    internal static bool TryParse(string? input, out string uri)
    {
        uri = "";
        var value = input?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 2048) return false;
        string[] parts;
        if (value.StartsWith("spotify:", StringComparison.Ordinal)) parts = value.Split(':').Skip(1).ToArray();
        else
        {
            if (!System.Uri.TryCreate(value, UriKind.Absolute, out var link) || link.Scheme != "https" ||
                link.Host != "open.spotify.com" || !link.IsDefaultPort || link.UserInfo.Length != 0) return false;
            parts = link.AbsolutePath.Trim('/').Split('/');
            if (parts.Length > 0 && parts[0].StartsWith("intl-", StringComparison.Ordinal)) parts = parts.Skip(1).ToArray();
        }
        if (parts.Length != 2 || parts[0] is not ("track" or "album" or "playlist") || parts[1].Length != 22 ||
            parts[1].Any(c => !char.IsAsciiLetterOrDigit(c))) return false;
        uri = "spotify:" + parts[0] + ":" + parts[1];
        return true;
    }
    internal static string SearchUri(string query) => "spotify:search:" + System.Uri.EscapeDataString(Clean(query, 200));
    internal static string Clean(string? text, int max = 256) => new((text ?? "").Where(c => !char.IsControl(c)).Take(max).ToArray());
    internal static string Kind(string uri) => uri.Split(':').ElementAtOrDefault(1) ?? "item";
}

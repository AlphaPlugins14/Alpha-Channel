using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

internal sealed record ProfileSocial(string Id, string Platform, string Value, string Label, string? Url = null);
internal sealed record ProfileSocials(string Visibility, bool Hidden, ProfileSocial[] Entries, int Version);
internal sealed record SocialPlatform(string Id, string Name, bool Username, int Color);

internal sealed class SocialsClient
{
    internal static readonly SocialPlatform[] Platforms = [
        new("discord","Discord",true,0x8892FF),new("twitch","Twitch",false,0xA970FF),new("steam","Steam",false,0x8CBFE6),
        new("youtube","YouTube",false,0xFF5757),new("bluesky","Bluesky",false,0x58A6FF),new("spotify","Spotify",false,0x1ED760),
        new("telegram","Telegram",false,0x42B5EF),new("playstation","PlayStation Network",true,0x659BFF),new("xbox","Xbox",true,0x70C85A),
        new("tiktok","TikTok",false,0x5DE5E5),new("kick","Kick",false,0x70E34E),new("x","X",false,0xDDDDDD),
        new("instagram","Instagram",false,0xEF78B1),new("facebook","Facebook",false,0x62A0FF),new("threads","Threads",false,0xDDDDDD),
        new("reddit","Reddit",false,0xFF8057),new("mastodon","Mastodon",false,0xA59AFF),new("tumblr","Tumblr",false,0x8EABC5),
        new("soundcloud","SoundCloud",false,0xFF9B57),new("bandcamp","Bandcamp",false,0x69C9D6),new("lastfm","Last.fm",false,0xF06A78),
        new("website","Website / Custom link",false,0x83C7E8)
    ];

    internal async Task<ProfileSocials> GetAsync(string token, string? target, CancellationToken cancel) =>
        await RequestAsync(token, target is null ? "/api/socials/me" : "/api/socials/" + Guid.Parse(target).ToString("D"), null, cancel).ConfigureAwait(false);

    internal async Task<ProfileSocials> SaveAsync(string token, ProfileSocials document, CancellationToken cancel) =>
        await RequestAsync(token,"/api/socials/me",new {document.Visibility,document.Entries,document.Version},cancel).ConfigureAwait(false);

    private static async Task<ProfileSocials> RequestAsync(string token,string path,object? data,CancellationToken cancel)
    {
        using var http=PluginHttpClients.CreateMetadataClient();
        http.BaseAddress=new Uri("https://socials.example.invalid");
        http.Timeout=TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
        using var response=data is null ? await http.GetAsync(path,cancel).ConfigureAwait(false) : await http.PutAsJsonAsync(path,data,cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result=await response.Content.ReadFromJsonAsync<ProfileSocials>(cancellationToken:cancel).ConfigureAwait(false);
        if(result is null || result.Entries is not {Length:<=20} || result.Version<0 ||
            result.Visibility is not ("friends" or "everyone" or "none") || result.Hidden && result.Entries.Length!=0 ||
            result.Entries.Any(e=>e is null || !Guid.TryParse(e.Id,out _) || !Platforms.Any(p=>p.Id==e.Platform) ||
                string.IsNullOrWhiteSpace(e.Value) || e.Value.Length>1024 || e.Label is null || e.Label.Length>80 ||
                e.Url is not null && (!Uri.TryCreate(e.Url,UriKind.Absolute,out var uri) || uri.Scheme!="https" || !string.IsNullOrEmpty(uri.UserInfo))))
            throw new InvalidOperationException("Invalid Socials response.");
        return result;
    }
}

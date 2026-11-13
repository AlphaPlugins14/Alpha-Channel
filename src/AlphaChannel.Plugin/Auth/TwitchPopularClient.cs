using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

internal sealed record TwitchPopularChannel(string Login, string ChannelName, string Url, string Title, string Category,
    string? ThumbnailUrl, bool? IsLive, long? ViewerCount, int PartyViewers, int PartyCount);

internal static class TwitchPopularClient
{
    internal static async Task<TwitchPopularChannel[]> GetAsync(string bearerToken, CancellationToken token)
    {
        using var http = PluginHttpClients.CreateMetadataClient();
        http.BaseAddress = new Uri("https://twitch-directory.example.invalid");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        http.Timeout = TimeSpan.FromSeconds(20);
        using var response = await http.GetAsync("/api/twitch/popular", token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PopularResponse>(cancellationToken: token).ConfigureAwait(false);
        if (result?.Channels is null || result.Channels.Length > 10000 || result.Channels.Any(channel => channel is null ||
            !Video.TwitchChannelChecker.TryNormalizeChannelName(channel.Url, out var login, out _) || login != channel.Login ||
            channel.PartyViewers < 0 || channel.PartyCount < 0 || channel.ViewerCount is < 0 ||
            channel.Title is null || channel.Title.Length > 512 || channel.ChannelName is null || channel.ChannelName.Length > 100))
            throw new InvalidOperationException("Invalid Twitch Popular response.");
        return result.Channels;
    }

    private sealed record PopularResponse(TwitchPopularChannel[] Channels);
}

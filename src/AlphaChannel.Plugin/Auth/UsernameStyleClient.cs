using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

internal sealed record UsernameStyle(string AccountId,string? NameColourHex,string NameEffect,long NameEffectUntil,string? SelectedEffect=null);
internal sealed class UsernameStyleClient
{
    private static HttpClient Client(string token)
    {
        var client=PluginHttpClients.CreateMetadataClient();
        client.BaseAddress=new Uri("https://alpha-chat.example.invalid");
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
        client.Timeout=TimeSpan.FromSeconds(20);
        return client;
    }
    internal async Task<UsernameStyle[]> ReadAsync(string token,string[] ids,CancellationToken ct)
    {
        using var client=Client(token);
        using var response=await client.PostAsJsonAsync("/api/username-styles",new {accountIds=ids},ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StyleResponse>(cancellationToken:ct).ConfigureAwait(false))?.Styles ?? [];
    }
    internal async Task<UsernameStyle?> SaveAsync(string token,string? colour,string effect,CancellationToken ct)
    {
        using var client=Client(token);
        using var response=await client.PutAsJsonAsync("/api/username-style",new {nameColourHex=colour,nameEffect=effect},ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StyleResponse>(cancellationToken:ct).ConfigureAwait(false))?.Styles.SingleOrDefault();
    }
    private sealed record StyleResponse(UsernameStyle[] Styles);
}

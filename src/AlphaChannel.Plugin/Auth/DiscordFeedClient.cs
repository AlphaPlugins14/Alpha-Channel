using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace AlphaChannel.Plugin.Auth;

internal sealed record DiscordFeedResult(bool Ok=false,string? Code=null,string? Error=null,string? Kind=null,
    string? EventId=null);
internal static class DiscordFeedClient
{
    internal static Task<DiscordFeedResult> ActivityStopped(string token, string version, CancellationToken ct)=>
        Send(token,"/api/statistics/activity",new{activity="none",contentType="social",hosting=false,sessionId=(string?)null,version},ct);
    internal static Task<DiscordFeedResult> Rooms(string token)=>Send(token,"/api/statistics/rooms",new{},default);
    private static async Task<DiscordFeedResult> Send(string token,string route,object body,CancellationToken ct)
    {
        using var http=Net.PluginHttpClients.CreateMetadataClient();
        http.BaseAddress=new Uri(CharacterDirectoryClient.WorkerUrl);http.Timeout=TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
        using var response=await http.PostAsJsonAsync(route,body,ct);
        var result=await response.Content.ReadFromJsonAsync<DiscordFeedResult>(cancellationToken:ct);
        return result??new(Error:"feed_unavailable");
    }
    internal static Task<DiscordFeedResult> Resolve(string token,string code,CancellationToken ct)=>
        Send(token,"/api/discord/resolve",new{code},ct);
}

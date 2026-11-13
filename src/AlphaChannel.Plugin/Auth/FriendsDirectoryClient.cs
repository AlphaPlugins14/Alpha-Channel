using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

internal sealed record FriendDirectoryPerson(string AccountId, string DisplayName, string? AvatarIcon,
    string AvatarColorHex, string? AvatarImageUrl, string Introduction, string LookingFor,
    string[] Tags, bool Visible, bool ShowOnline, bool ShowParty, bool Suspended, int Version,
    bool Online = false, string? PublicPartyHostId = null);
internal sealed record FriendDirectoryPage(FriendDirectoryPerson[] People, string? NextCursor);
internal sealed record FriendDirectoryOwn(FriendDirectoryPerson? Listing);
internal sealed class FriendDirectoryDraft
{
    public string Introduction = "";
    public string LookingFor = "Watching together";
    public string[] Tags = [];
    public bool Visible;
    public bool ShowOnline;
    public bool ShowParty;
    public int Version;
}
internal sealed class FriendsDirectoryClient
{
    internal const string WorkerUrl = "https://friends-directory.example.invalid";
    private static async Task<T?> Request<T>(string token, string route, HttpMethod method, object? body, CancellationToken ct)
    {
        using var http = PluginHttpClients.CreateMetadataClient();
        http.BaseAddress = new Uri(WorkerUrl); http.Timeout = TimeSpan.FromSeconds(40);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var request = new HttpRequestMessage(method, route);
        if (body is not null) request.Content = JsonContent.Create(body, options: EventsClient.Json);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        if (!response.IsSuccessStatusCode)
        {
            var error = "";
            try { error = (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(cancellationToken: ct))?.GetValueOrDefault("error") ?? ""; } catch { }
            throw new InvalidOperationException(error switch
            {
                "listing_changed" => "Your listing changed on another device. Reload it before saving.",
                "listing_suspended" => "This listing was hidden by a moderator. You can remove it or save it as hidden.",
                "invalid_listing" => "Check your introduction and tags. Use up to 8 tags, each up to 24 characters.",
                "rate_limited" => "Please wait a moment before trying again.",
                _ => "The friends directory is temporarily unavailable. Please try again."
            });
        }
        return await response.Content.ReadFromJsonAsync<T>(EventsClient.Json, ct).ConfigureAwait(false);
    }
    internal async Task<FriendDirectoryPage> Browse(string token, string query, string[] tags, string looking,
        bool online, string sort, string? cursor, CancellationToken ct)
    {
        var route = "/api/directory?q=" + Uri.EscapeDataString(query) + "&looking=" + Uri.EscapeDataString(looking)
            + "&online=" + online.ToString().ToLowerInvariant()
            + "&sort=" + sort + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
        foreach (var tag in tags) route += "&tag=" + Uri.EscapeDataString(tag);
        return await Request<FriendDirectoryPage>(token, route, HttpMethod.Get, null, ct)
            ?? throw new InvalidOperationException("The friends directory is not available yet.");
    }
    internal async Task<FriendDirectoryOwn> Own(string token, CancellationToken ct) =>
        await Request<FriendDirectoryOwn>(token, "/api/directory/me", HttpMethod.Get, null, ct)
        ?? throw new InvalidOperationException("The friends directory is not available yet.");
    internal async Task<FriendDirectoryOwn> Save(string token, FriendDirectoryDraft draft, CancellationToken ct) =>
        await Request<FriendDirectoryOwn>(token, "/api/directory/me", HttpMethod.Put, draft, ct)
        ?? throw new InvalidOperationException("Your listing could not be saved.");
    internal Task<object?> Remove(string token, int version, CancellationToken ct) =>
        Request<object>(token, "/api/directory/me?version=" + version, HttpMethod.Delete, null, ct);
    internal Task<object?> Moderate(string token, string id, CancellationToken ct) =>
        Request<object>(token, "/api/directory/moderate/" + Guid.Parse(id).ToString("D"), HttpMethod.Delete, null, ct);
    internal Task<AccountProfileDto?> Profile(string token, string id, CancellationToken ct) =>
        Request<AccountProfileDto>(token, "/api/directory/profiles/" + Guid.Parse(id).ToString("D"), HttpMethod.Get, null, ct);
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

internal sealed record CharacterDirectorySettings(string AccountId, bool Hidden, string? CharacterName, string? CharacterWorld);
internal sealed record CharacterDirectoryTarget(string Name, string World);
internal sealed record WorldBeacon(string AccountId, string LocationKey, float X, float Y, float Z,
    long ExpiresAt, RoomDirectoryDto Room);
internal sealed class CharacterDirectoryClient
{
    internal const string WorkerUrl = "https://characters.example.invalid";
    private static HttpClient Client(string token)
    {
        var http = PluginHttpClients.CreateMetadataClient();
        http.BaseAddress = new Uri(WorkerUrl);
        http.Timeout = TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }
    internal async Task<CharacterDirectorySettings> SyncAsync(string token, bool? hidden, CancellationToken ct)
    {
        using var http = Client(token);
        using var response = await http.PutAsJsonAsync("/api/me", hidden.HasValue ? (object)new { hidden = hidden.Value } : new { }, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CharacterDirectorySettings>(cancellationToken:ct) ?? throw new InvalidDataException();
    }
    internal async Task<AccountProfileDto?> GetAsync(string token, string id, CancellationToken ct)
        => await GetProfileRouteAsync(token,id,ct,"/api/accounts/");

    internal async Task<AccountProfileDto?> GetPublicProfileAsync(string token, string id, CancellationToken ct)
        => await GetProfileRouteAsync(token,id,ct,"/api/profiles/");

    private async Task<AccountProfileDto?> GetProfileRouteAsync(string token,string id,CancellationToken ct,string route)
    {
        using var http = Client(token);
        using var response = await http.GetAsync(route + Guid.Parse(id).ToString("D"), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AccountProfileDto>(cancellationToken:ct);
    }
    internal async Task<AccountProfileDto[]> LookupAsync(string token, CharacterDirectoryTarget[] targets, CancellationToken ct)
    {
        using var http = Client(token);
        using var response = await http.PostAsJsonAsync("/api/lookup", new { characters = targets }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LookupResponse>(cancellationToken:ct))?.Accounts ?? [];
    }
    private sealed record LookupResponse(AccountProfileDto[] Accounts);
    internal async Task PublishBeaconAsync(string token, string locationKey, System.Numerics.Vector3 position, CancellationToken ct)
    {
        using var http = Client(token);
        using var response = await http.PutAsJsonAsync("/api/beacons/me",
            new { locationKey, x = position.X, y = position.Y, z = position.Z }, ct);
        response.EnsureSuccessStatusCode();
    }
    internal async Task DeleteBeaconAsync(string token, CancellationToken ct)
    {
        using var http = Client(token);
        using var response = await http.DeleteAsync("/api/beacons/me", ct);
        response.EnsureSuccessStatusCode();
    }
    internal async Task<WorldBeacon[]> GetBeaconsAsync(string token, string location, CancellationToken ct, string? host = null)
    {
        using var http = Client(token);
        var route = "/api/beacons?location=" + Uri.EscapeDataString(location);
        if (host is not null) route += "&host=" + Uri.EscapeDataString(host);
        using var response = await http.GetAsync(route, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BeaconResponse>(cancellationToken:ct))?.Beacons ?? [];
    }
    private sealed record BeaconResponse(WorldBeacon[] Beacons);
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.BroadcastAllowance;

internal sealed class BroadcastLedgerClient : IBroadcastLedgerClient
{
    private const string WorkerUrl =
        "https://patreon.example.invalid";

    private static HttpClient Client(string token)
    {
        var client = PluginHttpClients.CreateMetadataClient();
        client.BaseAddress = new Uri(WorkerUrl);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task<BroadcastLedgerStatus> StartAsync(string token)
    {
        using var client = Client(token);
        using var response = await client.PostAsJsonAsync(
            "/api/broadcast/start", new { hasViewer = true }).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadStatus(response).ConfigureAwait(false);
    }

    public async Task<BroadcastLedgerStatus> RenewAsync(string token, string sessionId)
    {
        using var client = Client(token);
        using var response = await client.PostAsJsonAsync(
            "/api/broadcast/renew", new { sessionId, hasViewer = true }).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadStatus(response).ConfigureAwait(false);
    }

    public async Task StopAsync(string token, string sessionId)
    {
        using var client = Client(token);
        using var response = await client.PostAsJsonAsync(
            "/api/broadcast/stop", new { sessionId }).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<BroadcastLedgerStatus> StatusAsync(string token, string hostId)
    {
        using var client = Client(token);
        using var response = await client.GetAsync(
            "/api/broadcast/status?hostAccountId=" + Uri.EscapeDataString(hostId))
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadStatus(response).ConfigureAwait(false);
    }

    private static async Task<BroadcastLedgerStatus> ReadStatus(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<BroadcastLedgerStatus>()
            .ConfigureAwait(false)
        ?? throw new InvalidOperationException("Worker returned an empty broadcast status.");
}

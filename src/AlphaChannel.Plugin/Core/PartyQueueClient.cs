using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AlphaChannel.Plugin.Core;

internal sealed record PartyQueueAction(string Kind, Guid PartyId, Guid ActionId, PartyQueueItem? Item,
    Guid? ItemId, string SenderId, string SenderName);
internal sealed record PartyQueueReply(PartyQueueSnapshot? Snapshot, PartyQueueAction[]? Actions,
    PartyQueueNotice[]? Notices = null, PartyQueueSnapshot? RecoverySnapshot = null);
internal sealed record PartyQueueNetworkResult(string HostId, bool Hosting, PartyQueueReply? Reply,
    Guid[] Acknowledged, string? Error, int Generation, bool Push = false);

internal sealed class PartyQueueClient : IDisposable
{
    internal const string Origin = "https://party-queue.example.invalid";
    private readonly HttpClient http;
    internal PartyQueueClient(HttpMessageHandler? handler = null)
    {
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri(Origin);
        http.Timeout = TimeSpan.FromSeconds(12);
    }
    private readonly SemaphoreSlim serial = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private WorkerPushConnection? subscription;
    private string? subscriptionKey;
    internal bool SubscriptionConnected => subscription?.Connected == true;

    internal void Subscribe(string? token, string? host, Action<PartyQueueReply>? received = null, Action? disconnected = null)
    {
        var key = token is null || host is null ? null : token + "\n" + host;
        if (key == subscriptionKey) return;
        subscription?.Dispose(); subscription = null; subscriptionKey = key;
        if (key is null) return;
        subscription = new WorkerPushConnection(Origin.Replace("https:", "wss:") + "/rooms/" + host!.ToLowerInvariant() + "/socket", token!, message => {
            var reply = JsonSerializer.Deserialize<PartyQueueReply>(message, Json);
            if (reply is not null) received?.Invoke(reply);
        }, disconnected);
    }

    internal async Task<PartyQueueReply?> SyncAsync(string token, string host, PartyQueueSnapshot? snapshot,
        string[] members, Guid[] acknowledged, CancellationToken cancellationToken, bool discover = false)
    {
        var body = snapshot is null ? null : new { Snapshot = snapshot, Members = members, AckActionIds = acknowledged, NewParty = false, Discover = discover };
        using var response = await SendAsync(token, host, "", body is null ? HttpMethod.Get : HttpMethod.Post, body, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PartyQueueReply>(Json, cancellationToken);
    }
    internal async Task CommandAsync(string token, string host, PartyQueueCommand command, CancellationToken cancellationToken)
    { using var response = await SendAsync(token, host, "/actions", HttpMethod.Post, command, cancellationToken); }
    internal async Task TransferAsync(string token, string host, string target, Guid ticket, CancellationToken cancellationToken)
    { using var response = await SendAsync(token, host, "/transfer", HttpMethod.Post, new { TargetId = target, Ticket = ticket }, cancellationToken); }
    internal async Task CancelTransferAsync(string token, string host, string target, Guid ticket)
    { using var response = await SendAsync(token, host, "/transfer/cancel", HttpMethod.Post, new { TargetId = target, Ticket = ticket }, CancellationToken.None); }
    internal async Task EndAsync(string token, string host)
    { using var response = await SendAsync(token, host, "", HttpMethod.Delete, null, CancellationToken.None); }
    private async Task<HttpResponseMessage> SendAsync(string token, string host, string suffix, HttpMethod method,
        object? body, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(host, out _)) throw new InvalidOperationException("Waiting for the party host.");
        await serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(method, $"/rooms/{host.ToLowerInvariant()}{suffix}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) request.Content = JsonContent.Create(body, options: new JsonSerializerOptions { PropertyNamingPolicy = null });
            var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                string? code = null;
                try
                {
                    using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    var buffer = new byte[4097];var length = 0;
                    while (length < buffer.Length)
                    {
                        var read = await content.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                        if (read == 0) break;
                        length += read;
                    }
                    if (length <= 4096)
                    {
                        using var document = JsonDocument.Parse(buffer.AsMemory(0,length));
                        if (document.RootElement.TryGetProperty("code",out var value) && value.ValueKind == JsonValueKind.String)
                            code = value.GetString();
                    }
                }
                catch (OperationCanceledException) { response.Dispose();throw; }
                catch { /* Fall back to the status-specific safe message. */ }
                response.Dispose();
                throw new PartyQueueRequestException(status switch {
                    401 => "Sign in to use the party queue.", 403 => "Waiting for the host to confirm party membership.",
                    404 => "Waiting for the host to share the queue.",
                    409 => code switch {
                        "target_not_ready" => "The selected viewer's queue connection is not ready. Ask them to reopen the Watch Party and try again.",
                        "target_queue_active" => "The selected viewer still has an active queue connection. Ask them to rejoin the party and try again.",
                        "transfer_busy" => "Another host transfer is still being prepared. Try again shortly.",
                        _ => "The party queue changed during handover. Please try again."
                    },
                    429 => "Queue requests are busy. Try again shortly.",
                    _ => "Queue sync unavailable. Playback continues; retrying�" });
            }
            return response;
        }
        finally { serial.Release(); }
    }
    public void Dispose() { Subscribe(null, null); http.Dispose(); }
}

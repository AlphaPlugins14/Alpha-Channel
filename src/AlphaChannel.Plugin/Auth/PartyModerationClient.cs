using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

internal sealed record PartyAdmission(bool Allowed, long Revision);
internal sealed record PartyBan(string AccountId, string Name, long BannedAt);
internal sealed record PartyModerationIdentity(string Host, string Account, string Token, long Revision, string Party = "");

internal sealed class PartyModerationClient : IDisposable
{
    internal const string Origin = "https://party-moderation.example.invalid";
    private readonly object gate = new();
    private CancellationTokenSource? watchCancellation;
    private PartyModerationIdentity? watching;
    private bool disposed;
    private Task? watchTask;

    private static HttpClient Http(string token)
    {
        var http = PluginHttpClients.CreateMetadataClient();
        http.Timeout = TimeSpan.FromSeconds(12);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }
    private static string Route(string host, string action)
    {
        if (!Guid.TryParse(host, out var id)) throw new ArgumentException("Invalid host.");
        return Origin + "/hosts/" + id.ToString("D") + "/" + action;
    }
    internal static async Task<PartyAdmission> Admit(string token, string host, CancellationToken ct)
    {
        using var http = Http(token);
        var result = await http.GetFromJsonAsync<PartyAdmission>(Route(host, "admission"), ct).ConfigureAwait(false);
        return result is { Revision: >= 0 } ? result : throw new InvalidDataException("Invalid admission response.");
    }
    internal static async Task<PartyBan[]> Bans(string token, string host, CancellationToken ct)
    {
        using var http = Http(token);
        var result = await http.GetFromJsonAsync<BanList>(Route(host, "bans"), ct).ConfigureAwait(false);
        return result?.Bans is { Length: <= 1000 } bans && bans.All(b => Guid.TryParse(b.AccountId, out _) && b.Name is { Length: <= 96 })
            ? bans : throw new InvalidDataException("Invalid ban list.");
    }
    private sealed record BanList(PartyBan[] Bans);
    internal static async Task Moderate(string token, string host, string target, string name, string action, CancellationToken ct, string? party = null)
    {
        if (action is not ("kick" or "ban" or "unban") || !Guid.TryParse(target, out _)) throw new ArgumentException("Invalid moderation action.");
        using var http = Http(token);
        using var response = await http.PostAsJsonAsync(Route(host, action), new { accountId = target, name, partyId = party }, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
    internal void Watch(PartyModerationIdentity? identity, Func<PartyModerationIdentity, string, Task> removed)
    {
        lock (gate)
        {
            if (disposed || ReferenceEquals(identity, watching)) return;
            watchCancellation?.Cancel();
            watching = identity;
            if (identity is null) return;
            var cancellation = new CancellationTokenSource();
            watchCancellation = cancellation;
            watchTask = Task.Run(async () =>
            {
                try { await WatchLoop(identity, removed, cancellation.Token).ConfigureAwait(false); }
                finally { lock (gate) { if (ReferenceEquals(watchCancellation, cancellation)) watchCancellation = null; cancellation.Dispose(); } }
            });
        }
    }
    private static async Task WatchLoop(PartyModerationIdentity identity,
        Func<PartyModerationIdentity, string, Task> removed, CancellationToken ct)
    {
        var revision = identity.Revision;
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("Authorization", "Bearer " + identity.Token);
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connect.CancelAfter(TimeSpan.FromSeconds(12));
                    await socket.ConnectAsync(new Uri(Route(identity.Host, "watch").Replace("https:", "wss:") + "?after=" + revision + "&party=" + Uri.EscapeDataString(identity.Party)), connect.Token).ConfigureAwait(false);
                }
                var connectedAt = Environment.TickCount64;
                var bytes = new byte[4096];
                while (!ct.IsCancellationRequested)
                {
                    var length = 0;
                    WebSocketReceiveResult message;
                    do {
                        if (length == bytes.Length) throw new InvalidDataException();
                        message = await socket.ReceiveAsync(new ArraySegment<byte>(bytes, length, bytes.Length - length), ct).ConfigureAwait(false);
                        if (message.MessageType != WebSocketMessageType.Text) throw new IOException("Moderation connection ended.");
                        length += message.Count;
                    } while (!message.EndOfMessage);
                    using var doc = JsonDocument.Parse(bytes.AsMemory(0, length));
                    var type = doc.RootElement.GetProperty("type").GetString();
                    var next = doc.RootElement.GetProperty("revision").GetInt64();
                    if (next < revision || type is not ("ready" or "kick" or "ban")) throw new InvalidDataException();
                    revision = next;
                    if (type is "kick" or "ban") { ct.ThrowIfCancellationRequested(); await removed(identity, type).ConfigureAwait(false); return; }
                    if (Environment.TickCount64 - connectedAt > 60000) failures = 0;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { /* An outage never stops an existing viewer. Reconnect with the last revision. */ }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures++, 5)))) +
                TimeSpan.FromMilliseconds(Random.Shared.Next(250, 1000)), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; watchCancellation?.Cancel(); watching = null; }
    }
}

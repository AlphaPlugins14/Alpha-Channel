using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AlphaChannel.Plugin;

// This protocol is deliberately independent from every existing arcade relay.
internal sealed class BlastPartyClient : IDisposable
{
    internal const string BaseUrl = "https://blast-party.example.invalid";
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentQueue<string> actions = new();
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string roomId, token;
    private string? key, latest;
    private string status = "Connecting to Blast Party...";
    private volatile bool connected;
    private volatile bool terminal;
    private int disposed, sequence;
    private long lastReceived;
    private string members = "[]";
    private string[] memberIds;
    internal string? Key => Volatile.Read(ref key);
    internal bool Connected => connected;
    internal string Status => Volatile.Read(ref status);
    internal Task Completion { get; }
    internal bool IsHost { get; }
    internal BlastPartyClient(string roomId, string token, string? key, string[] members)
    {
        this.roomId = roomId; this.token = token; this.key = key;
        IsHost = key is null;
        memberIds = members;
        this.members = JsonSerializer.Serialize(members);
        Completion = Task.Run(RunAsync);
    }
    internal void SetMembers(string[] ids)
    {
        if (memberIds.AsSpan().SequenceEqual(ids)) return;
        memberIds = ids;
        Volatile.Write(ref members, JsonSerializer.Serialize(ids.Take(64)));
    }
    internal string? TakeSnapshot() => Interlocked.Exchange(ref latest, null);
    internal bool Send(object action)
    {
        if (!connected || disposed != 0 || actions.Count >= 16) return false;
        actions.Enqueue(JsonSerializer.Serialize(action)); return true;
    }
    private async Task SendAsync(ClientWebSocket ws, string body, CancellationToken ct)
    {
        // Sequence belongs to this connection; old actions are never replayed after reconnect.
        var json = body[..^1] + ",\"seq\":" + (++sequence).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
    }
    private async Task RunAsync()
    {
        var ct = lifetime.Token;
        try
        {
            if (IsHost)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/rooms");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent("{\"roomId\":\"" + roomId + "\",\"members\":" + members + "}", Encoding.UTF8, "application/json");
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) { status = $"Could not create Blast Party ({(int)response.StatusCode}). End the game and try again."; return; }
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var value = body.RootElement.GetProperty("key").GetString();
                if (!ArcadeRelayClient.Hex(value, 64)) { status = "Invalid Blast Party invitation."; return; }
                Volatile.Write(ref key, value);
            }
            var failures = 0;
            while (!ct.IsCancellationRequested)
            {
                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
                ws.Options.SetRequestHeader("X-Blast-Key", key);
                try
                {
                    using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    { handshake.CancelAfter(TimeSpan.FromSeconds(12)); await ws.ConnectAsync(new Uri(BaseUrl.Replace("https://", "wss://") + "/rooms/" + roomId + "/socket"), handshake.Token); }
                    sequence = 0; actions.Clear(); connected = true; status = "Connected"; failures = 0;
                    Interlocked.Exchange(ref lastReceived, Environment.TickCount64);
                    using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var receive = ReceiveAsync(ws, connection.Token);
                    var send = PumpAsync(ws, connection.Token);
                    await Task.WhenAny(receive, send);
                    connection.Cancel(); ws.Abort();
                    try { await Task.WhenAll(receive, send); } catch (Exception) when (connection.IsCancellationRequested) { }
                    if (terminal) return;
                    if (ws.CloseStatus is { } close && (int)close is 4000 or 4001 or 4003)
                    { status = "Blast Party ended, or you have left this party."; return; }
                }
                catch (Exception) when (!ct.IsCancellationRequested) { status = "Reconnecting to Blast Party..."; }
                finally { connected = false; actions.Clear(); }
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, ++failures * 2)), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { status = "Blast Party is unavailable. End the game and try again."; }
        finally { connected = false; http.Dispose(); }
    }
    private async Task PumpAsync(ClientWebSocket ws, CancellationToken ct)
    {
        string? sentMembers = null; var ping = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            if (Environment.TickCount64 - Interlocked.Read(ref lastReceived) > 45000) throw new TimeoutException("Blast Party connection timed out");
            var current = Volatile.Read(ref members);
            if (IsHost && sentMembers != current)
            { await SendAsync(ws, "{\"type\":\"members\",\"members\":" + current + "}", ct); sentMembers = current; }
            while (actions.TryDequeue(out var action)) await SendAsync(ws, action, ct);
            if ((DateTime.UtcNow - ping).TotalSeconds >= 20)
            { await ws.SendAsync(Encoding.UTF8.GetBytes("ping"), WebSocketMessageType.Text, true, ct); ping = DateTime.UtcNow; }
            await Task.Delay(30, ct);
        }
    }
    private async Task ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[131072];
        while (!ct.IsCancellationRequested)
        {
            var count = 0; WebSocketReceiveResult result;
            do
            {
                if (count == buffer.Length) throw new InvalidDataException("Blast Party frame too large");
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Invalid frame");
                count += result.Count;
            } while (!result.EndOfMessage);
            var text = Encoding.UTF8.GetString(buffer, 0, count);
            Interlocked.Exchange(ref lastReceived, Environment.TickCount64);
            if (text == "pong") continue;
            using var body = JsonDocument.Parse(text);
            if (body.RootElement.TryGetProperty("type", out var type) && type.GetString() == "revoked")
            { status = "You have left this Watch Party."; terminal = true; return; }
            Interlocked.Exchange(ref latest, text);
            if (body.RootElement.TryGetProperty("closed", out var closed) && closed.ValueKind == JsonValueKind.True)
            { status = "Blast Party ended."; terminal = true; return; }
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); actions.Clear(); latest = null;
        _ = Completion.ContinueWith(_ => lifetime.Dispose(), TaskScheduler.Default);
    }
}

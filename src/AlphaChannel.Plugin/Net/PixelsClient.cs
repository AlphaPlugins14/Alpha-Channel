using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;

namespace AlphaChannel.Plugin;

internal sealed class PixelsClient : IDisposable
{
    internal const string Origin = "https://pixels.example.invalid";
    private readonly CancellationTokenSource cancellation = new();
    private readonly SemaphoreSlim sendGate = new(1);
    private readonly string token;
    private ClientWebSocket? socket;
    private int disposed;
    internal readonly ConcurrentQueue<JsonElement> Incoming = new();
    internal bool Connected => socket?.State == WebSocketState.Open;
    internal PixelsClient(string token) { this.token = token; _ = Run(); }
    private void Enqueue(object value) { if (Incoming.Count > 256) { socket?.Abort(); return; } Incoming.Enqueue(JsonSerializer.SerializeToElement(value)); }
    private async Task Run()
    {
        var delay = 1000;
        while (!cancellation.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket(); socket = ws;
            try
            {
                ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
                ws.Options.CollectHttpResponseDetails = true;
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(25);
                ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await ws.ConnectAsync(new Uri(Origin.Replace("https:", "wss:") + "/connect"), timeout.Token);
                }
                delay = 1000;
                Enqueue(new { type = "connection", connected = true });
                var buffer = new byte[16384];
                while (ws.State == WebSocketState.Open && !cancellation.IsCancellationRequested)
                {
                    using var payload = new MemoryStream(); WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(buffer, cancellation.Token);
                        if (result.MessageType == WebSocketMessageType.Close) throw new IOException("Disconnected");
                        if (result.MessageType != WebSocketMessageType.Text || payload.Length + result.Count > 4 * 1024 * 1024) throw new IOException("Invalid board message");
                        payload.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    using var document = JsonDocument.Parse(payload.ToArray());
                    if (Incoming.Count > 256) throw new IOException("Updates exceeded queue");
                    Incoming.Enqueue(document.RootElement.Clone());
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
            catch
            {
                Enqueue(new { type = "connection", connected = false });
                if (ws.HttpStatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                {
                    Enqueue(new { type = "error", message = "Sign in again to reconnect to Pixels." });
                    break; // Invalid credentials do not generate repeated Worker requests.
                }
            }
            if (!cancellation.IsCancellationRequested) try { await Task.Delay(delay, cancellation.Token); } catch (OperationCanceledException) { break; }
            delay = Math.Min(30000, delay * 2);
        }
    }
    internal async Task Place(int index, int colour)
    {
        try
        {
            await sendGate.WaitAsync(cancellation.Token);
            try
            {
                var ws = socket;
                if (ws?.State != WebSocketState.Open) throw new IOException();
                await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "place", index, color = colour })), WebSocketMessageType.Text, true, cancellation.Token);
            }
            finally { sendGate.Release(); }
        }
        catch { Enqueue(new { type = "error", message = "Connection interrupted. Wait for the board to reconnect." }); socket?.Abort(); }
    }
    internal void Reconnect() => socket?.Abort();
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) != 0) return; cancellation.Cancel(); socket?.Abort(); }
    internal static async Task<JsonElement> Admin(string token, object? action = null)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(action is null ? HttpMethod.Get : HttpMethod.Post, Origin + "/admin/pixels");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (action is not null) request.Content = new StringContent(JsonSerializer.Serialize(action), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Pixels admin request failed ({(int)response.StatusCode}).");
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return data.RootElement.Clone();
    }
}

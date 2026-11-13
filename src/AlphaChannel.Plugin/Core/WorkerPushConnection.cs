using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace AlphaChannel.Plugin.Core;

// One connection per owner. A bounded outbox coalesces activity updates while
// offline; it must never be used for commands that require acknowledgement.
internal sealed class WorkerPushConnection : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<string> outbox = Channel.CreateBounded<string>(new BoundedChannelOptions(1) {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private string? latest;
    private readonly object publishGate = new();
    private int connected, disposed;
    internal bool Connected => Volatile.Read(ref connected) != 0;
    internal Task Completion { get; }

    internal WorkerPushConnection(string url, string token, Action<string> received, Action? disconnected = null, Task? beforeConnecting = null)
    {
        Completion = Task.Run(() => RunAsync(new Uri(url), token, received, disconnected, beforeConnecting));
    }
    internal void Publish(string message)
    {
        lock (publishGate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            latest = message;
            outbox.Writer.TryWrite(message);
        }
    }
    private async Task RunAsync(Uri uri, string token, Action<string> received, Action? disconnected, Task? beforeConnecting)
    {
        var failures = 0;
        var ct = lifetime.Token;
        try
        {
            if (beforeConnecting is not null) await beforeConnecting.WaitAsync(ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                var openedAt = Environment.TickCount64;
                try
                {
                    using var socket = new ClientWebSocket();
                    socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
                    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(45);
                    socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
                    using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    // Bound authenticated connection lifetime and revalidate at reconnect.
                    connection.CancelAfter(TimeSpan.FromHours(1));
                    using (var opening = CancellationTokenSource.CreateLinkedTokenSource(connection.Token))
                    {
                        opening.CancelAfter(TimeSpan.FromSeconds(15));
                        await socket.ConnectAsync(uri, opening.Token).ConfigureAwait(false);
                    }
                    openedAt = Environment.TickCount64;
                    Volatile.Write(ref connected, 1);
                    // Replay and publication must be atomic: an older replay
                    // must never evict a newer offline update from the outbox.
                    lock (publishGate)
                        if (latest is { } value) outbox.Writer.TryWrite(value);
                    var reader = ReceiveAsync(socket, received, connection.Token);
                    var writer = SendAsync(socket, connection.Token);
                    await Task.WhenAny(reader, writer).ConfigureAwait(false);
                    connection.Cancel();
                    try { await Task.WhenAll(reader, writer).ConfigureAwait(false); }
                    catch { /* Both operations are observed before disposing the socket. */ }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch { /* Transient failures reconnect without interrupting playback. */ }
                finally
                {
                    Volatile.Write(ref connected, 0);
                    if (!ct.IsCancellationRequested) disconnected?.Invoke();
                }
                if (Environment.TickCount64 - openedAt >= 60000) failures = 0;
                var delay = Math.Min(120, Math.Pow(2, Math.Min(failures++, 7)));
                await Task.Delay(TimeSpan.FromSeconds(delay) + TimeSpan.FromMilliseconds(Random.Shared.Next(250, 1250)), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { Volatile.Write(ref connected, 0); lifetime.Dispose(); }
    }
    private static async Task ReceiveAsync(ClientWebSocket socket, Action<string> received, CancellationToken ct)
    {
        var bytes = new byte[512 * 1024];
        while (!ct.IsCancellationRequested)
        {
            var length = 0;
            WebSocketReceiveResult part;
            do
            {
                if (length == bytes.Length) throw new InvalidDataException("Worker update too large.");
                part = await socket.ReceiveAsync(new ArraySegment<byte>(bytes, length, bytes.Length - length), ct).ConfigureAwait(false);
                if (part.MessageType != WebSocketMessageType.Text) throw new IOException("Worker connection ended.");
                length += part.Count;
            } while (!part.EndOfMessage);
            ct.ThrowIfCancellationRequested();
            received(Encoding.UTF8.GetString(bytes, 0, length));
        }
    }
    private async Task SendAsync(ClientWebSocket socket, CancellationToken ct)
    {
        await foreach (var message in outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            await socket.SendAsync(Encoding.UTF8.GetBytes(message).AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        outbox.Writer.TryComplete();
    }
}

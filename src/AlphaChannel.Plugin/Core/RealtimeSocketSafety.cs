using System.Net.WebSockets;
using System.Text.Json;

namespace AlphaChannel.Plugin;

internal sealed class RealtimeMessageTooLargeException(int limit)
    : IOException($"Realtime message exceeds the {limit:N0}-byte limit.");

internal static class RealtimeSocketSafety
{
    // Control messages carry JSON/base64 activity snapshots, never video frames.
    // Leave ample room for dense wheels, quizzes and party rosters.
    internal const int MaxMessageBytes = 1024 * 1024;
    internal static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    internal static byte[] Serialize<T>(T message)
    {
        using var stream = new LimitedMessageStream();
        JsonSerializer.Serialize(stream, message);
        return stream.ToArray();
    }

    internal static void AppendFragment(MemoryStream stream, ReadOnlySpan<byte> fragment)
    {
        CheckLength(stream.Length, fragment.Length);
        stream.Write(fragment);
    }

    private static void CheckLength(long length, int additional)
    {
        if (length > MaxMessageBytes - additional)
            throw new RealtimeMessageTooLargeException(MaxMessageBytes);
    }

    private sealed class LimitedMessageStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckLength(Length, count);
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckLength(Length, buffer.Length);
            base.Write(buffer);
        }
        public override void WriteByte(byte value)
        {
            CheckLength(Length, 1);
            base.WriteByte(value);
        }
    }

    internal static TimeSpan ReconnectDelay(int attempt, double jitter)
    {
        var seconds = Math.Min(30d, 3d * Math.Pow(2d, Math.Clamp(attempt - 1, 0, 5)));
        return TimeSpan.FromSeconds(Math.Clamp(seconds * jitter, 3d, 30d));
    }

    internal static async Task<bool> CloseOutputAsync(WebSocket socket, SemaphoreSlim sendGate,
        string reason, TimeSpan timeout, WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await SendCloseOutputAsync(socket, sendGate, reason, status, deadline.Token).ConfigureAwait(false);
            return socket.State is WebSocketState.CloseSent or WebSocketState.Closed;
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            Abort(socket);
            return false;
        }
    }

    internal static async Task<bool> CloseAndDrainAsync(WebSocket socket, SemaphoreSlim sendGate,
        Task receiveTask, string reason, TimeSpan timeout)
    {
        if (socket.State == WebSocketState.Closed) return true;
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await SendCloseOutputAsync(socket, sendGate, reason, WebSocketCloseStatus.NormalClosure,
                deadline.Token).ConfigureAwait(false);
            await receiveTask.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (socket.State == WebSocketState.Closed) return true;
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or
            ObjectDisposedException or IOException or JsonException or InvalidOperationException)
        {
            // A failed receiver or stalled peer cannot hold up a reconnect.
        }
        Abort(socket);
        return false;
    }

    private static async Task SendCloseOutputAsync(WebSocket socket, SemaphoreSlim sendGate,
        string reason, WebSocketCloseStatus status, CancellationToken token)
    {
        await sendGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // A close frame is a send too: share the ordinary message send gate.
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(status, reason, token).WaitAsync(token).ConfigureAwait(false);
        }
        catch
        {
            // Abort before releasing the gate so a waiting send cannot start on
            // a socket whose close operation is still unwinding.
            Abort(socket);
            throw;
        }
        finally { sendGate.Release(); }
    }

    private static void Abort(WebSocket socket)
    {
        try { socket.Abort(); } catch (ObjectDisposedException) { }
    }
}

using System.Net.WebSockets;

namespace AlphaChannel.Plugin;

// Used under StreamClient's send lock. Spread bursts rather than allowing each
// packet to trigger another server broadcast in the same instant.
internal sealed class RealtimeSendPacer(TimeSpan? interval = null, TimeSpan? timeout = null,
    Func<long>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly long spacing = (long)(interval ?? TimeSpan.FromMilliseconds(50)).TotalMilliseconds;
    private readonly TimeSpan sendTimeout = timeout ?? TimeSpan.FromSeconds(5);
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;
    private long nextSend;
    internal async Task SendAsync(WebSocket socket, ReadOnlyMemory<byte> bytes, CancellationToken token, Func<bool>? stillValid = null)
    {
        var remaining = nextSend - now();
        if (remaining > 0) await wait(TimeSpan.FromMilliseconds(remaining), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (stillValid is not null && !stillValid()) return;
        nextSend = now() + spacing;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(sendTimeout);
        var send = socket.SendAsync(bytes, WebSocketMessageType.Text, true, deadline.Token).AsTask();
        try { await send.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // Abort before releasing the caller's send lock. No queued send may
            // start on a socket with an outstanding, timed-out native send.
            try { socket.Abort(); } catch (ObjectDisposedException) { }
            _ = send.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException("Realtime send timed out; the connection was aborted.");
        }
    }
}

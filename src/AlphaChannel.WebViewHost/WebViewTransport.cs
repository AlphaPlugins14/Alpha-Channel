using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace AlphaChannel.WebViewHost;

internal sealed class WebViewTransport : IDisposable
{
    private readonly NamedPipeClientStream pipe, audio;
    private readonly BinaryWriter audioWriter;
    private readonly object audioLock = new();
    private readonly ConcurrentQueue<(byte Type, byte[] Payload)> messages = new();
    private readonly SemaphoreSlim outputReady = new(0, 1);
    private readonly CancellationTokenSource outputCancellation = new();
    private readonly Task outputTask;
    private byte[]? latestFrame, latestState;
    private int outputSignalled;
    private volatile bool disposed;
    internal WebViewTransport(NamedPipeClientStream pipe, NamedPipeClientStream audio)
    {
        this.pipe = pipe; this.audio = audio;
        audioWriter = new BinaryWriter(audio, Encoding.UTF8, true);
        outputTask = Task.Run(WriteMessagesAsync);
    }
    internal async Task ReadCommandsAsync(Action<byte, byte[]> dispatch)
    {
        var header = new byte[5];
        try
        {
            while (!disposed)
            {
                await pipe.ReadExactlyAsync(header);
                var length = BitConverter.ToInt32(header, 1);
                if (length < 0 || length > 1024 * 1024) throw new IOException("Invalid browser command.");
                var payload = new byte[length];
                await pipe.ReadExactlyAsync(payload);
                dispatch(header[0], payload);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        { if (!disposed) dispatch(21, []); }
    }
    internal void Send(byte type, byte[] payload)
    {
        if (disposed) return;
        // UI callbacks never wait behind a multi-megabyte frame write. Retain
        // only the newest pending image/state when the receiver falls behind.
        if (type == 1)
        {
            Interlocked.Exchange(ref latestFrame, payload);
        }
        else if (type == 3) Interlocked.Exchange(ref latestState, payload);
        else messages.Enqueue((type, payload));
        if (Interlocked.Exchange(ref outputSignalled, 1) == 0)
            try { outputReady.Release(); } catch (ObjectDisposedException) { }
    }
    private async Task WriteMessagesAsync()
    {
        var token = outputCancellation.Token;
        try
        {
            while (true)
            {
                await outputReady.WaitAsync(token);
                Interlocked.Exchange(ref outputSignalled, 0);
                while (messages.TryDequeue(out var message)) await WritePacketAsync(message.Type, message.Payload, token);
                var state = Interlocked.Exchange(ref latestState, null);
                if (state is not null) await WritePacketAsync(3, state, token);
                var frame = Interlocked.Exchange(ref latestFrame, null);
                if (frame is not null) await WritePacketAsync(1, frame, token);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    private async Task WritePacketAsync(byte type, byte[] payload, CancellationToken token)
    {
        var header = new byte[5]; header[0] = type;
        BitConverter.GetBytes(payload.Length).CopyTo(header, 1);
        await pipe.WriteAsync(header, token);
        await pipe.WriteAsync(payload, token);
    }
    internal void State(string address, string title, bool loading, bool back, bool forward, bool ready) =>
        Send(3, JsonSerializer.SerializeToUtf8Bytes(new { Address = address, Title = title, IsLoading = loading, CanGoBack = back, CanGoForward = forward, IsReady = ready }));
    internal void Error(string message) => Send(4, Encoding.UTF8.GetBytes(message));
    internal void Audio(ReadOnlySpan<byte> pcm, long captureTime)
    {
        lock (audioLock)
        {
            if (disposed) return;
            // Negative length identifies the timestamped audio protocol. The
            // timestamp is the capture start in monotonic 100-nanosecond units.
            try { audioWriter.Write(-pcm.Length); audioWriter.Write(captureTime); audioWriter.Write(pcm); audioWriter.Flush(); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        outputCancellation.Cancel();
        pipe.Dispose(); audio.Dispose();
        try { audioWriter.Dispose(); } catch (ObjectDisposedException) { }
        // Dispose coordination objects only once the blocked write has unwound.
        _ = outputTask.ContinueWith(_ => { outputReady.Dispose(); outputCancellation.Dispose(); }, TaskScheduler.Default);
        Interlocked.Exchange(ref latestFrame, null); Interlocked.Exchange(ref latestState, null);
        while (messages.TryDequeue(out _)) { }
    }
}

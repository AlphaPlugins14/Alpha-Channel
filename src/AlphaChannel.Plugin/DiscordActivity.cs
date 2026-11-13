using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace AlphaChannel.Plugin;

// Local Discord IPC only: no account credentials, remote API calls or party mutations.
internal sealed class DiscordActivity : IDisposable
{
    internal const string ApplicationId = "1564292556089597983";
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<CancellationToken, Task<Stream>> connect;
    private readonly Task worker;
    private sealed record DesiredActivity(string? Payload, bool Urgent);
    private DesiredActivity desired = new(null, false);
    private int disposed;

    internal DiscordActivity(Func<CancellationToken, Task<Stream>>? connector = null)
    {
        connect = connector ?? ConnectAsync;
        worker = Task.Run(RunAsync);
    }

    internal void Update(DiscordActivityState? state, bool urgent = false) =>
        Volatile.Write(ref desired, new(state is null ? null : JsonSerializer.Serialize(new
        {
            name = "Alpha Channel", type = state.Type, details = state.Details, state = state.State,
            assets = new { large_image = "alpha_channel", large_text = "Alpha Channel" }, instance = false
        }), urgent));

    private static async Task<Stream> ConnectAsync(CancellationToken cancellation)
    {
        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(100, cancellation).ConfigureAwait(false); return pipe; }
            catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
            { pipe.Dispose(); }
            catch { pipe.Dispose(); throw; }
        }
        throw new IOException("Discord desktop is unavailable.");
    }

    private async Task RunAsync()
    {
        var cancellation = lifetime.Token;
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (Volatile.Read(ref desired).Payload is null)
                { await Task.Delay(500, cancellation).ConfigureAwait(false); continue; }
                try
                {
                    using var pipe = await connect(cancellation).ConfigureAwait(false);
                    await RunSessionAsync(pipe, cancellation).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or JsonException or UnauthorizedAccessException or InvalidOperationException)
                { cancellation.ThrowIfCancellationRequested(); }
                // Discord can start/restart later. Keep retries away from the game thread.
                await Task.Delay(TimeSpan.FromSeconds(15), cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private async Task RunSessionAsync(Stream pipe, CancellationToken cancellation)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        session.CancelAfter(TimeSpan.FromSeconds(5));
        await WriteFrameAsync(pipe, 0, JsonSerializer.SerializeToUtf8Bytes(new { v = 1, client_id = ApplicationId }), session.Token).ConfigureAwait(false);
        while (true)
        {
            var frame = await ReadFrameAsync(pipe, session.Token).ConfigureAwait(false);
            if (frame.Opcode == 3)
            { await WriteFrameAsync(pipe, 4, frame.Payload, session.Token).ConfigureAwait(false); continue; }
            using var message = JsonDocument.Parse(frame.Payload);
            if (frame.Opcode != 1 || !message.RootElement.TryGetProperty("evt", out var evt) || evt.GetString() != "READY")
                throw new IOException("Discord rejected the handshake.");
            break;
        }
        session.CancelAfter(Timeout.InfiniteTimeSpan);
        string? sent = null;
        var nextUpdate = DateTime.MinValue;
        var read = ReadFrameAsync(pipe, session.Token);
        try
        {
            while (true)
            {
                var requested = Volatile.Read(ref desired);
                var target = requested.Payload;
                // Disabling clears immediately; ordinary updates are limited.
                if (target != sent && (target is null || requested.Urgent || DateTime.UtcNow >= nextUpdate))
                {
                    using var activity = JsonDocument.Parse(target ?? "null");
                    var command = JsonSerializer.SerializeToUtf8Bytes(new
                    { cmd = "SET_ACTIVITY", args = new { pid = Environment.ProcessId, activity = activity.RootElement }, nonce = Guid.NewGuid().ToString("N") });
                    using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                    writeTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                    await WriteFrameAsync(pipe, 1, command, writeTimeout.Token).ConfigureAwait(false);
                    sent = target;
                    nextUpdate = DateTime.UtcNow.AddSeconds(15);
                }
                if (target is null) return;
                await Task.WhenAny(read, Task.Delay(250, session.Token)).ConfigureAwait(false);
                session.Token.ThrowIfCancellationRequested();
                if (!read.IsCompleted) continue;
                var frame = await read.ConfigureAwait(false);
                if (frame.Opcode == 3)
                    await WriteFrameAsync(pipe, 4, frame.Payload, session.Token).ConfigureAwait(false);
                else if (frame.Opcode != 4)
                {
                    if (frame.Opcode != 1) throw new IOException("Discord disconnected.");
                    using var message = JsonDocument.Parse(frame.Payload);
                    if (message.RootElement.TryGetProperty("evt", out var evt) && evt.GetString() == "ERROR")
                        throw new IOException("Discord rejected the activity.");
                }
                read = ReadFrameAsync(pipe, session.Token);
            }
        }
        finally
        {
            session.Cancel();
            try { await read.ConfigureAwait(false); } catch (Exception) { /* Observe cancelled/closed read. */ }
        }
    }

    internal static async Task WriteFrameAsync(Stream stream, int opcode, byte[] payload, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), payload.Length);
        payload.CopyTo(frame, 8);
        await stream.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
        await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    internal static async Task<(int Opcode, byte[] Payload)> ReadFrameAsync(Stream stream, CancellationToken cancellation)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (size < 0 || size > 65536) throw new IOException("Invalid Discord frame size.");
        var payload = new byte[size];
        await stream.ReadExactlyAsync(payload, cancellation).ConfigureAwait(false);
        return (BinaryPrimitives.ReadInt32LittleEndian(header), payload);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); // Closing our IPC session removes our activity; never touch other apps.
        _ = worker.ContinueWith(_ => lifetime.Dispose(), TaskScheduler.Default);
    }
}

internal sealed record DiscordActivityState(int Type, string Details, string State);

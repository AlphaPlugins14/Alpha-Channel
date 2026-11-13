using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Text;
using System.Threading.Channels;
using AlphaChannel.BroadcastStudio;

namespace AlphaChannel.WebViewHost;

// Encoding and uploading are independent of the realtime mixer. Bounded queues
// prevent a slow or disconnected server from accumulating old audio indefinitely.
internal sealed class StudioBroadcast : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<byte[]> frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private Process? encoder;
    private string state = "CONNECTING";
    private string? error;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    internal string State => Volatile.Read(ref state);
    internal string? Error => Volatile.Read(ref error);
    internal double Seconds => elapsed.Elapsed.TotalSeconds;
    internal void Push(byte[] block) => frames.Writer.TryWrite(block);
    internal StudioBroadcast(string ffmpeg, StudioDestination destination, int bitrate)
    {
        if (string.IsNullOrWhiteSpace(destination.Host) || destination.Port is < 1 or > 65535 ||
            !destination.Mount.StartsWith('/')) throw new ArgumentException("Invalid broadcast connection details.");
        _ = RunAsync(ffmpeg, destination, bitrate);
    }
    private async Task RunAsync(string ffmpeg, StudioDestination destination, int bitrate)
    {
        var attempts = 0;
        while (!stop.IsCancellationRequested && attempts < 4)
        {
            state = attempts == 0 ? "CONNECTING" : "RECONNECTING";
            try
            {
                while (frames.Reader.TryRead(out _)) { }
                var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-f", "s16le", "-ar", "48000", "-ac", "2",
                    "-i", "pipe:0", "-vn", "-c:a", "libmp3lame", "-b:a", $"{Math.Clamp(bitrate, 64, 320)}k",
                    "-flush_packets", "1", "-f", "mp3", "pipe:1" }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start) ?? throw new IOException("Audio encoder could not start.");
                encoder = process;
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                var diagnostics = process.StandardError.ReadToEndAsync(attempt.Token);
                var feed = Task.Run(async () => { try { await FeedAsync(process, attempt.Token); } catch { attempt.Cancel(); throw; } });
                var address = new UriBuilder(destination.UseTls ? "https" : "http", destination.Host, destination.Port, destination.Mount).Uri;
                try
                {
                    // Icecast 2.4 source clients send an unbounded identity body.
                    // HttpClient wraps unknown-length HTTP/1.1 content in chunks,
                    // which older Icecast servers cannot decode as MP3.
                    using var socket = new TcpClient();
                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token);
                    handshake.CancelAfter(TimeSpan.FromSeconds(10));
                    await socket.ConnectAsync(address.Host, address.Port, handshake.Token);
                    using Stream stream = destination.UseTls
                        ? new SslStream(socket.GetStream(), leaveInnerStreamOpen: false)
                        : socket.GetStream();
                    if (stream is SslStream secure)
                        await secure.AuthenticateAsClientAsync(AlphaChannel.Media.MediaTlsTrust.ClientOptions(address.Host), handshake.Token);
                    var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(destination.User + ":" + destination.Password));
                    var headers = $"PUT {address.PathAndQuery} HTTP/1.1\r\nHost: {address.Authority}\r\nAuthorization: Basic {auth}\r\n" +
                        "Content-Type: audio/mpeg\r\nTransfer-Encoding: identity\r\nExpect: 100-continue\r\nConnection: close\r\n" +
                        $"Ice-Name: {destination.Name.Replace('\r', ' ').Replace('\n', ' ')}\r\nIce-Public: 0\r\n" +
                        $"Ice-Audio-Info: bitrate={Math.Clamp(bitrate, 64, 320)};samplerate=48000;channels=2\r\n\r\n";
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(headers), handshake.Token);
                    var status = await ReadSourceResponseAsync(stream, handshake.Token);
                    if (status != 100 && status is not (>= 200 and < 300))
                    {
                        throw new SourceRejectedException(status);
                    }
                    // 100 Continue is permission to send the body, not a final
                    // acceptance. Keep reading so later rejection/disconnects
                    // cannot leave the UI reporting a nonexistent live mount.
                    var response = MonitorSourceAsync(stream, status, attempt.Token);
                    var upload = CopyEncodedAudioAsync(process.StandardOutput.BaseStream, stream, attempt.Token);
                    try { await await Task.WhenAny(response, upload); }
                    finally
                    {
                        attempt.Cancel();
                        try { await response; } catch { }
                        try { await upload; } catch { }
                    }
                }
                finally
                {
                    attempt.Cancel();
                    try { if (!process.HasExited) process.Kill(true); } catch { }
                    try { await feed; } catch { }
                    try { await diagnostics; } catch { }
                    encoder = null;
                }
            }
            catch (Exception) when (stop.IsCancellationRequested) { break; }
            catch (SourceRejectedException rejection)
            {
                error = rejection.Status is 401 or 403
                    ? "Icecast rejected the connection details." : rejection.Status == 409
                        ? "This mount is already broadcasting. Stop your other DJ software first."
                        : $"Icecast rejected the broadcast (HTTP {rejection.Status}).";
                state = "FAILED";
                return;
            }
            catch
            {
                // Raw HTTP/FFmpeg exceptions can contain addresses or secrets.
                error = "Broadcast connection interrupted. Reconnecting…";
                state = "RECONNECTING";
                attempts++;
                if (attempts >= 4) { state = "FAILED"; error = "Could not connect to Icecast. Check your connection and try Go Live again."; break; }
                try { await Task.Delay(TimeSpan.FromSeconds(3), stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
        if (stop.IsCancellationRequested) state = "OFF AIR";
    }
    private sealed class SourceRejectedException(int status) : IOException
    {
        internal int Status { get; } = status;
    }
    private static async Task MonitorSourceAsync(Stream stream, int status, CancellationToken token)
    {
        while (status == 100)
        {
            status = await ReadSourceResponseAsync(stream, token);
            if (status != 100 && status is not (>= 200 and < 300)) throw new SourceRejectedException(status);
        }
        var buffer = new byte[1024];
        while (await stream.ReadAsync(buffer, token) > 0) { }
        throw new IOException("Icecast closed the source connection.");
    }
    private async Task FeedAsync(Process process, CancellationToken token)
    {
        await foreach (var frame in frames.Reader.ReadAllAsync(token))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await process.StandardInput.BaseStream.WriteAsync(frame, deadline.Token);
        }
    }
    public void Dispose()
    {
        stop.Cancel();
        frames.Writer.TryComplete();
        try { if (encoder is { HasExited: false } process) process.Kill(true); } catch { }
        state = "OFF AIR";
    }
    private static async Task<int> ReadSourceResponseAsync(Stream stream, CancellationToken token)
    {
        var header = new List<byte>(); var one = new byte[1];
        while (header.Count < 16384)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new IOException("Source connection closed.");
            header.Add(one[0]);
            var n = header.Count;
            if (n < 4 || header[n - 4] != 13 || header[n - 3] != 10 || header[n - 2] != 13 || header[n - 1] != 10) continue;
            var status = Encoding.ASCII.GetString(header.ToArray()).Split('\r')[0].Split(' ');
            if (status.Length >= 2 && status[0].StartsWith("HTTP/", StringComparison.Ordinal) && int.TryParse(status[1], out var code)) return code;
            throw new IOException("Invalid source response.");
        }
        throw new IOException("Source response too large.");
    }
    private async Task CopyEncodedAudioAsync(Stream input, Stream output, CancellationToken token)
    {
        var buffer = new byte[8192];
        while (true)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var count = await input.ReadAsync(buffer, deadline.Token);
            if (count == 0) throw new IOException("The encoder stopped.");
            await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
            if (!stop.IsCancellationRequested) { state = "LIVE"; error = null; }
        }
    }
}

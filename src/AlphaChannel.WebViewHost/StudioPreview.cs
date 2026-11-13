using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace AlphaChannel.WebViewHost;

// A private, bounded PCM feed for the local TV scene. Never a party broadcast URL.
internal sealed class StudioPreview : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly HashSet<Channel<byte[]>> readers = [];
    private readonly string path = "/" + Guid.NewGuid().ToString("N") + ".wav";
    private readonly Task serving;
    internal string Url { get; }

    internal StudioPreview()
    {
        listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}{path}";
        serving = ServeAsync();
    }

    internal void Push(byte[] pcm)
    {
        lock (gate)
        {
            if (readers.Count == 0) return;
            var copy = (byte[])pcm.Clone();
            foreach (var reader in readers) reader.Writer.TryWrite(copy);
        }
    }

    private async Task ServeAsync()
    {
        var clients = new List<Task>();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                clients.RemoveAll(task => task.IsCompleted);
                clients.Add(ServeClientAsync(client));
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
        finally { await Task.WhenAll(clients); stop.Dispose(); }
    }

    private async Task ServeClientAsync(TcpClient client)
    {
        var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        try
        {
            using (client)
            using (var network = client.GetStream())
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                using var request = new StreamReader(network, Encoding.ASCII, leaveOpen: true);
                var first = await request.ReadLineAsync(deadline.Token);
                var bytes = first?.Length ?? 0;
                while (await request.ReadLineAsync(deadline.Token) is { Length: > 0 } line)
                    if ((bytes += line.Length) > 8192) return;
                if (first != $"GET {path} HTTP/1.1" && first != $"GET {path} HTTP/1.0") return;
                lock (gate)
                {
                    if (readers.Count >= 4 || stop.IsCancellationRequested) return;
                    readers.Add(queue);
                }
                await network.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"), stop.Token);
                var wav = new byte[44];
                Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
                BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), uint.MaxValue);
                Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
                BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
                BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
                BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 2);
                BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), 48000);
                BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), 192000);
                BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 4);
                BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
                Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
                BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), uint.MaxValue);
                await network.WriteAsync(wav, stop.Token);
                await foreach (var pcm in queue.Reader.ReadAllAsync(stop.Token))
                {
                    using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    writeDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                    await network.WriteAsync(pcm, writeDeadline.Token);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { lock (gate) readers.Remove(queue); client.Dispose(); }
    }

    public void Dispose()
    {
        stop.Cancel(); listener.Stop();
        lock (gate) { foreach (var reader in readers) reader.Writer.TryComplete(); readers.Clear(); }
        // ServeAsync releases cancellation after the local clients have disconnected.
    }
}

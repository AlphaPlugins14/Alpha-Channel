using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AlphaChannel.Plugin.Video;

/// <summary>
/// Decodes a live network audio stream with the bundled FFmpeg process and
/// exposes stable PCM to libmpv over a loopback-only HTTP connection.
/// </summary>
internal sealed class LiveAudioFfmpegRelay : IDisposable
{
    private readonly Process process;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task relayTask;
    private readonly Task inputTask;
    private bool disposed;

    private LiveAudioFfmpegRelay(
        Process process,
        TcpListener listener,
        string playbackUrl,
        string sourceUrl,
        Func<HttpClient> createHttpClient)
    {
        this.process = process;
        this.listener = listener;
        PlaybackUrl = playbackUrl;
        inputTask = Task.Run(() => FeedSourceAsync(sourceUrl, createHttpClient));
        relayTask = Task.Run(ServeAsync);
    }

    internal string PlaybackUrl { get; }

    internal static LiveAudioFfmpegRelay? TryStart(
        string ffmpegPath,
        string sourceUrl,
        Func<HttpClient>? createHttpClient = null)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) ||
            !File.Exists(ffmpegPath) ||
            string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        Process? process = null;
        TcpListener? listener = null;

        try
        {
            // Own the loopback listener here instead of asking FFmpeg to create
            // it after connecting upstream. This removes the startup race.
            listener = new TcpListener(
                IPAddress.Loopback,
                0);
            listener.Start(1);

            var port =
                ((IPEndPoint)listener.LocalEndpoint).Port;
            var playbackUrl =
                $"http://127.0.0.1:{port}/alphachannel-live.wav";

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true
            };

            AddArgument(startInfo, "-hide_banner");
            AddArgument(startInfo, "-loglevel");
            AddArgument(startInfo, "warning");
            AddArgument(startInfo, "-nostdin");
            AddArgument(startInfo, "-probesize");
            AddArgument(startInfo, "32768");
            AddArgument(startInfo, "-analyzeduration");
            AddArgument(startInfo, "1000000");
            AddArgument(startInfo, "-i");
            AddArgument(startInfo, "pipe:0");
            AddArgument(startInfo, "-map");
            AddArgument(startInfo, "0:a:0");
            AddArgument(startInfo, "-vn");
            AddArgument(startInfo, "-af");
            AddArgument(
                startInfo,
                "aresample=48000:async=1000:first_pts=0");
            AddArgument(startInfo, "-ac");
            AddArgument(startInfo, "2");
            AddArgument(startInfo, "-ar");
            AddArgument(startInfo, "48000");
            AddArgument(startInfo, "-c:a");
            AddArgument(startInfo, "pcm_s16le");
            AddArgument(startInfo, "-f");
            AddArgument(startInfo, "s16le");
            AddArgument(startInfo, "pipe:1");

            process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            process.ErrorDataReceived +=
                (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        AepLog.Warning(
                            "[LIVE-AUDIO-RELAY] [redacted]");
                    }
                };

            if (!process.Start())
            {
                process.Dispose();
                listener.Stop();
                return null;
            }

            process.BeginErrorReadLine();

            

            return new LiveAudioFfmpegRelay(
                process,
                listener,
                playbackUrl,
                sourceUrl,
                createHttpClient ?? (() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan }));
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[LIVE-AUDIO-RELAY] Could not start.");

            try
            {
                listener?.Stop();

                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best-effort cleanup after a failed start.
            }

            process?.Dispose();
            return null;
        }
    }

    private async Task FeedSourceAsync(string sourceUrl, Func<HttpClient> createHttpClient)
    {
        var token = cancellation.Token;
        var buffer = new byte[16384];
        try
        {
            using var http = createHttpClient();
            using (var prepared = PreparedLiveAudioSource.Take(sourceUrl))
            {
                if (prepared != null)
                {
                    
                    await prepared.CopyToAsync(process.StandardInput.BaseStream, token).ConfigureAwait(false);
                    {}
                }
            }
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, sourceUrl);
                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
                    handshake.CancelAfter(TimeSpan.FromSeconds(10));
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, handshake.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        AepLog.Warning("[LIVE-AUDIO-RELAY] Listener HTTP [redacted]; no audio is available.");
                        return;
                    }
                    
                    using var audio = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    while (true)
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(15));
                        var count = await audio.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                        if (count == 0) break;
                        await process.StandardInput.BaseStream.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (!token.IsCancellationRequested && exception is HttpRequestException or IOException or OperationCanceledException)
                {
                    {}
                }
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception) { AepLog.Warning("[LIVE-AUDIO-RELAY] Listener feed failed: [redacted]."); }
        finally { try { process.StandardInput.Close(); } catch { } }
    }

    private async Task ServeAsync()
    {
        // Keep decoding the same upstream station across local player reconnects.
        // Raw PCM lets each new HTTP connection receive its own valid WAV header.
        var buffer = new byte[16384]; // Whole stereo, 16-bit sample frames.
        var token = cancellation.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(token);
                using var network = client.GetStream();
                try
                {
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await ReadHttpRequestAsync(network, requestTimeout.Token);
                    // Do not advertise a playable WAV stream before upstream
                    // decoding produces PCM. A missing mount used to return a
                    // valid-looking empty WAV, leaving a black TV with text.
                    try { await process.StandardOutput.BaseStream.ReadExactlyAsync(buffer, token); }
                    catch (EndOfStreamException)
                    {
                        AepLog.Warning("[LIVE-AUDIO-RELAY] Upstream ended before any decoded audio was available.");
                        return;
                    }
                    var header = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\n" +
                        "Content-Type: audio/wav\r\n" +
                        "Accept-Ranges: none\r\n" +
                        "Cache-Control: no-store\r\n" +
                        "Connection: close\r\n\r\n");
                    await network.WriteAsync(header, token);
                    await network.WriteAsync(CreateWaveHeader(), token);
                    await network.WriteAsync(buffer, token);
                    
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            // Keep reconnects aligned to whole PCM sample frames,
                            // even if a previous socket disconnects during a write.
                            await process.StandardOutput.BaseStream.ReadExactlyAsync(buffer, token);
                        }
                        catch (EndOfStreamException)
                        {
                            AepLog.Warning("[LIVE-AUDIO-RELAY] FFmpeg audio output ended.");
                            return;
                        }
                        await network.WriteAsync(buffer, token);
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    
                }
                catch (IOException) when (!token.IsCancellationRequested)
                {
                    
                }
            }
        }
        catch (OperationCanceledException) { /* Normal shutdown. */ }
        catch (ObjectDisposedException) { /* Normal shutdown. */ }
        catch (Exception)
        {
            if (!disposed) AepLog.Warning("[LIVE-AUDIO-RELAY] Loopback stream failed.");
        }
    }

    private static byte[] CreateWaveHeader()
    {
        using var header = new MemoryStream(44);
        using var writer = new BinaryWriter(header, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8); writer.Write(uint.MaxValue); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)2);
        writer.Write(48000); writer.Write(48000 * 4); writer.Write((short)4); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(uint.MaxValue);
        return header.ToArray();
    }

    private static async Task ReadHttpRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var terminator = new byte[]
        {
            (byte)'\r',
            (byte)'\n',
            (byte)'\r',
            (byte)'\n'
        };
        var matched = 0;

        while (matched < terminator.Length)
        {
            var read = await stream.ReadAsync(
                buffer,
                cancellationToken);

            if (read == 0)
            {
                throw new IOException(
                    "The local player disconnected before sending its request.");
            }

            for (var index = 0; index < read; index++)
            {
                if (buffer[index] == terminator[matched])
                {
                    matched++;

                    if (matched == terminator.Length)
                    {
                        return;
                    }
                }
                else
                {
                    matched = buffer[index] == terminator[0]
                        ? 1
                        : 0;
                }
            }
        }
    }

    private static void AddArgument(
        ProcessStartInfo startInfo,
        string argument)
    {
        startInfo.ArgumentList.Add(argument);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cancellation.Cancel();

        try
        {
            listener.Stop();

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1500);
            }

            relayTask.Wait(500);
            inputTask.Wait(500);
        }
        catch (Exception)
        {
            
        }
        finally
        {
            process.Dispose();
            cancellation.Dispose();
        }
    }
}

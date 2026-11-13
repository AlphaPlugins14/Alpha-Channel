using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using AlphaChannel.BroadcastStudio;

namespace AlphaChannel.WebViewHost;

internal sealed class StudioDecoder : IStudioDeckDecoder
{
    internal const int BlockBytes = 48000 * 2 * 2 / 50;
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<byte[]> blocks = Channel.CreateBounded<byte[]>(8);
    private readonly Process process;
    internal double Duration;
    internal string? Error;
    internal bool Finished => blocks.Reader.Completion.IsCompleted;
    internal bool TryRead(out byte[] block) => blocks.Reader.TryRead(out block!);
    double IStudioDeckDecoder.Duration => Duration;
    string? IStudioDeckDecoder.Error => Error;
    bool IStudioDeckDecoder.Finished => Finished;
    bool IStudioDeckDecoder.TryRead(out byte[] block) => TryRead(out block);

    internal StudioDecoder(string ffmpeg, string path, double offset)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The song file could not be found.");
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-nostdin", "-ss", Math.Max(0, offset).ToString(CultureInfo.InvariantCulture),
            "-i", path, "-vn", "-ac", "2", "-ar", "48000", "-f", "s16le", "pipe:1" }) start.ArgumentList.Add(arg);
        process = Process.Start(start) ?? throw new IOException("The music decoder could not start.");
        _ = ReadInfoAsync();
        _ = PumpAsync();
    }
    private async Task ReadInfoAsync()
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(stop.Token) is { } line)
            {
                var match = Regex.Match(line, @"Duration: (\d+):(\d+):(\d+\.\d+)");
                if (match.Success) Duration = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                    + double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                    + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
        catch { Error = "Could not read the song's metadata."; }
    }
    private async Task PumpAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var block = new byte[BlockBytes];
                var read = 0;
                while (read < block.Length)
                {
                    var count = await process.StandardOutput.BaseStream.ReadAsync(block.AsMemory(read), stop.Token);
                    if (count == 0) break;
                    read += count;
                }
                if (read == 0) break;
                if (read < block.Length) Array.Resize(ref block, read - read % 4);
                await blocks.Writer.WriteAsync(block, stop.Token);
            }
            await process.WaitForExitAsync(stop.Token);
            if (process.ExitCode != 0) Error = "The song could not be decoded. Try another audio file.";
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
        catch { Error = "Music decoding stopped unexpectedly."; }
        finally { blocks.Writer.TryComplete(); }
    }
    public void Dispose()
    {
        stop.Cancel();
        try { if (!process.HasExited) process.Kill(true); } catch { }
        // Streams are closed to release any pending decoder reads.
        process.Dispose();
    }
}

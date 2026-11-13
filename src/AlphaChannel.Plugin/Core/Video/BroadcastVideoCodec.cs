using System.Collections.Concurrent;
using System.Diagnostics;

namespace AlphaChannel.Plugin.Video;

/// <summary>Tests real encode sessions, never relying on FFmpeg's compiled encoder list.</summary>
internal static class BroadcastVideoCodec
{
    internal const string Software = "h264";
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> selections = new();
    private static readonly ConcurrentDictionary<string, byte> unavailable = new();

    internal static string[] Options(string codec) => codec switch
    {
        "h264_nvenc" => ["-c:v", codec, "-preset", "p1", "-tune", "ll", "-bf", "0", "-rc", "cbr", "-pix_fmt", "nv12"],
        "h264_qsv" => ["-c:v", codec, "-preset", "veryfast", "-async_depth", "1", "-bf", "0", "-pix_fmt", "nv12"],
        "h264_amf" => ["-c:v", codec, "-usage", "ultralowlatency", "-quality", "speed", "-rc", "cbr", "-bf", "0", "-pix_fmt", "nv12"],
        // The bundled LGPL build uses OpenH264. x264-only preset/tune options
        // do not apply to it; generic h264 also works with existing installations.
        _ => ["-c:v", Software, "-pix_fmt", "yuv420p"],
    };

    internal static void Reject(string path, string codec)
    {
        if (codec != Software) unavailable.TryAdd(Key(path, codec), 0);
    }

    private static string Key(string path, string suffix) => Path.GetFullPath(path).ToUpperInvariant() + "|" + suffix;

    internal static async Task<string> SelectAsync(string path, int width, int height, double fps, int bitrate)
    {
        // One bounded background test per output profile; multiple broadcasters
        // share it. Failed devices remain disabled until plugin reload.
        var key = Key(path, $"{width}x{height}|{fps:R}|{bitrate}");
        var selection = selections.GetOrAdd(key, _ => new Lazy<Task<string>>(() => Task.Run(async () =>
        {
            foreach (var codec in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
            {
                if (unavailable.ContainsKey(Key(path, codec)))
                {
                    
                    continue;
                }
                if (await ProbeAsync(path, codec, width, height, fps, bitrate).ConfigureAwait(false))
                {
                    
                    return codec;
                }
            }
            
            return Software;
        })));
        var selected = await selection.Value.ConfigureAwait(false);
        return unavailable.ContainsKey(Key(path, selected)) ? Software : selected;
    }

    internal static async Task<bool> ProbeAsync(string path, string codec, int width, int height, double fps, int bitrate)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(path)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true,
            },
        };
        var args = process.StartInfo.ArgumentList;
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i",
                     $"color=c=black:s={Math.Max(2, width / 2 * 2)}x{Math.Max(2, height / 2 * 2)}:r={fps.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}",
                     "-frames:v", "3", "-an" }) args.Add(arg);
        foreach (var arg in Options(codec)) args.Add(arg);
        foreach (var arg in new[] { "-b:v", $"{Math.Max(100, bitrate)}k", "-maxrate", $"{Math.Max(100, bitrate)}k",
                     "-bufsize", $"{Math.Max(100, bitrate) * 2}k", "-f", "null", "-" }) args.Add(arg);
        Task<string>? errors = null;
        try
        {
            if (!process.Start())
            {
                return false;
            }
            errors = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(errors, output).ConfigureAwait(false);
            if (process.ExitCode == 0) return true;
            return false;
        }
        catch (Exception)
        {
            try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(false); } } catch { }
            return false;
        }

    }
}

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AlphaChannel.Plugin.Video;

// Uses the installed FFmpeg, never the active MPV instance. No frames or audio
// are played. All output is drained with a memory cap, and each process is owned
// by a cancellation scope so closing a chooser cannot leave helpers behind.
internal static class WebVideoMetadataProbe
{
    private static readonly SemaphoreSlim Slots = new(2, 2);
    private static readonly Regex Duration = new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Dimensions = new(@"(?<![\w])(?<w>\d{2,5})x(?<h>\d{2,5})(?![\w])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static async Task<ResolvedMediaRequest> ReadAsync(string? ffmpeg, ResolvedMediaRequest media, CancellationToken token)
    {
        if (ffmpeg is null || !File.Exists(ffmpeg) || media.Url is null || ResolvedMediaRequest.HttpUrl(media.Url) is null ||
            media.DurationSeconds is not null && media.Width is not null && media.Height is not null && media.HasVideo is not null && media.HasAudio is not null)
            return media;
        try { await Slots.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return media; }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(6));
            using var process = new Process { StartInfo = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true, RedirectStandardOutput = true
            }};
            foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "info", "-rw_timeout", "3000000",
                "-probesize", "524288", "-analyzeduration", "1500000", "-threads", "1", "-protocol_whitelist", "http,https,tcp,tls,crypto" })
                process.StartInfo.ArgumentList.Add(argument);
            process.StartInfo.ArgumentList.Add("-tls_verify");
            process.StartInfo.ArgumentList.Add("1");
            process.StartInfo.ArgumentList.Add("-ca_file");
            process.StartInfo.ArgumentList.Add(AlphaChannel.Media.MediaTlsTrust.BundlePath);
            if (media.Headers is { Count: > 0 })
            {
                var headers = string.Join("", media.Headers.Where(h => ResolvedMediaRequest.ValidHeader(h.Key, h.Value))
                    .Select(h => h.Key + ": " + h.Value + "\r\n"));
                if (headers.Length > 0) { process.StartInfo.ArgumentList.Add("-headers"); process.StartInfo.ArgumentList.Add(headers); }
            }
            foreach (var argument in new[] { "-i", media.Url, "-t", "0", "-f", "null", "-" }) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return media;
            using var stop = deadline.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            });
            var error = DrainAsync(process.StandardError);
            var output = DrainAsync(process.StandardOutput);
            await process.WaitForExitAsync().ConfigureAwait(false);
            var text = await error.ConfigureAwait(false);
            await output.ConfigureAwait(false);
            return deadline.IsCancellationRequested || process.ExitCode != 0 ? media : Parse(text, media);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception or RegexMatchTimeoutException)
        { return media; }
        finally { Slots.Release(); }
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            if (text.Length < 65536) text.Append(buffer, 0, Math.Min(count, 65536 - text.Length));
        return text.ToString();
    }

    internal static ResolvedMediaRequest Parse(string text, ResolvedMediaRequest media)
    {
        var start = text.IndexOf("Input #0,", StringComparison.Ordinal);
        if (start < 0) return media;
        var input = text[start..];
        var end = input.IndexOf("Stream mapping:", StringComparison.Ordinal);
        if (end >= 0) input = input[..end];
        var lines = input.Split('\n');
        var video = lines.FirstOrDefault(l => l.Contains("Stream #0:", StringComparison.Ordinal) && l.Contains("Video:", StringComparison.Ordinal));
        var audio = lines.Any(l => l.Contains("Stream #0:", StringComparison.Ordinal) && l.Contains("Audio:", StringComparison.Ordinal));
        // A partial/error-only log must never claim that audio/video is absent.
        if (video is null && !audio) return media;
        var duration = Duration.Match(input);
        double? seconds = null;
        if (duration.Success && double.TryParse(duration.Groups[1].Value, CultureInfo.InvariantCulture, out var hours) &&
            double.TryParse(duration.Groups[2].Value, CultureInfo.InvariantCulture, out var minutes) &&
            double.TryParse(duration.Groups[3].Value, CultureInfo.InvariantCulture, out var remainder))
        {
            var total = hours * 3600 + minutes * 60 + remainder;
            if (double.IsFinite(total) && total >= 0 && total < TimeSpan.MaxValue.TotalSeconds / 2) seconds = total;
        }
        var dimensions = Dimensions.Match(video ?? "");
        int? width = dimensions.Success && int.TryParse(dimensions.Groups["w"].Value, out var w) && w is > 0 and <= 32768 ? w : null;
        int? height = dimensions.Success && int.TryParse(dimensions.Groups["h"].Value, out var h) && h is > 0 and <= 32768 ? h : null;
        return media with
        {
            DurationSeconds = media.DurationSeconds ?? seconds, Width = media.Width ?? width, Height = media.Height ?? height,
            HasVideo = media.HasVideo ?? (video is not null), HasAudio = media.HasAudio ?? audio
        };
    }
}

using System.Diagnostics;

namespace AlphaChannel.Plugin.Video;

internal sealed class LocalVideoSubtitleCache : IDisposable
{
    internal sealed record Result(string? Path, string? Error);
    private readonly Dictionary<string, Task<Result>> tasks = new(StringComparer.Ordinal);
    private readonly object lifetimeGate = new();
    private bool disposed;
    private readonly CancellationTokenSource cancellation = new();
    private readonly string directory = Path.Combine(Path.GetTempPath(), "AlphaChannelSubtitles", Guid.NewGuid().ToString("N"));

    // Called on the framework thread; extraction never blocks that thread.
    internal Result? Prepare(string ffmpeg, string source, int subtitleOrdinal)
    {
        lock (lifetimeGate)
        {
        if (disposed) return new(null, "Subtitle preparation is unavailable after shutdown.");
        string key;
        try
        {
            var info = new FileInfo(source);
            key = $"{source}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{subtitleOrdinal}";
        }
        catch (IOException) { return new(null, "The local video is no longer available."); }
        catch (UnauthorizedAccessException) { return new(null, "Could not access the local video."); }
        if (!tasks.TryGetValue(key, out var task))
        {
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".ass");
            var token = cancellation.Token;
            task = Task.Run(() => ExtractAsync(ffmpeg, source, subtitleOrdinal, path, token));
            tasks.Add(key, task);
        }
        if (!task.IsCompletedSuccessfully) return null;
        var result = task.Result;
        // Let the caller's retry delay govern another attempt after a transient failure.
        if (result.Error is not null) tasks.Remove(key);
        return result;
        }
    }

    private static async Task<Result> ExtractAsync(string ffmpeg, string source, int ordinal,
        string path, CancellationToken token)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var start = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                         "-i", source, "-map", $"0:s:{ordinal}", "-c:s", "ass", path })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return new(null, "Could not prepare broadcast subtitles.");
            using var registration = token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { }
            });
            // Drain stderr without logging local filenames or subtitle text.
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            return process.ExitCode == 0 && File.Exists(path)
                ? new(path, null) : new(null, "Could not extract the selected broadcast subtitles.");
        }
        catch (OperationCanceledException) { return new(null, "Subtitle preparation cancelled."); }
        catch (Exception) { return new(null, "Could not prepare broadcast subtitles."); }
    }

    public void Dispose()
    {
        Task<Result>[] pending;
        lock (lifetimeGate)
        {
        if (disposed) return;
        disposed = true;
        cancellation.Cancel();
        pending = tasks.Values.ToArray();
        tasks.Clear();
        }
        _ = Task.WhenAll(pending).ContinueWith(_ =>
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch { }
            cancellation.Dispose();
        }, TaskScheduler.Default);
    }
}

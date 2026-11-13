using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin.YouTube;

internal static class YouTubeLikedVideosFetcher
{
    internal const int FetchLimit = 60;

    internal static async Task<List<VideoSearchEntry>> FetchAsync(
        string ytDlpPath,
        string cookiesPath,
        CancellationToken cancellationToken,
        YouTubeRequestPriority priority = YouTubeRequestPriority.Background)
    {
        using var permit = await YouTubeRequestScheduler.Shared.WaitAsync(
            YouTubeRequestIdentity.Account, priority, 4, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        foreach (var argument in new[]
                 {
                     "--flat-playlist", "--playlist-end", FetchLimit.ToString(CultureInfo.InvariantCulture),
                     "--dump-single-json", "--skip-download", "--verbose",
                     "--cookies", cookiesPath, ":ytfav"
                 })
            process.StartInfo.ArgumentList.Add(argument);

        if (!process.Start())
            throw new InvalidOperationException("yt-dlp could not be started.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            var authenticated = error.Contains("Found YouTube account cookies", StringComparison.OrdinalIgnoreCase) ||
                                error.Contains("Extracted SAPISID cookie", StringComparison.OrdinalIgnoreCase);
            var rotatedCookies = error.Contains("account cookies are no longer valid", StringComparison.OrdinalIgnoreCase);
            var missingPlaylist = error.Contains("playlist does not exist", StringComparison.OrdinalIgnoreCase);
            
            if (YouTubeAccountSessionErrors.IndicatesExpiredCookies(error))
                throw new YouTubeSessionExpiredException();
            if (process.ExitCode != 0)
            {
                var errorLine = error.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .LastOrDefault(line => line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase));
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(errorLine)
                        ? "yt-dlp could not read your YouTube liked videos."
                        : errorLine[..Math.Min(errorLine.Length, 400)]);
            }

            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("YouTube returned no liked-video list.");

            var videos = new List<VideoSearchEntry>(FetchLimit);
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sourceCount = entries.GetArrayLength();
            var skippedEntries = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                if (videos.Count >= FetchLimit)
                    break;

                if (entry.ValueKind != JsonValueKind.Object ||
                    !HasUsableMetadata(Text(entry, "title"), Text(entry, "channel") ?? Text(entry, "uploader")) ||
                    string.Equals(Text(entry, "availability"), "private", StringComparison.OrdinalIgnoreCase))
                {
                    skippedEntries++;
                    continue;
                }

                var id = Text(entry, "id");
                if (id is null || id.Length != 11 ||
                    !id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ||
                    !seenIds.Add(id))
                {
                    skippedEntries++;
                    continue;
                }

                var title = Text(entry, "title")!;
                var channel = Text(entry, "channel") ?? Text(entry, "uploader") ?? "YouTube";
                var thumbnail = Text(entry, "thumbnail") ?? $"https://i.ytimg.com/vi/{id}/hqdefault.jpg";
                TimeSpan? duration = null;
                if (entry.TryGetProperty("duration", out var durationValue) &&
                    durationValue.ValueKind == JsonValueKind.Number &&
                    durationValue.TryGetDouble(out var seconds) && seconds >= 0)
                    duration = TimeSpan.FromSeconds(seconds);

                long? views = null;
                if (entry.TryGetProperty("view_count", out var viewValue) &&
                    viewValue.ValueKind == JsonValueKind.Number && viewValue.TryGetInt64(out var count))
                    views = count;

                var uploaded = YouTubeFeedEntryMetadata.UploadDate(entry);

                videos.Add(new VideoSearchEntry(title, $"https://www.youtube.com/watch?v={id}",
                    channel, duration, thumbnail, views, uploaded, Text(entry, "channel_id")));
            }

            

            return videos;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
    }

    // Reject metadata-free playlist placeholders, including old cached fallbacks.
    // A real video called "YouTube video" with an actual creator remains valid.
    internal static bool HasUsableMetadata(string? title, string? channel)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var normalized = title.Trim();
        if (normalized.Equals("[Private video]", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("[Deleted video]", StringComparison.OrdinalIgnoreCase))
            return false;

        return !normalized.Equals("YouTube video", StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(channel) &&
                !channel.Trim().Equals("YouTube", StringComparison.OrdinalIgnoreCase));
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

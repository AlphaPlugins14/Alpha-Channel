using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin.YouTube;

internal static class YouTubeSubscriptionFeedFetcher
{
    internal const int FetchLimit = 30;

    internal static Task<List<VideoSearchEntry>> FetchAsync(
        string ytDlpPath, string cookiesPath, CancellationToken cancellationToken,
        YouTubeRequestPriority priority = YouTubeRequestPriority.Background) =>
        FetchFeedAsync(ytDlpPath, cookiesPath, cancellationToken, priority, ":ytsubs", FetchLimit, "Subscriptions");

    internal static Task<List<VideoSearchEntry>> FetchRecommendationsAsync(
        string ytDlpPath, string cookiesPath, CancellationToken cancellationToken,
        YouTubeRequestPriority priority = YouTubeRequestPriority.Background) =>
        FetchFeedAsync(ytDlpPath, cookiesPath, cancellationToken, priority, ":ytrec", YouTubeRecommendationCache.FetchLimit, "Recommended");

    private static async Task<List<VideoSearchEntry>> FetchFeedAsync(
        string ytDlpPath, string cookiesPath, CancellationToken cancellationToken,
        YouTubeRequestPriority priority, string feed, int limit, string label)
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
                     "--flat-playlist", "--playlist-end", limit.ToString(CultureInfo.InvariantCulture),
                     "--dump-single-json", "--skip-download", "--verbose",
                     "--cookies", cookiesPath, feed
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
            
            if (YouTubeAccountSessionErrors.IndicatesExpiredCookies(error))
                throw new YouTubeSessionExpiredException();
            if (process.ExitCode != 0)
            {
                var errorLine = error.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .LastOrDefault(line => line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase));
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorLine)
                    ? $"yt-dlp could not read your YouTube {label.ToLowerInvariant()} feed."
                    : errorLine[..Math.Min(errorLine.Length, 400)]);
            }

            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"YouTube returned no {label.ToLowerInvariant()} feed.");

            var videos = new List<VideoSearchEntry>(limit);
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skippedEntries = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                // YouTube may mix channel cards into the subscriptions feed. Only
                // entries with video IDs belong in the video cache.
                if (entry.ValueKind != JsonValueKind.Object ||
                    !IsVideoEntry(entry, out var id) || !seenIds.Add(id))
                {
                    skippedEntries++;
                    continue;
                }

                var title = Text(entry, "title") ?? "YouTube video";
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
                    channel, duration, thumbnail, views, uploaded, Text(entry, "channel_id"),
                    views is not null ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : null));
                if (videos.Count == limit) break;
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

    private static bool IsVideoEntry(JsonElement entry, out string id)
    {
        id = Text(entry, "id") ?? string.Empty;
        var url = Text(entry, "url") ?? Text(entry, "webpage_url") ?? string.Empty;
        var isVideoUrl = url.Contains("watch?v=", StringComparison.OrdinalIgnoreCase) ||
                         url.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(Text(entry, "ie_key"), "Youtube", StringComparison.OrdinalIgnoreCase);
        return isVideoUrl && id.Length == 11 &&
               id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

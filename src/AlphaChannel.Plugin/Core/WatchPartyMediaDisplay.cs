using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal static class WatchPartyMediaDisplay
{
    // A stream address (including visualizer/text query data) is a playback
    // detail, not a public title. Keep friendly station/video names intact.
    internal static bool IsInternalAudioTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var value = title.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)) value = uri.AbsolutePath;
        return string.Equals(value.Split('/').Last().Split('?', '#')[0], "alphachannel-live.wav", StringComparison.OrdinalIgnoreCase);
    }

    internal static string Title(string? url, string? title, bool audioOnly = false)
    {
        if (InternetArchiveMediaDisplay.IsArchiveUrl(url))
            return InternetArchiveMediaDisplay.Title(url, title);
        var audio = audioOnly || AudioVisualizerSelection.HasAudioVisualizerMetadata(url ?? string.Empty);
        if (IsInternalAudioTitle(title)) title = null;
        if (audio && string.Equals(title, "Watch Party media", StringComparison.Ordinal))
            return "Live Radio";
        if (!string.IsNullOrWhiteSpace(title) &&
            !title.Contains("://", StringComparison.Ordinal) &&
            !title.TrimStart().StartsWith("www.", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(title, url, StringComparison.OrdinalIgnoreCase))
            return title;

        return audio
            ? "Live Radio"
            : "Watch Party media";
    }
}

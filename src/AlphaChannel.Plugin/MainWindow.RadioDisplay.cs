namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string GetRadioDisplayTitle(string? title)
    {
        var url = video.CurrentMediaUrl;
        var useful = !string.IsNullOrWhiteSpace(title) && !WatchPartyMediaDisplay.IsInternalAudioTitle(title) &&
            !title.Contains("://", StringComparison.Ordinal) && title != "Watch Party media";
        if (useful) return title!;

        // Overlay metadata belongs to the original station URL, not the player's temporary audio URL.
        if (url is not null && string.Equals(url, video.LastDisplayMediaUrl, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(video.LastDisplayMediaTitle) &&
            !WatchPartyMediaDisplay.IsInternalAudioTitle(video.LastDisplayMediaTitle))
            return WatchPartyMediaDisplay.Title(url, video.LastDisplayMediaTitle, true);

        var stationUrl = url?.Split('#')[0];
        var station = GetHomeRadioChoices().FirstOrDefault(s =>
            string.Equals(s.Url.TrimEnd('/'), stationUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        return WatchPartyMediaDisplay.Title(url, station.Name, true);
    }
}

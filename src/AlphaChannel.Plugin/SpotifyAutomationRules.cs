namespace AlphaChannel.Plugin;

internal sealed record SpotifyAutomationControl(string Name, bool Enabled, bool Offscreen, bool Invokable);

internal static class SpotifyAutomationRules
{
    internal static bool CollectionPage(string page, string title, string kind) => title.Length > 0 &&
        kind is "album" or "playlist" && page.EndsWith("| Spotify", StringComparison.OrdinalIgnoreCase) &&
        (page.StartsWith(title + " - " + kind, StringComparison.OrdinalIgnoreCase) ||
         page.StartsWith("Listen to " + title + " - " + kind, StringComparison.OrdinalIgnoreCase));

    internal static string FirstRowSong(string row, string button)
    {
        if (!row.StartsWith("1 Play ", StringComparison.Ordinal) || !button.StartsWith("Play ", StringComparison.Ordinal) ||
            !(row == "1 " + button || row.StartsWith("1 " + button + " ", StringComparison.Ordinal))) return "";
        var artistAt = button.LastIndexOf(" by ", StringComparison.Ordinal);
        return artistAt > 5 ? button[5..artistAt] : "";
    }

    internal static bool TargetPage(string page, string title) => title.Length > 0 &&
        (page.StartsWith(title + " - song", StringComparison.OrdinalIgnoreCase) && page.EndsWith("| Spotify", StringComparison.OrdinalIgnoreCase) ||
         page.StartsWith(title + " • ", StringComparison.OrdinalIgnoreCase) && page.Length > title.Length + 3);

    // Never use a bare Play button: that can resume the previous song.
    internal static int Select(IReadOnlyList<SpotifyAutomationControl> controls, string title)
    {
        var exact = Enumerable.Range(0, controls.Count).Where(i => Eligible(controls[i]) &&
            controls[i].Name.Equals("Play " + title, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length == 1) return exact[0];
        if (exact.Length > 1) return -1;
        var named = Enumerable.Range(0, controls.Count).Where(i => Eligible(controls[i]) &&
            controls[i].Name.StartsWith("Play " + title + " by ", StringComparison.OrdinalIgnoreCase)).ToArray();
        return named.Length == 1 ? named[0] : -1;
    }
    private static bool Eligible(SpotifyAutomationControl control) => control.Enabled && !control.Offscreen && control.Invokable;
}

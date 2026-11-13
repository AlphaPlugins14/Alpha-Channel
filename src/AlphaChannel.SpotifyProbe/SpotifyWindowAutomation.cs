using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using System.Runtime.InteropServices;
using AlphaChannel.Plugin;
using Windows.Media.Control;

internal static class SpotifyWindowAutomation
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint ShellExecuteW(nint window, string operation, string file,
        string? parameters, string? directory, int showCommand);

    internal static async Task<string> PlayAsync(string? input, string? selectedSession, Dictionary<string, string> fields)
    {
        fields["Automation / Verified playback"] = "False";
        if (!SpotifyLinks.TryParse(input, out var uri))
            throw new InvalidOperationException("Choose a full Spotify song, album or playlist URL.");
        var kind = SpotifyLinks.Kind(uri);
        var collection = kind != "track";
        fields["Automation / Requested URI"] = uri;
        var title = await SpotifyLinkMetadata.TitleAsync(uri);
        var collectionTitle = title;
        fields["Automation / Expected title"] = title;
        _ = Window(); // Require an existing, unambiguous Spotify desktop window.
        await using var focus = new SpotifyFocusRestore(fields);
        // ProcessStartInfo has no non-activating show mode. Pass the shell's
        // SW_SHOWNOACTIVATE hint directly; Spotify can still choose to activate.
        var opened = ShellExecuteW(0, "open", uri, null, null, 4);
        if (opened.ToInt64() <= 32) throw new IOException("Could not open the Spotify song (shell error " + opened + ").");
        fields["Automation / Navigation"] = "Background requested (SW_SHOWNOACTIVATE); Spotify may ignore this request";
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        async Task<bool> ConfirmPlayback()
        {
            var session = manager.GetSessions().FirstOrDefault(s => s.SourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(selectedSession) || s.SourceAppUserModelId == selectedSession));
            if (session is null) return false;
            var track = await session.TryGetMediaPropertiesAsync();
            fields["Automation / Observed title"] = track.Title;
            fields["Automation / Observed playback"] = session.GetPlaybackInfo().PlaybackStatus.ToString();
            return track.Title.Equals(title, StringComparison.OrdinalIgnoreCase) &&
                session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(12))
        {
            await Task.Delay(350);
            try
            {
            var elements = Elements(Window());
            AutomationElement? firstSong = null;
            if (collection)
            {
                var collectionPage = elements.FirstOrDefault(e => e.Current.ControlType == ControlType.Document &&
                    e.Current.AutomationId == "RootWebArea" && SpotifyAutomationRules.CollectionPage(e.Current.Name, collectionTitle, kind));
                if (collectionPage is null) continue;
                fields["Automation / Matched collection"] = collectionPage.Current.Name;
                var firstRows = elements.Where(e => e.Current.ControlType == ControlType.DataItem &&
                    !e.Current.IsOffscreen && e.Current.Name.StartsWith("1 Play ", StringComparison.Ordinal)).ToArray();
                fields["Automation / First rows found"] = firstRows.Length.ToString();
                // Spotify can expose mirrored copies of the same song list.
                // Accept identical first-row labels, but never different songs.
                if (firstRows.Length == 0) continue;
                var candidates = firstRows.SelectMany(row => Elements(row).Where(e => e.Current.ControlType == ControlType.Button &&
                    !e.Current.IsOffscreen && e.Current.IsEnabled &&
                    SpotifyAutomationRules.FirstRowSong(row.Current.Name, e.Current.Name).Length > 0 &&
                    e.TryGetCurrentPattern(InvokePattern.Pattern, out _))).ToArray();
                fields["Automation / First-row controls found"] = candidates.Length.ToString();
                if (candidates.Length == 0 || candidates.Select(e => e.Current.Name).Distinct(StringComparer.Ordinal).Count() != 1) continue;
                firstSong = candidates[0];
                title = SpotifyAutomationRules.FirstRowSong("1 " + firstSong.Current.Name, firstSong.Current.Name);
                fields["Automation / Expected title"] = title;
            }
            var page = elements.FirstOrDefault(e => e.Current.ControlType == ControlType.Document &&
                e.Current.AutomationId == "RootWebArea" && SpotifyAutomationRules.TargetPage(e.Current.Name, title));
            var spotifyPage = page ?? elements.FirstOrDefault(e => e.Current.ControlType == ControlType.Document &&
                e.Current.AutomationId == "RootWebArea" && e.Current.Name.EndsWith("| Spotify", StringComparison.OrdinalIgnoreCase));
            if (spotifyPage is null) continue;
            fields["Automation / Matched page"] = spotifyPage.Current.Name;
            if (!collection && page is not null && await ConfirmPlayback())
            {
                fields["Automation / Verified playback"] = "True";
                fields["Automation / Action"] = "Requested song already playing after opening its page; no click needed";
                return "Spotify is playing the requested song.";
            }
            var buttons = firstSong is not null ? new[] { firstSong } : elements.Where(e => e.Current.ControlType == ControlType.Button).ToArray();
            var controls = buttons.Select(e => new SpotifyAutomationControl(e.Current.Name, e.Current.IsEnabled,
                e.Current.IsOffscreen, e.TryGetCurrentPattern(InvokePattern.Pattern, out _))).ToArray();
            // Track links can open an album. There require an explicit song-and-artist row label.
            if (page is null)
                controls = controls.Select(c => c.Name.StartsWith("Play " + title + " by ", StringComparison.OrdinalIgnoreCase)
                    ? c : c with { Enabled = false }).ToArray();
            var choice = SpotifyAutomationRules.Select(controls, title);
            if (choice < 0) continue;
            var target = buttons[choice];
            // Recheck the button immediately before invoking its accessibility action.
            var current = target.Current;
            if (current.IsOffscreen || !current.IsEnabled || current.Name != controls[choice].Name ||
                !target.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) continue;
            fields["Automation / Invoked button"] = current.Name;
            ((InvokePattern)invoke).Invoke();
            for (var retry = 0; retry < 12; retry++)
            {
                await Task.Delay(300);
                if (await ConfirmPlayback())
                {
                    fields["Automation / Verified playback"] = "True";
                    return "Spotify started the requested song using its named Play button.";
                }
            }
            return "Spotify's song Play button was invoked, but the requested playback was not confirmed. Check Spotify.";
            }
            catch (ElementNotAvailableException) { /* Navigation replaced the accessibility tree. Retry the fresh page. */ }
        }
        return "No unique, visible Play button for the requested song was found. Keep Spotify open on the song page and try again.";
    }

    internal static AutomationElement Window()
    {
        var candidates = Process.GetProcessesByName("Spotify");
        try
        {
            var windows = candidates.Where(p => p.MainWindowHandle != 0).ToArray();
            if (windows.Length != 1) throw new InvalidOperationException("Open one Spotify desktop window on a song page, then try again.");
            return AutomationElement.FromHandle(windows[0].MainWindowHandle);
        }
        finally { foreach (var process in candidates) process.Dispose(); }
    }

    internal static List<AutomationElement> Elements(AutomationElement window)
    {
        var result = new List<AutomationElement>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((window, 0));
        var clock = Stopwatch.StartNew();
        var walker = TreeWalker.RawViewWalker;
        var spotifyProcesses = Process.GetProcessesByName("Spotify");
        var allowed = spotifyProcesses.Select(p => p.Id).ToHashSet();
        foreach (var process in spotifyProcesses) process.Dispose();
        while (queue.Count > 0 && result.Count < 2000 && clock.Elapsed.TotalSeconds < 6)
        {
            var (element, depth) = queue.Dequeue();
            try
            {
                if (!allowed.Contains(element.Current.ProcessId) ||
                    !visited.Add(string.Join(",", element.GetRuntimeId()))) continue;
            }
            catch (ElementNotAvailableException) { continue; }
            result.Add(element);
            if (depth >= 40) continue;
            try
            {
                var child = walker.GetFirstChild(element);
                while (child is not null && queue.Count + result.Count < 2000 && clock.Elapsed.TotalSeconds < 6)
                { queue.Enqueue((child, depth + 1)); child = walker.GetNextSibling(child); }
            }
            catch (ElementNotAvailableException) { }
        }
        return result;
    }

}

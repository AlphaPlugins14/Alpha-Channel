using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string spotifyLibrarySearch = "", spotifyLinkInput = "";
    private string? spotifyLibraryMessage;
    private string spotifyRenameUri = "", spotifyRenameName = "";
    private bool spotifyAutomationPending;
    private long spotifyAutomationBaseline;
    private string spotifyAutomationUri = "", spotifyPlayingUri = "";
    private Task<string>? spotifyMetadataRequest;
    private string spotifyMetadataUri = "";
    private bool spotifyLibraryOpen;

    private IDisposable SpotifyLibraryColors() => ImRaii.PushColor(ImGuiCol.FrameBg, Hex(0x1B2520))
        .Push(ImGuiCol.FrameBgHovered, Hex(0x26382C)).Push(ImGuiCol.FrameBgActive, Hex(0x304B39))
        .Push(ImGuiCol.Button, Hex(0x213B2A)).Push(ImGuiCol.ButtonHovered, Hex(0x2C553A))
        .Push(ImGuiCol.ButtonActive, Hex(0x356845)).Push(ImGuiCol.Border, Hex(0x365740))
        .Push(ImGuiCol.CheckMark, SpotifyGreen).Push(ImGuiCol.HeaderHovered, Hex(0x263E2E))
        .Push(ImGuiCol.Header, Hex(0x213B2A)).Push(ImGuiCol.PopupBg, Hex(0x111B15));

    private bool CanPlaySpotifyLink => !spotifyAutomationPending && spotifyClient?.CommandPending != true &&
        (spotifyOwnsStudio || CanStartSpotifyTv);

    private void DrawSpotifyUrlBar(float width)
    {
        using var colors = SpotifyLibraryColors();
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7));
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(10, 8));
        SpotifyPanel("links", width, Ui(128), () =>
        {
            DrawSpotifyActionHeading("Play from a link", FontAwesomeIcon.Link, true);
            ImGui.Dummy(UiVec(0, 3));
            var valid = SpotifyLinks.TryParse(spotifyLinkInput, out var uri);
            
            var available = ImGui.GetContentRegionAvail().X;
            ImGui.SetNextItemWidth(Math.Max(Ui(70), available - Ui(256)));
            var enter = ImGui.InputTextWithHint("##spotifySongUrl", "Paste a Spotify song, album or playlist URL…",
                ref spotifyLinkInput, 2049, ImGuiInputTextFlags.EnterReturnsTrue);
            // Revalidate after editing so buttons and Enter use the current text.
            valid = SpotifyLinks.TryParse(spotifyLinkInput, out uri);
            
            ImGui.SameLine();
            using (UiDisabled.Scope(!valid || !CanPlaySpotifyLink))
                if ((ImGui.Button(spotifyAutomationPending ? "Starting…" : "Play", UiVec(76, 36)) || enter) &&
                    valid && CanPlaySpotifyLink) PlaySpotifyLink(spotifyLinkInput);
            ImGui.SameLine();
            using (UiDisabled.Scope(!valid || spotifyMetadataRequest is not null))
                if (ImGui.Button("Save", UiVec(72, 36))) SaveSpotifyLink(spotifyLinkInput);
            ImGui.SameLine();
            if (ImGui.Button("Library", UiVec(84, 36))) spotifyLibraryOpen = true;
            using (UiFonts.PushRegularRelative(.86f))
            {
                var hint = spotifyLibraryMessage ?? (!string.IsNullOrWhiteSpace(spotifyLinkInput) && !valid
                    ? "Use a full open.spotify.com song link or Spotify URI."
                    : "Play a song now, or save its link for later.");
                ImGui.TextColored(MutedText, hint);
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip(hint);
            }
        });
    }

    private void DrawSpotifyLibrary(float width)
    {
        using (UiFonts.PushRegularRelative(.86f))
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextColored(MutedText, "Play saved songs, albums and playlists. Collections start from their first visible song; Spotify controls the playback order.");
            ImGui.PopTextWrapPos();
        }
        var library = Plugin.Cfg.SpotifySavedLinks ??= [];
        var saved = library.Where(s => s is not null &&
            SpotifyLinks.TryParse(s.Uri, out _)).Take(100).ToArray();
        ImGui.SetNextItemWidth(Math.Max(Ui(100), width - Ui(160)));
        ImGui.InputTextWithHint("##spotifyLibraryFilter", "Search your saved songs and artists",
            ref spotifyLibrarySearch, 201);
        ImGui.SameLine();
        using (UiDisabled.Scope(string.IsNullOrWhiteSpace(spotifyLibrarySearch) || spotifyAutomationPending))
            if (ImGui.Button("Search in Spotify", UiVec(150, 30)))
                OpenSpotifyDestination(SpotifyLinks.SearchUri(spotifyLibrarySearch));

        var filtered = saved.Where(s => s.Name.Contains(spotifyLibrarySearch, StringComparison.OrdinalIgnoreCase) ||
            s.Title.Contains(spotifyLibrarySearch, StringComparison.OrdinalIgnoreCase) ||
            s.Artist.Contains(spotifyLibrarySearch, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (filtered.Length == 0)
            ImGui.TextDisabled(saved.Length == 0 ? "Save a song link above to start your library." : "No saved links match your search.");
        var player = spotifyClient is null ? null : new SpotifyPlayback(spotifyClient.Snapshot);
        foreach (var link in filtered)
        {
            using var id = ImRaii.PushId(link.Uri);
            var origin = ImGui.GetCursorScreenPos();
            var track = SpotifyLinks.Kind(link.Uri) == "track";
            var selected = track && link.Uri == spotifyPlayingUri && player?.Playing == true &&
                !string.IsNullOrEmpty(link.Artist) &&
                player.Text("Track / Title") == (link.Title.Length > 0 ? link.Title : link.Name) &&
                player.Text("Track / Artist") == link.Artist;
            using (UiFonts.PushRelative(1.02f, emphasis: true))
                ImGui.TextColored(selected ? SpotifyGreen : Hex(0xE8EDEA),
                    TruncateToWidth(SpotifyLinks.Clean(link.Name), Math.Max(Ui(60), width - Ui(126))));
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip(link.Name + "\n" + link.Uri);
            using (UiFonts.PushRegularRelative(.85f))
                ImGui.TextColored(MutedText, TruncateToWidth(link.Artist.Length > 0 ? link.Artist :
                    track ? "Song" : SpotifyLinks.Kind(link.Uri) == "album" ? "Album" : "Playlist",
                    Math.Max(Ui(60), width - Ui(126))));
            var afterText = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(origin + new Vector2(Math.Max(Ui(70), width - Ui(118)), Ui(5)));
            using (UiDisabled.Scope(!CanPlaySpotifyLink))
                if (ImGui.Button("Play", UiVec(70, 30)))
                {
                    PlaySpotifyLink(link.Uri);
                }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                UiFonts.SetTooltip(track ? "Play this saved song" : "Start this " + SpotifyLinks.Kind(link.Uri) + " in Spotify.");
            ImGui.SameLine(0, Ui(6));
            if (ImGui.Button("…", UiVec(34, 30))) ImGui.OpenPopup("linkMenu");
            if (ImGui.BeginPopup("linkMenu"))
            {
                using (UiDisabled.Scope(spotifyAutomationPending))
                    if (ImGui.MenuItem("Open in Spotify")) OpenSpotifyDestination(link.Uri);
                if (ImGui.MenuItem("Rename")) { spotifyRenameUri = link.Uri; spotifyRenameName = link.Name; }
                if (ImGui.MenuItem("Remove"))
                {
                    library.RemoveAll(s => s is null || s.Uri == link.Uri);
                    Plugin.Cfg.Save();
                    if (spotifyRenameUri == link.Uri) spotifyRenameUri = "";
                }
                ImGui.EndPopup();
            }
            ImGui.SetCursorScreenPos(new Vector2(origin.X, Math.Max(afterText.Y, origin.Y + Ui(44))));
            ImGui.Separator();
            ImGui.Spacing();
        }
        if (spotifyRenameUri.Length > 0)
        {
            ImGui.TextUnformatted("Rename saved link");
            ImGui.SetNextItemWidth(Math.Max(Ui(100), width - Ui(155)));
            var enter = ImGui.InputText("##spotifyRename", ref spotifyRenameName, 257, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            if (ImGui.Button("Save name") || enter)
            {
                var name = SpotifyLinks.Clean(spotifyRenameName).Trim();
                var index = library.FindIndex(s => s.Uri == spotifyRenameUri);
                if (name.Length > 0 && index >= 0)
                {
                    library[index] = library[index] with { Name = name, CustomName = true };
                    Plugin.Cfg.Save();
                    spotifyRenameUri = "";
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) spotifyRenameUri = "";
        }
    }

    private void DrawSpotifyLibraryOverlay()
    {
        if (!spotifyLibraryOpen) return;
        var parent = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var size = Vector2.Min(UiVec(700, 590), parentSize - UiVec(32, 32));
        ImGui.SetNextWindowPos(parent);
        ImGui.SetNextWindowSize(parentSize);
        ImGui.SetNextWindowBgAlpha(0);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (!BeginWindowOverlay("##spotifyLibraryOverlay", flags)) { ImGui.End(); return; }
        ImGui.GetWindowDrawList().AddRectFilled(parent, parent + parentSize,
            ImGui.GetColorU32(new Vector4(0, 0, 0, .58f)));
        ImGui.SetCursorScreenPos(parent + (parentSize - size) * .5f);
        using (SpotifyLibraryColors())
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14)))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7)))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(24, 20)))
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8, 10)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, Hex(0x111917)).Push(ImGuiCol.Border, SpotifyGreen))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##spotifyLibraryPanel", size, true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (panel)
            {
                var available = ImGui.GetContentRegionAvail().X;
                var heading = ImGui.GetCursorScreenPos();
                using (UiFonts.PushRelative(1.35f, emphasis: true)) ImGui.TextColored(SpotifyGreen, "Your library");
                ImGui.SetCursorScreenPos(heading + new Vector2(Math.Max(0, available - Ui(34)), 0));
                using (ImRaii.PushId("spotifyLibraryClose"))
                    if (GameLayoutButton(string.Empty, FontAwesomeIcon.Times, Ui(34), height: 30)) spotifyLibraryOpen = false;
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Close library");
                ImGui.SetCursorScreenPos(heading + UiVec(0, 40));
                using (UiFonts.PushRegularRelative(.9f)) ImGui.TextColored(MutedText, "Your saved Spotify links in Alpha Channel.");
                ImGui.Separator();
                using var list = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##spotifyLibraryList", Vector2.Zero, false));
                if (list) DrawSpotifyLibrary(ImGui.GetContentRegionAvail().X);
            }
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape) && !ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId)) spotifyLibraryOpen = false;
        ImGui.End();
    }

    private void SaveSpotifyLink(string input)
    {
        if (!SpotifyLinks.TryParse(input, out var uri)) return;
        Plugin.Cfg.SpotifySavedLinks ??= [];
        if (Plugin.Cfg.SpotifySavedLinks.Any(s => s is not null &&
            SpotifyLinks.TryParse(s.Uri, out var previousUri) && previousUri == uri))
        { spotifyLibraryMessage = "Already in your library."; return; }
        if (Plugin.Cfg.SpotifySavedLinks.Count >= 100)
        { spotifyLibraryMessage = "Your library has reached 100 saved links."; return; }
        Plugin.Cfg.SpotifySavedLinks.Add(new(uri, SpotifyLinks.Kind(uri) + " · " + uri.Split(':')[2]));
        Plugin.Cfg.Save();
        spotifyLibraryMessage = "Saved. Fetching title…";
        spotifyMetadataUri = uri;
        spotifyMetadataRequest = Task.Run(() => SpotifyLinkMetadata.TitleAsync(uri));
    }

    private void OpenSpotifyDestination(string uri)
    {
        try { Dalamud.Utility.Util.OpenLink(uri); }
        catch { spotifyLibraryMessage = "Could not open Spotify. Check that the desktop app is installed."; }
    }

    private void PlaySpotifyLink(string input)
    {
        if (!CanPlaySpotifyLink || !SpotifyLinks.TryParse(input, out var uri) ||
            spotifyClient is null) return;
        spotifyAutomationBaseline = spotifyClient.CompletedCommands;
        if (!spotifyClient.SendCommand("play-link", spotifyUri: uri)) return;
        spotifyAutomationUri = uri;
        spotifyAutomationPending = true;
        spotifyLibraryMessage = "Starting song…";
    }

    internal void UpdateSpotifyAutomation()
    {
        if (spotifyMetadataRequest is { IsCompleted: true } metadata)
        {
            spotifyMetadataRequest = null;
            try
            {
                var title = metadata.GetAwaiter().GetResult();
                var index = Plugin.Cfg.SpotifySavedLinks.FindIndex(s => s.Uri == spotifyMetadataUri);
                if (index >= 0)
                {
                    var link = Plugin.Cfg.SpotifySavedLinks[index];
                    var current = spotifyClient is null ? null : new SpotifyPlayback(spotifyClient.Snapshot);
                    var savedArtist = spotifyPlayingUri == spotifyMetadataUri && current?.Text("Track / Title") == title
                        ? current.Text("Track / Artist") : link.Artist;
                    Plugin.Cfg.SpotifySavedLinks[index] = link with
                    { Title = title, Name = link.CustomName ? link.Name : title, Artist = savedArtist };
                    Plugin.Cfg.Save();
                    spotifyLibraryMessage = "Saved to your library.";
                }
            }
            catch { spotifyLibraryMessage = "Saved. Title unavailable; you can rename it in your library."; }
        }
        if (!spotifyAutomationPending || spotifyClient is null || spotifyClient.CommandPending ||
            spotifyClient.CompletedCommands <= spotifyAutomationBaseline) return;
        spotifyAutomationPending = false;
        var result = spotifyClient.LastCommandSnapshot;
        if (result is null) return;
        var verified = result.Fields.GetValueOrDefault("Automation / Requested URI") == spotifyAutomationUri &&
            result.Fields.GetValueOrDefault("Automation / Verified playback") == "True";
        spotifyLibraryMessage = verified ? "Playing in Spotify." : result.Status;
        if (!verified) return;
        spotifyPlayingUri = spotifyAutomationUri;
        var player = new SpotifyPlayback(result);
        var titleNow = player.Text("Track / Title");
        var artist = player.Text("Track / Artist");
        var expected = result.Fields.GetValueOrDefault("Automation / Expected title");
        var savedIndex = Plugin.Cfg.SpotifySavedLinks?.FindIndex(s => s.Uri == spotifyPlayingUri) ?? -1;
        if (savedIndex >= 0 && SpotifyLinks.Kind(spotifyPlayingUri) == "track" && titleNow == expected)
        {
            var link = Plugin.Cfg.SpotifySavedLinks![savedIndex];
            Plugin.Cfg.SpotifySavedLinks[savedIndex] = link with
            { Title = titleNow, Name = link.CustomName ? link.Name : titleNow, Artist = artist };
            Plugin.Cfg.Save();
        }
        if (!spotifyOwnsStudio && CanStartSpotifyTv) StartSpotifySession(share: false);
    }
}

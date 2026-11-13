using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly string[] RadioGenres =
    [
        "Pop", "Rock", "Indie", "Dance", "EDM", "House", "Trance",
        "Synthwave", "Chillout", "Ambient", "Jazz", "Classical", "Hip hop",
        "R&B", "Country", "Metal", "Reggae", "Disco", "80s", "90s", "Other"
    ];
    private string musicGenreFilter = "";
    private readonly HashSet<string> djStationGenreDraft = new(StringComparer.OrdinalIgnoreCase);

    private void DrawMusicRadioPage(float width)
    {
        var inlineGenre = width >= Ui(640);
        GameLayoutPanel("##musicRadioToolbar", width, Ui(inlineGenre ? 118 : 160), () =>
        {
            var available = ImGui.GetContentRegionAvail().X;
            var searchWidth = available - Ui(160);
            ImGui.SetNextItemWidth(Math.Max(Ui(80), searchWidth));
            using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(12, 9)))
                ImGui.InputTextWithHint("##musicSearch", "Search stations...", ref musicSearch, 100);
            ImGui.SameLine(0, Ui(10));
            if (GameLayoutButton("Add station", FontAwesomeIcon.Plus, Ui(150), true))
                OpenMusicStationEditor(null);
            DrawMusicRadioFilters();
            if (inlineGenre) ImGui.SameLine(0, Ui(16));
            ImGui.SetNextItemWidth(inlineGenre ? Math.Min(Ui(230), ImGui.GetContentRegionAvail().X) : -1);
            using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(10, 9)))
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##musicGenreFilter", musicGenreFilter.Length == 0 ? "All genres" : musicGenreFilter)))
            {
                if (ImGui.Selectable("All genres", musicGenreFilter.Length == 0)) musicGenreFilter = "";
                foreach (var genre in RadioGenres)
                    if (ImGui.Selectable(genre, musicGenreFilter == genre)) musicGenreFilter = genre;
                if (ImGui.Selectable("Uncategorised", musicGenreFilter == "Uncategorised")) musicGenreFilter = "Uncategorised";
                ImGui.EndCombo();
            }
        });
        SetGameLayoutRowGap(Ui(14));

        if (musicRadioFilter != 2)
        {
            var stations = SuggestedRadioStations.Where(s => MusicStationMatches(s.Name, s.Description, s.Genres)).ToArray();
            DrawMusicStationPanel("##musicRecommended", "Recommended stations", FontAwesomeIcon.Music,
                width, stations.Length, (index, cardWidth) =>
                {
                    var station = stations[index];
                    DrawMusicStationRow(station.Name, station.Description, station.Url, null, cardWidth);
                }, "No recommended stations match your search and genre filter.");
            SetGameLayoutRowGap(Ui(14));
        }
        if (musicRadioFilter != 1)
        {
            // Keep the original saved index even when the search hides earlier entries.
            var indices = Enumerable.Range(0, djSavedStations.Count)
                .Where(i => MusicStationMatches(djSavedStations[i].Name, SavedStationGenreSummary(djSavedStations[i]), djSavedStations[i].Genres)).ToArray();
            DrawMusicStationPanel("##musicSavedStations", "My stations", FontAwesomeIcon.FolderOpen,
                width, indices.Length, (position, cardWidth) =>
                {
                    var index = indices[position];
                    var station = djSavedStations[index];
                    DrawMusicStationRow(station.Name, SavedStationGenreSummary(station), station.Url, index, cardWidth);
                }, djSavedStations.Count == 0 ? "Save your own direct audio streams with Add station." :
                    "No saved stations match your search and genre filter.");
        }
    }

    private void DrawMusicRadioFilters()
    {
        var labels = new[] { "All", "Recommended", "My stations" };
        var widths = new[] { 70f, 130f, 120f };
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(8));
        using var border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, Ui(1));
        for (var i = 0; i < labels.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, Ui(8));
            using var colors = ImRaii.PushColor(ImGuiCol.Button, musicRadioFilter == i ? Accent : FrameBg)
                .Push(ImGuiCol.ButtonHovered, musicRadioFilter == i ? AccentHover : CardBgHover)
                .Push(ImGuiCol.ButtonActive, AccentActive).Push(ImGuiCol.Border, BorderSubtle);
            if (ImGui.Button(labels[i] + "##musicFilter", UiVec(widths[i], 36))) musicRadioFilter = i;
        }
    }

    private void DrawMusicStationPanel(string id, string title, FontAwesomeIcon icon, float width,
        int count, Action<int, float> drawStation, string emptyText)
    {
        // Allow room for each card's text, play buttons and saved-station menu.
        var gap = Ui(12);
        var columns = width - Ui(32) >= Ui(480) * 2 + gap ? 2 : 1;
        var rowCount = (count + columns - 1) / columns;
        var rowHeight = Math.Max(Ui(60), ImGui.GetTextLineHeight() * 2 + Ui(20));
        var height = Ui(36 + 10) + ImGui.GetTextLineHeight() + ImGui.GetStyle().ItemSpacing.Y * 2
            + (count == 0 ? Ui(42) : rowCount * (rowHeight + Ui(6)));
        GameLayoutPanel(id, width, height, () =>
        {
            using (UiFonts.PushRelative(1f, emphasis: true))
            using (ImRaii.PushColor(ImGuiCol.Text, Hex(0xE2E7EF)))
                GameLayoutHeading(title, icon);
            if (count == 0)
            {
                using var muted = ImRaii.PushColor(ImGuiCol.Text, MutedText);
                ImGui.TextWrapped(emptyText);
            }
            else
            {
                var start = ImGui.GetCursorScreenPos();
                var available = ImGui.GetContentRegionAvail().X;
                var cardWidth = (available - gap * (columns - 1)) / columns;
                for (var i = 0; i < count; i++)
                {
                    ImGui.SetCursorScreenPos(start + new Vector2(
                        i % columns * (cardWidth + gap), i / columns * (rowHeight + Ui(6))));
                    drawStation(i, cardWidth);
                }
                ImGui.SetCursorScreenPos(start);
                ImGui.Dummy(new Vector2(available, rowCount * (rowHeight + Ui(6))));
            }
        });
    }

    private bool MusicStationMatches(string name, string description, IReadOnlyCollection<string>? genres) =>
        (musicGenreFilter.Length == 0 ||
            (musicGenreFilter == "Uncategorised" ? genres is null || genres.Count == 0 :
                genres?.Contains(musicGenreFilter, StringComparer.OrdinalIgnoreCase) == true)) &&
        (name.Contains(musicSearch.Trim(), StringComparison.OrdinalIgnoreCase) ||
            description.Contains(musicSearch.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string SavedStationGenreSummary(SavedRadioStationRecord station) =>
        station.Genres is { Count: > 0 } ? string.Join(" • ", station.Genres) : "Saved station • Uncategorised";

    private void DrawMusicStationGenres()
    {
        ImGui.TextColored(MutedText, "Music genres (choose one or more)");
        if (!ImGui.BeginTable("##stationGenres", 3)) return;
        foreach (var genre in RadioGenres)
        {
            ImGui.TableNextColumn();
            var selected = djStationGenreDraft.Contains(genre);
            if (ImGui.Checkbox(genre, ref selected))
            {
                if (selected) djStationGenreDraft.Add(genre);
                else djStationGenreDraft.Remove(genre);
            }
        }
        ImGui.EndTable();
    }

    private void DrawMusicStationRow(string name, string description, string url, int? customIndex, float? cardWidth = null)
    {
        using var id = ImRaii.PushId(customIndex is { } index ? "saved" + index : url);
        var active = video.IsAudioOnly && AudioVisualizerSelection.IsSameMedia(video.CurrentMediaUrl, url)
            && video.State is VideoPlaybackState.Playing or VideoPlaybackState.Paused;
        var start = ImGui.GetCursorScreenPos();
        var width = cardWidth ?? ImGui.GetContentRegionAvail().X;
        var height = Math.Max(Ui(60), ImGui.GetTextLineHeight() * 2 + Ui(20));
        var draw = ImGui.GetWindowDrawList();
        var fill = active ? new Vector4(Accent.X, Accent.Y, Accent.Z, .10f) : FrameBg;
        draw.AddRectFilled(start, start + new Vector2(width, height), ImGui.GetColorU32(fill), Ui(9));
        draw.AddRect(start, start + new Vector2(width, height), ImGui.GetColorU32(active ? Accent : BorderSubtle),
            Ui(9), ImDrawFlags.None, Ui(active ? 1.5f : 1));

        var tile = start + UiVec(8, 8);
        var tileSize = height - Ui(16);
        draw.AddRectFilled(tile, tile + new Vector2(tileSize), ImGui.GetColorU32(Accent), Ui(7));
        using (UiFonts.PushIcon())
        {
            var note = FontAwesomeIcon.Music.ToIconString();
            var size = ImGui.CalcTextSize(note);
            draw.AddText(tile + (new Vector2(tileSize) - size) / 2, ImGui.GetColorU32(Vector4.One), note);
        }

        var compact = width < Ui(650);
        var playWidth = Ui(compact ? 72 : 96);
        var partyWidth = Ui(compact ? 122 : 146);
        var actionsWidth = playWidth + partyWidth + Ui(8) + (customIndex.HasValue ? Ui(38) : 0);
        var actionsX = start.X + width - Ui(10) - actionsWidth;
        var textX = tile.X + tileSize + Ui(14);
        var textWidth = Math.Max(0, actionsX - Ui(12) - textX);
        var lineHeight = ImGui.GetTextLineHeight();
        float descriptionHeight;
        using (UiFonts.PushRelative(0.90f)) descriptionHeight = ImGui.GetTextLineHeight();
        var titleY = start.Y + (height - lineHeight - descriptionHeight - Ui(4)) / 2;
        draw.PushClipRect(new Vector2(textX, start.Y), new Vector2(actionsX - Ui(10), start.Y + height), true);
        var badge = video.State == VideoPlaybackState.Paused ? "Paused" : "Playing";
        var badgeWidth = ImGui.CalcTextSize(badge).X + Ui(14);
        var titleWidth = Math.Max(0, textWidth - (active ? badgeWidth + Ui(10) : 0));
        var title = TruncateToWidth(name, titleWidth);
        draw.AddText(new Vector2(textX, titleY), ImGui.GetColorU32(Hex(0xE2E7EF)), title);
        if (active)
        {
            var badgeStart = new Vector2(textX + ImGui.CalcTextSize(title).X + Ui(10), titleY - Ui(2));
            draw.AddRectFilled(badgeStart, badgeStart + new Vector2(badgeWidth, lineHeight + Ui(4)), ImGui.GetColorU32(Accent), Ui(5));
            draw.AddText(badgeStart + UiVec(7, 2), ImGui.GetColorU32(Vector4.One), badge);
        }
        using (UiFonts.PushRelative(0.90f))
            draw.AddText(new Vector2(textX, titleY + lineHeight + Ui(4)), ImGui.GetColorU32(MutedText), TruncateToWidth(description, textWidth));
        draw.PopClipRect();

        ImGui.SetCursorScreenPos(new Vector2(actionsX, start.Y + (height - Ui(34)) / 2));
        using (UiDisabled.Scope(stream.Mode == StreamMode.Viewing))
        {
            if (GameLayoutButton("Play", FontAwesomeIcon.Play, playWidth, height: 34)) PlayDjUrl(url, name, "Radio");
            ImGui.SameLine(0, Ui(8));
            if (GameLayoutButton("Play in Party", FontAwesomeIcon.Users, partyWidth, true, height: 34))
                BeginRadioStationWatchParty(url, name);
        }
        if (customIndex.HasValue)
        {
            ImGui.SameLine(0, Ui(8));
            if (GameLayoutButton("", FontAwesomeIcon.EllipsisH, Ui(30), height: 34))
            {
                musicSavedIndex = customIndex.Value;
                OpenMusicDialog("stationActions");
            }
        }
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(width, height));
        SetGameLayoutRowGap(Ui(6));
    }
}

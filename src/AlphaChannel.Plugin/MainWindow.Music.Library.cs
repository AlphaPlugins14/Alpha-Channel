using System.Collections.Concurrent;
using AlphaChannel.BroadcastStudio;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool musicLibraryBusy;
    private string musicLibrarySearch = "", musicLibraryRename = "";
    private string? musicLibraryError, musicLibraryActionId;
    private readonly HashSet<string> musicLibrarySelection = [];
    private readonly ConcurrentQueue<Action> musicLibraryCompletions = new();
    private int? musicLibraryLoadDeck;
    private void OpenMusicLibrary(bool picker, int? deck = null)
    {
        musicLibraryLoadDeck = deck;
        musicLibrarySearch = ""; musicLibrarySelection.Clear(); musicLibraryActionId = null;
        OpenMusicDialog(picker ? "studioPickSongs" : "studioLibrary");
    }
    private Action<bool, T> MusicLibraryBrowseCallback<T>(Action<bool, T> callback)
    {
        // The library's overlay claims focus every frame. Suspend it while the
        // file browser owns input, then restore the same library view.
        var returnTo = musicDialog ?? "studioLibrary";
        CloseMusicDialog();
        return (success, value) =>
        {
            try { callback(success, value); }
            finally
            {
                if (Volatile.Read(ref disposeStarted) == 0 && stream.Mode != StreamMode.Viewing && activeGameDialog == null)
                    OpenMusicDialog(returnTo);
            }
        };
    }
    private void ChooseMusicLibraryFolder()
    {
        studioFileDialog.OpenFolderDialog("Choose music library folder", MusicLibraryBrowseCallback<string>((success, path) =>
        {
            if (!success || musicLibraryBusy) return;
            var sources = Plugin.Cfg.MusicLibrary.Concat(Plugin.Cfg.StudioQueue)
                .Concat(Plugin.Cfg.StudioPlaylists.SelectMany(p => p.Tracks)).DistinctBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            RunMusicLibraryImport(path, sources, true);
        }));
    }
    private void ImportMusicFiles()
    {
        studioFileDialog.OpenFileDialog("Import songs into music library", "Audio{.mp3,.wav,.flac,.ogg,.opus,.m4a,.aac,.aiff,.wma},.*",
            MusicLibraryBrowseCallback<List<string>>((success, paths) =>
            {
                if (!success || musicLibraryBusy) return;
                RunMusicLibraryImport(Plugin.Cfg.MusicLibraryFolder, paths.Select(p =>
                    new StudioTrack(Guid.NewGuid().ToString("N"), p, Path.GetFileNameWithoutExtension(p))).ToArray(), false);
            }), 0);
    }
    private void ImportMusicFolder()
    {
        studioFileDialog.OpenFolderDialog("Import songs from folder", MusicLibraryBrowseCallback<string>((success, path) =>
        {
            if (!success || musicLibraryBusy) return;
            try
            {
                var songs = Directory.EnumerateFiles(path).Where(p => MusicLibraryStorage.Extensions.Contains(Path.GetExtension(p)))
                    .Select(p => new StudioTrack(Guid.NewGuid().ToString("N"), p, Path.GetFileNameWithoutExtension(p))).ToArray();
                RunMusicLibraryImport(Plugin.Cfg.MusicLibraryFolder, songs, false);
            }
            catch { musicLibraryError = "The source folder could not be read."; }
        }));
    }
    private void RunMusicLibraryImport(string folder, StudioTrack[] sources, bool changeFolder)
    {
        if (musicLibraryBusy || string.IsNullOrWhiteSpace(folder)) return;
        musicLibraryBusy = true; musicLibraryError = null;
        var known = Plugin.Cfg.MusicLibrary.ToArray();
        _ = Task.Run(() =>
        {
            try
            {
                var storage = new MusicLibraryStorage(folder);
                var entries = storage.Scan(known); var replacements = new Dictionary<string, StudioTrack>(StringComparer.OrdinalIgnoreCase);
                var failures = 0;
                foreach (var source in sources)
                {
                    try
                    {
                        var imported = storage.Import(source);
                        var existing = entries.FirstOrDefault(t => MusicLibraryStorage.SamePath(t.Path, imported.Path));
                        if (existing == null) { entries.Add(imported); existing = imported; }
                        replacements[Path.GetFullPath(source.Path)] = existing;
                    }
                    catch { failures++; }
                }
                musicLibraryCompletions.Enqueue(() =>
                {
                    if (changeFolder) Plugin.Cfg.MusicLibraryFolder = storage.Root;
                    Plugin.Cfg.MusicLibrary = entries.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
                    StudioTrack Rebind(StudioTrack track) => replacements.TryGetValue(Path.GetFullPath(track.Path), out var song)
                        ? track with { Path = song.Path, Name = song.Name } : track;
                    Plugin.Cfg.StudioQueue = Plugin.Cfg.StudioQueue.Select(Rebind).ToList();
                    foreach (var playlist in Plugin.Cfg.StudioPlaylists) playlist.Tracks = playlist.Tracks.Select(Rebind).ToList();
                    StudioQueueChanged(); musicLibraryBusy = false;
                    musicLibraryError = failures > 0 ? $"{failures} song(s) could not be imported. Their original files and playlist references were kept." : null;
                });
            }
            catch
            {
                musicLibraryCompletions.Enqueue(() => { musicLibraryBusy = false; musicLibraryError = "The library folder could not be read or written. Choose an accessible folder and try again."; });
            }
        });
    }
    private void DrawMusicLibraryDialog(bool picker)
    {
        var folder = Plugin.Cfg.MusicLibraryFolder;
        ImGui.TextColored(MutedText, "Music library folder");
        ImGui.TextWrapped(string.IsNullOrEmpty(folder) ? "Choose where your music files will be stored." : folder);
        using (UiDisabled.Scope(musicLibraryBusy))
        {
            if (GameLayoutButton(string.IsNullOrEmpty(folder) ? "Choose folder" : "Change folder", FontAwesomeIcon.FolderOpen, Ui(140))) ChooseMusicLibraryFolder();
            using (UiDisabled.Scope(string.IsNullOrEmpty(folder)))
            {
                ImGui.SameLine(); if (GameLayoutButton("Import songs", FontAwesomeIcon.Plus, Ui(140), true)) ImportMusicFiles();
                ImGui.SameLine(); if (GameLayoutButton("Import folder", FontAwesomeIcon.FolderOpen, Ui(140))) ImportMusicFolder();
                ImGui.SameLine(); if (GameLayoutButton("Scan folder", FontAwesomeIcon.Sync, Ui(125))) RunMusicLibraryImport(folder, [], false);
            }
        }
        using (UiFonts.PushRelative(0.90f))
        using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
            ImGui.TextWrapped("Imported songs are copied here. Files already in this folder are used directly. Changing folders copies your library and updates playlist paths.");
        if (musicLibraryBusy) ImGui.TextColored(AccentHover, "Importing music…");
        if (musicLibraryError is { } error) ImGui.TextColored(Danger, error);
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##musicLibrarySearch", "Search your music library...", ref musicLibrarySearch, 150);
        var songs = Plugin.Cfg.MusicLibrary.Where(t => t.Name.Contains(musicLibrarySearch, StringComparison.OrdinalIgnoreCase)).ToArray();
        var listHeight = Math.Max(Ui(100), ImGui.GetContentRegionAvail().Y - Ui(70));
        using (var list = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##musicLibrarySongs", new Vector2(-1, listHeight))))
        using (UiDisabled.Scope(musicLibraryBusy))
        {
            if (songs.Length == 0) ImGui.TextColored(MutedText, string.IsNullOrEmpty(folder) ? "Choose a library folder to get started." : "No songs found. Import songs or scan your folder.");
            foreach (var song in songs)
            {
                using var id = ImRaii.PushId(song.Id);
                var exists = File.Exists(song.Path); var selected = musicLibrarySelection.Contains(song.Id);
                var row = ImGui.GetCursorScreenPos(); var rowWidth = ImGui.GetContentRegionAvail().X;
                var draw = ImGui.GetWindowDrawList();
                draw.AddRectFilled(row, row + new Vector2(rowWidth, Ui(54)), ImGui.GetColorU32(selected ? Vector4.Lerp(FrameBg, Accent, .15f) : FrameBg), Ui(6));
                draw.AddRect(row, row + new Vector2(rowWidth, Ui(54)), ImGui.GetColorU32(selected ? Accent : BorderSubtle), Ui(6));
                if (picker)
                {
                    using var unavailable = UiDisabled.Scope(!exists);
                    ImGui.SetCursorScreenPos(row + UiVec(10, 14));
                    if (ImGui.Checkbox("##selected", ref selected))
                    { if (selected) { if (musicLibraryLoadDeck.HasValue) musicLibrarySelection.Clear(); musicLibrarySelection.Add(song.Id); } else musicLibrarySelection.Remove(song.Id); }
                }
                var tileX = picker ? 44 : 10; var textX = tileX + 44;
                StudioMusicTile(row + UiVec(tileX, 9), UiVec(34, 34), false);
                var textWidth = rowWidth - Ui(textX + (picker ? 12 : 220));
                StudioClippedText(row + UiVec(textX, 9), textWidth, song.Name, !exists);
                StudioClippedText(row + UiVec(textX, 30), textWidth, exists ? Path.GetFileName(song.Path) : "File missing", true);
                if (!picker)
                {
                    ImGui.SetCursorScreenPos(row + new Vector2(rowWidth - Ui(214), Ui(13)));
                    if (GameLayoutButton("Rename", FontAwesomeIcon.Edit, Ui(100), height: 28))
                    { musicLibraryActionId = song.Id; musicLibraryRename = song.Name; OpenMusicDialog("studioLibraryRename"); }
                    ImGui.SameLine(); if (GameLayoutButton("Remove", FontAwesomeIcon.Trash, Ui(100), height: 28))
                    { musicLibraryActionId = song.Id; OpenMusicDialog("studioLibraryRemove"); }
                }
                ImGui.SetCursorScreenPos(row + new Vector2(0, Ui(60)));
            }
        }
        if (picker)
        {
            using (UiDisabled.Scope(musicLibraryBusy || musicLibrarySelection.Count == 0))
                if (GameLayoutButton(musicLibraryLoadDeck is { } target ? $"Load into Deck {(target == 0 ? "A" : "B")}" : $"Add selected ({musicLibrarySelection.Count})", FontAwesomeIcon.Plus, Ui(210), true))
                {
                    string? loaded = null;
                    foreach (var song in Plugin.Cfg.MusicLibrary.Where(t => musicLibrarySelection.Contains(t.Id) && File.Exists(t.Path)))
                    {
                        var track = song with { Id = Guid.NewGuid().ToString("N") };
                        Plugin.Cfg.StudioQueue.Add(track); loaded ??= track.Id;
                    }
                    StudioQueueChanged();
                    if (musicLibraryLoadDeck is { } deck && loaded != null) StudioDeckAction("load-deck", deck, loaded);
                    musicLibraryLoadDeck = null; CloseMusicDialog();
                }
            if (!musicLibraryLoadDeck.HasValue)
            { ImGui.SameLine(); if (GameLayoutButton("Select all", FontAwesomeIcon.Check, Ui(120))) foreach (var song in songs.Where(t => File.Exists(t.Path))) musicLibrarySelection.Add(song.Id); }
            ImGui.SameLine(); if (GameLayoutButton("Clear", FontAwesomeIcon.Undo, Ui(100))) musicLibrarySelection.Clear();
        }
        else ImGui.TextColored(MutedText, $"{Plugin.Cfg.MusicLibrary.Count} songs in your library");
    }
    private void DrawMusicLibraryAction(bool rename)
    {
        var song = Plugin.Cfg.MusicLibrary.FirstOrDefault(t => t.Id == musicLibraryActionId);
        if (song == null) { OpenMusicDialog("studioLibrary"); return; }
        ImGui.TextUnformatted(song.Name);
        if (rename)
        {
            ImGui.SetNextItemWidth(-1); ImGui.InputText("##libraryRename", ref musicLibraryRename, 150);
            using (UiDisabled.Scope(string.IsNullOrWhiteSpace(musicLibraryRename)))
                if (GameLayoutButton("Save name", FontAwesomeIcon.Save, Ui(150), true))
                {
                    Plugin.Cfg.MusicLibrary[Plugin.Cfg.MusicLibrary.IndexOf(song)] = song with { Name = musicLibraryRename.Trim() };
                    StudioTrack Rename(StudioTrack track) => MusicLibraryStorage.SamePath(track.Path, song.Path) ? track with { Name = musicLibraryRename.Trim() } : track;
                    Plugin.Cfg.StudioQueue = Plugin.Cfg.StudioQueue.Select(Rename).ToList();
                    foreach (var playlist in Plugin.Cfg.StudioPlaylists) playlist.Tracks = playlist.Tracks.Select(Rename).ToList();
                    StudioQueueChanged(); OpenMusicDialog("studioLibrary");
                }
        }
        else
        {
            ImGui.TextWrapped("Remove this song from the library list? The music file and existing playlists will be kept. Scanning the folder can add it back.");
            if (GameLayoutButton("Remove from library", FontAwesomeIcon.Trash, Ui(200), danger: true))
            { Plugin.Cfg.MusicLibrary.Remove(song); Plugin.Cfg.Save(); OpenMusicDialog("studioLibrary"); }
        }
        ImGui.SameLine(); if (GameLayoutButton("Back", FontAwesomeIcon.ArrowLeft, Ui(100))) OpenMusicDialog("studioLibrary");
    }
}

using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawMusicDialogs()
    {
        if (musicDialog is null || activeGameDialog != MusicDialogId) return;
        // Close existing editors when their controls become unavailable.
        if ((musicDialog.StartsWith("studio", StringComparison.Ordinal) && !CanUseBroadcastStudio) ||
            (stream.Mode == StreamMode.Viewing && musicDialog is "text" or "save" or "load" or "reset"))
        { CloseMusicDialog(); return; }
        var kind = musicDialog;
        var title = kind switch { "connection" => "DJ connection settings", "guide" => "DJ connection guide",
            "variables" => "Screen text variables", "text" => "Screen text", "save" => "Save scene", "saved" => "Saved scenes", "load" => "Load saved scene?",
            "delete" => "Delete saved scene?", "reset" => "Reset scene?", "station" => djEditingStationIndex.HasValue ? "Edit station" : "Add station",
            "studioLibrary" => "Manage music library", "studioPickSongs" => "Add songs from your library",
            "studioLibraryRename" => "Rename library song", "studioLibraryRemove" => "Remove library song",
            "studioTools" => "Studio audio settings", "studioSave" => "Save playlist", "studioLoad" => "Saved playlists", _ => "Saved station" };
        var height = kind switch { "studioLibrary" or "studioPickSongs" => 650f, "studioTools" => 180f, "studioLoad" => 480f, "guide" => 560f, "variables" => 570f, "text" => 740f, "saved" => 560f, "connection" => 380f, "station" => 580f, "stationActions" or "save" => 220f, _ => 260f };
        var width = kind is "studioLibrary" or "studioPickSongs" ? 780 : kind == "guide" ? 760 : kind == "stationActions" ? 360 : 650;
        if (!BeginGameDialog(MusicDialogId, title, FontAwesomeIcon.Music, width, height)) return;
        var afterHeading = ImGui.GetCursorScreenPos();
        // Content width accounts for both the card padding and its scrollbar.
        // The header and button also need the same scroll offset.
        var closeX = afterHeading.X + ImGui.GetContentRegionAvail().X - Ui(28);
        var closeY = ImGui.GetWindowPos().Y + ImGui.GetStyle().WindowPadding.Y - Ui(4) - ImGui.GetScrollY();
        ImGui.SetCursorScreenPos(new Vector2(closeX, closeY));
        if (WheelSecondaryButton("X##closeMusic", UiVec(28, 28))) CloseMusicDialog();
        ImGui.SetCursorScreenPos(afterHeading);
        if (musicDialog is null) { EndGameDialog(); return; }
        try { DrawMusicDialogContent(kind); }
        finally { EndGameDialog(); }
    }
    private void DrawMusicDialogContent(string kind)
    {
        // This method's scopes restore content styles before the caller closes the child.
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8, 8));
        using var frames = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(10, 6)).Push(ImGuiStyleVar.FrameRounding, Ui(7));
        using var input = ImRaii.PushColor(ImGuiCol.FrameBg, FrameBg).Push(ImGuiCol.FrameBgHovered, FrameBgHover);
        switch (kind)
        {
            case "studioLibrary": case "studioPickSongs": case "studioLibraryRename": case "studioLibraryRemove":
            case "studioTools": case "studioSave": case "studioLoad":
            {
                using var studioBorders = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, Ui(1));
                using var studioColours = ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Lerp(FrameBg, Vector4.One, .08f))
                    .Push(ImGuiCol.Border, Vector4.Lerp(BorderSubtle, Vector4.One, .20f));
                if (kind is "studioLibrary" or "studioPickSongs") DrawMusicLibraryDialog(kind == "studioPickSongs");
                else if (kind is "studioLibraryRename" or "studioLibraryRemove") DrawMusicLibraryAction(kind == "studioLibraryRename");
                else DrawStudioDialog(kind);
                break;
            }
            case "connection":
                DrawMusicConnectionSettings(); break;
            case "guide":
                DrawDjConnectionGuideTabs();
                ImGui.Spacing();
                using (var guide = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                    "##musicConnectionGuideBody", new Vector2(0f, -1f), false)))
                {
                    if (guide)
                    {
                        using var bodyColour = ImRaii.PushColor(ImGuiCol.Text, Hex(0xE2E7EF));
                        DrawDjConnectionGuideTabContent();
                    }
                }
                break;
            case "variables": DrawMusicTextVariables(); break;
            case "text":
                var dummy = new VideoQueueEntry(video.CurrentMediaUrl ?? "music-scene://preset", "Music scene", "Audio", null, null);
                DrawPartyAudioTextSection(dummy, 0); ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
                DrawPartyAudioTextSection(dummy, 1); ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
                DrawPartyAudioTextSection(dummy, 2);
                screenController.Engine.SetMusicPreviewText(ResolveMusicScreenText(partyAudioScreenText));
                if (GameLayoutButton("Done", FontAwesomeIcon.Check, Ui(120))) CloseMusicDialog();
                break;
            case "save":
                DrawMusicSaveScene(); break;
            case "saved":
                DrawMusicSavedScenes(); break;
            case "load":
                if (GetSelectedMusicScene() is { } load)
                {
                    ImGui.TextWrapped($"Load '{load.Name}'? This replaces your current scene and updates active music playback.");
                    if (GameLayoutButton("Load scene", FontAwesomeIcon.FolderOpen, Ui(170), true))
                    { RestoreMusicScene(load); ApplyMusicScene(); musicMessage = "Scene loaded."; CloseMusicDialog(); }
                }
                DrawMusicCancel(); break;
            case "delete":
                if (GetSelectedMusicScene() is { } remove)
                {
                    ImGui.TextWrapped($"Delete '{remove.Name}'? Your active scene will stay unchanged.");
                    if (GameLayoutButton("Delete scene", FontAwesomeIcon.Trash, Ui(170), danger: true))
                    { Plugin.Cfg.SavedMusicScenes.RemoveAt(musicSavedIndex); Plugin.Cfg.Save(); OpenMusicDialog("saved"); }
                }
                DrawMusicCancel(); break;
            case "reset":
                ImGui.TextWrapped("Reset the scene to Classic Bars, no screen text and no surrounding effects? Saved scenes are kept.");
                if (GameLayoutButton("Reset scene", FontAwesomeIcon.Undo, Ui(170), true))
                { partyVisualizerMode = AudioVisualizerMode.ClassicBars; partyVisualizerTheme = AudioVisualizerTheme.AlphaPurple;
                    partyAudioScreenText = AudioScreenTextSelection.Empty; musicEffects = MusicSceneEffects.Default; musicSceneName = ""; ApplyMusicScene(); CloseMusicDialog(); }
                DrawMusicCancel(); break;
            case "station":
            {
                using var stationBorders = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, Ui(1));
                using var stationColours = ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Lerp(FrameBg, Vector4.One, .08f))
                    .Push(ImGuiCol.FrameBgHovered, Vector4.Lerp(FrameBgHover, Vector4.One, .10f))
                    .Push(ImGuiCol.FrameBgActive, AccentFieldActive)
                    .Push(ImGuiCol.Border, Vector4.Lerp(BorderSubtle, Vector4.One, .20f))
                    .Push(ImGuiCol.CheckMark, AccentHover);
                ImGui.TextColored(MutedText, "Name"); ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##stationName", "Station name...", ref djStationNameInput, 100);
                ImGui.TextColored(MutedText, "Direct audio stream URL"); ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##stationUrl", "https://...", ref djStationUrlInput, 2000);
                using (UiFonts.PushRelative(0.90f))
                using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
                    ImGui.TextWrapped("Use a direct HTTP or HTTPS audio stream, rather than a station's website.");
                DrawMusicStationGenres();
                if (djStationEditorError is { } error) ImGui.TextColored(Danger, error);
                if (GameLayoutButton("Save station", FontAwesomeIcon.Save, Ui(170), true))
                { SaveDjStationEditor(); if (djStationEditorError is null) CloseMusicDialog(); }
                DrawMusicCancel(); break;
            }
            case "stationActions":
                if (musicSavedIndex >= 0 && musicSavedIndex < djSavedStations.Count)
                {
                    ImGui.TextColored(Hex(0xE2E7EF), djSavedStations[musicSavedIndex].Name);
                    if (GameLayoutButton("Edit station", FontAwesomeIcon.Edit, Ui(180))) OpenMusicStationEditor(musicSavedIndex);
                    if (GameLayoutButton("Remove station", FontAwesomeIcon.Trash, Ui(180), danger: true)) OpenMusicDialog("stationDelete");
                }
                break;
            case "stationDelete":
                ImGui.TextWrapped("Remove this station from your saved stations?");
                if (GameLayoutButton("Remove station", FontAwesomeIcon.Trash, Ui(180), danger: true))
                { if (musicSavedIndex >= 0 && musicSavedIndex < djSavedStations.Count) { djSavedStations.RemoveAt(musicSavedIndex); Plugin.Cfg.Save(); } CloseMusicDialog(); }
                DrawMusicCancel(); break;
        }
    }
    private void DrawMusicCancel()
    {
        ImGui.SameLine(0, Ui(8));
        if (GameLayoutButton("Cancel", FontAwesomeIcon.Times, Ui(120))) CloseMusicDialog();
    }
    private void DrawMusicConnectionSettings()
    {
        if (radioCredentials is not { } radio) { ImGui.TextWrapped("Get connection details on the DJ Live tab first."); return; }
        using (UiFonts.PushRelative(0.90f))
        using (ImRaii.TextWrapPos(0f))
            ImGui.TextColored(MutedText, "Enter these in your Icecast-compatible DJ software. Keep the password private.");
        ImGui.TextWrapped("For an encrypted connection, use port 8443 with SSL/TLS enabled. The built-in Studio and Spotify broadcasts use encryption automatically.");
        var column = (ImGui.GetContentRegionAvail().X - Ui(12)) / 2;
        DrawDjCredentialField("##musicServer", "Server", radio.SourceHost, column);
        ImGui.SameLine(0, Ui(12)); DrawDjCredentialField("##musicPort", "Port", radio.SourcePort.ToString(), column);
        DrawDjCredentialField("##musicUser", "Username", radio.SourceUser, column);
        ImGui.SameLine(0, Ui(12)); DrawDjCredentialField("##musicMount", "Mount", radio.Mount, column);
        DrawDjPasswordField(radio.SourcePassword ?? "", ImGui.GetContentRegionAvail().X);
        using (UiFonts.PushRelative(0.90f))
        using (ImRaii.TextWrapPos(0f))
            ImGui.TextColored(MutedText, "Recommended: MP3, OGG or AAC • 192 kbps • Stereo • 44.1 / 48 kHz");
        if (GameLayoutButton("Copy all settings", FontAwesomeIcon.Clipboard, Ui(200), true)) ImGui.SetClipboardText(BuildDjCredentialClipboard(radio));
    }
    private SavedMusicScene? GetSelectedMusicScene() => musicSavedIndex >= 0 && musicSavedIndex < (Plugin.Cfg.SavedMusicScenes?.Count ?? 0)
        ? Plugin.Cfg.SavedMusicScenes![musicSavedIndex] : null;
    private void DrawMusicSaveScene()
    {
        var scenes = Plugin.Cfg.SavedMusicScenes ??= [];
        using (UiFonts.PushRelative(0.90f))
        using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
            ImGui.TextWrapped("Save the visualiser, screen text, effect settings and colours together.");
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##musicSceneName", "Scene name...", ref musicSceneName, 61);
        var name = musicSceneName.Trim();
        var existing = scenes.FindIndex(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) ImGui.TextWrapped("A scene with this name exists. Saving replaces that saved scene.");
        if (scenes.Count >= 30 && existing < 0) ImGui.TextColored(MutedText, "You have 30 saved scenes. Replace one or delete a saved scene first.");
        using (UiDisabled.Scope(name.Length == 0 || scenes.Count >= 30 && existing < 0))
            if (GameLayoutButton(existing >= 0 ? "Replace saved scene" : "Save scene", FontAwesomeIcon.Save, Ui(220), true))
            {
                var saved = CaptureMusicScene(name);
                if (existing >= 0) scenes[existing] = saved; else scenes.Add(saved);
                musicSceneName = name; Plugin.Cfg.CurrentMusicScene = saved; Plugin.Cfg.Save(); musicMessage = "Scene saved."; CloseMusicDialog();
            }
        DrawMusicCancel();
    }
    private void DrawMusicSavedScenes()
    {
        var scenes = Plugin.Cfg.SavedMusicScenes ??= [];
        using (UiFonts.PushRelative(0.90f))
        using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
            ImGui.TextWrapped("Load a saved scene to use it for your current music and future streams.");
        using var body = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##savedMusicSceneList", new Vector2(0, Math.Max(Ui(120), ImGui.GetContentRegionAvail().Y - Ui(12))), false));
        if (!body) return;
        if (scenes.Count == 0) ImGui.TextColored(MutedText, "No saved scenes yet. Design a scene, then choose Save scene.");
        for (var i = 0; i < scenes.Count; i++)
        {
            using var id = ImRaii.PushId(i);
            var index = i; var scene = scenes[i];
            GameLayoutPanel("##savedScene", ImGui.GetContentRegionAvail().X, Ui(100), () =>
            {
                ImGui.TextColored(Hex(0xE2E7EF), scene.Name);
                var mode = AudioVisualizerSelection.Parse(scene.Presentation);
                ImGui.TextColored(MutedText, AudioVisualizerSelection.GetDisplayName(mode.Mode) + " • " + AudioVisualizerSelection.GetThemeDisplayName(mode.Theme));
                if (GameLayoutButton("Load", FontAwesomeIcon.FolderOpen, Ui(120), true, height: 30)) { musicSavedIndex = index; OpenMusicDialog("load"); }
                ImGui.SameLine(0, Ui(8));
                if (GameLayoutButton("Delete", FontAwesomeIcon.Trash, Ui(100), height: 30)) { musicSavedIndex = index; OpenMusicDialog("delete"); }
            }, 10);
            SetGameLayoutRowGap(Ui(10));
        }
    }
}

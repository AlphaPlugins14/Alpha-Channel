using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.BroadcastAllowance;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private enum MusicTab { Dj, Radio, Scene, Studio, Spotify }
    private MusicTab musicTab;
    private bool musicSceneInitialized;
    private MusicSceneEffects musicEffects = MusicSceneEffects.Default;
    private string musicSearch = "";
    private int musicRadioFilter;
    private string musicSceneName = "";
    private string? musicMessage;
    private string? musicDialog;
    private int musicSavedIndex = -1;
    private const string MusicDialogId = "Music settings";

    private void EnsureMusicScene()
    {
        if (musicSceneInitialized) return;
        musicSceneInitialized = true;
        if (Plugin.Cfg.CurrentMusicScene is { } saved)
            RestoreMusicScene(saved);
        else
            musicEffects = MusicSceneEffects.Default with { Mask =
                (Plugin.Cfg.TvAudioEffectsEnabled ? 33 : 0) |
                (Plugin.Cfg.TvAudioHaloEnabled ? 2 : 0) |
                (Plugin.Cfg.TvAudioLedStripsEnabled ? 4 : 0) |
                (Plugin.Cfg.TvAudioLightRingsEnabled ? 8 : 0) |
                (Plugin.Cfg.TvAudioSparklesEnabled ? 16 : 0) };
    }
    private MusicSceneEffects GetMusicEffects()
    {
        EnsureMusicScene();
        return stream.Mode == StreamMode.Viewing ? MusicSceneEffects.Parse(stream.CurrentRoomState?.Url) : musicEffects;
    }
    private void RestoreMusicScene(SavedMusicScene saved)
    {
        var selection = AudioVisualizerSelection.Parse(saved.Presentation);
        partyVisualizerMode = selection.Mode;
        partyVisualizerTheme = selection.Theme;
        partyAudioScreenText = AudioScreenTextSelection.Parse(saved.Presentation);
        musicEffects = MusicSceneEffects.Parse(saved.Presentation);
        musicSceneName = saved.Name;
    }
    private SavedMusicScene CaptureMusicScene(string name) => new()
    {
        Name = name,
        Presentation = BuildPartyAudioPresentationUrl("music-scene://preset")
    };
    private void ApplyMusicScene()
    {
        if (stream.Mode == StreamMode.Viewing) return;
        EnsureMusicScene();
        Plugin.Cfg.CurrentMusicScene = CaptureMusicScene(musicSceneName);
        Plugin.Cfg.Save();
        if (video.IsAudioOnly && video.CurrentMediaUrl is { Length: > 0 } url &&
            video.State is VideoPlaybackState.Playing or VideoPlaybackState.Paused or VideoPlaybackState.Loading)
            video.Play(BuildPartyAudioPresentationUrl(url));
    }
    private void OpenMusicDialog(string kind)
    {
        musicDialog = kind;
        activeGameDialog = MusicDialogId;
    }
    private void CloseMusicDialog()
    {
        musicDialog = null;
        if (activeGameDialog == MusicDialogId) activeGameDialog = null;
    }
    private void DrawMusicConnectionFlash()
    {
        var remaining = (djConnectionCheckFlashUntilUtc - DateTime.UtcNow).TotalSeconds;
        if (remaining > 0)
            ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), ImGui.GetColorU32(Gold), Ui(7), ImDrawFlags.None, Ui(1 + (float)Math.Abs(Math.Sin(remaining * 10))));
    }
    private void OpenMusicStationEditor(int? index)
    {
        OpenDjStationEditor(index);
        djAddStationPopupOpen = false;
        OpenMusicDialog("station");
    }
    private void DrawMusicScenePage(float width, float gap)
    {
        var viewing = stream.Mode == StreamMode.Viewing;
        var ownMode = partyVisualizerMode; var ownTheme = partyVisualizerTheme; var ownText = partyAudioScreenText;
        if (viewing)
        {
            var remote = AudioVisualizerSelection.Parse(stream.CurrentRoomState?.Url ?? "");
            partyVisualizerMode = remote.Mode; partyVisualizerTheme = remote.Theme;
            partyAudioScreenText = AudioScreenTextSelection.Parse(stream.CurrentRoomState?.Url);
        }
        try
        {
            GameLayoutPanel("##musicSceneAuthority", width, Ui(124), () =>
            {
                using (UiFonts.PushIcon()) ImGui.TextColored(Accent, FontAwesomeIcon.Cog.ToIconString());
                ImGui.SameLine(0, Ui(10));
                using (UiFonts.PushRelative(1f, emphasis: true))
                    ImGui.TextColored(Hex(0xE2E7EF), viewing || stream.Mode == StreamMode.Hosting ? "Host controls" : "Your scene");
                using var introductionFont = UiFonts.PushRelative(0.90f);
                using var introductionWrap = ImRaii.TextWrapPos(0f);
                ImGui.TextColored(MutedText, viewing ? "The host controls the shared music scene." :
                    stream.Mode == StreamMode.Hosting ? "Changes update your shared music scene." : "Design your scene for solo playback or your next Watch Party.");
                ImGui.SetCursorPosX(Ui(16));
                using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
                {
                    ImGui.TextWrapped("Your scene is automatically synced to listeners in your Watch Party, they see what you see!");
                    ImGui.TextWrapped("Scene only applies during audio only playback, live DJing, Radio Stations and broadcast studio");
                }
            });
            SetGameLayoutRowGap(gap);
            using (UiDisabled.Scope(viewing))
                GameLayoutPanel("##musicVisualiser", width, Ui(width < Ui(900) ? 100 : 60),
                    () => DrawMusicVisualiser(width < Ui(900)), 12);
            SetGameLayoutRowGap(gap);
            var wide = width >= Ui(560);
            // Give the controls a readable minimum width and let the preview
            // take the remaining space instead of stacking at desktop widths.
            var controlsWidth = wide ? MathF.Max(Ui(310), (width - gap) * .40f) : width;
            var previewWidth = wide ? width - gap - controlsWidth : width;
            var previewHeight = Math.Clamp((previewWidth - Ui(32)) * 1.1f, Ui(220), Ui(480));
            var compactPreviewActions = previewWidth < Ui(450);
            MusicSceneContentPanel("##musicPreview", previewWidth, () =>
            {
                DrawMusicScenePreviewHeading();
                DrawMusicPreview(ImGui.GetContentRegionAvail().X, previewHeight, true);
                ImGui.Dummy(UiVec(0, 6));
                using (UiFonts.PushRelative(0.90f))
                using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
                {
                    ImGui.TextWrapped("Updates as you change your scene. Effects animate during playback.");
                }
                ImGui.Dummy(UiVec(0, 4));
                using (UiDisabled.Scope(viewing))
                {
                    var button = compactPreviewActions ? ImGui.GetContentRegionAvail().X : (ImGui.GetContentRegionAvail().X - Ui(16)) / 3;
                    if (GameLayoutButton("Save scene", FontAwesomeIcon.Save, button)) OpenMusicDialog("save");
                    if (!compactPreviewActions) ImGui.SameLine(0, Ui(8));
                    if (GameLayoutButton("Saved scenes", FontAwesomeIcon.FolderOpen, button)) OpenMusicDialog("saved");
                    if (!compactPreviewActions) ImGui.SameLine(0, Ui(8));
                    if (GameLayoutButton("Reset scene", FontAwesomeIcon.Undo, button)) OpenMusicDialog("reset");
                }
                if (!string.IsNullOrWhiteSpace(musicSceneName) && !viewing)
                    ImGui.TextColored(MutedText, TruncateToWidth("Scene: " + musicSceneName, ImGui.GetContentRegionAvail().X));
            });
            if (wide) ImGui.SameLine(0, gap); else SetGameLayoutRowGap(gap);
            // Group the independently fitted controls without introducing a
            // fourth scrollable window or stretching either panel to a fixed height.
            ImGui.BeginGroup();
            try
            {
                using var disabled = UiDisabled.Scope(viewing);
                MusicSceneContentPanel("##musicEffects", controlsWidth, DrawMusicEffects, 12);
                SetGameLayoutRowGap(gap);
                MusicSceneContentPanel("##musicText", controlsWidth, DrawMusicSceneText, 12);
            }
            finally { ImGui.EndGroup(); }
        }
        finally { if (viewing) { partyVisualizerMode = ownMode; partyVisualizerTheme = ownTheme; partyAudioScreenText = ownText; } }
    }
}

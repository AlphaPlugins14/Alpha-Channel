using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string? studioMenuTrackId;
    private Vector2 studioMenuAnchor;
    private bool openStudioTrackMenu;

    private void DrawStudioTrackMenu()
    {
        const string popupId = "##studioTrackMenu";
        if (openStudioTrackMenu) { ImGui.OpenPopup(popupId); openStudioTrackMenu = false; }
        if (!ImGui.IsPopupOpen(popupId)) return;
        var index = Plugin.Cfg.StudioQueue.FindIndex(t => t.Id == studioMenuTrackId);
        var parentPos = ImGui.GetWindowPos(); var parentSize = ImGui.GetWindowSize();
        var rowHeight = MathF.Max(Ui(24), ImGui.GetTextLineHeight() + Ui(8));
        var size = new Vector2(MathF.Min(Ui(230), parentSize.X - Ui(16)), ImGui.GetTextLineHeight() + rowHeight * 5 + Ui(48));
        var min = parentPos + UiVec(8, 8);
        var max = Vector2.Max(min, parentPos + parentSize - size - UiVec(8, 8));
        var position = studioMenuAnchor + new Vector2(Ui(26) - size.X, Ui(32));
        if (position.Y > max.Y) position.Y = studioMenuAnchor.Y - size.Y - Ui(5);
        ImGui.SetNextWindowPos(Vector2.Clamp(position, min, max));
        ImGui.SetNextWindowSize(size);
        using var styles = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(10, 10))
            .Push(ImGuiStyleVar.ItemSpacing, UiVec(4, 3)).Push(ImGuiStyleVar.PopupBorderSize, Ui(1))
            .Push(ImGuiStyleVar.PopupRounding, Ui(10));
        using var colours = ImRaii.PushColor(ImGuiCol.PopupBg, CardBg)
            .Push(ImGuiCol.Border, new Vector4(AccentHover.X, AccentHover.Y, AccentHover.Z, .38f))
            .Push(ImGuiCol.HeaderHovered, new Vector4(Accent.X, Accent.Y, Accent.Z, .4f));
        using var popup = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Popup(popupId, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse));
        if (!popup) return;
        if (index < 0 || !CanUseBroadcastStudio) { ImGui.CloseCurrentPopup(); return; }
        var track = Plugin.Cfg.StudioQueue[index];
        ImGui.TextColored(MutedText, TruncateToWidth(track.Name, ImGui.GetContentRegionAvail().X));
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip(track.Name);
        ImGui.Separator();
        if (DrawYouTubeMenuRow("Play now", FontAwesomeIcon.Play))
        { StudioAction("play", track.Id); ImGui.CloseCurrentPopup(); }
        if (DrawYouTubeMenuRow("Play next", FontAwesomeIcon.StepForward))
        {
            var playingIndex = Plugin.Cfg.StudioQueue.FindIndex(t => t.Id == StudioState.TrackId);
            Plugin.Cfg.StudioQueue.RemoveAt(index);
            var insert = Math.Clamp(playingIndex + (index <= playingIndex ? 0 : 1), 0, Plugin.Cfg.StudioQueue.Count);
            Plugin.Cfg.StudioQueue.Insert(insert, track); StudioQueueChanged(); ImGui.CloseCurrentPopup();
        }
        using (UiDisabled.Scope(index == 0))
            if (DrawYouTubeMenuRow("Move up", FontAwesomeIcon.ArrowUp))
            {
                (Plugin.Cfg.StudioQueue[index - 1], Plugin.Cfg.StudioQueue[index]) = (track, Plugin.Cfg.StudioQueue[index - 1]);
                StudioQueueChanged(); ImGui.CloseCurrentPopup();
            }
        using (UiDisabled.Scope(index == Plugin.Cfg.StudioQueue.Count - 1))
            if (DrawYouTubeMenuRow("Move down", FontAwesomeIcon.ArrowDown))
            {
                (Plugin.Cfg.StudioQueue[index + 1], Plugin.Cfg.StudioQueue[index]) = (track, Plugin.Cfg.StudioQueue[index + 1]);
                StudioQueueChanged(); ImGui.CloseCurrentPopup();
            }
        ImGui.Separator();
        if (DrawYouTubeMenuRow("Remove song", FontAwesomeIcon.Trash))
        { Plugin.Cfg.StudioQueue.RemoveAt(index); StudioQueueChanged(); ImGui.CloseCurrentPopup(); }
    }
}

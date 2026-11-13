using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawInternetArchiveQueueOptions(InternetArchiveItem item, InternetArchiveEpisode? episode, float height)
    {
        CapturePartyPopupBounds();
        if (GameLayoutButton(string.Empty, FontAwesomeIcon.EllipsisH, Ui(40), height: height))
            ImGui.OpenPopup("##archiveVideoOptions");
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Video options");

        var inParty = stream.Mode is StreamMode.Hosting or StreamMode.Viewing;
        var rowCount = QueueManager.StandardSlotCount + 1 + (inParty ? 1 : 0);
        var rowHeight = MathF.Max(Ui(24), ImGui.GetTextLineHeight() + Ui(8));
        if (!BeginPartyPopup("##archiveVideoOptions", new(Ui(240),
                Ui(46) + ImGui.GetTextLineHeight() + rowCount * (rowHeight + ImGui.GetStyle().ItemSpacing.Y)))) return;

        var episodes = episode is null ? item.Episodes.ToArray() : [episode];
        var videos = episodes.Select(e =>
        {
            var entry = CreateInternetArchiveQueueEntry(item, e);
            return new VideoSearchEntry(entry.Title, entry.Url, entry.Source, entry.Duration, entry.ThumbnailUrl);
        }).ToArray();
        if (inParty && DrawYouTubeMenuRow(episode is null ? "Add all to Watch Party" : "Add to Watch Party", FontAwesomeIcon.Users))
            foreach (var video in videos) AddPartySearchResult(video);

        ImGui.TextColored(MutedText, episode is null ? "ADD ALL TO PERSONAL QUEUE" : "ADD TO PERSONAL QUEUE");
        for (var slot = 0; slot < QueueManager.StandardSlotCount; slot++)
        {
            var name = queueManager.Slots[slot]?.Name ?? $"Queue {slot + 1}";
            var selected = episode is not null && videos.Length == 1 && IsVideoInQueueSlot(videos[0], slot);
            if (DrawYouTubeMenuRow($"{name}##archiveSlot{slot}", FontAwesomeIcon.ListUl, selected))
            {
                if (episode is not null) ToggleVideoInQueueSlot(videos[0], slot);
                else foreach (var video in videos) AddVideoToQueueSlot(video, slot);
            }
        }
        ImGui.Separator();
        if (DrawYouTubeMenuRow(episode is null ? "Copy video URLs" : "Copy video URL", FontAwesomeIcon.Copy))
            ImGui.SetClipboardText(string.Join(Environment.NewLine, videos.Select(video => video.Url)));
        ImGui.EndPopup();
    }
}

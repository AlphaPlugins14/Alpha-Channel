using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private Action? DrawPersonalQueueRow(VideoQueueEntry[] entries, VideoQueueRecord[] records, int index, int slot, bool active, bool next)
    {
        var entry = entries[index];
        var current = active ? ReferenceEquals(entry, personalQueue.Current)
            : queueManager.Slots[slot] is { } owner && personalQueue.IsCurrentSavedRecord(owner, records[index]);
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var size = new Vector2(width, Ui(64));
        var draw = ImGui.GetWindowDrawList();
        if (current || ImGui.IsMouseHoveringRect(origin, origin + size))
            draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(AccentInk(current ? .12f : .055f)), Ui(6));
        Action? mutation = null;
        ImGui.InvisibleButton("##personalQueueDrag", UiVec(22, 62));
        for (var r = 0; r < 3; r++) for (var c = 0; c < 2; c++)
            draw.AddCircleFilled(origin + UiVec(5 + c * 6, 21 + r * 7), Ui(1.4f), ImGui.GetColorU32(current ? BorderSubtle : MutedText), 8);
        if (!current && ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            if (active) personalQueueDraggedEntry ??= entry;
            else personalQueueDraggedRecord ??= records[index];
        }
        var draggedIndex = active ? Array.IndexOf(entries, personalQueueDraggedEntry) : Array.IndexOf(records, personalQueueDraggedRecord);
        if (!current && draggedIndex >= 0 && draggedIndex != index && ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) && ImGui.IsMouseHoveringRect(origin, origin + size))
            DrawQueueDropMarker(origin, width, size.Y, draggedIndex < index);
        if (!current && ImGui.IsMouseHoveringRect(origin, origin + size) && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            var from = active ? Array.IndexOf(entries, personalQueueDraggedEntry) : Array.IndexOf(records, personalQueueDraggedRecord);
            if (from >= 0 && from != index) mutation = () => MovePersonalQueueEntry(slot, entries, records, from, index);
        }
        var thumbnail = width >= Ui(400);
        var textX = thumbnail ? 120 : 30;
        if (thumbnail)
        {
            var thumb = origin + UiVec(26, 6); var thumbSize = UiVec(82, 46);
            DrawPlayerStatusThumbnail(entry, entry.Title, entry.Source, thumb, thumbSize, activePlayback: false);
            var progress = current ? video.GetProgress() : ((float)entry.ResumePositionSeconds, (float)(entry.Duration?.TotalSeconds ?? 0), true);
            if (progress.Item1 > .5 && progress.Item2 > 0)
            {
                var bar = thumb + new Vector2(0, thumbSize.Y - Ui(3));
                draw.AddRectFilled(bar, thumb + thumbSize, ImGui.GetColorU32(FrameBg));
                draw.AddRectFilled(bar, bar + new Vector2(thumbSize.X * Math.Clamp(progress.Item1 / progress.Item2, 0, 1), Ui(3)), ImGui.GetColorU32(Accent));
            }
        }
        var badge = current ? "Playing" : next ? "Next" : null;
        var showBadge = badge is not null && width >= Ui(570);
        ImGui.SetCursorScreenPos(origin + UiVec(textX, 10));
        using (UiFonts.PushRelative(.98f, emphasis: true))
        {
            var visibleTitle = FitPartyTextToWidth(entry.Title, width - Ui(textX + (showBadge ? 182 : 106)));
            ImGui.TextUnformatted(visibleTitle);
            if (visibleTitle != entry.Title && ImGui.IsItemHovered()) UiFonts.SetTooltip(entry.Title);
        }
        ImGui.SetCursorScreenPos(origin + UiVec(textX, 34));
        using (UiFonts.PushRegularRelative(.88f))
            ImGui.TextColored(MutedText, FitPartyTextToWidth(entry.Source, width - Ui(textX + 45)));
        if (showBadge)
        {
            var pos = origin + new Vector2(width - Ui(172), Ui(13));
            draw.AddRectFilled(pos, pos + UiVec(62, 22), ImGui.GetColorU32(AccentInk(.16f)), Ui(5));
            using (UiFonts.PushRegularRelative(.84f)) draw.AddText(pos + UiVec(7, 3), ImGui.GetColorU32(AccentHover), badge!);
        }
        var time = entry.Duration is { } duration ? FormatTime((float)duration.TotalSeconds) : "Live";
        ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(42) - ImGui.CalcTextSize(time).X, Ui(16)));
        ImGui.TextColored(MutedText, time);
        ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(30), Ui(9)));
        if (PartyIconButton("personalRowMenu", FontAwesomeIcon.EllipsisH, UiVec(30, 30))) ImGui.OpenPopup("##personalRowMenu");
        var copyTargets = queueManager.Slots.Where((p, i) => p is not null && i != slot).Count();
        var menuHeight = Ui(52) + (6 + copyTargets) * (PartyActionRowHeight + ImGui.GetStyle().ItemSpacing.Y);
        if (BeginPartyPopup("##personalRowMenu", new(Ui(280), menuHeight)))
        {
            if (PartyActionMenuItem("Play now", FontAwesomeIcon.Play, active && !current && stream.Mode == StreamMode.None)) mutation = () => personalQueue.PlayNow(entry);
            if (PartyActionMenuItem("Move up", FontAwesomeIcon.ArrowUp, !current && index > 0 && (active
                    ? !ReferenceEquals(entries[index - 1], personalQueue.Current)
                    : !personalQueue.IsCurrentSavedRecord(queueManager.Slots[slot]!, records[index - 1]))))
                mutation = () => MovePersonalQueueEntry(slot, entries, records, index, index - 1);
            if (PartyActionMenuItem("Move down", FontAwesomeIcon.ArrowDown, !current && index + 1 < entries.Length))
                mutation = () => MovePersonalQueueEntry(slot, entries, records, index, index + 1);
            if (PartyActionMenuItem(current ? active ? "Stop & remove" : "Currently playing" : "Remove", FontAwesomeIcon.TrashAlt, !current || active && stream.Mode == StreamMode.None))
                mutation = () => { if (active) personalQueue.Remove(entry); else { queueManager.Slots[slot]!.Entries.Remove(records[index]); Plugin.Cfg.Save(); } };
            if (PartyActionMenuItem("Copy link", FontAwesomeIcon.Link)) ImGui.SetClipboardText(entry.Url);
            ImGui.Spacing();
            ImGui.TextColored(MutedText, "Copy to");
            if (PartyActionMenuItem("Watch Party queue", FontAwesomeIcon.Users,
                    stream.Mode is StreamMode.Hosting or StreamMode.Viewing && CurrentSession is not null))
                mutation = () =>
                {
                    if (stream.Mode is StreamMode.Hosting or StreamMode.Viewing && CurrentSession is { } session)
                        AddPartyItem(PartyItem(entry, session.AccountId, session.DisplayName));
                };
            for (var destination = 0; destination < queueManager.Slots.Count; destination++)
            {
                if (destination == slot || queueManager.Slots[destination] is not { } target) continue;
                using var targetId = ImRaii.PushId(destination);
                if (PartyActionMenuItem(FitPartyTextToWidth(target.Name, Ui(210)), FontAwesomeIcon.ListUl))
                {
                    var targetSlot = destination;
                    mutation = () => CopyPersonalQueueVideo(slot, targetSlot, entry);
                }
            }
            ImGui.EndPopup();
        }
        draw.AddLine(origin + UiVec(26, 63), origin + new Vector2(width, Ui(63)), ImGui.GetColorU32(BorderSubtle));
        if (draggedIndex == index) draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(CardBg with { W = .5f }), Ui(6));
        ImGui.SetCursorScreenPos(origin + new Vector2(0, size.Y));
        return mutation;
    }
}

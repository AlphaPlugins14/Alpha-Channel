using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawPersonalQueueTools(int slot, SavedQueueProfile? profile, int count)
    {
        var clear = false;
        if (BeginPartyPopup("##personalQueueTools", UiVec(280, 174)))
        {
            if (PartyActionMenuItem("Edit queue", FontAwesomeIcon.Edit, profile is not null)) OpenQueueEditor(slot, profile);
            clear = PartyActionMenuItem("Clear queue…", FontAwesomeIcon.TrashAlt, count > 0);
            if (PartyActionMenuItem("Delete queue…", FontAwesomeIcon.Trash, profile is not null))
            { deletingQueueIndex = slot; deletingQueueName = profile!.Name; deleteQueuePopupOpen = true; }
            ImGui.TextColored(MutedText, "Changes are saved automatically.");
            ImGui.EndPopup();
        }
        if (clear && profile is not null) OpenPersonalQueueDialog(PersonalQueueDialog.Clear, profile);
    }

    private void DrawPersonalQueueFooter(Vector2 origin, float width, VideoQueueEntry[] entries, int slot, bool active, bool stacked, ref Action? mutation)
    {
        ImGui.GetWindowDrawList().AddLine(origin - UiVec(0, 8), origin + new Vector2(width, -Ui(8)), ImGui.GetColorU32(BorderSubtle));
        var duration = FormatTime((float)entries.Sum(e => e.Duration?.TotalSeconds ?? 0)) + (entries.Any(e => e.Duration is null) ? "+" : "");
        ImGui.SetCursorScreenPos(origin + UiVec(0, 7));
        ImGui.TextColored(MutedText, FitPartyTextToWidth($"{entries.Length} {(entries.Length == 1 ? "video" : "videos")} · {duration}", stacked ? width : MathF.Max(1, width - Ui(320))));
        var controls = origin + (stacked ? UiVec(0, 30) : new Vector2(width - Ui(310), 0));
        ImGui.SetCursorScreenPos(controls);
        var enabled = stream.Mode == StreamMode.None && (!active || entries.Length > 0 && personalQueue.Current is null);
        using (UiDisabled.Scope(!enabled))
            if (ActivePartyButton(active ? "Resume queue" : "Activate queue", UiVec(144, 30), primary: enabled))
                mutation = active ? () => personalQueue.Advance() : () => queueManager.Activate(slot);
        ImGui.SetCursorScreenPos(controls + UiVec(156, 2));
        var autoplay = Plugin.Cfg.AutoPlayNextQueueVideo;
        using (UiDisabled.Scope(stream.Mode != StreamMode.None))
            if (ImGui.Checkbox("Auto-play next", ref autoplay))
            { Plugin.Cfg.AutoPlayNextQueueVideo = autoplay; Plugin.Cfg.Save(); }
    }

    private void MovePersonalQueueEntry(int slot, VideoQueueEntry[] entries, VideoQueueRecord[] records, int from, int to)
    {
        if (slot == queueManager.ActiveSlotIndex)
        {
            var source = -1; var target = -1;
            for (var i = 0; i < personalQueue.Entries.Count; i++)
            { if (ReferenceEquals(personalQueue.Entries[i], entries[from])) source = i; if (ReferenceEquals(personalQueue.Entries[i], entries[to])) target = i; }
            personalQueue.Reorder(source, target);
        }
        else if (queueManager.Slots[slot] is { } profile)
        {
            var source = profile.Entries.IndexOf(records[from]); var target = profile.Entries.IndexOf(records[to]);
            if (source < 0 || target < 0) return;
            var record = profile.Entries[source]; profile.Entries.RemoveAt(source); profile.Entries.Insert(target, record); Plugin.Cfg.Save();
        }
    }

    private static VideoQueueEntry PersonalQueueEntry(VideoQueueRecord record) => new(record.Url, record.Title, record.Source,
        record.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null, record.ThumbnailUrl, record.ResumePositionSeconds);

    private void CopyPersonalQueueVideo(int source, int destination, VideoQueueEntry entry)
    {
        if (source == destination || queueManager.Slots[destination] is not { } target) return;
        target.Entries.Add(new VideoQueueRecord { Url = entry.Url, Title = entry.Title, Source = entry.Source,
            DurationSeconds = entry.Duration?.TotalSeconds, ThumbnailUrl = entry.ThumbnailUrl, ResumePositionSeconds = entry.ResumePositionSeconds });
        if (destination == queueManager.ActiveSlotIndex) personalQueue.ReplaceUpcomingEntries(target.Entries.ToArray());
        Plugin.Cfg.Save();
    }

    private static VideoQueueRecord CopyPersonalQueueRecord(VideoQueueRecord record) => new()
    { Url = record.Url, Title = record.Title, Source = record.Source, DurationSeconds = record.DurationSeconds, ThumbnailUrl = record.ThumbnailUrl, ResumePositionSeconds = record.ResumePositionSeconds };

    private void DrawPersonalQueueCopyContents(int viewedSlot, bool importing)
    {
        ImGui.TextWrapped(importing ? "Copy videos from another queue into this one." : "Save a copy of these videos to another queue.");
        PartyPopupChoice("personalQueueCopySlot", queueManager.Slots.Select((p, i) => p?.Name ?? (importing ? $"Empty slot {i + 1}" : $"Create in slot {i + 1}")).ToArray(), ref personalQueueCopySlot);
        var creating = !importing && queueManager.Slots[personalQueueCopySlot] is null;
        if (creating)
        { ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##personalCopyName", "Queue name…", ref personalQueueCopyName, 64); }
        ImGui.TextWrapped("Videos are appended. Existing entries are kept. Queues save automatically.");
        var source = importing ? personalQueueCopySlot : viewedSlot;
        var destination = importing ? viewedSlot : personalQueueCopySlot;
        using (UiDisabled.Scope(source == destination || queueManager.Slots[source] is null || creating && string.IsNullOrWhiteSpace(personalQueueCopyName)))
            if (ActivePartyButton(importing ? "Import videos" : "Save copy", UiVec(132, 30), primary: true))
            {
                CopyPersonalQueue(source, destination, personalQueueCopyName);
                personalQueueDialog = PersonalQueueDialog.None;
            }
        ImGui.SameLine(); if (ActivePartyButton("Cancel", UiVec(90, 30))) personalQueueDialog = PersonalQueueDialog.None;
    }

    private void CopyPersonalQueue(int source, int destination, string name)
    {
        if (source == destination || queueManager.Slots[source] is not { } from) return;
        // Snapshot first: creating or refreshing a slot can persist the active queue.
        var records = (source == queueManager.ActiveSlotIndex ? personalQueue.ExportEntries() : from.Entries).Select(CopyPersonalQueueRecord).ToArray();
        if (queueManager.Slots[destination] is null && !queueManager.Create(destination, name, from.Icon)) return;
        var target = queueManager.Slots[destination]!;
        target.Entries.AddRange(records);
        if (destination == queueManager.ActiveSlotIndex) personalQueue.ReplaceUpcomingEntries(target.Entries.ToArray());
        Plugin.Cfg.Save();
    }
}

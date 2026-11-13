using AlphaChannel.Plugin.Core;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string partyQueueSaveName = "Watch party";
    private int partyQueueTab, partyQueueImportSlot, partyQueueSaveSlot, partyQueueExportScope;
    private bool partyQueueEndAfterSave;
    private PartyQueueItem? partyQueueDuplicate;
    private readonly HashSet<int> partyQueueImportSelection = new();
    private readonly HashSet<Guid> partyQueueExportSelection = new();
    private Guid? partyQueueDragging;

    private void MovePartyQueueItem(int from, int to)
    { var item = partyQueueState.Upcoming[from]; partyQueueState.Upcoming.RemoveAt(from); partyQueueState.Upcoming.Insert(to, item); PartyQueueChanged(); }
    private void PlayPartyQueueItem(PartyQueueItem item)
    {
        if (PartyPlaybackQueue is not { } playback) return;
        var match = playback.Entries.FirstOrDefault(e => partyQueueEntries.TryGetValue(e.Id, out var mapped) && mapped.Id == item.Id);
        if (match is not null) playback.PlayNow(match);
    }
    private void AddPartySearchResult(VideoSearchEntry entry)
    {
        if (ChoosePageVideo(new(entry.Url, entry.Title, entry.ChannelName, entry.Duration, entry.ThumbnailUrl),
            chosen => AddPartySearchResult(new VideoSearchEntry(chosen.Title, chosen.Url, chosen.Source, chosen.Duration, chosen.ThumbnailUrl)))) return;
        var item = PartyItem(new(entry.Url, entry.Title, entry.ChannelName, entry.Duration, entry.ThumbnailUrl),
            CurrentSession?.AccountId ?? "", CurrentSession?.DisplayName ?? "You");
        AddPartyItem(item);
    }
    private void DrawPartyQueueImportPopup()
    {
        var records = queueManager.Slots[partyQueueImportSlot]?.Entries ?? [];
        var rowHeight = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y;
        var listHeight = Math.Clamp(records.Count, 1, 6) * rowHeight;
        if (!BeginPartyPopup("##partyQueueImport", new(Ui(460), Ui(158) + listHeight), centered: true)) return;
        ImGui.TextUnformatted("Copy from a personal queue");
        if (PartyPopupChoice("partyImportSlot", queueManager.Slots.Select((p, i) => p?.Name ?? $"Empty slot {i + 1}").ToArray(), ref partyQueueImportSlot))
        { partyQueueImportSelection.Clear(); records = queueManager.Slots[partyQueueImportSlot]?.Entries ?? []; }
        if (records.Count == 0)
        {
            ImGui.TextColored(MutedText, "This personal queue is empty.");
            ImGui.Dummy(UiVec(0, 4));
        }
        else using (var child = ImRaii.Child("##partyImportRows", new(0, listHeight)))
            if (child) for (var i = 0; i < records.Count; i++)
            {
                var chosen = partyQueueImportSelection.Contains(i);
                if (ImGui.Checkbox(FitPartyTextToWidth(records[i].Title, ImGui.GetContentRegionAvail().X - Ui(40)) + "##import" + i, ref chosen))
                { if (chosen) partyQueueImportSelection.Add(i); else partyQueueImportSelection.Remove(i); }
            }
        using (UiDisabled.Scope(partyQueueImportSelection.Count == 0))
            if (ActivePartyButton("Import selected", UiVec(132, 30), primary: true))
            { ImportPartyRecords(records.Where((_, i) => partyQueueImportSelection.Contains(i))); ImGui.CloseCurrentPopup(); }
        ImGui.SameLine();
        using (UiDisabled.Scope(records.Count == 0))
            if (ActivePartyButton("Import all", UiVec(100, 30))) { ImportPartyRecords(records); ImGui.CloseCurrentPopup(); }
        ImGui.SameLine(); if (ActivePartyButton("Cancel", UiVec(80, 30))) ImGui.CloseCurrentPopup();
        ImGui.TextWrapped("Copies videos; your personal queue stays saved.");
        ImGui.EndPopup();
    }
    private void ImportPartyRecords(IEnumerable<VideoQueueRecord> records)
    {
        if (CurrentSession is not { } session) return;
        var wasEmpty = PartyQueueEmptyForAutoStart;
        var count = 0;
        foreach (var record in records.ToArray())
        {
            var item = new PartyQueueItem(Guid.NewGuid(), record.Url, PartyQueueState.Clip(record.Title, 240),
                PartyQueueState.Clip(record.Source, 128), record.DurationSeconds, record.ThumbnailUrl, session.AccountId, session.DisplayName);
            if (partyQueueState.Add(item, true, session.AccountId, session.DisplayName) is null) count++;
        }
        partyQueueNotice = $"Imported {count} videos. Duplicate or unsupported entries were skipped."; ApplyPartyUpcomingToPlayback();
        if (count > 0) ResumePartyQueueAfterAddition(wasEmpty);
    }
    private void DrawPartyQueueSavePopup()
    {
        var creating = queueManager.Slots[partyQueueSaveSlot] is null;
        if (!BeginPartyPopup("##partyQueueSave", UiVec(460, creating ? 258 : 216), centered: true)) return;
        ImGui.TextUnformatted("Save a copy to your personal queues");
        PartyPopupChoice("partySaveScope", ["Remaining videos", "Selected videos", "Whole session (including played)"], ref partyQueueExportScope);
        PartyPopupChoice("partySaveSlot", queueManager.Slots.Select((p, i) => p?.Name ?? $"Create in slot {i + 1}").ToArray(), ref partyQueueSaveSlot);
        if (queueManager.Slots[partyQueueSaveSlot] is null)
        { ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X); ImGui.InputTextWithHint("##partySaveName", "Queue name…", ref partyQueueSaveName, 64); }
        ImGui.TextWrapped("Videos are appended. Existing entries are kept.");
        if (ImGui.Button(partyQueueEndAfterSave ? "Save & close party" : "Save copy"))
        {
            if (SavePartyQueueCopy()) { ImGui.CloseCurrentPopup(); if (partyQueueEndAfterSave) { hostLeaveConfirmationRequested = false; LeaveStream(); } }
        }
        ImGui.SameLine(); if (ImGui.Button("Cancel")) { partyQueueEndAfterSave = false; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }
    private bool SavePartyQueueCopy()
    {
        if (queueManager.Slots[partyQueueSaveSlot] is null && !queueManager.Create(partyQueueSaveSlot, partyQueueSaveName, "Tv")) return false;
        var profile = queueManager.Slots[partyQueueSaveSlot]!;
        IEnumerable<PartyQueueItem> items = partyQueueState.Upcoming;
        if (partyQueueExportScope == 2) items = partyQueueState.Played.Concat(partyQueueState.Current is { } current ? [current] : Array.Empty<PartyQueueItem>()).Concat(items);
        else if (partyQueueExportScope == 1) items = partyQueueState.Upcoming.Concat(partyQueueState.Requests).Concat(partyQueueState.Played).Where(x => partyQueueExportSelection.Contains(x.Id));
        var records = items.Select(PartyRecord).ToArray(); profile.Entries.AddRange(records);
        if (partyQueueSaveSlot == queueManager.ActiveSlotIndex) personalQueue.ReplaceUpcomingEntries(profile.Entries.ToArray());
        Plugin.Cfg.Save(); partyQueueNotice = $"Saved {records.Length} videos to {profile.Name}."; return true;
    }
    private void RestorePartyQueueRecovery()
    {
        if (partyQueueAvailableRecovery is not { } recovery || recovery.AccountId != CurrentSession?.AccountId || !PartyQueueState.Valid(recovery.Snapshot)) return;
        ImportPartyRecords(recovery.Snapshot.Upcoming.Select(PartyRecord));
    }
}

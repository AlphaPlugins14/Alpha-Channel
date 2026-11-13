using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private SavedQueueProfile? personalQueueViewedProfile;
    private VideoQueueEntry? personalQueueDraggedEntry;
    private VideoQueueRecord? personalQueueDraggedRecord;
    private int personalQueueCopySlot;
    private string personalQueueCopyName = "Queue copy";
    private bool queueViewWasInParty, viewingPartyQueue;

    private int PersonalQueueViewedSlot
    {
        get
        {
            for (var i = 0; i < queueManager.Slots.Count; i++)
                if (personalQueueViewedProfile is not null && ReferenceEquals(queueManager.Slots[i], personalQueueViewedProfile)) return i;
            personalQueueViewedProfile = queueManager.ActiveProfile;
            return queueManager.ActiveSlotIndex;
        }
    }

    private void DrawQueue()
    {
        var inParty = stream.Mode is StreamMode.Hosting or StreamMode.Viewing;
        if (inParty != queueViewWasInParty)
        {
            viewingPartyQueue = inParty;
            queueViewWasInParty = inParty;
            personalQueueDraggedEntry = null;
            personalQueueDraggedRecord = null;
        }
        CapturePartyPopupBounds();
        _ = PersonalQueueViewedSlot;
        DrawSavedQueueSlots();
        if (inParty && viewingPartyQueue)
        {
            DrawWatchPartyQueuePanel();
            return;
        }
        var slot = PersonalQueueViewedSlot;
        var profile = queueManager.Slots[slot];
        var active = slot == queueManager.ActiveSlotIndex;
        var records = profile?.Entries.ToArray() ?? [];
        var entries = active ? personalQueue.Entries.Where(e => !e.IsTransient).ToArray()
            : records.Select(PersonalQueueEntry).ToArray();
        var width = ImGui.GetContentRegionAvail().X;
        var height = MathF.Max(Ui(250), ImGui.GetContentRegionAvail().Y - Ui(2));
        var stacked = width < Ui(590);
        Action? mutation = null;
        DrawActivePartyCard("##personalQueuePanel", new(width, height), () =>
        {
            var origin = ImGui.GetCursorScreenPos();
            var inner = ImGui.GetContentRegionAvail().X;
            var headerHeight = Ui(inner < Ui(450) ? 78 : 44);
            ImGui.SetCursorScreenPos(origin + UiVec(0, 5));
            using (UiFonts.PushRelative(1.12f, emphasis: true))
                DrawSectionTitle(FontAwesomeIcon.ListUl,
                    FitPartyTextToWidth(profile?.Name ?? "Personal queue", inner < Ui(450) ? inner - Ui(28) : inner - Ui(252)));
            var tools = origin + (inner < Ui(450) ? UiVec(0, 38) : new Vector2(inner - Ui(214), 0));
            ImGui.SetCursorScreenPos(tools);
            using (UiDisabled.Scope(profile is null))
            {
                if (PartyLabeledButton("personalImport", "Import", FontAwesomeIcon.Upload, UiVec(84, 30)))
                    OpenPersonalQueueDialog(PersonalQueueDialog.Import, profile!);
                ImGui.SameLine(0, Ui(8));
                if (PartyLabeledButton("personalSave", "Save", FontAwesomeIcon.Save, UiVec(80, 30)))
                    OpenPersonalQueueDialog(PersonalQueueDialog.Save, profile!);
            }
            ImGui.SameLine(0, Ui(8));
            if (PartyIconButton("personalQueueTools", FontAwesomeIcon.EllipsisH, UiVec(34, 30), true)) ImGui.OpenPopup("##personalQueueTools");
            DrawPersonalQueueTools(slot, profile, entries.Length);
            ImGui.GetWindowDrawList().AddLine(origin + new Vector2(0, headerHeight - Ui(4)),
                origin + new Vector2(inner, headerHeight - Ui(4)), ImGui.GetColorU32(BorderSubtle));
            var footer = origin + new Vector2(0, height - Ui(36) - Ui(stacked ? 64 : 32));
            ImGui.SetCursorScreenPos(origin + new Vector2(0, headerHeight));
            using (var list = ImRaii.Child("##personalQueueRows" + slot,
                       new(inner, MathF.Max(Ui(60), footer.Y - origin.Y - headerHeight - Ui(12))), false))
            {
                if (list)
                {
                    if (entries.Length == 0)
                    {
                        ImGui.Dummy(UiVec(0, 16));
                        ImGui.TextColored(MutedText, "Your next watch starts here.");
                        ImGui.TextWrapped(inParty ? "Your personal queues stay saved while the Watch Party queue controls playback."
                            : active ? "Add a video to build your queue." : "Import videos here, or activate this queue before adding media.");
                        if (!inParty && active && ActivePartyButton("Add Media", UiVec(140, 32), primary: true))
                        { activePlayerDrawer = PlayerDrawer.PlayVideo; showingAddMediaSources = true; }
                    }
                    var next = active ? Array.FindIndex(entries, e => !ReferenceEquals(e, personalQueue.Current)) : -1;
                    for (var i = 0; i < entries.Length; i++)
                    {
                        using var id = ImRaii.PushId(active ? entries[i].Id.ToString()
                            : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(records[i]).ToString());
                        var rowAction = DrawPersonalQueueRow(entries, records, i, slot, active, i == next);
                        if (rowAction is not null) mutation = rowAction;
                    }
                }
            }
            DrawPersonalQueueFooter(footer, inner, entries, slot, active, stacked, ref mutation);
        });
        // Complete all child windows and popups before changing their row snapshots.
        mutation?.Invoke();
        var dragged = personalQueueDraggedEntry;
        if (dragged is null && personalQueueDraggedRecord is { } record) dragged = PersonalQueueEntry(record);
        if (dragged is not null && ImGui.IsMouseDown(ImGuiMouseButton.Left)) DrawQueueDragPreview(dragged.Title);
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        { personalQueueDraggedEntry = null; personalQueueDraggedRecord = null; }
    }

    private void DrawSavedQueueSlots()
    {
        ImGui.TextColored(MutedText, "Your queues");
        var width = ImGui.GetContentRegionAvail().X;
        var origin = ImGui.GetCursorScreenPos();
        var x = 0f; var y = 0f;
        for (var i = 0; i < queueManager.Slots.Count; i++)
        {
            if (queueManager.Slots[i] is not { } profile) continue;
            using var id = ImRaii.PushId(i);
            var active = i == queueManager.ActiveSlotIndex;
            var selected = !viewingPartyQueue && ReferenceEquals(profile, personalQueueViewedProfile);
            var count = active ? personalQueue.Entries.Count(e => !e.IsTransient) : profile.Entries.Count;
            var label = TruncateToWidth(profile.Name, Ui(160)) + " · " + count;
            var size = new Vector2(MathF.Min(width, ImGui.CalcTextSize(label).X + Ui(active ? 98 : 28)), Ui(38));
            if (x > 0 && x + size.X > width) { x = 0; y += Ui(46); }
            ImGui.SetCursorScreenPos(origin + new Vector2(x, y));
            var pos = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton("##personalQueueTab", size))
            { viewingPartyQueue = false; personalQueueViewedProfile = profile; personalQueueDraggedEntry = null; personalQueueDraggedRecord = null; }
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(pos, pos + size, ImGui.GetColorU32(selected || ImGui.IsItemHovered() ? FrameBgHover : FrameBg), Ui(8));
            draw.AddRect(pos, pos + size, ImGui.GetColorU32(selected ? Accent : BorderSubtle), Ui(8));
            draw.AddText(pos + UiVec(12, 10), ImGui.GetColorU32(selected ? Vector4.One : MutedText), label);
            if (active)
            {
                var badge = pos + new Vector2(size.X - Ui(66), Ui(8));
                draw.AddRectFilled(badge, badge + UiVec(56, 22), ImGui.GetColorU32(AccentInk(.18f)), Ui(5));
                using (UiFonts.PushRegularRelative(.84f))
                    draw.AddText(badge + UiVec(7, 3), ImGui.GetColorU32(AccentHover), stream.Mode == StreamMode.None ? "Active" : "Solo");
            }
            if (selected) draw.AddRectFilled(pos + new Vector2(1, size.Y - Ui(3)), pos + size - new Vector2(1, 0), ImGui.GetColorU32(Accent));
            x += size.X + Ui(8);
        }
        var empty = Enumerable.Range(0, queueManager.Slots.Count).FirstOrDefault(i => queueManager.Slots[i] is null, -1);
        if (empty >= 0)
        {
            if (x > 0 && x + Ui(145) > width) { x = 0; y += Ui(46); }
            ImGui.SetCursorScreenPos(origin + new Vector2(x, y));
            if (PartyLabeledButton("personalNewQueue", "New queue", FontAwesomeIcon.Plus, new(MathF.Min(width, Ui(145)), Ui(38)))) OpenQueueEditor(empty, null);
            x += MathF.Min(width, Ui(145)) + Ui(8);
        }
        var partyWidth = MathF.Min(width, ImGui.CalcTextSize("Watch Party Queue").X + Ui(46));
        if (x > 0 && x + partyWidth > width) { x = 0; y += Ui(46); }
        ImGui.SetCursorScreenPos(origin + new Vector2(x, y));
        using (UiDisabled.Scope(stream.Mode == StreamMode.None))
            if (PartyLabeledButton("watchPartyQueueView", "Watch Party Queue", FontAwesomeIcon.Users, new(partyWidth, Ui(38))))
            { viewingPartyQueue = true; personalQueueDraggedEntry = null; personalQueueDraggedRecord = null; }
        if (viewingPartyQueue && stream.Mode != StreamMode.None)
            ImGui.GetWindowDrawList().AddRectFilled(origin + new Vector2(x, y + Ui(35)), origin + new Vector2(x + partyWidth, y + Ui(38)), ImGui.GetColorU32(Accent));
        ImGui.SetCursorScreenPos(origin + new Vector2(0, y + Ui(52)));
    }
}

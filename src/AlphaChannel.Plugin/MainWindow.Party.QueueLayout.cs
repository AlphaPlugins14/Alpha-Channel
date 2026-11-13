using AlphaChannel.Plugin.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool partyQueueScrollOnTabChange;

    private void DrawWatchPartyQueuePanel()
    {
        CapturePartyPopupBounds();
        var width = ImGui.GetContentRegionAvail().X;
        var available = ImGui.GetContentRegionAvail().Y;
        var host = stream.Mode == StreamMode.Hosting;
        var stackedFooter = width < Ui(650);
        // Keep the footer on the page. Only the video list scrolls as it grows.
        var height = Math.Clamp(available - Ui(2), Ui(272), Ui(520));
        DrawActivePartyCard("##watchPartySharedQueue", new(width, height), () => {
            using var fieldStyle = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7))
                .Push(ImGuiStyleVar.FrameBorderSize, Ui(1)).Push(ImGuiStyleVar.PopupRounding, Ui(9))
                .Push(ImGuiStyleVar.PopupBorderSize, Ui(1));
            using var fieldColours = ImRaii.PushColor(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, .3f))
                .Push(ImGuiCol.FrameBg, FrameBg).Push(ImGuiCol.Header, new Vector4(Accent.X, Accent.Y, Accent.Z, .18f))
                .Push(ImGuiCol.HeaderHovered, new Vector4(Accent.X, Accent.Y, Accent.Z, .3f))
                .Push(ImGuiCol.PopupBg, CardBg);
            var origin = ImGui.GetCursorScreenPos();
            var inner = ImGui.GetContentRegionAvail().X;
            ImGui.SetCursorScreenPos(origin + UiVec(0, 5));
            using (UiFonts.PushRelative(1.12f, emphasis: true)) DrawSectionTitle(FontAwesomeIcon.ListUl, "Watch Party queue");
            var titleRight = ImGui.GetItemRectMax().X;
            if (titleRight + Ui(22) < origin.X + inner - Ui(218))
            {
                ImGui.SetCursorScreenPos(new(titleRight + Ui(8), origin.Y + Ui(6)));
                DrawPartyStatusDot(partyQueueConnected ? Good : MutedText);
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip(partyQueueConnected ? "Shared with everyone in this party" : "Connecting to the party queue…");
            }
            ImGui.SetCursorScreenPos(origin + new Vector2(inner - Ui(214), 0));
            using (UiDisabled.Scope(!host))
                if (PartyLabeledButton("partyQueueImport", "Import", FontAwesomeIcon.Upload, UiVec(84, 30)))
                { partyQueueImportSelection.Clear(); ImGui.OpenPopup("##partyQueueImport"); }
            ImGui.SameLine(0, Ui(8));
            if (PartyLabeledButton("partyQueueSave", "Save", FontAwesomeIcon.Save, UiVec(80, 30)))
            { partyQueueEndAfterSave = false; ImGui.OpenPopup("##partyQueueSave"); }
            ImGui.SameLine(0, Ui(8));
            if (PartyIconButton("partyQueueTools", FontAwesomeIcon.EllipsisH, UiVec(34, 30), true)) ImGui.OpenPopup("##partyQueueTools");
            DrawPartyQueueToolsPopup(host);

            var tabs = origin + UiVec(0, 40);
            var draw = ImGui.GetWindowDrawList();
            draw.AddLine(tabs + UiVec(0, 30), tabs + new Vector2(inner, Ui(30)), ImGui.GetColorU32(BorderSubtle));
            var x = 0f;
            foreach (var (label, tab) in new[] { ($"Up next ({partyQueueState.Upcoming.Count})", 0),
                ($"Requests ({partyQueueState.Requests.Count})", 1), ($"Played ({partyQueueState.Played.Count})", 2) })
            {
                ImGui.SetCursorScreenPos(tabs + new Vector2(x, 0));
                x += DrawPartyQueueTab(label, tab) + Ui(8);
            }
            var footerHeight = Ui(stackedFooter ? 58 : 30);
            var footer = origin + new Vector2(0, height - Ui(36) - footerHeight);
            var notice = partyQueueNotice ?? (!partyQueueConnected ? "Connecting to the shared queue…" :
                video.IsAudioOnly || video.IsPlayingGame || video.IsPlayingBrowser || video.IsPlayingWhiteboard
                    ? "Your queue is kept while another activity is playing." : null);
            var rowsTop = origin + UiVec(0, 75);
            var rowsHeight = MathF.Max(Ui(54), footer.Y - rowsTop.Y - Ui(notice is null ? 10 : 32));
            ImGui.SetCursorScreenPos(rowsTop);
            var list = (partyQueueTab == 0 ? partyQueueState.Upcoming : partyQueueTab == 1 ? partyQueueState.Requests : partyQueueState.Played).ToArray();
            using (var rows = ImRaii.Child("##partyQueueRows", new(inner, rowsHeight), false))
            {
                if (rows)
                {
                    if (list.Length == 0)
                    {
                        ImGui.SetCursorPos(ImGui.GetCursorPos() + UiVec(10, 15));
                        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + inner - Ui(24));
                        ImGui.TextColored(MutedText, partyQueueTab == 1 ? "No requests yet." : partyQueueTab == 2
                            ? "Videos you've watched will appear here." : "Add a video or import a saved queue to start planning your party.");
                        ImGui.PopTextWrapPos();
                    }
                    for (var index = 0; index < list.Length; index++) DrawPartyQueueRow(list[index], index, host);
                    if (partyQueueScrollOnTabChange)
                    {
                        // History is oldest-first, so its newest entry is at the bottom.
                        // Target the new content extent, not the previous tab's scroll maximum.
                        if (partyQueueTab == 2 && list.Length > 0)
                            ImGui.SetScrollFromPosY(ImGui.GetCursorPosY(), 1f);
                        else
                            ImGui.SetScrollY(0f);
                        partyQueueScrollOnTabChange = false;
                    }
                }
            }
            if (notice is not null)
            {
                ImGui.SetCursorScreenPos(footer - UiVec(0, 24));
                ImGui.TextColored(MutedText, FitPartyTextToWidth(notice, inner));
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip(notice);
            }
            DrawPartyQueueFooter(footer, inner, host, stackedFooter);
            DrawPartyQueueImportPopup(); DrawPartyQueueSavePopup();
            if (partyQueueDuplicate is not null) ImGui.OpenPopup("##partyQueueDuplicate");
            if (BeginPartyPopup("##partyQueueDuplicate", UiVec(430, 136), centered: true))
            {
                ImGui.TextUnformatted("That video is already queued. Add another copy?");
                if (ImGui.Button("Add another copy"))
                { if (partyQueueDuplicate is { } duplicate) AddPartyItem(duplicate, true); partyQueueDuplicate = null; ImGui.CloseCurrentPopup(); }
                ImGui.SameLine(); if (ImGui.Button("Cancel")) { partyQueueDuplicate = null; ImGui.CloseCurrentPopup(); }
                ImGui.EndPopup();
            }
        });
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) partyQueueDragging = null;
        else if (partyQueueDragging is { } dragging && partyQueueState.Upcoming.FirstOrDefault(item => item.Id == dragging) is { } moving)
            DrawQueueDragPreview(moving.Title);
    }

    private float DrawPartyQueueTab(string label, int tab)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.CalcTextSize(label).X + Ui(22), Ui(30));
        if (ImGui.InvisibleButton("##queueTab" + tab, size) && partyQueueTab != tab)
        {
            partyQueueTab = tab;
            partyQueueScrollOnTabChange = true;
        }
        var selected = partyQueueTab == tab;
        UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), origin + UiVec(11, 6),
            ImGui.GetColorU32(selected || ImGui.IsItemHovered() ? AccentHover : MutedText), label);
        if (selected) ImGui.GetWindowDrawList().AddRectFilled(origin + UiVec(0, 28), origin + size, ImGui.GetColorU32(Accent), Ui(1));
        return size.X;
    }

    private void DrawPartyQueueFooter(Vector2 origin, float width, bool host, bool stacked)
    {
        ImGui.GetWindowDrawList().AddLine(origin - UiVec(0, 7), origin + new Vector2(width, -Ui(7)), ImGui.GetColorU32(BorderSubtle));
        var total = partyQueueState.Upcoming.Sum(x => x.DurationSeconds ?? 0);
        var count = partyQueueState.Upcoming.Count;
        var duration = FormatTime((float)total) + (partyQueueState.Upcoming.Any(x => x.DurationSeconds is null) ? "+" : "");
        ImGui.SetCursorScreenPos(origin + UiVec(0, 6));
        ImGui.TextColored(MutedText, FitPartyTextToWidth($"{count} {(count == 1 ? "video" : "videos")} · {duration}", stacked ? width : width - Ui(446)));
        var controls = origin + (stacked ? UiVec(0, 28) : new Vector2(width - Ui(436), 0));
        ImGui.SetCursorScreenPos(controls);
        var canResume = CanResumePartyQueue;
        using (UiDisabled.Scope(!canResume))
            if (ActivePartyButton("Resume queue", UiVec(124, 28))) ResumePartyQueue();
        ImGui.SetCursorScreenPos(controls + UiVec(132, 0));
        DrawPartyQueueAutoPlay(host);
        ImGui.SetCursorScreenPos(controls + new Vector2(stacked ? width - Ui(164) : Ui(272), 0));
        var adding = partyQueueState.Adding switch { PartyQueueAdding.HostOnly => "Host only", PartyQueueAdding.Requests => "Requests", _ => "Open queue" };
        using (UiDisabled.Scope(!host))
            if (PartyLabeledButton("partyQueueAdding", "Adding: " + adding, FontAwesomeIcon.ChevronDown, UiVec(164, 28))) ImGui.OpenPopup("##partyQueueAddingMenu");
        if (BeginPartyPopup("##partyQueueAddingMenu", new(Ui(220), Ui(24) + 3 * (PartyActionRowHeight + ImGui.GetStyle().ItemSpacing.Y))))
        {
            if (host) foreach (var (name, mode, icon) in new[] {
                ("Host only", PartyQueueAdding.HostOnly, FontAwesomeIcon.Lock),
                ("Requests", PartyQueueAdding.Requests, FontAwesomeIcon.CommentDots),
                ("Open queue", PartyQueueAdding.Open, FontAwesomeIcon.Users) })
                if (PartyActionMenuItem(name, icon, selected: partyQueueState.Adding == mode))
                { partyQueueState.Adding = mode; PartyQueueChanged(); ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
    }

    private void DrawPartyQueueAutoPlay(bool enabled)
    {
        var origin = ImGui.GetCursorScreenPos();
        using (UiDisabled.Scope(!enabled))
            if (ImGui.InvisibleButton("##partyQueueAutoPlay", UiVec(134, 28)))
            { partyQueueState.AutoPlay = !partyQueueState.AutoPlay; PartyQueueChanged(); }
        var draw = ImGui.GetWindowDrawList();
        var box = origin + UiVec(0, 4);
        var checkedOn = partyQueueState.AutoPlay;
        draw.AddRectFilled(box, box + UiVec(20, 20), ImGui.GetColorU32(enabled && checkedOn ? Accent : FrameBg), Ui(4));
        draw.AddRect(box, box + UiVec(20, 20), ImGui.GetColorU32(enabled && checkedOn ? Accent : BorderSubtle), Ui(4));
        if (checkedOn)
        {
            var ink = ImGui.GetColorU32(enabled ? Vector4.One : MutedText);
            draw.AddLine(box + UiVec(4, 10), box + UiVec(8, 14), ink, Ui(2));
            draw.AddLine(box + UiVec(8, 14), box + UiVec(16, 5), ink, Ui(2));
        }
        UiFonts.DrawText(draw, ImGui.GetFontSize(), origin + UiVec(28, 6), ImGui.GetColorU32(enabled ? Vector4.One : MutedText), "Auto-play next");
    }

    private void DrawPartyQueueToolsPopup(bool host)
    {
        var clear = false;
        if (BeginPartyPopup("##partyQueueTools", new(Ui(355), Ui(72) + (host ? 3 : 0) * (PartyActionRowHeight + ImGui.GetStyle().ItemSpacing.Y))))
        {
            if (host)
            {
                if (PartyActionMenuItem("Shuffle upcoming", FontAwesomeIcon.Random, partyQueueState.Upcoming.Count > 1))
                { Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(partyQueueState.Upcoming)); PartyQueueChanged(); }
                clear = PartyActionMenuItem("Clear upcoming & requests…", FontAwesomeIcon.TrashAlt);
                if (PartyActionMenuItem("Restore interrupted queue", FontAwesomeIcon.History,
                    partyQueueAvailableRecovery is { } recovery && recovery.AccountId == CurrentSession?.AccountId)) RestorePartyQueueRecovery();
            }
            ImGui.Spacing();
            ImGui.TextColored(MutedText, "Personal queues are kept separately.");
            ImGui.EndPopup();
        }
        if (clear) ImGui.OpenPopup("##partyQueueClear");
        if (BeginPartyPopup("##partyQueueClear", UiVec(410, 130), centered: true))
        {
            ImGui.TextWrapped("Clear the queue? The current video keeps playing.");
            if (ActivePartyButton("Clear", UiVec(100, 30))) { partyQueueState.Upcoming.Clear(); partyQueueState.Requests.Clear(); PartyQueueChanged(); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine(); if (ActivePartyButton("Cancel", UiVec(100, 30))) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private void DrawPartyQueueRow(PartyQueueItem item, int index, bool host)
    {
        ImGui.PushID(item.Id.ToString());
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();
        var rowSize = new Vector2(width, Ui(62));
        var selected = partyQueueExportSelection.Contains(item.Id);
        if (selected) draw.AddRectFilled(origin, origin + rowSize - UiVec(0, 2), ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, .1f)), Ui(6));
        ImGui.InvisibleButton("##drag", UiVec(20, 60));
        for (var row = 0; row < 3; row++) for (var col = 0; col < 2; col++)
            draw.AddCircleFilled(origin + UiVec(5 + col * 6, 20 + row * 7), Ui(1.4f), ImGui.GetColorU32(MutedText), 8);
        if (host && partyQueueTab == 0 && ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left)) partyQueueDragging ??= item.Id;
        if (host && partyQueueTab == 0 && partyQueueDragging is { } movingId && movingId != item.Id &&
            ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) && ImGui.IsMouseHoveringRect(origin, origin + rowSize))
            DrawQueueDropMarker(origin, width, rowSize.Y, partyQueueState.Upcoming.FindIndex(x => x.Id == movingId) < index);
        if (host && partyQueueTab == 0 && partyQueueDragging is { } dragged && dragged != item.Id &&
            ImGui.IsMouseHoveringRect(origin, origin + rowSize) && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            var from = partyQueueState.Upcoming.FindIndex(x => x.Id == dragged);
            if (from >= 0) { var moving = partyQueueState.Upcoming[from]; partyQueueState.Upcoming.RemoveAt(from); partyQueueState.Upcoming.Insert(index, moving); PartyQueueChanged(); }
            partyQueueDragging = null;
        }
        var thumbnail = width > Ui(400);
        if (thumbnail) DrawActivePartyThumbnail(PartyEntry(item), origin + UiVec(26, 5), UiVec(88, 49.5f));
        var textX = thumbnail ? 126f : 30f;
        var showNext = partyQueueTab == 0 && index == 0 && width >= Ui(600);
        ImGui.SetCursorScreenPos(origin + UiVec(textX, 9));
        using (UiFonts.PushRelative(1f, emphasis: true))
        {
            var visibleTitle = FitPartyTextToWidth(item.Title, width - Ui(textX + (showNext ? 164 : 108)));
            ImGui.TextUnformatted(visibleTitle);
            if (visibleTitle != item.Title && ImGui.IsItemHovered()) UiFonts.SetTooltip(item.Title);
        }
        ImGui.SetCursorScreenPos(origin + UiVec(textX, 32));
        ImGui.TextColored(MutedText, FitPartyTextToWidth($"{item.Source} · Added by {item.AddedByName}{(item.Status is null ? "" : " · " + item.Status)}", width - Ui(textX + 38)));
        if (showNext)
        {
            using var badgeFont = UiFonts.PushRelative(.85f, emphasis: true);
            var textSize = ImGui.CalcTextSize("Next");
            var badgeSize = textSize + UiVec(16, 6);
            var badge = origin + new Vector2(width - Ui(110) - badgeSize.X, Ui(7));
            draw.AddRectFilled(badge, badge + badgeSize, ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, .16f)), Ui(5));
            draw.AddRect(badge, badge + badgeSize, ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, .55f)), Ui(5));
            UiFonts.DrawText(draw, ImGui.GetFontSize(), badge + (badgeSize - textSize) / 2, ImGui.GetColorU32(AccentHover), "Next");
        }
        var time = item.DurationSeconds is { } duration ? FormatTime((float)duration) : "—";
        ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(42) - ImGui.CalcTextSize(time).X, Ui(13)));
        ImGui.TextColored(MutedText, time);
        ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(30), Ui(7)));
        if (PartyIconButton("partyQueueItemMenu", FontAwesomeIcon.EllipsisH, UiVec(30, 30))) ImGui.OpenPopup("##partyQueueItemMenu");
        DrawPartyQueueItemMenu(item, index, host, selected);
        draw.AddLine(origin + new Vector2(Ui(26), Ui(61)), origin + new Vector2(width, Ui(61)), ImGui.GetColorU32(BorderSubtle));
        if (partyQueueDragging == item.Id) draw.AddRectFilled(origin, origin + rowSize, ImGui.GetColorU32(CardBg with { W = .5f }), Ui(6));
        ImGui.SetCursorScreenPos(origin + UiVec(0, 62));
        ImGui.PopID();
    }

    private void DrawPartyQueueItemMenu(PartyQueueItem item, int index, bool host, bool selected)
    {
        var canWithdraw = !host && item.AddedById == CurrentSession?.AccountId && partyQueueTab != 2;
        var actionRows = partyQueueTab == 2 ? 1 : host ? (partyQueueTab == 0 ? 5 : 2) : canWithdraw ? 1 : 0;
        var height = Ui(34) + (actionRows + 2) * (PartyActionRowHeight + ImGui.GetStyle().ItemSpacing.Y);
        if (!BeginPartyPopup("##partyQueueItemMenu", new(Ui(250), height))) return;
        if (host)
        {
            if (partyQueueTab == 1)
            {
                if (PartyActionMenuItem("Accept request", FontAwesomeIcon.Check)) { var wasEmpty = PartyQueueEmptyForAutoStart; partyQueueState.Requests.RemoveAll(x => x.Id == item.Id); partyQueueState.Upcoming.Add(item); PartyQueueChanged(); ResumePartyQueueAfterAddition(wasEmpty); }
                if (PartyActionMenuItem("Decline", FontAwesomeIcon.Times)) { partyQueueState.Requests.RemoveAll(x => x.Id == item.Id); PartyQueueChanged(); }
            }
            else if (partyQueueTab == 0)
            {
                if (PartyActionMenuItem("Play now", FontAwesomeIcon.Play)) PlayPartyQueueItem(item);
                if (PartyActionMenuItem("Play next", FontAwesomeIcon.StepForward, index > 0)) MovePartyQueueItem(index, 0);
                if (PartyActionMenuItem("Move up", FontAwesomeIcon.ArrowUp, index > 0)) MovePartyQueueItem(index, index - 1);
                if (PartyActionMenuItem("Move down", FontAwesomeIcon.ArrowDown, index + 1 < partyQueueState.Upcoming.Count)) MovePartyQueueItem(index, index + 1);
                if (PartyActionMenuItem("Remove", FontAwesomeIcon.TrashAlt)) { partyQueueState.Upcoming.RemoveAll(x => x.Id == item.Id); PartyQueueChanged(); }
            }
        }
        if (partyQueueTab == 2 && PartyActionMenuItem(host ? "Add again" : "Request again", FontAwesomeIcon.Plus)) AddPartyItem(item, allowDuplicate: host);
        if (canWithdraw && PartyActionMenuItem("Withdraw", FontAwesomeIcon.Undo))
            _ = SendPartyQueueActionAsync(CurrentSession!.Token, partyQueueHost!, new("withdraw", partyQueueState.PartyId, Guid.NewGuid(), ItemId: item.Id));
        ImGui.Separator();
        if (PartyActionMenuItem("Select for saving", FontAwesomeIcon.Save, selected: selected))
        { if (selected) partyQueueExportSelection.Remove(item.Id); else partyQueueExportSelection.Add(item.Id); }
        if (PartyActionMenuItem("Copy link", FontAwesomeIcon.Link)) ImGui.SetClipboardText(item.Url);
        ImGui.EndPopup();
    }
}

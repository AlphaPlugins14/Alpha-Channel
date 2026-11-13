using System.Collections.Concurrent;
using AlphaChannel.Plugin.Core;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool ShouldUseViewerMediaActions => stream.Mode == StreamMode.Viewing;
    private VideoQueueEntry? pendingViewerMediaEntry;
    private string? viewerMediaHost;
    private Guid? viewerMediaParty;
    private int viewerMediaGeneration;
    private bool viewerMediaSubmitting, viewerMediaSent;
    private string? viewerMediaMessage;
    private readonly ConcurrentQueue<(AccountRequest Account, int Generation, int QueueGeneration, string? Error)> viewerMediaReplies = new();

    private void ResetViewerMediaAction()
    {
        pendingViewerMediaEntry = null;
        viewerMediaGeneration++;
        viewerMediaSubmitting = viewerMediaSent = false;
        viewerMediaMessage = null;
    }

    private void OpenViewerMediaAction(VideoQueueEntry entry)
    {
        ResetViewerMediaAction();
        pendingViewerMediaEntry = entry;
        viewerMediaHost = stream.HostId;
        viewerMediaParty = partyQueueDiscovered ? partyQueueState.PartyId : null;
    }

    private void HandlePlayNow(VideoQueueEntry entry)
    {
        if (ChoosePageVideo(entry, HandlePlayNow)) return;
        // Choosing a destination never replaces the viewer's party playback or
        // ends an activity. Hosts retain the normal playback ownership checks.
        if (ShouldUseViewerMediaActions) { OpenViewerMediaAction(entry); return; }
        queue.PlayNow(entry);
    }

    private void HandleAddToQueue(VideoQueueEntry entry)
    {
        if (ChoosePageVideo(entry, HandleAddToQueue)) return;
        if (ShouldUseViewerMediaActions) { OpenViewerMediaAction(entry); return; }
        if (stream.Mode == StreamMode.Hosting && CurrentSession is { } session)
            AddPartyItem(PartyItem(entry, session.AccountId, session.DisplayName));
        else
            personalQueue.Add(entry);
        queueAddedFeedbackUntil = ImGui.GetTime() + 2;
    }

    private async Task SubmitViewerMediaAsync(AccountRequest account, string host, PartyQueueCommand command,
        int generation, int queueGeneration)
    {
        string? error = null;
        try
        {
            await partyQueueClient.CommandAsync(account.Session.Token, host, command, partyQueueLifetime.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            error = e is InvalidOperationException ? e.Message : "Couldn't send this video. Please try again.";
        }
        // UI state is committed on the UI thread, only to this still-open decision.
        viewerMediaReplies.Enqueue((account, generation, queueGeneration, error));
    }

    private void DrawViewerMediaActionPopup()
    {
        while (viewerMediaReplies.TryDequeue(out var reply))
        {
            if (!IsCurrentAccountRequest(reply.Account) || reply.Generation != viewerMediaGeneration ||
                reply.QueueGeneration != partyQueueGeneration) continue;
            viewerMediaSubmitting = false;
            viewerMediaSent = reply.Error is null;
            viewerMediaMessage = reply.Error ?? "Sent to the party queue. You can keep watching.";
        }
        if (!ShouldUseViewerMediaActions || stream.HostId != viewerMediaHost ||
            viewerMediaParty is { } party && partyQueueDiscovered && party != partyQueueState.PartyId)
        {
            if (pendingViewerMediaEntry is not null) ResetViewerMediaAction();
            return;
        }
        if (pendingViewerMediaEntry is not { } entry) return;
        viewerMediaParty ??= partyQueueDiscovered ? partyQueueState.PartyId : null;
        var parentPos = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var size = Vector2.Min(UiVec(550, 564), Vector2.Max(UiVec(1, 1), parentSize - UiVec(36, 36)));
        ImGui.SetNextWindowPos(parentPos); ImGui.SetNextWindowSize(parentSize); ImGui.SetNextWindowBgAlpha(0);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##viewerMediaActionOverlay", flags)) { ImGui.End(); return; }
        ImGui.GetWindowDrawList().AddRectFilled(parentPos, parentPos + parentSize, ImGui.GetColorU32(new Vector4(0, 0, 0, .62f)));
        ImGui.SetCursorScreenPos(parentPos + (parentSize - size) * .5f);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14)).Push(ImGuiStyleVar.ChildBorderSize, Ui(1))
                   .Push(ImGuiStyleVar.WindowPadding, UiVec(22, 18)).Push(ImGuiStyleVar.ItemSpacing, UiVec(8, 8)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, Accent)
                   .Push(ImGuiCol.Button, FrameBg).Push(ImGuiCol.ButtonHovered, AccentFieldBg)
                   .Push(ImGuiCol.ButtonActive, AccentActive))
        using (var card = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##viewerMediaActionCard", size, true)))
        {
            if (card)
            {
                var origin = ImGui.GetCursorScreenPos();
                DrawSectionTitle(FontAwesomeIcon.Video, "Add this video");
                var belowTitle = ImGui.GetCursorScreenPos();
                ImGui.SetCursorScreenPos(origin + new Vector2(ImGui.GetContentRegionAvail().X - Ui(26), -Ui(3)));
                if (PartyIconButton("closeViewerMedia", FontAwesomeIcon.Times, UiVec(28, 28), true)) ResetViewerMediaAction();
                ImGui.SetCursorScreenPos(belowTitle);
                using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
                    ImGui.TextWrapped("Choose where it goes. Keep watching with the party.");
                ImGui.Dummy(UiVec(0, 2));
                var mediaOrigin = ImGui.GetCursorScreenPos();
                var width = ImGui.GetContentRegionAvail().X;
                DrawActivePartyThumbnail(entry, mediaOrigin, UiVec(112, 63));
                ImGui.SetCursorScreenPos(mediaOrigin + UiVec(126, 6));
                using (UiFonts.PushRelative(1f, emphasis: true))
                    ImGui.TextUnformatted(FitPartyTextToWidth(string.IsNullOrWhiteSpace(entry.Title) ? entry.Url : entry.Title, width - Ui(126)));
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip(entry.Title);
                ImGui.SetCursorScreenPos(mediaOrigin + UiVec(126, 32));
                ImGui.TextColored(MutedText, FitPartyTextToWidth(entry.Source, width - Ui(126)));
                ImGui.SetCursorScreenPos(mediaOrigin + UiVec(0, 76));

                DrawSectionTitle(FontAwesomeIcon.Users, "Watch Party queue");
                var adding = partyQueueState.Adding;
                var ready = partyQueueConnected && partyQueueDiscovered && partyQueueHost == stream.HostId;
                var duplicate = partyQueueState.Upcoming.Concat(partyQueueState.Requests).Any(x => PartyQueueState.SameUrl(x.Url, entry.Url));
                var label = viewerMediaSubmitting ? "Sending…" : viewerMediaSent ? "Sent to party queue" :
                    !ready ? "Connecting to party queue…" : duplicate ? "Already in party queue" : adding switch {
                        PartyQueueAdding.Requests => "Request in Watch Party",
                        PartyQueueAdding.Open => "Add to Watch Party queue",
                        _ => "Host-only party queue" };
                var caption = adding switch {
                    PartyQueueAdding.Requests => "The host approves requests before they play.",
                    PartyQueueAdding.Open => "Added straight to Up next for everyone.",
                    _ => "Only the host can add videos to this party." };
                using (UiDisabled.Scope(!ready || duplicate || adding == PartyQueueAdding.HostOnly || viewerMediaSubmitting || viewerMediaSent))
                {
                    if (DrawViewerDestination("party", label, caption, FontAwesomeIcon.Users, true) &&
                        CurrentSession is { } session && stream.HostId is { } host)
                    {
                        viewerMediaSubmitting = true; viewerMediaMessage = null;
                        _ = SubmitViewerMediaAsync(CaptureAccountRequest(session), host,
                            new("add", partyQueueState.PartyId, Guid.NewGuid(), PartyItem(entry, session.AccountId, session.DisplayName)),
                            viewerMediaGeneration, partyQueueGeneration);
                    }
                }
                ImGui.Dummy(UiVec(0, 2)); ImGui.Separator();
                DrawSectionTitle(FontAwesomeIcon.ListUl, "Your personal queues");
                var search = new VideoSearchEntry(entry.Title, entry.Url, entry.Source, entry.Duration, entry.ThumbnailUrl);
                for (var slot = 0; slot < QueueManager.StandardSlotCount; slot++)
                {
                    var exists = IsVideoInQueueSlot(search, slot);
                    var name = queueManager.Slots[slot]?.Name ?? $"Queue {slot + 1}";
                    using (UiDisabled.Scope(exists))
                        if (DrawViewerDestination($"personal{slot}", name, exists ? "Already saved here" : "Save for later · only you can see this",
                                exists ? FontAwesomeIcon.Check : FontAwesomeIcon.Plus, false))
                        {
                            AddVideoToQueueSlot(search, slot);
                            viewerMediaMessage = $"Saved to {name}.";
                        }
                }
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
                ImGui.TextColored(viewerMediaSent ? Good : MutedText,
                    viewerMediaMessage ?? "Adding a video won't interrupt the current playback.");
                ImGui.PopTextWrapPos();
                if (ImGui.Button("Done##viewerMediaDone", new(width, Ui(30)))) ResetViewerMediaAction();
            }
        }
        ImGui.End();
    }

    private bool DrawViewerDestination(string id, string title, string caption, FontAwesomeIcon icon, bool primary)
    {
        var width = ImGui.GetContentRegionAvail().X;
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(9)).Push(ImGuiStyleVar.FrameBorderSize, Ui(1));
        using var colour = ImRaii.PushColor(ImGuiCol.Border, primary ? Accent : BorderSubtle)
            .Push(ImGuiCol.Button, primary ? AccentFieldBg : FrameBg);
        var clicked = ImGui.Button("##viewerDestination" + id, new(width, Ui(54)));
        var min = ImGui.GetItemRectMin(); var draw = ImGui.GetWindowDrawList();
        UiFonts.DrawIcon(draw, ImGui.GetFontSize(), min + UiVec(15, 18), ImGui.GetColorU32(AccentHover), icon.ToIconString());
        UiFonts.DrawText(draw, ImGui.GetFontSize(), min + UiVec(44, 8), ImGui.GetColorU32(ImGuiCol.Text),
            FitPartyTextToWidth(title, width - Ui(58)));
        UiFonts.DrawText(draw, ImGui.GetFontSize() * .9f, min + UiVec(44, 30), ImGui.GetColorU32(MutedText),
            FitPartyTextToWidth(caption, width - Ui(58)));
        return clicked;
    }
}

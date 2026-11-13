using System.Security.Cryptography;
using System.Text;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly AlphaChatClient alphaChat = new();
    private readonly List<AlphaChatMessage> alphaChatMessages = [];
    private AlphaChatUser[] alphaChatUsers = [];
    private readonly Dictionary<long, AlphaChatReaction[]> alphaChatReactions = [];
    private readonly Dictionary<long, float> alphaChatRowHeights = [];
    private long? alphaChatReplyTo;
    private long? alphaChatJumpTo;
    private long? alphaChatReactionPickerTarget;
    private bool alphaChatJumpApplied;
    private bool alphaChatJumpToUnread;
    private string alphaChatDraft = string.Empty;
    private sealed record RecoverableChatDraft(string Text, long? ReplyTo, string RequestId);
    private RecoverableChatDraft? alphaChatRecoverableDraft;
    private void RestoreAlphaChatDraft()
    {
        alphaChatRecoverableDraft = null;
        if (alphaChatUiAccountId is not { } account || !Plugin.Cfg.AlphaChatProtectedDrafts.TryGetValue(account, out var blob)) return;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(blob), Encoding.UTF8.GetBytes(account), DataProtectionScope.CurrentUser);
            try { alphaChatRecoverableDraft = JsonSerializer.Deserialize<RecoverableChatDraft>(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            if (alphaChatRecoverableDraft is { Text.Length: <= 1000 } draft && Guid.TryParse(draft.RequestId, out _))
            { alphaChatDraft = draft.Text; alphaChatReplyTo = draft.ReplyTo; }
            else alphaChatRecoverableDraft = null;
        }
        catch (Exception) { AepLog.Warning("[AlphaChat] A saved draft could not be restored."); }
    }
    private void SaveAlphaChatDraft(string account, RecoverableChatDraft draft)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(draft);
        try { Plugin.Cfg.AlphaChatProtectedDrafts[account] = Convert.ToBase64String(ProtectedData.Protect(bytes, Encoding.UTF8.GetBytes(account), DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        Plugin.Cfg.SavePublicStateDeferred();
    }
    private readonly AlphaChatHistoryPages alphaChatHistoryPages = new();
    private (long Sequence, float Offset)? alphaChatHistoryAnchor;
    private AlphaChatUser? alphaChatPerson;
    private bool alphaChatPersonRequested;
    private bool alphaChatCanModerate;
    private bool alphaChatScrollToBottom;
    private bool alphaChatForceBottom;
    private bool alphaChatSending;
    private long alphaChatContextMessage;
    private bool alphaChatContextActionsOpen;
    private readonly HashSet<long> alphaChatDeleted = [];
    private readonly HashSet<long> alphaChatUnread = [];
    private bool alphaChatWasAtBottom = true;
    private string? alphaChatUiAccountId;
    private string? alphaChatUiSessionId;
    private long alphaChatLatestSequence;
    private long alphaChatHistoryUnreadAfter = long.MaxValue;

    private readonly HashSet<long> alphaChatKnownSequences = [];
    private bool alphaChatMaintenanceDirty;
    private DateTime nextAlphaChatUpdateUtc;
    internal void TickAlphaChatSession()
    {
        UpdateAlphaChatPresence();
        if (DateTime.UtcNow >= nextAlphaChatUpdateUtc)
        {
            nextAlphaChatUpdateUtc = DateTime.UtcNow.AddMilliseconds(100);
            UpdateAlphaChat();
        }
    }
    private void UpdateAlphaChat()
    {
        var changed = alphaChatMaintenanceDirty; alphaChatMaintenanceDirty = false;
        if (alphaChatUiAccountId != CurrentSession?.AccountId)
        {
            alphaChatUiAccountId = CurrentSession?.AccountId;
            alphaAdminNotice = null;
            alphaChatKnownSequences.Clear(); changed = true;
            alphaChatMessages.Clear(); alphaChatUnread.Clear(); alphaChatUnreadMentions.Clear(); alphaChatDeleted.Clear(); alphaChatUsers = [];
            alphaChatHistoryPages.Reset(); alphaChatHistoryAnchor = null;
            alphaChatUsernameColourOverrides.Clear();
            alphaChatUsernameEffectOverrides.Clear();
            alphaChatSending = false; alphaChatDraft = ""; alphaChatPendingCaretBytes = null; alphaChatFocusComposer = false;
            alphaChatReactions.Clear(); alphaChatRowHeights.Clear(); alphaChatReplyTo = null; alphaChatJumpTo = null; alphaChatJumpToUnread = false; alphaChatReactionPickerTarget = null;
            alphaChatUiSessionId = null; alphaChatLatestSequence = 0; alphaChatCanModerate = false;
            RestoreAlphaChatDraft();
            while (alphaChat.Events.TryDequeue(out _)) { }
            while (alphaChat.Messages.TryDequeue(out _)) { }
        }
        if (!alphaChat.Active && !alphaChat.Connected) alphaChatUnread.Clear();
        while (alphaChat.Events.TryDequeue(out var e))
        {
            changed = true;
            switch (e.GetProperty("type").GetString())
            {
                case "welcome":
                    if (alphaChat.ChatSubscribed) { alphaChatUsernameColourOverrides.Clear(); alphaChatUsernameEffectOverrides.Clear(); }
                    if (e.TryGetProperty("deleted", out var deletedMentions))
                        foreach (var deletedMention in deletedMentions.EnumerateArray()) alphaChatUnreadMentions.Remove(deletedMention.GetInt64());
                    var chatSession = e.GetProperty("sessionId").GetString();
                    // First joins ignore old history; resumed sessions flag posts missed during the outage.
                    alphaChatHistoryUnreadAfter = chatSession == alphaChatUiSessionId ? alphaChatLatestSequence : long.MaxValue;
                    alphaChatUiSessionId = chatSession;
                    alphaChatCanModerate = e.GetProperty("canModerate").GetBoolean();
                    if (e.TryGetProperty("user", out var ownUser))
                    {
                        var verifiedSelf = ownUser.Deserialize<AlphaChatUser>(AlphaChatClient.JsonOptions)!;
                        alphaChatUsers = alphaChatUsers.Where(u => u.AccountId != verifiedSelf.AccountId)
                            .Append(verifiedSelf with { ChatActive = e.TryGetProperty("mode", out var ownMode) && ownMode.GetString() == "chat" }).ToArray();
                    }
                    if (!alphaChat.ChatSubscribed) break;
                    alphaChatKnownSequences.Clear();
                    alphaChatMessages.Clear(); alphaChatDeleted.Clear(); alphaChatReactions.Clear();
                    alphaChatHistoryPages.Reset(); alphaChatHistoryAnchor = null;
                    if (e.TryGetProperty("reactions", out var initialReactions))
                        foreach (var group in (initialReactions.Deserialize<AlphaChatReaction[]>(AlphaChatClient.JsonOptions) ?? []).GroupBy(r => r.Sequence))
                            alphaChatReactions[group.Key] = group.ToArray();
                    if (e.GetProperty("roster").GetArrayLength() > 0)
                        alphaChatUsers = e.GetProperty("roster").Deserialize<AlphaChatUser[]>(AlphaChatClient.JsonOptions) ?? [];
                    else if (alphaChatUsers.Length == 0 && e.TryGetProperty("user", out var self))
                        alphaChatUsers = [self.Deserialize<AlphaChatUser>(AlphaChatClient.JsonOptions)!];
                    ObservePatreonPeople(alphaChatUsers.Select(u => u.AccountId));
                    if (e.GetProperty("roster").GetArrayLength() > 0)
                        foreach (var person in alphaChatUsers) ObserveAlphaChatUsernameStyle(person);
                    alphaChatCanModerate = e.GetProperty("canModerate").GetBoolean();
                    alphaChatScrollToBottom = true; alphaChatForceBottom = true;
                    break;
                case "roster":
                    alphaChatUsers = e.GetProperty("users").Deserialize<AlphaChatUser[]>(AlphaChatClient.JsonOptions) ?? [];
                    ObservePatreonPeople(alphaChatUsers.Select(u => u.AccountId));
                    foreach (var person in alphaChatUsers) ObserveAlphaChatUsernameStyle(person);
                    break;
                case "user":
                    var updatedUser = e.GetProperty("user").Deserialize<AlphaChatUser>(AlphaChatClient.JsonOptions)!;
                    alphaChatUsers = alphaChatUsers.Where(u => u.AccountId != updatedUser.AccountId).Append(updatedUser).ToArray();
                    ObserveAlphaChatUsernameStyle(updatedUser);
                    break;
                case "reactions":
                    alphaChatReactions[e.GetProperty("sequence").GetInt64()] = e.GetProperty("items").Deserialize<AlphaChatReaction[]>(AlphaChatClient.JsonOptions) ?? []; break;
                case "deleted":
                    var id = e.GetProperty("sequence").GetInt64(); alphaChatDeleted.Add(id);
                    alphaChatMessages.RemoveAll(m => m.Sequence == id); alphaChatUnread.Remove(id); alphaChatUnreadMentions.Remove(id); alphaChatReactions.Remove(id);
                    if (alphaChatReplyTo == id) alphaChatReplyTo = null; break;
                case "adminNotice":
                    alphaAdminNotice = e.GetProperty("text").GetString();
                    break;
                case "restrictions": alphaChatRestrictions = e.GetProperty("items").Clone(); break;
            }
        }
        HashSet<string>? arrivingSenders = null;
        while (alphaChat.Messages.TryDequeue(out var message))
        {
            if (alphaChatDeleted.Contains(message.Sequence) || !alphaChatKnownSequences.Add(message.Sequence)) continue;
            changed = true;
            alphaChatMessages.Add(message);
            if (message.IsHistory) alphaChatHistoryPages.HistoryArrived();
            else ObserveAlphaChatUsernameStyle(message.Sender);
            // Discover senders on arrivals, including history from people who already left.
            // The shared Patreon cache batches lookups; drawing never makes a request.
            (arrivingSenders ??= new()).Add(message.Sender.AccountId);
            // Ordinary messages only set the unread dot; explicit mentions use normal notifications.
            RegisterAlphaChatMention(message);
            if ((!message.IsHistory || message.Sequence > alphaChatHistoryUnreadAfter) && message.Sender.AccountId != CurrentSession?.AccountId &&
                !AlphaChatBlocked(message.Sender.AccountId) &&
                !(IsOpen && !windowMinimized && currentPage == HomePage.AlphaChat && alphaChatWasAtBottom))
                alphaChatUnread.Add(message.Sequence);
            alphaChatLatestSequence = Math.Max(alphaChatLatestSequence, message.Sequence);
            alphaChatScrollToBottom = true;
            if (!message.IsHistory && message.Sender.AccountId == CurrentSession?.AccountId)
                alphaChatForceBottom = true;
        }
        if (arrivingSenders is not null) ObservePatreonPeople(arrivingSenders);
        if (!changed) return;
        alphaChatMessages.Sort((a,b) => a.Sequence.CompareTo(b.Sequence));
        if (alphaChatMessages.Count > AlphaChatHistoryCache.Limit) alphaChatMessages.RemoveRange(0, alphaChatMessages.Count - AlphaChatHistoryCache.Limit);
        var indexed = alphaChatMessages.ToDictionary(m => m.Sequence);
        alphaChatKnownSequences.IntersectWith(indexed.Keys);
        foreach (var id in alphaChatReactions.Keys.Where(id => !indexed.ContainsKey(id)).ToArray()) alphaChatReactions.Remove(id);
        foreach (var id in alphaChatRowHeights.Keys.Where(id => !indexed.ContainsKey(id)).ToArray()) alphaChatRowHeights.Remove(id);
        if (alphaChatReplyTo is { } replyId && !indexed.ContainsKey(replyId)) alphaChatReplyTo = null;
        alphaChatUnread.RemoveWhere(id => !indexed.TryGetValue(id,out var message) || AlphaChatBlocked(message.Sender.AccountId));
        alphaChatUnreadMentions.RemoveWhere(id =>
            alphaChatMessages.Count > 0 && id < alphaChatMessages[0].Sequence ||
            indexed.TryGetValue(id,out var message) && AlphaChatBlocked(message.Sender.AccountId));
    }
    private bool AlphaChatBlocked(string id) => CurrentSession is { } session &&
        (localBlockChanges.TryGetValue((session.AccountId, id), out var blocked) ? blocked :
            friendsAccountId == session.AccountId && blockedAccounts.Any(a => a.Id == id));

    private void DrawAlphaChatPage()
    {
        UpdateAlphaChat();
        var width = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetContentRegionAvail().Y;
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(12f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(16f, 14f)).Push(ImGuiStyleVar.FrameRounding, Ui(9f))
            .Push(ImGuiStyleVar.ItemSpacing, UiVec(8f, 8f));
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.FrameBg, FrameBg)
            .Push(ImGuiCol.Button, FrameBg).Push(ImGuiCol.ButtonHovered, FrameBgHover).Push(ImGuiCol.Border, BorderSubtle);
        if (!alphaChat.ChatSubscribed)
        {
            using var welcome = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##alphaChatWelcome", new Vector2(width, MathF.Max(Ui(250f), height)), true));
            if (welcome)
            {
                var blockWidth = MathF.Min(ImGui.GetContentRegionAvail().X, Ui(430f));
                ImGui.SetCursorPos(new Vector2((width - blockWidth) * .5f, MathF.Max(Ui(30f), height * .5f - Ui(110f))));
                using var group = ImRaii.Group();
                using (UiFonts.PushIcon()) ImGui.TextColored(Accent, FontAwesomeIcon.Comments.ToIconString());
                ImGui.TextUnformatted("Welcome to Alpha Chat");
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + blockWidth);
                ImGui.TextColored(MutedText, "Chat with the Alpha Channel community and discover watch parties.");
                if (!string.IsNullOrEmpty(alphaChat.Status))
                {
                    ImGui.Spacing();
                    var status = alphaChat.Status == "Background presence" ||
                        alphaChat.Status.StartsWith("Chat paused after", StringComparison.Ordinal)
                        ? "Chat paused due to inactivity.\nYou will still receive notifications if someone mentions you."
                        : alphaChat.Status;
                    ImGui.TextColored(MutedText, status);
                }
                ImGui.PopTextWrapPos(); ImGui.Spacing();
                using (UiDisabled.Scope(CurrentSession is null || alphaChat.Joining))
                    if (ActivePartyButton("Join Alpha Chat", new Vector2(blockWidth, Ui(42f)), primary: true) && CurrentSession is { } session)
                    {
                        alphaChatAutoJoinSuppressed = false;
                        if (!alphaChat.Active) StartAlphaChatPresence();
                        alphaChat.OpenChat();
                        RefreshFriends(session.Token);
                    }
            }
            return;
        }
        using (var toolbar = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##alphaChatToolbar", new Vector2(width, Ui(width < Ui(692f) ? 112f : 62f)), true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (toolbar)
            {
                var status = ImGui.GetCursorScreenPos();
                ImGui.GetWindowDrawList().AddCircleFilled(status + UiVec(10f,18f), Ui(7f),
                    ImGui.GetColorU32(alphaChat.Connected ? new Vector4(.16f,.81f,.39f,1f) : Gold));
                ImGui.Dummy(UiVec(24f,36f));
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip(alphaChat.Status);
                ImGui.SameLine();
                if (AlphaChatButton("alphaChatRoster", $"{alphaChat.MemberCount} people here", FontAwesomeIcon.Users, Ui(174f))) alphaChatRosterOpen = true;
                if (width < Ui(692f)) ImGui.NewLine();
                else { ImGui.SameLine(); ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), width - Ui(388f))); }
                using (UiDisabled.Scope(!alphaChat.EncryptionHealthy || stream.Mode is not (StreamMode.Hosting or StreamMode.Viewing)))
                    if (AlphaChatButton("alphaChatShare", "Share Watch Party", FontAwesomeIcon.BroadcastTower, Ui(180f)))
                        RequestAlphaChatShare();
                ImGui.SameLine();
                if (AlphaChatButton("alphaChatRules", "", FontAwesomeIcon.ClipboardList, Ui(36f))) alphaChatRulesOpen = true;
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Rules");
                ImGui.SameLine();
                if (AlphaChatButton("alphaChatSettings", "", FontAwesomeIcon.Cog, Ui(36f))) OpenAlphaChatSettings();
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Chat Settings");
                ImGui.SameLine();
                var encrypted = alphaChat.EncryptionHealthy;
                using (UiDisabled.Scope(alphaChat.Joining))
                    if (AlphaChatButton("alphaChatEncryption", "", encrypted ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen,
                        Ui(36f), iconColor: encrypted ? new Vector4(.16f, .81f, .39f, 1f) : new Vector4(1f, .62f, .2f, 1f)))
                        alphaChat.RepairEncryption();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    UiFonts.SetTooltip(encrypted ? "Alpha Chat Encrypted" : "Encryption error, press to attempt to fix");
                ImGui.SameLine();
                if (AlphaChatButton("alphaChatLeave", "", FontAwesomeIcon.SignOutAlt, Ui(36f))) LeaveAlphaChatForLogin();
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Leave Chat");
            }
        }
        if (alphaChat.Status != "Connected") ImGui.TextColored(MutedText, alphaChat.Status);
        var showNewMessages = alphaChatUnread.Count > 0 && !alphaChatWasAtBottom;
        var feedHeight = MathF.Max(Ui(160f), ImGui.GetContentRegionAvail().Y -
            Ui((alphaChatReplyTo.HasValue ? 134f : 84f) + (showNewMessages ? 36f : 0f)) - ImGui.GetStyle().ItemSpacing.Y);
        using (var feed = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##alphaChatFeed", new Vector2(width, feedHeight), false, ImGuiWindowFlags.AlwaysUseWindowPadding)))
        {
            if (feed)
            {
                var atBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - Ui(30f);
                if (alphaChatMessages.Count == 0) ImGui.TextColored(MutedText, "Say hello — start the conversation!");
                alphaChatJumpApplied = false;
                // These popups live at feed scope, outside each message's ID scope.
                alphaChatContextActionsOpen = alphaChatPersonRequested ||
                    ImGui.IsPopupOpen("##alphaChatPersonPopup") || ImGui.IsPopupOpen("##alphaContextReact");
                var available = alphaChatMessages.Where(m => !AlphaChatBlocked(m.Sender.AccountId)).ToArray();
                var sequences = available.Select(m => m.Sequence).ToArray();
                if (alphaChatForceBottom || alphaChatJumpTo.HasValue ||
                    alphaChatHistoryAnchor is { } oldAnchor && !sequences.Contains(oldAnchor.Sequence)) alphaChatHistoryAnchor = null;
                if (alphaChatJumpTo is { } jump) alphaChatHistoryPages.Reveal(sequences, jump);
                var start = alphaChatHistoryPages.Start(sequences);
                var loadMore = start > 0 && AlphaChatButton("alphaChatLoadMore", "Load more messages", FontAwesomeIcon.ArrowUp,
                    ImGui.GetContentRegionAvail().X, Ui(32f));
                var preserveScroll = false;
                for (var i = start; i < available.Length; i++)
                {
                    var message = available[i];
                    var rowOffset = ImGui.GetCursorScreenPos().Y - ImGui.GetWindowPos().Y;
                    if (loadMore && i == start)
                        alphaChatHistoryAnchor = (message.Sequence, rowOffset);
                    else if (alphaChatHistoryAnchor is { } anchor && anchor.Sequence == message.Sequence)
                    {
                        ImGui.SetScrollY(MathF.Max(0f, ImGui.GetScrollY() + rowOffset - anchor.Offset));
                        alphaChatHistoryAnchor = null; preserveScroll = true;
                    }
                    DrawAlphaChatMessage(message, ImGui.GetContentRegionAvail().X);
                }
                if (loadMore) { alphaChatHistoryPages.LoadMore(sequences); alphaChatScrollToBottom = false; alphaChatForceBottom = false; }
                // Bubbles/cards are drawn at explicit positions. Submit an item at the
                // final cursor so scrolling includes their full height and bottom gap.
                ImGui.Dummy(new Vector2(1f, 1f));
                if (!preserveScroll && alphaChatScrollToBottom && (atBottom || alphaChatForceBottom)) ImGui.SetScrollHereY(1f);
                if (alphaChatPersonRequested) { ImGui.OpenPopup("##alphaChatPersonPopup"); alphaChatPersonRequested = false; }
                using (ImRaii.PushStyle(ImGuiStyleVar.PopupBorderSize, Ui(1f)).Push(ImGuiStyleVar.PopupRounding,Ui(10f)).Push(ImGuiStyleVar.WindowPadding,UiVec(14f,12f)).Push(ImGuiStyleVar.ItemSpacing,UiVec(8f,8f)))
                using (ImRaii.PushColor(ImGuiCol.Border, new Vector4(AccentHover.X, AccentHover.Y, AccentHover.Z, .3f)))
                using (var person = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Popup("##alphaChatPersonPopup")))
                    if (person.Success && alphaChatPerson is { } user) DrawAlphaChatPersonActions(user, alphaChatContextMessage);
                if (alphaChatReactionPickerTarget is { } reactionTarget)
                {
                    ImGui.OpenPopup("##alphaContextReact");
                    alphaChatContextMessage = reactionTarget; alphaChatReactionPickerTarget = null;
                }
                DrawChatComposerEmojiPopup("##alphaContextReact", emoji => ToggleAlphaChatReaction(alphaChatContextMessage, emoji), "React to message");
                alphaChatWasAtBottom = !loadMore && !preserveScroll && !alphaChatJumpApplied && (atBottom || alphaChatForceBottom);
                if (alphaChatWasAtBottom) { alphaChatUnread.Clear(); alphaChat.MarkChatRead(); }
                alphaChatScrollToBottom = false; alphaChatForceBottom = false;
            }
        }
        if (showNewMessages && alphaChatUnread.Count > 0)
        {
            if (AlphaChatButton("alphaChatNewMessages", $"New messages ({alphaChatUnread.Count})",
                FontAwesomeIcon.ArrowDown, width, Ui(28f)))
            {
                alphaChatJumpTo = alphaChatUnread.Count > 0 ? alphaChatUnread.Max() : null;
                alphaChatJumpToUnread = alphaChatJumpTo.HasValue;
            }
        }
        using var composerPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(12f,12f));
        using var composer = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##alphaChatComposer", new Vector2(width, Ui(alphaChatReplyTo.HasValue ? 126f : 76f)), false,
            ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse));
        if (composer)
        {
            DrawAlphaChatComposerReply();
            var origin = ImGui.GetCursorScreenPos(); var inner = ImGui.GetContentRegionAvail().X;
            var controlHeight = Ui(48f); var emojiWidth = Ui(44f); var sendWidth = Ui(92f); var gap = Ui(10f);
            if (AlphaChatButton("alphaChatEmoji", "", FontAwesomeIcon.Smile, emojiWidth, controlHeight, primary: true)) ImGui.OpenPopup("##alphaChatEmojiPopup");
            DrawChatComposerEmojiPopup("##alphaChatEmojiPopup", emoji =>
            {
                var shortcode = ChatReactionShortcodes[emoji];
                if (alphaChatDraft.Length + shortcode.Length <= 1000) alphaChatDraft += shortcode;
            });
            ImGui.SetCursorScreenPos(origin + new Vector2(emojiWidth + gap, 0));
            ImGui.SetNextItemWidth(MathF.Max(Ui(60f), inner - emojiWidth - sendWidth - gap * 2));
            bool enter;
            if (alphaChatFocusComposer) { ImGui.SetKeyboardFocusHere(); alphaChatFocusComposer = false; }
            using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(Ui(12f), MathF.Max(0,(controlHeight - ImGui.GetFontSize())*.5f))))
                ImGui.InputTextWithHint($"##alphaChatDraft{alphaChatComposerRevision}", "Message Alpha Chat…", ref alphaChatDraft, 1000,
                    ImGuiInputTextFlags.CallbackAlways | ImGuiInputTextFlags.CallbackCompletion | ImGuiInputTextFlags.CallbackHistory, AlphaChatComposerCallback);
            var inputActive = ImGui.IsItemActive();
            // Keep the bound draft live for autocomplete; Enter is a separate submit action.
            enter = (inputActive || ImGui.IsItemDeactivated()) && ImGui.IsKeyPressed(ImGuiKey.Enter);
            var inputMin = ImGui.GetItemRectMin(); var inputMax = ImGui.GetItemRectMax();
            if (DrawAlphaChatMentionSuggestions(inputActive, inputMin, inputMax, enter)) enter = false;
            ImGui.SetCursorScreenPos(origin + new Vector2(inner - sendWidth,0));
            using (UiDisabled.Scope(!alphaChat.EncryptionHealthy || alphaChatSending || string.IsNullOrWhiteSpace(alphaChatDraft)))
                if (ActivePartyButton("Send", new Vector2(sendWidth, controlHeight), primary:true) || enter) SendAlphaChatDraft();
        }
    }
    private async void SendAlphaChatDraft()
    {
        if (alphaChatSending || !alphaChat.Connected || string.IsNullOrWhiteSpace(alphaChatDraft)) return;
        // Enter deactivates InputText, and clicking Send focuses the button.
        // Restore the composer on the next UI frame for either submit path.
        alphaChatFocusComposer = true;
        if (CurrentSession is not { } account) return;
        var accountId = account.AccountId;
        alphaChatSending = true; var draft = alphaChatDraft; var replyTo = alphaChatReplyTo;
        var recovered = alphaChatRecoverableDraft;
        var requestId = recovered is not null && recovered.Text == draft && recovered.ReplyTo == replyTo
            ? recovered.RequestId : Guid.NewGuid().ToString("D");
        alphaChatRecoverableDraft = new(draft, replyTo, requestId);
        try { SaveAlphaChatDraft(accountId, alphaChatRecoverableDraft); }
        catch (Exception) { alphaChatSending = false; AepLog.Warning("[AlphaChat] Could not save draft; message was not sent."); return; }
        var names = alphaChatUsers.Select(u => u.DisplayName).ToArray();
        var mentions = alphaChatUsers.Where(u => AlphaChatMentions.Contains(draft, u.DisplayName, names))
            .Select(u => u.AccountId).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        var sent = await alphaChat.PostTextAsync(draft, replyTo, mentions, requestId);
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (Volatile.Read(ref disposeStarted) != 0 || CurrentSession?.AccountId != accountId) return;
            alphaChatSending = false;
            if (!sent) return;
            Plugin.Cfg.AlphaChatProtectedDrafts.Remove(accountId);
            Plugin.Cfg.SavePublicStateDeferred();
            alphaChatRecoverableDraft = null;
            // Recreate the input state so its focused internal buffer also clears.
            if (alphaChatDraft == draft) SetAlphaChatMentionDraft("",0);
            if (alphaChatReplyTo == replyTo) alphaChatReplyTo = null;
            alphaChatScrollToBottom = true;
            alphaChatForceBottom = true;
        });
    }
    private bool AlphaChatButton(string id, string label, FontAwesomeIcon icon, float width,
        float? height = null, bool primary = false, bool danger = false, Vector4? iconColor = null)
    {
        var clicked = ActivePartyButton("##" + id, new Vector2(width, height ?? Ui(36f)), primary: primary, danger: danger);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var textSize = ImGui.CalcTextSize(label);
        Vector2 iconSize;
        if(UiFonts.Active)iconSize=MeasureUiIcon(icon,ImGui.GetFontSize());
        else using (UiFonts.PushIcon()) iconSize = ImGui.CalcTextSize(icon.ToIconString());
        var gap = label.Length == 0 ? 0f : Ui(8f);
        var x = min.X + (width - iconSize.X - gap - textSize.X) * .5f;
        var draw = ImGui.GetWindowDrawList();
        if(UiFonts.Active)DrawUiIcon(icon,ImGui.GetFontSize(),
            new Vector2(x,min.Y+(max.Y-min.Y-iconSize.Y)*.5f),iconColor??(danger?Danger:primary?Vector4.One:AccentHover));
        else UiFonts.DrawIcon(draw, ImGui.GetFontSize(), new Vector2(x, min.Y + (max.Y - min.Y - iconSize.Y) * .5f), ImGui.GetColorU32(iconColor ?? (danger ? Danger : primary ? Vector4.One : AccentHover)), icon.ToIconString());
        draw.AddText(new Vector2(x + iconSize.X + gap, min.Y + (max.Y - min.Y - textSize.Y) * .5f),
            ImGui.GetColorU32(Vector4.One), label);
        return clicked;
    }


    private void DrawAlphaChatMessage(AlphaChatMessage message, float width)
    {
        using var id = ImRaii.PushId(message.Sequence.ToString());
        var user = message.Sender; var origin = ImGui.GetCursorScreenPos();
        DrawAvatarAt(origin, user.AvatarIcon, user.AvatarColorHex, Ui(40f), user.AvatarImageUrl);
        if (ChatPointerInput.IsHoveringRect(origin, origin + UiVec(40f,40f)) && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) SelectAlphaChatPerson(user, 0);
        var textOrigin = origin + UiVec(54f,0f);
        ImGui.SetCursorScreenPos(textOrigin);
        var visibleName = FitPartyTextToWidth(user.DisplayName, MathF.Max(Ui(60f), width - Ui(PatreonBadgeTier(user.AccountId) > 0 ? 302f : 280f)));
        DrawAlphaChatUsername(user, visibleName, AccentHover);
        if (visibleName != user.DisplayName && ImGui.IsItemHovered()) UiFonts.SetTooltip(user.DisplayName);
        if (ImGui.IsItemClicked()) SelectAlphaChatPerson(user, 0);
        if (PatreonBadgeTier(user.AccountId) > 0) { ImGui.SameLine(0, Ui(6f)); DrawAlphaChatSupporterHeart(); }
        ImGui.SameLine(); DrawSocialHelper(DateTimeOffset.FromUnixTimeMilliseconds(message.Timestamp).LocalDateTime.ToString("t"));
        var menuOrigin = ImGui.GetItemRectMax() + UiVec(10f, -19f);
        DrawAlphaChatHoverActions(message, origin, menuOrigin, width);
        var bubbleMin = textOrigin + UiVec(0f,23f);
        DrawAlphaChatReplyQuote(message, ref bubbleMin, width - Ui(60f));
        if (message.Kind == "room") { ImGui.SetCursorScreenPos(bubbleMin); DrawAlphaChatRoom(message, width - Ui(60f)); }
        else
        {
            var bubbleWidth = MathF.Min(MathF.Max(Ui(90f),width-Ui(60f)),MathF.Max(Ui(120f),MeasureChatRichTextWidth(message.Content)+Ui(28f)));
            var textHeight = LayoutChatRichText(message.Content,Vector2.Zero,bubbleWidth-Ui(28f),false);
            var max = bubbleMin + new Vector2(bubbleWidth,textHeight+Ui(22f));
            ImGui.GetWindowDrawList().AddRectFilled(bubbleMin,max,ImGui.GetColorU32(AccentFieldBg),Ui(10f));
            LayoutChatRichText(message.Content,bubbleMin+UiVec(14f,11f),bubbleWidth-Ui(28f),true);
            ImGui.SetCursorScreenPos(new Vector2(textOrigin.X,max.Y+Ui(9f)));
            DrawAlphaChatLinks(message.Content, width - Ui(60f));
        }
        DrawAlphaChatReactionChips(message, textOrigin.X, origin.X + width);
        var bottom = ImGui.GetCursorScreenPos().Y;
        if (ImGui.IsRectVisible(bubbleMin, new Vector2(origin.X + width, bottom))) alphaChatUnreadMentions.Remove(message.Sequence);
        if (ChatPointerInput.IsHoveringRect(bubbleMin,new Vector2(origin.X+width,bottom)) && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) SelectAlphaChatPerson(user,message.Sequence);
        if (alphaChatJumpTo == message.Sequence)
        {
            if (alphaChatJumpToUnread)
            {
                // Anchor at the end of the newest unread message, including its reactions/card.
                ImGui.SetCursorScreenPos(new Vector2(origin.X, bottom));
                ImGui.Dummy(new Vector2(1f, 1f));
                ImGui.SetScrollHereY(1f);
                alphaChatUnread.RemoveWhere(sequence => sequence <= message.Sequence);
                alphaChatJumpToUnread = false;
            }
            else { ImGui.SetCursorScreenPos(origin); ImGui.SetScrollHereY(.35f); }
            alphaChatJumpTo = null; alphaChatJumpApplied = true;
            alphaChatScrollToBottom = false; alphaChatForceBottom = false;
        }
        alphaChatRowHeights[message.Sequence] = bottom + Ui(20f) - origin.Y;
        ImGui.SetCursorScreenPos(new Vector2(origin.X,bottom+Ui(20f)));
    }
    private void SelectAlphaChatPerson(AlphaChatUser user, long sequence)
    { alphaChatPerson = user; alphaChatContextMessage = sequence; alphaChatPersonRequested = true; }
    private void DrawAlphaChatPersonActions(AlphaChatUser user, long sequence = 0)
    {
        if (sequence > 0)
        {
            if (ImGui.MenuItem("Reply")) alphaChatReplyTo = sequence;
            if (ImGui.MenuItem("React")) alphaChatReactionPickerTarget = sequence;
            if (ImGui.MenuItem("Copy message") && alphaChatMessages.FirstOrDefault(m => m.Sequence == sequence) is { } message)
                ImGui.SetClipboardText(AlphaChatReplyPreview(message));
            ImGui.Separator();
        }
        if (CurrentSession is not { } session) return;
        DrawAlphaChatUsername(user,user.DisplayName,AccentHover);
        if (PatreonBadgeTier(user.AccountId) > 0) { ImGui.SameLine(0, Ui(6f)); DrawAlphaChatSupporterHeart(); }
        ImGui.Separator();
        if (DrawAlphaChatMenuAction("Mention",alphaChat.Connected && !AlphaChatBlocked(user.AccountId))) MentionAlphaChatUser(user);
        var own = user.AccountId == session.AccountId;
        var friend = friendsAccountId == session.AccountId && friends.Any(f=>f.AccountId==user.AccountId);
        if (DrawAlphaChatMenuAction("View profile",!AlphaChatBlocked(user.AccountId))) OpenProfilePage(session,user.AccountId,user.DisplayName);
        if (DrawAlphaChatMenuAction("Add friend",!own && !friend && !AlphaChatBlocked(user.AccountId))) _=SendPartyFriendRequestAsync(session,user.AccountId,user.DisplayName);
        if (DrawAlphaChatMenuAction("Message",friend && !own)) StartOrOpenConversation(session,user.AccountId,user.DisplayName);
        if (!own && !friend && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiFonts.SetTooltip("Add to friend to message");
        RefreshPartyDirectoryIfNeeded();
        var hostedRoom = !partyDirectoryLoading && partyDirectoryAccountId == session.AccountId && roomsClient.LastFailure is null
            ? partyDirectoryRooms.FirstOrDefault(room => string.Equals(room.HostAccountId, user.AccountId, StringComparison.OrdinalIgnoreCase))
            : null;
        var canJoinRoom = !own && hostedRoom is not null && !AlphaChatBlocked(user.AccountId) &&
            stream.WatchPartyJoinBlockReason is null &&
            stream.IsConnected && stream.ConnectionState == RealtimeConnectionState.Connected &&
            !(stream.Mode == StreamMode.Viewing && string.Equals(stream.HostId, user.AccountId, StringComparison.OrdinalIgnoreCase));
        if (DrawAlphaChatMenuAction("Join Watch Party",canJoinRoom) && hostedRoom is not null) JoinPartyDirectoryRoom(hostedRoom);
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && stream.WatchPartyJoinBlockReason is { } joinBlocked)
            UiFonts.SetTooltip(joinBlocked);
        using (ImRaii.PushColor(ImGuiCol.Text, Danger))
        {
            if (!own && sequence > 0 && ImGui.MenuItem("Report message")) RequestAlphaChatReport(sequence);
            if (!own && ImGui.MenuItem(AlphaChatBlocked(user.AccountId)?"Unblock user":"Block user"))
            { if (AlphaChatBlocked(user.AccountId)) UnblockAccount(session,user.AccountId); else QueueFriendBlockConfirmation(user.AccountId,user.DisplayName); }
        }
        if (own && sequence > 0)
        {
            ImGui.Separator();
            if (ImGui.MenuItem("Delete message")) _ = alphaChat.CommandAsync(new { type = "delete", sequence });
        }
#if DEBUG
        if (alphaChatCanModerate)
        {
            ImGui.Separator();
            if (!own && sequence>0 && ImGui.MenuItem("Delete message")) RequestAlphaChatModeration("delete",user,sequence);
            if (!own && ImGui.MenuItem("Kick temporarily…")) RequestAlphaChatModeration("kick",user);
            if (!own && ImGui.MenuItem("Ban from Alpha Chat…")) RequestAlphaChatModeration("ban",user);
        }
#endif
    }
    private static bool DrawAlphaChatMenuAction(string label,bool enabled)
    {
        return Core.StableMenuItem.Draw(label,enabled,MutedText);
    }
    private sealed record AlphaChatRoomPayload(RoomDirectoryDto? Room,string Status);
    private void DrawAlphaChatRoom(AlphaChatMessage message, float availableWidth)
    {
        AlphaChatRoomPayload? payload;
        try { payload = JsonSerializer.Deserialize<AlphaChatRoomPayload>(message.Content, AlphaChatClient.JsonOptions); } catch { payload = null; }
        var room = payload?.Room; var valid = payload?.Status == "valid" && room is not null;
        var expired=WatchPartyInviteExpired(message.Timestamp/1000);
        DrawSharedChatRoomCard(valid?room:null,availableWidth,payload?.Status=="unavailable"?"Room information unavailable":"Invalid room invitation",
            expired,expired?"Expired":"Join Watch Party",()=> { if (room is not null) JoinPartyDirectoryRoom(room); });
    }

    private void DrawSharedChatRoomCard(RoomDirectoryDto? room,float availableWidth,string invalidLabel,
        bool actionDisabled,string actionLabel,Action action)
    {
        var width = MathF.Min(availableWidth, Ui(760f));
        // Keep the horizontal layout at every width, with even thumbnail padding.
        var bodyHeight = Ui(190f) * 9f / 16f;
        using var border = ImRaii.PushColor(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, .55f));
        using var bg = ImRaii.PushColor(ImGuiCol.ChildBg, FrameBg);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(12f, 12f));
        using var card = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##alphaChatRoom", new Vector2(width, room is not null ? bodyHeight + Ui(24f) : Ui(100f)), true,
            ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse));
        if (!card) return;
        if (room is null)
        {
            ImGui.TextUnformatted(invalidLabel);
            ImGui.TextColored(MutedText, "This watch party could not be found. Joining is unavailable."); return;
        }
        var origin = ImGui.GetCursorScreenPos(); var inner = ImGui.GetContentRegionAvail().X;
        var thumbWidth = MathF.Min(Ui(190f), inner * .36f); var thumbHeight = thumbWidth * 9f / 16f;
        var thumbOrigin = origin + new Vector2(0f, (bodyHeight - thumbHeight) * .5f);
        EnsurePartyDirectoryMediaLoaded(room!);
        var media = GetPartyDirectoryMedia(room!); var draw = ImGui.GetWindowDrawList();
        DrawPartyDirectoryThumbnail(draw, room!, media, thumbOrigin, new Vector2(thumbWidth, thumbHeight));
        DrawPartyDirectoryPlaybackBadge(draw, room!, thumbOrigin + UiVec(8f, 8f));
        DrawPartyDirectoryViewerBadge(draw, room!.ViewerCount + 1, thumbOrigin + new Vector2(thumbWidth - Ui(8f), Ui(8f)));
        var content = origin + new Vector2(thumbWidth + Ui(16f), 0f);
        var textWidth = inner - thumbWidth - Ui(16f);
        var visibility = WatchPartyVisibilityText(room.Kind);
        Vector2 visibilityIconSize;
        using (UiFonts.PushIcon())
            visibilityIconSize = ImGui.CalcTextSize(WatchPartyVisibilityIcon(room.Kind).ToIconString()) * .82f;
        var visibilityWidth = visibilityIconSize.X + ImGui.CalcTextSize(visibility).X + Ui(20f);
        var vibeSpace = MathF.Max(0f, MathF.Min(textWidth * .4f, textWidth - visibilityWidth - Ui(146f)));
        var vibeWidth = DrawDirectoryPartyVibes(draw, room, new Vector2(origin.X + inner, content.Y), vibeSpace);
        var headingWidth = MathF.Max(Ui(20f), textWidth - visibilityWidth - Ui(8f) - (vibeWidth > 0f ? vibeWidth + Ui(8f) : 0f));
        var title = room.HostDisplayName + "’s Watch Party";
        var visibleTitle = FitPartyTextToWidth(title, headingWidth);
        ImGui.SetCursorScreenPos(content + UiVec(0f, 4f));
        ImGui.TextUnformatted(visibleTitle);
        if (visibleTitle != title && ImGui.IsItemHovered()) UiFonts.SetTooltip(title);
        DrawWatchPartyTag(draw, content + new Vector2(ImGui.CalcTextSize(visibleTitle).X + Ui(8f), 0f),
            WatchPartyVisibilityIcon(room.Kind), visibility, WatchPartyVisibilityColor(room.Kind));

        var mediaOrigin = content + UiVec(0f, 30f);
        var categoryWidth = DrawWatchPartyTag(draw, mediaOrigin, WatchPartyCategoryIcon(room),
            WatchPartyCategoryText(room), WatchPartyCategoryColor(room));
        ImGui.SetCursorScreenPos(mediaOrigin + new Vector2(categoryWidth + Ui(8f), Ui(4f)));
        using (currentPage is HomePage.Messages or HomePage.AlphaChat ? UiFonts.PushRelative(.90f) : UiFonts.Empty)
        ImGui.TextColored(MutedText, FitPartyTextToWidth(media.Title ?? "Waiting for content", MathF.Max(Ui(20f), textWidth - categoryWidth - Ui(8f))));

        var description = room.Description ?? "";
        var end = description.IndexOf("#>", StringComparison.Ordinal);
        if (end >= 0) description = description[(end + 2)..].Trim();
        ImGui.SetCursorScreenPos(content + UiVec(0f, 58f));
        using (currentPage is HomePage.Messages or HomePage.AlphaChat ? UiFonts.PushRelative(.90f) : UiFonts.Empty)
        ImGui.TextColored(MutedText, FitPartyTextToWidth(description, textWidth));

        var joinWidth = MathF.Min(Ui(140f), textWidth * .45f);
        var locationOrigin = content + new Vector2(0f, bodyHeight - ImGui.GetFontSize() - Ui(5f));
        var locationWidth = MathF.Max(Ui(20f), textWidth - joinWidth - Ui(12f));
        UiFonts.DrawIcon(draw, ImGui.GetFontSize(), locationOrigin, ImGui.GetColorU32(AccentHover), FontAwesomeIcon.MapMarkerAlt.ToIconString());
        ImGui.SetCursorScreenPos(locationOrigin + UiVec(20f, 0f));
        using (currentPage is HomePage.Messages or HomePage.AlphaChat ? UiFonts.PushRelative(.90f) : UiFonts.Empty)
        ImGui.TextColored(MutedText, FitPartyTextToWidth(WatchPartyLocation(room), MathF.Max(Ui(1f), locationWidth - Ui(20f))));
        if (ChatPointerInput.IsHoveringRect(locationOrigin, locationOrigin + new Vector2(locationWidth, ImGui.GetFontSize())))
        {
            var server = WatchPartyServer(room);
            UiFonts.SetTooltip(string.IsNullOrWhiteSpace(server) ? "Server not specified" : server);
        }
        ImGui.SetCursorScreenPos(new Vector2(origin.X + inner - joinWidth, origin.Y + bodyHeight - Ui(32f)));
        using (UiDisabled.Scope(actionDisabled))
            if (ActivePartyButton(actionLabel,new Vector2(joinWidth,Ui(32)),primary:true)) action();
    }
}

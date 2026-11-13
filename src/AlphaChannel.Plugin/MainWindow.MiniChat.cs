using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private Guid? miniChatLastVisibleItemId;
    private bool miniChatScrollToBottom = true;
    private readonly ChatFeedScroll miniChatFeedScroll = new();
    private bool miniChatWasAtBottom;
    private DateTime miniChatLastReadDrawUtc = DateTime.MinValue;

    // Compact rendering over the same history and composer used by the full
    // Watch Party page. Media requests deliberately remain summary-only here.
    internal void DrawMiniPartyChatContents()
    {
        if (stream.WaitingForHost) ImGui.TextColored(Gold, "Waiting for the host to return...");
        // Mini windows have their own ImGui style scope. Apply the full chat's
        // palette explicitly instead of inheriting Dalamud's scrollbar colours.
        using var chatColours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
            .Push(ImGuiCol.FrameBg, FrameBg).Push(ImGuiCol.FrameBgHovered, FrameBgHover)
            .Push(ImGuiCol.FrameBgActive, AccentFieldActive)
            .Push(ImGuiCol.ScrollbarBg, new Vector4(Accent.X, Accent.Y, Accent.Z, .10f))
            .Push(ImGuiCol.ScrollbarGrab, new Vector4(Accent.X, Accent.Y, Accent.Z, .55f))
            .Push(ImGuiCol.ScrollbarGrabHovered, AccentHover).Push(ImGuiCol.ScrollbarGrabActive, Accent);
        notificationMiniChatLastDrawUtc = DateTime.UtcNow;
        DrainPartyChat();
        DrainChatMessageReactions();

        if (stream.Mode is not (StreamMode.Hosting or StreamMode.Viewing))
        {
            ImGui.TextColored(MutedText, "Join or create a Watch Party to use Mini Chat.");
            return;
        }

        var composerHeight = Ui(partyChatReplyTarget is null ? 92f : 114f);
        using (var feed = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                   "##miniPartyChatFeed",
                   new Vector2(-1f, MathF.Max(Ui(100f), ImGui.GetContentRegionAvail().Y - composerHeight)),
                   true,
                   ImGuiWindowFlags.None)))
        {
            if (feed)
            {
                miniChatFeedScroll.Begin(Ui(35f));
                var wasAtBottom = ImGui.GetScrollMaxY() - ImGui.GetScrollY() <= Ui(35f);
                miniChatWasAtBottom = wasAtBottom;
                miniChatLastReadDrawUtc = DateTime.UtcNow;
                var visibleItems = partyChatItems.Where(ShouldDisplayPartyChatItem).ToList();
                var lastVisibleId = visibleItems.LastOrDefault()?.Id;
                var newItemAtBottom = lastVisibleId != miniChatLastVisibleItemId;
                miniChatLastVisibleItemId = lastVisibleId;
                var hasVisibleItems = visibleItems.Count > 0;
                if (!hasVisibleItems)
                {
                    ImGui.TextColored(MutedText, "No messages yet.");
                }

                PartyChatItem? previous = null;
                for (var index = 0; index < visibleItems.Count; index++)
                {
                    var item = visibleItems[index];
                    if (IsSupersededGameCountdown(visibleItems, index))
                    {
                        continue;
                    }

                    if (item.Kind == PartyChatItemKind.Reaction)
                    {
                        var reactions = new List<PartyChatItem> { item };
                        while (index + reactions.Count < visibleItems.Count && reactions.Count < 6)
                        {
                            var next = visibleItems[index + reactions.Count];
                            if (next.Kind != PartyChatItemKind.Reaction || next.UserId != item.UserId ||
                                next.Name != item.Name ||
                                (item.ReceivedAt is { } first && next.ReceivedAt is { } later &&
                                 later - first > TimeSpan.FromSeconds(10)))
                                break;
                            reactions.Add(next);
                        }
                        DrawMiniReactionNotice(reactions);
                        index += reactions.Count - 1;
                    }
                    else
                    {
                        ImGui.PushID(item.Id.ToString());
                        DrawMiniPartyChatItem(item, previous);
                        ImGui.PopID();
                    }
                    previous = item;
                }

                // Mini Chat has its own scroll position; the full chat's unread and
                // scroll state must not decide whether new messages are visible here.
                ImGui.Dummy(UiVec(1f, 12f));
                var scrollToBottom = miniChatScrollToBottom ||
                                     (newItemAtBottom && wasAtBottom);
                miniChatFeedScroll.Finish(scrollToBottom);
                miniChatScrollToBottom = false;
                miniChatWasAtBottom = miniChatFeedScroll.FollowingLatest;
                if (miniChatWasAtBottom)
                {
                    // Both chat windows display the same history and share its
                    // unread badge, but retain independent scroll positions.
                    partyChatUnreadIds.Clear();
                    partyChatFirstUnreadId = null;
                    partyChatUnreadDividerShownUtc = null;
                }
            }
        }

        ImGui.Spacing();
        DrawMiniPartyChatComposer();
        DrawMessageReportConfirmationOverlay(miniChat: true);
    }

    private void DrawMiniPartyChatItem(PartyChatItem item, PartyChatItem? previous)
    {
        var name = string.IsNullOrWhiteSpace(item.Name) ? "Someone" : item.Name;

        switch (item.Kind)
        {
            case PartyChatItemKind.Message:
                if (item.Name == "Pictionary")
                    DrawMiniPictionaryNotice(item);
                else
                    DrawMiniHumanMessage(item, previous);
                break;

            case PartyChatItemKind.QueueNotice:
                DrawMiniCenterNotice($"{name} {item.Text}", AccentHover,
                    item.Text.StartsWith("requested", StringComparison.Ordinal) ? FontAwesomeIcon.Video : FontAwesomeIcon.ListUl);
                break;

            case PartyChatItemKind.Reaction:
                DrawMiniReactionNotice([item]);
                break;

            case PartyChatItemKind.RoomPresence:
                DrawMiniCenterNotice($"{name} {item.Text}", MutedText,
                    item.Text.StartsWith("joined", StringComparison.Ordinal)
                        ? FontAwesomeIcon.UserPlus : FontAwesomeIcon.SignOutAlt);
                break;
        }
    }

    private void DrawMiniPartyChatComposer()
    {
        if (partyChatReplyTarget is { } replyTarget)
        {
            DrawUsername(replyTarget.UserId,$"Replying to {replyTarget.Name}",AccentHover);
            ImGui.SameLine(0f, Ui(6f));
            if (ImGui.SmallButton("×##cancelMiniChatReply"))
                partyChatReplyTarget = null;
        }

        var available = ImGui.GetContentRegionAvail().X;
        var buttonWidth = Ui(34f);
        using (ImRaii.PushColor(ImGuiCol.Button, Accent).Push(ImGuiCol.ButtonHovered, AccentHover)
                   .Push(ImGuiCol.ButtonActive, AccentActive))
        using (UiFonts.PushIcon())
        {
            if (ImGui.Button($"{FontAwesomeIcon.Smile.ToIconString()}##miniChatEmoji",
                    new Vector2(buttonWidth, Ui(30f))))
                ImGui.OpenPopup("##miniChatEmojiPicker");
        }
        if(ImGui.IsPopupOpen("##miniChatEmojiPicker"))
        {
            var viewport=ImGui.GetWindowViewport();
            var popupSize=UiVec(238f,244f);
            var minimum=viewport.WorkPos+UiVec(8f,8f);
            var maximum=Vector2.Max(minimum,viewport.WorkPos+viewport.WorkSize-popupSize-UiVec(8f,8f));
            var anchor=ImGui.GetItemRectMin();
            var y=anchor.Y-popupSize.Y-Ui(6f);
            if(y<minimum.Y)y=ImGui.GetItemRectMax().Y+Ui(6f);
            ImGui.SetNextWindowPos(Vector2.Clamp(new Vector2(anchor.X,y),minimum,maximum),ImGuiCond.Always);
        }
        ImGui.SetNextWindowSize(UiVec(238f, 0f), ImGuiCond.Appearing);
        using (ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, Ui(10f)).Push(ImGuiStyleVar.PopupBorderSize,Ui(1f))
            .Push(ImGuiStyleVar.ItemSpacing,UiVec(3f,5f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(10f, 10f)))
        using (ImRaii.PushColor(ImGuiCol.PopupBg,CardBg).Push(ImGuiCol.Border,Accent))
        {
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup("##miniChatEmojiPicker")))
            {
                DrawSocialHeading("Add an emoji");
                ImGui.Dummy(UiVec(0f, 3f));
                DrawMiniEmojiGrid("composer", emoji =>
                {
                    partyChatInput += ChatReactionShortcodes[emoji];
                    miniChatComposerInputRevision++;
                    miniChatFocusComposer = true;
                    miniChatMoveCaretToEnd = true;
                });
                ImGui.EndPopup();
            }
        }

        ImGui.SameLine(0f, Ui(5f));
        ImGui.SetNextItemWidth(MathF.Max(Ui(90f), available - buttonWidth * 2f - Ui(10f)));
        if (miniChatFocusComposer)
        {
            ImGui.SetKeyboardFocusHere();
            miniChatFocusComposer = false;
        }
        var inputPaddingY = MathF.Max(0f, (Ui(30f) - ImGui.GetTextLineHeight()) * 0.5f);
        bool enterPressed;
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding,
                   new Vector2(ImGui.GetStyle().FramePadding.X, inputPaddingY)))
        {
            enterPressed = ImGui.InputTextWithHint(
                $"##miniPartyChatInput{miniChatComposerInputRevision}",
                "Message the Watch Party...",
                ref partyChatInput,
                280,
                ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CallbackAlways,
                PlaceMiniChatCaretAfterMention);
        }

        ImGui.SameLine(0f, Ui(5f));
        var hasMessage = !string.IsNullOrWhiteSpace(partyChatInput);
        using (UiDisabled.Scope(!hasMessage))
        using (ImRaii.PushColor(ImGuiCol.Button, Accent).Push(ImGuiCol.ButtonHovered, AccentHover)
                   .Push(ImGuiCol.ButtonActive, AccentActive))
        using (UiFonts.PushIcon())
        {
            var sendClicked = ImGui.Button(
                $"{Dalamud.Interface.FontAwesomeIcon.PaperPlane.ToIconString()}##miniChatSend",
                new Vector2(buttonWidth, Ui(30f)));

            if ((enterPressed || sendClicked) && hasMessage)
            {
                var text = partyChatInput.Trim();
                partyChatInput = string.Empty;
                miniChatComposerInputRevision++;
                miniChatFocusComposer = true;
                SubmitPartyChatMessage(text);
                partyChatStickToBottom = true;
                miniChatScrollToBottom = true;
            }
        }

        ImGui.Dummy(UiVec(0f, 4f));
        var reactionRowStart = ImGui.GetCursorPos();
        var reactionRowWidth = ImGui.GetContentRegionAvail().X;
        var optionsWidth = Ui(42f);
        var reactionGap = Ui(8f);
        var reactionWidth = MathF.Max(
            Ui(150f),
            reactionRowWidth - optionsWidth - reactionGap);

        DrawCompactReactions(reactionWidth);

        ImGui.SetCursorPos(
            reactionRowStart +
            new Vector2(reactionWidth + reactionGap, 0f));
        DrawMiniPartyChatOptionsButton(optionsWidth);
    }

    private void DrawMiniPartyChatOptionsButton(float width)
    {
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(9f)))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   FrameBg)
                   .Push(
                       ImGuiCol.ButtonHovered,
                       FrameBgHover)
                   .Push(
                       ImGuiCol.ButtonActive,
                       AccentFieldActive))
        {
            if (ImGui.Button(
                    "...##miniPartyChatOptions",
                    new Vector2(width, Ui(40f))))
            {
                ImGui.OpenPopup("##miniPartyChatOptionsPopup");
            }
        }

        if (ImGui.IsItemHovered())
        {
            UiFonts.SetTooltip("Chat options");
        }

        if (ImGui.IsPopupOpen("##miniPartyChatOptionsPopup"))
        {
            // Anchor above the button and constrain the menu to Mini Chat,
            // rather than letting ImGui place it outside the window.
            var window = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();
            var inset = UiVec(8f, 8f);
            var size = Vector2.Min(UiVec(300f, 190f), Vector2.Max(Vector2.One, windowSize - inset * 2));
            var minimum = window + inset;
            var maximum = Vector2.Max(minimum, window + windowSize - inset - size);
            var anchor = new Vector2(ImGui.GetItemRectMax().X - size.X, ImGui.GetItemRectMin().Y - size.Y - Ui(6f));
            ImGui.SetNextWindowPos(Vector2.Clamp(anchor, minimum, maximum), ImGuiCond.Always);
            ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        }
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(14f, 12f))
                   .Push(ImGuiStyleVar.PopupRounding, Ui(10f)))
        {
            if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup("##miniPartyChatOptionsPopup")))
            {
                return;
            }

            DrawPartyChatOptionsContents();
            ImGui.EndPopup();
        }
    }

    private void DrawPartyChatOptionsContents()
    {
        using var uiFontScale11330=UiFonts.PushRelative(1f, emphasis: true);
        ImGui.TextColored(Hex(0xE2E7EF), "Chat options");
        uiFontScale11330.Dispose();

        ImGui.Dummy(UiVec(0f, 5f));

        var showReactionsInChat = Plugin.Cfg.ShowReactionsInPartyChat;
        if (ImGui.Checkbox(
                "Show my reactions in party chat",
                ref showReactionsInChat))
        {
            Plugin.Cfg.ShowReactionsInPartyChat = showReactionsInChat;
            Plugin.Cfg.Save();
        }

        ImGui.Dummy(UiVec(0f, 3f));
        using var uiFontScale11850=UiFonts.PushRelative(0.76f);
        ImGui.TextColored(
            MutedText,
            "Turn this off to show reactions on the TV\nwithout adding them to party chat.");
        uiFontScale11850.Dispose();

        ImGui.Dummy(UiVec(0f, 7f));

        var hideAllReactions = Plugin.Cfg.HideAllReactionsInPartyChat;
        if (ImGui.Checkbox(
                "Hide all reactions in party chat",
                ref hideAllReactions))
        {
            Plugin.Cfg.HideAllReactionsInPartyChat = hideAllReactions;
            Plugin.Cfg.Save();
        }

        if (ImGui.IsItemHovered())
        {
            UiFonts.SetTooltip("Reactions will still appear on the TV.");
        }
    }

    private static bool ShouldDisplayPartyChatItem(PartyChatItem item) =>
        (item.Kind != PartyChatItemKind.Reaction ||
         !Plugin.Cfg.HideAllReactionsInPartyChat);
}

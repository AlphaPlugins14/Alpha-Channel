using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private Guid? miniChatJumpToMessageId;
    private Guid? miniChatHighlightedMessageId;
    private DateTime miniChatHighlightUntilUtc;
    private bool miniChatFocusComposer;
    private bool miniChatMoveCaretToEnd;
    private int miniChatComposerInputRevision;

    private int PlaceMiniChatCaretAfterMention(ref ImGuiInputTextCallbackData data)
    {
        if (miniChatMoveCaretToEnd)
        {
            data.CursorPos = data.BufTextLen;
            data.SelectionStart = data.BufTextLen;
            data.SelectionEnd = data.BufTextLen;
            miniChatMoveCaretToEnd = false;
        }
        return 0;
    }

    private void DrawMiniHumanMessage(PartyChatItem item, PartyChatItem? previous)
    {
        var grouped = previous is { Kind: PartyChatItemKind.Message } && previous.Name != "Pictionary" &&
                      previous.UserId == item.UserId && item.ReceivedAt is { } received &&
                      previous.ReceivedAt is { } earlier && received - earlier < TimeSpan.FromMinutes(2);
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var avatar = Ui(30f);
        var textX = start.X + avatar + Ui(9f);
        var maxWidth = MathF.Max(Ui(100f), width - avatar - Ui(15f));
        var headerHeight = grouped ? 0f : Ui(22f);
        var draw = ImGui.GetWindowDrawList();

        if (!grouped)
        {
            if (CurrentSession is { } session &&
                string.Equals(item.UserId, session.AccountId, StringComparison.OrdinalIgnoreCase))
                DrawAvatarAt(start, session.AvatarIcon, session.AvatarColorHex, avatar, session.AvatarImageUrl);
            else
            {
                if (!string.IsNullOrWhiteSpace(item.UserId))
                    EnsurePartyAvatarLoaded(item.UserId, item.Name);
                if (!string.IsNullOrWhiteSpace(item.UserId) &&
                    partyAvatarCache.TryGetValue(item.UserId, out var info))
                    DrawAvatarAt(start, info.AvatarIcon, info.AvatarColorHex, avatar, info.AvatarImageUrl);
                else
                    DrawAvatarAt(start, null, null, avatar);
            }
            DrawUsernameAt(draw,new Vector2(textX, start.Y),item.Name,AccentHover,item.UserId);
            if (item.ReceivedAt is { } time)
            {
                var nameWidth = ImGui.CalcTextSize(item.Name).X;
                using var timestampFont=UiFonts.PushRelative(.90f);
                draw.AddText(new Vector2(textX + nameWidth + Ui(7f), start.Y),
                    ImGui.GetColorU32(MutedText), time.ToString("HH:mm"));
            }
        }

        var target = item.ReplyToId is { } replyId
            ? partyChatItems.FirstOrDefault(other => other.Id == replyId) : null;
        var quoteHeight = item.ReplyToId is null ? 0f : Ui(51f);
        var bubbleWidth = MathF.Min(maxWidth, MathF.Max(Ui(item.ReplyToId is null ? 82f : 220f),
            MeasureChatRichTextWidth(item.Text) + Ui(29f)));
        var bodyHeight = MathF.Max(Ui(30f),
            LayoutChatRichText(item.Text, Vector2.Zero, bubbleWidth - Ui(28f), false) + Ui(14f));
        var bubbleMin = new Vector2(textX, start.Y + headerHeight);
        var bubbleMax = bubbleMin + new Vector2(bubbleWidth, bodyHeight + quoteHeight);
        draw.AddRectFilled(bubbleMin, bubbleMax,
            ImGui.GetColorU32(AccentFieldBg), Ui(8f));

        if (miniChatHighlightedMessageId == item.Id)
        {
            if (DateTime.UtcNow < miniChatHighlightUntilUtc)
                draw.AddRect(bubbleMin - UiVec(2f, 2f), bubbleMax + UiVec(2f, 2f),
                    ImGui.GetColorU32(AccentHover), Ui(9f), ImDrawFlags.None, Ui(2f));
            else
                miniChatHighlightedMessageId = null;
        }

        ImGui.SetCursorScreenPos(bubbleMin);
        ImGui.InvisibleButton("##miniChatBubble", bubbleMax - bubbleMin);
        if (ChatPointerInput.IsHoveringRect(bubbleMin, bubbleMax) &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup("##miniChatMessageMenu");

        if (quoteHeight > 0f)
        {
            var quoteMin = bubbleMin + UiVec(9f, 6f);
            var quoteMax = new Vector2(bubbleMax.X - Ui(9f), quoteMin.Y + Ui(40f));
            draw.AddRectFilled(quoteMin, quoteMax,
                ImGui.GetColorU32(FrameBgHover), Ui(5f));
            draw.AddRectFilled(quoteMin, quoteMin + new Vector2(Ui(3f), Ui(40f)),
                ImGui.GetColorU32(Accent));
            var previewX = quoteMin.X + Ui(13f);
            draw.AddText(new Vector2(previewX, quoteMin.Y + Ui(3f)),
                ImGui.GetColorU32(AccentHover), target?.Name ?? "Original message");
            var preview = target is null ? "Message no longer available" :
                TruncateChatReplyPreview(target.Text, quoteMax.X - previewX - Ui(10f));
            LayoutChatRichText(preview, new Vector2(previewX, quoteMin.Y + Ui(20f)),
                quoteMax.X - previewX - Ui(10f), true, MutedText, interactiveLinks: false);
            ImGui.SetCursorScreenPos(quoteMin);
            if (ImGui.InvisibleButton("##miniChatQuote", quoteMax - quoteMin) && target is not null)
            {
                miniChatJumpToMessageId = target.Id;
                miniChatHighlightedMessageId = target.Id;
                miniChatHighlightUntilUtc = DateTime.UtcNow.AddSeconds(2.5);
                partyChatStickToBottom = false;
            }
            if (target is not null && ImGui.IsItemHovered())
                UiFonts.SetTooltip("Jump to original message");
        }

        var bodyTop = bubbleMin.Y + quoteHeight;
        if (ImGui.IsRectVisible(new Vector2(bubbleMin.X, bodyTop), bubbleMax))
            partyChatUnseenMentions.Remove(item.Id);
        var contentHeight = LayoutChatRichText(item.Text, Vector2.Zero,
            bubbleWidth - Ui(28f), false);
        LayoutChatRichText(item.Text,
            new Vector2(bubbleMin.X + Ui(14f), bodyTop + (bodyHeight - contentHeight) * 0.5f),
            bubbleWidth - Ui(28f), true);
        DrawMiniMessageContextMenu(item);

        if (miniChatJumpToMessageId == item.Id)
        {
            miniChatFeedScroll.Cancel();
            var desiredTop = ImGui.GetWindowPos().Y + Ui(45f);
            ImGui.SetScrollY(MathF.Max(0f, ImGui.GetScrollY() + bubbleMin.Y - desiredTop));
            miniChatJumpToMessageId = null;
        }

        var bottom = bubbleMax.Y;
        if (partyChatMessageReactions.TryGetValue(item.Id, out var groups))
        {
            var chipX = bubbleMin.X;
            var chipY = bottom + Ui(4f);
            foreach (var (emoji, users) in groups.OrderBy(group => group.Key))
            {
                if (chipX + Ui(48f) > start.X + width)
                {
                    chipX = bubbleMin.X;
                    chipY += Ui(29f);
                }
                ImGui.SetCursorScreenPos(new Vector2(chipX, chipY));
                var reactedByMe = CurrentSession is { } session && users.Contains(session.AccountId);
                if (DrawChatReactionChip(item.Id, emoji, users, reactedByMe))
                    _ = stream.SendChatMessageReactionAsync(item.Id, emoji);
                chipX += Ui(54f);
                bottom = MathF.Max(bottom, ImGui.GetItemRectMax().Y);
            }
        }

        ImGui.SetCursorScreenPos(new Vector2(textX, bottom + Ui(4f)));
        DrawAlphaChatLinks(item.Text, maxWidth, imagesOnly: true);
        bottom = MathF.Max(bottom, ImGui.GetCursorScreenPos().Y);
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(width, bottom - start.Y + Ui(grouped ? 7f : 12f)));
    }

    private void DrawMiniMessageContextMenu(PartyChatItem item)
    {
        using (ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, Ui(9f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(10f, 9f)))
        {
            if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup("##miniChatMessageMenu")))
                return;

            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginMenu("React")))
            {
                DrawMiniEmojiGrid($"react{item.Id}", emoji =>
                    _ = stream.SendChatMessageReactionAsync(item.Id, emoji));
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem("Reply"))
                partyChatReplyTarget = item;
            if (ImGui.MenuItem("Mention"))
            {
                partyChatInput += $"@{item.Name} ";
                miniChatComposerInputRevision++;
            }
            if (ImGui.MenuItem("Copy message"))
                ImGui.SetClipboardText(ExpandChatEmojiShortcodes(item.Text));

            var canDelete = item.HasSharedMessageId && stream.IsConnected &&
                !string.IsNullOrWhiteSpace(stream.LocalUserId) &&
                (stream.Mode == StreamMode.Hosting ||
                 string.Equals(item.UserId, stream.LocalUserId, StringComparison.OrdinalIgnoreCase));
            if (canDelete && ImGui.MenuItem("Delete message"))
                _ = stream.SendChatMessageDeleteAsync(item.Id);

            if (CurrentSession is { } session &&
                !string.Equals(item.UserId, session.AccountId, StringComparison.OrdinalIgnoreCase))
            {
                using (ImRaii.PushColor(ImGuiCol.Text, Danger))
                {
                    if (ImGui.MenuItem("Report"))
                        QueuePartyMessageReport(item, fromMiniChat: true);
                }
            }
            ImGui.EndPopup();
        }
    }

    private void DrawMiniEmojiGrid(string id, Action<int> onPick)
    {
        ImGui.PushID(id);
        for (var emoji = 0; emoji < ChatReactionIcons.Length; emoji++)
        {
            if (emoji % 5 != 0) ImGui.SameLine(0f, Ui(3f));
            var clicked = ImGui.InvisibleButton($"##miniEmoji{emoji}", UiVec(39f, 39f));
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            if (ImGui.IsItemHovered())
            {
                ImGui.GetWindowDrawList().AddRectFilled(min, max,
                    ImGui.GetColorU32(new Vector4(0.18f, 0.17f, 0.27f, 1f)), Ui(7f));
                UiFonts.SetTooltip(ChatReactionLabels[emoji]);
            }
            var icon = ChatReactionIcons[emoji].ToIconString();
            Vector2 iconSize;
            using (UiFonts.PushIcon())
                iconSize = ImGui.CalcTextSize(icon);
            UiFonts.DrawIcon(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), min + (max - min - iconSize) * 0.5f, ImGui.GetColorU32(ChatEmojiColor(emoji)), icon);
            if (clicked)
            {
                onPick(emoji);
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.PopID();
    }

    private bool DrawMiniCenterNotice(string text, Vector4 color, FontAwesomeIcon icon,
        string? tooltip = null)
    {
        var start = ImGui.GetCursorScreenPos();
        var available = ImGui.GetContentRegionAvail().X;
        var label = TruncateToWidth(text, MathF.Max(Ui(40f), available - Ui(61f)));
        var width = MathF.Min(available,
            MathF.Max(Ui(155f), ImGui.CalcTextSize(label).X + Ui(48f)));
        var min = new Vector2(start.X + (available - width) * 0.5f, start.Y);
        var size = new Vector2(width, Ui(31f));
        ImGui.SetCursorScreenPos(min);
        var clicked = ImGui.InvisibleButton("##miniNotice", size);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + size,
            ImGui.GetColorU32(AccentFieldBg), Ui(8f));
        UiFonts.DrawIcon(draw, ImGui.GetFontSize(), min + UiVec(10f, 8f), ImGui.GetColorU32(color), icon.ToIconString());
        draw.AddText(min + UiVec(32f, 8f), ImGui.GetColorU32(color), label);
        if (ImGui.IsItemHovered() && (tooltip is not null || label != text))
        {
            if (tooltip is not null)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            UiFonts.SetTooltip(tooltip ?? text);
        }
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(available, size.Y + Ui(7f)));
        return clicked;
    }

    private void DrawMiniReactionNotice(IReadOnlyList<PartyChatItem> reactions)
    {
        var item = reactions[0];
        ImGui.PushID(item.Id.ToString());
        var start = ImGui.GetCursorScreenPos();
        var available = ImGui.GetContentRegionAvail().X;
        var label = reactions.Count == 1 ? $"{item.Name} reacted" :
            $"{item.Name} sent {reactions.Count} reactions";
        var iconSpace = Ui(23f * reactions.Count);
        label = TruncateToWidth(label, MathF.Max(Ui(50f), available - iconSpace - Ui(27f)));
        var width = MathF.Min(available,
            MathF.Max(Ui(150f), ImGui.CalcTextSize(label).X + iconSpace + Ui(25f)));
        var min = new Vector2(start.X + (available - width) * 0.5f, start.Y);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + UiVec(width, 31f),
            ImGui.GetColorU32(AccentFieldBg), Ui(8f));
        draw.AddText(min + UiVec(11f, 8f), ImGui.GetColorU32(MutedText), label);
        for (var index = 0; index < reactions.Count; index++)
            UiFonts.DrawIcon(draw, ImGui.GetFontSize(), new Vector2(min.X + width - iconSpace + Ui(23f * index), min.Y + Ui(8f)), ImGui.GetColorU32(GetReactionDisplayColor(reactions[index].Text)), reactions[index].Text);
        ImGui.Dummy(new Vector2(available, Ui(38f)));
        ImGui.PopID();
    }

    private void DrawMiniPictionaryNotice(PartyChatItem item)
    {
        var score = item.Text.Contains("guessed correctly!", StringComparison.OrdinalIgnoreCase) ||
                    item.Text.StartsWith("Cycle winner:", StringComparison.OrdinalIgnoreCase) ||
                    item.Text.StartsWith("Cycle tie:", StringComparison.OrdinalIgnoreCase);
        DrawMiniCenterNotice(item.Text, score ? Good : AccentHover,
            score ? FontAwesomeIcon.Trophy : FontAwesomeIcon.PaintBrush);
    }
}

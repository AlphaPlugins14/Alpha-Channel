using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // Retained for the existing mini-chat jump handler; main chat has no filters.
    private enum PartyChatFilter { All, Chat, Requests, Other }
    private PartyChatFilter partyChatFilter = PartyChatFilter.All;
    private string partyChatSearch = string.Empty;
    private Guid? partyChatMenuReactionRequested;
    private Guid? partyChatMenuActionsRequested;
    private Guid? partyChatFirstUnreadId;
    private DateTime? partyChatUnreadDividerShownUtc;
    private readonly HashSet<Guid> partyChatUnreadIds = [];
    private int partyChatUnreadCount => partyChatUnreadIds.Count;
    private readonly UnseenChatMentions partyChatUnseenMentions = new();
    private int partyChatMentionUnreadCount => partyChatUnseenMentions.Count;
    private bool partyChatWasAtBottom = true;
    private readonly ChatFeedScroll partyChatFeedScroll = new();
    private DateTime partyChatLastDrawUtc = DateTime.MinValue;

    private void ResetPartyChatState()
    {
        partyChatItems.Clear();
        partyChatMessageReactions.Clear();
        partyChatReactionNames.Clear();
        partyChatUnreadIds.Clear();
        partyChatUnseenMentions.Clear();
        partyChatFirstUnreadId = null;
        partyChatUnreadDividerShownUtc = null;
        partyChatReplyTarget = null;
        partyChatJumpToMessageId = null;
        partyChatHighlightedMessageId = null;
        partyChatFeedScroll.Cancel();
        miniChatFeedScroll.Cancel();
        partyChatStickToBottom = partyChatWasAtBottom = true;
        miniChatWasAtBottom = false;
        miniChatLastReadDrawUtc = DateTime.MinValue;
        miniChatLastVisibleItemId = null;
        miniChatScrollToBottom = true;
        ClearPartyNotificationSounds();
    }
    private PartyChatItem? partyChatReplyTarget;
    private Guid? partyChatJumpToMessageId;
    private Guid? partyChatHighlightedMessageId;
    private DateTime partyChatHighlightUntilUtc;
    private bool partyChatFocusComposer;
    private bool partyChatMoveCaretToEnd;
    private readonly Dictionary<Guid, Dictionary<int, HashSet<string>>> partyChatMessageReactions = [];
    private const int ChatMessageReactionCount = 20;
    private static readonly string[] ChatReactionGlyphs =
        ["♥", "😂", "👍", "🎉", "😮", "🔥", "😀", "😢", "😠", "😜",
         "👻", "🚀", "🏆", "🎵", "🍕", "🎁", "🐱", "☀", "🌙", "🌈"];
    private static readonly string[] ChatReactionLabels =
        ["Love", "Laugh", "Like", "Celebrate", "Surprised", "Fire", "Smile", "Crying",
         "Angry", "Cheeky", "Ghost", "Rocket", "Victory", "Music", "Pizza", "Gift",
         "Cat", "Sunny", "Night", "Rainbow"];
    private static readonly FontAwesomeIcon[] ChatReactionIcons =
        [FontAwesomeIcon.Heart, FontAwesomeIcon.Laugh, FontAwesomeIcon.ThumbsUp,
         FontAwesomeIcon.Star, FontAwesomeIcon.Surprise, FontAwesomeIcon.Fire,
         FontAwesomeIcon.Smile, FontAwesomeIcon.SadTear, FontAwesomeIcon.Angry,
         FontAwesomeIcon.GrinTongueWink, FontAwesomeIcon.Ghost, FontAwesomeIcon.Rocket,
         FontAwesomeIcon.Trophy, FontAwesomeIcon.Music, FontAwesomeIcon.PizzaSlice,
         FontAwesomeIcon.Gift, FontAwesomeIcon.Cat, FontAwesomeIcon.Sun,
         FontAwesomeIcon.Moon, FontAwesomeIcon.Rainbow];
    private static readonly string[] ChatReactionShortcodes =
        [":love:", ":laugh:", ":like:", ":celebrate:", ":surprised:", ":fire:",
         ":smile:", ":cry:", ":angry:", ":cheeky:", ":ghost:", ":rocket:",
         ":victory:", ":music:", ":pizza:", ":gift:", ":cat:", ":sun:",
         ":moon:", ":rainbow:"];
    private int partyChatComposerInputRevision;

    private bool ShowModernChatItem(PartyChatItem item)
    {
        if (!ShouldDisplayPartyChatItem(item))
            return false;
        if (PictionaryActive && item.Name == "Pictionary" &&
            (item.Text.EndsWith(" is drawing!", StringComparison.Ordinal) ||
             item.Text.EndsWith("seconds remaining", StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    private void DrawPartyChatToolbar(float width)
    {
        using var rounding=ImRaii.PushStyle(ImGuiStyleVar.ChildRounding,Ui(12));
        using var spacing=ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing,UiVec(8,8));
        using var padding=ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,UiVec(16,14));
        using var bg=ImRaii.PushColor(ImGuiCol.ChildBg,CardBg);
        using var child=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##partyChatToolbar",new Vector2(width,Ui(width<Ui(620)?108:62)),true,
            ImGuiWindowFlags.AlwaysUseWindowPadding|ImGuiWindowFlags.NoScrollbar|ImGuiWindowFlags.NoScrollWithMouse));
        if (!child) return;
        var origin=ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddCircleFilled(origin+UiVec(10,18),Ui(7),ImGui.GetColorU32(stream.IsConnected?Good:Gold));
        ImGui.Dummy(UiVec(24,36)); ImGui.SameLine();
        var people = stream.Roster.Length+1;
        if (AlphaChatButton("partyChatPeople",$"{people} {(people==1?"person":"people")} here",FontAwesomeIcon.Users,Ui(174)))
            partyChatRosterOpen=true;
        if (width<Ui(620)) ImGui.NewLine();
        else { ImGui.SameLine(); ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(),ImGui.GetWindowContentRegionMax().X-Ui(306))); }
        DrawCompactReactions(Ui(174),Ui(36),openBelow:true);
        ImGui.SameLine();
        if (AlphaChatButton("partyChatPopout","",FontAwesomeIcon.ExternalLinkAlt,Ui(36))) OnMiniChatRequested?.Invoke();
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Open Mini Chat");
        ImGui.SameLine();
        if (AlphaChatButton("partyChatSettings","",FontAwesomeIcon.Cog,Ui(36))) ImGui.OpenPopup("##partyChatSettings");
        if (ImGui.IsPopupOpen("##partyChatSettings")) PositionPartyChatPopup(UiVec(320f, 220f));
        using (ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, Ui(10f)).Push(ImGuiStyleVar.PopupBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(14f, 12f)))
        using (ImRaii.PushColor(ImGuiCol.PopupBg, CardBg).Push(ImGuiCol.Border, Accent))
        {
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup("##partyChatSettings")))
            { DrawPartyChatOptionsContents(); ImGui.EndPopup(); }
        }
        ImGui.SameLine();
        if (AlphaChatButton("partyChatLeave","",FontAwesomeIcon.SignOutAlt,Ui(36),danger:true,
            iconColor:new Vector4(1f,.57f,.62f,1f))) RequestLeaveWatchParty();
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip(stream.Mode==StreamMode.Hosting?"End Watch Party":"Leave Watch Party");
    }

    private void DrawPartyChatFeedModern(float width, float height)
    {
        using var bg = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg);
        using (var child = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##partyChatModernLog", new Vector2(width, height), false,ImGuiWindowFlags.AlwaysUseWindowPadding)))
        {
            if (!child) return;
            partyChatFeedScroll.Begin(Ui(35f));
            DrainChatMessageReactions();
            var list = partyChatItems.Where(ShowModernChatItem).ToList();
            var unreadIndex = partyChatFirstUnreadId is { } unreadId
                ? partyChatItems.FindIndex(item => item.Id == unreadId)
                : -1;
            var firstVisibleUnreadId = unreadIndex >= 0
                ? partyChatItems.Skip(unreadIndex).FirstOrDefault(ShowModernChatItem)?.Id
                : null;
            if (list.Count == 0)
            {
                ImGui.Dummy(UiVec(0f, 25f));
                DrawBrowserHelper(stream.IsConnected ? "No messages yet. Start the conversation!" : "Reconnecting to party chat...");
            }
            else
            {
                ImGui.Dummy(UiVec(0f, 6f));
                var contentWidth = MathF.Min(MathF.Max(Ui(180f), width - Ui(64f)), Ui(680f));
                var contentX = MathF.Max(Ui(18f), (width - contentWidth) * 0.5f);
                PartyChatItem? previous = null;
                for (var index = 0; index < list.Count; index++)
                {
                    var item = list[index];
                    if (IsSupersededGameCountdown(list, index)) continue;
                    if (item.Kind == PartyChatItemKind.Reaction)
                    {
                        var reactions = new List<PartyChatItem> { item };
                        while (index + reactions.Count < list.Count && reactions.Count < 6)
                        {
                            var nextReaction = list[index + reactions.Count];
                            if (nextReaction.Kind != PartyChatItemKind.Reaction ||
                                nextReaction.UserId != item.UserId ||
                                nextReaction.Name != item.Name ||
                                nextReaction.Id == firstVisibleUnreadId ||
                                (item.ReceivedAt is { } firstTime &&
                                 nextReaction.ReceivedAt is { } laterTime &&
                                 laterTime - firstTime > TimeSpan.FromSeconds(10)))
                                break;
                            reactions.Add(nextReaction);
                        }
                        if (firstVisibleUnreadId == item.Id)
                            DrawPartyChatUnreadDivider(contentX, contentWidth);
                        ImGui.SetCursorPosX(contentX);
                        DrawModernReactionNotice(reactions, contentWidth);
                        index += reactions.Count - 1;
                        previous = item;
                        continue;
                    }
                    if (firstVisibleUnreadId == item.Id)
                        DrawPartyChatUnreadDivider(contentX, contentWidth);
                    if (item.Kind == PartyChatItemKind.RoomPresence)
                    {
                        ImGui.SetCursorPosX(contentX);
                        DrawModernRoomPresence(item, contentWidth);
                    }
                    else if (item.Kind == PartyChatItemKind.QueueNotice)
                    {
                        ImGui.SetCursorPosX(contentX);
                        DrawModernQueueNotice(item, contentWidth);
                    }
                    else if (item.Kind == PartyChatItemKind.Message && item.Name is not ("Pictionary" or "Quiz"))
                    {
                        var messageX = Ui(16f);
                        var messageWidth = MathF.Max(Ui(180f), width - Ui(36f));
                        ImGui.SetCursorPosX(messageX);
                        DrawModernHumanMessage(item, previous,
                            index + 1 < list.Count ? list[index + 1] : null,
                            messageWidth);
                    }
                    else
                    {
                        ImGui.SetCursorPosX(contentX);
                        DrawModernActivity(item, contentWidth);
                    }
                    previous = item;
                }
            }

            // Scroll to a real trailing item so the final message has breathing room.
            ImGui.Dummy(UiVec(1f, 28f));
            partyChatFeedScroll.Finish(partyChatStickToBottom);
            partyChatStickToBottom = false;
            var awayFromBottom = !partyChatFeedScroll.FollowingLatest;
            partyChatWasAtBottom = !awayFromBottom;
            if (awayFromBottom)
            {
                var old = ImGui.GetCursorScreenPos();
                var pos = ImGui.GetWindowPos();
                var size = ImGui.GetWindowSize();
                ImGui.SetCursorScreenPos(pos + new Vector2(size.X - Ui(partyChatMentionUnreadCount > 0 ? 212f : 165f), size.Y - Ui(48f)));
                using var jumpStyle = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(17f))
                    .Push(ImGuiStyleVar.FrameBorderSize, Ui(1f));
                using var jumpColor = ImRaii.PushColor(ImGuiCol.Button, FrameBg)
                    .Push(ImGuiCol.Border, new Vector4(0.35f, 0.30f, 0.56f, 1f));
                if (ImGui.Button("↓  Jump to latest##partyChatLatest", UiVec(145f, 32f)))
                {
                    partyChatStickToBottom = true;
                    partyChatWasAtBottom = true;
                    partyChatFirstUnreadId = null;
                    partyChatUnreadDividerShownUtc = null;
                    partyChatUnreadIds.Clear();
                }
                ImGui.SetCursorScreenPos(old);
            }
            else if (partyPanelTab == PartyPanelTab.Chat && currentPage == HomePage.WatchAlong)
            {
                partyChatUnreadIds.Clear();
                if (partyChatFirstUnreadId is not null)
                {
                    partyChatUnreadDividerShownUtc ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - partyChatUnreadDividerShownUtc > TimeSpan.FromSeconds(3))
                    {
                        partyChatFirstUnreadId = null;
                        partyChatUnreadDividerShownUtc = null;
                    }
                }
            }
            DrawPartyChatMentionJumpButton();

        }
    }

    private static bool IsSupersededGameCountdown(IReadOnlyList<PartyChatItem> items, int index)
    {
        var item = items[index];
        if (item.Name != "Pictionary" || !item.Text.EndsWith("seconds remaining", StringComparison.OrdinalIgnoreCase))
            return false;
        for (var next = index + 1; next < items.Count; next++)
        {
            if (items[next].Name != "Pictionary") continue;
            return items[next].Text.EndsWith("seconds remaining", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private void DrawPartyChatUnreadDivider(float contentX, float width)
    {
        var origin = new Vector2(ImGui.GetWindowPos().X + contentX, ImGui.GetCursorScreenPos().Y);
        var draw = ImGui.GetWindowDrawList();
        var center = origin.X + width * 0.5f;
        draw.AddLine(new Vector2(origin.X, origin.Y + Ui(10f)), new Vector2(center - Ui(85f), origin.Y + Ui(10f)), ImGui.GetColorU32(Accent), 1f);
        draw.AddLine(new Vector2(center + Ui(85f), origin.Y + Ui(10f)), new Vector2(origin.X + width, origin.Y + Ui(10f)), ImGui.GetColorU32(Accent), 1f);
        ImGui.SetCursorScreenPos(new Vector2(center - Ui(63f), origin.Y));
        ImGui.TextColored(AccentHover, "Unread messages");
        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + Ui(30f)));
    }

    private void DrawModernActivity(PartyChatItem item, float width)
    {
        if (item.Kind == PartyChatItemKind.QueueNotice)
        {
            DrawModernQueueNotice(item, width);
            return;
        }
        if (item.Kind == PartyChatItemKind.Reaction)
        {
            DrawModernReactionNotice([item], width);
            return;
        }
        if (item.Name == "Quiz")
        {
            DrawQuizResultNotice(item, width);
            return;
        }
        var game = item.Name == "Pictionary";
        var cycleWinner = game &&
            (item.Text.StartsWith("Cycle winner:", StringComparison.OrdinalIgnoreCase) ||
             item.Text.StartsWith("Cycle tie:", StringComparison.OrdinalIgnoreCase));
        if (game && (item.Text.Contains("guessed correctly!", StringComparison.OrdinalIgnoreCase) || cycleWinner))
        {
            DrawPictionaryScoreNotice(item, width, cycleWinner);
            return;
        }
        if (game)
        {
            var answerText = item.Text;
            var roundLabel = "Answer revealed";
            if (answerText.StartsWith("Round ", StringComparison.OrdinalIgnoreCase))
            {
                var separator = answerText.IndexOf(": The answer was ", StringComparison.OrdinalIgnoreCase);
                if (separator > 0)
                {
                    roundLabel = answerText[..separator] + " answer";
                    answerText = answerText[(separator + 2)..];
                }
            }
            if (answerText.StartsWith("The answer was ", StringComparison.OrdinalIgnoreCase))
            {
                var answer = answerText["The answer was ".Length..].TrimEnd('.');
                DrawPictionaryEventNotice(width, FontAwesomeIcon.PaintBrush, roundLabel, answer);
                return;
            }
            DrawPictionaryEventNotice(width,
                item.Text.EndsWith("seconds remaining", StringComparison.OrdinalIgnoreCase)
                    ? FontAwesomeIcon.Clock : FontAwesomeIcon.PaintBrush,
                "Pictionary", item.Text);
            return;
        }
        var line = $"{item.Name} · {item.Text}";
        var tint = CardBg;
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(8f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(12f, 8f));
        using var color = ImRaii.PushColor(ImGuiCol.ChildBg, tint)
            .Push(ImGuiCol.Border, tint);
        var lineWidth = MathF.Min(width, MathF.Max(Ui(230f), ImGui.CalcTextSize(line).X + Ui(44f)));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (width - lineWidth) * 0.5f);
        using (var child = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child($"##chatActivity{item.Id}", new Vector2(lineWidth, Ui(34f)), false,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (child)
                ImGui.TextColored(MutedText, line);
        }
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() - (width - lineWidth) * 0.5f);
        ImGui.Dummy(UiVec(0f, 5f));
    }

    private void DrawQuizResultNotice(PartyChatItem item, float width)
    {
        var start = ImGui.GetCursorScreenPos();
        var noticeWidth = MathF.Min(width, Ui(600f));
        var min = new Vector2(start.X + (width - noticeWidth) * 0.5f, start.Y + Ui(4f));
        var max = min + new Vector2(noticeWidth, Ui(52f));
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.035f, 0.20f, 0.15f, 0.94f)), Ui(9f));
        draw.AddRect(min, max, ImGui.GetColorU32(new Vector4(0.14f, 0.70f, 0.49f, 0.85f)), Ui(9f));
        UiFonts.DrawIcon(draw, ImGui.GetFontSize() * 1.12f, min + UiVec(16f, 17f), ImGui.GetColorU32(new Vector4(0.39f, 0.90f, 0.67f, 1f)), FontAwesomeIcon.Trophy.ToIconString());
        draw.AddText(min + UiVec(48f, 17f), ImGui.GetColorU32(Vector4.One),
            TruncateToWidth(item.Text, noticeWidth - Ui(68f)));
        ImGui.SetCursorScreenPos(new Vector2(start.X, max.Y + Ui(10f)));
    }

    private void DrawPictionaryEventNotice(float width, FontAwesomeIcon icon, string label, string detail)
    {
        var start = ImGui.GetCursorScreenPos();
        var labelWidth = ImGui.CalcTextSize(label).X;
        var detailWidth = ImGui.CalcTextSize(detail).X;
        var noticeWidth = MathF.Min(width,
            MathF.Max(Ui(250f), labelWidth + detailWidth + Ui(78f)));
        var min = new Vector2(start.X + (width - noticeWidth) * 0.5f, start.Y + Ui(3f));
        var availableInlineWidth = noticeWidth - labelWidth - Ui(78f);
        var useSecondLine = detailWidth > availableInlineWidth;
        var max = min + new Vector2(noticeWidth, Ui(useSecondLine ? 58f : 40f));
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max,
            ImGui.GetColorU32(CardBg), Ui(8f));
        draw.AddRect(min, max,
            ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.42f)), Ui(8f));
        UiFonts.DrawIcon(draw, ImGui.GetFontSize(), min + UiVec(13f, 11f), ImGui.GetColorU32(AccentHover), icon.ToIconString());
        var textStart = min + UiVec(39f, 11f);
        draw.AddText(textStart, ImGui.GetColorU32(AccentHover), label);
        var detailStart = useSecondLine
            ? min + UiVec(39f, 31f)
            : textStart + new Vector2(labelWidth + Ui(10f), 0f);
        var availableDetailWidth = useSecondLine
            ? noticeWidth - Ui(54f)
            : availableInlineWidth;
        while (detail.Length > 1 && ImGui.CalcTextSize(detail + "…").X > availableDetailWidth)
            detail = detail[..^1];
        if (detail.Length > 1 && detailWidth > availableDetailWidth)
            detail = detail.TrimEnd() + "…";
        draw.AddText(detailStart, ImGui.GetColorU32(Vector4.One), detail);
        ImGui.SetCursorScreenPos(new Vector2(start.X, max.Y + Ui(5f)));
    }

    private void DrawModernReactionNotice(IReadOnlyList<PartyChatItem> reactions, float width)
    {
        var item = reactions[0];
        var start = ImGui.GetCursorScreenPos();
        var label = reactions.Count == 1 ? $"{item.Name} reacted" :
            $"{item.Name} sent {reactions.Count} reactions";
        var noticeWidth = MathF.Min(width,
            MathF.Max(Ui(120f), ImGui.CalcTextSize(label).X + Ui(30f + reactions.Count * 25f)));
        var min = new Vector2(start.X + (width - noticeWidth) * 0.5f, start.Y);
        var max = min + new Vector2(noticeWidth, Ui(35f));
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, ImGui.GetColorU32(AccentFieldBg), Ui(10f));
        draw.AddText(min + UiVec(13f, 9f), ImGui.GetColorU32(MutedText), label);
        for (var index = 0; index < reactions.Count; index++)
        {
            var emoji = Array.FindIndex(ChatReactionIcons,
                icon => icon.ToIconString() == reactions[index].Text);
            var liveReaction = Reactions.FirstOrDefault(reaction =>
                reaction.Icon.ToIconString() == reactions[index].Text);
            UiFonts.DrawIcon(draw, ImGui.GetFontSize(), new Vector2(max.X - Ui(13f + (reactions.Count - index) * 25f), min.Y + Ui(8f)), ImGui.GetColorU32(liveReaction?.BadgeColor ??
                    (emoji >= 0 ? ChatEmojiColor(emoji) : AccentHover)), reactions[index].Text);
        }
        ImGui.SetCursorScreenPos(new Vector2(start.X, max.Y + Ui(7f)));
    }

    private void DrawModernQueueNotice(PartyChatItem item, float width)
    {
        var label = FitPartyTextToWidth($"{item.Name} {item.Text}", MathF.Max(1, width - Ui(54)));
        var start = ImGui.GetCursorScreenPos();
        var noticeWidth = MathF.Min(width, ImGui.CalcTextSize(label).X + Ui(54));
        var min = start + new Vector2((width - noticeWidth) * .5f, Ui(3));
        var max = min + new Vector2(noticeWidth, Ui(35));
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, ImGui.GetColorU32(AccentFieldBg), Ui(10));
        var icon = item.Text.StartsWith("requested", StringComparison.Ordinal) ? FontAwesomeIcon.Video : FontAwesomeIcon.ListUl;
        UiFonts.DrawIcon(draw, ImGui.GetFontSize(), min + UiVec(12, 9), ImGui.GetColorU32(AccentHover), icon.ToIconString());
        UiFonts.DrawText(draw, ImGui.GetFontSize(), min + UiVec(34, 9), ImGui.GetColorU32(MutedText), label);
        ImGui.Dummy(new Vector2(width, Ui(46)));
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip($"{item.Name} {item.Text}");
    }

    private void DrawModernRoomPresence(PartyChatItem item, float width)
    {
        var joined = item.Text.StartsWith("joined", StringComparison.Ordinal);
        var label = $"{item.Name} {item.Text}";
        var icon = joined ? FontAwesomeIcon.UserPlus : FontAwesomeIcon.SignOutAlt;
        var iconColor = joined
            ? new Vector4(0.39f, 0.82f, 0.62f, 1f)
            : MutedText;
        var start = ImGui.GetCursorScreenPos();
        var noticeWidth = MathF.Min(width, MathF.Max(Ui(170f),
            ImGui.CalcTextSize(label).X + Ui(54f)));
        var min = new Vector2(start.X + (width - noticeWidth) * 0.5f, start.Y + Ui(3f));
        var max = min + new Vector2(noticeWidth, Ui(34f));
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max,
            ImGui.GetColorU32(CardBg), Ui(9f));
        UiFonts.DrawIcon(draw, ImGui.GetFontSize(), min + UiVec(12f, 8f), ImGui.GetColorU32(iconColor), icon.ToIconString());
        draw.AddText(min + UiVec(34f, 8f), ImGui.GetColorU32(MutedText), label);
        ImGui.SetCursorScreenPos(new Vector2(start.X, max.Y + Ui(8f)));
    }

    private void DrawPictionaryScoreNotice(PartyChatItem item, float width, bool cycleWinner)
    {
        var start = ImGui.GetCursorScreenPos();
        var noticeWidth = MathF.Min(width, Ui(cycleWinner ? 620f : 550f));
        var min = new Vector2(start.X + (width - noticeWidth) * 0.5f, start.Y + Ui(5f));
        var max = min + new Vector2(noticeWidth, Ui(53f));
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0.055f, 0.19f, 0.15f, 1f)), Ui(9f));
        draw.AddRect(min, max, ImGui.GetColorU32(new Vector4(0.14f, 0.66f, 0.46f, 0.8f)), Ui(9f));
        UiFonts.DrawIcon(draw, ImGui.GetFontSize() * 1.2f, min + UiVec(18f, 17f), ImGui.GetColorU32(new Vector4(0.39f, 0.88f, 0.65f, 1f)), (cycleWinner ? FontAwesomeIcon.Trophy : FontAwesomeIcon.Gamepad).ToIconString());
        var rewardStart = item.Text.LastIndexOf(cycleWinner ? " — " : " +", StringComparison.Ordinal);
        var announcement = rewardStart >= 0 ? item.Text[..rewardStart] : item.Text;
        var reward = rewardStart >= 0
            ? item.Text[(rewardStart + (cycleWinner ? 3 : 1))..]
            : string.Empty;
        var stamp = item.ReceivedAt?.ToString("t") ?? string.Empty;
        var availableAnnouncementWidth = MathF.Max(Ui(70f), noticeWidth - Ui(105f) -
            ImGui.CalcTextSize(reward).X - ImGui.CalcTextSize(stamp).X);
        if (ImGui.CalcTextSize(announcement).X > availableAnnouncementWidth)
        {
            while (announcement.Length > 1 &&
                   ImGui.CalcTextSize(announcement + "…").X > availableAnnouncementWidth)
                announcement = announcement[..^1];
            announcement = announcement.TrimEnd() + "…";
        }
        var textStart = min + UiVec(53f, 19f);
        draw.AddText(textStart, ImGui.GetColorU32(Vector4.One), announcement);
        if (reward.Length > 0)
            draw.AddText(textStart + new Vector2(ImGui.CalcTextSize(announcement).X + Ui(9f), 0f),
                ImGui.GetColorU32(new Vector4(0.39f, 0.88f, 0.65f, 1f)), reward);
        if (stamp.Length > 0)
        {
            draw.AddText(new Vector2(max.X - ImGui.CalcTextSize(stamp).X - Ui(19f), min.Y + Ui(19f)),
                ImGui.GetColorU32(MutedText), stamp);
        }
        ImGui.SetCursorScreenPos(new Vector2(start.X, max.Y + Ui(12f)));
    }

    private void DrawModernHumanMessage(PartyChatItem item, PartyChatItem? previous,
        PartyChatItem? next, float width)
    {
        var left = ImGui.GetCursorScreenPos();
        var avatar = Ui(40f);
        var menuOrigin=left+UiVec(200,0);
        {
            if (CurrentSession is { } session &&
                string.Equals(item.UserId, session.AccountId, StringComparison.OrdinalIgnoreCase))
                DrawAvatarAt(left, session.AvatarIcon, session.AvatarColorHex, avatar, session.AvatarImageUrl);
            else
            {
                if (!string.IsNullOrWhiteSpace(item.UserId))
                    EnsurePartyAvatarLoaded(item.UserId, item.Name);
                if (!string.IsNullOrWhiteSpace(item.UserId) &&
                    partyAvatarCache.TryGetValue(item.UserId, out var info))
                    DrawAvatarAt(left, info.AvatarIcon, info.AvatarColorHex, avatar, info.AvatarImageUrl);
                else
                    DrawAvatarAt(left, null, null, avatar);
            }
        }
        ImGui.SetCursorScreenPos(left);
        if (ImGui.InvisibleButton($"##partyChatAvatar{item.Id}",new Vector2(avatar)))
            ImGui.OpenPopup($"##actionsPopup{item.Id}");
        ImGui.SetCursorScreenPos(left + new Vector2(avatar + Ui(12f), 0f));
        {
            DrawUsername(item.UserId,item.Name,AccentHover);
            if (PatreonBadgeTier(item.UserId) > 0)
            {
                ImGui.SameLine(0f, Ui(6f));
                DrawAlphaChatSupporterHeart();
            }
            if (IsPartyChatHost(item.UserId)) { ImGui.SameLine(0,Ui(6)); DrawPartyChatHostTag(); }
            if (item.ReceivedAt is { } time)
            {
                ImGui.SameLine(0f, Ui(7f));
                using (UiFonts.PushRelative(.90f)) ImGui.TextColored(MutedText, time.ToString("t"));
                menuOrigin=ImGui.GetItemRectMax()+UiVec(10,-19);
            }
        }

        var messageX = left.X + avatar + Ui(12f);
        var messageWidth = width - avatar - Ui(12f);
        ImGui.SetCursorScreenPos(new Vector2(messageX, left.Y + Ui(23f)));
        var replyTarget = item.ReplyToId is { } replyId
            ? partyChatItems.FirstOrDefault(other => other.Id == replyId)
            : null;
        var hasReply = item.ReplyToId is not null;
        var textWidth = MeasureChatRichTextWidth(item.Text);
        var bubbleWidth = MathF.Min(MathF.Max(Ui(90f),messageWidth),MathF.Max(Ui(120f),textWidth+Ui(28f)));
        if (hasReply)
            bubbleWidth = MathF.Min(messageWidth,
                MathF.Max(bubbleWidth, Ui(245f)));
        var textHeight = LayoutChatRichText(item.Text, Vector2.Zero, bubbleWidth - Ui(28f), false);
        var bodyHeight = textHeight + Ui(22f);
        var quoteHeight = hasReply ? Ui(51f) : 0f;
        var bubbleMin = ImGui.GetCursorScreenPos();
        var bubbleMax = bubbleMin + new Vector2(bubbleWidth, bodyHeight + quoteHeight);
        var bubbleDraw = ImGui.GetWindowDrawList();
        bubbleDraw.AddRectFilled(bubbleMin, bubbleMax,
            ImGui.GetColorU32(AccentFieldBg), Ui(10f));
        if (hasReply)
        {
            var quoteMin = bubbleMin + UiVec(8f, 7f);
            var quoteMax = new Vector2(bubbleMax.X - Ui(8f), quoteMin.Y + Ui(43f));
            bubbleDraw.AddRectFilled(quoteMin, quoteMax,
                ImGui.GetColorU32(FrameBgHover), Ui(6f));
            bubbleDraw.AddRectFilled(quoteMin,
                new Vector2(quoteMin.X + Ui(3f), quoteMax.Y), ImGui.GetColorU32(Accent), Ui(2f));
            var previewX = quoteMin.X + Ui(12f);
            bubbleDraw.AddText(new Vector2(previewX, quoteMin.Y + Ui(4f)),
                ImGui.GetColorU32(AccentHover), replyTarget?.Name ?? "Original message");
            var preview = replyTarget is null ? "Message no longer available" :
                TruncateChatReplyPreview(replyTarget.Text, quoteMax.X - previewX - Ui(8f));
            LayoutChatRichText(preview, new Vector2(previewX, quoteMin.Y + Ui(23f)),
                quoteMax.X - previewX - Ui(8f), true, MutedText, interactiveLinks: false);
            ImGui.SetCursorScreenPos(quoteMin);
            if (ImGui.InvisibleButton($"##chatReplyJump{item.Id}", quoteMax - quoteMin) &&
                replyTarget is not null)
            {
                partyChatJumpToMessageId = replyTarget.Id;
                partyChatHighlightedMessageId = replyTarget.Id;
                partyChatHighlightUntilUtc = DateTime.UtcNow.AddSeconds(2.5);
                partyChatStickToBottom = false;
                partyChatFeedScroll.Cancel();
                if (!ShowModernChatItem(replyTarget))
                {
                }
            }
            if (replyTarget is not null && ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                UiFonts.SetTooltip("Jump to original message");
            }
        }
        LayoutChatRichText(item.Text,
            new Vector2(bubbleMin.X + Ui(14f), bubbleMin.Y + quoteHeight +
                (bodyHeight - textHeight) * 0.5f),
            bubbleWidth - Ui(28f), true);
        var bodyMin = new Vector2(bubbleMin.X, bubbleMin.Y + quoteHeight);
        if (ImGui.IsRectVisible(bodyMin, bubbleMax)) partyChatUnseenMentions.Remove(item.Id);
        ImGui.SetCursorScreenPos(bodyMin);
        ImGui.InvisibleButton($"##focusChatMessage{item.Id}",
            new Vector2(bubbleWidth, bodyHeight));
        var keyboardSelected = ImGui.IsItemFocused();
        if (partyChatJumpToMessageId == item.Id)
        {
            partyChatFeedScroll.Cancel();
            var desiredTop = ImGui.GetWindowPos().Y + Ui(75f);
            ImGui.SetScrollY(MathF.Max(0f, ImGui.GetScrollY() + bubbleMin.Y - desiredTop));
            partyChatJumpToMessageId = null;
        }
        if (partyChatHighlightedMessageId == item.Id)
        {
            if (DateTime.UtcNow < partyChatHighlightUntilUtc)
                bubbleDraw.AddRect(bubbleMin - UiVec(3f, 3f), bubbleMax + UiVec(3f, 3f),
                    ImGui.GetColorU32(AccentHover), Ui(10f), ImDrawFlags.None, Ui(2f));
            else
                partyChatHighlightedMessageId = null;
        }
        ImGui.SetCursorScreenPos(new Vector2(bubbleMin.X, bubbleMax.Y));
        var footerY = bubbleMax.Y + Ui(6f);
        var reactionBottom = bubbleMax.Y;
        if (Core.ChatLinkPreview.Extract(item.Text).Length > 0)
        using (ImRaii.PushId(item.Id.ToString()))
        {
            ImGui.SetCursorScreenPos(new Vector2(bubbleMin.X,bubbleMax.Y+Ui(9)));
            DrawAlphaChatLinks(item.Text,messageWidth);
            reactionBottom=MathF.Max(reactionBottom,ImGui.GetCursorScreenPos().Y);
        }
        footerY=MathF.Max(footerY,reactionBottom+Ui(6));
        if (partyChatMessageReactions.TryGetValue(item.Id, out var emojiGroups))
        {
            foreach (var (emoji, users) in emojiGroups.OrderBy(group => group.Key))
            {
                ImGui.SetCursorScreenPos(new Vector2(messageX, footerY));
                var reactedByMe = CurrentSession is { } session && users.Contains(session.AccountId);
                if (DrawChatReactionChip(item.Id, emoji, users, reactedByMe))
                    _ = stream.SendChatMessageReactionAsync(item.Id, emoji);
                messageX += ImGui.GetItemRectSize().X + Ui(9f);
                reactionBottom = MathF.Max(reactionBottom, ImGui.GetItemRectMax().Y);
            }
        }

        var rowHeight=MathF.Max(avatar,MathF.Max(bubbleMax.Y,reactionBottom)-left.Y);
        if (ChatPointerInput.IsHoveringRect(left,left+new Vector2(width,rowHeight)) && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup($"##actionsPopup{item.Id}");
        var actionsOpen=ImGui.IsPopupOpen($"##actionsPopup{item.Id}")||ImGui.IsPopupOpen($"##reactPopup{item.Id}");
        using (ImRaii.PushId(item.Id.ToString()))
            DrawSharedChatHoverActions(left,menuOrigin,width,rowHeight,stream.IsConnected,
                actionsOpen,
                () => partyChatReplyTarget=item,
                () => partyChatMenuReactionRequested=item.Id,
                () => partyChatMenuActionsRequested=item.Id);
        ImGui.SetCursorScreenPos(new Vector2(left.X,MathF.Max(bubbleMax.Y,reactionBottom)));
        DrawChatMessageMenus(item);
        var endY = MathF.Max(MathF.Max(ImGui.GetCursorScreenPos().Y, reactionBottom), left.Y + avatar);
        var gap = 20f;
        ImGui.SetCursorScreenPos(new Vector2(left.X, endY + Ui(gap)));
    }

    private bool DrawChatReactionChip(Guid messageId, int emoji, HashSet<string> users, bool reactedByMe)
    {
        var clicked=AlphaChatTextAction($"reaction{messageId}{emoji}",users.Count.ToString(),ChatReactionIcons[emoji],ChatEmojiColor(emoji),
            Ui(48),Ui(25),reactedByMe);
        DrawChatReactionTooltip(emoji, users.Count, reactedByMe, users.Select(id => ChatReactionDisplayName(id, party: true)));
        return clicked;
    }

    private static Vector4 ChatEmojiColor(int emoji) => emoji switch
    {
        0 => new Vector4(1f, 0.43f, 0.60f, 1f),
        1 => new Vector4(1f, 0.75f, 0.30f, 1f),
        2 => new Vector4(0.54f, 0.70f, 1f, 1f),
        3 => new Vector4(1f, 0.78f, 0.30f, 1f),
        4 => new Vector4(0.42f, 0.80f, 0.96f, 1f),
        5 => new Vector4(1f, 0.43f, 0.15f, 1f),
        6 => new Vector4(1f, 0.82f, 0.32f, 1f),
        7 => new Vector4(0.36f, 0.68f, 1f, 1f),
        8 => new Vector4(1f, 0.35f, 0.35f, 1f),
        9 => new Vector4(0.73f, 0.86f, 0.30f, 1f),
        10 => new Vector4(0.77f, 0.67f, 1f, 1f),
        11 => new Vector4(0.24f, 0.83f, 0.74f, 1f),
        12 => new Vector4(1f, 0.76f, 0.25f, 1f),
        13 => new Vector4(0.91f, 0.51f, 0.95f, 1f),
        14 => new Vector4(1f, 0.56f, 0.25f, 1f),
        15 => new Vector4(1f, 0.45f, 0.68f, 1f),
        16 => new Vector4(0.72f, 0.67f, 1f, 1f),
        17 => new Vector4(1f, 0.79f, 0.25f, 1f),
        18 => new Vector4(0.61f, 0.72f, 1f, 1f),
        _ => new Vector4(0.79f, 0.64f, 1f, 1f),
    };

    private static string ExpandChatEmojiShortcodes(string text)
    {
        text = text.Replace(":applaud:", ":fire:", StringComparison.OrdinalIgnoreCase)
            .Replace("👏", "🔥", StringComparison.Ordinal);
        for (var index = 0; index < ChatReactionGlyphs.Length; index++)
            text = text.Replace(ChatReactionShortcodes[index], ChatReactionGlyphs[index], StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private float MeasureChatRichTextWidth(string text)
    {
        text = ExpandChatEmojiShortcodes(text);
        var currentWidth = 0f;
        var maxWidth = 0f;
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (element is "\n" or "\r\n")
            {
                maxWidth = MathF.Max(maxWidth, currentWidth);
                currentWidth = 0f;
                continue;
            }
            var emoji = Array.IndexOf(ChatReactionGlyphs, element.TrimEnd('\uFE0F'));
            if (emoji >= 0)
            {
                using var iconFont = UiFonts.PushIcon();
                currentWidth += ImGui.CalcTextSize(ChatReactionIcons[emoji].ToIconString()).X * 1.1f + Ui(8f);
            }
            else
            {
                currentWidth += ImGui.CalcTextSize(element).X;
            }
        }
        return MathF.Max(maxWidth, currentWidth);
    }

    private float LayoutChatRichText(string text, Vector2 origin, float width, bool drawText,
        Vector4? textColor = null, bool interactiveLinks = true)
    {
        text = ExpandChatEmojiShortcodes(text);

        var x = 0f;
        var y = 0f;
        var atWordStart = true;
        var lineHeight = ImGui.GetTextLineHeight() + Ui(3f);
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        var draw = ImGui.GetWindowDrawList();
        var mentions = System.Text.RegularExpressions.Regex.Matches(text, @"(?<!\w)@[^\s@.,!?;:]+")
            .Select(match => (Start: match.Index, End: match.Index + match.Length)).ToArray();
        var links = new List<(int Start, int End, string Url)>();
        if (drawText && interactiveLinks)
        {
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(text,
                         @"(?i)\b(?:https?://|www\.)[^\s<>]+"))
            {
                var visibleUrl = match.Value.TrimEnd('.', ',', '!', '?', ';', ':', ')', ']');
                var browserUrl = visibleUrl.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                    ? "https://" + visibleUrl : visibleUrl;
                if (Uri.TryCreate(browserUrl, UriKind.Absolute, out var uri) &&
                    uri.Scheme is "http" or "https")
                    links.Add((match.Index, match.Index + visibleUrl.Length, uri.AbsoluteUri));
            }
        }
        var openedLink = false;
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var elementIndex = elements.ElementIndex;
            if (element is "\n" or "\r\n")
            {
                x = 0f;
                y += lineHeight;
                atWordStart = true;
                continue;
            }
            var normalized = element.TrimEnd('\uFE0F');
            var emoji = Array.IndexOf(ChatReactionGlyphs, normalized);
            var iconText = emoji >= 0 ? ChatReactionIcons[emoji].ToIconString() : null;
            float elementWidth;
            if (iconText is not null)
            {
                using var iconFont = UiFonts.PushIcon();
                elementWidth = ImGui.CalcTextSize(iconText).X * 1.1f + Ui(8f);
            }
            else
            {
                elementWidth = ImGui.CalcTextSize(element).X;
            }
            var whitespace = string.IsNullOrWhiteSpace(element);
            if (whitespace && x == 0f) continue;
            if (atWordStart && !whitespace && emoji < 0 && x > 0f)
            {
                var wordEnd = elementIndex;
                while (wordEnd < text.Length && !char.IsWhiteSpace(text[wordEnd])) wordEnd++;
                var wordWidth = ImGui.CalcTextSize(text[elementIndex..wordEnd]).X;
                if (x + wordWidth > width)
                {
                    x = 0f;
                    y += lineHeight;
                }
            }
            if (x > 0f && x + elementWidth > width)
            {
                x = 0f;
                y += lineHeight;
            }
            if (drawText)
            {
                var position = origin + new Vector2(x, y);
                var mentioned = mentions.Any(mention => elementIndex >= mention.Start &&
                                                       elementIndex < mention.End);
                var link = links.FirstOrDefault(candidate => elementIndex >= candidate.Start &&
                                                             elementIndex < candidate.End);
                if (mentioned)
                    draw.AddRectFilled(position - UiVec(1f, 1f),
                        position + new Vector2(elementWidth + Ui(1f), lineHeight - Ui(1f)),
                        ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.25f)));
                if (iconText is not null)
                    UiFonts.DrawIcon(draw, ImGui.GetFontSize() * 1.1f, position, ImGui.GetColorU32(ChatEmojiColor(emoji)), iconText);
                else
                    draw.AddText(position, ImGui.GetColorU32(link.Url is not null
                        ? new Vector4(0.42f, 0.78f, 1f, 1f)
                        : mentioned ? AccentHover : textColor ?? Vector4.One), element);
                if (link.Url is not null)
                {
                    draw.AddLine(position + new Vector2(0f, lineHeight - Ui(2f)),
                        position + new Vector2(elementWidth, lineHeight - Ui(2f)),
                        ImGui.GetColorU32(new Vector4(0.42f, 0.78f, 1f, 0.75f)));
                    if (ChatPointerInput.IsHoveringRect(position,
                            position + new Vector2(elementWidth, lineHeight)))
                    {
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                        if (!openedLink && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                        {
                            openedLink = true;
                            Dalamud.Utility.Util.OpenLink(link.Url);
                        }
                    }
                }
            }
            x += elementWidth;
            atWordStart = whitespace || emoji >= 0;
        }
        return y + lineHeight;
    }

    private void DrawChatMessageMenus(PartyChatItem item)
    {
        if (partyChatMenuReactionRequested == item.Id)
        {
            partyChatMenuReactionRequested = null;
            ImGui.OpenPopup($"##reactPopup{item.Id}");
        }
        if (partyChatMenuActionsRequested==item.Id)
        {
            partyChatMenuActionsRequested=null;
            ImGui.OpenPopup($"##actionsPopup{item.Id}");
        }
        DrawChatComposerEmojiPopup($"##reactPopup{item.Id}",emoji => _=stream.SendChatMessageReactionAsync(item.Id,emoji),"React to message");
        using var menuStyle=ImRaii.PushStyle(ImGuiStyleVar.PopupBorderSize,Ui(1))
            .Push(ImGuiStyleVar.PopupRounding,0f).Push(ImGuiStyleVar.WindowPadding,UiVec(16,14))
            .Push(ImGuiStyleVar.ItemSpacing,UiVec(8,8));
        using var menuColors=ImRaii.PushColor(ImGuiCol.PopupBg,CardBg)
            .Push(ImGuiCol.Border,new Vector4(AccentHover.X,AccentHover.Y,AccentHover.Z,0.3f));
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup($"##actionsPopup{item.Id}")))
        {
            if (ImGui.MenuItem("Reply")) partyChatReplyTarget = item;
            if (ImGui.MenuItem("React")) partyChatMenuReactionRequested = item.Id;
            if (ImGui.MenuItem("Copy message")) ImGui.SetClipboardText(item.Text);
            if (CurrentSession is { } profileSession && !string.IsNullOrWhiteSpace(item.UserId))
            {
                var own=item.UserId==profileSession.AccountId;
                var friend=friends.Any(f=>f.AccountId==item.UserId);
                ImGui.Separator();
                DrawUsername(item.UserId,item.Name,AccentHover);
                if (PatreonBadgeTier(item.UserId)>0) { ImGui.SameLine(0,Ui(6)); DrawAlphaChatSupporterHeart(); }
                ImGui.Separator();
                if (DrawAlphaChatMenuAction("Mention",stream.IsConnected&&!AlphaChatBlocked(item.UserId)))
                {
                    partyChatInput += $"@{item.Name} ";
                    partyChatComposerInputRevision++;
                    partyChatFocusComposer=true;
                }
                if (DrawAlphaChatMenuAction("View Profile",!AlphaChatBlocked(item.UserId))) OpenProfilePage(profileSession,item.UserId,item.Name);
                if (DrawAlphaChatMenuAction("Add Friend",!own&&!friend&&!AlphaChatBlocked(item.UserId))) _=SendPartyFriendRequestAsync(profileSession,item.UserId,item.Name);
                if (DrawAlphaChatMenuAction("Message",!own&&friend)) StartOrOpenConversation(profileSession,item.UserId,item.Name);
                DrawAlphaChatMenuAction("Join Watch Party",false);
                using (ImRaii.PushColor(ImGuiCol.Text,Danger))
                {
                    if (!own && ImGui.MenuItem("Report message")) QueuePartyMessageReport(item);
                if (!own && ImGui.MenuItem(AlphaChatBlocked(item.UserId)?"Unblock User":"Block User"))
                { if (AlphaChatBlocked(item.UserId)) UnblockAccount(profileSession,item.UserId); else QueueFriendBlockConfirmation(item.UserId,item.Name); }
                }
                ImGui.Separator();
            }
            var canDelete = item.HasSharedMessageId && stream.IsConnected &&
                !string.IsNullOrWhiteSpace(stream.LocalUserId) &&
                (stream.Mode == StreamMode.Hosting ||
                 string.Equals(item.UserId, stream.LocalUserId, StringComparison.OrdinalIgnoreCase));
            if (canDelete)
            {
                if (ImGui.MenuItem("Delete message"))
                    _ = stream.SendChatMessageDeleteAsync(item.Id);
            }
            ImGui.EndPopup();
        }
    }

    private string TruncateChatReplyPreview(string text, float maxWidth)
    {
        var flattened = ExpandChatEmojiShortcodes(text).Replace('\r', ' ').Replace('\n', ' ');
        var elements = new List<string>();
        var iterator = System.Globalization.StringInfo.GetTextElementEnumerator(flattened);
        while (elements.Count < 70 && iterator.MoveNext())
            elements.Add(iterator.GetTextElement());
        var truncated = iterator.MoveNext();
        while (elements.Count > 0)
        {
            var preview = string.Concat(elements).TrimEnd() + (truncated ? "…" : string.Empty);
            if (MeasureChatRichTextWidth(preview) <= maxWidth)
                return preview;
            elements.RemoveAt(elements.Count - 1);
            truncated = true;
        }
        return "…";
    }

    private int PlacePartyChatCaretAfterMention(ref ImGuiInputTextCallbackData data)
    {
        if (partyChatMoveCaretToEnd)
        {
            data.CursorPos = data.BufTextLen;
            data.SelectionStart = data.BufTextLen;
            data.SelectionEnd = data.BufTextLen;
            partyChatMoveCaretToEnd = false;
        }
        return 0;
    }

    private bool PartyChatMentionsLocalUser(string text)
    {
        var name = string.IsNullOrWhiteSpace(CurrentDisplayName) ? CurrentSession?.DisplayName : CurrentDisplayName;
        return UnseenChatMentions.Matches(text, name);
    }

    private void DrawPartyChatMentionJumpButton()
    {
        partyChatUnseenMentions.IntersectWith(partyChatItems.Select(item => item.Id));
        if (partyChatMentionUnreadCount == 0) return;
        var old = ImGui.GetCursorScreenPos();
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var buttonSize = Ui(36f);
        var min = pos + new Vector2(size.X - buttonSize - Ui(18f) - ImGui.GetStyle().ScrollbarSize, size.Y - Ui(50f));
        ImGui.SetCursorScreenPos(min);
        var clicked = ImGui.InvisibleButton("##partyChatJumpMention", new Vector2(buttonSize));
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        var center = min + new Vector2(buttonSize * .5f);
        draw.AddCircleFilled(center, buttonSize * .5f, ImGui.GetColorU32(hovered ? Accent : FrameBgHover));
        draw.AddCircle(center, buttonSize * .5f, ImGui.GetColorU32(AccentHover), 0, Ui(1f));
        using (UiFonts.PushIcon())
        {
            var glyph = FontAwesomeIcon.Eye.ToIconString();
            draw.AddText(center - ImGui.CalcTextSize(glyph) * .5f, ImGui.GetColorU32(Vector4.One), glyph);
        }
        if (hovered) UiFonts.SetTooltip($"Jump to mention({partyChatMentionUnreadCount})");
        if (clicked)
        {
            var targetId = partyChatUnseenMentions.TakeNewest(partyChatItems.Select(item => item.Id));
            var target = partyChatItems.FirstOrDefault(item => item.Id == targetId);
            if (target is not null)
            {
                partyChatJumpToMessageId = target.Id;
                partyChatHighlightedMessageId = target.Id;
                partyChatHighlightUntilUtc = DateTime.UtcNow.AddSeconds(2.5);
                partyChatStickToBottom = false;
                partyChatFeedScroll.Cancel();
                partyChatWasAtBottom = false;
            }
        }
        ImGui.SetCursorScreenPos(old);
    }
}

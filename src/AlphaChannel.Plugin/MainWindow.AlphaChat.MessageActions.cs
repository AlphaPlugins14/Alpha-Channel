using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static string AlphaChatReplyPreview(AlphaChatMessage message) =>
        message.Kind == "room" ? "Shared a watch party" : message.Content.Replace('\n', ' ');

    private void DrawAlphaChatComposerReply()
    {
        if (alphaChatReplyTo is not { } targetId || alphaChatMessages.FirstOrDefault(m => m.Sequence == targetId) is not { } target) return;
        var start = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        DrawUsername(target.Sender.AccountId,"Replying to " + target.Sender.DisplayName,AccentHover);
        var previewOrigin = ImGui.GetCursorScreenPos();
        using (UiFonts.PushRelative(.90f)) LayoutChatRichText(TruncateChatReplyPreview(AlphaChatReplyPreview(target), width - Ui(40f)),
            previewOrigin, width - Ui(40f), true, MutedText, interactiveLinks: false);
        ImGui.SetCursorScreenPos(start + new Vector2(width - Ui(26f), 0));
        if (AlphaChatButton("cancelReply", "", FontAwesomeIcon.Times, Ui(26f), Ui(26f))) alphaChatReplyTo = null;
        ImGui.SetCursorScreenPos(start + UiVec(0f, 50f));
    }

    private void DrawAlphaChatHoverActions(AlphaChatMessage message, Vector2 origin, Vector2 menuOrigin, float width)
    {
        var rowHeight = alphaChatRowHeights.GetValueOrDefault(message.Sequence, Ui(message.Kind == "room" ? 200f : 85f));
        var hovered = ChatPointerInput.IsHoveringRect(origin, origin + new Vector2(width, rowHeight));
        var menuOpen = ImGui.IsPopupOpen("##alphaReactionPicker") ||
            (alphaChatContextActionsOpen && alphaChatContextMessage == message.Sequence) ||
            alphaChatReactionPickerTarget == message.Sequence;
        DrawSharedChatHoverActions(origin,menuOrigin,width,rowHeight,alphaChat.Connected,menuOpen,
            () => alphaChatReplyTo=message.Sequence,
            () => ImGui.OpenPopup("##alphaReactionPicker"),
            () => SelectAlphaChatPerson(message.Sender,message.Sequence));
        DrawChatComposerEmojiPopup("##alphaReactionPicker", emoji => ToggleAlphaChatReaction(message.Sequence, emoji), "React to message");
    }

    private void DrawSharedChatHoverActions(Vector2 origin,Vector2 menuOrigin,float width,float rowHeight,
        bool connected,bool menuOpen,Action reply,Action react,Action more)
    {
        var hovered=ChatPointerInput.IsHoveringRect(origin,origin+new Vector2(width,rowHeight));
        if (!(hovered||menuOpen) || menuOrigin.X+Ui(162)>origin.X+width) return;
        ImGui.SetCursorScreenPos(menuOrigin);
        using var actionFont = currentPage is HomePage.WatchAlong or HomePage.Messages or HomePage.AlphaChat ? UiFonts.PushRelative(.90f) : UiFonts.Empty;
        using var spacing=ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing,UiVec(4,0));
        using var disabled=UiDisabled.Scope(!connected);
        if (AlphaChatTextAction("reply","Reply",FontAwesomeIcon.Reply,AccentHover,Ui(64),Ui(22))) reply();
        ImGui.SameLine();
        if (AlphaChatTextAction("react","React",FontAwesomeIcon.Smile,AccentHover,Ui(66),Ui(22))) react();
        ImGui.SameLine();
        if (AlphaChatTextAction("more","",FontAwesomeIcon.EllipsisH,AccentHover,Ui(24),Ui(22))) more();
    }

    private void DrawAlphaChatReplyQuote(AlphaChatMessage message, ref Vector2 origin, float width)
    {
        if (message.ReplyTo is not { } targetId) return;
        var target = alphaChatMessages.FirstOrDefault(m => m.Sequence == targetId && !AlphaChatBlocked(m.Sender.AccountId));
        width = MathF.Max(Ui(90f), width);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + new Vector2(width, Ui(42f)), ImGui.GetColorU32(FrameBg), Ui(7f));
        draw.AddRectFilled(origin, origin + UiVec(3f, 42f), ImGui.GetColorU32(Accent), Ui(2f));
        var quoteName = FitPartyTextToWidth(target?.Sender.DisplayName ?? "Original message unavailable", width - Ui(20f));
        if (target is null) draw.AddText(origin + UiVec(10f, 3f), ImGui.GetColorU32(AccentHover), quoteName);
        else DrawUsernameAt(draw,origin + UiVec(10f,3f),quoteName,AccentHover,target.Sender.AccountId);
        var preview = target is null ? "Deleted or no longer in chat history" : AlphaChatReplyPreview(target);
        using (UiFonts.PushRelative(.90f)) LayoutChatRichText(TruncateChatReplyPreview(preview, width - Ui(20f)), origin + UiVec(10f, 22f),
            width - Ui(20f), true, MutedText, interactiveLinks: false);
        ImGui.SetCursorScreenPos(origin);
        if (ImGui.InvisibleButton("##alphaReplyJump", new Vector2(width, Ui(42f))) && target is not null)
        { alphaChatJumpTo = target.Sequence; alphaChatJumpToUnread = false; alphaChatHistoryAnchor = null; }
        if (target is not null && ImGui.IsItemHovered())
        { ImGui.SetMouseCursor(ImGuiMouseCursor.Hand); UiFonts.SetTooltip("Jump to original message"); }
        origin.Y += Ui(48f);
    }

    private void DrawAlphaChatReactionChips(AlphaChatMessage message, float left, float right)
    {
        if (!alphaChatReactions.TryGetValue(message.Sequence, out var reactions) || reactions.Length == 0) return;
        var x = left; var y = ImGui.GetCursorScreenPos().Y;
        foreach (var reaction in reactions.Where(r => r.Emoji >= 0 && r.Emoji < ChatReactionIcons.Length && r.Count > 0))
        {
            var chipWidth = MathF.Max(Ui(40f), ImGui.CalcTextSize(reaction.Count.ToString()).X + Ui(26f));
            if (x + chipWidth > right) { x = left; y += Ui(30f); }
            ImGui.SetCursorScreenPos(new Vector2(x, y));
            using (UiDisabled.Scope(!alphaChat.Connected))
                if (AlphaChatTextAction($"reaction{reaction.Emoji}", reaction.Count.ToString(), ChatReactionIcons[reaction.Emoji],
                    ChatEmojiColor(reaction.Emoji), chipWidth, Ui(25f), reaction.ReactedByMe)) ToggleAlphaChatReaction(message.Sequence, reaction.Emoji);
            DrawChatReactionTooltip(reaction.Emoji, reaction.Count, reaction.ReactedByMe, reaction.Users?.Select(u => u.DisplayName));
            x += chipWidth + Ui(6f);
        }
        ImGui.SetCursorScreenPos(new Vector2(left, y + Ui(30f)));
    }

    private void DrawAlphaChatSupporterHeart(bool showTooltip = true)
    {
        using (UiFonts.PushIcon())
            ImGui.TextColored(new Vector4(1f, .55f, .18f, .85f), FontAwesomeIcon.Heart.ToIconString());
        if (showTooltip && ImGui.IsItemHovered()) UiFonts.SetTooltip("Patreon supporter");
    }

    private bool AlphaChatTextAction(string id, string label, FontAwesomeIcon icon, Vector4 iconColor,
        float width, float height, bool selected = false)
    {
        var clicked = ImGui.InvisibleButton("##" + id, new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax();
        var textSize = ImGui.CalcTextSize(label);
        Vector2 iconSize;
        using (UiFonts.PushIcon()) iconSize = ImGui.CalcTextSize(icon.ToIconString());
        var gap = label.Length == 0 ? 0f : Ui(6f);
        var x = min.X + (width - iconSize.X - gap - textSize.X) * .5f;
        var draw = ImGui.GetWindowDrawList();
        var alpha = ImGui.GetStyle().Alpha;
        UiFonts.DrawIcon(draw, ImGui.GetFontSize(), new Vector2(x, min.Y + (height - iconSize.Y) * .5f), ImGui.GetColorU32(iconColor * new Vector4(1f, 1f, 1f, alpha)), icon.ToIconString());
        var textColor = selected ? AccentHover : hovered ? Vector4.One : MutedText;
        draw.AddText(new Vector2(x + iconSize.X + gap, min.Y + (height - textSize.Y) * .5f),
            ImGui.GetColorU32(textColor * new Vector4(1f, 1f, 1f, alpha)), label);
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return clicked;
    }

    private void ToggleAlphaChatReaction(long sequence, int emoji)
    {
        if (!alphaChat.Connected) return;
        var mine = alphaChatReactions.TryGetValue(sequence, out var items) && items.Any(r => r.Emoji == emoji && r.ReactedByMe);
        _ = alphaChat.CommandAsync(new { type = "react", sequence, emoji, added = !mine });
    }
}

using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Crypto;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{

    private void DrawModernDmThread(CharacterSession session, string conversationId)
    {
        using var chatStyle=ImRaii.PushStyle(ImGuiStyleVar.ChildRounding,Ui(12)).Push(ImGuiStyleVar.FrameRounding,Ui(9))
            .Push(ImGuiStyleVar.WindowPadding,UiVec(16,14)).Push(ImGuiStyleVar.ItemSpacing,UiVec(8,8));
        using var chatColors=ImRaii.PushColor(ImGuiCol.ChildBg,CardBg).Push(ImGuiCol.FrameBg,FrameBg)
            .Push(ImGuiCol.Button,FrameBg).Push(ImGuiCol.ButtonHovered,FrameBgHover).Push(ImGuiCol.Border,BorderSubtle);
        notificationDmLastDrawUtc = DateTime.UtcNow;
        if (!messagesLoading)
            MarkDisplayedConversationReadIfNeeded(session, conversationId);
        CheckMessageEncryption(session, conversationId);

        DrawDmChatToolbar(session,conversationId);

        var composerHeight = Ui(messageReplyTarget is null ? 76f : 126f);
        composerHeight += GroupImageComposerExtra(conversationId);
        if (IsOpenDmBlocked(session))
            composerHeight += BlockedDmNoticeHeight(ImGui.GetContentRegionAvail().X);
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg))
        using (var child = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##modernDmFeed", new Vector2(0f, -composerHeight - Ui(12f)),
                   false, ImGuiWindowFlags.AlwaysUseWindowPadding)))
        {
            if (child)
            {
                var messages = openMessages;
                var reactions = BuildDmReactions(messages);
                ImGui.Dummy(UiVec(0f, 6f));
                if (messagesNextCursor is { Length: > 0 })
                {
                    using (UiDisabled.Scope(messagesLoadingOlder || messagesLoading))
                    {
                        if (ImGui.Button("Load older messages", UiVec(-1f, 30f)))
                            RefreshMessages(session, reset: false);
                    }
                    ImGui.Dummy(UiVec(0f, 10f));
                }
                if (messagesLoading && messages.Length == 0)
                    ImGui.TextDisabled("Loading messages…");

                MessageDto? previous = null;
                DateTime? previousDay = null;
                foreach (var message in messages)
                {
                    var raw = decryptedCache.GetValueOrDefault(message.Id);
                    var rich = DecodeRichDm(raw);
                    if (rich?.Kind == "reaction")
                        continue;
                    var day = DateTimeOffset.FromUnixTimeSeconds(message.SentAtUnix).LocalDateTime.Date;
                    if (previousDay != day) { DrawDmDayDivider(day); previousDay = day; previous = null; }
                    if (DrawCloudBlockedMessage(message)) { previous = null; continue; }
                    DrawModernDmBubble(session, conversationId, message, previous, rich, raw, reactions);
                    previous = rich?.Kind == "watch_party_invite" ? null : message;
                }
                if (messageJumpToGroupId is { } jumpId &&
                    !messages.Any(message => message.GroupId == jumpId) &&
                    !messageLoadingReplyTarget && !messagesLoading && !messagesLoadingOlder &&
                    messagesNextCursor is not null)
                    FindDmReplyTarget(session, conversationId, jumpId);
                else if (messageJumpToGroupId is { } missingId &&
                         !messages.Any(message => message.GroupId == missingId) &&
                         messagesNextCursor is null && !messagesLoading)
                    messageJumpToGroupId = null;
                ImGui.Dummy(UiVec(0f, 14f));
                if (threadScrollToBottom)
                {
                    ImGui.SetScrollHereY(1f);
                    threadScrollToBottom = false;
                }
            }
        }

        if (messagesError is { Length: > 0 } error)
            ImGui.TextColored(Danger, error);
        DrawModernDmComposer(session, conversationId, composerHeight);
    }

    private void DrawDmDayDivider(DateTime day)
    {
        var label = day == DateTime.Today ? "Today" : day == DateTime.Today.AddDays(-1) ? "Yesterday" : day.ToString("ddd d MMM yyyy");
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        using (UiFonts.PushRelative(.85f)) {
            var size = ImGui.CalcTextSize(label); var center = origin + new Vector2(width*.5f,Ui(14)); var draw = ImGui.GetWindowDrawList();
            var gap = size.X*.5f+Ui(14);
            draw.AddLine(origin+UiVec(0,14),center-new Vector2(gap,0),ImGui.GetColorU32(AccentInk(.2f)));
            draw.AddLine(center+new Vector2(gap,0),origin+new Vector2(width,Ui(14)),ImGui.GetColorU32(AccentInk(.2f)));
            draw.AddText(center-size*.5f,ImGui.GetColorU32(MutedText),label);
        }
        ImGui.Dummy(new Vector2(width,Ui(32)));
    }

    private void DrawDmMemberNames(IEnumerable<ConversationMemberDto> members)
    {
        DrawUsername(CurrentSession?.AccountId,$"{CurrentDisplayName ?? CurrentSession?.DisplayName ?? "You"} (You)");
        foreach (var member in members.OrderBy(member => member.DisplayName, StringComparer.OrdinalIgnoreCase))
            DrawUsername(member.AccountId,member.DisplayName,Vector4.One);
    }

    private void DrawModernDmSecurityStatus(CharacterSession session, string conversationId)
    {
        var min = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var size = new Vector2(width, Ui(36f));
        var max = min + size;
        var unreadableCount = openMessages.Count(message =>
            decryptedCache.GetValueOrDefault(message.Id) == DecryptFailedMarker);
        var status = new DmEncryptionStatus(messageEncryptionReady, messageCheckingKeys,
            unreadableCount, messageEncryptionRepairAttempted, messageEncryptionError);
        var ready = status.KeysReady == true;
        var color = ready ? new Vector4(0.38f, 0.84f, 0.67f, 1f)
            : status.HasKeyError ? new Vector4(1f, 0.69f, 0.42f, 1f) : MutedText;
        var text = status.Title;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max,
            ImGui.GetColorU32(ready ? new Vector4(0.07f, 0.16f, 0.15f, 1f) :
                new Vector4(0.16f, 0.12f, 0.12f, 1f)), Ui(9f));
        draw.AddRect(min, max, ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, 0.65f)), Ui(9f));
        var icon = (ready ? FontAwesomeIcon.Lock : status.HasKeyError ? FontAwesomeIcon.ExclamationTriangle : FontAwesomeIcon.Unlock).ToIconString();
        text = TruncateToWidth(text, MathF.Max(1f, width - Ui(47f)));
        var labelSize = ImGui.CalcTextSize(text);
        var groupWidth = labelSize.X + Ui(27f);
        var x = min.X + (width - groupWidth) * 0.5f;
        UiFonts.DrawIcon(draw, ImGui.GetFontSize(), new Vector2(x, min.Y + Ui(9f)), ImGui.GetColorU32(color), icon);
        draw.AddText(new Vector2(x + Ui(27f), min.Y + Ui(9f)), ImGui.GetColorU32(color), text);
        if (ImGui.InvisibleButton("##dmEncryptionStatus", size) && status.CanRetry)
            CheckMessageEncryption(session, conversationId, repair: true);
        if (ImGui.IsItemHovered() && status.CanRetry)
        {
            UiFonts.SetTooltip(status.HasKeyError ? status.Details ?? "Click to recheck encryption keys."
                : "Click to refresh member keys and retry unreadable saved messages. Messages encrypted with lost or replaced private keys may remain unreadable.");
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        if (status.Details is { } details)
        {
            using var font = UiFonts.PushRelative(.90f);
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(status.HasKeyError || unreadableCount > 0
                ? new Vector4(1f, 0.69f, 0.42f, 1f) : MutedText, details);
        }
    }

    private Dictionary<(string Target, int Emoji), HashSet<string>> BuildDmReactions(
        IEnumerable<MessageDto> messages)
    {
        var result = new Dictionary<(string, int), HashSet<string>>();
        foreach (var message in messages)
        {
            if (GroupChatClient.IsMessage(message) && AlphaChatBlocked(message.SenderAccountId)) continue;
            var reaction = DecodeRichDm(decryptedCache.GetValueOrDefault(message.Id));
            if (reaction is not { Kind: "reaction", Target: { } target, Emoji: { } emoji,
                    Active: { } active })
                continue;
            var key = (target, emoji);
            if (!result.TryGetValue(key, out var users))
                result[key] = users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (active)
                users.Add(message.SenderAccountId);
            else
                users.Remove(message.SenderAccountId);
        }
        return result;
    }

    private void DrawModernDmBubble(CharacterSession session,string conversationId,MessageDto message,
        MessageDto? previous,RichDmContent? rich,string? raw,
        Dictionary<(string Target,int Emoji),HashSet<string>> reactions)
    {
        using var id=ImRaii.PushId(message.Id);
        var mine=message.SenderAccountId==session.AccountId;
        var name=memberDisplayNameCache.GetValueOrDefault(message.SenderAccountId,mine?session.DisplayName:"Someone");
        var imageContent = GroupChatClient.ImageContent(raw);
        if (imageContent is not null) rich = new RichDmContent("message", Text: imageContent.Text, ReplyTo: imageContent.ReplyTo);
        var text=imageContent is not null ? imageContent.Text + (dmClient.Groups.IsPublic(conversationId) ? "\nDirect image uploading is only available in private groups." : "") : raw is null?"Decrypting…":raw==DecryptFailedMarker?"Couldn't decrypt — key missing":rich?.Kind=="message"?rich.Text??"":raw;
        var origin=ImGui.GetCursorScreenPos();
        if(raw is null && ImGui.IsRectVisible(UiVec(100,70))) QueueVisibleDmPlaintext(session,message);
        var width=ImGui.GetContentRegionAvail().X;
        DrawDmSenderAvatar(session,message.SenderAccountId,name,origin);
        if (ChatPointerInput.IsHoveringRect(origin,origin+UiVec(40,40)) && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            dmActionsRequested=message.Id;
        var content=origin+UiVec(54,0);
        ImGui.SetCursorScreenPos(content);
        DrawUsername(message.SenderAccountId,name,AccentHover);
        if (PatreonBadgeTier(message.SenderAccountId)>0) { ImGui.SameLine(0,Ui(6)); DrawAlphaChatSupporterHeart(); }
        ImGui.SameLine(); DrawSocialHelper(DateTimeOffset.FromUnixTimeSeconds(message.SentAtUnix).LocalDateTime.ToString("t"));
        if (mine)
        {
            ImGui.SameLine(0,Ui(5));
            using (UiFonts.PushIcon()) ImGui.TextColored(message.ReadAtUnix.HasValue?Good:MutedText,FontAwesomeIcon.Check.ToIconString());
            if (!GroupChatClient.IsMessage(message) && ImGui.IsItemHovered()) UiFonts.SetTooltip(message.ReadAtUnix.HasValue?"Read":"Sent");
        }
        var menuOrigin=ImGui.GetItemRectMax()+UiVec(10,-19);
        var body=content+UiVec(0,23);
        var inner=MathF.Max(Ui(90),width-Ui(54));
        if (rich?.ReplyTo is { Length:>0 } reply)
        {
            var target=openMessages.FirstOrDefault(m=>m.GroupId==reply);
            var targetRaw=target is null?null:decryptedCache.GetValueOrDefault(target.Id);
            var targetRich=DecodeRichDm(targetRaw);
            var preview=target is not null && IsCloudMessageHidden(target) ? "Blocked message — click the original to view" : target is null?messagesNextCursor is null?"Message no longer available":"Click to load original message":
                targetRaw==DecryptFailedMarker?"Couldn't decrypt — key missing":targetRich?.Kind=="watch_party_invite"?"Shared a watch party":targetRich?.Text??(targetRaw is null ? "Decrypting…" : GroupChatClient.ImageCaption(targetRaw));
            var targetName=target is null?"Original message":memberDisplayNameCache.GetValueOrDefault(target.SenderAccountId,"Someone");
            var draw=ImGui.GetWindowDrawList();
            draw.AddRectFilled(body,body+new Vector2(inner,Ui(42)),ImGui.GetColorU32(FrameBg),Ui(7));
            draw.AddRectFilled(body,body+UiVec(3,42),ImGui.GetColorU32(Accent),Ui(2));
            DrawUsernameAt(draw,body+UiVec(12,4),FitPartyTextToWidth(targetName,inner-Ui(24)),AccentHover,target?.SenderAccountId);
            using (UiFonts.PushRelative(.90f)) LayoutChatRichText(TruncateChatReplyPreview(preview,inner-Ui(24)),body+UiVec(12,22),inner-Ui(24),true,MutedText,interactiveLinks:false);
            ImGui.SetCursorScreenPos(body);
            if (ImGui.InvisibleButton("##replyJump",new Vector2(inner,Ui(42)))) { messageJumpToGroupId=reply; threadScrollToBottom=false; }
            body.Y+=Ui(49);
        }
        ImGui.SetCursorScreenPos(body);
        if (rich is {Kind:"watch_party_invite"}) DrawWatchPartyInviteCard(session,message,rich);
        else if (imageContent is not null && !dmClient.Groups.IsPublic(conversationId)) DrawGroupImage(session,message,imageContent,inner);
        else
        {
            var bubbleWidth=MathF.Min(inner,MathF.Max(Ui(120),MeasureChatRichTextWidth(text)+Ui(28)));
            var textHeight=LayoutChatRichText(text,Vector2.Zero,bubbleWidth-Ui(28),false);
            var bottom=body+new Vector2(bubbleWidth,textHeight+Ui(22));
            ImGui.GetWindowDrawList().AddRectFilled(body,bottom,ImGui.GetColorU32(AccentFieldBg),Ui(10));
            LayoutChatRichText(text,body+UiVec(14,11),bubbleWidth-Ui(28),true);
            ImGui.SetCursorScreenPos(new Vector2(body.X,bottom.Y+Ui(9)));
            if (raw is not null && raw!=DecryptFailedMarker) DrawAlphaChatLinks(text,inner);
        }
        var bottomY=ImGui.GetCursorScreenPos().Y;
        var chipX=body.X; var chipY=bottomY;
        foreach (var ((target,emoji),users) in reactions.OrderBy(r=>r.Key.Emoji))
        {
            if (target!=message.GroupId || users.Count==0) continue;
            var chipWidth=Ui(36)+ImGui.CalcTextSize(users.Count.ToString()).X;
            if (chipX+chipWidth>origin.X+width) { chipX=body.X; chipY+=Ui(30); }
            ImGui.SetCursorScreenPos(new Vector2(chipX,chipY));
            var mineReacted=users.Contains(session.AccountId);
            if (AlphaChatTextAction("reaction"+emoji,users.Count.ToString(),ChatReactionIcons[emoji],ChatEmojiColor(emoji),chipWidth,Ui(25),mineReacted))
                SendDmReaction(session,conversationId,message.GroupId,emoji,!mineReacted);
            DrawChatReactionTooltip(emoji, users.Count, mineReacted, users.Select(id => ChatReactionDisplayName(id)));
            chipX+=chipWidth+Ui(6); bottomY=chipY+Ui(30);
        }
        var menuId="##dmActions"; var reactId="##dmReact";
        var rowHeight=MathF.Max(Ui(40),bottomY-origin.Y);
        if (ChatPointerInput.IsHoveringRect(origin,origin+new Vector2(width,rowHeight)) && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) dmActionsRequested=message.Id;
        DrawSharedChatHoverActions(origin,menuOrigin,width,rowHeight,messageEncryptionReady==true,
            dmOpenActionsMessage==message.Id || dmOpenReactionMessage==message.Id,
            ()=>messageReplyTarget=message,()=>dmReactionRequested=message.Id,()=>dmActionsRequested=message.Id);
        DrawModernDmMenus(session,conversationId,message,text,raw,reactId,menuId,mine,reactions);
        ImGui.SetCursorScreenPos(new Vector2(origin.X,MathF.Max(origin.Y+Ui(40),bottomY)+Ui(20)));
        if (messageJumpToGroupId==message.GroupId)
        {
            ImGui.Dummy(Vector2.One); ImGui.SetScrollHereY(0.35f);
            messageHighlightedGroupId=message.GroupId; messageHighlightUntilUtc=DateTime.UtcNow.AddSeconds(2);
            messageJumpToGroupId=null;
        }
    }

    private string? dmActionsRequested,dmReactionRequested,dmOpenActionsMessage,dmOpenReactionMessage;
    private void DrawModernDmMenus(CharacterSession session,string conversationId,MessageDto message,string text,string? raw,
        string reactId,string menuId,bool mine,Dictionary<(string Target,int Emoji),HashSet<string>> reactions)
    {
        if (dmActionsRequested==message.Id) { ImGui.OpenPopup(menuId); dmActionsRequested=null; }
        if (dmReactionRequested==message.Id) { ImGui.OpenPopup(reactId); dmReactionRequested=null; }
        if (dmOpenActionsMessage==message.Id && !ImGui.IsPopupOpen(menuId)) dmOpenActionsMessage=null;
        if (dmOpenReactionMessage==message.Id && !ImGui.IsPopupOpen(reactId)) dmOpenReactionMessage=null;
        if (ImGui.IsPopupOpen(reactId)) dmOpenReactionMessage=message.Id;
        DrawChatComposerEmojiPopup(reactId,emoji=>
        {
            var reacted=reactions.TryGetValue((message.GroupId,emoji),out var users)&&users.Contains(session.AccountId);
            SendDmReaction(session,conversationId,message.GroupId,emoji,!reacted);
        },"React to message");
        using var style=ImRaii.PushStyle(ImGuiStyleVar.PopupBorderSize,Ui(1)).Push(ImGuiStyleVar.PopupRounding,Ui(10))
            .Push(ImGuiStyleVar.WindowPadding,UiVec(16,14)).Push(ImGuiStyleVar.ItemSpacing,UiVec(8,8));
        using var colors=ImRaii.PushColor(ImGuiCol.PopupBg,CardBg).Push(ImGuiCol.Border,new Vector4(AccentHover.X,AccentHover.Y,AccentHover.Z,.3f));
        if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup(menuId))) return;
        dmOpenActionsMessage=message.Id;
        if (ImGui.MenuItem("Reply")) messageReplyTarget=message;
        if (ImGui.MenuItem("React")) dmReactionRequested=message.Id;
        if (ImGui.MenuItem("Copy message")) ImGui.SetClipboardText(ExpandChatEmojiShortcodes(text));
        if (GroupChatClient.ImageContent(raw) is not null && groupImageViews.TryGetValue(message.Id, out var imageView))
            using (UiDisabled.Scope(imageView.Bytes is null)) if (ImGui.MenuItem("Save image…")) SaveGroupImage(session, imageView);
        ImGui.Separator();
        var name=memberDisplayNameCache.GetValueOrDefault(message.SenderAccountId,mine?session.DisplayName:"Someone");
        DrawUsername(message.SenderAccountId,name,AccentHover);
        if (PatreonBadgeTier(message.SenderAccountId)>0) { ImGui.SameLine(0,Ui(6)); DrawAlphaChatSupporterHeart(); }
        ImGui.Separator();
        if (ImGui.MenuItem("Mention")) { messageComposerInput+="@"+name+" "; messageComposerFocus=true; }
        DrawDmPersonActions(session,message.SenderAccountId,name,includeBlock:false);
        using (ImRaii.PushColor(ImGuiCol.Text,Danger))
        {
            if (!mine && raw is not null && raw!=DecryptFailedMarker && ImGui.MenuItem("Report message")) QueueDmMessageReport(message,raw);
            if (!mine&&ImGui.MenuItem(AlphaChatBlocked(message.SenderAccountId)?"Unblock user":"Block user"))
            { if (AlphaChatBlocked(message.SenderAccountId)) UnblockAccount(session,message.SenderAccountId); else QueueFriendBlockConfirmation(message.SenderAccountId,name); }
        }
        if (mine || dmClient.Groups.State.Groups.Any(g => g.Id == GroupChatClient.Raw(conversationId) && g.IsPublic && g.OwnerId == session.AccountId))
        {
            ImGui.Separator();
            if (ImGui.MenuItem("Delete message")) DeleteDmForAll(session,conversationId,message);
        }
        ImGui.EndPopup();
    }

    private void SendDmReaction(CharacterSession session, string conversationId, string targetGroupId,
        int emoji, bool active)
    {
        SendEncryptedDm(session, conversationId, EncodeRichDm(new RichDmContent("reaction",
            Target: targetGroupId, Emoji: emoji, Active: active)), showInFeed: false);
    }

    private void FindDmReplyTarget(CharacterSession session, string conversationId, string targetGroupId)
    {
        if (messageLoadingReplyTarget)
            return;
        messageLoadingReplyTarget = true;
        messagesLoadingOlder = true;
        var generation = Volatile.Read(ref messagesRequestGeneration);
        bool IsCurrentLookup() => IsCurrentMessageSession(session) &&
            openConversationId == conversationId && generation == Volatile.Read(ref messagesRequestGeneration);
        _ = Task.Run(async () =>
        {
            try
            {
                var identity = GroupChatClient.IsGroup(conversationId) ? null : await keyVault.EnsureIdentityAsync(session.AccountId, session.Token);
                while (IsCurrentLookup() && messagesNextCursor is { } cursor &&
                       !openMessages.Any(message => message.GroupId == targetGroupId))
                {
                    var before = cursor;
                    var page = await dmClient.GetMessagesAsync(session.Token, conversationId, before);
                    if (page is null || !IsCurrentLookup())
                        break;
                    var older = page.Items.Reverse().ToArray();
                    foreach (var message in older)
                        await DecryptAndCacheAsync(session, message, identity);
                    await Plugin.Framework.RunOnFrameworkThread(() =>
                    {
                        if (!IsCurrentLookup()) return;
                        var existing = openMessages.Select(message => message.GroupId).ToHashSet();
                        openMessages = [.. older.Where(message => !messageDeletedGroups.ContainsKey(message.GroupId) &&
                            !existing.Contains(message.GroupId)), .. openMessages];
                        messagesNextCursor = page.NextCursor;
                    });
                    if (older.Length == 0 || page.NextCursor == cursor)
                        break;
                }
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (IsCurrentLookup() && !openMessages.Any(message => message.GroupId == targetGroupId))
                        messageJumpToGroupId = null;
                });
            }
            catch (Exception)
            {
                AepLog.Warning("[Messages] Reply target lookup failed.");
            }
            finally
            {
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (!IsCurrentLookup()) return;
                    messageLoadingReplyTarget = false;
                    messagesLoadingOlder = false;
                });
            }
        });
    }

    private void DeleteDmForAll(CharacterSession session, string conversationId, MessageDto message)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!GroupChatClient.IsMessage(message) && GroupChatClient.ImageContent(decryptedCache.GetValueOrDefault(message.Id)) is not null)
                    await dmClient.Groups.RevokeDirectImageAsync(session.Token, conversationId, message, ImagePlaintext(message));
                var deleted = await dmClient.DeleteMessageForAllAsync(session.Token, conversationId, message.GroupId);
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (!IsCurrentMessageSession(session) || openConversationId != conversationId) return;
                    if (!deleted) { messagesError = "Couldn't delete that message."; return; }
                    messageDeletedGroups[message.GroupId] = 0;
                    openMessages = openMessages.Where(item => item.GroupId != message.GroupId).ToArray();
                    if (messageReplyTarget?.GroupId == message.GroupId) messageReplyTarget = null;
                    if (GroupChatClient.IsGroup(conversationId)) MergeCloudGroupConversations(); else conversationsDirty = true;
                });
            }
            catch (Exception)
            {
                AepLog.Warning("[Messages] delete failed.");
            }
        });
    }

    private void DrawModernDmComposer(CharacterSession session,string conversationId,float height)
    {
        var width=ImGui.GetContentRegionAvail().X;
        using var padding=ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,UiVec(12,12));
        using var child=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##dmComposer",new Vector2(width,height),false,
            ImGuiWindowFlags.AlwaysUseWindowPadding|ImGuiWindowFlags.NoScrollbar|ImGuiWindowFlags.NoScrollWithMouse));
        if (!child) return;
        var blocked=IsOpenDmBlocked(session);
        if (blocked)
        {
            var noticeOrigin=ImGui.GetCursorScreenPos();
            var noticeWidth=ImGui.GetContentRegionAvail().X;
            DrawBlockedDmNotice(session,noticeOrigin,noticeWidth);
            ImGui.SetCursorScreenPos(noticeOrigin+new Vector2(0,BlockedDmNoticeHeight(noticeWidth)));
        }
        using var disabled=UiDisabled.Scope(blocked);
        if (messageReplyTarget is { } reply)
        {
            var start=ImGui.GetCursorScreenPos(); var inner=ImGui.GetContentRegionAvail().X;
            var name=memberDisplayNameCache.GetValueOrDefault(reply.SenderAccountId,reply.SenderAccountId==session.AccountId?session.DisplayName:"Someone");
            DrawUsername(reply.SenderAccountId,"Replying to "+name,AccentHover);
            var raw=decryptedCache.GetValueOrDefault(reply.Id); var rich=DecodeRichDm(raw);
            var preview=raw==DecryptFailedMarker?"Couldn't decrypt — key missing":rich?.Kind=="watch_party_invite"?"Shared a watch party":rich?.Text??(raw is null ? "Decrypting…" : GroupChatClient.ImageCaption(raw));
            using (UiFonts.PushRelative(.90f)) LayoutChatRichText(TruncateChatReplyPreview(preview,inner-Ui(40)),ImGui.GetCursorScreenPos(),inner-Ui(40),true,MutedText,interactiveLinks:false);
            ImGui.SetCursorScreenPos(start+new Vector2(inner-Ui(26),0));
            if (AlphaChatButton("cancelDmReply","",FontAwesomeIcon.Times,Ui(26),Ui(26))) messageReplyTarget=null;
            ImGui.SetCursorScreenPos(start+UiVec(0,50));
        }
        DrawGroupImageDraft(conversationId);
        var imageDraft = groupImageDrafts.GetValueOrDefault(conversationId);
        var hasImage = CanAttachGroupImage(conversationId) && imageDraft is not null;
        var imageBusy = hasImage && imageDraft!.Busy;
        using var sendingImage = UiDisabled.Scope(imageBusy);
        var origin=ImGui.GetCursorScreenPos(); var available=ImGui.GetContentRegionAvail().X;
        if (AlphaChatButton("dmEmoji","",FontAwesomeIcon.Smile,Ui(44),Ui(48),primary:true)) ImGui.OpenPopup("##dmEmoji");
        DrawChatComposerEmojiPopup("##dmEmoji",emoji=> { if (messageComposerInput.Length+ChatReactionShortcodes[emoji].Length<=2000) messageComposerInput+=ChatReactionShortcodes[emoji]; });
        var inputOffset = CanAttachGroupImage(conversationId) || session.IsGuest ? 102f : 54f;
        if (session.IsGuest)
        {
            ImGui.SetCursorScreenPos(origin+UiVec(52,0));
            if (AlphaChatButton("verifyImagePosting", "", FontAwesomeIcon.Image, Ui(40), Ui(48))) StartSignIn(session);
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Verify character to access this");
        }
        if (CanAttachGroupImage(conversationId)) {
            ImGui.SetCursorScreenPos(origin+UiVec(52,0));
            using (UiDisabled.Scope(!dmClient.Groups.Ready || messageEncryptionReady != true))
                if (AlphaChatButton("attachGroupImage", "", FontAwesomeIcon.Image, Ui(40), Ui(48))) ChooseGroupImage(session, conversationId);
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Attach an image · PNG, JPEG or WebP · up to 20 MB");
        }
        ImGui.SetCursorScreenPos(origin+UiVec(inputOffset,0));
        ImGui.SetNextItemWidth(MathF.Max(Ui(60),available-Ui(inputOffset+102)));
        if (messageComposerFocus&&!blocked) { ImGui.SetKeyboardFocusHere(); messageComposerFocus=false; }
        bool enter;
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding,new Vector2(Ui(12),MathF.Max(0,(Ui(48)-ImGui.GetFontSize())*.5f))))
            enter=ImGui.InputTextWithHint("##dmDraft",$"Message {openConversationTitle}…",ref messageComposerInput,2000,ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SetCursorScreenPos(origin+new Vector2(available-Ui(92),0));
        bool send;
        using (UiDisabled.Scope(messageEncryptionReady!=true||!hasImage&&string.IsNullOrWhiteSpace(messageComposerInput)))
            send=ActivePartyButton("Send",UiVec(92,48),primary:true);
        if (!blocked&&!imageBusy&&(enter||send)&&messageEncryptionReady==true&&(hasImage||!string.IsNullOrWhiteSpace(messageComposerInput)))
        {
            if (hasImage) { SendGroupImage(session,conversationId,messageComposerInput.Trim()); return; }
            SendMessage(session,conversationId,messageComposerInput.Trim(),messageReplyTarget?.GroupId);
            messageComposerInput=""; messageReplyTarget=null; messageComposerFocus=true;
        }
    }
}

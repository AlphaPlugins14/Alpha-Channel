using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Collections.Concurrent;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string chatsPageSearch=string.Empty;
    private int chatsPageFilter;
    private bool chatsPageArchived;
    private sealed record ChatsPreview(string GroupId,string SenderId,string Text,long At);
    private readonly ConcurrentDictionary<string,ChatsPreview> chatsPagePreviews=new();
    private bool chatsCreationOpen;
    private bool chatsCreationGroup;
    private CharacterSession? chatsCreationSession;
    private string chatsCreationSearch=string.Empty;
    private string? chatsCreationError;
    private Task<string?>? chatsDirectCreationTask;
    private FriendDto? chatsDirectTarget;

    private void UpdateChatsPreview(string conversationId,MessageDto message,string? plaintext)
    {
        if (GroupChatClient.IsMessage(message) && AlphaChatBlocked(message.SenderAccountId)) return;
        if (string.IsNullOrEmpty(plaintext)||plaintext==DecryptFailedMarker) return;
        plaintext = GroupChatClient.ImageCaption(plaintext);
        var rich=DecodeRichDm(plaintext);
        if (rich?.Kind=="reaction") return;
        var text=rich?.Kind=="watch_party_invite"?"Watch Party invitation":rich?.Text??plaintext;
        if (text.StartsWith(RichDmPrefix,StringComparison.Ordinal)) text="Encrypted message";
        var preview=new ChatsPreview(message.GroupId,message.SenderAccountId,text,message.SentAtUnix);
        chatsPagePreviews.AddOrUpdate(conversationId,preview,(_,old)=>old.At>preview.At?old:preview);
    }

    private void DrawChatsPage()
    {
        if (CurrentSession is not { } session)
        {
            DrawPlainEmpty("Sign in to view your chats.","Open Settings",()=>currentPage=HomePage.Settings); return;
        }
        DrawCloudGroupInvitations(session);
        if (conversationsDirty&&!conversationsLoading&&DateTime.UtcNow>=conversationsNextRetryUtc) RefreshConversations(session.Token);
        using var style=ImRaii.PushStyle(ImGuiStyleVar.ChildRounding,Ui(10)).Push(ImGuiStyleVar.FrameRounding,Ui(8))
            .Push(ImGuiStyleVar.FrameBorderSize,Ui(1)).Push(ImGuiStyleVar.ItemSpacing,UiVec(10,10));
        using var colors=ImRaii.PushColor(ImGuiCol.ChildBg,FrameBg).Push(ImGuiCol.FrameBg,FrameBg)
            .Push(ImGuiCol.Button,FrameBg).Push(ImGuiCol.ButtonHovered,FrameBgHover).Push(ImGuiCol.Border,AccentInk(.3f));
        var origin=ImGui.GetCursorScreenPos();
        var width=ImGui.GetContentRegionAvail().X;
        var stacked=width<Ui(600);
        var activeCount=conversations.Count(c=>!IsArchivedChat(c));
        DrawSocialHeading(chatsPageArchived?"Archived chats":$"Your chats · {activeCount}", 1.3f);
        ImGui.SetCursorScreenPos(origin+new Vector2(stacked?0:width-Ui(324),Ui(stacked?34:0)));
        if (AlphaChatButton("newDirectChat","New direct chat",FontAwesomeIcon.Comments,Ui(172))) OpenChatsCreation(session,false);
        ImGui.SameLine();
        if (AlphaChatButton("newGroupChat","Create group",FontAwesomeIcon.UserPlus,Ui(142),primary:true)) OpenChatsCreation(session,true);
        var toolbarOrigin=origin+new Vector2(0,Ui(stacked?88:54));
        ImGui.SetCursorScreenPos(toolbarOrigin);
        var filterWidth=MathF.Min(Ui(330),width*.5f);
        var searchWidth=width-filterWidth-Ui(12);
        ImGui.SetNextItemWidth(searchWidth);
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding,new Vector2(Ui(12),(Ui(36)-ImGui.GetFontSize())*.5f)))
            ImGui.InputTextWithHint("##chatsSearch","Search chats…",ref chatsPageSearch,100);
        DrawChatsPageFilters(toolbarOrigin+new Vector2(searchWidth+Ui(12),0),filterWidth);
        ImGui.SetCursorScreenPos(toolbarOrigin+UiVec(0,52));
        ImGui.Separator();
        var archivedCount=conversations.Count(IsArchivedChat);
        var footerHeight=chatsPageArchived||archivedCount>0?Ui(52):0;
        using (ImRaii.PushColor(ImGuiCol.ChildBg,Vector4.Zero))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,Vector2.Zero))
        using (var list=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(chatsPageArchived?"##archivedChatsPageList":"##activeChatsPageList",
            new Vector2(width,MathF.Max(Ui(50),ImGui.GetContentRegionAvail().Y-footerHeight)),false)))
        if (list)
        {
            var matches=conversations.Where(c=>IsArchivedChat(c)==chatsPageArchived)
                .Where(c=>chatsPageFilter switch {1=>c.UnreadCount>0,2=>!c.IsGroup,3=>c.IsGroup,_=>true})
                .Where(c=>string.IsNullOrWhiteSpace(chatsPageSearch)||ConversationTitle(c).Contains(chatsPageSearch.Trim(),StringComparison.OrdinalIgnoreCase)||
                    c.Members.Any(m=>m.DisplayName.Contains(chatsPageSearch.Trim(),StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(c=>c.LastMessageAtUnix??0).ToArray();
            if (conversationsLoading&&conversations.Length==0) ImGui.TextDisabled("Loading chats…");
            else if (conversationsDirty&&!conversationsLoading)
            {
                ImGui.TextWrapped("Couldn't load chats. Please try again.");
                if (ImGui.Button("Retry##chats")) {conversationsNextRetryUtc=DateTime.MinValue;conversationsDirty=true;}
            }
            if (!conversationsLoading&&matches.Length==0)
            {
                ImGui.TextColored(MutedText,conversations.Length==0?"No chats yet. Start a direct chat or create a group above.":
                    chatsPageArchived?"No archived chats match your filters.":"No chats match your search or filters.");
            }
            foreach (var chat in matches) DrawChatsPageRow(session,chat,ImGui.GetContentRegionAvail().X);
        }
        if (chatsPageArchived)
        {
            if (AlphaChatButton("backToActiveChats","Back to active chats",FontAwesomeIcon.ArrowLeft,width)) chatsPageArchived=false;
        }
        else if (archivedCount>0)
            if (AlphaChatButton("openArchivedChats",$"Archived chats · {archivedCount}",FontAwesomeIcon.Archive,width)) chatsPageArchived=true;
    }

    private void DrawChatsPageFilters(Vector2 origin,float width)
    {
        var draw=ImGui.GetWindowDrawList(); var height=Ui(36); var part=width/4;
        draw.AddRectFilled(origin,origin+new Vector2(width,height),ImGui.GetColorU32(FrameBg),Ui(8));
        draw.AddRect(origin,origin+new Vector2(width,height),ImGui.GetColorU32(AccentInk(.35f)),Ui(8));
        string[] labels=["All","Unread","Direct","Groups"];
        for(var i=0;i<labels.Length;i++)
        {
            var min=origin+new Vector2(part*i,0); ImGui.SetCursorScreenPos(min);
            if(ImGui.InvisibleButton($"##chatsFilter{i}",new Vector2(part,height))) chatsPageFilter=i;
            var selected=chatsPageFilter==i;
            if(selected||ImGui.IsItemHovered()) draw.AddRectFilled(min+UiVec(3,3),min+new Vector2(part,height)-UiVec(3,3),
                ImGui.GetColorU32(selected?new Vector4(Accent.X,Accent.Y,Accent.Z,.32f):CardBgHover),Ui(6));
            if(i>0) draw.AddLine(min+UiVec(0,10),min+UiVec(0,26),ImGui.GetColorU32(AccentInk(.3f)));
            var size=ImGui.CalcTextSize(labels[i]);
            draw.AddText(min+new Vector2((part-size.X)*.5f,(height-size.Y)*.5f),ImGui.GetColorU32(selected?Vector4.One:MutedText),labels[i]);
            if(ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
    }

    private void DrawChatsPageRow(CharacterSession session,ConversationSummaryDto chat,float width)
    {
        using var id=ImRaii.PushId(chat.ConversationId);
        var origin=ImGui.GetCursorScreenPos(); var height=Ui(76); var draw=ImGui.GetWindowDrawList();
        var clicked=ImGui.InvisibleButton("##openInboxChat",new Vector2(width-Ui(52),height));
        var hovered=ImGui.IsItemHovered();
        draw.AddRectFilled(origin,origin+new Vector2(width,height),ImGui.GetColorU32(hovered?CardBgHover:FrameBg),Ui(10));
        draw.AddRect(origin,origin+new Vector2(width,height),ImGui.GetColorU32(AccentInk(hovered ? .5f : .25f)),Ui(10));
        if(hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        var member=chat.Members.FirstOrDefault();
        if (!chat.IsGroup&&member is not null)
        {
            var friend=friendsAccountId==session.AccountId?friends.FirstOrDefault(f=>f.AccountId==member.AccountId):null;
            if(friend is not null) DrawAvatarAt(origin+UiVec(14,16),friend.AvatarIcon,friend.AvatarColorHex,Ui(44),friend.AvatarImageUrl);
            else { EnsurePartyAvatarLoaded(member.AccountId,member.DisplayName); partyAvatarCache.TryGetValue(member.AccountId,out var avatar);
                DrawAvatarAt(origin+UiVec(14,16),avatar?.AvatarIcon,avatar?.AvatarColorHex,Ui(44),avatar?.AvatarImageUrl); }
        }
        else
        {
            var center=origin+UiVec(36,38); draw.AddCircleFilled(center,Ui(22),ImGui.GetColorU32(AccentInk(.5f)));
            UiFonts.DrawIcon(draw, Ui(22), center-UiVec(12,11), ImGui.GetColorU32(Vector4.One), FontAwesomeIcon.Users.ToIconString());
        }
        var title=ConversationTitle(chat); var textOrigin=origin+UiVec(72,14);
        var titleWidth=MathF.Max(Ui(40),width-Ui(246)-(chat.IsGroup?Ui(100):0));
        ImGui.SetCursorScreenPos(textOrigin);
        if(chat.IsGroup) ImGui.TextUnformatted(FitPartyTextToWidth(title,titleWidth));
        else DrawUsername(member?.AccountId,FitPartyTextToWidth(title,titleWidth),chat.UnreadCount>0?Vector4.One:AccentHover);
        if (!chat.IsGroup&&member is not null&&PatreonBadgeTier(member.AccountId)>0) {ImGui.SameLine(0,Ui(4));DrawAlphaChatSupporterHeart();}
        if(chat.IsGroup&&width>=Ui(520)) {ImGui.SameLine(0,Ui(10));DrawSocialHelper($"· {chat.Members.Length+1} members");}
        var previewText="No messages yet";
        if(chat.LastMessageAtUnix is not null)
        {
            previewText="Encrypted message";
            if(chatsPagePreviews.TryGetValue(chat.ConversationId,out var preview)&&preview.At>=chat.LastMessageAtUnix)
            {
                previewText=preview.Text.Replace('\r',' ').Replace('\n',' ');
                if(chat.IsGroup)
                {
                    var sender=preview.SenderId==session.AccountId?session.DisplayName:chat.Members.FirstOrDefault(m=>m.AccountId==preview.SenderId)?.DisplayName??"Member";
                    previewText=$"{sender}: {previewText}";
                }
            }
        }
        ImGui.SetCursorScreenPos(origin+UiVec(72,42));
        using (UiFonts.PushRelative(.90f))
            ImGui.TextColored(chat.UnreadCount>0?Vector4.One:MutedText,FitPartyTextToWidth(previewText,MathF.Max(Ui(40),width-Ui(226))));
        var time=chat.LastMessageAtUnix is { } at?FormatRelativeTime(at):"";
        using (UiFonts.PushRelative(.90f))
        {
            var timeSize=ImGui.CalcTextSize(time);
            draw.AddText(origin+new Vector2(width-Ui(68)-timeSize.X,Ui(15)),ImGui.GetColorU32(MutedText),time);
        }
        if(chat.UnreadCount>0) DrawUnreadMessageBadge(origin+new Vector2(width-Ui(82),Ui(51)),chat.UnreadCount);
        ImGui.SetCursorScreenPos(origin+new Vector2(width-Ui(48),Ui(21)));
        if(AlphaChatButton("chatRowMore","",FontAwesomeIcon.EllipsisH,Ui(34),Ui(34))) ImGui.OpenPopup("##inboxChatMenu");
        var menuAnchor=ImGui.GetItemRectMax()+UiVec(0,6);
        ImGui.SetNextWindowSize(UiVec(210,0),ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(menuAnchor,ImGuiCond.Always,new Vector2(1,0));
        using var menuStyle=ImRaii.PushStyle(ImGuiStyleVar.PopupBorderSize,Ui(1)).Push(ImGuiStyleVar.PopupRounding,Ui(10))
            .Push(ImGuiStyleVar.WindowPadding,UiVec(16,14)).Push(ImGuiStyleVar.ItemSpacing,UiVec(8,8));
        using var menuColors=ImRaii.PushColor(ImGuiCol.PopupBg,CardBg)
            .Push(ImGuiCol.Border,new Vector4(AccentHover.X,AccentHover.Y,AccentHover.Z,.3f));
        using(var popup=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Popup("##inboxChatMenu"))) if(popup)
        {
            if(ImGui.MenuItem("Open chat")) clicked=true;
            ImGui.Separator();
            if(chat.IsGroup) ImGui.TextUnformatted(FitPartyTextToWidth(title,Ui(178)));
            else
            {
                DrawUsername(member?.AccountId,FitPartyTextToWidth(title,Ui(152)),AccentHover);
                if(member is not null&&PatreonBadgeTier(member.AccountId)>0) {ImGui.SameLine(0,Ui(6));DrawAlphaChatSupporterHeart();}
            }
            ImGui.Separator();
            if(chat.IsGroup)
            {
                if(UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginMenu("Members"))) {DrawDmMemberNames(chat.Members);ImGui.EndMenu();}
            }
            else if(member is not null && ImGui.MenuItem("View profile")) OpenProfilePage(session,member.AccountId,member.DisplayName);
            ImGui.Separator();
            if(ImGui.MenuItem(chatsPageArchived?"Restore chat":"Archive chat"))
            {if(chatsPageArchived) RestoreArchivedChat(session.AccountId,chat.ConversationId);else ArchiveChat(chat);}
            if(!chatsPageArchived&&ImGui.IsItemHovered()) UiFonts.SetTooltip("Move this chat to Archived chats. New messages bring it back to the active list.");
        }
        if(clicked) {OpenConversation(session,chat.ConversationId,chat.Members,chat.IsGroup,title);currentPage=HomePage.Messages;}
        ImGui.SetCursorScreenPos(origin);ImGui.Dummy(new Vector2(width,height));
    }

    private void OpenChatsCreation(CharacterSession session,bool group)
    {
        if(friendsDirty&&!friendsLoading) RefreshFriends(session.Token);
        chatsCreationOpen=true;chatsCreationGroup=group;chatsCreationSession=session;chatsCreationSearch="";chatsCreationError=null;
        hubGroupCreationType = -1; hubGroupDescription = ""; hubGroupCategory = 0;
        hubGroupIcon="Users"; hubGroupColor="#4D96FF";
        chatsDirectCreationTask=null;chatsDirectTarget=null;
        if(group) BeginFriendGroupSelection(session);
    }

    private void DrawChatsCreationOverlay()
    {
        if(!chatsCreationOpen) return;
        if (chatsCreationGroup) { DrawHubGroupCreationOverlay(); return; }
        if(CurrentSession is not { } session||!ReferenceEquals(session,chatsCreationSession))
        {chatsCreationOpen=false;EndFriendGroupSelection();return;}
        if(chatsCreationGroup)
        {
            CompleteFriendGroupCreation(session);
            if(!friendGroupSelectionMode) {chatsCreationOpen=false;return;}
            if(!friendsLoading) RefreshFriendGroupKeys(session);
            friendGroupSelectedIds.RemoveWhere(id=>!friends.Any(f=>f.AccountId==id)||friendGroupKeyStates.TryGetValue(id,out var state)&&state!=FriendGroupKeyState.Ready);
        }
        else if(chatsDirectCreationTask is { IsCompleted:true } task)
        {
            chatsDirectCreationTask=null;
            string? conversationId=null;
            try {conversationId=task.GetAwaiter().GetResult();} catch(Exception) {AepLog.Warning("[Messages] Direct chat creation failed.");}
            if(conversationId is not null&&chatsDirectTarget is { } target)
            {
                OpenConversation(session,conversationId,[new(target.AccountId,target.Handle,target.DisplayName)],false,target.DisplayName);
                currentPage=HomePage.Messages;chatsCreationOpen=false;return;
            }
            chatsCreationError="Couldn't open this chat. Please try again.";
        }
        var busy=chatsDirectCreationTask is not null||friendGroupCreationTask is not null;
        var parent=ImGui.GetWindowPos();var size=ImGui.GetWindowSize();
        var panelSize=new Vector2(MathF.Min(Ui(520),size.X-Ui(36)),MathF.Min(Ui(520),size.Y-Ui(36)));
        var position=parent+(size-panelSize)*.5f;
        ImGui.SetNextWindowPos(parent,ImGuiCond.Always);ImGui.SetNextWindowSize(size,ImGuiCond.Always);ImGui.SetNextWindowBgAlpha(0);
        const ImGuiWindowFlags flags=ImGuiWindowFlags.NoTitleBar|ImGuiWindowFlags.NoResize|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|
            ImGuiWindowFlags.NoScrollbar|ImGuiWindowFlags.NoScrollWithMouse|ImGuiWindowFlags.NoDocking|ImGuiWindowFlags.NoBackground;
        if(!BeginWindowOverlay("##chatsCreationOverlay",flags)) {ImGui.End();return;}
        ImGui.GetWindowDrawList().AddRectFilled(parent,parent+size,ImGui.GetColorU32(new Vector4(0,0,0,.58f)));
        if(!busy&&(ImGui.IsKeyPressed(ImGuiKey.Escape)||ImGui.IsMouseClicked(ImGuiMouseButton.Left)&&!ImGui.IsMouseHoveringRect(position,position+panelSize)))
            CloseChatsCreation();
        ImGui.SetCursorScreenPos(position);
        using(ImRaii.PushStyle(ImGuiStyleVar.ChildRounding,Ui(14)).Push(ImGuiStyleVar.ChildBorderSize,Ui(1))
            .Push(ImGuiStyleVar.WindowPadding,UiVec(24,20)).Push(ImGuiStyleVar.FrameRounding,Ui(8)).Push(ImGuiStyleVar.ItemSpacing,UiVec(8,8)))
        using(ImRaii.PushColor(ImGuiCol.ChildBg,CardBg).Push(ImGuiCol.Border,AccentInk(.65f)).Push(ImGuiCol.FrameBg,FrameBg))
        using(var panel=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##chatsCreationPanel",panelSize,true,ImGuiWindowFlags.AlwaysUseWindowPadding|ImGuiWindowFlags.NoScrollbar)))
        if(panel)
        {
            var origin=ImGui.GetCursorScreenPos();var width=ImGui.GetContentRegionAvail().X;
            DrawSocialHeading(chatsCreationGroup?"Create group chat":"New direct chat", 1.25f);
            ImGui.SetCursorScreenPos(origin+new Vector2(width-Ui(30),0));
            using(UiDisabled.Scope(busy)) if(AlphaChatTextAction("closeChatCreation","",FontAwesomeIcon.Times,Vector4.One,Ui(30),Ui(30))) CloseChatsCreation();
            ImGui.SetCursorScreenPos(origin+UiVec(0,34));
            DrawSocialHelperWrapped(chatsCreationGroup?"Choose a name and at least two friends.":"Choose a friend to start or continue a conversation.");
            if(chatsCreationGroup)
            {
                ImGui.TextColored(MutedText,"Group name");ImGui.SetNextItemWidth(width);
                using(UiDisabled.Scope(busy)) ImGui.InputTextWithHint("##chatsGroupName","Group name…",ref friendGroupNameInput,48);
                using(ImRaii.PushColor(ImGuiCol.ChildBg,Vector4.Zero))
                using(var chips=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##selectedGroupMembers",new Vector2(width,Ui(friendGroupSelectedIds.Count == 0 ? 24 : 56)),false)))
                if(chips)
                {
                    if(friendGroupSelectedIds.Count==0) DrawSocialHelper("No friends selected yet.");
                    else foreach(var selected in friendGroupSelectedIds.ToArray())
                    {
                        var name=friends.FirstOrDefault(f=>f.AccountId==selected)?.DisplayName??"Friend";
                        var chipWidth=MathF.Min(Ui(180),ImGui.CalcTextSize(name).X+Ui(42));
                        if(ImGui.GetContentRegionAvail().X<chipWidth) ImGui.NewLine();
                        using(UiDisabled.Scope(busy)) if(AlphaChatButton("removeGroupMember"+selected,FitPartyTextToWidth(name,chipWidth-Ui(34)),FontAwesomeIcon.Times,chipWidth,Ui(28))) friendGroupSelectedIds.Remove(selected);
                        ImGui.SameLine();
                    }
                }
            }
            ImGui.SetNextItemWidth(width);ImGui.InputTextWithHint("##chatsCreationSearch","Search friends…",ref chatsCreationSearch,100);
            var error=chatsCreationGroup?friendGroupError:chatsCreationError;
            if(error is { Length:>0 }) ImGui.TextColored(Danger,error);
            var listHeight=MathF.Max(Ui(80),position.Y+panelSize.Y-Ui(80)-ImGui.GetCursorScreenPos().Y);
            using(ImRaii.PushColor(ImGuiCol.ChildBg,Vector4.Zero))
            using(ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,Vector2.Zero))
            using(var list=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##chatsCreationFriendList",new Vector2(width,listHeight),false)))
            if(list)
            {
                if(friendsLoading) ImGui.TextDisabled("Loading friends…");
                if(friendsLoadError is { } loadError) {ImGui.TextWrapped(loadError);if(ImGui.Button("Retry##chatFriends")) friendsDirty=true;}
                if(friendsDirty&&!friendsLoading) RefreshFriends(session.Token);
                var matches=friends.Where(f=>string.IsNullOrWhiteSpace(chatsCreationSearch)||f.DisplayName.Contains(chatsCreationSearch.Trim(),StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f=>f.Online).ThenBy(f=>f.DisplayName,StringComparer.OrdinalIgnoreCase).ToArray();
                if(!friendsLoading&&matches.Length==0) ImGui.TextColored(MutedText,friends.Length==0?"Add some friends before starting a chat.":"No friends match your search.");
                using(UiDisabled.Scope(busy)) foreach(var friend in matches)
                {
                    using var id=ImRaii.PushId(friend.AccountId);
                    if(chatsCreationGroup) DrawFriendGroupSelectionCard(friend,ImGui.GetContentRegionAvail().X);
                    else DrawChatsDirectPickerRow(session,friend,ImGui.GetContentRegionAvail().X);
                }
            }
            ImGui.SetCursorScreenPos(position+new Vector2(Ui(24),panelSize.Y-Ui(56)));
            using(UiDisabled.Scope(busy)) if(AlphaChatButton("cancelChatCreation","Cancel",FontAwesomeIcon.Times,Ui(110))) CloseChatsCreation();
            if(chatsCreationGroup)
            {
                ImGui.SameLine();ImGui.TextColored(MutedText,$"{friendGroupSelectedIds.Count} selected");
                ImGui.SetCursorScreenPos(position+new Vector2(panelSize.X-Ui(188),panelSize.Y-Ui(56)));
                var ready=friendGroupSelectedIds.Count>=2&&friendGroupSelectedIds.All(id=>friendGroupKeyStates.TryGetValue(id,out var state)&&state==FriendGroupKeyState.Ready);
                using(UiDisabled.Scope(busy||!ready||string.IsNullOrWhiteSpace(friendGroupNameInput)))
                    if(AlphaChatButton("submitChatsGroup",busy?"Creating…":"Create group",FontAwesomeIcon.Users,Ui(164),primary:true))
                    {
                        friendGroupSubmittedFriends=friends.Where(f=>friendGroupSelectedIds.Contains(f.AccountId)).ToArray();
                        friendGroupSubmittedName=friendGroupNameInput.Trim();friendGroupError=null;
                        friendGroupCreationTask=dmClient.CreateConversationAsync(session.Token,friendGroupSubmittedFriends.Select(f=>f.AccountId).ToArray(),friendGroupSubmittedName);
                    }
            }
            else if(busy) {ImGui.SameLine();ImGui.TextDisabled("Opening chat…");}
        }
        ImGui.End();
    }

    private void DrawChatsDirectPickerRow(CharacterSession session,FriendDto friend,float width)
    {
        var origin=ImGui.GetCursorScreenPos();var draw=ImGui.GetWindowDrawList();
        var clicked=ImGui.InvisibleButton("##selectDirectChat",new Vector2(width,Ui(64)));
        if(ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        draw.AddRectFilled(origin,origin+new Vector2(width,Ui(64)),ImGui.GetColorU32(ImGui.IsItemHovered()?CardBgHover:FrameBg),Ui(9));
        DrawAvatarAt(origin+UiVec(12,12),friend.AvatarIcon,friend.AvatarColorHex,Ui(40),friend.AvatarImageUrl);
        DrawUsernameAt(draw,origin+UiVec(64,12),FitPartyTextToWidth(friend.DisplayName,width-Ui(100)),Vector4.One,friend.AccountId);
        using (UiFonts.PushRelative(.90f)) draw.AddText(origin+UiVec(64,36),ImGui.GetColorU32(friend.Online?Good:MutedText),friend.Online?"Online":"Offline");
        if(clicked&&chatsDirectCreationTask is null) {chatsDirectTarget=friend;chatsDirectCreationTask=dmClient.CreateConversationAsync(session.Token,[friend.AccountId]);}
    }

    private void CloseChatsCreation()
    {
        chatsCreationOpen=false;
        if(chatsCreationGroup) EndFriendGroupSelection();
    }
}

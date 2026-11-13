using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private int friendsRoomSnapshotGeneration = -1;
    private string friendsRoomSnapshotKey = string.Empty;
    private bool friendsWatchingCollapsed;
    private bool friendsWatchingShowAll;

    private static bool FriendHasParty(FriendDto friend) => friend.Online &&
        (friend.HostingJoinableWatchParty || !string.IsNullOrWhiteSpace(friend.WatchingLabel));

    private void DrawModernFriendsPage(CharacterSession session)
    {
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10))
            .Push(ImGuiStyleVar.FrameRounding, Ui(8)).Push(ImGuiStyleVar.WindowPadding, UiVec(14,12))
            .Push(ImGuiStyleVar.ItemSpacing, UiVec(10,10)).Push(ImGuiStyleVar.ChildBorderSize, Ui(1))
            .Push(ImGuiStyleVar.FrameBorderSize,Ui(1));
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.FrameBg, FrameBg)
            .Push(ImGuiCol.Button, FrameBg).Push(ImGuiCol.ButtonHovered, FrameBgHover).Push(ImGuiCol.Border, AccentInk(.3f));

        // Resolve room details on friend refreshes or hosting presence changes, without a polling timer.
        var roomSnapshotKey=session.AccountId+"|"+string.Join("|",friends.Where(CanJoinFriendWatchParty)
            .OrderBy(f=>f.AccountId,StringComparer.Ordinal).Select(f=>$"{f.AccountId}:{f.WatchingLabel}"));
        if (!friendsLoading && (friendsRoomSnapshotGeneration != friendsRefreshGeneration || friendsRoomSnapshotKey != roomSnapshotKey))
        {
            friendsRoomSnapshotGeneration = friendsRefreshGeneration;
            friendsRoomSnapshotKey = roomSnapshotKey;
            if (friends.Any(CanJoinFriendWatchParty)) RefreshPartyDirectoryIfNeeded();
        }
        DrawFriendsOverviewBar(session);
        DrawFriendsUnifiedToolbar(session);
        if (friendsError is { Length: > 0 } message)
            ImGui.TextColored(friendsMessageIsSuccess ? Good : Danger, message);
        if (friendsLoadError is { Length: > 0 } loadError)
        {
            ImGui.TextWrapped(loadError);
            if (AlphaChatButton("friendsRetry", "Retry", FontAwesomeIcon.Redo, Ui(90), Ui(32))) friendsDirty=true;
        }
        if (friendGroupSelectionMode) DrawFriendGroupSelectionBar(session);

        var visible = friends.Where(f => friendListStatusFilter switch
            { 1 => f.Online, 2 => FriendHasParty(f), _ => true })
            .Where(f => string.IsNullOrWhiteSpace(friendListSearchInput) ||
                f.DisplayName.Contains(friendListSearchInput.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(CanJoinFriendWatchParty).ThenByDescending(FriendHasParty)
            .ThenByDescending(f => f.Online).ThenBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        DrawFriendsShareBar();
        DrawModernFriendRequests(session);
        var active = friendGroupSelectionMode ? [] : visible.Where(FriendHasParty).ToArray();
        if (active.Length > 0)
        {
            DrawFriendsWatchingTogether(session,active);
        }
        var ordinary = visible;
        if (ordinary.Length > 0)
        {
            if (friendGroupSelectionMode) FriendsSectionTitle("Choose friends for your group");
            DrawModernFriendGrid(session, ordinary, activeRoom:false);
        }
        if (visible.Length == 0)
        {
            FriendsSectionTitle(friendsLoading ? "Loading friends…" : friends.Length==0 ? "Watch together with friends" : "No matching friends");
            ImGui.TextWrapped(friends.Length==0 ? "Add a friend by username or share your friend code to get started." :
                "Try another search or choose All to see your friends.");
            if (friends.Length==0)
            {
                if (AlphaChatButton("emptyAddFriends", "Add friends", FontAwesomeIcon.UserPlus, Ui(140))) OpenFriendsAddPanel(session);
            }
            else if (AlphaChatButton("clearFriendFilters", "Show all friends", FontAwesomeIcon.Users, Ui(160)))
            { friendListSearchInput=""; friendListStatusFilter=0; }
        }
    }

    private void FriendsSectionTitle(string label)
    {
        ImGui.Dummy(UiVec(0,4));
        DrawSocialHeading(label, 1.3f);
    }

    private void DrawFriendsOverviewBar(CharacterSession session)
    {
        var origin=ImGui.GetCursorScreenPos();
        var width=ImGui.GetContentRegionAvail().X;
        var stacked=width<Ui(580);
        ImGui.SetCursorScreenPos(origin+UiVec(0,8));
        DrawSocialHeading($"Your friends · {friends.Length}", 1.3f);
        ImGui.SetCursorScreenPos(origin+new Vector2(stacked?0:width-Ui(354),Ui(stacked?42:0)));
        if (AlphaChatButton("myFriendsProfile","My profile",FontAwesomeIcon.User,Ui(112)))
            OpenProfilePage(session,session.AccountId,session.DisplayName);
        ImGui.SameLine(0,Ui(10));
        var unread=UnreadChatNotificationCount();
        var chatsOrigin=ImGui.GetCursorScreenPos();
        var chatsWidth=Ui(unread>0?112:102);
        if (AlphaChatButton("friendsViewChats","Chats",FontAwesomeIcon.Comments,chatsWidth))
        {
            conversationsDirty=true; OpenUnreadChatsHub();
        }
        if (unread>0)
            DrawUnreadMessageBadge(chatsOrigin+new Vector2(chatsWidth-Ui(13),Ui(8)),unread);
        ImGui.SameLine(0,Ui(10));
        if (AlphaChatButton("friendsAdd","Add friends",FontAwesomeIcon.UserPlus,Ui(unread>0?110:120),primary:true)) OpenFriendsAddPanel(session);
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width,Ui(stacked?78:36)));
        ImGui.SetCursorScreenPos(origin+new Vector2(0,Ui(stacked?96:54)));
    }

    private void DrawFriendsUnifiedToolbar(CharacterSession session)
    {
        var origin=ImGui.GetCursorScreenPos();
        var width=ImGui.GetContentRegionAvail().X;
        var stacked=width<Ui(550);
        var filterWidth=MathF.Min(Ui(320),stacked?width:width*.48f);
        var searchWidth=stacked?width:width-filterWidth-Ui(12);
        ImGui.SetNextItemWidth(searchWidth);
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding,new Vector2(Ui(12),(Ui(36)-ImGui.GetFontSize())*.5f)))
            ImGui.InputTextWithHint("##friendListSearch","Search friends…",ref friendListSearchInput,64);
        var filterOrigin=origin+new Vector2(stacked?0:searchWidth+Ui(12),Ui(stacked?46:0));
        DrawFriendsSegmentedFilters(filterOrigin,filterWidth);
        var end=origin+new Vector2(0,Ui(stacked?98:52));
        ImGui.GetWindowDrawList().AddLine(end,end+new Vector2(width,0),ImGui.GetColorU32(BorderSubtle));
        ImGui.SetCursorScreenPos(end);
        ImGui.Dummy(new Vector2(width,Ui(1)));
    }

    private void DrawFriendsSegmentedFilters(Vector2 origin,float width)
    {
        var draw=ImGui.GetWindowDrawList();
        var height=Ui(36);
        draw.AddRectFilled(origin,origin+new Vector2(width,height),ImGui.GetColorU32(FrameBg),Ui(8));
        draw.AddRect(origin,origin+new Vector2(width,height),ImGui.GetColorU32(AccentInk(.35f)),Ui(8));
        string[] labels=[$"All {friends.Length}",$"Online {friends.Count(f=>f.Online)}",$"In Party {friends.Count(FriendHasParty)}"];
        float[] shares=[.28f,.33f,.39f];
        var offset=0f;
        for (var i=0;i<3;i++)
        {
            var segmentWidth=width*shares[i];
            var min=origin+new Vector2(offset,0);
            ImGui.SetCursorScreenPos(min);
            if (ImGui.InvisibleButton($"##friendSegment{i}",new Vector2(segmentWidth,height))) friendListStatusFilter=i;
            var selected=friendListStatusFilter==i;
            if (selected||ImGui.IsItemHovered()) draw.AddRectFilled(min+UiVec(3,3),min+new Vector2(segmentWidth,height)-UiVec(3,3),
                ImGui.GetColorU32(selected?new Vector4(Accent.X,Accent.Y,Accent.Z,.32f):CardBgHover),Ui(6));
            if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (i>0) draw.AddLine(min+UiVec(0,10),min+UiVec(0,26),ImGui.GetColorU32(AccentInk(.3f)));
            var textSize=ImGui.CalcTextSize(labels[i]);
            draw.AddText(min+new Vector2((segmentWidth-textSize.X)*.5f,(height-textSize.Y)*.5f),
                ImGui.GetColorU32(selected?Vector4.One:MutedText),labels[i]);
            offset+=segmentWidth;
        }
    }

    private void DrawFriendsWatchingTogether(CharacterSession session,FriendDto[] active)
    {
        ImGui.Dummy(UiVec(0,4));
        var headingOrigin=ImGui.GetCursorScreenPos();
        using var uiFontScale8899=UiFonts.PushRelative(1.3f);
        var heading=$"Watching together · {active.Length}";
        var headingSize=ImGui.CalcTextSize(heading);
        if (ImGui.InvisibleButton("##friendsWatchingToggle",new Vector2(headingSize.X+Ui(30),Ui(30))))
            friendsWatchingCollapsed=!friendsWatchingCollapsed;
        var draw=ImGui.GetWindowDrawList();
        UiFonts.DrawIcon(draw, ImGui.GetFontSize(), headingOrigin+UiVec(0,5), ImGui.GetColorU32(AccentHover), (friendsWatchingCollapsed?FontAwesomeIcon.ChevronRight:FontAwesomeIcon.ChevronDown).ToIconString());
        draw.AddText(headingOrigin+new Vector2(Ui(26),(Ui(30)-headingSize.Y)*.5f),ImGui.GetColorU32(Hex(0xE2E7EF)),heading);
        if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        uiFontScale8899.Dispose();
        if (friendsWatchingCollapsed) return;
        var shown=friendsWatchingShowAll?active:active.Take(2).ToArray();
        foreach (var friend in shown)
        {
            using var id=ImRaii.PushId("roomStrip"+friend.AccountId);
            DrawFriendsRoomStrip(session,friend,ImGui.GetContentRegionAvail().X);
        }
        if (active.Length>2)
            if (AlphaChatButton("friendsMoreParties",friendsWatchingShowAll?"Show fewer parties":$"Show {active.Length-2} more parties",
                friendsWatchingShowAll?FontAwesomeIcon.ChevronUp:FontAwesomeIcon.ChevronDown,Ui(190),Ui(30)))
                friendsWatchingShowAll=!friendsWatchingShowAll;
    }

    private void DrawFriendsRoomStrip(CharacterSession session,FriendDto friend,float width)
    {
        var canJoin=CanJoinFriendWatchParty(friend);
        var room=canJoin&&partyDirectoryAccountId==session.AccountId&&roomsClient.LastFailure is null
            ?partyDirectoryRooms.FirstOrDefault(r=>r.HostAccountId==friend.AccountId):null;
        var stacked=width<Ui(620);
        var height=Ui(stacked?138:96);
        using var colors=ImRaii.PushColor(ImGuiCol.ChildBg,FrameBg).Push(ImGuiCol.Border,AccentInk(.45f));
        using var child=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##friendRoomStrip",new Vector2(width,height),true,ImGuiWindowFlags.NoScrollbar|ImGuiWindowFlags.NoScrollWithMouse));
        if (!child) return;
        var origin=ImGui.GetWindowPos();
        var draw=ImGui.GetWindowDrawList();
        var thumbOrigin=origin+UiVec(14,16);
        if (room is not null) DrawPartyDirectoryThumbnail(draw,room,GetPartyDirectoryMedia(room),thumbOrigin,UiVec(100,64));
        else
        {
            draw.AddRectFilled(thumbOrigin,thumbOrigin+UiVec(100,64),ImGui.GetColorU32(CardBg),Ui(8));
            DrawAvatarAt(thumbOrigin+UiVec(30,12),friend.AvatarIcon,friend.AvatarColorHex,Ui(40),friend.AvatarImageUrl);
        }
        var infoOrigin=origin+UiVec(128,15);
        var infoWidth=MathF.Max(Ui(50),width-Ui(142)-(canJoin&&!stacked?Ui(230):0));
        ImGui.SetCursorScreenPos(infoOrigin);
        using var uiFontScale11833=UiFonts.PushRelative(1.2f);
        var title=canJoin?$"{friend.DisplayName}’s Watch Party":friend.DisplayName;
        var visibleTitle=FitPartyTextToWidth(title,infoWidth);
        ImGui.TextUnformatted(visibleTitle);
        if (visibleTitle!=title&&ImGui.IsItemHovered()) UiFonts.SetTooltip(title);
        uiFontScale11833.Dispose();
        var metadata=infoOrigin+UiVec(0,26);
        ImGui.SetCursorScreenPos(metadata);
        var status=canJoin?"Hosting":"Watching";
        draw.AddCircleFilled(metadata+new Vector2(Ui(3),ImGui.GetFontSize()*.5f),Ui(3),ImGui.GetColorU32(Good));
        ImGui.SetCursorScreenPos(metadata+UiVec(11,0));
        ImGui.TextColored(Good,status);
        if (room is not null)
        {
            var categoryOrigin=metadata+new Vector2(Ui(20)+ImGui.CalcTextSize(status).X,-Ui(3));
            var categoryWidth=DrawWatchPartyTag(draw,categoryOrigin,WatchPartyCategoryIcon(room),WatchPartyCategoryText(room),WatchPartyCategoryColor(room));
            var countOrigin=categoryOrigin+new Vector2(categoryWidth+Ui(9),Ui(3));
            var watching=$"{room.ViewerCount+1} watching";
            if (countOrigin.X+ImGui.CalcTextSize(watching).X<=infoOrigin.X+infoWidth)
            {
                ImGui.SetCursorScreenPos(countOrigin); ImGui.TextColored(MutedText,watching);
            }
        }
        ImGui.SetCursorScreenPos(infoOrigin+UiVec(0,49));
        var media=room is not null?GetPartyDirectoryMedia(room).Title:friend.WatchingLabel;
        ImGui.TextColored(MutedText,FitPartyTextToWidth(media??(canJoin?"Come watch together.":"Watching a Watch Party"),infoWidth));
        if (canJoin)
        {
            var actions=stacked?origin+new Vector2(width-Ui(238),height-Ui(48)):origin+new Vector2(width-Ui(238),(height-Ui(36))*.5f);
            ImGui.SetCursorScreenPos(actions);
            if (AlphaChatButton("friendViewRoom","View room",FontAwesomeIcon.Eye,Ui(104))) ViewFriendRoom(friend);
            ImGui.SameLine(0,Ui(8));
            using (UiDisabled.Scope(stream.WatchPartyJoinBlockReason is not null))
                if (AlphaChatButton("friendJoinRoom","Join room",FontAwesomeIcon.Users,Ui(112),primary:true)) OpenPlayerAndJoin(friend.DisplayName);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)&&stream.WatchPartyJoinBlockReason is { } blocked) UiFonts.SetTooltip(blocked);
        }
    }

    private bool FriendsPrimaryButton(string label,float width,float height=36)
    {
        using var colors=ImRaii.PushColor(ImGuiCol.Button,Accent).Push(ImGuiCol.ButtonHovered,AccentHover)
            .Push(ImGuiCol.ButtonActive,AccentActive);
        return ImGui.Button(label,new Vector2(width,Ui(height)));
    }

    private void DrawModernFriendGrid(CharacterSession session,FriendDto[] entries,bool activeRoom)
    {
        var width=ImGui.GetContentRegionAvail().X;
        var columns=width>=Ui(activeRoom||friendGroupSelectionMode?850:650)?2:1;
        var cardWidth=(width-Ui(12)*(columns-1))/columns;
        for (var i=0;i<entries.Length;i++)
        {
            using var id=ImRaii.PushId(entries[i].AccountId);
            if (friendGroupSelectionMode) DrawFriendGroupSelectionCard(entries[i],cardWidth);
            else DrawModernFriendCard(session,entries[i],cardWidth,activeRoom);
            if (columns==2 && i%2==0 && i+1<entries.Length) ImGui.SameLine(0,Ui(12));
        }
    }

    private void DrawModernFriendCard(CharacterSession session,FriendDto friend,float width,bool activeRoom)
    {
        var compactActions=!activeRoom&&width<Ui(430);
        var height=Ui(activeRoom?196:76);
        using var surface=ImRaii.PushColor(ImGuiCol.ChildBg,FrameBg)
            .Push(ImGuiCol.Border,AccentInk(activeRoom ? .5f : .25f));
        using var child=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##modernFriend",new Vector2(width,height),true,
            ImGuiWindowFlags.NoScrollbar|ImGuiWindowFlags.NoScrollWithMouse));
        if (!child) return;
        var origin=ImGui.GetWindowPos();
        var draw=ImGui.GetWindowDrawList();
        var avatarOrigin=origin+UiVec(14,14);
        ImGui.SetCursorScreenPos(avatarOrigin);
        DrawAvatarChip(friend.AvatarIcon,friend.AvatarColorHex,Ui(44),friend.AvatarImageUrl);
        if (ImGui.IsItemHovered()&&ImGui.IsMouseClicked(ImGuiMouseButton.Left)) OpenProfilePage(session,friend.AccountId,friend.DisplayName);
        var room=activeRoom && CanJoinFriendWatchParty(friend) && partyDirectoryAccountId==session.AccountId && roomsClient.LastFailure is null
            ? partyDirectoryRooms.FirstOrDefault(r=>r.HostAccountId==friend.AccountId):null;
        var textWidth=width-Ui(activeRoom?244:compactActions?160:220);
        var nameOrigin=origin+UiVec(68,15);
        ImGui.SetCursorScreenPos(nameOrigin);
        DrawUsername(friend.AccountId,FitPartyTextToWidth(friend.DisplayName,MathF.Max(Ui(40),textWidth-Ui(24))));
        if (ImGui.IsItemHovered()&&ImGui.IsMouseClicked(ImGuiMouseButton.Left)) OpenProfilePage(session,friend.AccountId,friend.DisplayName);
        if (PatreonBadgeTier(friend.AccountId)>0) { ImGui.SameLine(0,Ui(4)); DrawAlphaChatSupporterHeart(); }
        var status=FriendHasParty(friend)?FriendWatchPartyStatus.DisplayLabel(friend.WatchingLabel??"Hosting a Watch Party"):friend.Online?"Online":"Offline";
        using (UiFonts.PushRelative(.90f))
        {
            var statusOrigin=origin+UiVec(68,37);
            draw.AddCircleFilled(statusOrigin+new Vector2(Ui(4),ImGui.GetFontSize()*.5f),Ui(3),ImGui.GetColorU32(friend.Online?Good:MutedText));
            ImGui.SetCursorScreenPos(statusOrigin+UiVec(12,0));
            ImGui.TextColored(friend.Online?Good:MutedText,FitPartyTextToWidth(status,MathF.Max(Ui(50),textWidth)));
            if (!activeRoom && friend.StatusMessage is { Length: >0 } custom)
            {
                ImGui.SameLine(0,Ui(8));
                var customWidth=MathF.Max(Ui(20),origin.X+width-Ui(compactActions?110:170)-ImGui.GetCursorScreenPos().X);
                ImGui.TextColored(MutedText,FitPartyTextToWidth($"· {custom}",customWidth));
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip(custom);
            }
        }
        if (activeRoom)
        {
            var infoWidth=room is null?width-Ui(28):width-Ui(176);
            if (room is not null)
            {
                // Directory fields and thumbnail cache already contain the preview; no per-friend metadata lookup.
                var media=GetPartyDirectoryMedia(room);
                DrawPartyDirectoryThumbnail(draw,room,media,origin+new Vector2(width-Ui(154),Ui(16)),UiVec(140,79));
            }
            ImGui.SetCursorScreenPos(origin+UiVec(14,68));
            using var uiFontScale18486=UiFonts.PushRelative(1.35f);
            ImGui.TextUnformatted(FitPartyTextToWidth(room is not null?$"{room.HostDisplayName}’s Watch Party":CanJoinFriendWatchParty(friend)?$"{friend.DisplayName}’s Watch Party":"Watching together",MathF.Max(Ui(40),infoWidth)));
            uiFontScale18486.Dispose();
            ImGui.SetCursorScreenPos(origin+UiVec(14,96));
            var description=room?.Description;
            if (description is { } encodedDescription && encodedDescription.IndexOf("#>",StringComparison.Ordinal) is var markerEnd && markerEnd>=0)
                description=encodedDescription[(markerEnd+2)..].Trim();
            if (string.IsNullOrWhiteSpace(description)) description=room is not null?GetPartyDirectoryMedia(room).Title??"Waiting for content":
                CanJoinFriendWatchParty(friend)?"Come watch together.":"This friend is watching a Watch Party.";
            ImGui.TextColored(MutedText,FitPartyTextToWidth(description, width-Ui(28)));
            ImGui.SetCursorScreenPos(origin+UiVec(14,120));
            if (room is not null)
            {
                var categoryOrigin=ImGui.GetCursorScreenPos();
                var categoryWidth=DrawWatchPartyTag(draw,categoryOrigin,WatchPartyCategoryIcon(room),WatchPartyCategoryText(room),WatchPartyCategoryColor(room));
                ImGui.SetCursorScreenPos(categoryOrigin+new Vector2(categoryWidth+Ui(12),Ui(4)));
                ImGui.TextColored(MutedText,$"{room.ViewerCount+1} watching");
            }
            else ImGui.TextColored(MutedText,CanJoinFriendWatchParty(friend)?"Open the room to see more details.":"Room details are private or unavailable.");
        }
        var actionsY=origin.Y+(activeRoom?height-Ui(48):(height-Ui(34))*.5f);
        var actionsX=activeRoom?origin.X+Ui(14):origin.X+width-Ui(compactActions?92:156);
        ImGui.SetCursorScreenPos(new Vector2(actionsX,actionsY));
        var unread=FriendUnreadMessageCount(friend.AccountId);
        var messageLabel=unread>0?$"Message · {unread}":"Message";
        if (activeRoom)
        {
            if (AlphaChatButton("friendMessage",messageLabel,FontAwesomeIcon.Comments,Ui(100),Ui(34))) StartOrOpenConversation(session,friend.AccountId,friend.DisplayName);
            ImGui.SameLine(0,Ui(8));
            if (CanJoinFriendWatchParty(friend))
            {
                if (AlphaChatButton("friendViewRoom","View room",FontAwesomeIcon.Eye,Ui(105),Ui(34))) ViewFriendRoom(friend);
                ImGui.SameLine(0,Ui(8));
                using (UiDisabled.Scope(stream.WatchPartyJoinBlockReason is not null))
                    if (AlphaChatButton("friendJoinRoom","Join room",FontAwesomeIcon.Users,Ui(112),Ui(34),primary:true)) OpenPlayerAndJoin(friend.DisplayName);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)&&stream.WatchPartyJoinBlockReason is { } blocked) UiFonts.SetTooltip(blocked);
                ImGui.SameLine(0,Ui(8));
            }
        }
        else
        {
            if (compactActions)
            {
                if (AlphaChatButton("compactFriendMessage","",FontAwesomeIcon.Comments,Ui(36),Ui(34),primary:true)) StartOrOpenConversation(session,friend.AccountId,friend.DisplayName);
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip(messageLabel);
                if (unread>0) DrawUnreadMessageBadge(ImGui.GetItemRectMax()-UiVec(0,30),unread);
            }
            else if (FriendsPrimaryButton(messageLabel,Ui(100),34)) StartOrOpenConversation(session,friend.AccountId,friend.DisplayName);
            ImGui.SameLine(0,Ui(8));
        }
        if (AlphaChatButton("friendMore","",FontAwesomeIcon.EllipsisH,Ui(34),Ui(34))) ImGui.OpenPopup("##modernFriendActions");
        using var menuStyle=ImRaii.PushStyle(ImGuiStyleVar.PopupBorderSize,Ui(1)).Push(ImGuiStyleVar.PopupRounding,Ui(10)).Push(ImGuiStyleVar.WindowPadding,UiVec(16,14));
        using var menuColors=ImRaii.PushColor(ImGuiCol.PopupBg,CardBg).Push(ImGuiCol.Border,AccentInk(.4f));
        using var popup=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Popup("##modernFriendActions"));
        if (popup)
        {
            if (ImGui.MenuItem("View profile")) OpenProfilePage(session,friend.AccountId,friend.DisplayName);
            using (UiDisabled.Scope(!CanInviteToCurrentWatchParty()))
                if (ImGui.MenuItem("Invite to Watch Party")) StartOrOpenConversation(session,friend.AccountId,friend.DisplayName,CreateWatchPartyInviteMessage());
            if (CanJoinFriendWatchParty(friend))
            {
                if (ImGui.MenuItem("View room")) ViewFriendRoom(friend);
                using (UiDisabled.Scope(stream.WatchPartyJoinBlockReason is not null))
                    if (ImGui.MenuItem("Join room")) OpenPlayerAndJoin(friend.DisplayName);
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Remove friend")) QueueFriendRemovalConfirmation(friend.AccountId,friend.DisplayName);
            using (ImRaii.PushColor(ImGuiCol.Text,Danger))
                if (ImGui.MenuItem("Block user")) QueueFriendBlockConfirmation(friend.AccountId,friend.DisplayName);
        }
    }

    private void ViewFriendRoom(FriendDto friend)
    {
        if (!AllowInlineProfileNavigation()) return;
        currentPage=HomePage.PartyDirectory; ResetPartyDirectoryFilters();
        partyDirectorySearch=friend.DisplayName; partyDirectoryFocusSearchRequested=true;
        RefreshPartyDirectoryIfNeeded();
    }

    private void DrawModernFriendRequests(CharacterSession session)
    {
        if (friendRequests.Incoming.Length>0)
        {
            FriendsSectionTitle($"Friend requests · {friendRequests.Incoming.Length}");
            foreach (var request in friendRequests.Incoming)
            {
                using var id=ImRaii.PushId(request.Id);
                var width=ImGui.GetContentRegionAvail().X;
                using var child=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##modernFriendRequest",new Vector2(width,Ui(70)),true,ImGuiWindowFlags.NoScrollbar|ImGuiWindowFlags.NoScrollWithMouse));
                if (!child) continue;
                var origin=ImGui.GetWindowPos();
                EnsurePartyAvatarLoaded(request.OtherAccountId,request.OtherDisplayName);
                partyAvatarCache.TryGetValue(request.OtherAccountId,out var avatar);
                DrawAvatarAt(origin+UiVec(14,14),avatar?.AvatarIcon,avatar?.AvatarColorHex,Ui(40),avatar?.AvatarImageUrl);
                ImGui.SetCursorScreenPos(origin+UiVec(66,15));
                DrawUsername(request.OtherAccountId,FitPartyTextToWidth(request.OtherDisplayName,width-Ui(290)));
                ImGui.SetCursorScreenPos(origin+UiVec(66,38)); ImGui.TextColored(MutedText,"New friend request");
                ImGui.SetCursorScreenPos(origin+new Vector2(width-Ui(208),Ui(18)));
                if (FriendsPrimaryButton("Accept",Ui(92),34)) _=RespondModernFriendRequest(session,request.Id,true);
                ImGui.SameLine();
                if (AlphaChatButton("declineRequest","Decline",FontAwesomeIcon.Times,Ui(92),Ui(34))) _=RespondModernFriendRequest(session,request.Id,false);
            }
        }
        if (friendRequests.Outgoing.Length>0)
        {
            FriendsSectionTitle($"Requests sent · {friendRequests.Outgoing.Length}");
            foreach (var request in friendRequests.Outgoing)
                ImGui.TextColored(MutedText,$"{request.OtherDisplayName} · Waiting for them to accept");
        }
    }

    private async Task RespondModernFriendRequest(CharacterSession session,string requestId,bool accept)
    {
        try
        {
            if (accept) await friendsClient.AcceptRequestAsync(session.Token,requestId);
            else await friendsClient.DeclineRequestAsync(session.Token,requestId);
            await Plugin.Framework.RunOnFrameworkThread(()=>{ if (ReferenceEquals(CurrentSession,session)) friendsDirty=true; });
        }
        catch (Exception) { AepLog.Warning("[Friends] Request response failed."); }
    }

    private void DrawBlockedAccountsSettings()
    {
        SettingsSection("Blocked accounts","Manage the people you have blocked across Alpha Channel.");
        if (CurrentSession is not { } session) { ImGui.TextDisabled("Sign in to manage blocked accounts."); return; }
        if (friendsDirty && !friendsLoading) RefreshFriends(session.Token);
        if (friendsLoadError is { Length: >0 } error) { ImGui.TextWrapped(error); if (ImGui.Button("Retry##blockedAccounts")) friendsDirty=true; }
        if (friendsLoading) ImGui.TextDisabled("Loading…");
        if (blockedAccounts.Length==0) { ImGui.TextColored(MutedText,"You haven’t blocked anyone."); return; }
        ImGui.TextColored(MutedText,"Unblocking does not restore a friendship automatically.");
        foreach (var blocked in blockedAccounts)
        {
            using var id=ImRaii.PushId(blocked.Id);
            DrawUsername(blocked.Id,blocked.DisplayName,Vector4.One); ImGui.SameLine();
            if (AlphaChatButton("unblockAccount","Unblock",FontAwesomeIcon.UserCheck,Ui(110),Ui(32))) UnblockAccount(session,blocked.Id);
        }
    }
}

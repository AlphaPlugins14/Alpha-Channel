using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawDmChatToolbar(CharacterSession session,string conversationId)
    {
        if (chatsHubDrawing) { DrawHubConversationToolbar(session, conversationId); return; }
        var width=ImGui.GetContentRegionAvail().X;
        var narrow = width < Ui(520);
        using var child=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##dmChatToolbar",new Vector2(width,Ui((narrow?108:62)+(GroupChatClient.IsGroup(conversationId)?42:0))),true,
            ImGuiWindowFlags.AlwaysUseWindowPadding|ImGuiWindowFlags.NoScrollbar|ImGuiWindowFlags.NoScrollWithMouse));
        if (!child) return;
        var origin=ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddCircleFilled(origin+UiVec(10,18),Ui(7),ImGui.GetColorU32(messageEncryptionReady==true?Good:Gold));
        var contentWidth=ImGui.GetContentRegionAvail().X;
        var peopleWidth=Ui(openConversationIsGroup?128:132);
        var identityDesired=Ui(44)+ImGui.CalcTextSize(openConversationTitle).X+
            (!openConversationIsGroup&&PatreonBadgeTier(openConversationMembers.FirstOrDefault()?.AccountId??"")>0?Ui(24):0);
        var identityWidth=MathF.Min(MathF.Max(Ui(80),identityDesired),
            MathF.Min(Ui(220),MathF.Max(Ui(80),contentWidth-Ui(48)-peopleWidth-(narrow?0:Ui(224)))));
        var identityOrigin = origin + UiVec(32,0);
        ImGui.SetCursorScreenPos(identityOrigin);
        if (openConversationIsGroup)
        {
            if (AlphaChatButton("dmToolbarGroup", "", FontAwesomeIcon.Users, Ui(36), Ui(36))) dmRosterOpen=true;
        }
        else if (openConversationMembers.FirstOrDefault() is { } person)
        {
            EnsurePartyAvatarLoaded(person.AccountId, person.DisplayName);
            partyAvatarCache.TryGetValue(person.AccountId, out var avatar);
            DrawAvatarChip(avatar?.AvatarIcon, avatar?.AvatarColorHex, Ui(36), avatar?.AvatarImageUrl);
            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                OpenProfilePage(session, person.AccountId, person.DisplayName);
        }
        ImGui.SetCursorScreenPos(identityOrigin + new Vector2(Ui(44), (Ui(36)-ImGui.GetFontSize())*.5f));
        var supporter = !openConversationIsGroup && openConversationMembers.FirstOrDefault() is { } namedMember && PatreonBadgeTier(namedMember.AccountId)>0;
        var visibleTitle = FitPartyTextToWidth(openConversationTitle, MathF.Max(Ui(20),identityWidth-Ui(44)-(supporter?Ui(24):0)));
        if (openConversationIsGroup) ImGui.TextUnformatted(visibleTitle);
        else DrawUsername(openConversationMembers.FirstOrDefault()?.AccountId,visibleTitle);
        if (visibleTitle != openConversationTitle && ImGui.IsItemHovered()) UiFonts.SetTooltip(openConversationTitle);
        if (supporter) { ImGui.SameLine(0,Ui(4)); DrawAlphaChatSupporterHeart(); }
        ImGui.SetCursorScreenPos(identityOrigin + new Vector2(identityWidth+Ui(8),0));
        if (AlphaChatButton("dmPeople",openConversationIsGroup?$"{openConversationMembers.Length+1} members":"View profile",
            openConversationIsGroup?FontAwesomeIcon.Users:FontAwesomeIcon.User,peopleWidth))
        {
            if (openConversationIsGroup) dmRosterOpen=true;
            else if (openConversationMembers.FirstOrDefault() is { } member) OpenProfilePage(session,member.AccountId,member.DisplayName);
        }
        if (narrow) ImGui.SetCursorScreenPos(origin+UiVec(0,46));
        else ImGui.SetCursorScreenPos(origin+new Vector2(contentWidth-Ui(224),0));
        using (UiDisabled.Scope(!CanInviteToCurrentWatchParty()||messageEncryptionReady!=true||IsOpenDmBlocked(session)))
            if (AlphaChatButton("dmShare","Share Watch Party",FontAwesomeIcon.BroadcastTower,Ui(180)))
                SendEncryptedDm(session,conversationId,CreateWatchPartyInviteMessage(),showInFeed:true);
        ImGui.SameLine();
        var unreadable=openMessages.Count(m=>decryptedCache.GetValueOrDefault(m.Id)==DecryptFailedMarker);
        if (AlphaChatButton("dmSecurity","",messageEncryptionReady==true?FontAwesomeIcon.Lock:FontAwesomeIcon.LockOpen,Ui(36),
            iconColor:messageEncryptionReady==true?Good:Gold)) ImGui.OpenPopup("##dmSecurityDetails");
        var securityButtonMax=ImGui.GetItemRectMax();
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip(unreadable>0?
            $"{unreadable} saved messages could not be decrypted. Open the padlock for recovery options.":"Private chat encryption and recovery");
        ImGui.SetNextWindowSize(UiVec(430,0),ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(securityButtonMax+UiVec(0,8),ImGuiCond.Always,new Vector2(1,0));
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,UiVec(16,14)).Push(ImGuiStyleVar.PopupBorderSize,Ui(1)).Push(ImGuiStyleVar.PopupRounding,Ui(10)))
        using (ImRaii.PushColor(ImGuiCol.PopupBg,CardBg).Push(ImGuiCol.Border,new Vector4(AccentHover.X,AccentHover.Y,AccentHover.Z,.3f)))
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup("##dmSecurityDetails")))
        {
            DrawSocialHeading("Private chat encryption");
            DrawSocialHelperWrapped(GroupChatClient.IsGroup(conversationId) ? "This group uses a shared end-to-end encryption key. Cloudflare controls delivery and history access; it cannot decrypt messages. Your local key vault requires an authenticated connection to unlock." : "Direct messages use your account's end-to-end encryption keys.");
            if (GroupChatClient.IsGroup(conversationId) && dmClient.Groups.VerificationCode is { } verification)
                ImGui.TextUnformatted("Installation verification code: " + verification);
            DrawModernDmSecurityStatus(session,conversationId);
            ImGui.EndPopup();
        }
        if (GroupChatClient.IsGroup(conversationId)) {
            ImGui.SetCursorScreenPos(origin + UiVec(0, narrow ? 88 : 44));
            DrawCloudGroupControls(session, conversationId);
        }
    }

    private void DrawDmSenderAvatar(CharacterSession session,string id,string name,Vector2 origin)
    {
        if (id==session.AccountId) DrawAvatarAt(origin,session.AvatarIcon,session.AvatarColorHex,Ui(40),session.AvatarImageUrl);
        else
        {
            EnsurePartyAvatarLoaded(id,name);
            partyAvatarCache.TryGetValue(id,out var avatar);
            DrawAvatarAt(origin,avatar?.AvatarIcon,avatar?.AvatarColorHex,Ui(40),avatar?.AvatarImageUrl);
        }
    }

    private void DrawDmPersonActions(CharacterSession session,string id,string name,bool includeBlock=true)
    {
        var own=id==session.AccountId;
        var friend=friendsAccountId==session.AccountId&&friends.Any(f=>f.AccountId==id);
        if (DrawAlphaChatMenuAction("View profile",!AlphaChatBlocked(id))) OpenProfilePage(session,id,name);
        if (DrawAlphaChatMenuAction("Add friend",!own&&!friend&&!AlphaChatBlocked(id))) _=SendPartyFriendRequestAsync(session,id,name);
        if (DrawAlphaChatMenuAction("Message",!own&&friend&&openConversationIsGroup)) StartOrOpenConversation(session,id,name);
        RefreshPartyDirectoryIfNeeded();
        var room=!partyDirectoryLoading&&partyDirectoryAccountId==session.AccountId&&roomsClient.LastFailure is null?
            partyDirectoryRooms.FirstOrDefault(r=>r.HostAccountId==id):null;
        if (DrawAlphaChatMenuAction("Join Watch Party",!own&&room is not null&&!AlphaChatBlocked(id)&&stream.WatchPartyJoinBlockReason is null)
            &&room is not null) JoinPartyDirectoryRoom(room);
        using (ImRaii.PushColor(ImGuiCol.Text,Danger))
            if (includeBlock&&!own&&ImGui.MenuItem(AlphaChatBlocked(id)?"Unblock user":"Block user"))
            { if (AlphaChatBlocked(id)) UnblockAccount(session,id); else QueueFriendBlockConfirmation(id,name); }
    }

    private AlphaChatUser[] GetDmRosterUsers()
    {
        if (CurrentSession is not { } session) return [];
        var members=openConversationMembers.Select(m=>(Id:m.AccountId,Name:m.DisplayName)).Prepend((Id:session.AccountId,Name:session.DisplayName));
        return members.DistinctBy(m=>m.Id).Select(m=>
        {
            if (m.Id==session.AccountId) return new AlphaChatUser(m.Id,m.Name,session.AvatarIcon,session.AvatarColorHex??"#9966FA",session.AvatarImageUrl,PatreonBadgeTier(m.Id));
            EnsurePartyAvatarLoaded(m.Id,m.Name); partyAvatarCache.TryGetValue(m.Id,out var avatar);
            return new AlphaChatUser(m.Id,m.Name,avatar?.AvatarIcon,avatar?.AvatarColorHex??"#9966FA",avatar?.AvatarImageUrl,PatreonBadgeTier(m.Id));
        }).ToArray();
    }
}

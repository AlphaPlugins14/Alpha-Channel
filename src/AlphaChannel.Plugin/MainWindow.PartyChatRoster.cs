using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool IsPartyChatHost(string? id) => !string.IsNullOrWhiteSpace(id) &&
        id == (stream.Mode == StreamMode.Hosting ? CurrentSession?.AccountId : stream.HostId);

    private void DrawPartyChatHostTag()
    {
        var origin=ImGui.GetCursorScreenPos();
        var size=ImGui.CalcTextSize("Host")+UiVec(12,2);
        var draw=ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin,origin+size,ImGui.GetColorU32(new Vector4(Accent.X,Accent.Y,Accent.Z,0.22f)),Ui(5));
        draw.AddRect(origin,origin+size,ImGui.GetColorU32(new Vector4(Accent.X,Accent.Y,Accent.Z,0.65f)),Ui(5));
        draw.AddText(origin+UiVec(6,1),ImGui.GetColorU32(Vector4.One),"Host");
        ImGui.Dummy(size);
    }

    private AlphaChatUser[] GetPartyChatRosterUsers()
    {
        if (CurrentSession is not { } session) return [];
        var hostId=stream.Mode==StreamMode.Hosting?session.AccountId:stream.HostId;
        var roster = stream.Roster;
        var receivedHostName = roster.FirstOrDefault(member =>
            string.Equals(member.UserId, hostId, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(member.DisplayName))?.DisplayName;
        var hostName = stream.Mode == StreamMode.Hosting ? session.DisplayName :
            receivedHostName ?? (string.IsNullOrWhiteSpace(joinedHostDisplayName) ? "Host" : joinedHostDisplayName);
        var members=roster.Select(m=>(Id:m.UserId,Name:m.DisplayName)).ToList();
        if (!string.IsNullOrWhiteSpace(hostId)) members.Insert(0,(hostId,hostName));
        return members.DistinctBy(m=>m.Id, StringComparer.OrdinalIgnoreCase).Select(m=>
        {
            if (m.Id==session.AccountId)
                return new AlphaChatUser(m.Id,m.Name,session.AvatarIcon,session.AvatarColorHex ?? "#9966FA",session.AvatarImageUrl,PatreonBadgeTier(m.Id));
            EnsurePartyAvatarLoaded(m.Id,m.Name);
            partyAvatarCache.TryGetValue(m.Id,out var avatar);
            return new AlphaChatUser(m.Id,m.Name,avatar?.AvatarIcon,avatar?.AvatarColorHex ?? "#9966FA",avatar?.AvatarImageUrl,PatreonBadgeTier(m.Id));
        }).ToArray();
    }

    private void DrawPartyChatRosterActions(AlphaChatUser user)
    {
        if (CurrentSession is not { } session) return;
        var own=user.AccountId==session.AccountId;
        var friend=friends.Any(f=>f.AccountId==user.AccountId);
        DrawUsername(user.AccountId,user.DisplayName,AccentHover);
        ImGui.Separator();
        if (DrawAlphaChatMenuAction("View Profile",!AlphaChatBlocked(user.AccountId))) OpenProfilePage(session,user.AccountId,user.DisplayName);
        if (DrawAlphaChatMenuAction("Add Friend",!own&&!friend&&!AlphaChatBlocked(user.AccountId))) _=SendPartyFriendRequestAsync(session,user.AccountId,user.DisplayName);
        if (DrawAlphaChatMenuAction("Message",!own&&friend)) StartOrOpenConversation(session,user.AccountId,user.DisplayName);
        if (!own && stream.Mode == StreamMode.Hosting && stream.Roster.Any(p => p.UserId == user.AccountId))
        {
            using (UiDisabled.Scope(partyModerationBusy))
            {
                if (ImGui.MenuItem("Kick from party")) ModeratePartyMember(user.AccountId, user.DisplayName, "kick");
                if (ImGui.MenuItem("Ban from party")) ModeratePartyMember(user.AccountId, user.DisplayName, "ban");
            }
        }
        using (ImRaii.PushColor(ImGuiCol.Text,Danger))
            if (!own && ImGui.MenuItem(AlphaChatBlocked(user.AccountId)?"Unblock User":"Block User"))
            { if (AlphaChatBlocked(user.AccountId)) UnblockAccount(session,user.AccountId); else QueueFriendBlockConfirmation(user.AccountId,user.DisplayName); }
    }
}

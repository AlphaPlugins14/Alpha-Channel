using AlphaChannel.Plugin.Auth;
using AlphaChannel.Contracts;
using Dalamud.Interface;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string? pendingWatchPartyInviteId;
    private string? pendingWatchPartyInviteHostId;
    private DateTime pendingWatchPartyInviteStartedUtc;
    private WatchPartyInviteJoinAttempt? pendingWatchPartyInviteAttempt;

    private static bool WatchPartyInviteExpired(long sentAtUnixSeconds) =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() - sentAtUnixSeconds >= 30 * 60;

    private bool CanInviteToCurrentWatchParty() =>
        CurrentSession is not null && stream.IsConnected &&
        stream.CurrentRoomState is not null &&
        (stream.Mode == StreamMode.Hosting ||
         stream.Mode == StreamMode.Viewing && stream.HostId is { Length: > 0 });

    private string CreateWatchPartyInviteMessage()
    {
        var hostId = stream.Mode == StreamMode.Hosting
            ? CurrentSession?.AccountId
            : stream.HostId;
        var hostName = stream.Mode == StreamMode.Hosting
            ? CurrentDisplayName ?? CurrentSession?.DisplayName
            : joinedHostDisplayName;
        var state=stream.CurrentRoomState;
        var snapshot=hostId is null || state is null ? null : new RoomDirectoryDto(hostId,
            string.IsNullOrWhiteSpace(hostName)?"Watch Party host":hostName,stream.RoomDescription,stream.RoomLocation,
            stream.RoomKind,stream.Roster.Length,state.Paused??true,!string.IsNullOrWhiteSpace(state.Url),
            null,stream.RoomKind==RoomKind.Locked?null:state.MediaTitle,stream.RoomKind==RoomKind.Locked?null:state.MediaThumbnailUrl);
        return EncodeRichDm(new RichDmContent("watch_party_invite",HostAccountId:hostId,
            HostDisplayName:string.IsNullOrWhiteSpace(hostName)?"Watch Party host":hostName,Room:snapshot));
    }

    private void DrawWatchPartyInviteCard(CharacterSession session,MessageDto message,RichDmContent invite)
    {
        var expired=WatchPartyInviteExpired(message.SentAtUnix);
        var mine=message.SenderAccountId==session.AccountId;
        var joining=pendingWatchPartyInviteId==message.GroupId && DateTime.UtcNow-pendingWatchPartyInviteStartedUtc<TimeSpan.FromSeconds(20);
        var unavailable=Plugin.Cfg.ExpiredWatchPartyInviteIds?.Contains(message.GroupId)==true;
        if (expired && invite.Room is null) {
            var origin=ImGui.GetCursorScreenPos(); var width=ImGui.GetContentRegionAvail().X; var draw=ImGui.GetWindowDrawList();
            draw.AddRectFilled(origin,origin+new Vector2(width,Ui(72)),ImGui.GetColorU32(FrameBg),Ui(9));
            draw.AddRect(origin,origin+new Vector2(width,Ui(72)),ImGui.GetColorU32(AccentInk(.25f)),Ui(9));
            UiFonts.DrawCenteredIcon(draw,Ui(24),origin+UiVec(30,36),ImGui.GetColorU32(MutedText),FontAwesomeIcon.Clock.ToIconString());
            draw.AddText(origin+UiVec(56,15),ImGui.GetColorU32(Vector4.One),FitPartyTextToWidth((invite.HostDisplayName??"Host")+"'s Watch Party",width-Ui(150)));
            using (UiFonts.PushRelative(.85f)) draw.AddText(origin+UiVec(56,41),ImGui.GetColorU32(MutedText),"This Watch Party invitation has expired.");
            ImGui.Dummy(new Vector2(width,Ui(80)));
            return;
        }
        var room=invite.Room ?? new RoomDirectoryDto(invite.HostAccountId??"",invite.HostDisplayName??"Watch Party host",
            "Shared Watch Party invitation",null,RoomKind.Public,0,true,false,null,"Content not provided",null);
        // Invites remain snapshots. Joining always uses the existing checked invite flow.
        DrawSharedChatRoomCard(room,ImGui.GetContentRegionAvail().X,"Room information unavailable",
            expired||joining||mine,expired?"Expired":joining?"Joining…":mine?"Invitation sent":unavailable?"Retry Join":"Join Watch Party",
            ()=>JoinFromWatchPartyInvite(message.GroupId,invite));
    }

    private void JoinFromWatchPartyInvite(string inviteId, RichDmContent invite)
    {
        if (invite.HostAccountId is not { Length: > 0 } hostId)
            return;
        if(!EnsureWatchPartyJoinAllowed()) { messagesError=joinError; return; }
        if (stream.Mode is StreamMode.Hosting or StreamMode.Viewing)
        {
            messagesError = string.Equals(stream.HostId ?? CurrentSession?.AccountId,
                hostId, StringComparison.OrdinalIgnoreCase)
                ? "You're already in this Watch Party."
                : "Leave your current Watch Party before joining this one.";
            return;
        }

        messagesError = null;
        if (Plugin.Cfg.ExpiredWatchPartyInviteIds?.Remove(inviteId) == true) Plugin.Cfg.Save();
        var hostName = friends.FirstOrDefault(friend =>
            string.Equals(friend.AccountId, hostId, StringComparison.OrdinalIgnoreCase))?.DisplayName
            ?? invite.HostDisplayName;
        DoJoin(hostId, visibleHostName: hostName, inviteMessageId: inviteId);
    }

    private void HandleWatchPartyInviteJoined()
    {
        var inviteId = Interlocked.Exchange(ref pendingWatchPartyInviteId, null);
        pendingWatchPartyInviteHostId = null;
        pendingWatchPartyInviteAttempt = null;
        if (inviteId is null) return;
        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            messagesError = null;
            currentPage = HomePage.WatchAlong;
            partyPanelTab = PartyPanelTab.Chat;
            OpenUi();
        });
    }

    private void HandleWatchPartyInviteDeclined(string? reason)
    {
        var attempt = pendingWatchPartyInviteAttempt;
        var session = CurrentSession;
        if (pendingWatchPartyInviteId is { } pendingId &&
            attempt?.TakeDisplayNameFallback(reason) is { } fallbackName)
        {
            _ = Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0 || pendingWatchPartyInviteId != pendingId ||
                    !ReferenceEquals(pendingWatchPartyInviteAttempt, attempt) ||
                    session is null || !ReferenceEquals(CurrentSession, session) ||
                    stream.Mode != StreamMode.None) return;
                messagesError = null;
                joinError = null;
                pendingWatchPartyInviteHostId = fallbackName;
                pendingWatchPartyInviteStartedUtc = DateTime.UtcNow;
                
                _ = stream.JoinAsync(fallbackName);
            });
            return;
        }
        var inviteId = Interlocked.Exchange(ref pendingWatchPartyInviteId, null);
        var hostId = pendingWatchPartyInviteHostId;
        pendingWatchPartyInviteHostId = null;
        pendingWatchPartyInviteAttempt = null;
        if (inviteId is null) return;
        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            var roomGone = reason is "Host not found." or "Host is not streaming.";
            if (roomGone)
            {
                messagesError = "This Watch Party is not available right now. Check that the host is still hosting, then try again.";
            }
            else if (reason is "Wrong password." or "This room is locked.")
            {
                messagesError = "This room needs a password. Enter it in the Watch Party join form.";
                joinHostNameInput = hostId ?? string.Empty;
                joinPasswordInput = string.Empty;
                watchPartyJoinOverlayRequested = true;
                currentPage = HomePage.WatchAlong;
                OpenUi();
            }
            else
                messagesError = string.IsNullOrWhiteSpace(reason)
                    ? "Could not join this Watch Party. Try again."
                    : $"Could not join this Watch Party: {reason}";
        });
    }
}

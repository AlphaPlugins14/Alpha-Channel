using System.Text;
using System.Text.Json;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private ArcadeRelayClient? bingoRelay;
    private string? bingoRelayHost, bingoRelayAccount, bingoRelayToken, bingoRelaySession, bingoRelayRoster;
    private DateTime bingoNextInvitationUtc;

    private void DisposeBingoRelay()
    {
        bingoRelay?.Dispose(); bingoRelay = null;
        bingoRelayHost = bingoRelayAccount = bingoRelayToken = bingoRelaySession = bingoRelayRoster = null;
        bingoNextInvitationUtc = DateTime.MinValue;
    }
    private void UpdateBingoRelay()
    {
        if (bingoRelay is not { } relay) return;
        if ((relay.IsHost ? stream.Mode != StreamMode.Hosting : stream.Mode != StreamMode.Viewing) ||
            bingoRelayHost != stream.HostId || bingoRelayAccount != CurrentSession?.AccountId || (!relay.IsHost && bingoRelayToken != CurrentSession?.Token) ||
            (!relay.IsHost && ActivityFromCanvasUrl(stream.CurrentRoomState?.Url) != ActiveActivityKind.Bingo))
        { DisposeBingoRelay(); return; }
        if (relay.IsHost && relay.Completion.IsCompleted) { StopBingoGame(); return; }
        while (relay.Incoming.TryDequeue(out var command))
        {
            ApplyBingoCommand(command);
            if (!ReferenceEquals(bingoRelay, relay)) return;
        }
        if (!relay.IsHost) return;
        UpdateBingoRelayMembers();
        while (relay.IncomingActions.TryDequeue(out var action))
        {
            var accepted = HandleBingoContribution(action.UserId, action.Command);
            relay.AcknowledgeAction(action.ActionId, accepted);
        }
        if (relay.Connected && DateTime.UtcNow >= bingoNextInvitationUtc) SendBingoRelayInvitation();
    }
    private void UpdateBingoRelayMembers()
    {
        var members = stream.Roster.Select(p => p.UserId).ToArray();
        bingoRelay?.SetMembers(members);
        var signature = string.Join(';', members.Order(StringComparer.Ordinal));
        if (signature != bingoRelayRoster) { bingoRelayRoster = signature; bingoNextInvitationUtc = DateTime.MinValue; }
    }
    private bool TrySendBingoThroughCloudflare(string command)
    {
        if (stream.Mode != StreamMode.Hosting || bingoSessionId is null || bingoWhiteboardSessionId is null) return false;
        if (bingoRelay is not { IsHost: true } || bingoRelaySession != bingoSessionId || bingoRelayHost != stream.HostId)
        {
            DisposeBingoRelay();
            bingoRelayHost = stream.HostId; bingoRelayAccount = CurrentSession?.AccountId; bingoRelayToken = CurrentSession?.Token; bingoRelaySession = bingoSessionId;
            var session = bingoSessionId;
            bingoRelay = new(ArcadeRelayClient.DefaultUrl, "bingo", session, bingoWhiteboardSessionId,
                value => BingoRelayRules.Revision(value, session), validateAction: value => BingoRelayRules.ValidAction(value, session));
        }
        UpdateBingoRelayMembers();
        bingoRelay.Publish(command);
        if (bingoRelay.Connected && DateTime.UtcNow >= bingoNextInvitationUtc) SendBingoRelayInvitation();
        return true;
    }
    private void SendBingoRelayInvitation()
    {
        if (bingoRelay is not { IsHost: true, Connected: true, Room: { } room }) return;
        var invite = new LuckyWheelRelayInvitation(ArcadeRelayClient.DefaultUrl, room.RoomId, room.ViewerKey,
            room.SessionId, room.CanvasId, room.ExpiresUnixMs);
        _ = stream.SendBingoCommandAsync("R|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(invite))));
        bingoNextInvitationUtc = DateTime.UtcNow.AddSeconds(15);
    }
    private void ReceiveBingoRelayInvitation(string command)
    {
        if (stream.Mode != StreamMode.Viewing || command.Length > 2048 || CurrentSession is not { } account ||
            ActivityFromCanvasUrl(stream.CurrentRoomState?.Url) != ActiveActivityKind.Bingo) return;
        try
        {
            var invite = JsonSerializer.Deserialize<LuckyWheelRelayInvitation>(Convert.FromBase64String(command[2..]));
            if (!LuckyWheelRelayClient.ValidInvitation(invite, ArcadeRelayClient.DefaultUrl, whiteboardSessionId ?? "",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            { AepLog.Warning("[Bingo] Relay invitation rejected."); return; }
            if (bingoRelay is { IsHost: false, Room: { } room } && room.RoomId == invite!.RoomId &&
                room.ViewerKey == invite.ViewerKey && bingoRelayHost == stream.HostId && bingoRelayToken == account.Token) return;
            DisposeBingoRelay();
            bingoRelayHost = stream.HostId; bingoRelayAccount = account.AccountId; bingoRelayToken = account.Token; bingoRelaySession = invite!.SessionId;
            var session = invite.SessionId;
            var player = account.AccountId.ToLowerInvariant();
            bingoRelay = new(invite.Url, "bingo", session, invite.CanvasId, value => BingoRelayRules.Revision(value, session, player),
                new(invite.RoomId, "bingo", "", invite.ViewerKey, session, invite.CanvasId, invite.ExpiresUnixMs),
                accountToken: account.Token, accountId: player, validateAction: value => BingoRelayRules.ValidAction(value, session));
        }
        catch { AepLog.Warning("[Bingo] Invalid relay invitation."); }
    }
}

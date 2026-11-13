using System.Text;
using System.Text.Json;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private LuckyWheelRelayClient? luckyWheelRelay;
    private string? luckyWheelRelayHost;
    private string? luckyWheelRelayAccount;
    private string? luckyWheelRelayToken;
    private string? luckyWheelRelaySession;
    private string? luckyWheelRelayRoster;
    private DateTime luckyWheelNextInvitationUtc;
    private bool LuckyWheelCanSpin => stream.Mode == StreamMode.None || luckyWheelRelay is { Connected: true };

    private void DisposeLuckyWheelRelay()
    {
        luckyWheelRelay?.Dispose();
        luckyWheelRelay = null;
        luckyWheelRelayHost = luckyWheelRelaySession = null;
        luckyWheelRelayAccount = luckyWheelRelayToken = null;
        luckyWheelRelayRoster = null;
        luckyWheelNextInvitationUtc = DateTime.MinValue;
    }

    private void UpdateLuckyWheelRelay()
    {
        var relay = luckyWheelRelay;
        if (relay is null) return;
        var matchingRole = relay.IsHost ? stream.Mode == StreamMode.Hosting : stream.Mode == StreamMode.Viewing;
        if (!matchingRole || stream.HostId != luckyWheelRelayHost || CurrentSession?.AccountId != luckyWheelRelayAccount || !relay.IsHost && CurrentSession?.Token != luckyWheelRelayToken ||
            (!relay.IsHost && ActivityFromCanvasUrl(stream.CurrentRoomState?.Url) != ActiveActivityKind.LuckyWheel))
        { DisposeLuckyWheelRelay(); return; }
        while (relay.Incoming.TryDequeue(out var command))
        {
            if (command.StartsWith("S|", StringComparison.Ordinal))
            {
                try
                {
                    var snapshot = JsonSerializer.Deserialize<LuckyWheelSnapshot>(Convert.FromBase64String(command[2..]));
                    if (snapshot?.SessionId != luckyWheelRelaySession) continue;
                }
                catch { continue; }
            }
            ApplyLuckyWheelCommand(command);
            if (!ReferenceEquals(luckyWheelRelay, relay)) return;
        }
        if (relay.IsHost)
        {
            relay.SetMembers(stream.Roster.Select(p => p.UserId));
            while (relay.IncomingActions.TryDequeue(out var action))
            {
                var accepted = stream.Roster.Any(p => p.UserId == action.UserId) &&
                    AcceptLuckyWheelSpin(action.UserId, action.Command);
                relay.AcknowledgeSpin(action.ActionId, accepted);
            }
            var roster = string.Join(';', stream.Roster.Select(member => member.UserId).Order(StringComparer.Ordinal));
            if (roster != luckyWheelRelayRoster)
            { luckyWheelRelayRoster = roster; luckyWheelNextInvitationUtc = DateTime.MinValue; }
            if (relay.Connected && DateTime.UtcNow >= luckyWheelNextInvitationUtc) SendLuckyWheelRelayInvitation();
        }
    }

    private bool TrySendLuckyWheelThroughCloudflare(string command)
    {
        if (stream.Mode != StreamMode.Hosting || luckyWheelState is not { } state)
            return false;
        if (luckyWheelRelay is not { IsHost: true } || luckyWheelRelaySession != state.SessionId || luckyWheelRelayHost != stream.HostId)
        {
            DisposeLuckyWheelRelay();
            luckyWheelRelayHost = stream.HostId;
            luckyWheelRelayAccount = CurrentSession?.AccountId;
            luckyWheelRelayToken = CurrentSession?.Token;
            luckyWheelRelaySession = state.SessionId;
            luckyWheelRelay = new(LuckyWheelRelayClient.DefaultUrl, state.SessionId, state.CanvasId,
                accountToken: CurrentSession?.Token, accountId: CurrentSession?.AccountId);
            luckyWheelRelay.SetMembers(stream.Roster.Select(p => p.UserId));
        }
        luckyWheelRelay.Publish(command);
        if (luckyWheelRelay.Connected && DateTime.UtcNow >= luckyWheelNextInvitationUtc) SendLuckyWheelRelayInvitation();
        return true;
    }

    private void SendLuckyWheelRelayInvitation()
    {
        if (luckyWheelRelay is not { IsHost: true, Connected: true, Invitation: { } invite }) return;
        _ = stream.SendLuckyWheelCommandAsync("R|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(invite))));
        luckyWheelNextInvitationUtc = DateTime.UtcNow.AddSeconds(15);
    }

    private void ReceiveLuckyWheelRelayInvitation(string command)
    {
        if (stream.Mode != StreamMode.Viewing || command.Length > 2048 ||
            ActivityFromCanvasUrl(stream.CurrentRoomState?.Url) != ActiveActivityKind.LuckyWheel) return;
        try
        {
            var invite = JsonSerializer.Deserialize<LuckyWheelRelayInvitation>(Convert.FromBase64String(command[2..]));
            if (!LuckyWheelRelayClient.ValidInvitation(invite, LuckyWheelRelayClient.DefaultUrl, whiteboardSessionId ?? "", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            { AepLog.Warning("[Lucky Wheel] Relay invitation rejected."); return; }
            if (luckyWheelRelay is { IsHost: false, Invitation: { } current } && current == invite && luckyWheelRelayHost == stream.HostId) return;
            DisposeLuckyWheelRelay();
            luckyWheelRelayHost = stream.HostId;
            luckyWheelRelayAccount = CurrentSession?.AccountId;
            luckyWheelRelayToken = CurrentSession?.Token;
            luckyWheelRelaySession = invite!.SessionId;
            luckyWheelRelay = new(invite.Url, invite.SessionId, invite.CanvasId, invite,
                accountToken: CurrentSession?.Token, accountId: CurrentSession?.AccountId);
        }
        catch { AepLog.Warning("[Lucky Wheel] Invalid relay invitation."); }
    }

}

using System.Collections.Concurrent;
using System.Text.Json;

namespace AlphaChannel.Plugin;

internal sealed record LuckyWheelRelayInvitation(string Url, string RoomId, string ViewerKey,
    string SessionId, string CanvasId, long ExpiresUnixMs, int Version = 1);

// Wheel-specific validation/UI adapter over the shared WebSocket transport.
internal sealed class LuckyWheelRelayClient : IDisposable
{
    internal const string DefaultUrl = ArcadeRelayClient.DefaultUrl;
    internal const int MaxCommandLength = 48000;
    private readonly ArcadeRelayClient relay;
    internal bool IsHost => relay.IsHost;
    internal bool Connected => relay.Connected;
    internal Task Completion => relay.Completion;
    internal string Status => relay.Status;
    internal ConcurrentQueue<string> Incoming => relay.Incoming;
    internal LuckyWheelRelayInvitation? Invitation => relay.Room is { } room
        ? new(DefaultUrl, room.RoomId, room.ViewerKey, room.SessionId, room.CanvasId, room.ExpiresUnixMs) : null;
    internal static bool ValidUrl(string? url, out string normalized) => ArcadeRelayClient.ValidUrl(url, out normalized);
    internal static bool ValidInvitation(LuckyWheelRelayInvitation? invite, string configuredUrl, string expectedCanvas, long now) =>
        invite is { Version: 1 } && ValidUrl(configuredUrl, out var trusted) && ValidUrl(invite.Url, out var advertised) &&
        trusted == advertised && ArcadeRelayClient.Hex(invite.RoomId, 32) && ArcadeRelayClient.Hex(invite.ViewerKey, 64) &&
        ArcadeRelayClient.Hex(invite.SessionId, 32) && invite.CanvasId == expectedCanvas && ArcadeRelayClient.Hex(invite.CanvasId, 32) &&
        invite.ExpiresUnixMs > now && invite.ExpiresUnixMs <= now + (long)TimeSpan.FromHours(3).TotalMilliseconds;

    internal LuckyWheelRelayClient(string url, string session, string canvas, LuckyWheelRelayInvitation? invite = null,
        HttpClient? client = null, Action<Exception>? failureObserver = null, Func<IArcadeRelaySocket>? socketFactory = null, string? accountToken = null, string? accountId = null)
    {
        if (invite is not null && (!ValidInvitation(invite, url, canvas, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) || invite.SessionId != session))
            throw new ArgumentException("Invalid wheel invitation.");
        relay = new(url, "lucky-wheel", session, canvas, command =>
        {
            if (command.Length > MaxCommandLength || !command.StartsWith("S|", StringComparison.Ordinal)) return null;
            try
            {
                var snapshot = JsonSerializer.Deserialize<LuckyWheelSnapshot>(Convert.FromBase64String(command[2..]));
                return LuckyWheelRules.ValidSnapshot(snapshot) && snapshot!.SessionId == session && snapshot.CanvasId == canvas
                    ? snapshot.Revision : null;
            }
            catch { return null; }
        }, invite is null ? null : new(invite.RoomId, "lucky-wheel", "", invite.ViewerKey, session, canvas, invite.ExpiresUnixMs),
            client, socketFactory, failureObserver, accountToken, accountId,
            accountId is null ? null : command => LuckyWheelRules.ValidSpinAction(command, session));
    }
    internal ConcurrentQueue<(string UserId, string Command, string ActionId)> IncomingActions => relay.IncomingActions;
    internal bool SubmitSpin(string command) => relay.SubmitAction(command);
    internal void SetMembers(IEnumerable<string> ids) => relay.SetMembers(ids);
    internal void AcknowledgeSpin(string id, bool accepted) => relay.AcknowledgeAction(id, accepted);
    internal void Publish(string command) => relay.Publish(command);
    public void Dispose() => relay.Dispose();
}

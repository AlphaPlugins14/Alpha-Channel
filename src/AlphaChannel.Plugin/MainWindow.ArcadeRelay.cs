using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private sealed class ActivityRelay(ArcadeRelayClient client, string? host, string? account, string? token, string session)
    {
        internal readonly ArcadeRelayClient Client = client;
        internal readonly string? Host = host, Account = account, Token = token;
        internal readonly string Session = session;
        internal string? Roster;
        internal DateTime NextInvitation;
        internal int LastEvent;
        internal string? LastState;
    }
    private readonly Dictionary<string, (string Command, string? Host, string Account, string Token, DateTime Received)> pendingActivityInvites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActivityRelay> activityRelays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> activityRelayErrors = new(StringComparer.Ordinal);
    private ArcadeRelayClient? GameRelay(string game) => activityRelays.GetValueOrDefault(game)?.Client;
    private string? GameSession(string game) => game switch { "quiz" => quizSessionId, "slot-machine" => slotMachineSessionId,
        "roulette" => rouletteSessionId, "choco-race" => chocoRaceSessionId, "poker" => pokerSessionId,
        "pictionary" => pictionarySessionId, "pictionary-teams" => teamsSession, "canvas" => whiteboardSessionId, "sky-rail" => skyRailSessionId, _ => null };
    private string? GameCanvas(string game) => game switch { "quiz" => quizWhiteboardSessionId, "slot-machine" => slotMachineWhiteboardSessionId,
        "roulette" => rouletteWhiteboardSessionId, "choco-race" => chocoRaceWhiteboardSessionId, "poker" => pokerWhiteboardSessionId,
        "pictionary" or "pictionary-teams" or "canvas" => whiteboardSessionId, "sky-rail" => skyRailWhiteboardSessionId, _ => null };
    private int GameRevision(string game) => game switch { "quiz" => quizRevision, "slot-machine" => slotMachineRevision,
        "roulette" => rouletteRevision, "choco-race" => chocoRaceRevision, "poker" => pokerRevision, _ => remainingRelayRevisions.GetValueOrDefault(game) };
    private ActiveActivityKind GameActivity(string game) => game switch { "quiz" => ActiveActivityKind.Quiz, "slot-machine" => ActiveActivityKind.SlotMachine,
        "roulette" => ActiveActivityKind.Roulette, "choco-race" => ActiveActivityKind.ChocoRaceBets, "poker" => ActiveActivityKind.Poker,
        "pictionary" => ActiveActivityKind.Pictionary, "pictionary-teams" => ActiveActivityKind.PictionaryTeams,
        "canvas" => ActiveActivityKind.Whiteboard, "sky-rail" => ActiveActivityKind.SkyRail, _ => ActiveActivityKind.None };
    private void DisposeActivityRelay(string game)
    {
        if (activityRelays.Remove(game, out var slot)) slot.Client.Dispose();
        activityRelayErrors.Remove(game);
    }
    private void DisposeActivityRelays() { pendingActivityInvites.Clear(); foreach (var game in activityRelays.Keys.ToArray()) DisposeActivityRelay(game); }
    private void StopRelayGame(string game)
    {
        switch (game) { case "quiz": StopQuiz(); break; case "slot-machine": StopSlotMachineGame(); break;
            case "roulette": StopRouletteGame(); break; case "choco-race": StopChocoRace(); break; case "poker": StopPokerTable(); break;
            case "pictionary": StopPictionaryGame(); break; case "pictionary-teams": StopPictionaryTeams(); break;
            case "sky-rail": StopSkyRailGame(); break; case "canvas": whiteboardActive = false; ResetRemainingRelay(game); break; }
        DisposeActivityRelay(game);
        if (RemainingArcadeRelayRules.Game(game))
        { whiteboardActive = false; if (screenController.Engine.IsPlayingWhiteboard) screenController.Engine.StopVideo(); }
    }
    private void ApplyRelayGame(string game, string command)
    {
        if (RemainingArcadeRelayRules.Game(game)) { ApplyRemainingState(game, command); return; }
        switch (game) { case "quiz": ApplyQuizCommand(command); break; case "slot-machine": ApplySlotMachineCommand(command); break;
            case "roulette": ApplyRouletteCommand(command); break; case "choco-race": ApplyChocoRaceCommand(command); break;
            case "poker":
                ApplyPokerCommand(command);
                using (var doc = ArcadeGameRelayRules.Decode(command))
                    if (doc?.RootElement is { } state && state.TryGetProperty("OwnHand", out var hand) && hand.GetArrayLength() == 2)
                        ApplyPokerCommand($"H|{state.GetProperty("SessionId").GetString()}|{state.GetProperty("Hand").GetInt32()}|{hand[0].GetInt32()}|{hand[1].GetInt32()}");
                break;
        }
    }
    private void HandleRelayGameAction(string game, string player, string command)
    {
        if (RemainingArcadeRelayRules.Game(game)) { HandleRemainingAction(game, player, command); return; }
        switch (game) { case "quiz": ApplyQuizContribution(player, command); break; case "slot-machine": HandleSlotContribution(player, command); break;
            case "roulette":
                if (command.StartsWith("E|", StringComparison.Ordinal))
                { var envelope = command.Split('|', 3); if (envelope.Length != 3 || envelope[1] != rouletteRoundNumber.ToString()) break; command = envelope[2]; }
                HandleRouletteContribution(player, command); break;
            case "choco-race": HandleChocoRaceContribution(player, command); break; case "poker": HandlePokerContribution(player, command); break; }
    }
    private Task SendRelayServerCommand(string game, string command) => game switch {
        "quiz" => stream.SendQuizCommandAsync(command), "slot-machine" => stream.SendSlotMachineCommandAsync(command),
        "roulette" => stream.SendRouletteCommandAsync(command), "choco-race" => stream.SendChocoRaceCommandAsync(command), "poker" => stream.SendPokerCommandAsync(command),
        "pictionary" => stream.SendPictionaryCommandAsync(command), "pictionary-teams" => stream.SendPictionaryTeamsCommandAsync(command),
        "canvas" => stream.SendWhiteboardCommandAsync(command), "sky-rail" => stream.SendSkyRailCommandAsync(command), _ => Task.CompletedTask };
    private bool SendArcadePlayerAction(string game, string command)
    {
        if (GameRelay(game) is { IsHost: false } relay) return relay.SubmitAction(game == "roulette" ? $"E|{rouletteRoundNumber}|{command}" : command);
        _ = game switch { "quiz" => stream.SendQuizContributionAsync(command), "slot-machine" => stream.SendSlotMachineContributionAsync(command),
            "roulette" => stream.SendRouletteContributionAsync(command), "choco-race" => stream.SendChocoRaceContributionAsync(command), "poker" => stream.SendPokerContributionAsync(command),
            "pictionary" => stream.SendPictionaryContributionAsync(command), "pictionary-teams" => stream.SendPictionaryTeamsContributionAsync(command), "sky-rail" => stream.SendSkyRailContributionAsync(command), _ => Task.CompletedTask };
        return true;
    }
    private void ReceiveArcadeServerCommand(string game, string player, string command)
    {
        if (IsLocalActivityHost) { if (GameRelay(game) is null) HandleRelayGameAction(game, player, command); return; }
        if (command.StartsWith("V|", StringComparison.Ordinal) || game == "quiz" && command.StartsWith("R|", StringComparison.Ordinal))
        { ReceiveActivityRelayInvitation(game, command); return; }
        // An old in-flight legacy snapshot must not tear down the newer Worker
        // connection. The current host/canvas identity handles real transitions.
        if (GameRelay(game) is not null && command.StartsWith("S|", StringComparison.Ordinal)) return;
        if (game == "poker" && GameRelay(game) is not null && command.StartsWith("H|", StringComparison.Ordinal)) return;
        if (RemainingArcadeRelayRules.Game(game))
        {
            // Legacy host compatibility. New host state and secrets arrive only
            // through the account-bound Worker snapshot.
            if (GameRelay(game) is not null) return;
            switch (game) { case "pictionary": ApplyPictionaryCommand(command); break; case "pictionary-teams": ApplyTeamsCommand(command); break;
                case "sky-rail": ApplySkyRailCommand(command); break; case "canvas": ApplyWhiteboardCommand(command); break; }
        }
        else ApplyRelayGame(game, command);
    }
    private void UpdateActivityRelay(string game)
    {
        if (pendingActivityInvites.TryGetValue(game, out var pending))
        {
            if (stream.Mode != StreamMode.Viewing || stream.HostId != pending.Host ||
                CurrentSession?.AccountId != pending.Account || CurrentSession.Token != pending.Token ||
                DateTime.UtcNow - pending.Received > TimeSpan.FromSeconds(30)) pendingActivityInvites.Remove(game);
            else if (TryAcceptActivityRelayInvitation(game, pending.Command)) pendingActivityInvites.Remove(game);
        }
        if (!activityRelays.TryGetValue(game, out var slot)) return;
        var relay = slot.Client;
        if ((relay.IsHost ? stream.Mode != StreamMode.Hosting : stream.Mode != StreamMode.Viewing) || slot.Host != stream.HostId ||
            slot.Account != CurrentSession?.AccountId || (!relay.IsHost && slot.Token != CurrentSession?.Token) ||
            (!relay.IsHost && ActivityFromCanvasUrl(stream.CurrentRoomState?.Url) != GameActivity(game)))
        {
            if (!relay.IsHost && stream.Mode == StreamMode.Hosting) StopRelayGame(game);
            else DisposeActivityRelay(game);
            return;
        }
        if (relay.IsHost && relay.Completion.IsCompleted) { StopRelayGame(game); return; }
        while (relay.Incoming.TryDequeue(out var command))
        { ApplyRelayGame(game, command); if (GameRelay(game) != relay) return; }
        while (relay.ActionResults.TryDequeue(out var result))
        {
            if (result.Accepted) continue;
            if (RemainingArcadeRelayRules.Game(game)) RejectWhiteboardPreview(result.Command);
            var parts = result.Command.Split('|');
            if (game == "quiz" && parts[0] == "J") quizLocalJoined = quizJoinedPlayers.Contains(LocalQuizUserId);
            if (game == "quiz" && parts[0] == "A" && parts[2] == (quizQuestionIndex + 1).ToString() && !quizAnswers.ContainsKey(LocalQuizUserId)) quizLocalAnswer = -1;
            if (game == "choco-race" && parts.Length == 5 && chocoRacePendingBetId == parts[3])
            { chocoRacePendingBetId = null; chocoRacePendingBetSentUtc = DateTime.MinValue; }
        }
        if (!relay.IsHost) return;
        UpdateActivityRelayMembers(slot);
        while (relay.IncomingActions.TryDequeue(out var action))
        {
            var before = GameRevision(game);
            if (stream.Roster.Any(p => p.UserId == action.UserId)) HandleRelayGameAction(game, action.UserId, action.Command);
            var accepted = GameRevision(game) > before || game == "quiz" && action.Command.StartsWith("J|", StringComparison.Ordinal) && quizJoinedPlayers.Contains(action.UserId);
            if (game == "pictionary" && action.Command.StartsWith("N|", StringComparison.Ordinal))
            { var parts = action.Command.Split('|'); accepted |= parts.Length == 3 && pictionaryPlayerNames.GetValueOrDefault(action.UserId) == DecodePictionaryText(parts[2]).Trim(); }
            relay.AcknowledgeAction(action.ActionId, accepted);
        }
        if (RemainingArcadeRelayRules.Game(game)) FlushRemainingState(game);
        if (relay.Connected && DateTime.UtcNow >= slot.NextInvitation) SendActivityRelayInvitation(game, slot);
    }
    private void UpdateActivityRelayMembers(ActivityRelay slot)
    {
        var members = stream.Roster.Select(m => m.UserId).ToArray();
        slot.Client.SetMembers(members);
        var signature = string.Join(';', members.Order(StringComparer.Ordinal));
        if (signature != slot.Roster) { slot.Roster = signature; slot.NextInvitation = DateTime.MinValue; }
    }
    private bool TrySendActivityThroughCloudflare(string game, string command)
    {
        if (stream.Mode != StreamMode.Hosting || GameSession(game) is not { } session || GameCanvas(game) is not { } canvas) return false;
        if (!activityRelays.TryGetValue(game, out var slot) || !slot.Client.IsHost || slot.Session != session || slot.Host != stream.HostId)
        {
            DisposeActivityRelay(game);
            var client = new ArcadeRelayClient(RelayUrl(game), game, session, canvas,
                s => RelayRevision(s, game, session, canvas), validateAction: s => RelayAction(s, game, session),
                failureObserver: ActivityRelayFailureObserver(game));
            slot = new(client, stream.HostId, CurrentSession?.AccountId, CurrentSession?.Token, session);
            activityRelays[game] = slot;
        }
        if (game == "poker")
        {
            using var doc = ArcadeGameRelayRules.Decode(command);
            var node = JsonNode.Parse(doc!.RootElement.GetRawText())!;
            var hands = new JsonObject();
            foreach (var p in pokerPlayers.Where(p => p.InHand && p.First is >= 0 and < 52 && p.Second is >= 0 and < 52)) hands[p.Id] = new JsonArray(p.First, p.Second);
            node["Hands"] = hands;
            command = "S|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(node.ToJsonString()));
        }
        UpdateActivityRelayMembers(slot);
        slot.Client.Publish(command);
        if (slot.Client.Connected && DateTime.UtcNow >= slot.NextInvitation) SendActivityRelayInvitation(game, slot);
        return true;
    }
    private void SendActivityRelayInvitation(string game, ActivityRelay slot)
    {
        if (slot.Client.Room is not { } room) return;
        var invite = new LuckyWheelRelayInvitation(RelayUrl(game), room.RoomId, room.ViewerKey, room.SessionId, room.CanvasId, room.ExpiresUnixMs);
        _ = SendRelayServerCommand(game, "V|" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(invite)));
        slot.NextInvitation = DateTime.UtcNow.AddSeconds(15);
    }
    private void ReceiveActivityRelayInvitation(string game, string command)
    {
        if (stream.Mode != StreamMode.Viewing || command.Length > 2048 || CurrentSession is not { } account) return;
        pendingActivityInvites[game] = (command, stream.HostId, account.AccountId, account.Token, DateTime.UtcNow);
    }
    private bool TryAcceptActivityRelayInvitation(string game, string command)
    {
        if (CurrentSession is not { } account || ActivityFromCanvasUrl(stream.CurrentRoomState?.Url) != GameActivity(game)) return false;
        var expectedCanvas = stream.CurrentRoomState?.Url?[WhiteboardUrlPrefix.Length..].Split('?', 2)[0];
        // The RT callback and UI playback callback run independently. Wait for
        // both to adopt the advertised canvas instead of dropping its invitation.
        if (expectedCanvas != whiteboardSessionId) return false;
        try
        {
            var invite = JsonSerializer.Deserialize<LuckyWheelRelayInvitation>(Convert.FromBase64String(command[2..]));
            if (!LuckyWheelRelayClient.ValidInvitation(invite, RelayUrl(game), whiteboardSessionId ?? "", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                throw new InvalidDataException("Invalid invitation");
            if (activityRelays.TryGetValue(game, out var old) && !old.Client.IsHost && old.Client.Room?.RoomId == invite!.RoomId &&
                old.Host == stream.HostId && old.Token == account.Token && old.Client.Room.ViewerKey == invite.ViewerKey && !old.Client.Completion.IsCompleted) return true;
            DisposeActivityRelay(game);
            var session = invite!.SessionId; var canvas = invite.CanvasId; var player = account.AccountId.ToLowerInvariant();
            if (game == "canvas") whiteboardRequiresRelay = true;
            var relay = new ArcadeRelayClient(invite.Url, game, session, canvas, s => RelayRevision(s, game, session, canvas, player),
                new(invite.RoomId, game, "", invite.ViewerKey, session, canvas, invite.ExpiresUnixMs), accountToken: account.Token, accountId: player,
                validateAction: s => RelayAction(s, game, session), failureObserver: ActivityRelayFailureObserver(game));
            activityRelays[game] = new(relay, stream.HostId, account.AccountId, account.Token, session);
            return true;
        }
        catch (Exception)
        {
            activityRelayErrors[game] = "Could not accept the activity invitation. Check that you and the host have the latest plugin.";
            AepLog.Warning("[Arcade] Viewer activity invitation rejected.");
            return true;
        }
    }
    private static Action<Exception>? ActivityRelayFailureObserver(string game) => AirForceRelayFailureObserver(game) ??
        (_ => AepLog.Warning("[Arcade] Activity relay reconnecting after a transport failure."));
    private static string RelayUrl(string game) => RemainingArcadeRelayRules.Game(game) ? RemainingArcadeRelayRules.Url(game) : ArcadeRelayClient.DefaultUrl;
    private static int? RelayRevision(string command, string game, string session, string canvas, string? player = null) => RemainingArcadeRelayRules.Game(game)
        ? RemainingArcadeRelayRules.Revision(command, game, session, canvas, player) : ArcadeGameRelayRules.Revision(command, game, session, canvas, player);
    private static bool RelayAction(string command, string game, string session) => RemainingArcadeRelayRules.Game(game)
        ? RemainingArcadeRelayRules.Action(command, game, session) : ArcadeGameRelayRules.ValidAction(command, game, session);
}

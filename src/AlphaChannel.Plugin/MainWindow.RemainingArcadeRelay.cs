using System.Text.Json.Nodes;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly Dictionary<string, string> remainingRelayStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> remainingRelayRevisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(int Serial, string Command)>> remainingRelayEvents = new(StringComparer.Ordinal);
    private readonly HashSet<string> remainingRelayDirty = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> remainingRelayPublished = new(StringComparer.Ordinal);
    private int remainingEventSerial;
    private string? DrawingRelayGame => ActiveActivity switch { ActiveActivityKind.Pictionary => "pictionary",
        ActiveActivityKind.PictionaryTeams => "pictionary-teams", ActiveActivityKind.Whiteboard => "canvas", _ => null };
    private int DrawingRelayRound => DrawingRelayGame == "pictionary" ? pictionaryRound : DrawingRelayGame == "pictionary-teams" ? teamsRound : 0;
    private bool SendDrawingContribution(string command)
    {
        if (!DrawingTransportReady) return false;
        if (DrawingRelayGame is { } game && GameRelay(game) is { IsHost: false } relay)
            return relay.SubmitAction($"Q|{GameSession(game)}|{DrawingRelayRound}|{command}");
        _ = stream.SendWhiteboardContributionAsync(command);
        return true;
    }
    private void HandleRemainingAction(string game, string player, string command)
    {
        if (command.StartsWith("Q|", StringComparison.Ordinal))
        {
            var parts = command.Split('|', 4);
            if (parts.Length == 4 && parts[1] == GameSession(game) && parts[2] == DrawingRelayRound.ToString(System.Globalization.CultureInfo.InvariantCulture))
                TryRelayWhiteboardContribution(player, parts[3]);
            return;
        }
        switch (game) { case "pictionary": ApplyPictionaryContribution(player, command); break;
            case "pictionary-teams": HostHandleTeamsContribution(player, command); break;
            case "sky-rail": HandleSkyRailContribution(player, command); break; }
    }
    private bool SendRemainingEvent(string game, string command)
    {
        if (stream.Mode != StreamMode.Hosting || GameRelay(game) is null) return false;
        if (!remainingRelayEvents.TryGetValue(game, out var events)) remainingRelayEvents[game] = events = [];
        events.Add((checked(++remainingEventSerial), command));
        if (events.Count > 64) events.RemoveAt(0);
        if (game == "sky-rail") SendSkyRailState(); else PublishRemainingState(game);
        return true;
    }
    private void SendTeamsFeedback(string player)
    {
        var command = $"F|{player}|{EncodePictionaryText("Try again!")}";
        if (!SendRemainingEvent("pictionary-teams", command)) _ = stream.SendPictionaryTeamsCommandAsync(command);
    }
    private bool TrySendRemainingState(string game, string state)
    {
        if (stream.Mode != StreamMode.Hosting || GameSession(game) is null || GameCanvas(game) is null) return false;
        remainingRelayStates[game] = state;
        PublishRemainingState(game);
        return true;
    }
    private void PublishDrawingState()
    {
        if (stream.Mode == StreamMode.Hosting && DrawingRelayGame is { } game && remainingRelayStates.ContainsKey(game)) PublishRemainingState(game);
    }
    private void PublishRemainingState(string game)
    {
        if (!remainingRelayStates.TryGetValue(game, out var state) || GameSession(game) is not { } session || GameCanvas(game) is not { } canvas) return;
        remainingRelayRevisions[game] = checked(remainingRelayRevisions.GetValueOrDefault(game) + 1);
        remainingRelayDirty.Add(game);
        if (GameRelay(game) is null) FlushRemainingState(game);
    }
    private void FlushRemainingState(string game)
    {
        if (!remainingRelayDirty.Contains(game) || !remainingRelayStates.TryGetValue(game, out var state) || GameSession(game) is not { } session || GameCanvas(game) is not { } canvas) return;
        var now = DateTime.UtcNow;
        if (remainingRelayPublished.TryGetValue(game, out var last) && now - last < TimeSpan.FromMilliseconds(100)) return;
        var revision = remainingRelayRevisions[game];
        remainingRelayPublished[game] = now; remainingRelayDirty.Remove(game);
        var privateCommands = new JsonObject();
        if (game == "pictionary" && pictionaryDrawerId.Length > 0 && pictionaryPhase is PictionaryPhase.ChoosingWord or PictionaryPhase.Drawing)
        {
            privateCommands[pictionaryDrawerId] = new JsonArray(pictionaryPhase == PictionaryPhase.ChoosingWord
                ? $"O|{session}|{pictionaryRound}|{pictionaryDrawerId}|{EncodePictionaryJson(pictionaryWordChoices)}"
                : $"D|{session}|{pictionaryRound}|{pictionaryDrawerId}|{EncodePictionaryText(pictionarySelectedWord)}");
        }
        if (game == "pictionary-teams" && teamsPhase is TeamsPhase.ChoosingWords or TeamsPhase.Drawing)
            for (var team = 0; team < 2; team++) if (teamsDrawers[team].Length > 0)
                privateCommands[teamsDrawers[team]] = new JsonArray(teamsPhase == TeamsPhase.ChoosingWords && !teamsSelected[team]
                    ? $"O|{session}|{teamsRound}|{teamsDrawers[team]}|{team}|{EncodePictionaryJson(teamsChoices[team])}"
                    : $"D|{session}|{teamsRound}|{teamsDrawers[team]}|{team}|{EncodePictionaryText(teamsWords[team])}");
        JsonObject? picture = null;
        if (game != "sky-rail")
        {
            var strokes = new JsonArray();
            foreach (var stroke in whiteboardStrokes)
                strokes.Add(new JsonObject { ["Id"] = stroke.Id, ["Colour"] = stroke.ColourIndex, ["Width"] = stroke.Width,
                    ["Sequence"] = stroke.LastSequence, ["Started"] = stroke.Started, ["Complete"] = stroke.Complete, ["Points"] = EncodeWhiteboardPoints(stroke.Points) });
            picture = new JsonObject { ["White"] = whiteboardBackgroundWhite, ["Mode"] = whiteboardMode == WhiteboardMode.Team ? "T" : "S", ["Strokes"] = strokes };
        }
        var events = new JsonArray();
        foreach (var e in remainingRelayEvents.GetValueOrDefault(game) ?? []) events.Add(new JsonObject { ["Serial"] = e.Serial, ["Command"] = e.Command });
        var envelope = new JsonObject { ["SessionId"] = session, ["CanvasId"] = canvas, ["Revision"] = revision,
            ["State"] = state, ["Canvas"] = picture, ["Private"] = privateCommands, ["Events"] = events };
        TrySendActivityThroughCloudflare(game, RemainingArcadeRelayRules.Encode(envelope));
    }
    private void ApplyRemainingState(string game, string command)
    {
        if (command.StartsWith("X|", StringComparison.Ordinal)) { StopRelayGame(game); return; }
        var s = RemainingArcadeRelayRules.Decode(command);
        if (s is null) return;
        var slot = activityRelays.GetValueOrDefault(game);
        var state = s["State"]!.GetValue<string>();
        // Scores/health from the complete snapshot are applied after cosmetic shot
        // history so a delayed effect can never roll authoritative state backwards.
        if (game == "sky-rail" && skyRailSessionId is null) ApplySkyRailCommand(state);
        foreach (var e in (JsonArray)s["Events"]!)
        {
            var serial = e!["Serial"]!.GetValue<int>();
            if (slot is null || serial <= slot.LastEvent) continue;
            slot.LastEvent = serial;
            var effect = e["Command"]!.GetValue<string>();
            if (game == "sky-rail") ApplySkyRailCommand(effect);
            else if (game == "pictionary") ApplyPictionaryCommand(effect);
            else if (game == "pictionary-teams") ApplyTeamsCommand(effect);
        }
        if (slot?.LastState != state)
        {
            switch (game) { case "pictionary": ApplyPictionaryCommand(state); break; case "pictionary-teams": ApplyTeamsCommand(state); break; case "sky-rail": ApplySkyRailCommand(state); break; }
            if (slot is not null) slot.LastState = state;
        }
        foreach (var own in (JsonArray)s["Own"]!)
        { if (game == "pictionary") ApplyPictionaryCommand(own!.GetValue<string>()); else if (game == "pictionary-teams") ApplyTeamsCommand(own!.GetValue<string>()); }
        if (game == "sky-rail") ActivateSkyRailViewerCanvas(s["CanvasId"]!.GetValue<string>());
        if (s["Canvas"] is JsonObject picture)
        {
            whiteboardBackgroundWhite = picture["White"]!.GetValue<bool>();
            whiteboardMode = picture["Mode"]!.GetValue<string>() == "T" ? WhiteboardMode.Team : WhiteboardMode.Solo;
            whiteboardActive = true;
            var wanted = ((JsonArray)picture["Strokes"]!).Select(x => x!["Id"]!.GetValue<string>()).ToHashSet();
            whiteboardStrokes.RemoveAll(x => !wanted.Contains(x.Id));
            foreach (var id in whiteboardStrokeById.Keys.Where(x => !wanted.Contains(x)).ToArray()) whiteboardStrokeById.Remove(id);
            foreach (var item in (JsonArray)picture["Strokes"]!)
            {
                var id = item!["Id"]!.GetValue<string>(); var sequence = item["Sequence"]!.GetValue<int>();
                if (whiteboardStrokeById.TryGetValue(id, out var stroke) && stroke.LastSequence == sequence) continue;
                if (stroke is null) { stroke = new WhiteboardStroke { Id = id, Started = item["Started"]?.GetValue<int>() ?? 1, ColourIndex = item["Colour"]!.GetValue<int>(), Width = item["Width"]!.GetValue<float>() }; whiteboardStrokeById[id] = stroke; whiteboardStrokes.Add(stroke); }
                stroke.LastSequence = sequence; stroke.Complete = item["Complete"]!.GetValue<bool>(); stroke.Points.Clear();
                var bytes = Convert.FromBase64String(item["Points"]!.GetValue<string>());
                for (var i = 0; i < bytes.Length; i += 4) stroke.Points.Add(new((bytes[i] | bytes[i + 1] << 8) / 65535f, (bytes[i + 2] | bytes[i + 3] << 8) / 65535f));
            }
            ConfirmWhiteboardPreviews();
            whiteboardTvDirty = true;
        }
    }
    private void ResetRemainingRelay(string game)
    {
        DisposeActivityRelay(game); remainingRelayStates.Remove(game); remainingRelayEvents.Remove(game); remainingRelayRevisions.Remove(game);
        remainingRelayDirty.Remove(game); remainingRelayPublished.Remove(game);
    }
}

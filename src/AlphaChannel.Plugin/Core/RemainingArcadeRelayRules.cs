using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AlphaChannel.Plugin;

internal static class RemainingArcadeRelayRules
{
    internal static bool Game(string game) => game is "pictionary" or "pictionary-teams" or "canvas" or "sky-rail";
    internal static string Url(string game) => game == "sky-rail"
        ? "https://arcade-action.example.invalid"
        : "https://arcade-drawing.example.invalid";
    internal static JsonObject? Decode(string command)
    {
        try { return command.Length <= 280000 && command.StartsWith("S|", StringComparison.Ordinal)
            ? JsonNode.Parse(Convert.FromBase64String(command[2..]), documentOptions: new() { MaxDepth = 12 }) as JsonObject : null; }
        catch { return null; }
    }
    internal static string Encode(JsonNode node) => "S|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(node.ToJsonString()));
    private static bool Int(JsonNode? n, int min = 0, int max = int.MaxValue) => n is JsonValue v && v.TryGetValue<int>(out var x) && x >= min && x <= max;
    private static bool Text(JsonNode? n, int max) => n is JsonValue v && v.TryGetValue<string>(out var x) && x.Length <= max;
    private static bool Array(JsonNode? n, int max) => n is JsonArray a && a.Count <= max;
    private static bool Time(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var x) && x >= 0 && x <= 253402300799999;
    private static bool Points(JsonNode? n, int maximum)
    { try { var raw = Convert.FromBase64String(n!.GetValue<string>()); return raw.Length % 4 == 0 && raw.Length <= maximum * 4; } catch { return false; } }
    internal static int? Revision(string command, string game, string session, string canvas, string? player = null)
    {
        try
        {
            var s = Decode(command);
            if (s is null || s["SessionId"]?.GetValue<string>() != session || s["CanvasId"]?.GetValue<string>() != canvas || !Int(s["Revision"], 1) || !Array(s["Events"], 64)) return null;
            if (player is not null && (s.ContainsKey("Private") || s["OwnPlayer"]?.GetValue<string>() != player || !Array(s["Own"], 2))) return null;
            if (player is null && s["Private"] is not JsonObject) return null;
            var events = (JsonArray)s["Events"]!;
            if (events.Any(e => !Int(e?["Serial"], 1) || !Text(e?["Command"], 8192))) return null;
            if (game != "sky-rail")
            {
                var c = s["Canvas"];
                if (c?["White"] is not JsonValue w || !w.TryGetValue<bool>(out _) || c["Mode"]?.GetValue<string>() is not ("T" or "S") || !Array(c["Strokes"], 128) || c["Base"] is not null) return null;
                var strokes = (JsonArray)c["Strokes"]!; var count = 0;
                var seen = new HashSet<string>();
                foreach (var stroke in strokes)
                {
                    if (!ArcadeRelayClient.Hex(stroke?["Id"]?.GetValue<string>(), 32) || !seen.Add(stroke!["Id"]!.GetValue<string>()) || !Int(stroke["Colour"], 0, 15) ||
                        stroke["Width"]?.GetValue<float>() is not (3 or 6 or 12 or 20) || !Int(stroke["Sequence"]) || stroke["Complete"] is not JsonValue complete || !complete.TryGetValue<bool>(out _) || !Points(stroke["Points"], 16384)) return null;
                    count += Convert.FromBase64String(stroke["Points"]!.GetValue<string>()).Length / 4;
                }
                if (count > 16384) return null;
            }
            if (game == "canvas") return s["State"]?.GetValue<string>() == "" ? s["Revision"]!.GetValue<int>() : null;
            var state = Decode(s["State"]!.GetValue<string>());
            if (state is null || state[game == "pictionary-teams" ? "Session" : "SessionId"]?.GetValue<string>() != session || !Int(state["Round"], game == "pictionary-teams" ? 0 : 1, 100000)) return null;
            if (!Time(state[game == "pictionary-teams" ? "Ends" : "PhaseEndsUnixMs"])) return null;
            if (game == "sky-rail")
            {
                if (!Int(state["Phase"], 1, 4) || !Int(state["Revision"]) || !Int(state["WaveCount"], 4, 12) || !Int(state["Wave"], 0, state["WaveCount"]!.GetValue<int>()) ||
                    state["WaveSeconds"]?.GetValue<int>() is not (10 or 15) || !Int(state["BossMode"], 0, 2) || !Int(state["BossMaxHealth"], 50, 100000) || !Int(state["BossHealth"], 0, state["BossMaxHealth"]!.GetValue<int>()) ||
                    !Array(state["Players"], 65) || !Array(state["Targets"], 128) || !Array(state["Rockets"], 128) || !Array(state["ReadyIds"], 65) || !Array(state["LockedIds"], 65) || !Time(state["WaveStartsUnixMs"]) || !Time(state["BossDefeatedUnixMs"]) || s["Canvas"] is not null) return null;
                if (((JsonArray)state["Players"]!).Any(p => !Text(p?["Id"], 128) || !Text(p?["Name"], 256) || !Int(p?["Score"], int.MinValue) || !Int(p?["Combo"]))) return null;
            }
            else if (game == "pictionary")
            {
                if (!Int(state["Phase"], 1, 6) || !Int(state["RoundCount"], 1, 50) || !Int(state["DrawingSeconds"], 15, 300) || !Array(state["Players"], 65) || !Array(state["GuessedUserIds"], 65) || !Text(state["RevealedWord"], 80)) return null;
                if (state["Phase"]!.GetValue<int>() is not (4 or 5) && state["RevealedWord"]!.GetValue<string>() != "") return null;
                if (((JsonArray)state["Players"]!).Any(p => !Text(p?["Id"], 128) || !Text(p?["Name"], 256) || !Int(p?["Score"]) || p?["Active"] is not JsonValue a || !a.TryGetValue<bool>(out _))) return null;
            }
            else if (!Int(state["Phase"], 1, 6) || !Int(state["Rounds"], 1, 50) || !Int(state["Seconds"], 15, 300) || !Array(state["Members"], 65) ||
                ((JsonArray)state["Members"]!).Any(p => !Text(p?["Id"], 128) || !Text(p?["Name"], 256) || !Int(p?["Team"], -1, 1) || !Int(p?["Correct"]))) return null;
            return s["Revision"]!.GetValue<int>();
        }
        catch { return null; }
    }
    // Called on the receive task before coalescing. A delta is never discarded
    // without first merging it into a complete picture for the UI task.
    internal static string Merge(string command, string? previous)
    {
        var next = Decode(command) ?? throw new InvalidDataException("Invalid state");
        if (next["Canvas"] is JsonObject patch && patch["Base"] is not null)
        {
            var old = previous is null ? null : Decode(previous);
            if (old?["Revision"]?.GetValue<int>() != patch["Base"]!.GetValue<int>() || old["Canvas"] is not JsonObject before) throw new InvalidDataException("Canvas needs resynchronising");
            var removed = ((JsonArray)patch["Removed"]!).Select(x => x!.GetValue<string>()).ToHashSet();
            var strokes = ((JsonArray)before["Strokes"]!).Where(x => !removed.Contains(x!["Id"]!.GetValue<string>())).Select(x => x!.DeepClone()).ToList();
            foreach (var edit in (JsonArray)patch["Strokes"]!)
            {
                var clone = edit!.DeepClone(); var id = clone["Id"]!.GetValue<string>();
                var index = strokes.FindIndex(x => x["Id"]!.GetValue<string>() == id);
                var offset = clone["Offset"]!.GetValue<int>();
                var prefix = index >= 0 ? Convert.FromBase64String(strokes[index]["Points"]!.GetValue<string>()) : [];
                if (offset < 0 || offset > prefix.Length || offset % 4 != 0) throw new InvalidDataException("Invalid canvas offset");
                var suffix = Convert.FromBase64String(clone["Points"]!.GetValue<string>());
                if (offset + suffix.Length > 65536) throw new InvalidDataException("Stroke too large");
                clone["Points"] = Convert.ToBase64String(prefix.AsSpan(0, offset).ToArray().Concat(suffix).ToArray());
                ((JsonObject)clone).Remove("Offset");
                if (index >= 0) strokes[index] = clone; else strokes.Add(clone);
            }
            next["Canvas"] = new JsonObject { ["White"] = patch["White"]!.DeepClone(), ["Mode"] = patch["Mode"]!.DeepClone(), ["Strokes"] = new JsonArray(strokes.ToArray()) };
        }
        return Encode(next);
    }
    internal static bool Action(string command, string game, string session)
    {
        if (command.Length > 2048) return false;
        var p = command.Split('|');
        bool Number(int index, int min = 0, int max = int.MaxValue) => index < p.Length && int.TryParse(p[index], out var n) && n >= min && n <= max && n.ToString(System.Globalization.CultureInfo.InvariantCulture) == p[index];
        bool Encoded(int index, int max) { try { return Encoding.UTF8.GetString(Convert.FromBase64String(p[index])).Length <= max; } catch { return false; } }
        if (p.Length < 3 || p[1] != session) return false;
        if (p[0] == "Q")
        {
            if (game == "sky-rail") return false;
            if (!Number(2) || p.Length < 6 || p[3] != "Q") return false;
            if (p[4] == "C") return p.Length == 6 && game != "pictionary-teams";
            if (p[4] == "U") return p.Length == 7 && game != "pictionary-teams" && ArcadeRelayClient.Hex(p[6], 32);
            return p.Length == 12 && p[4] == "P" && ArcadeRelayClient.Hex(p[6], 32) && Number(7) && p[8] is "0" or "1" && Number(9, 0, 15) && p[10] is "3" or "6" or "12" or "20" && Points(JsonValue.Create(p[11]), 64);
        }
        return game switch {
            "pictionary" => p[0] == "N" && p.Length == 3 && Encoded(2, 50) || Number(2, 1) && (p[0] == "W" && p.Length == 4 && Number(3, 0, 2) || p[0] == "G" && p.Length is 4 or 5 && Encoded(p.Length - 1, 80)),
            "pictionary-teams" => p[0] == "T" && p.Length == 3 && Number(2, 0, 1) || p.Length == 5 && Number(2, 1) && Number(3, 0, 1) && (p[0] == "W" && Number(4, 0, 2) || p[0] == "G" && Encoded(4, 80)),
            "sky-rail" => p.Length == 4 && Number(2, 1) && (p[0] == "R" && p[3] is "0" or "1" || p[0] == "P" && Number(3)) || p.Length == 7 && p[0] == "F" && Number(2, 1) && Number(3, 1, 12) && ArcadeRelayClient.Hex(p[4], 32) && Number(5, 80, 1195) && Number(6, 130, 565),
            _ => false };
    }
}

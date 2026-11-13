using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlphaChannel.Plugin;

internal static class ArcadeGameRelayRules
{
    internal static JsonDocument? Decode(string command)
    {
        if (command.Length > 48000 || !command.StartsWith("S|", StringComparison.Ordinal)) return null;
        try { return JsonDocument.Parse(Convert.FromBase64String(command[2..]), new() { MaxDepth = 12 }); } catch { return null; }
    }
    private static bool Int(JsonElement e, long lo = 0, long hi = int.MaxValue) => e.TryGetInt64(out var n) && n >= lo && n <= hi;
    private static bool Int(JsonElement e, string key, long lo = 0, long hi = int.MaxValue) => e.TryGetProperty(key, out var v) && Int(v, lo, hi);
    private static bool Time(JsonElement e, string key) => Int(e, key, 0, 253402300799999);
    private static bool Text(JsonElement e, int max = 256) => e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s && s.Length <= max;
    private static bool Array(JsonElement e, string key, int max) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() <= max;
    private static bool Bounded(JsonElement e) => e.ValueKind switch {
        JsonValueKind.Object => e.EnumerateObject().Count() <= 256 && e.EnumerateObject().All(p => Bounded(p.Value)),
        JsonValueKind.Array => e.GetArrayLength() <= 1024 && e.EnumerateArray().All(Bounded),
        JsonValueKind.String => e.GetString()!.Length <= 8192,
        JsonValueKind.Number => e.TryGetDouble(out var n) && double.IsFinite(n) && Math.Abs(n) <= 9007199254740991,
        JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true, _ => false };
    internal static int? Revision(string command, string game, string session, string canvas, string? player = null)
    {
        if (game == "quiz") return QuizRelayRules.Revision(command, session);
        using var doc = Decode(command);
        if (doc is null) return null;
        try
        {
            var s = doc.RootElement;
            if (!Bounded(s) || s.GetProperty("SessionId").GetString() != session || !Int(s, "Revision", 1) || !Array(s, "Players", game == "poker" ? 16 : 256)) return null;
            var players = s.GetProperty("Players").EnumerateArray().ToArray();
            if (players.Any(p => !Text(p.GetProperty("Id")) || !Text(p.GetProperty("Name"))) || players.Select(p => p.GetProperty("Id").GetString()).Distinct().Count() != players.Length) return null;
            switch (game)
            {
                case "slot-machine":
                    if (!Int(s, "Phase", 0, 5) || !Time(s, "PhaseStartedUnixMs") || !Int(s, "Turn") || !Array(s, "Symbols", 9) || s.GetProperty("Symbols").GetArrayLength() != 9 ||
                        s.GetProperty("Symbols").EnumerateArray().Any(n => !Int(n, 0, 73) || !new[] { 5, 11, 13, 17, 38, 40, 46, 50, 55 }.Contains(n.GetInt32())) || !Array(s, "Queue", 256) || !Array(s, "Wins", 10) || !Int(s, "Payout")) return null;
                    break;
                case "roulette":
                    if (!Int(s, "Phase", 1, 5) || !Int(s, "RoundNumber", 1) || !Int(s, "ResultIndex", 0, 36) ||
                        !Time(s, "PhaseEndsUnixMs") || !Time(s, "SpinStartedUnixMs") || !Time(s, "ResultsStartedUnixMs") || !Time(s, "MusicStartedUnixMs") ||
                        !Array(s, "Bets", 1024) || !Array(s, "Winners", 256) || !Array(s, "Ledger", 256) || s.GetProperty("Bets").EnumerateArray().Any(b => !Target(b.GetProperty("Target").GetString()))) return null;
                    break;
                case "choco-race":
                    if (s.GetProperty("WhiteboardSessionId").GetString() != canvas || !Int(s, "Phase", 1, 4) || !Int(s, "Round", 1) ||
                        !Time(s, "PhaseEndsUnixMs") || !Time(s, "RaceStartedUnixMs") || !Time(s, "RoundStartedUnixMs") || !Time(s, "HostSentUnixMs") ||
                        !Int(s, "WinnerLane", -1, 5) || !Array(s, "Racers", 6) || s.GetProperty("Racers").GetArrayLength() != 6 ||
                        !Array(s, "Obstacles", 256) || !Array(s, "Bets", 384) || !Array(s, "LedgerBets", 1024) || !Array(s, "Payouts", 256)) return null;
                    break;
                case "poker":
                    if (!Int(s, "Phase", 0, 4) || !Int(s, "Street", 0, 3) || !Int(s, "Hand") || !Int(s, "ActionSequence") || !Time(s, "EndsUnixMs") || !Time(s, "MusicStartedUnixMs") ||
                        !Int(s, "ActingSeat", -1, 15) || !Int(s, "Button", -1, 15) || !Array(s, "Board", 5) || !Array(s, "Ledger", 256)) return null;
                    var board = s.GetProperty("Board").EnumerateArray().ToArray();
                    if (board.Any(c => !Int(c, 0, 51)) || board.Select(c => c.GetInt32()).Distinct().Count() != board.Length ||
                        players.Select(p => p.GetProperty("Seat").GetInt32()).Distinct().Count() != players.Length ||
                        players.Any(p => !Int(p, "Seat", 0, 15) || !Int(p, "First", -1, 51) || !Int(p, "Second", -1, 51) ||
                            (s.GetProperty("Phase").GetInt32() != 4 || p.GetProperty("Folded").GetBoolean() || !p.GetProperty("InHand").GetBoolean()) &&
                            (p.GetProperty("First").GetInt32() != -1 || p.GetProperty("Second").GetInt32() != -1))) return null;
                    if (player is null)
                    {
                        if (!s.TryGetProperty("Hands", out var hands) || hands.ValueKind != JsonValueKind.Object || hands.EnumerateObject().Count() > 16) return null;
                        var cards = board.Select(c => c.GetInt32()).ToList();
                        foreach (var hand in hands.EnumerateObject())
                        {
                            if (!players.Any(p => p.GetProperty("Id").GetString() == hand.Name && p.GetProperty("InHand").GetBoolean()) ||
                                hand.Value.ValueKind != JsonValueKind.Array || hand.Value.GetArrayLength() != 2 || hand.Value.EnumerateArray().Any(c => !Int(c, 0, 51))) return null;
                            cards.AddRange(hand.Value.EnumerateArray().Select(c => c.GetInt32()));
                        }
                        if (cards.Distinct().Count() != cards.Count) return null;
                    }
                    else
                    {
                        if (s.TryGetProperty("Hands", out _) || s.GetProperty("OwnHandPlayer").GetString() != player || !Array(s, "OwnHand", 2)) return null;
                        var hand = s.GetProperty("OwnHand").EnumerateArray().ToArray();
                        if (hand.Length is not (0 or 2) || hand.Any(c => !Int(c, 0, 51)) || hand.Select(c => c.GetInt32()).Distinct().Count() != hand.Length ||
                            hand.Any(c => board.Any(b => b.GetInt32() == c.GetInt32())) || hand.Length > 0 && !players.Any(p => p.GetProperty("Id").GetString() == player && p.GetProperty("InHand").GetBoolean())) return null;
                    }
                    break;
                default: return null;
            }
            return s.GetProperty("Revision").GetInt32();
        }
        catch { return null; }
    }
    internal static bool Target(string? value) => value is not null && Regex.IsMatch(value, @"^(?:RED|BLACK|EVEN|ODD|LOW|HIGH|D[1-3]|COL[1-3]|N:(?:[0-9]|[12][0-9]|3[0-6]))$");
    private static bool Number(string value, int lo = 0, int hi = int.MaxValue) => int.TryParse(value, out var n) && n >= lo && n <= hi && value == n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal static bool ValidAction(string command, string game, string session)
    {
        if (command.Length > 512) return false;
        if (game == "roulette" && command.StartsWith("E|", StringComparison.Ordinal))
        { var envelope = command.Split('|', 3); return envelope.Length == 3 && Number(envelope[1], 1) && ValidAction(envelope[2], game, session); }
        var p = command.Split('|');
        if (p.Length < 2 || p[1] != session) return false;
        switch (game)
        {
            case "quiz": return p[0] == "J" && p.Length == 2 || p[0] == "A" && p.Length == 4 && Number(p[2], 1, 100) && Number(p[3], 0, 3);
            case "slot-machine": return p.Length == 4 && ArcadeRelayClient.Hex(p[2], 32) && Number(p[3]) && p[0] is "Q" or "C" or "P";
            case "roulette": return p[0] == "R" && p.Length == 3 && Number(p[2], 2) || p[0] == "C" && p.Length == 2 || p[0] == "B" && p.Length == 4 && Number(p[2], 1) && p[2] is "1" or "5" or "25" or "100" or "500" && Target(p[3]) ||
                p[0] == "T" && p.Length == 4 && Guid.TryParseExact(p[2], "D", out _) && Number(p[3], 1);
            case "choco-race":
                if (p.Length != 5 || p[0] != "B" || !Number(p[2], 1) || !ArcadeRelayClient.Hex(p[3], 32)) return false;
                var entries = p[4].Split(','); var lanes = new HashSet<string>();
                if (entries.Length is < 1 or > 6) return false;
                foreach (var entry in entries) { var pair = entry.Split(':'); if (pair.Length != 2 || !Number(pair[0], 0, 5) || !Number(pair[1], 10) || int.Parse(pair[1]) % 10 != 0 || !lanes.Add(pair[0])) return false; }
                return true;
            case "poker": return p[0] == "J" && p.Length == 3 && Number(p[2], 0, 15) || p[0] == "L" && p.Length == 2 ||
                p[0] == "A" && p.Length == 5 && Number(p[2], 1) && Number(p[3]) && p[4] is "fold" or "check" or "call" or "raise" or "allin";
            default: return false;
        }
    }
}

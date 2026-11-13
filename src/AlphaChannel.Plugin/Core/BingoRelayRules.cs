using System.Text;
using System.Text.Json;

namespace AlphaChannel.Plugin;

internal enum BingoPhase { Inactive, Waiting, Countdown, Drawing, Results, Finished }
internal sealed record BingoCard(string PlayerId, string Name, int[] Numbers)
{
    public int[] Marks { get; init; } = [];
}
internal sealed record BingoStanding(string PlayerId, string Name, int Wins);
internal sealed record BingoParticipant(string PlayerId, string Name);
internal sealed record BingoSnapshot(
    string SessionId, int Revision, int Round, BingoPhase Phase, long PhaseEndsUnixMs,
    long LastDrawUnixMs, int[] Called, BingoCard[] Cards, string[] WinnerIds,
    BingoPattern[]? Patterns = null, int RoundCount = 0, int DrawSeconds = 5, BingoStanding[]? Standings = null,
    BingoParticipant[]? Participants = null, bool Manual = false);

internal static class BingoRelayRules
{
    private static bool Text(string? value, int max) => !string.IsNullOrEmpty(value) && value.Length <= max;
    internal static BingoSnapshot? Decode(string command)
    {
        if (command.Length > 48000 || !command.StartsWith("S|", StringComparison.Ordinal)) return null;
        try { return JsonSerializer.Deserialize<BingoSnapshot>(Convert.FromBase64String(command[2..])); }
        catch { return null; }
    }
    internal static int? Revision(string command, string session, string? player = null)
    {
        var s = Decode(command);
        if (s is null || s.SessionId != session || s.Revision < 1 || s.RoundCount is < 1 or > 14 ||
            s.Round < 1 || s.Round > s.RoundCount || s.Phase is < BingoPhase.Waiting or > BingoPhase.Finished ||
            s.DrawSeconds is < 2 or > 10 || s.PhaseEndsUnixMs is < 0 or > 253402300799999 ||
            s.LastDrawUnixMs is < 0 or > 253402300799999 ||
            s.Patterns is not { Length: >= 1 and <= 7 } || s.Patterns.Any(p => !Enum.IsDefined(p)) ||
            s.Called is not { Length: <= 75 } || s.Called.Any(n => n is < 1 or > 75) || s.Called.Distinct().Count() != s.Called.Length ||
            s.Cards is not { Length: <= 65 } || s.Cards.Any(c => c is null || !Text(c.PlayerId, 128) || !Text(c.Name, 256) || !BingoRules.ValidCard(c.Numbers) || !BingoRules.ValidMarks(c.Numbers, s.Called, c.Marks)) ||
            s.Cards.Select(c => c.PlayerId).Distinct(StringComparer.Ordinal).Count() != s.Cards.Length ||
            s.WinnerIds is not { Length: <= 65 } || s.WinnerIds.Distinct(StringComparer.Ordinal).Count() != s.WinnerIds.Length ||
            s.Standings is not { Length: <= 65 } || s.Standings.Any(p => p is null || !Text(p.PlayerId, 128) || !Text(p.Name, 256) || p.Wins is < 0 or > 14) ||
            s.Standings.Select(p => p.PlayerId).Distinct(StringComparer.Ordinal).Count() != s.Standings.Length) return null;
        var participants = player is null ? s.Cards.Select(c => c.PlayerId).ToArray() : s.Participants?.Select(p => p?.PlayerId).ToArray();
        if (participants is null || participants.Length > 65 || participants.Any(id => !Text(id, 128)) ||
            participants.Distinct(StringComparer.Ordinal).Count() != participants.Length || s.WinnerIds.Any(id => !participants.Contains(id, StringComparer.Ordinal))) return null;
        if (player is not null && (s.Cards.Length > 1 || s.Cards.Any(c => c.PlayerId != player || !participants.Contains(c.PlayerId, StringComparer.Ordinal)) ||
            s.Participants!.Any(p => p is null || !Text(p.Name, 256)))) return null;
        return s.Revision;
    }
    internal static bool ValidAction(string command, string session)
    {
        if (command.Length > 512) return false;
        var parts = command.Split('|');
        if (parts.Length != 4 || parts[1] != session || !int.TryParse(parts[2], out var round) || round is < 1 or > 14 || parts[2] != round.ToString()) return false;
        if (parts[0] == "M") return int.TryParse(parts[3], out var number) && number is >= 1 and <= 75 && parts[3] == number.ToString();
        if (parts[0] == "B") return parts[3] == "claim";
        if (parts[0] != "L") return false;
        try { return BingoRules.ValidCard(JsonSerializer.Deserialize<int[]>(Convert.FromBase64String(parts[3]))); }
        catch { return false; }
    }
}

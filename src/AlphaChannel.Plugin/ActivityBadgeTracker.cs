namespace AlphaChannel.Plugin;

// Only sampled runtime counts: restarting never credits time spent offline.
internal sealed class ActivityBadgeTracker
{
    internal static readonly string[] Emulators = ["snes", "gb", "nes", "gba", "sms", "gg"];
    internal static readonly string[] Games = ["Pictionary", "PictionaryTeams", "Quiz", "Roulette", "Bingo", "SkyRail", "SlotMachine", "Poker", "ChocoRaceBets", "LuckyWheel"];
    public Dictionary<string, double> Seconds { get; set; } = new();
    // Retained only to migrate old distinct-host progress once.
    public HashSet<string> Hosts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Parties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    private double? previousTime;
    private HashSet<string> previousActivities = new();

    internal void Join(string account, string? host, string? party)
    {
        if (Parties.Count < 20 && Guid.TryParse(host, out var id) && Guid.TryParse(party, out var session) &&
            !string.Equals(account, id.ToString("D"), StringComparison.OrdinalIgnoreCase))
            Parties.Add(id.ToString("D") + "/" + session.ToString("D"));
    }

    // The relay already preserves Reason. Keep the TV location first and TV-HELD last.
    internal static string EncodeParty(string? reason, string session) =>
        (reason ?? "TV1:?").Split(';')[0] + ";PARTY1:" + session +
        string.Concat((reason ?? "").Split(';').Skip(1).Where(p => !p.StartsWith("PARTY1:", StringComparison.Ordinal)).Select(p => ";" + p));

    internal static string? ReadParty(string? reason) => reason?.Split(';')
        .Where(p => p.StartsWith("PARTY1:", StringComparison.Ordinal))
        .Select(p => p[7..]).FirstOrDefault(p => Guid.TryParse(p, out _));

    internal void Sample(string? emulator, string? game, bool dj, double now)
    {
        var active = new HashSet<string>();
        if (Emulators.Contains(emulator)) active.Add("retro:" + emulator);
        if (Games.Contains(game)) active.Add("arcade:" + game);
        if (dj) active.Add("dj");
        if (previousTime is double last && now - last is > 0 and <= 3)
            foreach (var key in active.Intersect(previousActivities))
                Seconds[key] = Math.Min(key == "dj" ? 10800 : 5400, Seconds.GetValueOrDefault(key) + now - last);
        previousTime = now;
        previousActivities = active;
    }

    internal IEnumerable<string> Eligible()
    {
        if (Parties.Count >= 20) yield return "chat-buddies";
        if (Emulators.Sum(id => Seconds.GetValueOrDefault("retro:" + id)) >= 5400) yield return "controller-crunch";
        if (Games.Sum(id => Seconds.GetValueOrDefault("arcade:" + id)) >= 5400) yield return "arcade-buddy";
        if (Seconds.GetValueOrDefault("dj") >= 10800) yield return "vinyl-pal";
    }

    internal void Sanitize()
    {
        var keys = Emulators.Select(id => "retro:" + id).Concat(Games.Select(id => "arcade:" + id)).Append("dj").ToHashSet();
        Seconds = (Seconds ?? new()).Where(p => keys.Contains(p.Key) && double.IsFinite(p.Value) && p.Value >= 0)
            .ToDictionary(p => p.Key, p => Math.Min(p.Key == "dj" ? 10800 : 5400, p.Value));
        Hosts = (Hosts ?? new()).Where(id => Guid.TryParse(id, out _)).Select(id => Guid.Parse(id).ToString("D"))
            .Take(5).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Parties = (Parties ?? new()).Where(value => value.Split('/') is [var host, var party] &&
            Guid.TryParse(host, out _) && Guid.TryParse(party, out _)).Take(20).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Each previously recorded host proves at least one successfully joined party.
        foreach (var host in Hosts)
            if (Parties.Count < 20 && !Parties.Any(p => p.StartsWith(host + "/", StringComparison.OrdinalIgnoreCase)))
                Parties.Add(host + "/00000000-0000-0000-0000-000000000000");
        Hosts.Clear();
    }
}

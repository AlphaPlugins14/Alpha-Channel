using AlphaChannel.Contracts;

namespace AlphaChannel.Plugin.Core;

// Receive-thread roster updates must not erase the newly promoted host's name.
internal sealed class WatchPartyHostIdentity
{
    private readonly object gate = new();
    private readonly Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
    private string? host;
    internal void ObserveRoster(IEnumerable<ParticipantInfo> roster)
    { lock (gate) foreach (var member in roster) Remember(member.UserId, member.DisplayName); }
    internal void SelectHost(string? id, IEnumerable<ParticipantInfo> previousRoster, string? announcedName = null)
    {
        lock (gate)
        {
            host = id;
            foreach (var member in previousRoster) Remember(member.UserId, member.DisplayName);
            Remember(id, announcedName);
        }
    }
    internal string? NameFor(string? id)
    {
        lock (gate) return id is not null && string.Equals(host, id, StringComparison.OrdinalIgnoreCase)
            ? names.GetValueOrDefault(id) : null;
    }
    private void Remember(string? id, string? name)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || Guid.TryParse(name, out _)) return;
        names[id] = name.Trim();
        while (names.Count > 256)
        {
            var old = names.Keys.First(key => !string.Equals(key, host, StringComparison.OrdinalIgnoreCase));
            names.Remove(old);
        }
    }
    internal void Reset() { lock (gate) { host = null; names.Clear(); } }
}

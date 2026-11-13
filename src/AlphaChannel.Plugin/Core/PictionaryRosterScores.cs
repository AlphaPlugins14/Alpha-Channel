namespace AlphaChannel.Plugin;

internal static class PictionaryRosterScores
{
    // Scores belong to the current party membership. Rejoining starts at zero;
    // removing historical entries also keeps relay snapshots bounded by the
    // current roster rather than everyone who ever visited this game.
    internal static void RetainConnected(Dictionary<string, int> scores,
        Dictionary<string, string> names, IReadOnlySet<string> connected)
    {
        foreach (var id in scores.Keys.Where(id => !connected.Contains(id)).ToArray()) scores.Remove(id);
        foreach (var id in names.Keys.Where(id => !connected.Contains(id)).ToArray()) names.Remove(id);
    }
}

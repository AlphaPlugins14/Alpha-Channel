namespace AlphaChannel.Plugin;

// Entering a room invalidates outstanding discovery responses, even if the user
// leaves that room again before an earlier request finishes.
internal sealed class WorldBeaconDiscoveryState
{
    internal string? Token { get; private set; }
    internal string? Location { get; private set; }
    internal int Generation { get; private set; }
    internal bool Enabled => Token is not null && Location is not null;

    internal bool Observe(string? token, string? location, bool inRoom)
    {
        if (inRoom || token is null || location is null) { token = null; location = null; }
        if (Token == token && Location == location) return false;
        Token = token;
        Location = location;
        Generation++;
        return true;
    }

    internal bool Matches(string token, string location, int generation) =>
        Enabled && Token == token && Location == location && Generation == generation;
}

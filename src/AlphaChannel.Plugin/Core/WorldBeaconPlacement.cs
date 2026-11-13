using System.Numerics;

namespace AlphaChannel.Plugin;

// Pure placement debounce. Camera/player motion never changes this state.
internal sealed class WorldBeaconPlacement
{
    internal string? Location { get; private set; }
    internal Vector3 Position { get; private set; }
    internal DateTime ReadyAt { get; private set; }
    internal void Observe(string? location, Vector3 position, DateTime now)
    {
        if (location is null) { Location = null; return; }
        if (Location != location || Vector3.DistanceSquared(Position, position) > 0.0001f)
        {
            Location = location;
            Position = position;
            ReadyAt = now.AddSeconds(30);
        }
    }
    internal bool Ready(DateTime now) => Location is not null && now >= ReadyAt;
}

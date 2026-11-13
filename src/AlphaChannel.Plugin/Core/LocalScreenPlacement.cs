namespace AlphaChannel.Plugin;

// Runtime-only: never restore a manual position from a previous visit/session.
internal sealed class LocalScreenPlacement
{
    private uint territory;
    private string? location;
    internal bool IsFixed { get; private set; }

    internal bool Observe(bool loggedIn, uint currentTerritory, string? currentLocation)
    {
        var changed = !loggedIn
            ? territory != 0 || location is not null || IsFixed
            : territory != currentTerritory ||
              currentLocation is not null && location is not null && currentLocation != location;
        if (changed) ClearFixed();
        if (!loggedIn)
        {
            territory = 0;
            location = null;
        }
        else
        {
            if (territory != currentTerritory) location = null;
            territory = currentTerritory;
            // Missing housing/player details during zoning aren't a new location.
            if (currentLocation is not null) location = currentLocation;
        }
        return changed;
    }

    internal void Pin() => IsFixed = true;
    internal void ClearFixed() => IsFixed = false;
    internal bool ShouldRecenter(bool followingHost) => !followingHost && !IsFixed;
}

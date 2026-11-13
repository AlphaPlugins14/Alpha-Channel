using System.Globalization;
using AlphaChannel.Contracts;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AlphaChannel.Plugin;

// The server already preserves StreamControl.Reason on stream.state. Keep this
// versioned, numeric location tag separate from the visible room Location field.
internal readonly record struct WatchPartyTvLocation(
    uint World, uint Territory, uint Instance, int? Ward, int? Plot, int? Room)
{
    private const string Prefix = "TV1:";

    internal string Encode() => Ward is { } ward && Plot is { } plot && Room is { } room
        ? $"{Prefix}{World:X}.{Territory:X}.{Instance:X}.{ward:X}.{plot:X}.{room:X}"
        : $"{Prefix}{World:X}.{Territory:X}.{Instance:X}";

    internal static bool TryDecode(string? value, out WatchPartyTvLocation location)
    {
        location = default;
        value = WatchPartyScreenSyncPause.LocationReason(value);
        if (value is null || value.Length > 72 ||
            !value.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        var parts = value[Prefix.Length..].Split('.');
        if (parts.Length is not (3 or 6) ||
            !TryHex(parts[0], out var world) || world == 0 ||
            !TryHex(parts[1], out var territory) || territory == 0 ||
            !TryHex(parts[2], out var instance)) return false;

        if (parts.Length == 3)
        {
            location = new(world, territory, instance, null, null, null);
            return true;
        }

        if (!TryHex(parts[3], out var ward) ||
            !TryHex(parts[4], out var plot) ||
            !TryHex(parts[5], out var room)) return false;
        location = new(world, territory, instance,
            unchecked((int)ward), unchecked((int)plot), unchecked((int)room));
        return true;
    }

    private static bool TryHex(string text, out uint value)
    {
        value = 0;
        return text.Length is > 0 and <= 8 &&
            uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    internal static bool IsLocalPlayerReady() =>
        Plugin.ClientState.IsLoggedIn &&
        Plugin.ObjectTable.LocalPlayer is not null &&
        !Plugin.Condition[ConditionFlag.BetweenAreas] &&
        !Plugin.Condition[ConditionFlag.BetweenAreas51];

    internal static unsafe bool TryReadCurrent(out WatchPartyTvLocation location)
    {
        location = default;
        if (!IsLocalPlayerReady()) return false;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null) return false;
        var world = player.CurrentWorld.RowId;
        var territory = Plugin.ClientState.TerritoryType;
        if (world == 0 || territory == 0) return false;

        var instance = Plugin.ClientState.Instance;
        var housing = HousingManager.Instance();
        if (housing is null) return false;
        if (housing->CurrentTerritory is null)
        {
            location = new(world, territory, instance, null, null, null);
            return true;
        }

        var ward = housing->GetCurrentWard();
        var plot = housing->GetCurrentPlot();
        var room = housing->GetCurrentRoom();
        if (ward < 0) return false;
        location = new(world, territory, instance, ward, plot, room);
        return true;
    }

    internal static ViewerTvPlacementStatus Assess(StreamControl? state,
        float maxDistance = 35f)
    {
        if (state is null || !TryReadCurrent(out var local) ||
            !TryDecode(state.Reason, out var host) ||
            state.ScreenX is not { } x || state.ScreenY is not { } y ||
            state.ScreenZ is not { } z)
            return ViewerTvPlacementStatus.Unknown;

        if (local != host) return ViewerTvPlacementStatus.DifferentLocation;
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null) return ViewerTvPlacementStatus.Unknown;
        return IsResidentialInterior() || Vector3.Distance(player.Position, new Vector3(x, y, z)) <= maxDistance
            ? ViewerTvPlacementStatus.Near
            : ViewerTvPlacementStatus.TooFar;
    }

    internal static unsafe bool IsResidentialInterior()
    {
        if (!IsLocalPlayerReady()) return false;
        var housing = HousingManager.Instance();
        return housing is not null && housing->IndoorTerritory is not null &&
            (void*)housing->CurrentTerritory == (void*)housing->IndoorTerritory;
    }

}

internal enum ViewerTvPlacementStatus
{
    Near,
    TooFar,
    DifferentLocation,
    Unknown,
}

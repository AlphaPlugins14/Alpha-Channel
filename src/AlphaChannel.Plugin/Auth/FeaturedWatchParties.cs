using AlphaChannel.Contracts;

namespace AlphaChannel.Plugin.Auth;

internal static class FeaturedWatchParties
{
    internal static RoomDirectoryDto[] Select(IEnumerable<RoomDirectoryDto> rooms,
        string? viewerAccountId, Func<string, int> badgeTier, int count = 3) =>
        Order(rooms, viewerAccountId, badgeTier).Take(count).ToArray();

    internal static IEnumerable<RoomDirectoryDto> Order(IEnumerable<RoomDirectoryDto> rooms,
        string? viewerAccountId, Func<string, int> badgeTier) =>
        rooms.Where(room => !string.IsNullOrWhiteSpace(room.HostAccountId))
            .Select(room => (Room: room, Priority: Priority(room, viewerAccountId, badgeTier)))
            .OrderBy(item => item.Priority)
            .ThenByDescending(item => item.Room.ViewerCount)
            .ThenBy(item => item.Room.HostDisplayName, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(item => item.Room.HostAccountId, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Room);

    private static int Priority(RoomDirectoryDto room, string? viewerAccountId, Func<string, int> badgeTier)
    {
        var patreon = badgeTier(room.HostAccountId) > 0;
        if (patreon && string.Equals(room.HostAccountId, viewerAccountId, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (patreon) return room.HasMedia ? 1 : 2;
        if (room.Kind == RoomKind.Venue) return room.HasMedia ? 3 : 4;
        return room.HasMedia ? 5 : 6;
    }
}

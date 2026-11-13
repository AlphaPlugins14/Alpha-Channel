using System.Globalization;

namespace AlphaChannel.Contracts;

public static class DmPageCursor
{
    public static string Encode(DateTime timestamp, Guid logicalId) => $"v2.{timestamp.Ticks:x16}.{logicalId:N}";
    public static bool TryDecode(string? cursor, out DateTime timestamp, out Guid logicalId)
    {
        timestamp = default; logicalId = default;
        if (cursor is null || cursor.Length != 52) return false;
        var parts = cursor.Split('.');
        if (parts.Length != 3 || parts[0] != "v2" || parts[1].Length != 16 ||
            !long.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var ticks) ||
            ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks || !Guid.TryParseExact(parts[2], "N", out logicalId)) return false;
        timestamp = new(ticks, DateTimeKind.Utc); return true;
    }
}

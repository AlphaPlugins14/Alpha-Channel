using System.Globalization;

namespace AlphaChannel.Plugin;

// Stable wire IDs: never renumber these when changing labels or display order.
internal static class PartyVibes
{
    internal static bool Matches(string? description, IReadOnlySet<int> selected) =>
        selected.Count == 0 || Read(description).Overlaps(selected);

    internal static readonly (int Id, string Label)[] Options =
    [
        (1, "LFW"), (2, "Chat Welcome"), (3, "Quiet Viewing"), (4, "No Spoilers"),
        (5, "Welcome to join"), (6, "Hang out"), (7, "First time viewing"), (8, "Watch & Discuss"),
    ];

    internal static string Marker(string category, int rating, string world, IEnumerable<int> ids)
    {
        var selected = ids.Where(id => id is >= 1 and <= 8).Distinct().Order().ToArray();
        var vibes = selected.Length == 0 ? "" : "#v=" + string.Join(',', selected);
        return $"<#{category}#{rating}#{world}{vibes}#>";
    }

    internal static HashSet<int> Read(string? description)
    {
        var selected = new HashSet<int>();
        if (description is null) return selected;
        var start = description.IndexOf("<#", StringComparison.Ordinal);
        var end = start < 0 ? -1 : description.IndexOf("#>", start + 2, StringComparison.Ordinal);
        if (end < 0) return selected;
        var parts = description[(start + 2)..end].Split('#', StringSplitOptions.TrimEntries);
        foreach (var part in parts.Skip(3))
        {
            if (!part.StartsWith("v=", StringComparison.Ordinal)) continue;
            foreach (var value in part[2..].Split(','))
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id is >= 1 and <= 8)
                    selected.Add(id);
        }
        return selected;
    }
}

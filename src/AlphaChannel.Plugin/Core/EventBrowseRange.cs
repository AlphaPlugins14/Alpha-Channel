namespace AlphaChannel.Plugin;

internal static class EventBrowseRange
{
    internal static readonly string[] Labels = ["See all", "Next 7 days", "Next 30 days", "Next 45 days"];
    // Events can be scheduled up to a year ahead. Include the final local day.
    internal static DateTime End(DateTime today, int filter) => today.AddDays(filter switch { 0 => 367, 1 => 7, 2 => 30, _ => 45 });

    internal static IEnumerable<(long From, long To)> RequestWindows(long from, long to)
    {
        // The service accepts at most 63 days per request. Millisecond windows
        // avoid local daylight-saving changes and overlap only at the boundary.
        const long window = 60L * 24 * 60 * 60 * 1000;
        for (var cursor = from; cursor < to;)
        {
            var end = Math.Min(cursor + window, to);
            yield return (cursor, end);
            cursor = end;
        }
    }
}

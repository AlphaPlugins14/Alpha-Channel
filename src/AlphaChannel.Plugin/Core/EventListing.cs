namespace AlphaChannel.Plugin;

internal sealed record EventListingGroup(CommunityEvent Event,CommunityEvent[] Dates);
internal static class EventListing
{
    internal static string DayHeading(DateTime date,DateTime today)
    {
        if(date.Date==today.Date)return "Today · "+date.ToString("dddd d MMMM");
        if(date.Date==today.Date.AddDays(1))return "Tomorrow · "+date.ToString("dddd d MMMM");
        if(date.Date>today.Date&&date.Date<today.Date.AddDays(7)&&date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)return "This weekend";
        return date.ToString("dddd d MMMM");
    }
    // Group only the already-filtered local snapshot. Each date retains its own ID/RSVP/room.
    internal static EventListingGroup[] Groups(IEnumerable<CommunityEvent> events,bool collapseSeries,long now)=>
        events.GroupBy(e=>collapseSeries&&e.SeriesId is not null?"series:"+e.SeriesId:"event:"+e.Id)
            .Select(g=>
            {
                var dates=g.OrderBy(e=>e.StartAt).ThenBy(e=>e.Id,StringComparer.Ordinal).ToArray();
                return new EventListingGroup(dates.FirstOrDefault(e=>EventSchedule.Live(e,now))??dates[0],dates);
            })
            .OrderBy(g=>g.Event.StartAt).ThenBy(g=>g.Event.Title,StringComparer.OrdinalIgnoreCase).ToArray();
    internal static string Timing(CommunityEvent e,long now)
    {
        if(e.Cancelled)return "Cancelled";
        if(EventSchedule.Live(e,now))return "Live now";
        if(e.EndAt<=now)return "Ended";
        var minutes=(long)Math.Ceiling((e.StartAt-now)/60000d);
        if(minutes<60)return $"Starts in {Math.Max(1,minutes)} min";
        if(minutes<360){var hours=(long)Math.Ceiling(minutes/60d);return $"Starts in {hours} {(hours==1?"hour":"hours")}";}
        var local=DateTimeOffset.FromUnixTimeMilliseconds(e.StartAt).LocalDateTime.Date;
        var today=DateTimeOffset.FromUnixTimeMilliseconds(now).LocalDateTime.Date;
        return local==today?"Later today":local==today.AddDays(1)?"Starts tomorrow":"";
    }
}

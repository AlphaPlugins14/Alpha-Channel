using AlphaChannel.Contracts;

namespace AlphaChannel.Plugin;

internal sealed record EventPerson(string AccountId,string DisplayName,string? AvatarIcon,string? AvatarColorHex,string? AvatarImageUrl);
internal sealed record EventRecurrence(string Frequency,int Interval,int[] Weekdays,string? Until,int Count);
internal sealed record CommunityEvent(string Id,string? SeriesId,string OwnerId,EventPerson Organiser,string Title,string Icon,string Colour,
    string Website,string Description,string Category,long StartAt,long EndAt,string Timezone,string StartLocal,string EndLocal,
    string World,string DataCentre,string Location,bool Adult,string? HostAccountId,string? HostName,string HostState,int Version,
    bool Cancelled,long UpdatedAt,int GoingCount,bool MyRsvp,bool PublicAttendance,bool Reminders,EventPerson[] AttendeeSample,
    EventRecurrence? Recurrence,RoomDirectoryDto? Room,bool AllowSeriesRsvp=true,bool OfferJoinPrompt=true,bool ShowJoinButton=true,
    string? ConnectedHostAccountId=null,int ConnectionRevision=0,bool ShareToDiscord=false);
internal sealed record EventSnapshot(CommunityEvent[] Events,long ServerTime,bool Truncated);
internal sealed record EventAttendees(EventPerson[] Attendees,string? Next,int Total);
internal sealed record EventCreated(string Id,string? SeriesId,int Count);
internal sealed class EventDraft
{
    public string Title="",Icon="Film",Colour="#3B82F6",Website="",Description="",Category="Games Night";
    public string StartLocal=DateTime.Now.AddDays(1).ToString("yyyy-MM-ddT20:00"),EndLocal=DateTime.Now.AddDays(1).ToString("yyyy-MM-ddT23:00");
    public string Timezone=TimeZoneInfo.Local.HasIanaId?TimeZoneInfo.Local.Id:
        TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id,out var zone)?zone:"Etc/UTC";
    public string World="Balmung",DataCentre="Crystal",Location="";
    public bool Adult,Future;
    public bool ShareToDiscord=true;
    public bool AllowSeriesRsvp=true,OfferJoinPrompt=true,ShowJoinButton=true;
    public string? HostAccountId;
    public EventRecurrence? Recurrence;
    public int Version;
}
internal static class EventSchedule
{
    internal static string ShareText(CommunityEvent e,int maxLength=int.MaxValue)
    {
        var start=DateTimeOffset.FromUnixTimeMilliseconds(e.StartAt).ToLocalTime();
        var end=DateTimeOffset.FromUnixTimeMilliseconds(e.EndAt).ToLocalTime();
        var time=start.ToString("ddd d MMM yyyy · HH:mm")+" — "+(start.Date==end.Date?end.ToString("HH:mm"):end.ToString("ddd d MMM yyyy · HH:mm"));
        var lines=new List<string>{e.Title+(e.Adult?" [18+]":"")+(e.Cancelled?" [Cancelled]":""),time+" ("+TimeZoneInfo.Local.DisplayName+")",
            string.Join(" · ",new[]{e.World,e.DataCentre,e.Location}.Where(s=>!string.IsNullOrWhiteSpace(s)))};
        if(e.HostAccountId is not null)lines.Add("Watch Party host: "+e.HostName);
        var heading=string.Join("\n",lines);const string footer="Find this event in Alpha Channel → Events.";
        var available=Math.Max(0,maxLength-heading.Length-footer.Length-2);
        var description=e.Description.Length<=available?e.Description:available>0?e.Description[..Math.Max(0,available-1)]+"…":"";
        return heading+"\n"+description+"\n"+footer;
    }
    internal static bool ReminderDue(CommunityEvent e,long now)=>e.MyRsvp&&e.Reminders&&!e.Cancelled&&
        now>=e.StartAt-30*60000L&&now<e.EndAt-Math.Min(30*60000L,Math.Max(60000L,(e.EndAt-e.StartAt)/2));
    internal static bool Live(CommunityEvent e,long now)=>!e.Cancelled&&now>=e.StartAt&&now<e.EndAt;
    internal static string? RoomHost(CommunityEvent e)=>e.ConnectedHostAccountId??(e.HostState=="accepted"?e.HostAccountId:null);
    internal static bool RoomNotificationDue(CommunityEvent e,long now)=>Live(e,now)&&e.MyRsvp&&
        (e.ConnectedHostAccountId is not null&&e.ConnectionRevision>0||e.OfferJoinPrompt&&ReminderDue(e,now));
    internal static string RoomNotificationKey(string account,CommunityEvent e)=>$"{account}:{e.Id}:room:{e.StartAt}"+
        (e.ConnectionRevision>0?$":connect:{e.ConnectionRevision}":"");
    internal static string ReminderKey(string account,CommunityEvent e)=>$"{account}:{e.Id}:reminder:{e.StartAt}";
    internal static string EscapeCalendar(string value)=>value.Replace("\\","\\\\").Replace(";","\\;").Replace(",","\\,").Replace("\r","").Replace("\n","\\n");
    internal static string CalendarFile(CommunityEvent e)
    {
        string Stamp(long at)=>DateTimeOffset.FromUnixTimeMilliseconds(at).UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");
        var lines=new[]{"BEGIN:VCALENDAR","VERSION:2.0","PRODID:-//Alpha Channel//Events//EN","BEGIN:VEVENT",$"UID:{e.Id}@alphachannel",
            $"DTSTAMP:{Stamp(e.UpdatedAt)}",$"DTSTART:{Stamp(e.StartAt)}",$"DTEND:{Stamp(e.EndAt)}",$"SUMMARY:{EscapeCalendar(e.Title)}",
            $"LOCATION:{EscapeCalendar(e.World+" · "+e.Location)}",$"DESCRIPTION:{EscapeCalendar(e.Description)}",
            e.Website.Length>0?$"URL:{e.Website.Replace("\r","").Replace("\n","")}":"",e.Cancelled?"STATUS:CANCELLED":"STATUS:CONFIRMED","END:VEVENT","END:VCALENDAR"};
        return string.Join("\r\n",lines.Where(l=>l.Length>0).Select(FoldCalendarLine))+"\r\n";
    }
    private static string FoldCalendarLine(string line)
    {
        var builder=new System.Text.StringBuilder();var bytes=0;
        foreach(var rune in line.EnumerateRunes())
        {
            var text=rune.ToString();var size=System.Text.Encoding.UTF8.GetByteCount(text);
            if(bytes+size>75){builder.Append("\r\n ");bytes=1;}
            builder.Append(text);bytes+=size;
        }
        return builder.ToString();
    }
}

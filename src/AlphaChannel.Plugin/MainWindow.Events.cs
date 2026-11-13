using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool eventsCalendarView,eventsWeekView,eventsMyView,eventsHideAdult;
    private int eventsFilter,eventsMyTab;
    private int eventsPastDays=60;
    private string eventsSearch="",eventsWorldFilter="",eventsCategoryFilter="";
    private DateTime? eventsSelectedDay;
    private bool eventsScrollToSelectedDay;
    private CommunityEvent? eventSelected;
    private bool eventDialogOpen,eventEditorOpen,eventAttendeesOpen,eventShareOpen,eventCancelOpen;
    private string? eventActionNotice;
    private static FontAwesomeIcon EventIcon(string icon)=>icon switch
    {"Music"=>FontAwesomeIcon.Music,"Star"=>FontAwesomeIcon.Star,"Question"=>FontAwesomeIcon.Question,"Gamepad"=>FontAwesomeIcon.Gamepad,
        "Users"=>FontAwesomeIcon.Users,"Heart"=>FontAwesomeIcon.Heart,"Calendar"=>FontAwesomeIcon.CalendarAlt,_=>FontAwesomeIcon.Film};
    private Vector4 EventColour(CommunityEvent e)
    {
        try {var rgb=Convert.ToInt32(e.Colour.TrimStart('#'),16);return new((rgb>>16&255)/255f,(rgb>>8&255)/255f,(rgb&255)/255f,1);}catch{return AccentHover;}
    }
    private int eventsDateFilter=2;
    private CommunityEvent[]? eventsCapacitySnapshot;
    private void DrawEventsPage()
    {
        DrawQuickViewBar();
        using var eventFontScope=BeginUiFonts();
        if(CurrentSession is not { } session){DrawSocialHelper("Sign in to discover events and RSVP.");return;}
        using var frames=ImRaii.PushStyle(ImGuiStyleVar.FramePadding,UiVec(10,7)).Push(ImGuiStyleVar.FrameRounding,Ui(8)).Push(ImGuiStyleVar.FrameBorderSize,Ui(1))
            .Push(ImGuiStyleVar.PopupBorderSize,Ui(1)).Push(ImGuiStyleVar.PopupRounding,Ui(8));
        using var colours=ImRaii.PushColor(ImGuiCol.FrameBg,FrameBg).Push(ImGuiCol.Border,AccentInk(.55f)).Push(ImGuiCol.PopupBg,FrameBg);
        DrawEventsToolbar();
        if(!ReferenceEquals(eventsCapacitySnapshot,events))
        {
            eventsCapacitySnapshot=events;
            RoomCapacityClient.Shared.QueueRefresh(session.Token,events.Select(e=>EventSchedule.RoomHost(e)).OfType<string>());
        }
        var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();var range=EventsRange();
        foreach(var invite in events.Where(e=>e.HostAccountId==session.AccountId&&e.HostState=="pending"&&!e.Cancelled&&e.EndAt>now).GroupBy(e=>e.SeriesId??e.Id).Select(g=>g.First()))
        {
            ImGui.TextWrapped($"{invite.Organiser.DisplayName} invited you to host {invite.Title}");
            using(UiDisabled.Scope(eventsMutationBusy))
            {
                if(AlphaChatButton("eventHostYes"+invite.Id,"Accept hosting",FontAwesomeIcon.Check,Ui(140),primary:true))MutateEvent(s=>eventsApi.Host(s.Token,invite.Id,true,youtubeFeedToken));
                ImGui.SameLine();if(AlphaChatButton("eventHostNo"+invite.Id,"Decline",FontAwesomeIcon.Times,Ui(90)))MutateEvent(s=>eventsApi.Host(s.Token,invite.Id,false,youtubeFeedToken));
            }
        }
        var dateEnd=EventBrowseRange.End(DateTime.Today,eventsDateFilter);
        var filtered=events.Where(e=>
            (!eventsHideAdult||!e.Adult)&&
            (eventsSearch.Length==0||(e.Title+" "+e.Description+" "+e.Location+" "+e.HostName+" "+e.Organiser.DisplayName).Contains(eventsSearch,StringComparison.OrdinalIgnoreCase))&&
            (eventsWorldFilter.Length==0||(e.World+" "+e.DataCentre).Contains(eventsWorldFilter,StringComparison.OrdinalIgnoreCase))&&
            (eventsCategoryFilter.Length==0||e.Category.Contains(eventsCategoryFilter,StringComparison.OrdinalIgnoreCase))&&
            (eventsMyView?eventsMyTab switch {0=>e.MyRsvp&&e.EndAt>now&&!e.Cancelled,1=>e.OwnerId==session.AccountId&&e.EndAt>now,_=>(e.MyRsvp||e.OwnerId==session.AccountId)&&(e.EndAt<=now||e.Cancelled)&&e.StartAt<range.To&&e.EndAt>=range.From}:
                !e.Cancelled&&e.StartAt<range.To&&e.EndAt>=range.From&&(eventsFilter==1?EventSchedule.Live(e,now):eventsCalendarView||e.EndAt>now)&&
                (eventsCalendarView||DateTimeOffset.FromUnixTimeMilliseconds(e.StartAt).LocalDateTime<dateEnd))).OrderBy(e=>e.StartAt).ToArray();
        if(eventsCalendarView&&!eventsMyView)DrawEventsCalendar(filtered);
        else DrawEventsAgenda(filtered,session);
    }
    private void RefreshEventsNow(){eventsHasPublicSnapshot=false;eventsNextSync=DateTime.MinValue;eventsRetryAfter=DateTime.MinValue;eventActionNotice=null;}
    private void OpenEventDetails(CommunityEvent e)
    {eventDetailsGeneration++;eventDetailsLoading=false;eventSelected=e;eventDialogOpen=true;eventDetailsId="";eventActionNotice=null;}
    private void DrawEventsAgenda(CommunityEvent[] list,CharacterSession session)
    {
        if(list.Length==0)
        {
            ImGui.Spacing();
            using(ImRaii.PushColor(ImGuiCol.ChildBg,FrameBg).Push(ImGuiCol.Border,AccentInk(.25f)))
            using(ImRaii.PushStyle(ImGuiStyleVar.ChildRounding,Ui(12)).Push(ImGuiStyleVar.WindowPadding,UiVec(24,24)))
            using(var empty=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##eventsEmpty",new Vector2(-1,Ui(170)),true,ImGuiWindowFlags.AlwaysUseWindowPadding|ImGuiWindowFlags.NoScrollbar)))
            {
                if(empty)
                {
                    EventSectionHeading(eventsSyncBusy?"Loading your plans…":eventsMyView&&eventsMyTab==0?"Your next plans start here":"Something to look forward to",FontAwesomeIcon.CalendarAlt);
                    DrawSocialHelperWrapped(eventsMyView&&eventsMyTab==0?"RSVP to an event and find it here, with your reminders and linked Watch Parties.":"No events match this view. Try clearing your filters or bring people together with a new event.");
                    ImGui.Spacing();if(eventsMyView&&eventsMyTab==0)
                    {if(AlphaChatButton("browseEventsEmpty","Browse events",FontAwesomeIcon.Search,Ui(160),primary:true)){eventsMyView=false;eventsCalendarView=false;eventsFilter=0;}}
                    else if(AlphaChatButton("createEmptyEvent","Create event",FontAwesomeIcon.Plus,Ui(144),primary:true))OpenEventEditor(null,false);
                }
            }
            return;
        }
        string? previous=null;
        var groups=EventListing.Groups(list,!eventsMyView&&!eventsCalendarView&&eventsFilter==0,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        foreach(var group in groups)
        {
            var e=group.Event;
            var date=DateTimeOffset.FromUnixTimeMilliseconds(e.StartAt).LocalDateTime.Date;
            var heading=EventListing.DayHeading(date,DateTime.Today);
            if(heading!=previous){EventSectionHeading(heading,FontAwesomeIcon.CalendarDay);previous=heading;}
            var expanded=e.SeriesId is not null&&eventsExpandedSeries.Contains(e.SeriesId);
            DrawEventRow(e,session,group.Dates.Length,expanded);
            if(e.SeriesId is not null&&eventsExpandedSeries.Contains(e.SeriesId)&&group.Dates.Length>1)DrawEventSeriesDates(group);
        }
    }
    private void DrawEventActionsMenu(CharacterSession session,CommunityEvent e,string id,Vector2 anchor,bool upwards=false)
    {
        ImGui.SetNextWindowPos(anchor,ImGuiCond.Always,new Vector2(1,upwards?1:0));ImGui.SetNextWindowSize(new Vector2(Ui(210),0));
        using var style=ImRaii.PushStyle(ImGuiStyleVar.PopupRounding,0).Push(ImGuiStyleVar.PopupBorderSize,Ui(1)).Push(ImGuiStyleVar.WindowPadding,UiVec(16,14)).Push(ImGuiStyleVar.ItemSpacing,UiVec(8,8));
        using var colour=ImRaii.PushColor(ImGuiCol.PopupBg,CardBg).Push(ImGuiCol.Border,AccentInk(.35f));
        using var popup=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Popup(id));if(!popup)return;
        if(ImGui.MenuItem("View event"))OpenEventDetails(e);
        if(ImGui.MenuItem("Share event")){eventSelected=e;eventShareOpen=true;}
        if(ImGui.MenuItem("Add to calendar"))ExportEventCalendar(e);
        if(CanManageEventConnection(session,e))
        {
            ImGui.Separator();
            if(ImGui.MenuItem("Connect Watch Party")){eventSelected=e;eventConnectOpen=true;eventsError=null;}
        }
        if(e.OwnerId==session.AccountId)
        {
            ImGui.Separator();if(ImGui.MenuItem("Edit event"))OpenEventEditor(e,false);
            if(ImGui.MenuItem("Duplicate event"))OpenEventEditor(e,true);
            if(!e.Cancelled&&ImGui.MenuItem("Cancel event")){eventSelected=e;eventCancelOpen=true;eventCancelFuture=false;}
        }
#if DEBUG
#endif
    }
    private void DrawEventsCalendar(CommunityEvent[] list)
    {
        // Apply after the selection has settled, since selecting a day can resize the calendar.
        var scrollToSelectedEvents=eventsScrollToSelectedDay;
        eventsScrollToSelectedDay=false;
        var width=ImGui.GetContentRegionAvail().X;var origin=ImGui.GetCursorScreenPos();
        if(AlphaChatButton("eventPrev","",FontAwesomeIcon.ChevronLeft,Ui(30),Ui(30)))ShiftEventCalendar(-1);
        ImGui.SameLine();DrawSocialHeading(eventsCalendarMonth.ToString("MMMM yyyy"),1.15f);ImGui.SameLine();
        if(AlphaChatButton("eventNext","",FontAwesomeIcon.ChevronRight,Ui(30),Ui(30)))ShiftEventCalendar(1);
        if(width>Ui(490))ImGui.SetCursorScreenPos(origin+new Vector2(width-Ui(246),0));
        if(AlphaChatButton("eventToday","Today",FontAwesomeIcon.CalendarDay,Ui(80),Ui(30))){eventsCalendarMonth=new(DateTime.Today.Year,DateTime.Today.Month,1);eventsSelectedDay=DateTime.Today;}
        ImGui.SameLine(0,Ui(8));var view=eventsWeekView?1:0;EventSegments("calendarMode",["Month","Week"],ref view,Ui(158),30);eventsWeekView=view==1;
        ImGui.Spacing();
        var first=eventsWeekView?eventsSelectedDay??(eventsCalendarMonth.Month==DateTime.Today.Month&&eventsCalendarMonth.Year==DateTime.Today.Year?DateTime.Today:eventsCalendarMonth):eventsCalendarMonth;
        var offset=((int)first.DayOfWeek+6)%7;first=first.AddDays(-offset);
        var rows=eventsWeekView?1:(int)Math.Ceiling((offset+DateTime.DaysInMonth(eventsCalendarMonth.Year,eventsCalendarMonth.Month))/7f);
        var available=ImGui.GetContentRegionAvail().Y-Ui(35)-(eventsSelectedDay is null?0:Ui(172));var h=eventsWeekView?Ui(220):Math.Clamp((available-Ui(32))/rows,Ui(68),Ui(94));
        var grid=ImGui.GetCursorScreenPos();var cellWidth=width/7;var draw=ImGui.GetWindowDrawList();
        draw.AddRectFilled(grid,grid+new Vector2(width,Ui(32)+rows*h),ImGui.GetColorU32(FrameBg),Ui(10));
        draw.AddRect(grid,grid+new Vector2(width,Ui(32)+rows*h),ImGui.GetColorU32(AccentInk(.35f)),Ui(10));
        var days=new[]{"Mon","Tue","Wed","Thu","Fri","Sat","Sun"};
        for(var col=0;col<7;col++)
        {
            var text=ImGui.CalcTextSize(days[col]);draw.AddText(grid+new Vector2(cellWidth*col+(cellWidth-text.X)*.5f,(Ui(32)-text.Y)*.5f),ImGui.GetColorU32(MutedText),days[col]);
        }
        var grouped=list.GroupBy(e=>DateTimeOffset.FromUnixTimeMilliseconds(e.StartAt).LocalDateTime.Date).ToDictionary(g=>g.Key,g=>g.ToArray());
        for(var row=0;row<rows;row++)for(var col=0;col<7;col++)
        {
            var date=first.AddDays(row*7+col);var p=grid+new Vector2(col*cellWidth,Ui(32)+row*h);var size=new Vector2(cellWidth,h);
            using var id=ImRaii.PushId(date.ToString("yyyyMMdd"));
            if(eventsSelectedDay==date)draw.AddRectFilled(p+UiVec(1,1),p+size-UiVec(1,1),ImGui.GetColorU32(AccentInk(.12f)));
            draw.AddRect(p,p+size,ImGui.GetColorU32(AccentInk(.18f)));
            if(eventsSelectedDay==date||date==DateTime.Today)draw.AddRect(p+UiVec(1,1),p+size-UiVec(1,1),ImGui.GetColorU32(AccentHover),Ui(3));
            ImGui.SetCursorScreenPos(p+UiVec(5,3));if(ImGui.InvisibleButton("##calendarDate",new Vector2(cellWidth-Ui(10),Ui(24))))eventsSelectedDay=date;
            if(ImGui.IsItemHovered())ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            draw.AddText(p+UiVec(8,7),ImGui.GetColorU32(date==DateTime.Today?AccentHover:date.Month==eventsCalendarMonth.Month?Vector4.One:MutedText),date.Day.ToString());
            var entries=grouped.GetValueOrDefault(date)??[];var capacity=Math.Max(0,(int)((h-Ui(29))/Ui(38)));
            var shown=Math.Min(entries.Length,Math.Max(0,(int)((h-Ui(29)-(entries.Length>capacity?Ui(22):0))/Ui(38))));
            for(var i=0;i<shown;i++)
            {
                var e=entries[i];var pill=p+UiVec(5,31+i*38);var pillSize=new Vector2(cellWidth-Ui(10),Ui(35));var ink=EventColour(e);
                draw.AddRectFilled(pill,pill+pillSize,ImGui.GetColorU32(new Vector4(ink.X,ink.Y,ink.Z,.22f)),Ui(4));
                draw.AddRect(pill,pill+pillSize,ImGui.GetColorU32(new Vector4(ink.X,ink.Y,ink.Z,.5f)),Ui(4));
                ImGui.SetCursorScreenPos(pill);if(ImGui.InvisibleButton("##calendarEvent"+e.Id,pillSize)){eventsSelectedDay=date;OpenEventDetails(e);}
                using(UiFonts.PushRelative(.76f))
                    draw.AddText(pill+UiVec(5,3),ImGui.GetColorU32(e.Adult?EventOrange:MutedText),FitPartyTextToWidth(DateTimeOffset.FromUnixTimeMilliseconds(e.StartAt).ToLocalTime().ToString("HH:mm")+(e.Adult?" · 18+":""),pillSize.X-Ui(10)));
                using(UiFonts.PushRelative(.82f))
                    draw.AddText(pill+UiVec(5,18),ImGui.GetColorU32(Hex(0xE2E7EF)),FitPartyTextToWidth(e.Title,pillSize.X-Ui(10)));
                if(ImGui.IsItemHovered()){ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);DrawEventCalendarTooltip(e);}
            }
            if(entries.Length>shown)
            {
                ImGui.SetCursorScreenPos(p+UiVec(6,31+shown*38));
                if(ImGui.SmallButton($"+{entries.Length-shown} more"))
                {eventsSelectedDay=date;eventsScrollToSelectedDay=true;}
            }
        }
        ImGui.SetCursorScreenPos(grid);ImGui.Dummy(new Vector2(width,Ui(32)+rows*h));ImGui.Spacing();
        DrawSocialHelper("Times shown in your local time zone · Select a day to see its events.");
        if(eventsSelectedDay is { } selected&&CurrentSession is { } session)
        {
            EventSectionHeading("Selected day · "+selected.ToString("ddd d MMM"),FontAwesomeIcon.CalendarDay);
            if(scrollToSelectedEvents)ImGui.SetScrollHereY(0f);
            var selectedEvents=list.Where(e=>DateTimeOffset.FromUnixTimeMilliseconds(e.StartAt).LocalDateTime.Date==selected).ToArray();
            if(selectedEvents.Length==0)DrawSocialHelper("No events on this day.");
            foreach(var e in selectedEvents)DrawEventRow(e,session);
        }
    }
    private void ShiftEventCalendar(int direction)
    {
        var target=eventsWeekView?(eventsSelectedDay??(eventsCalendarMonth.Year==DateTime.Today.Year&&eventsCalendarMonth.Month==DateTime.Today.Month?DateTime.Today:eventsCalendarMonth)).AddDays(direction*7):eventsCalendarMonth.AddMonths(direction);
        var month=new DateTime(target.Year,target.Month,1);
        if(new DateTimeOffset(month.AddDays(-7))<DateTimeOffset.UtcNow.AddDays(-366)||new DateTimeOffset(month.AddMonths(1).AddDays(7))>DateTimeOffset.UtcNow.AddDays(367))return;
        eventsCalendarMonth=month;eventsSelectedDay=eventsWeekView?target:null;
    }
}

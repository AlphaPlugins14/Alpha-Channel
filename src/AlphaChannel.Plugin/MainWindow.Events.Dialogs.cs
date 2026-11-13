using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Diagnostics;
using System.Globalization;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private EventDraft eventDraft=new();
    private CommunityEvent? eventEditing;
    private bool eventRepeats,eventIncludesParty,eventRsvpPublic=true,eventRsvpReminders=true,eventRsvpFuture,eventCancelFuture;
    private int eventRepeatFrequency=1,eventRepeatInterval=1,eventRepeatCount=8,eventRepeatEnd;
    private bool[] eventWeekdays=new bool[7];
    private string eventRepeatUntil="",eventDetailsId="";
    private string? eventFormError;
    private EventAttendees? eventAttendeeData;
    private bool eventAttendeeBusy;
    private static readonly string[] EventFrequencies=["daily","weekly","monthly"];
    private static readonly string[] EventIcons=["Film","Music","Star","Question","Gamepad","Users","Calendar","Heart"];
    private void OpenEventEditor(CommunityEvent? e,bool duplicate)
    {
        eventEditing=duplicate?null:e;eventFormError=null;eventsError=null;eventActionNotice=null;
        eventDraft=e is null?new():new EventDraft{Title=duplicate?e.Title+" (copy)":e.Title,Icon=e.Icon,Colour=e.Colour,Website=e.Website,
            Description=e.Description,Category=e.Category,StartLocal=e.StartLocal,EndLocal=e.EndLocal,Timezone=e.Timezone,World=e.World,
            DataCentre=e.DataCentre,Location=e.Location,Adult=e.Adult,HostAccountId=e.HostAccountId,Recurrence=e.Recurrence,Version=duplicate?0:e.Version,
            AllowSeriesRsvp=e.AllowSeriesRsvp,OfferJoinPrompt=e.OfferJoinPrompt,ShowJoinButton=e.ShowJoinButton,ShareToDiscord=duplicate||e.ShareToDiscord};
        if(!EventCategories.Contains(eventDraft.Category))eventDraft.Category="Other";
        if(duplicate){eventDraft.StartLocal=DateTime.Today.AddDays(1).ToString("yyyy-MM-ddT20:00");eventDraft.EndLocal=DateTime.Today.AddDays(1).ToString("yyyy-MM-ddT23:00");}
        eventRepeats=eventDraft.Recurrence is not null;eventIncludesParty=eventDraft.HostAccountId is not null;
        if(eventDraft.Recurrence is { } recurrence)
        {
            eventRepeatFrequency=Math.Max(0,Array.IndexOf(EventFrequencies,recurrence.Frequency));eventRepeatInterval=recurrence.Interval;
            eventRepeatCount=recurrence.Count==0?8:recurrence.Count;eventRepeatUntil=recurrence.Until??"";
            eventRepeatEnd=recurrence.Until is not null?1:recurrence.Count==0?2:0;
            eventWeekdays=Enumerable.Range(0,7).Select(i=>recurrence.Weekdays.Contains(i)).ToArray();
        }
        else {eventRepeatFrequency=1;eventRepeatInterval=1;eventRepeatCount=8;eventRepeatEnd=0;eventRepeatUntil=DateTime.Today.AddMonths(3).ToString("yyyy-MM-dd");eventWeekdays=new bool[7];eventWeekdays[(int)DateTime.Today.AddDays(1).DayOfWeek]=true;}
        if(e is null && Plugin.ObjectTable.LocalPlayer is { } player)
        {
            try{if(FfxivEventWorlds.Find(player.HomeWorld.Value.Name.ToString()) is { } home){eventDraft.World=home.Name;eventDraft.DataCentre=home.Centre;}}catch{}
        }
        eventDetailsGeneration++;eventDetailsLoading=false;eventEditorOpen=true;eventDialogOpen=false;eventEditorTab=0;eventRsvpOptionsOpen=false;
        if(CurrentSession is { } session&&friendsDirty&&!friendsLoading)RefreshFriends(session.Token);
    }
    private void DrawEventOverlays()
    {
        if(CurrentSession is not { } session)return;
        if(eventConnectOpen&&eventSelected is { } connectEvent)
            DrawEventWindow("eventConnect","Connect Watch Party",UiVec(540,320),()=>DrawEventConnectConfirmation(session,connectEvent),()=>eventConnectOpen=false,()=>DrawEventConnectFooter(session,connectEvent));
        else if(eventEditorOpen) DrawEventWindow("eventEditor",eventEditing is null?"Create event":"Edit event",UiVec(760,740),()=>DrawEventEditor(session),()=>eventEditorOpen=false,DrawEventEditorFooter);
        else if(eventRsvpOptionsOpen&&eventSelected is { } rsvpEvent)
            DrawEventWindow("eventRsvpSettings","Your RSVP",UiVec(480,370),()=>DrawEventRsvpSettings(rsvpEvent),()=>eventRsvpOptionsOpen=false,()=>DrawEventRsvpSettingsFooter(rsvpEvent));
        else if(eventAttendeesOpen&&eventSelected is { } attendeesEvent)
            DrawEventWindow("eventAttendees","People going · "+attendeesEvent.GoingCount,UiVec(520,Math.Clamp(210+(eventAttendeeData?.Attendees.Length??Math.Min(attendeesEvent.GoingCount,5))*54,264,560)),()=>DrawEventAttendees(session,attendeesEvent),()=>eventAttendeesOpen=false,DrawEventAttendeesFooter);
        else if(eventShareOpen&&eventSelected is { } shareEvent)
            DrawEventWindow("eventShare","Share event",UiVec(520,480),()=>DrawEventShare(session,shareEvent),()=>eventShareOpen=false);
        else if(eventCancelOpen&&eventSelected is { } cancelEvent)
            DrawEventWindow("eventCancel","Cancel event",UiVec(480,300),()=>DrawEventCancellation(cancelEvent),()=>eventCancelOpen=false);
        else if(eventDialogOpen&&eventSelected is { } e)
            DrawEventWindow("eventDetails","Event details",UiVec(680,680),()=>DrawEventDetails(session,e),()=>eventDialogOpen=false,()=>DrawEventDetailsFooter(session,e));
    }
    private void DrawEventWindow(string id,string title,Vector2 preferred,Action draw,Action close,Action? footer=null)
    {
        var parent=ImGui.GetWindowPos();var size=ImGui.GetWindowSize();var panelSize=Vector2.Min(preferred,size-UiVec(36,36));var position=parent+(size-panelSize)*.5f;
        ImGui.SetNextWindowPos(parent,ImGuiCond.Always);ImGui.SetNextWindowSize(size,ImGuiCond.Always);ImGui.SetNextWindowBgAlpha(0);
        const ImGuiWindowFlags flags=ImGuiWindowFlags.NoTitleBar|ImGuiWindowFlags.NoResize|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoScrollbar|
            ImGuiWindowFlags.NoScrollWithMouse|ImGuiWindowFlags.NoCollapse|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoDocking|ImGuiWindowFlags.NoBackground;
        if(!BeginWindowOverlay("##"+id+"Overlay",flags)){ImGui.End();return;}
        using(var eventFontScope=id.StartsWith("event",StringComparison.Ordinal)?BeginUiFonts():null)
        {
        ImGui.GetWindowDrawList().AddRectFilled(parent,parent+size,ImGui.GetColorU32(new Vector4(0,0,0,.58f)));
        // Backdrop input belongs to BeginWindowOverlay's shared window dragging.
        // Only explicit dismissal should close an event dialog.
        if(!ImGui.IsPopupOpen("",ImGuiPopupFlags.AnyPopupId|ImGuiPopupFlags.AnyPopupLevel)&&
            ImGui.IsKeyPressed(ImGuiKey.Escape))close();
        ImGui.SetCursorScreenPos(position);
        using(ImRaii.PushStyle(ImGuiStyleVar.ChildRounding,Ui(14)).Push(ImGuiStyleVar.ChildBorderSize,Ui(1)).Push(ImGuiStyleVar.WindowPadding,UiVec(24,20))
            .Push(ImGuiStyleVar.FrameRounding,Ui(8)).Push(ImGuiStyleVar.FrameBorderSize,Ui(1)).Push(ImGuiStyleVar.ItemSpacing,UiVec(8,6)).Push(ImGuiStyleVar.FramePadding,UiVec(10,7))
            .Push(ImGuiStyleVar.PopupBorderSize,Ui(1)).Push(ImGuiStyleVar.PopupRounding,Ui(8)))
        using(ImRaii.PushColor(ImGuiCol.ChildBg,CardBg).Push(ImGuiCol.Border,AccentInk(.65f)).Push(ImGuiCol.FrameBg,FrameBg).Push(ImGuiCol.Button,FrameBg).Push(ImGuiCol.PopupBg,FrameBg))
        using(var panel=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##"+id+"Panel",panelSize,true,ImGuiWindowFlags.AlwaysUseWindowPadding|ImGuiWindowFlags.NoScrollbar)))
        {
            if(panel)
            {
                var origin=ImGui.GetCursorScreenPos();var width=ImGui.GetContentRegionAvail().X;
                DrawSocialHeading(title,1.2f);
                ImGui.SetCursorScreenPos(origin+new Vector2(width-Ui(30),0));
                if(AlphaChatButton("closeEventWindow","",FontAwesomeIcon.Times,Ui(30),Ui(30)))close();
                if(id=="eventDetails"&&eventSelected is { } refreshEvent)
                {
                    ImGui.SetCursorScreenPos(origin+new Vector2(width-Ui(68),0));
                    using(UiDisabled.Scope(eventDetailsLoading))if(AlphaChatButton("refreshEventDialog","",FontAwesomeIcon.SyncAlt,Ui(30),Ui(30)))ReloadEventDetails(refreshEvent.Id);
                    if(ImGui.IsItemHovered())UiFonts.SetTooltip("Refresh event");
                }
                ImGui.SetCursorScreenPos(origin+UiVec(0,42));
                var footerHeight=footer is null?0:Ui(58);
                if (footer is not null && id == "friendDirectoryEditor")
                {
                    if (directoryOwn is not null) footerHeight += Ui(directoryRemoveConfirm ? 50 : 24);
                    if (directoryEditorError is not null) footerHeight += ImGui.CalcTextSize(directoryEditorError, false, width).Y + Ui(6);
                }
                if(footer is not null&&id.StartsWith("event",StringComparison.Ordinal))
                {
                    if(eventsError is not null)footerHeight+=ImGui.CalcTextSize(eventsError,false,width).Y+Ui(6);
                    if(id=="eventEditor"&&eventFormError is not null)footerHeight+=ImGui.CalcTextSize(eventFormError,false,width).Y+Ui(6);
                    if(id=="eventDetails"&&eventActionNotice is not null)footerHeight+=ImGui.GetTextLineHeightWithSpacing();
                }
                var footerY=position.Y+panelSize.Y-Ui(20)-footerHeight;
                var bodyHeight=footer is null?ImGui.GetContentRegionAvail().Y:MathF.Max(Ui(80),footerY-ImGui.GetCursorScreenPos().Y-Ui(8));
                using(ImRaii.PushColor(ImGuiCol.ChildBg,Vector4.Zero))
                using(ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,Vector2.Zero))
                using(var body=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##"+id+"Body",new Vector2(width,bodyHeight),false)))if(body)draw();
                if(footer is not null){ImGui.SetCursorScreenPos(new Vector2(origin.X,footerY));ImGui.Separator();ImGui.Spacing();footer();}
            }
        }
        }
        ImGui.End();
    }
    private static void EventInput(string label,ref string value,int max,string? hint=null)
    {ImGui.TextUnformatted(label);ImGui.SetNextItemWidth(-1);if(hint is null)ImGui.InputText("##"+label,ref value,max);else ImGui.InputTextWithHint("##"+label,hint,ref value,max);}
    private int eventEditorTab;
    private bool eventRsvpOptionsOpen;
    private void EventDateTimeInput(string label,ref string value,bool showLabel=true)
    {
        if(showLabel)ImGui.TextUnformatted(label);
        var parts=value.Split('T');var date=parts[0];var time=parts.Length>1?parts[1]:"";
        var width=ImGui.GetContentRegionAvail().X;
        EventDatePicker(label,ref date,MathF.Max(Ui(90),width-Ui(84)));
        ImGui.SameLine(0,Ui(8));ImGui.SetNextItemWidth(Ui(76));
        if(ImGui.InputTextWithHint("##"+label+"Time","HH:MM",ref time,5))value=date+"T"+time;
        else if(date!=parts[0])value=date+"T"+time;
        if(label=="Starts"&&date!=parts[0]&&DateTime.TryParseExact(parts[0],"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var oldDate)&&
            DateTime.TryParseExact(date,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var newDate)&&
            DateTime.TryParseExact(eventDraft.EndLocal,"yyyy-MM-ddTHH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out var end))
            eventDraft.EndLocal=end.AddDays((newDate-oldDate).Days).ToString("yyyy-MM-ddTHH:mm");
    }
    private void DrawEventEditor(CharacterSession session)
    {
        using var editorSpacing=ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing,UiVec(8,5));
        EventSegments("editorTab",["Event details","Schedule & repeats","Watch Party"],ref eventEditorTab,ImGui.GetContentRegionAvail().X,36);
        ImGui.Spacing();using var disabled=UiDisabled.Scope(eventsMutationBusy);
        if(eventEditorTab==1){DrawEventScheduleForm();return;}
        if(eventEditorTab==2){DrawEventHostForm(session);return;}
        DrawSocialHelper("Public event · Give people something to look forward to.");
        var wide=ImGui.GetContentRegionAvail().X>=Ui(560);
        if(wide&&ImGui.BeginTable("##eventDetailsColumns",2,ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextRow();ImGui.TableSetColumnIndex(0);DrawEventIdentityForm();
            ImGui.TableSetColumnIndex(1);DrawEventWhenWhereForm();ImGui.EndTable();
        }
        else {DrawEventIdentityForm();DrawEventWhenWhereForm();}
        ImGui.Spacing();
        using(UiDisabled.Scope(eventEditing?.ShareToDiscord==true))
            EventCheckbox("Share my event to the Alpha Channel Discord",ref eventDraft.ShareToDiscord);
        DrawSocialHelperWrapped(eventEditing?.ShareToDiscord==true?"Discord sharing cannot be turned off after publication.":
            "Shares event details, attendance and a Quick View Code. Once saved with sharing enabled, it cannot be disabled.");
    }
    private void DrawEventIdentityForm()
    {
        EventSectionHeading("Event details",FontAwesomeIcon.CalendarAlt);
        EventInput("Event name",ref eventDraft.Title,100,"Give your event a name…");
        ImGui.TextUnformatted("Event icon");
        var available=ImGui.GetContentRegionAvail().X;var iconWidth=MathF.Min(Ui(36),(available-Ui(7*4))/8);
        for(var i=0;i<EventIcons.Length;i++)
        {
            if(i>0)ImGui.SameLine(0,Ui(4));
            if(AlphaChatButton("chooseEventIcon"+i,"",EventIcon(EventIcons[i]),iconWidth,Ui(34),primary:eventDraft.Icon==EventIcons[i]))eventDraft.Icon=EventIcons[i];
            if(ImGui.IsItemHovered())UiFonts.SetTooltip(EventIcons[i]);
        }
        ImGui.TextUnformatted("Accent colour");
        var swatches=new[]{"#3B82F6","#F59E0B","#9966FA","#16B8B8","#E65C9C","#22C55E"};
        for(var i=0;i<swatches.Length;i++)
        {
            if(i>0)ImGui.SameLine(0,Ui(8));var hex=swatches[i];var rgb=Convert.ToInt32(hex[1..],16);var colour=new Vector4((rgb>>16&255)/255f,(rgb>>8&255)/255f,(rgb&255)/255f,1);
            var p=ImGui.GetCursorScreenPos();
            if(ImGui.ColorButton("##eventSwatch"+i,colour,ImGuiColorEditFlags.NoTooltip,UiVec(24,24)))eventDraft.Colour=hex;
            if(eventDraft.Colour.Equals(hex,StringComparison.OrdinalIgnoreCase))ImGui.GetWindowDrawList().AddRect(p-UiVec(2,2),p+UiVec(26,26),ImGui.GetColorU32(Vector4.One),Ui(5));
        }
        ImGui.SameLine();if(ImGui.SmallButton("Custom"))ImGui.OpenPopup("##eventCustomColour");
        using(var popup=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Popup("##eventCustomColour")))if(popup)
        {
            var rgb=Convert.ToInt32(eventDraft.Colour.TrimStart('#'),16);var colour=new Vector3((rgb>>16&255)/255f,(rgb>>8&255)/255f,(rgb&255)/255f);
            if(ImGui.ColorPicker3("##eventColourPicker",ref colour,ImGuiColorEditFlags.NoSidePreview))eventDraft.Colour=$"#{(int)(colour.X*255):X2}{(int)(colour.Y*255):X2}{(int)(colour.Z*255):X2}";
        }
        EventCategoryPicker("Category",ref eventDraft.Category);
        EventInput("Website (optional)",ref eventDraft.Website,500,"https://…");
        ImGui.TextUnformatted("Description");ImGui.InputTextMultiline("##eventDescription",ref eventDraft.Description,1500,new Vector2(-1,Ui(80)));
        DrawSocialHelper($"{eventDraft.Description.Length}/1500");
    }
    private void DrawEventWhenWhereForm()
    {
        EventSectionHeading("When and where",FontAwesomeIcon.MapMarkerAlt);
        EventDateTimeInput("Starts",ref eventDraft.StartLocal);
        EventDateTimeInput("Ends",ref eventDraft.EndLocal);
        EventTimezonePicker();
        DrawSocialHelper("Viewers see times in their local time zone.");
        EventWorldPicker();
        EventInput("Location",ref eventDraft.Location,160,"In-game location");
        ImGui.Spacing();EventCheckbox("18+ event · Adults only",ref eventDraft.Adult);
    }
    private void DrawEventScheduleForm()
    {
        EventSectionHeading("Schedule",FontAwesomeIcon.CalendarAlt);
        if(ImGui.BeginTable("##eventDates",2,ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextRow();ImGui.TableSetColumnIndex(0);ImGui.TextUnformatted("Starts");
            ImGui.TableSetColumnIndex(1);ImGui.TextUnformatted("Ends");
            ImGui.TableNextRow();ImGui.TableSetColumnIndex(0);EventDateTimeInput("Starts",ref eventDraft.StartLocal,showLabel:false);
            ImGui.TableSetColumnIndex(1);EventDateTimeInput("Ends",ref eventDraft.EndLocal,showLabel:false);ImGui.EndTable();
        }
        EventTimezonePicker();ImGui.Spacing();ImGui.Separator();
        EventSectionHeading("Recurrence",FontAwesomeIcon.SyncAlt);
        if(eventEditing?.SeriesId is not null)
        {
            EventCheckbox("Apply changes to this and future occurrences",ref eventDraft.Future);
            if(!eventDraft.Future)DrawSocialHelper("The repeat schedule is shared. Select future occurrences to change it.");
        }
        var canChange=eventEditing?.SeriesId is null||eventDraft.Future;
        using(UiDisabled.Scope(!canChange))
        {
            DrawSettingsToggle("##eventRepeatsToggle","Recurring event",ref eventRepeats);
            if(!eventRepeats){ImGui.Spacing();DrawSocialHelper("One date. One plan. You can enable repeats any time.");return;}
            ImGui.Spacing();ImGui.TextUnformatted("Repeat every");
            ImGui.SetNextItemWidth(Ui(80));ImGui.InputInt("##eventInterval",ref eventRepeatInterval);eventRepeatInterval=Math.Clamp(eventRepeatInterval,1,12);
            ImGui.SameLine();ImGui.SetNextItemWidth(Ui(180));
            if(UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##eventRepeatFrequency",new[]{"day(s)","week(s)","month(s)"}[eventRepeatFrequency])))
            {for(var i=0;i<3;i++)if(ImGui.Selectable(new[]{"day(s)","week(s)","month(s)"}[i],eventRepeatFrequency==i))eventRepeatFrequency=i;ImGui.EndCombo();}
            if(eventRepeatFrequency==1)
            {
                ImGui.TextUnformatted("Repeat on");var dayWidth=MathF.Min(Ui(64),(ImGui.GetContentRegionAvail().X-Ui(6*6))/7);
                string[] days=["Sun","Mon","Tue","Wed","Thu","Fri","Sat"];
                for(var k=0;k<7;k++)
                {
                    var i=(k+1)%7;if(k>0)ImGui.SameLine(0,Ui(6));
                    using(ImRaii.PushColor(ImGuiCol.Button,eventWeekdays[i]?AccentInk(.4f):FrameBg).Push(ImGuiCol.Border,eventWeekdays[i]?AccentHover:AccentInk(.35f)))
                        if(ImGui.Button(days[i]+"##eventWeekday"+i,new Vector2(dayWidth,Ui(32))))eventWeekdays[i]=!eventWeekdays[i];
                }
            }
            ImGui.TextUnformatted("Ends");ImGui.SetNextItemWidth(Ui(200));
            if(UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##repeatEnd",new[]{"After a number of dates","On a date","Never"}[eventRepeatEnd])))
            {for(var i=0;i<3;i++)if(ImGui.Selectable(new[]{"After a number of dates","On a date","Never"}[i],eventRepeatEnd==i))eventRepeatEnd=i;ImGui.EndCombo();}
            if(eventRepeatEnd==0){ImGui.SameLine();ImGui.SetNextItemWidth(Ui(80));ImGui.InputInt("##eventOccurrences",ref eventRepeatCount);eventRepeatCount=Math.Clamp(eventRepeatCount,1,60);ImGui.SameLine();DrawSocialHelper("dates");}
            if(eventRepeatEnd==1){ImGui.SameLine();EventDatePicker("Repeat until",ref eventRepeatUntil,Ui(200));}
            ImGui.Spacing();EventCheckbox("Allow RSVPs to the whole series",ref eventDraft.AllowSeriesRsvp);
            DrawSocialHelper("People can still change their attendance for individual dates.");ImGui.Spacing();DrawEventRepeatPreview();
        }
    }
    private void DrawEventHostForm(CharacterSession session)
    {
        EventSectionHeading("Linked Watch Party",FontAwesomeIcon.Users);
        DrawSettingsToggle("##eventPartyToggle","Include a Watch Party",ref eventIncludesParty);
        ImGui.Spacing();DrawSocialHelperWrapped("Give attendees a room to watch, listen or play together when your event starts.");
        if(!eventIncludesParty)return;
        eventDraft.HostAccountId??=session.AccountId;
        var friend=friends.FirstOrDefault(f=>f.AccountId==eventDraft.HostAccountId);var own=eventDraft.HostAccountId==session.AccountId;
        var name=own?session.DisplayName+" (you)":friend?.DisplayName??eventEditing?.HostName??"Choose a host";
        ImGui.Spacing();ImGui.TextUnformatted("Hosting account");ImGui.SetNextItemWidth(-1);
        if(UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##eventHost",name)))
        {
            if(ImGui.Selectable(session.DisplayName+" (you)",own))eventDraft.HostAccountId=session.AccountId;
            foreach(var f in friends)if(ImGui.Selectable(f.DisplayName,eventDraft.HostAccountId==f.AccountId))eventDraft.HostAccountId=f.AccountId;
            ImGui.EndCombo();
        }
        using(ImRaii.PushColor(ImGuiCol.Text,MutedText))DrawSocialHelperWrapped("To assign someone else as the room host they must be on your Alpha Channel friends list");
        DrawRoomCapacityDetails(eventDraft.HostAccountId!,upgrade:false);
        ImGui.Spacing();
        var p=ImGui.GetCursorScreenPos();DrawAvatarAt(p,own?session.AvatarIcon:friend?.AvatarIcon,own?session.AvatarColorHex:friend?.AvatarColorHex,Ui(40),own?session.AvatarImageUrl:friend?.AvatarImageUrl);
        ImGui.SetCursorScreenPos(p+UiVec(52,2));ImGui.TextUnformatted(name);
        ImGui.SetCursorScreenPos(p+UiVec(52,24));EventBadge(own||eventEditing?.HostAccountId==eventDraft.HostAccountId&&eventEditing.HostState=="accepted"?"Host confirmed":"Confirmation needed",own?EventGreen:EventOrange);
        ImGui.SetCursorScreenPos(p+UiVec(0,64));
        DrawSocialHelperWrapped(own?"Your public room becomes joinable from this event while the event is live. You choose when to start hosting.":"This friend will receive a notification to accept their role as watch party host for your event.");
        ImGui.Spacing();ImGui.Separator();EventSectionHeading("Attendee options",FontAwesomeIcon.Bell);
        EventCheckbox("Offer a join prompt when the room opens",ref eventDraft.OfferJoinPrompt);
        EventCheckbox("Show Join Watch Party on the live event",ref eventDraft.ShowJoinButton);
        ImGui.Spacing();DrawSocialHelper("Joining is optional. Attendees are never joined automatically.");
        ImGui.Spacing();DrawEventHostingInfo();
    }
    private void DrawEventEditorFooter()
    {
        EventNotice(eventFormError,true);EventNotice(eventsError,true);
        var width=ImGui.GetContentRegionAvail().X;var p=ImGui.GetCursorScreenPos();
        if(AlphaChatButton("eventDiscard","Cancel",FontAwesomeIcon.Times,Ui(110)))eventEditorOpen=false;
        ImGui.SetCursorScreenPos(p+new Vector2(MathF.Max(Ui(118),width-Ui(180)),0));
        using(UiDisabled.Scope(eventsMutationBusy))
            if(AlphaChatButton("eventSave",eventsMutationBusy?"Saving…":eventEditing is null?"Create event":"Save changes",FontAwesomeIcon.CalendarPlus,Ui(180),primary:true))SaveEventEditor();
        if(eventsError is not null&&eventEditing is { } reload&&ImGui.SmallButton("Reload latest event (discard edits)"))ReloadEventDetails(reload.Id,true);
    }
    private void SaveEventEditor()
    {
        eventFormError=null;
        if(string.IsNullOrWhiteSpace(eventDraft.Title)||string.IsNullOrWhiteSpace(eventDraft.Description)||string.IsNullOrWhiteSpace(eventDraft.Location)||string.IsNullOrWhiteSpace(eventDraft.World))
        {eventFormError="Add a name, description, world and location.";return;}
        if(eventDraft.World=="Online")eventDraft.DataCentre="";
        else if(FfxivEventWorlds.Find(eventDraft.World) is { } world){eventDraft.World=world.Name;eventDraft.DataCentre=world.Centre;}
        else {eventFormError="Choose a published FFXIV world from the list, or Online · Any world.";eventEditorTab=0;return;}
        if(!DateTime.TryParseExact(eventDraft.StartLocal,"yyyy-MM-ddTHH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out var start)||
            !DateTime.TryParseExact(eventDraft.EndLocal,"yyyy-MM-ddTHH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out var end)||end<=start)
        {eventFormError="Check the dates and 24-hour times, with the end after the start.";return;}
        if(eventDraft.Colour.Length!=7||!int.TryParse(eventDraft.Colour.AsSpan(1),NumberStyles.HexNumber,null,out _)||eventDraft.Colour[0]!='#')
        {eventFormError="Use a highlight colour such as #3B82F6.";return;}
        if(eventDraft.Website.Length>0&&(!Uri.TryCreate(eventDraft.Website,UriKind.Absolute,out var website)||website.Scheme is not ("http" or "https")||!string.IsNullOrEmpty(website.UserInfo)||eventDraft.Website.Contains('\n')||eventDraft.Website.Contains('\r')))
        {eventFormError="Use a complete http or https website address.";return;}
        eventDraft.HostAccountId=eventIncludesParty?eventDraft.HostAccountId:null;
        eventDraft.Recurrence=eventRepeats?new EventRecurrence(EventFrequencies[eventRepeatFrequency],eventRepeatInterval,Enumerable.Range(0,7).Where(i=>eventWeekdays[i]).ToArray(),eventRepeatEnd==1?eventRepeatUntil:null,eventRepeatEnd==0?eventRepeatCount:0):null;
        if(eventRepeats&&eventRepeatFrequency==1&&!eventWeekdays.Any(x=>x)){eventFormError="Choose at least one repeat day.";return;}
        var draft=System.Text.Json.JsonSerializer.Deserialize<EventDraft>(System.Text.Json.JsonSerializer.Serialize(eventDraft,EventsClient.Json),EventsClient.Json)!;var editing=eventEditing;
        MutateEvent(async s=>{
            if(editing is null)await eventsApi.Create(s.Token,draft,youtubeFeedToken);
            else await eventsApi.Update(s.Token,editing.Id,draft,youtubeFeedToken);
        },()=>
        {
            eventEditorOpen=false;
            if(editing is null)
            {
                currentPage=HomePage.Events;eventsMyView=true;eventsMyTab=1;eventsCalendarView=false;
                eventsSearch="";eventsWorldFilter="";eventsCategoryFilter="";eventsHideAdult=false;eventActionNotice="Event created.";
            }
        });
    }
    private void DrawEventDetails(CharacterSession session,CommunityEvent e)
    {
        if(eventDetailsId!=e.Id){eventDetailsId=e.Id;eventRsvpPublic=!e.MyRsvp||e.PublicAttendance;eventRsvpReminders=!e.MyRsvp||e.Reminders;eventRsvpFuture=false;eventRsvpOptionsOpen=false;}
        var p=ImGui.GetCursorScreenPos();var width=ImGui.GetContentRegionAvail().X;var ink=EventColour(e);
        EventIconTile(p,UiVec(64,64),e.Icon,ink);
        ImGui.SetCursorScreenPos(p+UiVec(80,0));using(PushUiFont(1.3f,true))using(ImRaii.PushColor(ImGuiCol.Text,Hex(0xE2E7EF)))ImGui.TextWrapped(e.Title);
        var badgeY=MathF.Max(Ui(32),ImGui.GetCursorScreenPos().Y-p.Y+Ui(4));
        ImGui.SetCursorScreenPos(p+new Vector2(Ui(80),badgeY));
        if(e.Cancelled)EventBadge("Cancelled",new Vector4(1,.5f,.45f,1));
        else if(EventSchedule.Live(e,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))EventBadge("LIVE NOW",EventGreen);
        else EventBadge(e.Category,ink);
        if(e.Adult){ImGui.SameLine();EventBadge("18+ · Adults only",EventOrange);}
        ImGui.SetCursorScreenPos(p+new Vector2(0,MathF.Max(Ui(78),badgeY+Ui(32))));
        var localStart=DateTimeOffset.FromUnixTimeMilliseconds(e.StartAt).ToLocalTime();var localEnd=DateTimeOffset.FromUnixTimeMilliseconds(e.EndAt).ToLocalTime();
        EventSectionHeading(localStart.ToString("ddd d MMM yyyy")+" · "+localStart.ToString("HH:mm")+" — "+(localStart.Date==localEnd.Date?localEnd.ToString("HH:mm"):localEnd.ToString("ddd d MMM HH:mm")),FontAwesomeIcon.Clock);
        DrawSocialHelper("Your local time · Event time zone: "+e.Timezone);
        DrawSocialHelperWrapped(e.World+" ("+e.DataCentre+") · "+e.Location);
        if(e.Recurrence is { } recurrence){ImGui.Spacing();EventBadge(EventRecurrenceLabel(recurrence)+(recurrence.Until is null?recurrence.Count==0?" · Ongoing":" · "+recurrence.Count+" dates":" · Until "+recurrence.Until),ink);}
        ImGui.Spacing();ImGui.TextWrapped(e.Description);ImGui.Spacing();
        var organiser=ImGui.GetCursorScreenPos();DrawAvatarAt(organiser,e.Organiser.AvatarIcon,e.Organiser.AvatarColorHex,Ui(26),e.Organiser.AvatarImageUrl);
        ImGui.SetCursorScreenPos(organiser+UiVec(36,5));DrawSocialHelper("Organised by");ImGui.SameLine(0,Ui(5));DrawUsername(e.OwnerId,e.Organiser.DisplayName,AccentHover);
        if(ImGui.IsItemClicked()){eventDialogOpen=false;OpenProfilePage(session,e.OwnerId,e.Organiser.DisplayName);}
        if(PatreonBadgeTier(e.OwnerId)>0){ImGui.SameLine(0,Ui(4));DrawAlphaChatSupporterHeart();}
        ImGui.SetCursorScreenPos(organiser+UiVec(0,38));
        if(e.Website.Length>0){if(AlphaChatButton("eventWebsite","Website",FontAwesomeIcon.ExternalLinkAlt,Ui(112),Ui(32)))OpenEventWebsite(e.Website);ImGui.SameLine();}
        if(AlphaChatButton("eventShare","Share",FontAwesomeIcon.ShareAlt,Ui(96),Ui(32)))eventShareOpen=true;
        ImGui.SameLine();if(AlphaChatButton("eventCalendarExport","Add to calendar",FontAwesomeIcon.CalendarPlus,Ui(152),Ui(32)))ExportEventCalendar(e);
        ImGui.Spacing();
        using(ImRaii.PushColor(ImGuiCol.ChildBg,FrameBg).Push(ImGuiCol.Border,AccentInk(.3f)))
        using(ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,UiVec(14,12)).Push(ImGuiStyleVar.ChildRounding,Ui(10)))
        using(var attendance=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##eventAttendance",new Vector2(width,Ui(94)),true,ImGuiWindowFlags.AlwaysUseWindowPadding|ImGuiWindowFlags.NoScrollbar)))
        {
            if(attendance)
            {
                var origin=ImGui.GetCursorScreenPos();DrawSocialHelper($"{e.GoingCount} going");
                ImGui.SetCursorScreenPos(origin+UiVec(0,28));
                var sample=e.AttendeeSample.Take(6).ToArray();
                for(var i=0;i<sample.Length;i++)DrawAvatarAt(origin+UiVec(i*30,28),sample[i].AvatarIcon,sample[i].AvatarColorHex,Ui(28),sample[i].AvatarImageUrl);
                if(sample.Length==0)DrawSocialHelper(e.GoingCount>0?"The host is included. Other attendee names may be private.":"Be the first to make plans.");
                ImGui.SetCursorScreenPos(origin+new Vector2(ImGui.GetWindowContentRegionMax().X-ImGui.GetWindowContentRegionMin().X-Ui(152),Ui(26)));
                if(AlphaChatButton("eventAttendees","View attendees",FontAwesomeIcon.Users,Ui(152),Ui(32))){eventAttendeesOpen=true;eventAttendeeData=null;LoadEventAttendees(e,null);}
            }
        }
        if(e.MyRsvp){EventBadge("Going",EventGreen);ImGui.SameLine();ImGui.TextColored(e.Reminders?AccentHover:MutedText,e.Reminders?"Reminders on":"Reminders muted");}
        if(e.HostAccountId is not null||e.ConnectedHostAccountId is not null)
        {
            EventSectionHeading("Linked Watch Party",FontAwesomeIcon.Users);
            var room=EventRoom(e);var ended=e.EndAt<=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();var pending=e.ConnectedHostAccountId is null&&e.HostState=="pending"&&!ended&&!e.Cancelled;var tall=pending&&e.HostAccountId==session.AccountId||room is not null;
            using(ImRaii.PushColor(ImGuiCol.ChildBg,FrameBg).Push(ImGuiCol.Border,AccentInk(.35f)))
            using(ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,UiVec(14,12)).Push(ImGuiStyleVar.ChildRounding,Ui(10)))
            using(var party=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##eventLinkedParty",new Vector2(width,Ui(tall?134:106)),true,ImGuiWindowFlags.AlwaysUseWindowPadding|ImGuiWindowFlags.NoScrollbar)))
            {
                if(party)
                {
                    var roomHostName=e.ConnectedHostAccountId==e.OwnerId?e.Organiser.DisplayName:e.HostName;
                    ImGui.TextUnformatted((room?.HostDisplayName??roomHostName)+"’s Watch Party");ImGui.SameLine();EventBadge(e.Cancelled?"Cancelled":ended?"Ended":room is not null?"LIVE":e.ConnectedHostAccountId is not null?"Connected":pending?"Pending":e.HostState=="accepted"?"Host confirmed":"Declined",room is not null||e.ConnectedHostAccountId is not null||e.HostState=="accepted"?EventGreen:EventOrange);
                    DrawSocialHelper(e.Cancelled?"This event was cancelled.":ended?"The event has ended. Its room link is no longer available.":pending?"Waiting for the host to accept the invitation.":e.HostState=="declined"&&e.ConnectedHostAccountId is null?"The hosting invitation was declined.":room is not null?RoomCapacityText(room.HostAccountId,room.ViewerCount,includeHost:true):"Not started yet · Join appears while the event and room are live.");
                    if(pending&&e.HostAccountId==session.AccountId)
                    {using(UiDisabled.Scope(eventsMutationBusy)){if(AlphaChatButton("acceptEventHost","Accept hosting",FontAwesomeIcon.Check,Ui(140),Ui(32),primary:true))MutateEvent(s=>eventsApi.Host(s.Token,e.Id,true,youtubeFeedToken));ImGui.SameLine();if(AlphaChatButton("declineEventHost","Decline",FontAwesomeIcon.Times,Ui(90),Ui(32)))MutateEvent(s=>eventsApi.Host(s.Token,e.Id,false,youtubeFeedToken));}}
                    if(room is not null&&e.ShowJoinButton)
                    {
                        if(AlphaChatButton("detailViewRoom","View room",FontAwesomeIcon.Eye,Ui(120),Ui(32))){eventDialogOpen=false;ViewEventRoom(e);}
                        ImGui.SameLine();using(UiDisabled.Scope(stream.WatchPartyJoinBlockReason is not null))if(AlphaChatButton("detailJoin","Join Watch Party",FontAwesomeIcon.Users,Ui(176),Ui(32),primary:true)){eventDialogOpen=false;JoinPartyDirectoryRoom(room);}
                        if(stream.WatchPartyJoinBlockReason is { } block&&ImGui.IsItemHovered())UiFonts.SetTooltip(block);
                    }
                }
            }
        }
    }
    private void SaveEventRsvp(CommunityEvent e,bool going)
    {
        var show=eventRsvpPublic;var remind=eventRsvpReminders;var future=eventRsvpFuture;
        MutateEvent(s=>eventsApi.Rsvp(s.Token,e.Id,going,show,remind,future,youtubeFeedToken),()=>eventRsvpOptionsOpen=false);
    }
    private void DrawEventRsvpSettings(CommunityEvent e)
    {
        ImGui.TextWrapped(e.Title);DrawSocialHelper(EventLocalTime(e.StartAt));ImGui.Spacing();
        EventCheckbox("Show my name to other attendees",ref eventRsvpPublic);
        DrawSocialHelper("Private attendance still counts towards the total.");
        ImGui.Spacing();EventCheckbox("Remind me 30 minutes before",ref eventRsvpReminders);
        if(e.SeriesId is not null&&e.AllowSeriesRsvp){ImGui.Spacing();EventCheckbox("This and future occurrences",ref eventRsvpFuture);}
    }
    private void DrawEventRsvpSettingsFooter(CommunityEvent e)
    {
        EventNotice(eventsError,true);
        using(UiDisabled.Scope(eventsMutationBusy||e.Cancelled||e.EndAt<=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
        {
            if(AlphaChatButton("saveEventRsvpSettings",eventsMutationBusy?"Saving…":e.MyRsvp?"Save RSVP":"I'm going",FontAwesomeIcon.Check,Ui(140),primary:true))SaveEventRsvp(e,true);
            if(e.MyRsvp){ImGui.SameLine();if(AlphaChatButton("removeRsvpSettings","Not going",FontAwesomeIcon.Times,Ui(110)))SaveEventRsvp(e,false);}
        }
    }
    private void DrawEventDetailsFooter(CharacterSession session,CommunityEvent e)
    {
        EventNotice(eventsError,true);DrawEventActionNotice();
        var p=ImGui.GetCursorScreenPos();var width=ImGui.GetContentRegionAvail().X;
        if(AlphaChatButton("closeEventDetails","Close",FontAwesomeIcon.Times,Ui(96)))eventDialogOpen=false;
        ImGui.SetCursorScreenPos(p+new Vector2(MathF.Max(Ui(104),width-Ui(e.OwnerId==session.AccountId||CanAdminEvents?270:174)),0));
        using(UiDisabled.Scope(eventsMutationBusy||e.Cancelled||e.EndAt<=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
        {
            var label=eventsMutationBusy?"Saving…":e.MyRsvp?eventRsvpOptionsOpen?"Save RSVP":"Going":"I'm going";
            if(EventAttendanceButton("detailRsvp",label,Ui(130),Ui(36),e.MyRsvp)){if(e.MyRsvp&&!eventRsvpOptionsOpen)eventRsvpOptionsOpen=true;else SaveEventRsvp(e,true);}
            ImGui.SameLine(0,Ui(8));var rsvpAnchor=ImGui.GetCursorScreenPos()+UiVec(36,-6);
            if(AlphaChatButton("detailRsvpOptions","",FontAwesomeIcon.ChevronDown,Ui(36)))ImGui.OpenPopup("##eventRsvpMenu");
            ImGui.SetNextWindowPos(rsvpAnchor,ImGuiCond.Always,new Vector2(1,1));ImGui.SetNextWindowSize(new Vector2(Ui(210),0));
            using var menuStyle=ImRaii.PushStyle(ImGuiStyleVar.PopupRounding,0).Push(ImGuiStyleVar.WindowPadding,UiVec(16,14)).Push(ImGuiStyleVar.ItemSpacing,UiVec(8,8));
            using var menuColour=ImRaii.PushColor(ImGuiCol.PopupBg,CardBg).Push(ImGuiCol.Border,AccentInk(.35f));
            using(var popup=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Popup("##eventRsvpMenu")))if(popup)
            {
                if(ImGui.MenuItem("Manage RSVP"))eventRsvpOptionsOpen=true;
                if(e.MyRsvp&&ImGui.MenuItem(e.Reminders?"Mute reminders":"Enable reminders")){eventRsvpReminders=!e.Reminders;SaveEventRsvp(e,true);}
                if(e.MyRsvp){ImGui.Separator();if(ImGui.MenuItem("Not going"))SaveEventRsvp(e,false);}
            }
        }
        if(e.OwnerId==session.AccountId||CanAdminEvents)
        {
            ImGui.SameLine(0,Ui(8));var anchor=ImGui.GetCursorScreenPos()+UiVec(36,-6);
            if(AlphaChatButton("detailMore","",FontAwesomeIcon.EllipsisH,Ui(36)))ImGui.OpenPopup("##detailEventMenu");
            DrawEventActionsMenu(session,e,"##detailEventMenu",anchor,true);
        }
        if(ImGui.IsKeyPressed(ImGuiKey.F5))ReloadEventDetails(e.Id);
    }
    private void LoadEventAttendees(CommunityEvent e,string? after)
    {
        if(eventAttendeeBusy||CurrentSession is not { } session)return;eventAttendeeBusy=true;
        _=Task.Run(async()=>
        {
            try{var result=await eventsApi.Attendees(session.Token,e.Id,after,youtubeFeedToken);await Plugin.Framework.RunOnFrameworkThread(()=>
            {if(!IsCurrentMessageSession(session)||eventSelected?.Id!=e.Id)return;eventAttendeeData=after is not null&&eventAttendeeData is { } previous?result with{Attendees=previous.Attendees.Concat(result.Attendees).ToArray()}:result;});}
            catch{await Plugin.Framework.RunOnFrameworkThread(()=>{if(IsCurrentMessageSession(session))eventsError="Could not load attendees. Please try again.";});}
            finally{await Plugin.Framework.RunOnFrameworkThread(()=>eventAttendeeBusy=false);}
        });
    }
    private void DrawEventAttendees(CharacterSession session,CommunityEvent e)
    {
        DrawSocialHelperWrapped("The total includes the host and private RSVPs. Only people who chose to show their name appear here.");
        if(eventAttendeeBusy)DrawSocialHelper("Loading attendees…");
        foreach(var person in eventAttendeeData?.Attendees??[])
        {
            var origin=ImGui.GetCursorScreenPos();DrawAvatarAt(origin,person.AvatarIcon,person.AvatarColorHex,Ui(40),person.AvatarImageUrl);
            ImGui.SetCursorScreenPos(origin+UiVec(52,10));DrawUsername(person.AccountId,person.DisplayName,AccentHover);
            if(ImGui.IsItemClicked()){eventAttendeesOpen=false;eventDialogOpen=false;OpenProfilePage(session,person.AccountId,person.DisplayName);}
            ImGui.SetCursorScreenPos(origin+UiVec(0,54));
        }
        if(eventsError is not null){ImGui.TextWrapped(eventsError);if(ImGui.Button("Retry"))LoadEventAttendees(e,null);}
        if(eventAttendeeData?.Next is { } next&&ImGui.Button("Load more"))LoadEventAttendees(e,next);

    }
    private void DrawEventAttendeesFooter()
    {
        if(AlphaChatButton("attendeeBack","Back to event",FontAwesomeIcon.ArrowLeft,Ui(140)))eventAttendeesOpen=false;
    }
    private void DrawEventCancellation(CommunityEvent e)
    {
        ImGui.TextWrapped($"Cancel {e.Title}? People who RSVP'd will see the cancellation when their event data updates.");
        if(e.SeriesId is not null)EventCheckbox("Cancel this and future occurrences",ref eventCancelFuture);
        using(UiDisabled.Scope(eventsMutationBusy))if(AlphaChatButton("confirmCancelEvent","Cancel event",FontAwesomeIcon.Times,Ui(150),primary:true))
            MutateEvent(s=>eventsApi.Cancel(s.Token,e,eventCancelFuture,youtubeFeedToken),()=>eventCancelOpen=false);
        if(eventsError is not null)ImGui.TextWrapped(eventsError);
    }
    private static string EventShareText(CommunityEvent e,int maxLength=int.MaxValue)=>EventSchedule.ShareText(e,maxLength);
    private void DrawEventShare(CharacterSession session,CommunityEvent e)
    {
        ImGui.TextWrapped(e.Title);DrawSocialHelper(EventLocalTime(e.StartAt));
        if(AlphaChatButton("copyEventDetails","Copy details",FontAwesomeIcon.Copy,Ui(140))){ImGui.SetClipboardText(EventShareText(e));eventActionNotice="Details copied.";}
        if(AlphaChatButton("eventShareAlpha","Prepare in Alpha Chat",FontAwesomeIcon.Comments,Ui(220)))
        {alphaChatDraft=EventShareText(e,1000);alphaChatFocusComposer=true;currentPage=HomePage.AlphaChat;eventShareOpen=false;eventDialogOpen=false;}
        DrawSocialHelper("Choose a chat to prepare the message. You can review it before sending.");
        if(conversationsDirty&&!conversationsLoading)RefreshConversations(session.Token);
        foreach(var chat in conversations)
        {
            if(ImGui.Selectable(ConversationTitle(chat)+"##shareEvent"+chat.ConversationId))
            {OpenConversation(session,chat.ConversationId,chat.Members,chat.IsGroup,ConversationTitle(chat));messageComposerInput=EventShareText(e,2000);currentPage=HomePage.Messages;eventShareOpen=false;eventDialogOpen=false;}
        }
        if(eventActionNotice is not null)ImGui.TextUnformatted(eventActionNotice);
    }
    private void ExportEventCalendar(CommunityEvent e)
    {
        try{var directory=Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName,"Events","Calendar");Directory.CreateDirectory(directory);
            var path=Path.Combine(directory,Guid.Parse(e.Id).ToString("D")+".ics");File.WriteAllText(path,EventSchedule.CalendarFile(e));Process.Start(new ProcessStartInfo(path){UseShellExecute=true});eventActionNotice="Calendar file exported.";}
        catch{eventsError="Could not open your calendar. The event file is saved in the plugin's Events/Calendar folder.";}
    }
    private static void OpenEventWebsite(string url)
    {if(Uri.TryCreate(url,UriKind.Absolute,out var uri)&&uri.Scheme is "https" or "http")try{Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});}catch{}}
    private void ViewEventRoom(CommunityEvent e)
    {currentPage=HomePage.PartyDirectory;ResetPartyDirectoryFilters();partyDirectorySearch=e.HostName??"";partyDirectoryFocusSearchRequested=true;RefreshPartyDirectoryIfNeeded();}
    private void DrawEventRepeatPreview()
    {
        if(!DateTime.TryParseExact(eventDraft.StartLocal,"yyyy-MM-ddTHH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out var start))return;
        var max=eventRepeatEnd==0?Math.Min(eventRepeatCount,3):3;var shown=0;
        var until=eventRepeatEnd==1&&DateTime.TryParseExact(eventRepeatUntil,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)?date.AddDays(1):start.AddYears(3);
        DrawSocialHelper("Next dates");
        for(var n=0;n<1100&&shown<max;n++)
        {
            var at=start.AddDays(n);if(at>=until)break;
            var match=eventRepeatFrequency switch
            {0=>n%eventRepeatInterval==0,1=>n==0||eventWeekdays[(int)at.DayOfWeek]&&((n+((int)start.DayOfWeek+6)%7)/7)%eventRepeatInterval==0,
                _=>at.Day==start.Day&&((at.Year-start.Year)*12+at.Month-start.Month)%eventRepeatInterval==0};
            if(match){DrawSocialHelper(at.ToString("ddd d MMM yyyy · HH:mm"));shown++;}
        }
    }
}

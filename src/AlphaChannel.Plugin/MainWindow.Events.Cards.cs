using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawEventRow(CommunityEvent e,CharacterSession session,int seriesDates=0,bool expanded=false)
    {
        using var id=ImRaii.PushId(e.Id);
        var width=ImGui.GetContentRegionAvail().X;
        var narrow=width<Ui(600);var tiny=width<Ui(440);
        var height=Ui(narrow?208:seriesDates>1?140:124);
        var parentDraw=ImGui.GetWindowDrawList();
        var parentClipMin=parentDraw.GetClipRectMin();var parentClipMax=parentDraw.GetClipRectMax();
        var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();var live=EventSchedule.Live(e,now);
        var room=EventRoom(e);var ink=live?EventGreen:EventColour(e);
        using(ImRaii.PushColor(ImGuiCol.ChildBg,FrameBg).Push(ImGuiCol.Border,AccentInk(.4f)))
        using(ImRaii.PushStyle(ImGuiStyleVar.ChildRounding,Ui(10)).Push(ImGuiStyleVar.ChildBorderSize,Ui(1)).Push(ImGuiStyleVar.WindowPadding,UiVec(16,14)))
        using(var card=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##eventCard",new Vector2(width,height),true,ImGuiWindowFlags.AlwaysUseWindowPadding|ImGuiWindowFlags.NoScrollbar|ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if(!card)return;
            var p=ImGui.GetCursorScreenPos();var inner=ImGui.GetContentRegionAvail().X;
            var edge=ImGui.GetWindowPos();var draw=ImGui.GetWindowDrawList();
            // The accent sits outside the child's padded content clip, but must
            // still respect the scrolling page's clip rectangle.
            draw.PushClipRect(Vector2.Max(edge,parentClipMin),Vector2.Min(edge+new Vector2(width,height),parentClipMax),false);
            draw.AddRectFilled(edge+UiVec(1,3),edge+new Vector2(Ui(5),height-Ui(3)),ImGui.GetColorU32(ink),Ui(2));
            draw.PopClipRect();
            var date=DateTimeOffset.FromUnixTimeMilliseconds(e.StartAt).LocalDateTime;
            DrawEventDateTile(p,date);
            if(!tiny)EventIconTile(p+UiVec(76,0),UiVec(60,60),e.Icon,EventColour(e));
            var content=p+UiVec(tiny?76:146,0);
            var rightWidth=narrow?0:MathF.Min(Ui(375),MathF.Max(Ui(160),inner-(content.X-p.X)-Ui(220)));
            var actionWidth=Ui(e.MyRsvp&&room is null?190:152);
            var textWidth=inner-(content.X-p.X)-(narrow?0:actionWidth+Ui(12));
            var titleExtra=Ui((live?48:0)+(e.Adult?38:0))+
                (e.Recurrence is { } titleRule&&!tiny?ImGui.CalcTextSize(EventRecurrenceLabel(titleRule)).X+Ui(18):0);
            ImGui.SetCursorScreenPos(content);
            if(live){EventBadge("LIVE",EventGreen);ImGui.SameLine(0,Ui(8));}
            using(PushUiFont(1.12f,true))
                ImGui.TextUnformatted(FitPartyTextToWidth(e.Title,MathF.Max(Ui(36),textWidth-titleExtra)));
            var opened=false;
            if(ImGui.IsItemClicked()){OpenEventDetails(e);opened=true;}
            if(ImGui.IsItemHovered()){ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);UiFonts.SetTooltip(e.Title+"\nView event details");}
            if(e.Adult){ImGui.SameLine(0,Ui(6));EventBadge("18+",EventOrange);}
            if(e.Recurrence is { } rule&&!tiny){ImGui.SameLine(0,Ui(6));EventBadge(EventRecurrenceLabel(rule),AccentHover);}

            ImGui.SetCursorScreenPos(content+UiVec(0,26));
            var end=DateTimeOffset.FromUnixTimeMilliseconds(e.EndAt).LocalDateTime;
            var timing=EventListing.Timing(e,now);
            var schedule=$"{date:HH:mm} — "+(end.Date==date.Date?$"{end:HH:mm}":$"{end:ddd d MMM HH:mm}");
            DrawSocialHelper(FitPartyTextToWidth(schedule+(timing.Length>0?" · "+timing:""),textWidth));
            ImGui.SetCursorScreenPos(content+UiVec(0,48));
            var place=e.World=="Online"?"Online · Any world":string.Join(" · ",new[]{e.World,e.Location}.Where(s=>s.Length>0));
            DrawEventCardMeta(FontAwesomeIcon.MapMarkerAlt,place,MutedText,textWidth);
            ImGui.SetCursorScreenPos(content+UiVec(0,70));
            DrawAvatarAt(ImGui.GetCursorScreenPos(),e.Organiser.AvatarIcon,e.Organiser.AvatarColorHex,Ui(22),e.Organiser.AvatarImageUrl);
            ImGui.SetCursorScreenPos(content+UiVec(30,73));
            DrawSocialHelper("Hosted by");ImGui.SameLine(0,Ui(4));
            var hostWidth=narrow?textWidth:inner-rightWidth-(content.X-p.X)-Ui(12);
            DrawUsername(e.OwnerId,FitPartyTextToWidth(e.Organiser.DisplayName,MathF.Max(Ui(30),hostWidth-Ui(110))),AccentHover);
            if(ImGui.IsItemClicked()){opened=true;eventDialogOpen=false;OpenProfilePage(session,e.OwnerId,e.Organiser.DisplayName);}
            if(PatreonBadgeTier(e.OwnerId)>0){ImGui.SameLine(0,Ui(4));DrawAlphaChatSupporterHeart();}

            var actions=narrow?p+new Vector2(tiny?0:Ui(76),Ui(142)):p+new Vector2(inner-actionWidth,Ui(2));
            ImGui.SetCursorScreenPos(actions);
            using(UiDisabled.Scope(eventsMutationBusy||e.Cancelled||e.EndAt<=now))
            {
                if(e.ShowJoinButton&&room is not null)
                {
                    using(UiDisabled.Scope(stream.WatchPartyJoinBlockReason is not null))
                        if(AlphaChatButton("eventJoin","Join party",FontAwesomeIcon.Users,Ui(112),Ui(34),primary:true)){opened=true;JoinPartyDirectoryRoom(room);}
                }
                else if(EventAttendanceButton("eventRsvp",e.MyRsvp?"Going":"RSVP",Ui(112),Ui(34),e.MyRsvp)){opened=true;OpenEventDetails(e);}
            }
            if(e.MyRsvp&&room is null)
            {
                ImGui.SameLine(0,Ui(6));
                using(UiDisabled.Scope(eventsMutationBusy||e.Cancelled||e.EndAt<=now))
                if(AlphaChatButton("eventReminder","",e.Reminders?FontAwesomeIcon.Bell:FontAwesomeIcon.BellSlash,Ui(32),Ui(34)))
                {
                    opened=true;
                    MutateEvent(s=>eventsApi.Rsvp(s.Token,e.Id,true,e.PublicAttendance,!e.Reminders,false,youtubeFeedToken));
                }
                if(ImGui.IsItemHovered())UiFonts.SetTooltip(e.Reminders?"Mute event reminders":"Enable event reminders");
            }
            ImGui.SameLine(0,Ui(6));var anchor=ImGui.GetCursorScreenPos()+UiVec(34,40);
            if(AlphaChatButton("eventMore","",FontAwesomeIcon.EllipsisH,Ui(34),Ui(34)))ImGui.OpenPopup("##eventRowActions");
            DrawEventActionsMenu(session,e,"##eventRowActions",anchor);

            // Narrow cards keep all metadata above their action row.
            if(narrow)
            {
                var attendance=$"{e.GoingCount} going";
                var remainingWidth=inner-(actions.X-p.X)-actionWidth-Ui(12);
                if(remainingWidth>Ui(65))
                {ImGui.SetCursorScreenPos(actions+new Vector2(actionWidth+Ui(12),Ui(9)));DrawSocialHelper(attendance);}
                if(seriesDates<=1&&(room is not null||e.HostAccountId is not null||e.ConnectedHostAccountId is not null))
                {
                    ImGui.SetCursorScreenPos(p+new Vector2(tiny?0:Ui(76),Ui(110)));
                    DrawEventCardMeta(room is not null?FontAwesomeIcon.BroadcastTower:FontAwesomeIcon.Link,
                        room is not null?RoomCapacityText(room.HostAccountId,room.ViewerCount,includeHost:true):live?"Waiting for host":"Watch Party linked",
                        room is not null?EventGreen:AccentHover,inner-(tiny?0:Ui(76)));
                }
            }
            else
            {
                var status=room is not null?$"Watch Party · {room.ViewerCount+1}/{(RoomCapacityClient.Shared.Cached(room.HostAccountId)?.Capacity+1)?.ToString()??"…"}":
                    e.HostAccountId is not null||e.ConnectedHostAccountId is not null?live?"Waiting for host":"Watch Party linked":"";
                var attendance=$"{e.GoingCount} going";
                float attendeeWidth;
                using(UiFonts.PushRelative(.90f)) attendeeWidth=ImGui.CalcTextSize(attendance).X+MeasureUiIcon(FontAwesomeIcon.Users,Ui(14)).X+Ui(8);
                if(status.Length>0)
                {
                    var statusWidth=MathF.Min(rightWidth-attendeeWidth-Ui(24),ImGui.CalcTextSize(status).X+Ui(20));
                    var statusAt=p+new Vector2(inner-attendeeWidth-Ui(24)-statusWidth,Ui(73));
                    ImGui.SetCursorScreenPos(statusAt);
                    DrawEventCardMeta(room is not null?FontAwesomeIcon.BroadcastTower:FontAwesomeIcon.Link,status,room is not null?EventGreen:AccentHover,statusWidth);
                    var separator=p+new Vector2(inner-attendeeWidth-Ui(12),Ui(69));
                    draw.AddLine(separator,separator+UiVec(0,22),ImGui.GetColorU32(AccentInk(.25f)));
                }
                ImGui.SetCursorScreenPos(p+new Vector2(inner-attendeeWidth,Ui(73)));
                DrawEventCardMeta(FontAwesomeIcon.Users,attendance,MutedText,attendeeWidth);
            }
            if(seriesDates>1&&e.SeriesId is not null)
            {
                var label=(e.Recurrence is { } repeat?EventRecurrenceLabel(repeat)+" · ":"")+(expanded?"Hide upcoming dates":"View upcoming dates");
                var labelWidth=MathF.Min(inner,ImGui.CalcTextSize(label).X+Ui(24));
                var at=new Vector2(p.X+inner-labelWidth,p.Y+Ui(narrow?110:100));
                ImGui.SetCursorScreenPos(at);
                if(ImGui.InvisibleButton("##expandEventSeries",new Vector2(labelWidth,Ui(18))))
                {opened=true;if(!eventsExpandedSeries.Add(e.SeriesId))eventsExpandedSeries.Remove(e.SeriesId);}
                var hovered=ImGui.IsItemHovered();if(hovered)ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                draw.AddText(at,ImGui.GetColorU32(hovered?Vector4.One:AccentHover),FitPartyTextToWidth(label,labelWidth-Ui(16)));
                DrawUiIcon(expanded?FontAwesomeIcon.ChevronUp:FontAwesomeIcon.ChevronDown,Ui(12),at+new Vector2(labelWidth-Ui(12),Ui(2)),AccentHover);
            }
            // Only blank card space opens details; buttons/popups retain their input.
            if(!opened&&ImGui.IsWindowHovered()&&!ImGui.IsAnyItemHovered()&&ImGui.IsMouseClicked(ImGuiMouseButton.Left)&&
                !ImGui.IsPopupOpen("",ImGuiPopupFlags.AnyPopupId|ImGuiPopupFlags.AnyPopupLevel))OpenEventDetails(e);
        }
    }

    private void DrawEventDateTile(Vector2 p,DateTime date)
    {
        var draw=ImGui.GetWindowDrawList();var size=UiVec(62,88);
        draw.AddRectFilled(p,p+size,ImGui.GetColorU32(CardBg),Ui(8));
        draw.AddRect(p,p+size,ImGui.GetColorU32(AccentInk(.4f)),Ui(8));
        void Center(string text,float y,float scale,Vector4 ink)
        {
            using var font=PushUiFont(scale,scale>1);
            var measured=ImGui.CalcTextSize(text);
            ImGui.SetCursorScreenPos(Vector2.Round(p+new Vector2((size.X-measured.X)*.5f,Ui(y))));ImGui.TextColored(ink,text);
        }
        Center(date.ToString("MMM").ToUpperInvariant(),8,.85f,MutedText);
        Center(date.Day.ToString(),27,1.8f,Vector4.One);
        Center(date.ToString("ddd").ToUpperInvariant(),65,.85f,MutedText);
    }

    private void DrawEventCardMeta(FontAwesomeIcon icon,string text,Vector4 ink,float width)
    {
        using var font=UiFonts.PushRelative(.90f);
        var p=ImGui.GetCursorScreenPos();var draw=ImGui.GetWindowDrawList();
        var textOffset=MeasureUiIcon(icon,Ui(14)).X+Ui(8);
        DrawUiIcon(icon,Ui(14),p+UiVec(0,1),ink);
        draw.AddText(p+new Vector2(textOffset,0),ImGui.GetColorU32(ink),FitPartyTextToWidth(text,MathF.Max(0,width-textOffset)));
        ImGui.Dummy(new Vector2(MathF.Max(0,width),ImGui.GetTextLineHeight()));
        if(ImGui.IsItemHovered())UiFonts.SetTooltip(text);
    }

    private void DrawEventSeriesDates(EventListingGroup group)
    {
        var dates=group.Dates.Where(e=>e.Id!=group.Event.Id).ToArray();
        using var id=ImRaii.PushId("dates"+group.Event.Id);
        using var colours=ImRaii.PushColor(ImGuiCol.ChildBg,CardBg).Push(ImGuiCol.Border,AccentInk(.3f));
        using var styles=ImRaii.PushStyle(ImGuiStyleVar.ChildRounding,Ui(8)).Push(ImGuiStyleVar.WindowPadding,UiVec(16,10));
        using var child=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##seriesDates",new Vector2(-1,Ui(Math.Min(256,36+dates.Length*38))),true,ImGuiWindowFlags.AlwaysUseWindowPadding));
        if(!child)return;
        DrawSocialHelperWrapped("More dates · RSVP separately or choose the series in event details");
        foreach(var date in dates)
        {
            var when=DateTimeOffset.FromUnixTimeMilliseconds(date.StartAt).ToLocalTime();
            var row=ImGui.GetCursorScreenPos();var rowWidth=ImGui.GetContentRegionAvail().X;
            if(ImGui.Selectable("##seriesDate"+date.Id,false,ImGuiSelectableFlags.None,new Vector2(0,Ui(32))))OpenEventDetails(date);
            using(UiFonts.PushRelative(.90f))
            {
                var draw=ImGui.GetWindowDrawList();
                draw.AddText(row+UiVec(0,6),ImGui.GetColorU32(Hex(0xE2E7EF)),FitPartyTextToWidth($"{when:ddd d MMM · HH:mm}",rowWidth*.58f));
                draw.AddText(row+new Vector2(rowWidth*.62f,Ui(6)),ImGui.GetColorU32(date.MyRsvp?EventGreen:MutedText),date.MyRsvp?"Going":$"{date.GoingCount} going");
            }
            if(ImGui.IsItemHovered())UiFonts.SetTooltip(date.Title+"\n"+date.World+" · "+date.Location+"\nView this date and RSVP");
        }
    }
}

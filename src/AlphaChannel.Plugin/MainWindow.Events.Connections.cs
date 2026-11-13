using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool eventConnectOpen;

    private static bool CanManageEventConnection(CharacterSession session,CommunityEvent e)=>
        e.OwnerId==session.AccountId||e.HostAccountId==session.AccountId&&e.HostState=="accepted";

    private string? EventConnectionBlockReason(CharacterSession session,CommunityEvent e)=>
        !CanManageEventConnection(session,e)?"Only the event creator or confirmed Watch Party host can connect a room.":
        !EventSchedule.Live(e,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())?"You can connect a Watch Party while the event is live.":
        !stream.IsConnected||stream.Mode!=StreamMode.Hosting||stream.RoomKind!=RoomKind.Public?
            "Start hosting a public Watch Party before connecting it to this event.":null;

    private void DrawEventConnectConfirmation(CharacterSession session,CommunityEvent e)
    {
        ImGui.TextWrapped("This will connect your current Watch Party to the event, confirmed attendees will receive a notification to join the party.");
        ImGui.Spacing();ImGui.TextColored(AccentHover,e.Title);
        DrawRoomCapacityDetails(session.AccountId,stream.Mode==StreamMode.Hosting?stream.Roster.Length:null);
        if(EventConnectionBlockReason(session,e) is { } reason)
        {ImGui.Spacing();using(ImRaii.PushColor(ImGuiCol.Text,MutedText))ImGui.TextWrapped(reason);}
    }

    private void DrawEventConnectFooter(CharacterSession session,CommunityEvent e)
    {
        EventNotice(eventsError,true);
        if(AlphaChatButton("cancelEventConnect","Cancel",FontAwesomeIcon.Times,Ui(110)))eventConnectOpen=false;
        ImGui.SameLine();
        using(UiDisabled.Scope(eventsMutationBusy||EventConnectionBlockReason(session,e) is not null))
            if(AlphaChatButton("confirmEventConnect",eventsMutationBusy?"Connecting…":"Connect Watch Party",FontAwesomeIcon.Link,Ui(210),primary:true))
                MutateEvent(async s=>
                {
                    await eventsApi.Connect(s.Token,e,youtubeFeedToken);
                    var updated=await eventsApi.Get(s.Token,e.Id,youtubeFeedToken);
                    await Plugin.Framework.RunOnFrameworkThread(()=>
                    {
                        if(!IsCurrentMessageSession(s))return;
                        events=events.Where(current=>current.Id!=updated.Id).Append(updated).ToArray();
                        eventSelected=updated;
                        if(updated.Room is not null)eventRoomReceived[updated.Id]=DateTime.UtcNow;
                    });
                },()=>{eventConnectOpen=false;eventActionNotice="Watch Party connected. Attendees will be notified when their events sync.";});
    }

    private void DrawEventHostingInfo()
    {
        const string guidance="The assigned Watch Party host needs to create a public room for the event. It will automatically connect when the event and room are live, and attendees will be prompted to join.\n\nIf the room closes during the event, the event creator or confirmed host can use Connect Watch Party in the event’s … menu to connect the public room they are currently hosting. This sends a new join alert to confirmed attendees.";
        var width=ImGui.GetContentRegionAvail().X;
        var textHeight=ImGui.CalcTextSize(guidance,false,MathF.Max(Ui(80),width-Ui(28))).Y;
        using(ImRaii.PushColor(ImGuiCol.ChildBg,FrameBg).Push(ImGuiCol.Border,AccentInk(.45f)))
        using(ImRaii.PushStyle(ImGuiStyleVar.ChildRounding,Ui(8)).Push(ImGuiStyleVar.ChildBorderSize,Ui(1)).Push(ImGuiStyleVar.WindowPadding,UiVec(14,12)))
        using(var box=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##eventHostingInfo",new Vector2(width,textHeight+Ui(58)),true,ImGuiWindowFlags.AlwaysUseWindowPadding|ImGuiWindowFlags.NoScrollbar)))
        {
            if(!box)return;
            EventSectionHeading("How hosting works",FontAwesomeIcon.InfoCircle);
            DrawSocialHelperWrapped(guidance);
        }
    }

    private void DrawConnectedEventLink()
    {
        if(CurrentSession is not { } session||!eventsSessionValidated||stream.RoomKind!=RoomKind.Public)return;
        var hostId=stream.Mode==StreamMode.Hosting?session.AccountId:stream.HostId;
        if(hostId is null)return;
        var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach(var e in events.Where(e=>EventSchedule.Live(e,now)&&EventSchedule.RoomHost(e)==hostId).OrderBy(e=>e.StartAt))
        {
            using var id=ImRaii.PushId(e.Id);
            if(AlphaChatButton("connectedEvent",e.Title,EventIcon(e.Icon),ImGui.GetContentRegionAvail().X,Ui(34)))
            {currentPage=HomePage.Events;OpenEventDetails(e);}
            if(ImGui.IsItemHovered())UiFonts.SetTooltip("View connected event");
        }
    }
}

using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private const string RoomCapacityUpgradeText="Support Alpha Channel on Patreon to increase party size limit";
    private void DrawRoomCapacityOverlay()
    {
        var prompt=stream.CapacityPrompt;
        if(prompt is null||CurrentSession?.AccountId!=prompt.Account)return;
        DrawEventWindow("roomCapacity",prompt.Full?"Watch Party Full":"Unable to Join Watch Party",UiVec(490,280),()=>
        {
            ImGui.TextWrapped(prompt.Message);
            if(prompt.AutoRetry)
            {ImGui.Spacing();ImGui.TextColored(MutedText,stream.IsConnected?$"Retrying in {Math.Max(0,(prompt.RetryAt-Environment.TickCount64+999)/1000)} seconds…":"Waiting for a connection…");}
        },()=>stream.CancelCapacityRetry(),()=>
        {
            if(AlphaChatButton("capacityCancel","Cancel",FontAwesomeIcon.Times,Ui(110)))stream.CancelCapacityRetry();
            if(prompt.Full&&!prompt.AutoRetry)
            {ImGui.SameLine();if(AlphaChatButton("capacityRetry","Auto-retry",FontAwesomeIcon.SyncAlt,Ui(150),primary:true))stream.EnableCapacityRetry();}
        });
    }
    private string RoomCapacityText(string host,int viewers,bool includeHost=false)
    {
        var status=RoomCapacityClient.Shared.Cached(host);
        var total = viewers + (includeHost ? 1 : 0);
        var capacity = status?.Capacity + (includeHost ? 1 : 0);
        return status is null?$"{total} / … viewers":$"{total} / {capacity} viewers · {Math.Max(0,status.Capacity-viewers)} spaces left";
    }
    private void DrawRoomCapacityDetails(string host,int? viewers=null,bool upgrade=true)
    {
        if(CurrentSession is not { } session)return;
        var currentHost=stream.Mode==StreamMode.Hosting?session.AccountId:stream.Mode==StreamMode.Viewing?stream.HostId:null;
        if(currentHost!=host||stream.Mode==StreamMode.Viewing&&stream.CapacityDisplay?.HostId!=host)
            RoomCapacityClient.Shared.QueueRefresh(session.Token,[host]);
        var status=RoomCapacityClient.Shared.Cached(host);
        using(ImRaii.PushColor(ImGuiCol.Text,MutedText))
            ImGui.TextWrapped(status is null?"Checking Watch Party capacity…":viewers is { } count?RoomCapacityText(host,count):$"This host’s Watch Party can hold up to {status.Capacity} viewers. The host does not use a viewer space.");
        if(upgrade&&status is {Tier:0})
        {
            using var uiFontScale2429=UiFonts.PushRelative(.85f);
            using(ImRaii.PushColor(ImGuiCol.Text,AccentHover))
                ImGui.TextWrapped(RoomCapacityUpgradeText);
            if(ImGui.IsItemHovered())UiFonts.SetTooltip("View Patreon membership options");
            if(ImGui.IsItemClicked())OpenPatreonMembershipPage();
            uiFontScale2429.Dispose();
        }
    }
    private void DrawCurrentRoomCapacity()
    {
        if(CurrentSession is not { } session)return;
        var host=stream.Mode==StreamMode.Hosting?session.AccountId:stream.HostId;
        if(host is null)return;
        DrawRoomCapacityDetails(host,stream.Roster.Count(p=>p.UserId!=host),stream.Mode==StreamMode.Hosting);
        ImGui.Spacing();
    }
}

using AlphaChannel.Plugin.Auth;
using Dalamud.Interface.ImGuiNotification;
using System.Text.Json;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly EventsClient eventsApi=new();
    private CommunityEvent[] events=[];
    private string? eventsAccount,eventsError,eventsSessionToken;
    private DateTime eventsRetryAfter;
    private bool eventsSyncBusy,eventsMutationBusy,eventsSessionValidated,eventsHasPublicSnapshot;
    private long eventsGeneration;
    private DateTime eventsNextSync,eventsPublicRefreshed,eventsNextTick,eventsNextRoomCheck;
    private DateTime eventsCalendarMonth=new(DateTime.Today.Year,DateTime.Today.Month,1);
    private string eventsPublicRange="";
    private readonly Dictionary<string,DateTime> eventRoomReceived=[];
    private string EventsCachePath(string account)=>Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName,"Events",Guid.Parse(account).ToString("D")+".json");
    private (long From,long To,string Key) EventsRange()
    {
        var past=currentPage==HomePage.Events&&eventsMyView&&eventsMyTab==2;
        var calendar=currentPage==HomePage.Events&&eventsCalendarView;
        var from=past?DateTime.Today.AddDays(-eventsPastDays):calendar?eventsCalendarMonth.AddDays(-7):DateTime.Today.AddDays(-1);
        var all=currentPage==HomePage.Events&&!eventsMyView&&!eventsCalendarView&&eventsDateFilter==0;
        var to=past?from.AddDays(60):calendar?eventsCalendarMonth.AddMonths(1).AddDays(7):all?EventBrowseRange.End(DateTime.Today,0):DateTime.Today.AddDays(45);
        return (new DateTimeOffset(from).ToUnixTimeMilliseconds(),new DateTimeOffset(to).ToUnixTimeMilliseconds(),from.ToString("yyyy-MM-dd")+":"+to.ToString("yyyy-MM-dd"));
    }
    internal void TickEvents()
    {
        if(Volatile.Read(ref disposeStarted)!=0||DateTime.UtcNow<eventsNextTick) return;
        eventsNextTick=DateTime.UtcNow.AddSeconds(1);
        var session=CurrentSession;
        if(eventsAccount!=session?.AccountId||eventsSessionToken!=session?.Token)
        {
            eventsGeneration++;eventDetailsGeneration++;eventDetailsLoading=false;eventsAccount=session?.AccountId;eventsSessionToken=session?.Token;events=[];eventsSessionValidated=false;eventsHasPublicSnapshot=false;
            eventsRetryAfter=DateTime.MinValue;eventsSyncBusy=false;
            eventRoomReceived.Clear();
            eventsError=null;eventsNextSync=DateTime.MinValue;eventDialogOpen=false;eventEditorOpen=false;eventAttendeesOpen=false;
            eventShareOpen=false;eventCancelOpen=false;eventConnectOpen=false;eventRsvpOptionsOpen=false;eventSelected=null;eventDetailsId="";eventActionNotice=null;eventFormError=null;
#if DEBUG
#endif
            if(session is not null)
            {
                try {if(File.Exists(EventsCachePath(session.AccountId))) events=JsonSerializer.Deserialize<EventSnapshot>(File.ReadAllText(EventsCachePath(session.AccountId)),EventsClient.Json)?.Events??[];}catch{}
            }
        }
        if(session is null) return;
        var visible=(currentPage==HomePage.Events||currentPage==HomePage.WatchAlong&&stream.Mode is StreamMode.Hosting or StreamMode.Viewing)&&IsOpen&&!windowMinimized;
        var range=EventsRange();
        var needsPublic=visible&&(!eventsHasPublicSnapshot||range.Key!=eventsPublicRange||DateTime.UtcNow-eventsPublicRefreshed>TimeSpan.FromMinutes(5));
        if(DateTime.UtcNow>=eventsRetryAfter&&!eventsSyncBusy&&!eventsMutationBusy&&(needsPublic||DateTime.UtcNow>=eventsNextSync)) _=SyncEventsAsync(session,!visible);
        if(!eventsSessionValidated) return;
        var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach(var e in events.Where(e=>e.MyRsvp&&e.Reminders))
        {
            var key=EventSchedule.ReminderKey(session.AccountId,e);
            if(EventSchedule.ReminderDue(e,now)&&!Plugin.Cfg.EventNotificationHistory.ContainsKey(key)) NotifyEventOnce(session,e,key,
                now<e.StartAt?$"{e.Title} starts in {Math.Max(1,(e.StartAt-now)/60000)} minutes":$"{e.Title} is underway","reminder");
        }
        var live=events.Where(e=>EventSchedule.Live(e,now)&&EventSchedule.RoomHost(e) is not null&&(visible||eventDialogOpen&&IsOpen&&eventSelected?.Id==e.Id||e.MyRsvp&&(e.Reminders||e.ConnectedHostAccountId is not null))).ToArray();
        // Only an active event that concerns this account needs faster batched sync.
        if(events.Any(e=>EventSchedule.Live(e,now)&&(e.MyRsvp||e.OwnerId==session.AccountId||e.HostAccountId==session.AccountId)))
            eventsNextSync=eventsNextSync<DateTime.UtcNow.AddMinutes(2)?eventsNextSync:DateTime.UtcNow.AddMinutes(2);
        if(live.Length>0&&DateTime.UtcNow>=eventsNextRoomCheck)
        {
            eventsNextRoomCheck=DateTime.UtcNow.AddMinutes(2);
            RefreshPartyDirectoryIfNeeded(); // One existing relay directory request for every relevant event, no Worker polling.
        }
        foreach(var e in live.Where(e=>EventSchedule.RoomNotificationDue(e,now)))
        {
            if(EventRoom(e) is null) continue;
            NotifyEventOnce(session,e,EventSchedule.RoomNotificationKey(session.AccountId,e),$"Watch Party is ready for {e.Title}","room");
        }
    }
    private async Task SyncEventsAsync(CharacterSession session,bool mineOnly)
    {
        eventsSyncBusy=true;eventsNextSync=DateTime.UtcNow.AddMinutes(15);
        var generation=eventsGeneration;var range=EventsRange();
        try
        {
            var result=await eventsApi.Sync(session.Token,range.From,range.To,mineOnly,!mineOnly,youtubeFeedToken);
            await Plugin.Framework.RunOnFrameworkThread(()=>
            {
                if(generation!=eventsGeneration||!IsCurrentMessageSession(session)) return;
                var previousConnections=events.ToDictionary(e=>e.Id,e=>e.ConnectionRevision);
                if(mineOnly)
                {
                    var returned=result.Events.Select(e=>e.Id).ToHashSet();
                    // Personal sync is authoritative only for its documented ownership/RSVP
                    // windows. Keep unrelated public browse entries outside that scope.
                    var now=result.ServerTime;
                    var merged=events.Where(e=>result.Truncated||returned.Contains(e.Id)||!((e.OwnerId==session.AccountId||e.MyRsvp)&&e.EndAt>=now-30*86400000L || (e.HostAccountId==session.AccountId||e.ConnectedHostAccountId==session.AccountId)&&e.EndAt>=now))
                        .ToDictionary(e=>e.Id,e=>e);
                    foreach(var e in result.Events) merged[e.Id]=e;
                    events=merged.Values.OrderBy(e=>e.StartAt).ToArray();
                }
                else {events=result.Events;eventsHasPublicSnapshot=true;eventsPublicRange=range.Key;eventsPublicRefreshed=DateTime.UtcNow;}
                foreach(var e in result.Events){if(e.Room is not null)eventRoomReceived[e.Id]=DateTime.UtcNow;else eventRoomReceived.Remove(e.Id);}
                eventsSessionValidated=true;eventsError=result.Truncated?"More events exist than fit this view. Narrow the date range.":null;
                CheckEventChanges(session,previousConnections);
                var snapshot=new EventSnapshot(events.Select(e=>e with{Room=null}).ToArray(),result.ServerTime,result.Truncated);
                var cachePath=EventsCachePath(session.AccountId);
                _=SaveEventSnapshotAsync(cachePath,snapshot);
                if(eventSelected is { } selected) {
                    var current=events.FirstOrDefault(e=>e.Id==selected.Id);
                    var inScope=!mineOnly && selected.StartAt<range.To && selected.EndAt>=range.From ||
                        (selected.OwnerId==session.AccountId||selected.MyRsvp) && selected.EndAt>=result.ServerTime-30*86400000L ||
                        (selected.HostAccountId==session.AccountId||selected.ConnectedHostAccountId==session.AccountId)&&selected.EndAt>=result.ServerTime;
                    if(current is not null) eventSelected=current;
                    else if(inScope && !result.Truncated) {eventSelected=null;eventDialogOpen=false;eventDetailsGeneration++;}
                }
            });
        }
        catch(Exception ex)
        {
            await Plugin.Framework.RunOnFrameworkThread(()=>{if(generation==eventsGeneration&&IsCurrentMessageSession(session)){eventsError=ex is InvalidOperationException?ex.Message:"Events are temporarily unavailable.";eventsNextSync=DateTime.UtcNow.AddMinutes(2);eventsRetryAfter=eventsNextSync;}});
        }
        finally {await Plugin.Framework.RunOnFrameworkThread(()=>{if(generation==eventsGeneration)eventsSyncBusy=false;});}
    }
    private readonly SemaphoreSlim eventCacheWrites=new(1,1);
    private long eventCacheRevision;
    private Task SaveEventSnapshotAsync(string path,EventSnapshot snapshot)
    {
        var revision=Interlocked.Increment(ref eventCacheRevision);
        return Task.Run(async () =>
    {
        // Serialize writes per window so an older snapshot cannot overwrite a newer one.
        await eventCacheWrites.WaitAsync().ConfigureAwait(false);
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            if(revision!=Volatile.Read(ref eventCacheRevision))return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(temporary,JsonSerializer.Serialize(snapshot,EventsClient.Json)).ConfigureAwait(false);
            File.Move(temporary,path,true);
        } catch {AepLog.Warning("[Events] Could not save the local event cache.");}
        finally {try{File.Delete(temporary);}catch{} eventCacheWrites.Release();}
    });
    }
    private void CheckEventChanges(CharacterSession session,Dictionary<string,int> previousConnections)
    {
        var changed=false;
        foreach(var e in events.Where(e=>e.MyRsvp||e.OwnerId==session.AccountId||e.HostAccountId==session.AccountId))
        {
            var key=session.AccountId+":"+e.Id;
            if(Plugin.Cfg.EventKnownVersions.TryGetValue(key,out var old)&&old!=e.Version&&e.MyRsvp&&e.OwnerId!=session.AccountId&&
                !(e.ConnectedHostAccountId is not null&&previousConnections.GetValueOrDefault(e.Id)!=e.ConnectionRevision))
                NotifyEventOnce(session,e,$"{key}:change:{e.Version}",e.Cancelled?$"Event cancelled: {e.Title}":$"Event updated: {e.Title}","change");
            if(!Plugin.Cfg.EventKnownVersions.TryGetValue(key,out old)||old!=e.Version){Plugin.Cfg.EventKnownVersions[key]=e.Version;changed=true;}
            if(e.HostAccountId==session.AccountId&&e.HostState=="pending"&&!e.Cancelled)
                NotifyEventOnce(session,e,$"{session.AccountId}:{e.SeriesId??e.Id}:host:{e.Version}",$"{e.Organiser.DisplayName} invited you to host {e.Title}","host");
        }
        if(Plugin.Cfg.EventKnownVersions.Count>3000) Plugin.Cfg.EventKnownVersions=Plugin.Cfg.EventKnownVersions.TakeLast(2000).ToDictionary();
        if(changed) Plugin.Cfg.SavePublicStateDeferred();
    }
    private void NotifyEventOnce(CharacterSession session,CommunityEvent e,string key,string title,string kind)
    {
        if(Plugin.Cfg.EventNotificationHistory.ContainsKey(key)) return;
        Plugin.Cfg.EventNotificationHistory[key]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cutoff=DateTimeOffset.UtcNow.AddDays(-60).ToUnixTimeMilliseconds();
        foreach(var expired in Plugin.Cfg.EventNotificationHistory.Where(p=>p.Value<cutoff).Select(p=>p.Key).ToArray()) Plugin.Cfg.EventNotificationHistory.Remove(expired);
        Plugin.Cfg.SavePublicStateDeferred();
        var notification=Plugin.NotificationManager.AddNotification(new Notification{Title=title,Content=$"{EventLocalTime(e.StartAt)} · {e.World} · {e.Location}\nClick to view the event.",Type=NotificationType.Info,InitialDuration=TimeSpan.FromSeconds(30)});
        notification.Click+=_=>{if(Volatile.Read(ref disposeStarted)==0&&IsCurrentMessageSession(session)){currentPage=HomePage.Events;eventSelected=events.FirstOrDefault(current=>current.Id==e.Id)??e;eventDialogOpen=true;OpenUi();}};
        notification.DrawActions+=_=>
        {
            if(Volatile.Read(ref disposeStarted)!=0||!IsCurrentMessageSession(session)) return;
            if(Dalamud.Bindings.ImGui.ImGui.Button("View Event##eventNotification")){currentPage=HomePage.Events;eventSelected=e;eventDialogOpen=true;OpenUi();}
            if(kind=="room"&&EventRoom(events.FirstOrDefault(current=>current.Id==e.Id)??e) is { } room&&stream.WatchPartyJoinBlockReason is null)
            {Dalamud.Bindings.ImGui.ImGui.SameLine();if(Dalamud.Bindings.ImGui.ImGui.Button("Join Watch Party##eventNotification"))JoinPartyDirectoryRoom(room);}
        };
        QueueNotificationSound();
    }
    private AlphaChannel.Contracts.RoomDirectoryDto? EventRoom(CommunityEvent e)
    {
        var hostId=EventSchedule.RoomHost(e);
        if(hostId is null||!EventSchedule.Live(e,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) return null;
        var cached=partyDirectoryAccountId==CurrentSession?.AccountId&&!partyDirectoryLoading&&roomsClient.LastFailure is null&&DateTime.UtcNow-partyDirectoryRefreshedAt<TimeSpan.FromMinutes(3)?
            partyDirectoryRooms.FirstOrDefault(r=>r.HostAccountId==hostId&&r.Kind==AlphaChannel.Contracts.RoomKind.Public):null;
        return cached??(eventRoomReceived.TryGetValue(e.Id,out var received)&&DateTime.UtcNow-received<TimeSpan.FromMinutes(2)&&e.Room?.HostAccountId==hostId?e.Room:null);
    }
    private void MutateEvent(Func<CharacterSession,Task> work,Action? success=null)
    {
        if(eventsMutationBusy||CurrentSession is not { } session) return;
        eventsGeneration++;eventsSyncBusy=false;
        eventsMutationBusy=true;eventsError=null;
        var mutationGeneration=eventsGeneration;var dialogGeneration=eventDetailsGeneration;
        _=Task.Run(async()=>
        {
            try
            {
                await work(session);
                await Plugin.Framework.RunOnFrameworkThread(()=>{if(IsCurrentMessageSession(session)&&mutationGeneration==eventsGeneration){eventsGeneration++;eventsSyncBusy=false;eventsNextSync=DateTime.MinValue;eventsRetryAfter=DateTime.MinValue;eventsHasPublicSnapshot=false;eventActionNotice="Saved.";if(dialogGeneration==eventDetailsGeneration)success?.Invoke();}});
            }
            catch(Exception ex){await Plugin.Framework.RunOnFrameworkThread(()=>{if(IsCurrentMessageSession(session))eventsError=ex is InvalidOperationException?ex.Message:"Events could not be updated. Please try again.";});}
            finally{await Plugin.Framework.RunOnFrameworkThread(()=>{if(IsCurrentMessageSession(session))eventsMutationBusy=false;});}
        });
    }
    private static string EventLocalTime(long at)=>DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime().ToString("ddd d MMM · HH:mm");
}

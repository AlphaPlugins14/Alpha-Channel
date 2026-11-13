using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

internal sealed class EventsClient
{
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){IncludeFields=true};
    private static HttpClient Client(string token)
    {
        var http=PluginHttpClients.CreateMetadataClient();http.BaseAddress=new Uri(CharacterDirectoryClient.WorkerUrl);
        http.Timeout=TimeSpan.FromSeconds(25);http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);return http;
    }
    private static async Task<T> Request<T>(string token,string route,HttpMethod method,object? body,CancellationToken ct)
    {
        using var http=Client(token);using var request=new HttpRequestMessage(method,route);
        if(body is not null) request.Content=JsonContent.Create(body,options:Json);
        using var response=await http.SendAsync(request,ct);
        if(!response.IsSuccessStatusCode)
        {
            string? error=null;try{error=(await response.Content.ReadFromJsonAsync<Dictionary<string,string>>(cancellationToken:ct))?.GetValueOrDefault("error");}catch{}
            throw new InvalidOperationException(error switch
            {
                "event_changed"=>"This event changed. Refresh it before editing again.","event_limit"=>"You have reached the limit of upcoming events.",
                "event_ended"=>"This event has ended or been cancelled.","invalid_request"=>"Check the event fields and enter valid dates and 24-hour times, with the end after the start.",
                "event_not_live"=>"Watch Parties can only be connected while the event is live.",
                "public_room_required"=>"Start hosting a public Watch Party before connecting it to this event.",
                _=>response.StatusCode==System.Net.HttpStatusCode.Forbidden?"You aren't allowed to change this event or select that host.":"Events could not be updated. Please try again."
            });
        }
        return await response.Content.ReadFromJsonAsync<T>(Json,ct)??throw new InvalidDataException("Invalid events response.");
    }
    internal async Task<EventSnapshot> Sync(string token,long from,long to,bool mineOnly,bool includeRooms,CancellationToken ct)
    {
        var events=new Dictionary<string,CommunityEvent>();
        long serverTime=0;var truncated=false;
        foreach(var window in EventBrowseRange.RequestWindows(from,to))
        {
            var snapshot=await Request<EventSnapshot>(token,"/api/events/sync",HttpMethod.Post,
                new{from=window.From,to=window.To,mineOnly,includeRooms},ct).ConfigureAwait(false);
            foreach(var entry in snapshot.Events)events[entry.Id]=entry;
            serverTime=Math.Max(serverTime,snapshot.ServerTime);truncated|=snapshot.Truncated;
            // Personal events are included independently of the requested range.
            if(mineOnly)break;
        }
        return new(events.Values.OrderBy(e=>e.StartAt).ToArray(),serverTime,truncated);
    }
    internal Task<EventCreated> Create(string token,EventDraft draft,CancellationToken ct)=>Request<EventCreated>(token,"/api/events",HttpMethod.Post,draft,ct);
    internal Task<CommunityEvent> Get(string token,string id,CancellationToken ct)=>Request<CommunityEvent>(token,"/api/events/"+Guid.Parse(id).ToString("D"),HttpMethod.Get,null,ct);
    internal Task<EventAttendees> Attendees(string token,string id,string? after,CancellationToken ct)=>Request<EventAttendees>(token,
        "/api/events/"+Guid.Parse(id).ToString("D")+"/attendees"+(after is null?"":"?after="+Uri.EscapeDataString(after)),HttpMethod.Get,null,ct);
    internal Task<Dictionary<string,bool>> Rsvp(string token,string id,bool going,bool publicAttendance,bool reminders,bool future,CancellationToken ct)=>
        Request<Dictionary<string,bool>>(token,"/api/events/"+Guid.Parse(id).ToString("D")+"/rsvp",HttpMethod.Put,new{going,publicAttendance,reminders,future},ct);
    internal Task<Dictionary<string,bool>> Host(string token,string id,bool accepted,CancellationToken ct)=>Request<Dictionary<string,bool>>(token,
        "/api/events/"+Guid.Parse(id).ToString("D")+"/host",HttpMethod.Put,new{accepted},ct);
    internal Task<Dictionary<string,bool>> Connect(string token,CommunityEvent e,CancellationToken ct)=>Request<Dictionary<string,bool>>(token,
        "/api/events/"+Guid.Parse(e.Id).ToString("D")+"/connect",HttpMethod.Put,new{e.Version},ct);
    internal Task<Dictionary<string,bool>> Update(string token,string id,EventDraft draft,CancellationToken ct)=>Request<Dictionary<string,bool>>(token,
        "/api/events/"+Guid.Parse(id).ToString("D"),HttpMethod.Put,draft,ct);
    internal Task<Dictionary<string,bool>> Cancel(string token,CommunityEvent e,bool future,CancellationToken ct)=>Request<Dictionary<string,bool>>(token,
        "/api/events/"+Guid.Parse(e.Id).ToString("D"),HttpMethod.Delete,new{e.Version,future},ct);
    internal Task<Dictionary<string,bool>> ModeratorAction(string token,CommunityEvent e,bool remove,string title,string description,bool future,string reason,CancellationToken ct)=>
        Request<Dictionary<string,bool>>(token,"/api/events/"+Guid.Parse(e.Id).ToString("D")+"/moderator",remove?HttpMethod.Delete:HttpMethod.Put,
            new{e.Version,title,description,future,reason},ct);
#if DEBUG
    internal Task<Dictionary<string,bool>> AdminUpdate(string token,string id,EventDraft draft,CancellationToken ct)=>Request<Dictionary<string,bool>>(token,
        "/api/events/"+Guid.Parse(id).ToString("D")+"/admin",HttpMethod.Put,draft,ct);
    internal Task<Dictionary<string,bool>> AdminDelete(string token,CommunityEvent e,bool future,CancellationToken ct)=>Request<Dictionary<string,bool>>(token,
        "/api/events/"+Guid.Parse(e.Id).ToString("D")+"/admin",HttpMethod.Delete,new{e.Version,future},ct);
#endif
}

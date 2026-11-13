using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

internal sealed record RoomCapacitySnapshot(string HostId,int Viewers,int Capacity,int Tier,int Remaining,
    bool Allowed=false,bool Full=false,string? Permit=null,string? HostUsername=null);
internal sealed class RoomCapacityClient
{
    internal static readonly RoomCapacityClient Shared=new();
    private readonly ConcurrentDictionary<string,RoomCapacitySnapshot> snapshots=new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string,DateTime> nextFetch=new(StringComparer.OrdinalIgnoreCase);
    private sealed record Batch(RoomCapacitySnapshot[] Capacities);
    internal RoomCapacitySnapshot? Cached(string? host)=>host is not null?snapshots.GetValueOrDefault(host):null;
    private void Remember(RoomCapacitySnapshot s){snapshots[s.HostId]=s;nextFetch[s.HostId]=DateTime.UtcNow.AddMinutes(1);}
    internal void RememberDisplay(RoomCapacitySnapshot s)=>Remember(s);
    private static async Task<T> Post<T>(string token,string action,object body,CancellationToken ct)
    {
        using var http=PluginHttpClients.CreateMetadataClient();
        http.BaseAddress=new Uri("https://patreon.example.invalid");
        http.Timeout=TimeSpan.FromSeconds(15);http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
        using var response=await http.PostAsJsonAsync("/api/rooms/"+action,body,ct).ConfigureAwait(false);
        if(!response.IsSuccessStatusCode)
        {
            string? error=null;try{error=(await response.Content.ReadFromJsonAsync<Dictionary<string,string>>(cancellationToken:ct))?.GetValueOrDefault("error");}catch{}
            throw new InvalidOperationException(error switch{
                "room_unavailable"=>"This Watch Party is no longer available.",
                "own_room"=>"You cannot join your own Watch Party.",
                "unauthorized"=>"Sign in again before joining a Watch Party.",
                _=>"Room capacity could not be checked. Please try again shortly."});
        }
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken:ct).ConfigureAwait(false)??throw new InvalidDataException();
    }
    internal async Task<RoomCapacitySnapshot> Admit(string token,string host,CancellationToken ct)
    {var s=await Post<RoomCapacitySnapshot>(token,"admit",new{hostId=host,attemptId=Guid.NewGuid().ToString("D")},ct).ConfigureAwait(false);Remember(s);return s;}
    internal async Task Confirm(string token,string host,string permit,CancellationToken ct)
    {var s=await Post<RoomCapacitySnapshot>(token,"confirm",new{hostId=host,permit},ct).ConfigureAwait(false);Remember(s);}
    internal async Task Release(string token,string host,string permit,CancellationToken ct)
    {await Post<RoomCapacitySnapshot>(token,"release",new{hostId=host,permit},ct).ConfigureAwait(false);nextFetch.TryRemove(host,out _);}
    internal async Task Report(string token,string host,string[] viewers,bool listedRoom,CancellationToken ct)
    {var s=await Post<RoomCapacitySnapshot>(token,"report",new{hostId=host,viewers,listedRoom},ct).ConfigureAwait(false);Remember(s);}
    internal async Task Close(string token,string host)
    {try{await Post<RoomCapacitySnapshot>(token,"close",new{hostId=host},CancellationToken.None).ConfigureAwait(false);}catch{}}
    internal async Task RefreshBatch(string token,IEnumerable<string> hosts,CancellationToken ct=default)
    {
        var now=DateTime.UtcNow;
        var due=hosts.Where(id=>Guid.TryParse(id,out _)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id=>nextFetch.GetOrAdd(id,DateTime.MinValue)<=now).ToArray();
        foreach(var id in due)nextFetch[id]=now.AddMinutes(1);
        foreach(var chunk in due.Chunk(80))
        {var batch=await Post<Batch>(token,"capacity",new{hostIds=chunk},ct).ConfigureAwait(false);foreach(var s in batch.Capacities)Remember(s);}
    }
    internal void QueueRefresh(string token,IEnumerable<string> hosts)
    {
        var due=hosts.Where(id=>nextFetch.GetValueOrDefault(id)<=DateTime.UtcNow).ToArray();
        if(due.Length==0)return;
        // RefreshBatch marks hosts before its first await, preventing per-frame duplicate requests.
        _=QuietRefresh(token,due);
    }
    private async Task QuietRefresh(string token,string[] hosts)
    {try{await RefreshBatch(token,hosts).ConfigureAwait(false);}catch{/* Existing snapshots remain display-only. Admission always fails closed. */}}
}

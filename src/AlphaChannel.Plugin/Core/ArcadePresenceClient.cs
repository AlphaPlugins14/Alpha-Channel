using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin;

// Framework-thread state, asynchronous HTTP. A host reports the entire arcade
// party, so viewers create no separate presence and need no heartbeat traffic.
internal sealed class ArcadePresenceClient : IDisposable
{
    private readonly Configuration configuration;
    private readonly HttpClient client;
    private readonly CancellationTokenSource lifetime = new();
    private Task<Reply?>? pending;
    private int? submittedPlayers;
    private int desiredPlayers, publishedPlayers;
    private bool mayHaveSession;
    private DateTime nextHeartbeat, nextRead, retryAfter, changedAt;
    private DateTime receivedAt;
    private int? total;
    private long revision;
    private bool disposed;
    private sealed record Reply(int PlayingCount);

    internal ArcadePresenceClient(Configuration configuration, HttpClient? client = null)
    {
        this.configuration=configuration;
        this.client=client??PluginHttpClients.CreateMetadataClient();
        this.client.Timeout=TimeSpan.FromSeconds(10);
    }

    internal int? Count(DateTime now) => now-receivedAt<=TimeSpan.FromMinutes(5)?total:null;

    internal static int Contribution(bool loggedIn, bool active, bool viewing, bool hosting, int viewers) =>
        !loggedIn || !active || viewing ? 0 : hosting ? Math.Clamp(viewers,0,150)+1 : 1;

    internal void Tick(DateTime now, int players, bool visible)
    {
        if(disposed)return;
        players=Math.Clamp(players,0,151);
        if(players!=desiredPlayers) { desiredPlayers=players; changedAt=now; }
        if(pending is { IsCompleted:true })
        {
            var reply=pending.GetAwaiter().GetResult();
            if(reply is not null)
            {
                total=Math.Max(0,reply.PlayingCount);receivedAt=now;
                if(submittedPlayers is { } sent) { publishedPlayers=sent;mayHaveSession=sent>0; }
                nextRead=now.AddMinutes(2);
                nextHeartbeat=now.AddMinutes(2);
            }
            else retryAfter=now.AddMinutes(1);
            pending=null;submittedPlayers=null;
        }
        if(pending is not null || now<retryAfter)return;
        if((desiredPlayers!=publishedPlayers || desiredPlayers==0 && mayHaveSession) && now-changedAt>=TimeSpan.FromSeconds(2) ||
            desiredPlayers>0 && desiredPlayers==publishedPlayers && now>=nextHeartbeat)
        {
            Start(desiredPlayers==0?"DELETE":"POST",desiredPlayers,now);
        }
        else if(visible && now>=nextRead && desiredPlayers==publishedPlayers)
            Start("GET",null,now);
    }

    private void Start(string method,int? players,DateTime now)
    {
        if(players is >0 && string.IsNullOrEmpty(configuration.GuestPresenceInstallationId))
        {
            configuration.GuestPresenceInstallationId=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            configuration.Save();
        }
        if(method=="POST")mayHaveSession=true;
        submittedPlayers=players;
        pending=Send(method,players,NextRevision(now),lifetime.Token);
    }

    private long NextRevision(DateTime now) => revision=Math.Max(revision+1,new DateTimeOffset(now).ToUnixTimeMilliseconds());

    private async Task<Reply?> Send(string method,int? players,long requestRevision,CancellationToken token)
    {
        try
        {
            var origin=(string.IsNullOrWhiteSpace(configuration.GuestPresenceWorkerUrl)?GuestPresenceClient.DefaultUrl:
                configuration.GuestPresenceWorkerUrl).TrimEnd('/');
            using var request=new HttpRequestMessage(new HttpMethod(method),origin+(method=="GET"?"/arcade/count":"/arcade/session"));
            if(method!="GET")request.Content=JsonContent.Create(new { installationId=configuration.GuestPresenceInstallationId,
                players=players??0,revision=requestRevision });
            using var response=await client.SendAsync(request,token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<Reply>(new JsonSerializerOptions(JsonSerializerDefaults.Web),token).ConfigureAwait(false);
        }
        catch(Exception) { return null; }
    }

    public void Dispose()
    {
        if(disposed)return;
        disposed=true;lifetime.Cancel();
        // One bounded best-effort withdrawal on unload. Tombstone ordering
        // prevents the canceled heartbeat from reappearing if it arrived late.
        if(mayHaveSession)
        {
            var stopRevision=NextRevision(DateTime.UtcNow);
            _=Task.Run(async()=>
            {
                try { using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await Send("DELETE",0,stopRevision,timeout.Token).ConfigureAwait(false); }
                finally { client.Dispose(); }
            });
        }
        else client.Dispose();
    }
}

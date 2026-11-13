using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;

namespace AlphaChannel.Plugin;

internal sealed record CapacityJoinPrompt(string Host,string? Password,string Account,string Token,string Message,
    bool Full,bool AutoRetry=false,long RetryAt=0);

internal sealed partial class StreamClient
{
    private sealed record CapacityLease(string Host,string Permit,string Account,string Token,string JoinHost);
    private CapacityLease? capacityLease;
    private CapacityJoinPrompt? capacityPrompt;
    private Task? capacityTask;
    private readonly SemaphoreSlim capacityJoinGate=new(1,1);
    private long capacityJoinVersion;
    private long capacityAuthorizedVersion=-1;
    private const string CapacityDisplayPrefix="[[AC_CAPACITY_1]]";
    private RoomCapacitySnapshot? capacityDisplay;
    internal RoomCapacitySnapshot? CapacityDisplay=>Volatile.Read(ref capacityDisplay);
    internal CapacityJoinPrompt? CapacityPrompt=>Volatile.Read(ref capacityPrompt);
    internal event Action? OnCapacityBlocked;
    internal void CancelCapacityRetry()
    {
        Interlocked.Increment(ref capacityJoinVersion);
        var prompt=Interlocked.Exchange(ref capacityPrompt,null);
        if(prompt is not null)lock(lifecycleGate)
        {
            if(Mode==StreamMode.None&&pendingJoinHost==prompt.Host)
            {pendingJoinHost=null;pendingJoinPassword=null;HostId=null;}
        }
    }
    internal void EnableCapacityRetry()
    {
        var p=CapacityPrompt;
        if(p is {Full:true})Volatile.Write(ref capacityPrompt,p with{AutoRetry=true,RetryAt=Environment.TickCount64+20000});
    }
    private async Task<bool> PrepareCapacityJoin(StreamControl message)
    {
        var version=Volatile.Read(ref capacityJoinVersion);
        if(disposed)return false;
        try{await capacityJoinGate.WaitAsync(lifetime.Token).ConfigureAwait(false);}
        catch(OperationCanceledException){return false;}
        catch(ObjectDisposedException){return false;}
        try
        {
            var session=sessionProvider();var target=message.HostId;
            if(session is null||target is null||disposed||version!=Volatile.Read(ref capacityJoinVersion)||
                !string.Equals(target,pendingJoinHost,StringComparison.Ordinal)||Mode==StreamMode.Hosting||leavingRoom)return false;
            if(capacityAuthorizedVersion==version&&capacityLease is { } granted&&granted.Account==session.AccountId&&granted.Token==session.Token)return true;
            try
            {
                var grant=await RoomCapacityClient.Shared.Admit(session.Token,target,lifetime.Token).ConfigureAwait(false);
                if(version!=Volatile.Read(ref capacityJoinVersion)||sessionProvider()?.Token!=session.Token||leavingRoom||disposed)
                {if(grant.Permit is { } abandoned)_=ReleaseCapacityQuiet(session.Token,grant.HostId,abandoned);return false;}
                if(!grant.Allowed||grant.Permit is null)
                {
                    BlockCapacityJoin(session,grant.HostId,message.Password,"Watch party is currently full.",true);
                    return false;
                }
                PartyAdmission moderation;
                try { moderation=await PartyModerationClient.Admit(session.Token,grant.HostId,lifetime.Token).ConfigureAwait(false); }
                catch
                {
                    _=ReleaseCapacityQuiet(session.Token,grant.HostId,grant.Permit);
                    if(version==Volatile.Read(ref capacityJoinVersion)&&sessionProvider()?.Token==session.Token&&!disposed)
                        BlockCapacityJoin(session,target,message.Password,"Party permissions could not be checked. Please try again shortly.",false);
                    return false;
                }
                if(version!=Volatile.Read(ref capacityJoinVersion)||sessionProvider()?.Token!=session.Token||leavingRoom||disposed)
                {_=ReleaseCapacityQuiet(session.Token,grant.HostId,grant.Permit);return false;}
                if(!moderation.Allowed)
                {
                    _=ReleaseCapacityQuiet(session.Token,grant.HostId,grant.Permit);
                    BlockCapacityJoin(session,grant.HostId,message.Password,"You are banned from this host's Watch Parties.",false);
                    return false;
                }
                var joinHost=grant.HostUsername?.Trim();
                if(string.IsNullOrWhiteSpace(joinHost)&&!Guid.TryParse(target,out _))joinHost=target.Trim();
                if(string.IsNullOrWhiteSpace(joinHost)||Guid.TryParse(joinHost,out _))
                {
                    _=ReleaseCapacityQuiet(session.Token,grant.HostId,grant.Permit);
                    BlockCapacityJoin(session,target,message.Password,"The host's username could not be resolved. Refresh the party list and try again.",false);
                    return false;
                }
                CapacityLease? previous;
                lock(lifecycleGate)
                {
                    if(version!=Volatile.Read(ref capacityJoinVersion)||sessionProvider()?.Token!=session.Token||leavingRoom||disposed||pendingJoinHost!=target)
                    {_=ReleaseCapacityQuiet(session.Token,grant.HostId,grant.Permit);return false;}
                    previous=Interlocked.Exchange(ref capacityLease,new(grant.HostId,grant.Permit,session.AccountId,session.Token,joinHost));
                    Volatile.Write(ref moderationAdmission,new(grant.HostId,session.AccountId,session.Token,moderation.Revision));
                    automaticWatchPartyJoinsSuppressed=false;
                    capacityAuthorizedVersion=version;Volatile.Write(ref capacityPrompt,null);
                }
                if(previous is not null&&(previous.Host!=grant.HostId||previous.Permit!=grant.Permit))_=ReleaseCapacityQuiet(previous.Token,previous.Host,previous.Permit);
                return true;
            }
            catch(OperationCanceledException)when(lifetime.IsCancellationRequested){return false;}
            catch(Exception ex)
            {
                if(version==Volatile.Read(ref capacityJoinVersion)&&sessionProvider()?.Token==session.Token)
                    BlockCapacityJoin(session,target,message.Password,ex is InvalidOperationException?ex.Message:"Room capacity could not be checked. Please try again shortly.",false);
                return false;
            }
        }
        finally{capacityJoinGate.Release();}
    }
    private bool ApplyCapacityJoinTarget(ref StreamControl message,long version)
    {
        lock(lifecycleGate)
        {
            if(capacityLease is not { } lease||pendingJoinHost!=message.HostId||capacityAuthorizedVersion!=version)return false;
            // /rt accepts usernames; permit confirmation/release still uses lease.Host (account ID).
            pendingJoinHost=lease.JoinHost;HostId=lease.Host;message=message with{HostId=lease.JoinHost};
            return true;
        }
    }
    private void BlockCapacityJoin(CharacterSession session,string target,string? password,string message,bool full)
    {
        var previous=CapacityPrompt;
        var retry=full&&previous is {AutoRetry:true}&&previous.Account==session.AccountId;
        lock(lifecycleGate)
        {
            pendingJoinHost=null;pendingJoinPassword=null;HostId=null;Mode=StreamMode.None;
            Volatile.Write(ref capacityPrompt,new(target,password,session.AccountId,session.Token,message,full,retry,retry?Math.Max(Environment.TickCount64+250,previous!.RetryAt+20000):0));
        }
        if(!retry)OnCapacityBlocked?.Invoke();
    }
    private static async Task ReleaseCapacityQuiet(string token,string host,string permit)
    {try{await RoomCapacityClient.Shared.Release(token,host,permit,CancellationToken.None).ConfigureAwait(false);}catch{}}
    private void ReleaseCapacitySeat()
    {
        Interlocked.Increment(ref capacityJoinVersion);capacityAuthorizedVersion=-1;
        var lease=Interlocked.Exchange(ref capacityLease,null);
        if(lease is not null)_=ReleaseCapacityQuiet(lease.Token,lease.Host,lease.Permit);
    }
    private async Task RunCapacityAsync(CancellationToken ct)
    {
        string? lastHost=null,lastRoster=null;long nextReport=0,nextReportAttempt=0,nextRenew=0;
        while(!ct.IsCancellationRequested)
        {
            try
            {
                var session=sessionProvider();var now=Environment.TickCount64;var prompt=CapacityPrompt;
                if(prompt is not null&&(session?.AccountId!=prompt.Account||session.Token!=prompt.Token||Mode!=StreamMode.None))CancelCapacityRetry();
                else if(prompt is {AutoRetry:true}&&now>=prompt.RetryAt&&IsConnected&&WatchPartyJoinBlockReason is null)
                {
                    // JoinAsync keeps this retry context until admission succeeds or fails definitively.
                    await JoinAsync(prompt.Host,prompt.Password,capacityRetry:true).ConfigureAwait(false);
                }
                if(session is not null&&IsConnected&&Mode==StreamMode.Hosting)
                {
                    var roster=Roster.Select(p=>p.UserId).Where(id=>Guid.TryParse(id,out _)&&id!=session.AccountId).Distinct().Order().ToArray();
                    var key=string.Join(',',roster);
                    if(now>=nextReportAttempt&&(lastHost!=session.AccountId||key!=lastRoster||now>=nextReport))
                    {
                        nextReportAttempt=now+30000;
                        await RoomCapacityClient.Shared.Report(session.Token,session.AccountId,roster,!IsPrivate,ct).ConfigureAwait(false);
                        nextReportAttempt=now+4000;
                        lastHost=session.AccountId;lastRoster=key;nextReport=now+45000;
                        var display=RoomCapacityClient.Shared.Cached(session.AccountId);
                        if(display is not null)
                        {
                            Volatile.Write(ref capacityDisplay,display);
                            await SendAsync(new StreamControl{Type=SignalType.StreamReaction,Reaction=CapacityDisplayPrefix+
                                System.Text.Json.JsonSerializer.Serialize(display with{Permit=null,Allowed=false}),IsPrivate=true}).ConfigureAwait(false);
                        }
                    }
                }
                else {lastHost=null;lastRoster=null;}
                var lease=Volatile.Read(ref capacityLease);
                if(lease is not null&&(session?.AccountId!=lease.Account||session.Token!=lease.Token||Mode==StreamMode.Hosting))ReleaseCapacitySeat();
                else if(lease is not null&&Mode==StreamMode.Viewing&&HostId!=lease.Host&&now>=nextRenew&&IsConnected)
                {
                    nextRenew=now+10000;
                    // Re-admission renews existing members even when the room is over capacity.
                    var currentHost=HostId??lease.Host;
                    var grant=await RoomCapacityClient.Shared.Admit(lease.Token,currentHost,ct).ConfigureAwait(false);
                    if(grant.Allowed&&grant.Permit is { } permit&&Mode==StreamMode.Viewing&&HostId==currentHost)
                    {
                        await RoomCapacityClient.Shared.Confirm(lease.Token,currentHost,permit,ct).ConfigureAwait(false);
                        var updated=new CapacityLease(currentHost,permit,lease.Account,lease.Token,grant.HostUsername??lease.JoinHost);
                        if(Interlocked.CompareExchange(ref capacityLease,updated,lease)!=lease)_=ReleaseCapacityQuiet(lease.Token,currentHost,permit);
                        else if(currentHost!=lease.Host)_=ReleaseCapacityQuiet(lease.Token,lease.Host,lease.Permit);
                    }
                }
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch{/* Capacity service trouble never ejects an existing viewer. Retry at the next bounded tick. */}
            try{await Task.Delay(CapacityPrompt is {AutoRetry:true}?250:2000,ct).ConfigureAwait(false);}catch(OperationCanceledException){break;}
        }
    }
    private async Task ConfirmCapacitySeat()
    {
        var lease=Volatile.Read(ref capacityLease);if(lease is null)return;
        try{await RoomCapacityClient.Shared.Confirm(lease.Token,lease.Host,lease.Permit,lifetime.Token).ConfigureAwait(false);}catch{}
    }
    private void ReceiveCapacityDisplay(StreamControl message)
    {
        if(Mode!=StreamMode.Viewing||message.UserId!=HostId||message.Reaction is not { } payload||payload.Length>2048)return;
        try
        {
            var status=System.Text.Json.JsonSerializer.Deserialize<RoomCapacitySnapshot>(payload[CapacityDisplayPrefix.Length..]);
            if(status is null||status.HostId!=HostId||status.Capacity is not (10 or 25 or 35 or 50 or 80 or 150)||status.Viewers<0)return;
            status=status with{Permit=null,Allowed=false};
            Volatile.Write(ref capacityDisplay,status);RoomCapacityClient.Shared.RememberDisplay(status);
        }catch{}
    }
}

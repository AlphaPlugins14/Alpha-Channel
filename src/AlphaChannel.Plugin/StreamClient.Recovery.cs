using System.Net;
using System.Net.Http.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;

namespace AlphaChannel.Plugin;

internal sealed partial class StreamClient
{
    private volatile bool recoveryCheckPending;
    private bool recoveryServerSupported;
    private RoomRecoveryDto? recoveryOffer;
    private long waitingForHostUntil;
    private int recoveryDecisionBusy, recoveryNoticePending;
    private long recoveryDecisionDeadline;
    private string? leaveAcknowledgedKey;
    internal RoomRecoveryDto? RecoveryOffer => Volatile.Read(ref recoveryOffer);
    internal bool RecoveryDecisionBusy
    {
        get
        {
            if (Volatile.Read(ref recoveryDecisionBusy) == 0) return false;
            if (Environment.TickCount64 < Interlocked.Read(ref recoveryDecisionDeadline)) return true;
            Interlocked.Exchange(ref recoveryDecisionBusy, 0);
            return false;
        }
    }
    internal bool WaitingForHost => Interlocked.Read(ref waitingForHostUntil) > 0;
    internal bool RecoveryBlocked => recoveryCheckPending || RecoveryOffer is not null || HasPendingPartyLeave;
    internal bool PreservePartyOnShutdown => recoveryServerSupported && !HasPendingPartyLeave &&
        (Mode == StreamMode.Hosting || RecoveryOffer is not null);
    internal bool TakeRecoveryNotice() => Interlocked.Exchange(ref recoveryNoticePending, 0) != 0;
    internal bool TakeLeaveAcknowledgement() => Volatile.Read(ref leaveAcknowledgedKey) is not null;
    private void AcknowledgePartyLeave() => Volatile.Write(ref leaveAcknowledgedKey,
        configuration.RelayServerUrl.TrimEnd('/') + "|" + myAccountId);
    private string LeaveIntentKey => configuration.RelayServerUrl.TrimEnd('/') + "|" + sessionProvider()?.AccountId;
    private bool HasPendingPartyLeave => configuration.PendingPartyLeaves.ContainsKey(LeaveIntentKey);

    // Called only on the framework thread before deliberate departure.
    internal void RecordPartyLeaveIntent()
    {
        if (Mode != StreamMode.Hosting && RecoveryOffer is null) return;
        configuration.PendingPartyLeaves[LeaveIntentKey] = true;
        configuration.Save();
    }

    internal void CommitPartyLeaveAcknowledgement()
    {
        var key = Interlocked.Exchange(ref leaveAcknowledgedKey, null);
        if (key is not null && configuration.PendingPartyLeaves.TryRemove(key, out _)) configuration.Save();
    }

    private async Task CheckRoomRecoveryAsync(CharacterSession session, CancellationToken token)
    {
        using var http = Net.PluginHttpClients.CreateApiClient(configuration, session.Token);
        using var response = await http.GetAsync("/rooms/recovery", token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Original server / old deployments do not support recovery.
            recoveryCheckPending = false;
            recoveryServerSupported = false;
            if (HasPendingPartyLeave) AcknowledgePartyLeave();
            return;
        }
        response.EnsureSuccessStatusCode();
        recoveryServerSupported = true;
        var status = await response.Content.ReadFromJsonAsync<RoomRecoveryStatusDto>(token).ConfigureAwait(false)
            ?? throw new IOException("Missing room recovery status.");
        if (sessionProvider()?.Token != session.Token) return;
        if (HasPendingPartyLeave)
        {
            await SendAsync(new StreamControl { Type = SignalType.StreamLeave }).ConfigureAwait(false);
            return; // Acknowledgement unblocks fresh hosting; never show Resume after Leave.
        }
        if (status.Room is { } offer)
        {
            Interlocked.Exchange(ref recoveryDecisionBusy, 0);
            Volatile.Write(ref recoveryOffer, offer);
            Interlocked.Exchange(ref recoveryNoticePending, 1);
        }
        else if (Mode == StreamMode.Hosting || RecoveryOffer is not null)
        {
            Volatile.Write(ref recoveryOffer, null);
            ClearRoomForAuthenticationLoss();
        }
        recoveryCheckPending = false;
    }

    internal async Task DecideRoomRecoveryAsync(bool resume)
    {
        if (RecoveryOffer is not { } offer || !IsConnected || RecoveryDecisionBusy) return;
        Interlocked.Exchange(ref recoveryDecisionDeadline, Environment.TickCount64 + 10000);
        if (Interlocked.CompareExchange(ref recoveryDecisionBusy, 1, 0) != 0) return;
        try
        {
            await SendAsync(new StreamControl
            {
                Type = resume ? SignalType.StreamResume : SignalType.StreamRecoveryDecline,
                RecoveryId = offer.RecoveryId,
            }).ConfigureAwait(false);
        }
        catch { Interlocked.Exchange(ref recoveryDecisionBusy, 0); }
    }

    private void ReceiveRoomResumed(StreamControl message)
    {
        Interlocked.Exchange(ref waitingForHostUntil, 0);
        if (message.HostId != myAccountId) return;
        var offer = RecoveryOffer;
        if (offer is null) return;
        // Keep publication blocked until the framework has stopped stale playback.
        var state = offer.State ?? message;
        RoomDescription = state.Description; RoomLocation = state.Location;
        RoomKind = state.Kind ?? RoomKind.Public; IsPrivate = state.IsPrivate == true;
        Mode = StreamMode.Hosting;
        if (Guid.TryParse(state.HostingSessionId, out _)) HostingSessionId = state.HostingSessionId;
        HostId = null;
        lastHostState = CurrentRoomState = state with { Type = SignalType.StreamState, HostId = myAccountId, Url = null, Paused = true, PositionSeconds = 0 };
        Volatile.Write(ref recoveryOffer, null);
        recoveryCheckPending = false;
        Interlocked.Exchange(ref recoveryDecisionBusy, 0);
    }

    private void ClearRoomRecovery()
    {
        Volatile.Write(ref recoveryOffer, null);
        Interlocked.Exchange(ref waitingForHostUntil, 0);
        Interlocked.Exchange(ref recoveryDecisionBusy, 0);
    }
}

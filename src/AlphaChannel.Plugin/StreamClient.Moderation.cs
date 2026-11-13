using AlphaChannel.Plugin.Auth;

namespace AlphaChannel.Plugin;

internal sealed partial class StreamClient
{
    private readonly PartyModerationClient partyModeration = new();
    private PartyModerationIdentity? moderationAdmission;
    private Task? moderationTask;
    private volatile bool automaticWatchPartyJoinsSuppressed;
    internal bool AutomaticWatchPartyJoinsSuppressed => automaticWatchPartyJoinsSuppressed;
    internal event Action<string>? OnPartyModerated;

    private async Task RunPartyModeration(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var session = sessionProvider();
                var host = HostId;
                var party = ActivityBadgeTracker.ReadParty(CurrentRoomState?.Reason);
                var admission = Volatile.Read(ref moderationAdmission);
                if (Mode != StreamMode.Viewing || session is null || !Guid.TryParse(host, out _) || party is null)
                    partyModeration.Watch(null, RemoveModeratedViewer);
                else if (admission is not null && string.Equals(admission.Host, host, StringComparison.OrdinalIgnoreCase) && admission.Account == session.AccountId && admission.Token == session.Token)
                {
                    if (admission.Party != party)
                    {
                        lock (lifecycleGate)
                        {
                            if (!ReferenceEquals(moderationAdmission, admission) || Mode != StreamMode.Viewing || HostId != host) continue;
                            moderationAdmission = admission = admission with { Party = party };
                        }
                    }
                    partyModeration.Watch(admission, RemoveModeratedViewer);
                }
                else
                {
                    // Host transfer: the new host's bans apply, and the old subscription ends.
                    partyModeration.Watch(null, RemoveModeratedViewer);
                    try
                    {
                        var result = await PartyModerationClient.Admit(session.Token, host!, ct).ConfigureAwait(false);
                        lock (lifecycleGate)
                        {
                            if (!disposed && Mode == StreamMode.Viewing && HostId == host && sessionProvider()?.Token == session.Token)
                                moderationAdmission = admission = new(host!, session.AccountId, session.Token, 0, party);
                            else admission = null;
                        }
                        if (admission is not null)
                        {
                            if (!result.Allowed) await RemoveModeratedViewer(admission, "ban").ConfigureAwait(false);
                            else partyModeration.Watch(admission, RemoveModeratedViewer);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
                }
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { partyModeration.Dispose(); }
    }
    private async Task RemoveModeratedViewer(PartyModerationIdentity identity, string action)
    {
        Task leave;
        lock (lifecycleGate)
        {
            if (disposed || !ReferenceEquals(moderationAdmission, identity) || Mode != StreamMode.Viewing ||
                !string.Equals(HostId, identity.Host, StringComparison.OrdinalIgnoreCase) || sessionProvider()?.Token != identity.Token ||
                ActivityBadgeTracker.ReadParty(CurrentRoomState?.Reason) != identity.Party) return;
            automaticWatchPartyJoinsSuppressed = true;
            // LeaveAsync clears mode, pending joins, retries and admission synchronously.
            leave = LeaveAsync();
            OnEnded?.Invoke();
            OnPartyModerated?.Invoke(action == "ban" ? "You were banned from this host's Watch Parties." : "You were removed from this Watch Party.");
        }
        try { await leave.ConfigureAwait(false); } catch { }
    }
}

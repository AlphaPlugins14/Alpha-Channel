using System.Collections.Concurrent;
using AlphaChannel.Plugin.Core;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly PartyCountdownClient countdownClient = new();
    private readonly CancellationTokenSource countdownLifetime = new();
    private readonly ConcurrentQueue<Action> countdownCompletions = new();
    private readonly Dictionary<string, PartyCountdownView> directoryCountdowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> countdownDirectorySeen = new(StringComparer.OrdinalIgnoreCase);
    private PartyCountdownView? partyCountdown;
    private string? countdownHost, countdownHostToken, countdownMessage;
    private bool countdownWasHost, countdownBusy, countdownPollBusy;
    private int countdownGeneration, countdownMutation;
    private DateTime countdownNextPoll;
    private readonly Dictionary<string, DateTime> countdownHostNextPoll = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> countdownMissingPolls = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> countdownSignals = new(StringComparer.OrdinalIgnoreCase);
    private Guid? countdownPendingStart;
    private readonly HashSet<Guid> cancelledCountdowns = new();
    private Task countdownCancellation = Task.CompletedTask;
    private sealed record CountdownStart(Guid Id, Guid Session, Guid Party, Guid? Target, int PlaybackAttempt);
    private CountdownStart? countdownStart;

    private PartyCountdownView? VisiblePartyCountdown => partyCountdown is { Visible: true } view ? view : null;
    private bool CanStartPartyCountdown => stream.Mode == StreamMode.Hosting && !hostTransferPreparing &&
        !PlaybackActivityOwnsTv && !StudioOwnsPlaybackControls && !IsPlayingOwnIcecastDjStream && !countdownBusy;

    internal void TickPartyCountdown()
    {
        if (Volatile.Read(ref disposeStarted) != 0 || HalloweenCountdownSuppressed) return;
        var host = stream.Mode == StreamMode.Hosting ? CurrentSession?.AccountId : stream.Mode == StreamMode.Viewing ? stream.HostId : null;
        var hosting = stream.Mode == StreamMode.Hosting;
        if (host != countdownHost || hosting != countdownWasHost)
        {
            if (countdownWasHost && countdownHost is { } oldHost && countdownHostToken is { } token &&
                (countdownPendingStart ?? partyCountdown?.State.Id) is { } oldId) countdownCancellation = CancelCountdownRemoteAsync(token, oldHost, oldId);
            countdownGeneration++; countdownMutation++;
            countdownStart = null; countdownPendingStart = null; countdownBusy = countdownPollBusy = false;
            partyCountdown = null; countdownMessage = null; countdownNextPoll = default;
            countdownHost = host; countdownWasHost = hosting;
        }
        countdownHostToken = hosting ? CurrentSession?.Token : null;
        while (countdownCompletions.TryDequeue(out var complete)) complete();
        if (hosting && partyCountdown is { } current)
        {
            if (!Guid.TryParse(stream.HostingSessionId, out var session) || session != current.State.SessionId ||
                PlaybackActivityOwnsTv || StudioOwnsPlaybackControls || hostTransferPreparing ||
                countdownStart is { } start && start.PlaybackAttempt != video.PlaybackAttemptId)
                CancelPartyCountdown();
            else if (current.Remaining <= 0)
            {
                var action = countdownStart;
                // Disarm before calling any playback code: one authoritative start.
                countdownStart = null;
                CancelPartyCountdown();
                if (action?.Target is { } target && action.Id == current.State.Id && current.Remaining > -10 && action.Party == partyQueueState.PartyId &&
                    partyQueueState.Upcoming.FirstOrDefault(x => x.Id == target) is { } item && CanStartPartyCountdown)
                    PlayPartyQueueItem(item);
                else if (action?.Target is not null) countdownMessage = "Countdown finished; the chosen video is no longer available. Choose a video to play.";
            }
        }
        stream.HostPartyCountdownUrl = hosting && partyCountdown is { } advertised ? PartyCountdownSignal.Encode(advertised.State) : null;
        // TV visibility remains the viewer's choice. The overlay is cleared on
        // every role/room change and never owns an activity renderer.
        var visible = VisiblePartyCountdown;
        if (visible is not null && !hosting) RequestViewerTvSpawnPrompt(applyPlaybackStartBehaviour: true);
        screenController.Engine.SetPartyCountdown(visible);
        // The host's TV is spawned once on Start. A later Despawn stays hidden;
        // viewers retain their explicit TV visibility setting.
        if (visible is not null && !hosting && ViewerTvEnabled) screenController.Engine.ShowWaitingScreen();
        if (visible is not null && !hosting && ViewerTvEnabled && stream.CurrentRoomState is { } placement &&
            CanApplyRemoteTvPlacement(placement) && placement.ScreenX is { } x && placement.ScreenY is { } y && placement.ScreenZ is { } z &&
            placement.ScreenYaw is { } yaw && placement.ScreenScale is { } scale)
            screenController.Engine.ApplyRemoteScreenTransform(new(x, y, z), yaw, scale, placement.ScreenDisableFixedScaleRatio ?? false,
                placement.ScreenWidthScale, placement.ScreenHeightScale);

        if (countdownPollBusy || DateTime.UtcNow < countdownNextPoll) return;
        var now = DateTime.UtcNow;
        foreach (var id in countdownDirectorySeen.Where(x => now - x.Value > TimeSpan.FromSeconds(4)).Select(x => x.Key).ToArray())
        { countdownDirectorySeen.Remove(id); if (id != host) { countdownSignals.Remove(id); countdownMissingPolls.Remove(id); countdownHostNextPoll.Remove(id); } directoryCountdowns.Remove(id); }
        // Host already has the authoritative response from Start/StartNow. Idle rooms
        // never poll; viewers only discover countdowns advertised by ordinary room state.
        var candidates = countdownDirectorySeen.Keys.Concat(!hosting && host is not null ? new[] { host } : Array.Empty<string>());
        var hosts = candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(id =>
            !(hosting && id == host) && countdownSignals.ContainsKey(id) && (!countdownHostNextPoll.TryGetValue(id, out var next) || now >= next)).Take(32).ToArray();
        if (hosts.Length == 0) return;
        countdownPollBusy = true; countdownNextPoll = now.AddSeconds(1);
        foreach (var id in hosts) countdownHostNextPoll[id] = now.AddSeconds(15);
        _ = PollCountdownsAsync(hosts, countdownGeneration, countdownMutation);
    }

    private async Task PollCountdownsAsync(string[] hosts, int generation, int mutation)
    {
        try
        {
            var views = await countdownClient.GetAsync(hosts, countdownLifetime.Token).ConfigureAwait(false);
            countdownCompletions.Enqueue(() => {
                if (generation != countdownGeneration) return;
                countdownPollBusy = false;
                foreach (var host in hosts)
                {
                    var view = views.FirstOrDefault(x => x.State.HostId.Equals(host, StringComparison.OrdinalIgnoreCase));
                    if (!countdownSignals.TryGetValue(host, out var signal) || !PartyCountdownSignal.TryRead(signal, out var expectedId, out var expectedEnd)) continue;
                    if (view is not null && (cancelledCountdowns.Contains(view.State.Id) || view.State.Id != expectedId)) view = null;
                    // A stale in-flight response must not replace a StartNow deadline.
                    if (view is not null && view.State.EndsAt != expectedEnd) { countdownHostNextPoll[host] = DateTime.UtcNow.AddSeconds(3); continue; }
                    if (view is null)
                    {
                        countdownMissingPolls.TryGetValue(host, out var missing);
                        countdownMissingPolls[host] = ++missing;
                        countdownHostNextPoll[host] = missing >= 3 ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(5);
                    }
                    else
                    {
                        countdownMissingPolls.Remove(host);
                        countdownHostNextPoll[host] = view.Remaining <= 0 ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(Math.Clamp(view.Remaining / 2, 15, 60));
                    }
                    if (view is null) directoryCountdowns.Remove(host); else directoryCountdowns[host] = view;
                    if (host == countdownHost && mutation == countdownMutation && !countdownBusy)
                    {
                        if (countdownWasHost && view is not null && view.State.SessionId.ToString("D") != stream.HostingSessionId)
                        { _ = CancelCountdownRemoteAsync(countdownHostToken!, host, view.State.Id); view = null; }
                        partyCountdown = view;
                    }
                }
            });
        }
        catch (Exception error)
        {
            var delay = error is PartyCountdownRateLimitException limited ? limited.RetryAfter : TimeSpan.FromSeconds(15);
            countdownCompletions.Enqueue(() => {
                if (generation != countdownGeneration) return;
                countdownPollBusy = false; countdownNextPoll = DateTime.UtcNow + delay;
            });
        }
    }

    internal bool ObservePartyCountdownState(AlphaChannel.Contracts.StreamControl state)
    {
        if (stream.Mode != StreamMode.Viewing || stream.HostId is not { } host) return false;
        if (!PartyCountdownSignal.IsCountdown(state.Url))
        {
            countdownSignals.Remove(host); countdownMissingPolls.Remove(host); countdownHostNextPoll.Remove(host);
            partyCountdown = null;
            screenController.Engine.SetPartyCountdown(null);
            return false;
        }
        ObserveCountdownSignal(host, state.Url!);
        return true;
    }

    private void ObserveCountdownSignal(string host, string url)
    {
        if (countdownSignals.TryGetValue(host, out var previous) && previous == url) return;
        countdownSignals[host] = url;
        countdownMissingPolls.Remove(host);
        countdownHostNextPoll.Remove(host);
        countdownNextPoll = default;
        if (host == countdownHost && stream.Mode == StreamMode.Viewing) { partyCountdown = null; video.Stop(); }
    }

    private PartyCountdownView? DirectoryCountdown(string host, string? url)
    {
        if (!PartyCountdownSignal.IsCountdown(url))
        { countdownDirectorySeen.Remove(host); directoryCountdowns.Remove(host); return null; }
        countdownDirectorySeen[host] = DateTime.UtcNow;
        if (host == countdownHost && stream.Mode == StreamMode.Hosting) return VisiblePartyCountdown;
        ObserveCountdownSignal(host, url!);
        if (host == countdownHost) return VisiblePartyCountdown;
        return directoryCountdowns.TryGetValue(host, out var view) && view.Visible ? view : null;
    }

    private void BeginPartyCountdown(int seconds, string label, bool playNext)
    {
        if (!CanStartPartyCountdown || CurrentSession is not { } session || !Guid.TryParse(stream.HostingSessionId, out var roomSession)) return;
        var target = playNext ? partyQueueState.Upcoming.FirstOrDefault() : null;
        if (playNext && (target is null || !partyQueueDiscovered)) { countdownMessage = "Add a video to the party queue first."; return; }
        if (target is not null && (IsBroadcastPartyMedia(PartyEntry(target)) || IsPartyLiveMedia(PartyEntry(target))))
        { countdownMessage = "Use Countdown only for live streams and activities."; return; }
        var id = Guid.NewGuid(); var request = CaptureAccountRequest(session);
        var context = new CountdownStart(id, roomSession, partyQueueState.PartyId, target?.Id, video.PlaybackAttemptId);
        countdownBusy = true; countdownPendingStart = id; countdownMessage = null;
        var generation = countdownGeneration; countdownMutation++;
        // Labels are public directory metadata. Locked/private rooms stay generic.
        var publicLabel = stream.RoomKind == AlphaChannel.Contracts.RoomKind.Locked || stream.IsPrivate ? "Starting soon" : label.Trim();
        _ = PublishCountdownAsync(request, context, Math.Clamp(seconds, 1, 3600), publicLabel, generation);
    }

    private async Task PublishCountdownAsync(AccountRequest request, CountdownStart context, int seconds, string label, int generation)
    {
        try
        {
            var views = await countdownClient.StartAsync(request.Session.Token, request.Session.AccountId, context.Id, context.Session, seconds, label, countdownLifetime.Token).ConfigureAwait(false);
            countdownCompletions.Enqueue(() => {
                if (cancelledCountdowns.Contains(context.Id) || countdownPendingStart != context.Id ||
                    !IsCurrentAccountRequest(request) || generation != countdownGeneration || stream.Mode != StreamMode.Hosting ||
                    stream.HostingSessionId != context.Session.ToString("D"))
                { _ = CancelCountdownRemoteAsync(request.Session.Token, request.Session.AccountId, context.Id); return; }
                countdownBusy = false; countdownPendingStart = null;
                if (!CanStartPartyCountdown || video.PlaybackAttemptId != context.PlaybackAttempt || views.FirstOrDefault(x => x.State.Id == context.Id) is not { } view)
                { _ = CancelCountdownRemoteAsync(request.Session.Token, request.Session.AccountId, context.Id); countdownMessage = "Content changed. Start the countdown again when ready."; return; }
                var hadTv = screenController.Engine.IsActive;
                PartyPlaybackQueue?.StopPlayback();
                partyQueueNextReconcile = DateTime.MinValue;
                countdownStart = context with { PlaybackAttempt = video.PlaybackAttemptId };
                partyCountdown = view;
                stream.HostPartyCountdownUrl = PartyCountdownSignal.Encode(view.State);
                if (!hadTv) screenController.Engine.RecenterScreen(manualAdjustment: false);
                screenController.Engine.ShowWaitingScreen();
                countdownNextPoll = DateTime.UtcNow.AddSeconds(1);
            });
        }
        catch (Exception)
        {
            countdownCompletions.Enqueue(() => {
                if (generation != countdownGeneration || !IsCurrentAccountRequest(request) || countdownPendingStart != context.Id) return;
                countdownBusy = false; countdownPendingStart = null;
                countdownMessage = "Couldn't start the countdown. Please try again.";
                _ = CancelCountdownRemoteAsync(request.Session.Token, request.Session.AccountId, context.Id);
            });
        }
    }

    private void CancelPartyCountdown()
    {
        var id = countdownPendingStart ?? partyCountdown?.State.Id;
        if (id is { } cancelled) { if (cancelledCountdowns.Count >= 64) cancelledCountdowns.Clear(); cancelledCountdowns.Add(cancelled); }
        countdownMutation++; countdownPendingStart = null; countdownStart = null; partyCountdown = null;
        countdownBusy = false;
        stream.HostPartyCountdownUrl = null;
        if (id is { } countdownId && countdownWasHost && countdownHost is { } host && countdownHostToken is { } token)
            countdownCancellation = CancelCountdownRemoteAsync(token, host, countdownId);
    }
    private async Task CancelCountdownRemoteAsync(string token, string host, Guid id)
    {
        try { await countdownClient.CancelAsync(token, host, id).ConfigureAwait(false); }
        catch (Exception) { /* The service also expires countdowns after their deadline. */ }
    }
    private void StartPartyCountdownNow()
    {
        if (VisiblePartyCountdown is not { } current || !CanStartPartyCountdown || CurrentSession is not { } session) return;
        countdownBusy = true; countdownMutation++;
        var generation = countdownGeneration;
        _ = Task.Run(async () => {
            try {
                var views = await countdownClient.StartNowAsync(session.Token, session.AccountId, current.State.Id, countdownLifetime.Token);
                countdownCompletions.Enqueue(() => {
                    if (generation != countdownGeneration) return;
                    if (partyCountdown?.State.Id == current.State.Id) { countdownBusy = false; partyCountdown = views.FirstOrDefault(x => x.State.Id == current.State.Id); stream.HostPartyCountdownUrl = partyCountdown is { } updated ? PartyCountdownSignal.Encode(updated.State) : null; }
                });
            } catch (Exception) { countdownCompletions.Enqueue(() => { if (generation == countdownGeneration && partyCountdown?.State.Id == current.State.Id) { countdownBusy = false; countdownMessage = "Couldn't update the countdown. Please try again."; } }); }
        });
    }
    private void DisposePartyCountdown()
    {
        CancelPartyCountdown(); countdownLifetime.Cancel();
        _ = FinishCountdownDisposalAsync(countdownCancellation);
    }
    private async Task FinishCountdownDisposalAsync(Task cancel)
    {
        try { await cancel.ConfigureAwait(false); }
        finally { countdownClient.Dispose(); countdownLifetime.Dispose(); }
    }
}

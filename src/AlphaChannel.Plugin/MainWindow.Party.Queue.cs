using AlphaChannel.Plugin.Core;
using AlphaChannel.Plugin.Video;
using System.Collections.Concurrent;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    internal AetherStreamQueue? PartyPlaybackQueue { get; private set; }
    private PartyQueueState partyQueueState = new();
    private PartyQueueNoticeTracker partyQueueNoticeTracker = new();
    private readonly PartyQueueClient partyQueueClient = new();
    private PartyQueueRecoveryStorage? partyQueueRecoveryStorage;
    private PartyQueueRecoveryStorage QueueRecoveryStorage => partyQueueRecoveryStorage ??=
        new(Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "PartyQueueRecovery"));
    private readonly CancellationTokenSource partyQueueLifetime = new();
    private readonly ConcurrentQueue<PartyQueueNetworkResult> partyQueueReplies = new();
    private readonly ConcurrentQueue<string> partyQueueMessages = new();
    private readonly Dictionary<Guid, PartyQueueItem> partyQueueEntries = new();
    private readonly HashSet<Guid> partyQueueAcknowledged = new();
    private string? partyQueueHost, partyQueueSessionToken;
    private int partyQueueSyncFailures;
    private bool partyQueueWasHost, partyQueueBusy, partyQueueConnected;
    private DateTime partyQueueNextSync, partyQueueRecoverySaved;
    private string? partyQueueNotice;
    private Guid? partyQueueLastCurrent;
    private string? partyQueueFailure;
    private bool partyQueueForgotten;
    private PartyQueueRecovery? partyQueueAvailableRecovery;
    private bool partyQueueDiscovered;
    private HashSet<Guid> partyQueueInitialIds = new();
    private int partyQueueGeneration, partyQueueSubscriptionGeneration = -1;
    private DateTime partyQueueNextReconcile;
    private long partyQueueSavedRevision = -1, partyQueuePublishedRevision = -1;
    private string partyQueuePublishedMembers = "";
    private DateTime partyQueueLeaseRenewal, partyQueueNextPublishCheck;

    internal void TickPartyQueue()
    {
        if (disposeStarted != 0) return;
        while (partyQueueMessages.TryDequeue(out var message)) partyQueueNotice = message;
        var hosting = stream.Mode == StreamMode.Hosting;
        var active = hosting || stream.Mode == StreamMode.Viewing;
        var host = active ? hosting ? CurrentSession?.AccountId : stream.HostId : null;
        if (host != partyQueueHost || hosting != partyQueueWasHost || partyQueueSessionToken != CurrentSession?.Token)
        {
            SavePartyQueueRecovery();
            if (partyQueueWasHost && stream.Mode == StreamMode.Viewing && partyQueueHost is { } previousHost && CurrentSession is { } previousSession)
                _ = EndPartyQueueAsync(previousSession.Token, previousHost);
            DisposePartyPlaybackQueue();
            partyQueueEntries.Clear(); partyQueueLastCurrent = null;
            if (!active || partyQueueHost is null)
            {
                partyQueueState = new();
                partyQueueNoticeTracker = new();
                partyQueueEntries.Clear(); partyQueueAcknowledged.Clear();
                partyQueueLastCurrent = null;
            }
            partyQueueHost = host; partyQueueWasHost = hosting; partyQueueSessionToken = CurrentSession?.Token; partyQueueSyncFailures = 0;
            partyQueueClient.Subscribe(null, null);
            partyQueuePublishedRevision = -1; partyQueuePublishedMembers = "";
            partyQueueLeaseRenewal = partyQueueNextPublishCheck = DateTime.MinValue;
            partyQueueGeneration++;
            partyQueueDuplicate = null;
            partyQueueExportSelection.Clear(); partyQueueNotice = null;
            partyQueueBusy = false;
            partyQueueDiscovered = false;
            partyQueueSavedRevision = -1; partyQueueNextReconcile = DateTime.MinValue;
            partyQueueInitialIds = partyQueueState.Upcoming.Select(x => x.Id).ToHashSet();
            partyQueueConnected = false; partyQueueNextSync = DateTime.MinValue;
            partyQueueForgotten = false;
            if (hosting)
            {
                partyQueueAvailableRecovery = CurrentSession is { } recoverySession
                    ? QueueRecoveryStorage.Read(recoverySession.AccountId, Plugin.Cfg.PartyQueueRecovery) : null;
                var current = personalQueue.Current;
                personalQueue.ReleaseCurrentPlaybackForHostTransfer();
                PartyPlaybackQueue = new AetherStreamQueue(video, Array.Empty<VideoQueueRecord>(), temporary: true);
                if (current is not null) PartyPlaybackQueue.AdoptCurrentPlaybackForHostTransfer(current);
                PartyPlaybackQueue.TemporaryPlaybackFailed = failed => {
                    partyQueueFailure = "Playback failed; skipped.";
                    partyQueueNotice = $"Skipped: {failed.Title}";
                };
                ApplyPartyUpcomingToPlayback();
            }
        }
        if (!active || host is null || partyQueueForgotten || CurrentSession is not { } session)
        { partyQueueClient.Subscribe(null, null); partyQueueSubscriptionGeneration = -1; return; }
        if (partyQueueSubscriptionGeneration != partyQueueGeneration)
        {
            partyQueueSubscriptionGeneration = partyQueueGeneration;
            var watchGeneration = partyQueueGeneration;
            partyQueueClient.Subscribe(session.Token, host,
                reply => partyQueueReplies.Enqueue(new(host, hosting, reply, [], null, watchGeneration, Push: true)),
                () => partyQueueReplies.Enqueue(new(host, hosting, null, [], "Queue connection interrupted; reconnecting…", watchGeneration, Push: true)));
        }
        // Keep observing leave/rejoin/role changes during handover so its
        // generation checks remain valid, but freeze queue reconciliation/sync.
        if (hostTransferPreparing) return;
        while (partyQueueReplies.TryDequeue(out var result))
        {
            if (result.HostId != host || result.Hosting != hosting || result.Generation != partyQueueGeneration) continue;
            if (!result.Push) partyQueueBusy = false;
            if (result.Error is not null) {
                partyQueueConnected = false; partyQueueNotice = result.Error;
                if (!result.Push) {
                    partyQueuePublishedRevision = -1;
                    partyQueueNextSync = DateTime.UtcNow.AddSeconds(Math.Min(120, Math.Pow(2, Math.Min(++partyQueueSyncFailures, 7))) + Random.Shared.NextDouble());
                }
                continue;
            }
            if (result.Reply?.Snapshot is not { } snapshot || !PartyQueueState.Valid(snapshot)) continue;
            if (hosting && partyQueueAvailableRecovery is null && result.Reply.RecoverySnapshot is { } recovery &&
                recovery.PartyId != snapshot.PartyId && PartyQueueState.Valid(recovery))
                partyQueueAvailableRecovery = new(session.AccountId,DateTime.UtcNow,recovery);
            if (!result.Push) partyQueueSyncFailures = 0;
            if (!hosting && partyQueueDiscovered && snapshot.PartyId == partyQueueState.PartyId && snapshot.Revision < partyQueueState.Revision) continue;
            if (!hosting || !partyQueueDiscovered || snapshot.PartyId != partyQueueState.PartyId || snapshot.Revision > partyQueueState.Revision)
            {
                var locallyAdded = hosting ? partyQueueState.Upcoming.Where(x => snapshot.PartyId != partyQueueState.PartyId ||
                    !partyQueueInitialIds.Contains(x.Id)).ToArray() : [];
                if (hosting) partyQueueState = new();
                partyQueueState.ApplySnapshot(snapshot, true);
                var serverIds = snapshot.Upcoming.Concat(snapshot.Requests).Concat(snapshot.Played).Select(x => x.Id).ToHashSet();
                foreach (var item in locallyAdded.Where(x => !serverIds.Contains(x.Id) && snapshot.Current?.Id != x.Id))
                    partyQueueState.Add(item, true, session.AccountId, session.DisplayName);
                if (hosting) ApplyPartyUpcomingToPlayback();
            }
            foreach (var ack in result.Acknowledged) partyQueueAcknowledged.Remove(ack);
            foreach (var notice in partyQueueNoticeTracker.Observe(snapshot.PartyId, result.Reply.Notices))
            {
                AppendPartyQueueChatNotice(notice, DateTime.Now);
            }
            if (hosting)
            {
                foreach (var action in result.Reply.Actions ?? [])
                {
                    if (!stream.Roster.Any(x => x.UserId.Equals(action.SenderId, StringComparison.OrdinalIgnoreCase))) continue;
                    var command = new PartyQueueCommand(action.Kind, action.PartyId, action.ActionId, action.Item, action.ItemId);
                    var wasEmpty = PartyQueueEmptyForAutoStart;
                    if (partyQueueState.HandleViewer(command, action.SenderId, action.SenderName, out var error))
                    {
                        ApplyPartyUpcomingToPlayback();
                        ResumePartyQueueAfterAddition(wasEmpty && command.Kind == "add");
                    }
                    else if (error is not null) partyQueueNotice = error;
                    partyQueueAcknowledged.Add(action.ActionId);
                }
            }
            partyQueueConnected = true;
            partyQueueDiscovered = true;
            if (partyQueueNotice?.Contains("queue", StringComparison.OrdinalIgnoreCase) == true ||
                partyQueueNotice?.StartsWith("Waiting", StringComparison.Ordinal) == true) partyQueueNotice = null;
        }
        if (hosting && PartyPlaybackQueue is { } playback)
        {
            playback.TemporaryAutoPlay = PartyQueueMayAutoPlay();
            if (DateTime.UtcNow >= partyQueueNextReconcile)
            {
                partyQueueNextReconcile = DateTime.UtcNow.AddMilliseconds(250);
                ReconcilePartyPlayback(playback, session.AccountId, session.DisplayName);
            }
            if (partyQueueDiscovered && !partyQueueForgotten && partyQueueSavedRevision != partyQueueState.Revision &&
                partyQueueState.Upcoming.Count + partyQueueState.Requests.Count + partyQueueState.Played.Count + (partyQueueState.Current is null ? 0 : 1) > 0 &&
                DateTime.UtcNow - partyQueueRecoverySaved > TimeSpan.FromSeconds(20))
            {
                partyQueueRecoverySaved = DateTime.UtcNow;
                partyQueueSavedRevision = partyQueueState.Revision;
                QueueRecoveryStorage.Save(session.AccountId, new(session.AccountId, DateTime.UtcNow, partyQueueState.Snapshot()));
            }
        }
        // Viewers receive the initial snapshot and every later change over the
        // subscription. The host publishes only dirty state plus an hourly lease.
        if (!hosting || partyQueueBusy || DateTime.UtcNow < partyQueueNextSync || DateTime.UtcNow < partyQueueNextPublishCheck) return;
        partyQueueNextPublishCheck = DateTime.UtcNow.AddMilliseconds(500);
        var members = stream.Roster.Select(x => x.UserId.ToLowerInvariant()).Distinct().OrderBy(x => x).Take(64).ToArray();
        var memberKey = string.Join(",", members);
        if (partyQueueDiscovered && partyQueuePublishedRevision == partyQueueState.Revision &&
            partyQueuePublishedMembers == memberKey && partyQueueAcknowledged.Count == 0 && DateTime.UtcNow < partyQueueLeaseRenewal) return;
        partyQueueBusy = true;
        partyQueueNextSync = DateTime.UtcNow.AddSeconds(1);
        partyQueueLeaseRenewal = DateTime.UtcNow.AddHours(1);
        partyQueuePublishedRevision = partyQueueState.Revision;
        partyQueuePublishedMembers = memberKey;
        var sent = partyQueueState.Snapshot();
        var acknowledgements = partyQueueAcknowledged.Take(100).ToArray();
        _ = SyncPartyQueueAsync(session.Token, host, hosting, sent, members, acknowledgements, !partyQueueDiscovered, partyQueueGeneration);
    }

    private async Task SyncPartyQueueAsync(string token, string host, bool hosting, PartyQueueSnapshot? snapshot,
        string[] members, Guid[] acknowledged, bool discover, int generation)
    {
        try
        {
            var reply = await partyQueueClient.SyncAsync(token, host, snapshot, members, acknowledged, partyQueueLifetime.Token, discover).ConfigureAwait(false);
            partyQueueReplies.Enqueue(new(host, hosting, reply, acknowledged, null, generation));
        }
        catch (OperationCanceledException) { partyQueueReplies.Enqueue(new(host, hosting, null, [], "Queue sync timed out; retrying…", generation)); }
        catch (Exception e) { partyQueueReplies.Enqueue(new(host, hosting, null, [], e is InvalidOperationException ? e.Message : "Queue sync unavailable. Playback continues; retrying…", generation)); }
    }

    private PartyQueueItem PartyItem(VideoQueueEntry entry, string id, string name) => new(Guid.NewGuid(),
        entry.Url, PartyQueueState.Clip(entry.Title, 240), PartyQueueState.Clip(entry.Source, 128),
        entry.Duration?.TotalSeconds is { } seconds && double.IsFinite(seconds) ? Math.Clamp(seconds, 0, 604800) : null,
        entry.ThumbnailUrl is { Length: <= 512 } thumb ? thumb : null, id, PartyQueueState.Clip(name, 64));

    private void ReconcilePartyPlayback(AetherStreamQueue playback, string accountId, string displayName)
    {
        var current = playback.Current;
        var remappedCurrent = current is not null && partyQueueEntries.TryGetValue(current.Id, out var mappedCurrent) &&
            mappedCurrent.Id != partyQueueState.Current?.Id;
        if (partyQueueLastCurrent != current?.Id || remappedCurrent)
        {
            if (partyQueueLastCurrent is { } previousId)
                partyQueueState.ReleaseCurrent(playback.Entries.Any(x => x.Id == previousId), partyQueueFailure);
            partyQueueFailure = null;
            partyQueueState.Current = null;
            partyQueueLastCurrent = current?.Id;
            if (current is not null && !video.IsAudioOnly && !video.IsPlayingGame && !video.IsPlayingBrowser && !video.IsPlayingWhiteboard &&
                !video.IsPlayingLocalVideo && !IsBroadcastPartyMedia(current) && PartyQueueState.ValidItem(PartyItem(current, accountId, displayName)))
            {
                if (!partyQueueEntries.TryGetValue(current.Id, out var item)) item = PartyItem(current, accountId, displayName);
                partyQueueEntries[current.Id] = item;
                partyQueueState.Upcoming.RemoveAll(x => x.Id == item.Id);
                // The transferred current item is already in the shared snapshot.
                if (partyQueueState.Played.Any(x => x.Id == item.Id)) item = item with { Id = Guid.NewGuid() };
                partyQueueState.Current = item; partyQueueEntries[current.Id] = item;
            }
            partyQueueState.Changed();
        }
        if (current is not null && partyQueueState.Current is { } active)
        {
            var updated = RefreshPartyItem(active, current);
            if (updated != active) { partyQueueState.Current = updated; partyQueueEntries[current.Id] = updated; partyQueueState.Changed(); }
        }
        var upcoming = new List<PartyQueueItem>();
        foreach (var entry in playback.Entries.Where(x => x.Id != current?.Id).Take(100))
        {
            if (!partyQueueEntries.TryGetValue(entry.Id, out var item)) item = PartyItem(entry, accountId, displayName);
            if (!PartyQueueState.ValidItem(item)) continue;
            item = RefreshPartyItem(item, entry);
            partyQueueEntries[entry.Id] = item; upcoming.Add(item);
        }
        var capacity = 100 - partyQueueState.Requests.Count - (partyQueueState.Current is null ? 0 : 1);
        var overLimit = upcoming.Count > capacity;
        if (overLimit) { upcoming = upcoming.Take(capacity).ToList(); partyQueueNotice = "Party queue limit reached (100 items)."; }
        while (partyQueueState.Played.Count > 0 && upcoming.Count + partyQueueState.Played.Count > capacity) partyQueueState.Played.RemoveAt(0);
        if (!partyQueueState.Upcoming.SequenceEqual(upcoming))
        { partyQueueState.Upcoming.Clear(); partyQueueState.Upcoming.AddRange(upcoming); partyQueueState.Changed(); }
        if (overLimit) ApplyPartyUpcomingToPlayback();
        var liveIds = playback.Entries.Select(x => x.Id).Append(current?.Id ?? Guid.Empty).ToHashSet();
        foreach (var id in partyQueueEntries.Keys.Where(x => !liveIds.Contains(x)).ToArray()) partyQueueEntries.Remove(id);
    }
    private static PartyQueueItem RefreshPartyItem(PartyQueueItem item, VideoQueueEntry entry) => item with {
        Title = PartyQueueState.Clip(entry.Title, 240), Source = PartyQueueState.Clip(entry.Source, 128),
        DurationSeconds = entry.Duration?.TotalSeconds is { } d && double.IsFinite(d) ? Math.Clamp(d, 0, 604800) : null,
        ThumbnailUrl = entry.ThumbnailUrl is { Length: <= 512 } t && Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? t : null };

    private void ApplyPartyUpcomingToPlayback()
    {
        if (PartyPlaybackQueue is not { } playback) return;
        var existing = playback.Entries.ToDictionary(x => x.Id);
        var items = partyQueueState.Upcoming.Select(item => {
            var match = partyQueueEntries.FirstOrDefault(x => x.Value.Id == item.Id);
            if (existing.TryGetValue(match.Key, out var found)) return found;
            var entry = PartyEntry(item); partyQueueEntries[entry.Id] = item; return entry;
        }).ToArray();
        playback.ReplaceTemporaryUpcoming(items);
        if (playback.Current is { } current && partyQueueState.Current is { } shared &&
            PartyQueueState.SameUrl(current.Url, shared.Url)) partyQueueEntries[current.Id] = shared;
        playback.TemporaryAutoPlay = PartyQueueMayAutoPlay();
    }
    private bool PartyQueueMayAutoPlay() => VisiblePartyCountdown is null && partyQueueState.AutoPlay && !video.IsAudioOnly &&
        !screenController.Engine.HasActiveNonVideoPlayback && !video.IsPlayingLocalVideo && !IsBroadcastPartyMedia(PartyPlaybackQueue?.Current);
    private static VideoQueueEntry PartyEntry(PartyQueueItem item) => new(item.Url, item.Title, item.Source,
        item.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null, item.ThumbnailUrl);
    private static VideoQueueRecord PartyRecord(PartyQueueItem item) => new() { Url = item.Url, Title = item.Title,
        Source = item.Source, DurationSeconds = item.DurationSeconds, ThumbnailUrl = item.ThumbnailUrl };

    private void AddPartyItem(PartyQueueItem item, bool allowDuplicate = false)
    {
        if (CurrentSession is not { } session || partyQueueHost is not { } host) return;
        if (!allowDuplicate && partyQueueState.Upcoming.Concat(partyQueueState.Requests).Any(x => PartyQueueState.SameUrl(x.Url, item.Url)))
        {
            partyQueueNotice = "That video is already queued or requested.";
            if (stream.Mode == StreamMode.Hosting) partyQueueDuplicate = item;
            return;
        }
        if (stream.Mode == StreamMode.Hosting)
        {
            var wasEmpty = PartyQueueEmptyForAutoStart;
            partyQueueNotice = partyQueueState.Add(item, true, session.AccountId, session.DisplayName, allowDuplicate);
            ApplyPartyUpcomingToPlayback(); partyQueueNextSync = DateTime.MinValue;
            if (partyQueueNotice is null) ResumePartyQueueAfterAddition(wasEmpty);
        }
        else _ = SendPartyQueueActionAsync(session.Token, host, new("add", partyQueueState.PartyId, Guid.NewGuid(), item));
    }
    private async Task SendPartyQueueActionAsync(string token, string host, PartyQueueCommand command)
    {
        try { await partyQueueClient.CommandAsync(token, host, command, partyQueueLifetime.Token).ConfigureAwait(false);
            partyQueueMessages.Enqueue(command.Kind == "add" ? "Sent to the party queue." : "Withdrawal sent."); }
        catch (Exception e) { partyQueueMessages.Enqueue(e is InvalidOperationException ? e.Message : "Couldn't send the queue request. Please try again."); }
    }
    private void PartyQueueChanged()
    { partyQueueState.Changed(); ApplyPartyUpcomingToPlayback(); partyQueueNextSync = DateTime.MinValue; }
    private void DisposePartyPlaybackQueue()
    { PartyPlaybackQueue?.Dispose(); PartyPlaybackQueue = null; }
    private void DisposePartyQueueConnection()
    {
        SavePartyQueueRecovery();
        partyQueueLifetime.Cancel(); partyQueueClient.Dispose(); partyQueueLifetime.Dispose();
    }
    private void SavePartyQueueRecovery()
    {
        if (partyQueueWasHost && !partyQueueForgotten && partyQueueDiscovered && partyQueueHost is { } account)
            QueueRecoveryStorage.Save(account, new(account, DateTime.UtcNow, partyQueueState.Snapshot()));
    }
    private void ForgetEndedPartyQueue()
    {
        CancelPartyCountdown();
        if (partyQueueWasHost && partyQueueHost is { } host && CurrentSession is { } session)
        {
            partyQueueForgotten = true;
            QueueRecoveryStorage.Save(session.AccountId, null);
            _ = EndPartyQueueAsync(session.Token, host);
        }
    }
    private async Task EndPartyQueueAsync(string token, string host)
    { try { await partyQueueClient.EndAsync(token, host).ConfigureAwait(false); } catch { /* Worker also expires abandoned rooms. */ } }
}

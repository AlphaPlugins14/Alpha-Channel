using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Core;
using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool hostTransferPreparing;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> hostTransferMessages = new();
    private void ReportHostTransferNotice(string message)
    {
        partyQueueMessages.Enqueue(message);
        hostTransferMessages.Enqueue(message);
    }
    private Task TransferWatchPartyHostAsync(string userId)
    {
        if (stream.Mode != StreamMode.Hosting || hostTransferPreparing) return Task.CompletedTask;
        if (CurrentContentBlocksHostTransfer())
        {
            hostTransferNoticeText = null;
            hostTransferNoticeUntilUtc = DateTime.UtcNow.AddSeconds(6);
            return Task.CompletedTask;
        }
        return TransferPartyQueueAndHostAsync(userId);
    }

    // Framework thread only; capture progress at handover, after the worker awaits.
    private StreamControl CaptureHostTransferState()
    {
        var engine = screenController.Engine;
        var state = stream.CurrentRoomState ?? new StreamControl { Type = SignalType.StreamState };
        var current = queue.Current;
        var (position, _, paused) = video.GetProgress();
        var publishedUrl = current?.Url ?? state.Url;
        if (current is not null && engine.IsAudioOnly)
        {
            publishedUrl = BuildPartyAudioPresentationUrl(current.Url);
            if (VideoPlayer.IsNonSeekableLiveSource(current.Source) && !publishedUrl.Contains("ac_live=1", StringComparison.Ordinal))
                publishedUrl += publishedUrl.Contains('#') ? "&ac_live=1" : "#ac_live=1";
        }
        state = state with
        {
            Url = publishedUrl,
            IsPrivate = stream.IsPrivate,
            PositionSeconds = current is null ? state.PositionSeconds : position,
            Paused = current is null ? state.Paused : paused,
            MediaTitle = current?.Title ?? state.MediaTitle,
            MediaThumbnailUrl = current?.ThumbnailUrl ?? state.MediaThumbnailUrl,
            SubtitleLanguage = stream.SubtitleLanguage,
            SubtitleName = stream.SubtitleName,
            ScreenX = engine.IsActive ? engine.ScreenPosition.X : state.ScreenX,
            ScreenY = engine.IsActive ? engine.ScreenPosition.Y : state.ScreenY,
            ScreenZ = engine.IsActive ? engine.ScreenPosition.Z : state.ScreenZ,
            ScreenYaw = engine.IsActive ? engine.ScreenYaw : state.ScreenYaw,
            ScreenScale = engine.IsActive ? engine.ScreenScale : state.ScreenScale,
            ScreenDisableFixedScaleRatio = engine.IsActive ? engine.DisableFixedScreenScaleRatio : state.ScreenDisableFixedScaleRatio,
            ScreenWidthScale = engine.IsActive ? engine.ScreenWidthScale : state.ScreenWidthScale,
            ScreenHeightScale = engine.IsActive ? engine.ScreenHeightScale : state.ScreenHeightScale,
        };
        return state;
    }

    private async Task TransferPartyQueueAndHostAsync(string userId)
    {
        if (CurrentSession is not { } session || partyQueueHost is not { } host) return;
        var request = CaptureAccountRequest(session);
        var generation = partyQueueGeneration;
        var connection = stream.ConnectionGeneration;
        var snapshot = partyQueueState.Snapshot();
        var acknowledged = partyQueueAcknowledged.ToArray();
        var revision = snapshot.Revision;
        var playbackAttempt = video.PlaybackAttemptId;
        var members = stream.Roster.Select(x => x.UserId.ToLowerInvariant()).Distinct().Take(64).ToArray();
        var ticket = Guid.NewGuid();
        hostTransferPreparing = true;
        var transferAttempted = false;
        var mediaTransferStarted = false;
        bool StillCurrent() => IsCurrentAccountRequest(request) && stream.Mode == StreamMode.Hosting &&
            stream.ConnectionGeneration == connection &&
            partyQueueHost == host && partyQueueGeneration == generation &&
            partyQueueState.PartyId == snapshot.PartyId && partyQueueState.Revision == revision &&
            video.PlaybackAttemptId == playbackAttempt && !CurrentContentBlocksHostTransfer() &&
            stream.Roster.Any(x => x.UserId == userId);
        try
        {
            // No queue data uses the chat/reaction or video-update transport.
            var synced = await partyQueueClient.SyncAsync(session.Token, host, snapshot, members, acknowledged.Take(100).ToArray(), partyQueueLifetime.Token).ConfigureAwait(false);
            foreach (var batch in acknowledged.Skip(100).Chunk(100))
                synced = await partyQueueClient.SyncAsync(session.Token, host, snapshot, members, batch, partyQueueLifetime.Token).ConfigureAwait(false);
            if (synced?.Snapshot is not { } canonical || canonical.PartyId != snapshot.PartyId || canonical.Revision != revision)
                throw new PartyQueueRequestException("The queue changed during handover. Please try again.");
            var ready = await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (!StillCurrent()) return false;
                // The worker has committed these acknowledgements before it
                // seeds the new host. Newly arrived requests remain pending.
                partyQueueAcknowledged.ExceptWith(acknowledged);
                return true;
            }).ConfigureAwait(false);
            if (!ready) return;
            transferAttempted = true;
            await partyQueueClient.TransferAsync(session.Token, host, userId, ticket, partyQueueLifetime.Token).ConfigureAwait(false);
            Task<bool>? handover = null;
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (!StillCurrent()) return;
                handover = stream.TransferHostAsync(userId, CaptureHostTransferState());
            }).ConfigureAwait(false);
            if (handover is not null) mediaTransferStarted = await handover.ConfigureAwait(false);
            if (!mediaTransferStarted)
                ReportHostTransferNotice("Host transfer cancelled because the party or playback changed. Your queue is kept.");
        }
        catch (PartyQueueRequestException error)
        {
            ReportHostTransferNotice($"Couldn't transfer host: {error.Message} Your party and queue are kept.");
        }
        catch
        {
            ReportHostTransferNotice("Couldn't prepare the host transfer. Your party and queue are kept; please try again.");
        }
        finally
        {
            // The target seed is provisional until its new host publishes. A
            // cancelled handover must not leave a second active queue behind.
            if (transferAttempted && !mediaTransferStarted)
            {
                try { await partyQueueClient.CancelTransferAsync(session.Token, host, userId, ticket).ConfigureAwait(false); }
                catch { partyQueueMessages.Enqueue("Queue handover cleanup was unavailable; the unclaimed copy expires automatically."); }
            }
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                hostTransferPreparing = false;
                partyQueueNextSync = DateTime.MinValue;
            }).ConfigureAwait(false);
        }
    }

}

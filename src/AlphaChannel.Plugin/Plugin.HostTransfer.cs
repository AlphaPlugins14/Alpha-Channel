using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

public sealed partial class Plugin
{
    private StreamControl? adoptedHostPlayback;
    private DateTime adoptedHostPlaybackReceivedUtc;
    private bool adoptedHostNeedsSeek;
    private DateTime adoptedHostRetryUtc;
    private StreamControl? transferredSubtitleState;
    private DateTime transferredSubtitleRetryUtc;
    private DateTime transferredSubtitleDeadlineUtc;

    private void UpdateTransferredHostSubtitles()
    {
        if (transferredSubtitleState is not { } state) return;
        if (stream.Mode != StreamMode.Hosting || queue.Current?.Url != state.Url ||
            stream.SubtitleLanguage != state.SubtitleLanguage || stream.SubtitleName != state.SubtitleName ||
            DateTime.UtcNow >= transferredSubtitleDeadlineUtc)
        { transferredSubtitleState = null; return; }
        if (DateTime.UtcNow < transferredSubtitleRetryUtc) return;
        transferredSubtitleRetryUtc = DateTime.UtcNow.AddSeconds(1);
        if (video.SelectSubtitle(state.SubtitleLanguage, state.SubtitleName)) transferredSubtitleState = null;
    }

    private void ApplyPendingHostTransfers()
    {
        mainWindow.UpdateWatchPartyHostName();
        while (stream.IncomingHostTransfers.TryDequeue(out var transfer))
        {
            if (transfer.Generation != stream.ConnectionGeneration ||
                stream.Mode is not (StreamMode.Hosting or StreamMode.Viewing)) continue;
            mainWindow.UpdateWatchPartyHostName(forceRefresh: true);
            var becameHost = stream.Mode == StreamMode.Hosting;
            // Other viewers only change the host identity; their decoder and TV
            // continue unchanged. Ignore duplicate server acknowledgements.
            if (!becameHost && transfer.PreviousMode != StreamMode.Hosting) continue;
            var state = transfer.State;
            var pendingState = Interlocked.Exchange(ref pendingRemoteState, null);
            if (!becameHost && pendingState is not null && pendingState.HostId == stream.HostId)
                state = pendingState;
            transferredSubtitleState = null;
            screenRangeDespawned = false;
            screenRangeWarningShown = false;
            if (becameHost && state is { Url: { Length: > 0 } url })
            {
                var engine = screenController.Engine;
                var alreadyPlaying = engine.HasVideoPlaybackSession &&
                    string.Equals(video.CurrentMediaUrl, AudioVisualizerSelection.Parse(url).MediaUrl, StringComparison.Ordinal);
                if (WatchPartyTvLocation.Assess(state, 45f) == ViewerTvPlacementStatus.Near &&
                    state.ScreenX is { } x && state.ScreenY is { } y && state.ScreenZ is { } z &&
                    state.ScreenYaw is { } yaw && state.ScreenScale is { } scale)
                    engine.SetScreenTransform(new Vector3(x, y, z), yaw,
                        state.ScreenDisableFixedScaleRatio ?? false,
                        state.ScreenWidthScale ?? scale, state.ScreenHeightScale ?? scale, localPlacement: false);
                stream.SubtitleLanguage = state.SubtitleLanguage;
                stream.SubtitleName = state.SubtitleName;
                video.SetViewerClassicBarsOverride(false);
                video.Play(url); // Same-URL playback keeps the existing decoder.
                adoptedHostPlayback = state;
                adoptedHostPlaybackReceivedUtc = transfer.ReceivedUtc;
                adoptedHostNeedsSeek = !alreadyPlaying;
                adoptedHostRetryUtc = DateTime.UtcNow.AddSeconds(1);
            }
            else
            {
                adoptedHostPlayback = null;
                if (becameHost && state is not null)
                {
                    var engine = screenController.Engine;
                    stream.PreserveHostTransferScreen(state, engine.ScreenPosition, engine.ScreenYaw,
                        engine.ScreenWidthScale, engine.ScreenHeightScale, engine.DisableFixedScreenScaleRatio);
                }
            }

            mainWindow.ApplyHostTransferUi(becameHost, state);
            stream.CompleteHostPlaybackAdoption();
            // Any state received just before the role changed has now been
            // adopted. It must not subsequently stop or replace host playback.
            if (!becameHost && state is not null)
            {
                latestRemoteState = state;
                lastReceivedRemoteUrl = state.Url;
                lastAppliedRemoteUrl = state.Url;
                loggedEmptyRemoteState = string.IsNullOrWhiteSpace(state.Url);
                waitingForMedia = string.IsNullOrWhiteSpace(state.Url);
                ApplyRemoteState(state);
            }
            else
            {
                latestRemoteState = null;
                lastReceivedRemoteUrl = null;
                lastAppliedRemoteUrl = null;
                waitingForMedia = false;
            }
        }
    }

    // A viewer may have disabled their TV or still be loading. Keep the
    // inherited state authoritative until the new decoder can accept a seek.
    private bool CompleteAdoptedHostPlayback()
    {
        if (stream.Mode != StreamMode.Hosting) { adoptedHostPlayback = null; return true; }
        if (stream.HostPlaybackAdoptionPending) return false;
        if (adoptedHostPlayback is not { } state) return true;
        if (video.State == VideoPlaybackState.Failed &&
            (queue.Current is null || queue.Current.Url == state.Url))
            return false;
        if (queue.Current?.Url != state.Url)
        {
            adoptedHostPlayback = null;
            return true;
        }
        if (!screenController.Engine.HasVideoPlaybackSession ||
            video.State == VideoPlaybackState.Loading)
        {
            if (video.State == VideoPlaybackState.Idle && DateTime.UtcNow >= adoptedHostRetryUtc)
            {
                adoptedHostRetryUtc = DateTime.UtcNow.AddSeconds(1);
                video.Play(state.Url!);
            }
            return false;
        }
        var (_, duration, _) = video.GetProgress();
        if (adoptedHostNeedsSeek && !video.IsAudioOnly && !IsLiveBroadcastUrl(state.Url ?? string.Empty))
        {
            if (duration <= 0f) return false;
            var position = WatchPartyHostHandover.AtTime(state, adoptedHostPlaybackReceivedUtc, DateTime.UtcNow).PositionSeconds ?? 0d;
            var target = (float)Math.Min(position, Math.Max(0f, duration - 0.1f));
            if (MathF.Abs(video.GetProgress().Position - target) > 1.5f)
            {
                video.Seek(target);
                return false; // Do not publish zero while MPV processes the seek.
            }
        }
        if (!video.SelectSubtitle(state.SubtitleLanguage, state.SubtitleName))
        {
            transferredSubtitleState = state;
            transferredSubtitleRetryUtc = DateTime.UtcNow.AddSeconds(1);
            transferredSubtitleDeadlineUtc = DateTime.UtcNow.AddSeconds(30);
        }
        video.Pause(state.Paused ?? false);
        var engine = screenController.Engine;
        stream.PreserveHostTransferScreen(state, engine.ScreenPosition, engine.ScreenYaw,
            engine.ScreenWidthScale, engine.ScreenHeightScale, engine.DisableFixedScreenScaleRatio);
        adoptedHostPlayback = null;
        return true;
    }
}

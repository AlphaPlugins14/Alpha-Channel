namespace AlphaChannel.Plugin.Video;

internal sealed partial class VideoEngine
{
    private Task<bool> EnsureYouTubePlaybackProviderAsync() => Resources.YouTubePolicy.EnsureProviderReadyAsync(
        TimeSpan.FromSeconds(90), _lifetimeCancellation.Token);

    private async Task<bool> WaitForYouTubeProviderAsync(Func<Task<bool>> startup, int generation,
        CancellationToken cancellationToken = default)
    {
        if (_screenPainter is not { } painter) return false;
        var version = painter.LoadingConnectionVersion;
        AepLog.PlaybackAttempt(new(PlaybackAttemptStage.ProviderStarting, PlaybackAttemptRoute.PoToken,
            generation, 0, 0, false, Resources.IsYouTubeProviderRunning, false, false));
        var task = startup();
        if (!task.IsCompleted && !_disposing && generation == _playbackGeneration && !cancellationToken.IsCancellationRequested)
            painter.SetYouTubeConnectionWaiting(version, true);
        // Keep the message through extraction/buffering. Playback start or an explicit
        // stop/replacement resets it, rather than provider readiness hiding it early.
        try
        {
            var ready = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            AepLog.PlaybackAttempt(new(PlaybackAttemptStage.ProviderReady, PlaybackAttemptRoute.PoToken,
                generation, 0, 0, false, Resources.IsYouTubeProviderRunning, false, ready));
            return ready;
        }
        catch
        {
            if (generation == _playbackGeneration)
                painter.SetYouTubeConnectionWaiting(version, false);
            throw;
        }
    }
}

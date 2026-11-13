namespace AlphaChannel.Plugin.Video;

/// <summary>
/// A small audio-only libmpv session used alongside the TV renderer. Casino games keep their
/// whiteboard texture on the main VideoEngine while this independent player supplies music.
/// </summary>
internal sealed class RouletteMusicPlayer : IDisposable
{
    private MpvRenderer? renderer;
    private CancellationTokenSource? cancellation;
    private volatile bool loaded;
    private volatile bool failed;
    private int lastVolume = -1, preparationGeneration;

    internal string? CurrentUrl { get; private set; }
    internal bool Finished => renderer is not null && loaded && (failed || renderer.IsEofReached());
    internal bool Failed => failed;

    internal void Play(string url, double startSeconds, int volume)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var preparation = MpvRenderer.PrepareAudioAsync();
        if (!preparation.IsCompletedSuccessfully)
        {
            var generation = ++preparationGeneration;
            CurrentUrl = url; failed = false;
            _ = PrepareAndPlay();
            async Task PrepareAndPlay()
            {
                try
                {
                    await preparation.ConfigureAwait(false);
                    await Plugin.Framework.RunOnFrameworkThread(() =>
                    { if (generation == preparationGeneration) Play(url,startSeconds,volume); });
                }
                catch { if (generation == preparationGeneration) failed = true; }
            }
            return;
        }
        if (!EnsurePlayer(volume)) return;
        loaded = false;
        failed = false;
        CurrentUrl = url;
        SetVolume(volume);
        renderer!.Play(url, Math.Max(0d, startSeconds), true);
        renderer.Pause(false);
    }

    internal void SetVolume(int volume)
    {
        volume = Math.Clamp(volume, 0, 200);
        if (renderer is null || volume == lastVolume) return;
        renderer.SetVolume(volume);
        lastVolume = volume;
    }

    private bool EnsurePlayer(int volume)
    {
        if (renderer is not null) return true;
        cancellation = new CancellationTokenSource();
        renderer = new MpvRenderer
        {
            OnMediaLoaded = () => loaded = true,
            OnPlaybackFailed = message =>
            {
                loaded = true;
                failed = true;
                AepLog.Warning("[[redacted] Music] Playback failed.");
            },
        };
        try
        {
            renderer.Initialize(16, 16, null, cancellation.Token,
                initialVolume: Math.Clamp(volume, 0, 200), auxiliaryAudioOnly: true);
            lastVolume = Math.Clamp(volume, 0, 200);
            return true;
        }
        catch (Exception)
        {
            Stop();
            failed = true;
            AepLog.Warning("[[redacted] Music] Could not initialise playback.");
            return false;
        }
    }

    internal void Stop()
    {
        preparationGeneration++;
        CurrentUrl = null;
        loaded = false;
        failed = false;
        lastVolume = -1;
        cancellation?.Cancel();
        renderer?.Dispose();
        renderer = null;
        cancellation?.Dispose();
        cancellation = null;
    }

    public void Dispose() => Stop();
}

namespace AlphaChannel.Plugin.Video;

/// <summary>Independent audio-only player so race sounds do not replace the TV picture.</summary>
internal sealed class ChocoRaceAudioPlayer : IDisposable
{
    private MpvRenderer? renderer;
    private CancellationTokenSource? cancellation;
    private volatile bool loaded;
    private volatile bool failed;
    private int lastVolume = -1, preparationGeneration;

    internal bool Finished => renderer is not null && loaded && renderer.IsEofReached();
    internal bool Failed => failed;

    internal void Play(string path, double startSeconds, int volume, bool loop = false)
    {
        if (!File.Exists(path)) return;
        var preparation = MpvRenderer.PrepareAudioAsync();
        if (!preparation.IsCompletedSuccessfully)
        {
            var generation = ++preparationGeneration;
            _ = PrepareAndPlay();
            async Task PrepareAndPlay()
            {
                try
                {
                    await preparation.ConfigureAwait(false);
                    await Plugin.Framework.RunOnFrameworkThread(() =>
                    { if (generation == preparationGeneration) Play(path,startSeconds,volume,loop); });
                }
                catch { if (generation == preparationGeneration) failed = true; }
            }
            return;
        }
        if (!EnsurePlayer(volume)) return;
        loaded = false;
        failed = false;
        SetVolume(volume);
        renderer!.SetFileLoop(loop);
        renderer.Play(path, Math.Max(0d, startSeconds), true);
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
                AepLog.Warning("[[redacted] Audio] Playback failed.");
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
            AepLog.Warning("[[redacted] Audio] Could not initialise playback.");
            return false;
        }
    }

    internal void Stop()
    {
        preparationGeneration++;
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

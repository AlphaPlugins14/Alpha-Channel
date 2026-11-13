namespace AlphaChannel.Plugin.Video;

// Keep decoding separate from media playback and serialize it with shutdown.
internal sealed class NotificationSoundPlayer : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private MpvRenderer? renderer;
    private int disposed;

    internal void Play(string path)
    {
        if (Volatile.Read(ref disposed) != 0 || !File.Exists(path)) return;
        _ = Task.Run(async () =>
        {
            try { await MpvRenderer.PrepareAudioAsync().WaitAsync(lifetime.Token).ConfigureAwait(false); }
            catch { return; }
            lock (gate)
            {
                if (Volatile.Read(ref disposed) != 0) return;
                try
                {
                    if (renderer is null)
                    {
                        renderer = new MpvRenderer
                        {
                            OnPlaybackFailed = message =>
                                AepLog.Warning("[Notifications] Sound playback failed."),
                        };
                        renderer.Initialize(16, 16, null, lifetime.Token, initialVolume: 75, auxiliaryAudioOnly: true);
                    }
                    if (Volatile.Read(ref disposed) == 0)
                    {
                        renderer.Play(path, 0d, true);
                        // MPV's keep-open mode pauses at EOF; loading another sound retains that pause.
                        renderer.Pause(false);
                    }
                }
                catch (Exception)
                {
                    if (Volatile.Read(ref disposed) == 0)
                        AepLog.Warning("[Notifications] Could not play sound.");
                    ReleaseRenderer();
                }
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        lock (gate)
        {
            ReleaseRenderer();
            lifetime.Dispose();
        }
    }

    private void ReleaseRenderer()
    {
        try { renderer?.Dispose(); }
        catch (Exception) { AepLog.Warning("[Notifications] Audio cleanup failed."); }
        finally { renderer = null; }
    }
}

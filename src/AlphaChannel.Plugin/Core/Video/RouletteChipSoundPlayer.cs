using SharpDX;
using SharpDX.Multimedia;
using SharpDX.XAudio2;

namespace AlphaChannel.Plugin.Video;

/// <summary>Reusable, low-latency one-shot player for the packaged roulette chip effect.</summary>
internal sealed class RouletteChipSoundPlayer(string fileName = "roulette-chips.wav") : IDisposable
{
    private XAudio2? audio;
    private MasteringVoice? master;
    private SourceVoice? source;
    private SoundStream? sound;
    private DataStream? data;
    private AudioBuffer? buffer;

    internal void Prepare()
    {
        try { EnsureLoaded(); }
        catch (Exception)
        {
            AepLog.Warning("[[redacted]] Could not prepare the sound effect.");
            Dispose();
        }
    }

    internal void SetVolume(int volume) => source?.SetVolume(Math.Clamp(volume / 100f, 0f, 2f));

    internal void Play(int volume)
    {
        try
        {
            EnsureLoaded();
            if (source is null || buffer is null || sound is null) return;
            source.Stop();
            source.FlushSourceBuffers();
            source.SetVolume(Math.Clamp(volume / 100f, 0f, 2f));
            source.SubmitSourceBuffer(buffer, sound.DecodedPacketsInfo);
            source.Start();
        }
        catch (Exception)
        {
            AepLog.Warning("[[redacted]] Could not play the sound effect.");
            Dispose();
        }
    }

    private void EnsureLoaded()
    {
        if (source is not null) return;
        var pluginDirectory = Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty;
        var path = Path.Combine(pluginDirectory, "Assets", fileName);
        if (!File.Exists(path)) return;

        sound = new SoundStream(File.OpenRead(path));
        data = sound.ToDataStream();
        buffer = new AudioBuffer
        {
            Stream = data,
            AudioBytes = (int)sound.Length,
            Flags = BufferFlags.EndOfStream,
        };
        audio = new XAudio2();
        master = new MasteringVoice(audio);
        source = new SourceVoice(audio, sound.Format, true);
    }

    public void Dispose()
    {
        try { source?.Stop(); source?.FlushSourceBuffers(); } catch { }
        try { source?.DestroyVoice(); } catch { }
        source?.Dispose();
        master?.Dispose();
        audio?.Dispose();
        data?.Dispose();
        sound?.Dispose();
        source = null;
        master = null;
        audio = null;
        data = null;
        sound = null;
        buffer = null;
    }
}

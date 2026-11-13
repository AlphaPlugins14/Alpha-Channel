using SharpDX;
using SharpDX.Multimedia;
using SharpDX.XAudio2;

namespace AlphaChannel.Plugin.Video;

/// <summary>Looping wheel-and-ball sound used for the duration of a roulette spin.</summary>
internal sealed class RouletteRollSoundPlayer(string fileName = "roulette-roll.wav") : IDisposable
{
    private XAudio2? audio;
    private MasteringVoice? master;
    private SourceVoice? source;
    private SoundStream? sound;
    private DataStream? data;
    private AudioBuffer? buffer;
    private bool playing;

    internal void SetPlaying(bool shouldPlay, int volume)
    {
        try
        {
            if (!shouldPlay)
            {
                Stop();
                return;
            }

            EnsureLoaded();
            if (source is null || buffer is null || sound is null) return;
            source.SetVolume(Math.Clamp(volume / 100f, 0f, 2f));
            if (playing) return;

            source.Stop();
            source.FlushSourceBuffers();
            source.SubmitSourceBuffer(buffer, sound.DecodedPacketsInfo);
            source.Start();
            playing = true;
        }
        catch (Exception)
        {
            AepLog.Warning("[[redacted]] Could not play the rolling sound.");
            Dispose();
        }
    }

    internal void Stop()
    {
        if (!playing) return;
        try
        {
            source?.Stop();
            source?.FlushSourceBuffers();
        }
        catch { }
        playing = false;
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
            LoopBegin = 0,
            LoopLength = (int)(sound.Length / sound.Format.BlockAlign),
            LoopCount = XAudio2.MaximumLoopCount,
        };
        audio = new XAudio2();
        master = new MasteringVoice(audio);
        source = new SourceVoice(audio, sound.Format, true);
    }

    public void Dispose()
    {
        Stop();
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

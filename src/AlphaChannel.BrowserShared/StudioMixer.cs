using System.Buffers.Binary;

namespace AlphaChannel.BroadcastStudio;

// Pure, fixed-size PCM processing shared with regression checks. Local monitoring
// and the broadcast use separate gains; microphone monitoring is explicitly opt-in.
internal sealed class StudioMixer
{
    private float duckGain = 1;
    private int speechHold;
    private float previousCrossfader;
    private bool crossfaderInitialized;
    internal (float Music, float Mic, float Left, float Right, bool Clipping) Mix(
        ReadOnlySpan<byte> music, ReadOnlySpan<byte> mic, Span<byte> output, Span<byte> monitor, StudioSettings settings)
        => MixSources(music, [], mic, output, monitor, settings, false);
    internal (float Music, float Mic, float Left, float Right, bool Clipping) MixDecks(
        ReadOnlySpan<byte> deckA, ReadOnlySpan<byte> deckB, ReadOnlySpan<byte> mic,
        Span<byte> output, Span<byte> monitor, StudioSettings settings)
        => MixSources(deckA, deckB, mic, output, monitor, settings, true);
    private (float Music, float Mic, float Left, float Right, bool Clipping) MixSources(
        ReadOnlySpan<byte> music, ReadOnlySpan<byte> deckB, ReadOnlySpan<byte> mic,
        Span<byte> output, Span<byte> monitor, StudioSettings settings, bool dual)
    {
        var cross = Math.Clamp(settings.Crossfader, 0, 1);
        if (!crossfaderInitialized) { previousCrossfader = cross; crossfaderInitialized = true; }
        var gainAStart = MathF.Cos(previousCrossfader * MathF.PI / 2) * Math.Clamp(settings.DeckAVolume, 0, 1);
        var gainBStart = MathF.Sin(previousCrossfader * MathF.PI / 2) * Math.Clamp(settings.DeckBVolume, 0, 1);
        var gainAEnd = MathF.Cos(cross * MathF.PI / 2) * Math.Clamp(settings.DeckAVolume, 0, 1);
        var gainBEnd = MathF.Sin(cross * MathF.PI / 2) * Math.Clamp(settings.DeckBVolume, 0, 1);
        float musicPeak = 0, micPeak = 0, left = 0, right = 0;
        var micOpen = settings.MicEnabled && (!settings.PushToTalk || settings.TalkHeld);
        for (var i = 0; i < mic.Length; i += 2)
            micPeak = Math.Max(micPeak, Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(mic[i..]) / 32768f * settings.MicVolume));
        if (micOpen && micPeak > .025f) speechHold = 18; // 360 ms release between words.
        else speechHold = Math.Max(0, speechHold - 1);
        var target = settings.DuckMusic && micOpen && speechHold > 0 ? 1 - Math.Clamp(settings.DuckAmount, 0, 1) : 1;
        var nextDuckGain = duckGain + (target - duckGain) * (target < duckGain ? .4f : .08f);
        var clipped = false;
        for (var i = 0; i < output.Length; i += 2)
        {
            var gain = duckGain + (nextDuckGain - duckGain) * i / output.Length;
            var song = i < music.Length ? BinaryPrimitives.ReadInt16LittleEndian(music[i..]) / 32768f : 0;
            if (dual)
            {
                // Equal-power gains and a per-frame ramp avoid a dip in the middle
                // of a transition or clicks when the user moves the fader.
                var progress = (i / 4 + 1f) / Math.Max(1, output.Length / 4);
                var other = i < deckB.Length ? BinaryPrimitives.ReadInt16LittleEndian(deckB[i..]) / 32768f : 0;
                song = song * (gainAStart + (gainAEnd - gainAStart) * progress) +
                    other * (gainBStart + (gainBEnd - gainBStart) * progress);
            }
            else song *= settings.MusicVolume;
            var voice = micOpen && i < mic.Length ? BinaryPrimitives.ReadInt16LittleEndian(mic[i..]) / 32768f * settings.MicVolume : 0;
            musicPeak = Math.Max(musicPeak, Math.Abs(song));
            var mix = (song * gain + voice) * settings.MasterVolume;
            clipped |= Math.Abs(mix) > 1;
            if (i % 4 == 0) left = Math.Max(left, Math.Abs(mix));
            else right = Math.Max(right, Math.Abs(mix));
            Write(output[i..], mix);
            // The source application is already audible locally. Replaying its capture
            // by default would produce an echo; microphone monitoring remains independent.
            var monitorSong = settings.MusicSource != StudioMusicSource.Application || settings.MonitorApplication ? song * gain : 0;
            Write(monitor[i..], (monitorSong + (settings.MonitorMic ? voice : 0)) * settings.MasterVolume * settings.MonitorVolume);
        }
        duckGain = nextDuckGain;
        previousCrossfader = cross;
        return (musicPeak, micPeak, Math.Min(1, left), Math.Min(1, right), clipped);
    }
    private static void Write(Span<byte> destination, float value) =>
        BinaryPrimitives.WriteInt16LittleEndian(destination, (short)Math.Clamp(value * 32767, -32768, 32767));
}

namespace AlphaChannel.Plugin.Video;

// Callers serialize capture and rendering. Positions use the same monotonic
// 100-nanosecond clock as WASAPI, rather than the time a pipe delivers a packet.
internal sealed class BrowserAudioTimeline(long startTime)
{
    private readonly long origin = startTime;
    internal static BrowserAudioTimeline? ForEncoderStart(BrowserAudioTimeline? current, long startTime)
    {
        if (startTime <= 0) return null;
        return current is not null && current.origin == startTime ? current : new BrowserAudioTimeline(startTime);
    }
    internal const int SampleRate = 48000;
    internal const int HeadroomFrames = SampleRate / 4;
    internal const int BatchFrames = SampleRate / 100;
    private sealed class Packet(long start, byte[] data)
    {
        internal readonly long Start = start;
        internal readonly byte[] Data = data;
        internal int Offset;
    }
    private readonly Queue<Packet> packets = new();
    private long position, queuedFrames;
    private long? previousCaptureEnd;
    internal long CapturedFrames { get; private set; }
    internal long LateFrames { get; private set; }
    internal long MissingFrames { get; private set; }
    internal long OverflowFrames { get; private set; }
    internal long Position => position;
    internal long QueuedFrames => queuedFrames;

    private long FrameAt(long time) => (long)Math.Floor((time - origin) * (double)SampleRate / 10000000);

    internal void Capture(byte[] pcm, long captureTime)
    {
        var frames = pcm.Length / 4;
        if (frames == 0 || pcm.Length % 4 != 0) return;
        CapturedFrames += frames;
        var start = FrameAt(captureTime);
        // CEF PTS is rounded to milliseconds and WASAPI timestamps have small
        // capture-clock variations. Keep contiguous PCM contiguous within 2 ms;
        // otherwise every rounding boundary would create clicks or trim samples.
        if (previousCaptureEnd is { } previous && Math.Abs(start - previous) <= SampleRate / 500)
            start = previous;
        previousCaptureEnd = start + frames;
        if (start + frames <= position) { LateFrames += frames; return; }
        packets.Enqueue(new Packet(start, pcm));
        queuedFrames += frames;
        // A stalled encoder must not cause unlimited retained capture memory.
        while (queuedFrames > SampleRate && packets.Count > 0)
        {
            var packet = packets.Dequeue();
            var lost = packet.Data.Length / 4 - packet.Offset;
            queuedFrames -= lost;
            OverflowFrames += lost;
        }
    }

    internal void ClearCapturedAudio()
    {
        packets.Clear();
        queuedFrames = 0;
    }

    internal byte[]? Render(long now)
    {
        var target = Math.Max(0, FrameAt(now) - HeadroomFrames);
        // The pump polls rapidly near a video deadline. Sending every newly
        // elapsed sample creates thousands of tiny packets and overflows the
        // encoder's packet-count bound despite containing very little audio.
        var available = target - position;
        if (available < BatchFrames) return null;
        var frames = (int)Math.Min(SampleRate / 20, available / BatchFrames * BatchFrames);
        var output = new byte[frames * 4];
        var end = position + frames;
        var written = 0;
        while (position < end)
        {
            if (!packets.TryPeek(out var packet))
            {
                MissingFrames += end - position;
                position = end;
                break;
            }
            var packetFrames = packet.Data.Length / 4;
            var packetEnd = packet.Start + packetFrames;
            if (packetEnd <= position)
            {
                packets.Dequeue(); queuedFrames -= packetFrames - packet.Offset;
                LateFrames += packetFrames - packet.Offset;
                continue;
            }
            if (packet.Start > position)
            {
                var gap = (int)Math.Min(end - position, packet.Start - position);
                MissingFrames += gap; position += gap; written += gap;
                continue;
            }
            var offset = (int)(position - packet.Start);
            var count = (int)Math.Min(end - position, packetEnd - position);
            LateFrames += offset - packet.Offset;
            queuedFrames -= offset - packet.Offset + count;
            Buffer.BlockCopy(packet.Data, offset * 4, output, written * 4, count * 4);
            position += count; written += count;
            packet.Offset = offset + count;
            if (position == packetEnd) packets.Dequeue();
        }
        return output;
    }

}

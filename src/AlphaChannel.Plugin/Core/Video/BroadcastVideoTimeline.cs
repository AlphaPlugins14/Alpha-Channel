using System.Buffers;

namespace AlphaChannel.Plugin.Video;

// Rawvideo has no per-frame timestamps: FFmpeg assigns a time slot to each
// frame written. Replacing pending captures must not delete those slots while
// the independently queued PCM audio retains its original duration.
internal sealed class BroadcastVideoTimeline : IDisposable
{
    private byte[]? previousFrame;
    private long writtenSequence;

    // Takes ownership of the pooled frame only after a successful write.
    internal long Write(Stream input, byte[] frame, int bytes, long sequence, CancellationToken cancellation)
    {
        if (sequence <= writtenSequence) throw new InvalidOperationException("Video sequence must advance.");
        var repeated = sequence - writtenSequence - 1;
        while (writtenSequence + 1 < sequence)
        {
            cancellation.ThrowIfCancellationRequested();
            input.Write(previousFrame ?? frame, 0, bytes);
            writtenSequence++;
        }
        cancellation.ThrowIfCancellationRequested();
        input.Write(frame, 0, bytes);
        writtenSequence = sequence;
        if (previousFrame is not null) ArrayPool<byte>.Shared.Return(previousFrame);
        previousFrame = frame;
        return repeated;
    }

    public void Dispose()
    {
        if (previousFrame is not null) ArrayPool<byte>.Shared.Return(previousFrame);
        previousFrame = null;
    }
}

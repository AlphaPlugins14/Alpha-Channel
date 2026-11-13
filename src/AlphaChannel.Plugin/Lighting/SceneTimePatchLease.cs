namespace AlphaChannel.Plugin.Lighting;

// Scoped render-time instruction replacement. Refuse unknown instructions and
// never restore over another plugin's subsequent changes.
internal sealed class SceneTimePatchLease(Func<byte[]> read, Action<byte[]> write) : IDisposable
{
    internal static byte[] OriginalInstruction => [0x4D, 0x8B, 0x8A, 0x78, 0x17, 0, 0];
    internal static byte[] MidnightInstruction => [0x49, 0xC7, 0xC1, 0, 0, 0, 0];
    private bool owned;
    private byte[] applied = MidnightInstruction;

    internal bool Active => owned && read().SequenceEqual(applied);
    internal bool Available => owned ? Active : read().SequenceEqual(OriginalInstruction);

    internal bool Enable(uint seconds = 0)
    {
        if (seconds >= 86400) return false;
        if (!Available) return false;
        var next = MidnightInstruction;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(next.AsSpan(3), seconds);
        if (owned && applied.SequenceEqual(next)) return true;
        var previous = applied;
        var previouslyOwned = owned;
        applied = next;
        owned = true;
        try { write(applied); return true; }
        catch
        {
            // Recover a completed write, but never overwrite an unknown partial/foreign write.
            if (read().SequenceEqual(applied)) write(previouslyOwned ? previous : OriginalInstruction);
            applied = previous; owned = previouslyOwned;
            throw;
        }
    }

    public void Dispose()
    {
        if (!owned) return;
        if (read().SequenceEqual(applied)) write(OriginalInstruction);
        owned = false;
    }
}

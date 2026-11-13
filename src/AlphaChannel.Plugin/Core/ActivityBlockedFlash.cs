namespace AlphaChannel.Plugin;

internal sealed class ActivityBlockedFlash
{
    private long startedAt = -1;
    internal void Trigger(long now) => Interlocked.Exchange(ref startedAt, now);
    internal void Clear() => Interlocked.Exchange(ref startedAt, -1);
    internal bool IsActive(long now)
    {
        var start = Volatile.Read(ref startedAt);
        return start >= 0 && now >= start && now - start < 1500;
    }
    internal bool IsOn(long now)
    {
        var start = Volatile.Read(ref startedAt);
        return start >= 0 && now >= start && now - start < 1500 && (now - start) % 500 < 250;
    }
}

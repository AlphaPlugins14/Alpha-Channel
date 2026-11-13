namespace AlphaChannel.Plugin.Video;

// Called under the owning cache's publication lock. Never retire a texture
// requested in the last few seconds: ImGui may still be using its draw command.
// If every slot is in use, defer new images instead of exceeding the budget.
internal sealed class TextureCacheBudget<TKey>(int capacity, Func<long>? clock = null, Func<int>? frameClock = null) where TKey : notnull
{
    private readonly Dictionary<TKey, (long Time, int Frame)> accessed = new();
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    internal bool Admit(TKey key, Action<TKey> evict)
    {
        var time = now();
        var frame = frameClock?.Invoke() ?? 0;
        if (accessed.ContainsKey(key)) { accessed[key] = (time, frame); return true; }
        if (accessed.Count >= capacity)
        {
            var candidates = accessed.Where(pair => frameClock is null || pair.Value.Frame != frame).ToArray();
            if (candidates.Length == 0) return false;
            var oldest = candidates.MinBy(pair => pair.Value.Time);
            if (time - oldest.Value.Time < 5_000) return false;
            accessed.Remove(oldest.Key);
            evict(oldest.Key);
        }
        accessed[key] = (time, frame);
        return true;
    }
    internal void Remove(TKey key) => accessed.Remove(key);
    internal void Clear() => accessed.Clear();
}

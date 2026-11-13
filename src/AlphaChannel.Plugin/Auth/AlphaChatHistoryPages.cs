namespace AlphaChannel.Plugin.Auth;

// Keep a sequence boundary so live arrivals/deletions do not shift older visible posts.
internal sealed class AlphaChatHistoryPages
{
    internal const int BatchSize = 60;
    private long? firstSequence;
    private bool expanded;
    internal void Reset() { firstSequence = null; expanded = false; }
    internal void HistoryArrived() { if (!expanded) firstSequence = null; }

    internal int Start(IReadOnlyList<long> sequences)
    {
        if (sequences.Count == 0) return 0;
        firstSequence ??= sequences[Math.Max(0, sequences.Count - BatchSize)];
        var start = 0;
        while (start < sequences.Count && sequences[start] < firstSequence.Value) start++;
        return start;
    }

    internal void LoadMore(IReadOnlyList<long> sequences)
    {
        if (sequences.Count == 0) return;
        firstSequence = sequences[Math.Max(0, Start(sequences) - BatchSize)];
        expanded = true;
    }

    internal void Reveal(IReadOnlyList<long> sequences, long target)
    {
        if (!sequences.Contains(target)) return;
        while (Start(sequences) > 0 && sequences[Start(sequences)] > target) LoadMore(sequences);
    }
}

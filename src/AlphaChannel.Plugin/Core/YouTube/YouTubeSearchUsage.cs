namespace AlphaChannel.Plugin.YouTube;

internal sealed class YouTubeSearchUsage(Func<DateTime>? clock = null)
{
    private readonly Queue<DateTime> searches = new();
    private readonly Func<DateTime> now = clock ?? (() => DateTime.UtcNow);
    private DateTime blockedUntil;
    internal bool ShowWarning { get { Prune(now()); return searches.Count > 5 || BlockedSeconds > 0; } }
    internal int BlockedSeconds => Math.Max(0, (int)Math.Ceiling((blockedUntil - now()).TotalSeconds));
    internal bool TrySearch()
    {
        var time = now();
        Prune(time);
        if (time < blockedUntil) return false;
        searches.Enqueue(time);
        if (searches.Count <= 20) return true;
        blockedUntil = time.AddMinutes(1);
        return false;
    }
    private void Prune(DateTime time)
    {
        while (searches.TryPeek(out var oldest) && time - oldest >= TimeSpan.FromMinutes(1)) searches.Dequeue();
    }
}

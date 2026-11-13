namespace AlphaChannel.Plugin;

// Pure local playback accounting. No URLs or timing telemetry leave the plugin.
internal sealed class MovieBuffTracker
{
    internal const double RequiredSeconds = 180 * 60;
    private static readonly string TotalKey = new('0', 64);
    internal Dictionary<string, double> Progress { get; set; } = new();
    private string? previousIdentity;
    private double previousTime, previousPosition, pendingElapsed;
    private int previousSeek;
    private bool previousPlaying;

    internal double WatchedSeconds => Math.Min(RequiredSeconds, Progress.Values.Sum());
    internal bool Eligible => WatchedSeconds >= RequiredSeconds;

    internal void Restore(Dictionary<string, double> progress)
    {
        var total = progress.Where(p => p.Key.Length == 64 && p.Key.All(Uri.IsHexDigit) && double.IsFinite(p.Value) && p.Value >= 0)
            .Sum(p => Math.Min(RequiredSeconds, p.Value));
        Progress = total > 0 ? new() { [TotalKey] = Math.Min(RequiredSeconds, total) } : new();
    }

    internal void Sample(string? identity, bool playing, double position, int seekVersion, double now)
    {
        var elapsed = now - previousTime;
        var advance = position - previousPosition;
        if (identity is not null && identity == previousIdentity && playing && previousPlaying &&
            seekVersion == previousSeek && elapsed is > 0 and <= 3)
        {
            pendingElapsed = Math.Min(3, pendingElapsed + elapsed);
            if (double.IsFinite(advance) && advance > 0 && advance <= pendingElapsed * 2 + .5)
                Progress = new() { [TotalKey] = Math.Min(RequiredSeconds, WatchedSeconds + Math.Min(pendingElapsed, advance)) };
            if (!double.IsFinite(advance) || advance != 0) pendingElapsed = 0;
        }
        else pendingElapsed = 0;
        previousIdentity = identity;
        previousTime = now;
        previousPosition = position;
        previousSeek = seekVersion;
        previousPlaying = playing;
    }
}

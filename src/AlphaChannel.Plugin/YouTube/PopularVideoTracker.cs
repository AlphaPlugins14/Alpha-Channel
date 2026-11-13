namespace AlphaChannel.Plugin.YouTube;

[Serializable]
internal sealed class PopularVideoRecord
{
    public string VideoId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ChannelName { get; set; } = string.Empty;
    public string? ChannelId { get; set; }
    public string? ThumbnailUrl { get; set; }
    public double? DurationSeconds { get; set; }
    public long? ViewCount { get; set; }
    public long? ViewCountObservedAtUnix { get; set; }
    public bool SharedViewCountKnown { get; set; }
    public long? MetadataObservedAtUnix { get; set; }
    public bool SharedMetadataKnown { get; set; }
    public long SharedMetadataRefreshAfterUnix { get; set; }
    public DateTimeOffset? UploadDate { get; set; }
    public DateTime MetadataAttemptUtc { get; set; }
}

// Samples decoded playback progress, rather than the selected URL or wall time
// alone. Discontinuities never count, and a stalled decoder contributes nothing.
internal sealed class PopularVideoTracker
{
    private string? identity;
    private double previousTime, previousPosition, watched;
    private int previousSeek;
    private bool previousPlaying, submitted;

    internal static double Threshold(double duration) =>
        double.IsFinite(duration) && duration > 0 ? Math.Min(30, duration / 2) : 30;

    internal double WatchedSeconds => watched;

    internal bool Sample(string? playbackIdentity, bool playing, double position,
        double duration, int seekVersion, double monotonicSeconds)
    {
        if (identity != playbackIdentity)
        {
            identity = playbackIdentity;
            watched = 0;
            submitted = false;
            previousPlaying = false;
        }
        var elapsed = monotonicSeconds - previousTime;
        var advance = position - previousPosition;
        if (identity is not null && playing && previousPlaying &&
            seekVersion == previousSeek && elapsed is > 0 and <= 2 &&
            double.IsFinite(advance) && advance > 0 && advance <= elapsed * 2 + .25)
            watched += Math.Min(elapsed, advance);
        previousTime = monotonicSeconds;
        previousPosition = position;
        previousSeek = seekVersion;
        previousPlaying = playing;
        if (identity is null || submitted || watched + .001 < Threshold(duration)) return false;
        submitted = true;
        return true;
    }
}

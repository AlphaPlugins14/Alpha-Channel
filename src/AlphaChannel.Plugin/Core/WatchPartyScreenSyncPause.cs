namespace AlphaChannel.Plugin;

// Carried in the existing stream.state Reason field: no relay changes or
// persisted viewer preference changes are needed. The host retains this tag
// until explicitly placing its TV after duty.
internal sealed class WatchPartyScreenSyncPause
{
    private const string HeldSuffix = ";TV-HELD";
    internal bool IsPaused { get; private set; }
    internal bool ShouldFollowHost(bool viewerEnabledSync) => viewerEnabledSync && !IsPaused;

    internal static string? LocationReason(string? reason) =>
        reason is null ? null : reason.Split(';')[0];

    internal static string MarkHeld(string? reason) =>
        (IsHeld(reason) ? reason![..^HeldSuffix.Length] : reason ?? "TV1:?") + HeldSuffix;

    internal static bool IsHeld(string? reason) =>
        reason?.EndsWith(HeldSuffix, StringComparison.Ordinal) == true;

    // Return true only when the host has explicitly released the hold and its
    // newly published TV is in this viewer's area. Unknown locations stay paused.
    internal bool Observe(string? reason, bool sameArea)
    {
        if (IsHeld(reason)) IsPaused = true;
        else if (IsPaused && sameArea)
        {
            IsPaused = false;
            return true;
        }
        return false;
    }

    internal void Reset() => IsPaused = false;

    // Local placement can temporarily disable sync even without a host hold.
    // Restore that saved preference on return; deliberate toggles clear it.
    internal bool ObserveViewer(string? reason, bool sameArea, bool nearHost,
        ref bool enabled, ref bool? beforeLocalPlacement)
    {
        var releasedHold = Observe(reason, sameArea);
        if (beforeLocalPlacement is not { } previous ||
            !(releasedHold || nearHost && !IsPaused)) return false;
        enabled = previous;
        beforeLocalPlacement = null;
        return true;
    }
}

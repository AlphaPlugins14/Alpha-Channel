namespace AlphaChannel.Plugin;

// Framework-owned. Visible/disabled notifications never consume the cooldown,
// and suppressed notifications are discarded rather than played later.
internal sealed class NotificationSoundCooldown
{
    private TimeSpan? lastPlayed;

    internal bool TryAcquire(TimeSpan now, bool enabled, bool chatVisible, bool limitToTwoMinutes = true)
    {
        var interval = limitToTwoMinutes ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(3);
        if (!enabled || chatVisible ||
            lastPlayed is { } last && now - last < interval)
            return false;
        lastPlayed = now;
        return true;
    }
}

namespace AlphaChannel.Plugin;

// Track observed titles, not desired activity status: changing media during the
// grace period must not interrupt another plugin's temporary title.
internal sealed class HonorificStatusGracePeriod(Func<string, string, bool> equivalent)
{
    internal static readonly TimeSpan Duration = TimeSpan.FromSeconds(30);
    private string? observed;
    private string? notified;
    private bool notificationPending;
    private bool applied;
    internal DateTime? RestoreAfterUtc { get; private set; }
    internal bool Waiting(DateTime now) => RestoreAfterUtc is { } deadline && now < deadline;
    internal bool RestorationDue(DateTime now) => RestoreAfterUtc is { } deadline && now >= deadline;

    internal void Observe(string title, DateTime now)
    {
        if (observed is null || !equivalent(observed, title))
        {
            observed = title;
            // Honorific reports original titles as empty in notifications, but
            // returns their title data when queried. Reconcile that notification
            // without counting it as a second change and extending the grace.
            if (applied && (!notificationPending || RestoreAfterUtc is null))
                RestoreAfterUtc = now + Duration;
        }
        notificationPending = false;
    }

    internal void Notify(string title, DateTime now)
    {
        if (notified is not null && equivalent(notified, title)) return;
        notified = title;
        notificationPending = true;
        if (applied) RestoreAfterUtc = now + Duration;
    }

    internal void Applied(string title)
    {
        applied = true;
        observed = title;
        notified = title;
        notificationPending = false;
        RestoreAfterUtc = null;
    }

    internal void Reset()
    {
        applied = false;
        observed = null;
        notified = null;
        notificationPending = false;
        RestoreAfterUtc = null;
    }
}

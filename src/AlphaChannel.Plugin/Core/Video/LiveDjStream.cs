namespace AlphaChannel.Plugin.Video;

internal static class LiveDjStream
{
    internal static bool AppliesTo(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return false;
        var path = uri.AbsolutePath;
        const string proxyPrefix = "/radio-stream/radio/";
        const string directPrefix = "/radio/";
        var mount = path.StartsWith(proxyPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[proxyPrefix.Length..]
            : uri.Port == 8000 && path.StartsWith(directPrefix, StringComparison.OrdinalIgnoreCase)
                ? path[directPrefix.Length..] : null;
        return Guid.TryParse(mount, out _);
    }
}

// All state is owned by the framework thread. A successful connection starts
// a fresh recovery budget for a later outage; repeated failures cannot extend it.
internal sealed class LiveDjRetry
{
    private string? url;
    private DateTime? deadlineUtc;
    private DateTime nextAttemptUtc;
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    internal void SetUrl(string? value)
    {
        if (string.Equals(url, value, StringComparison.Ordinal)) return;
        Reset();
        url = value;
    }

    internal bool Expired(DateTime now) => deadlineUtc is { } deadline && now >= deadline;
    internal bool CanAttempt(DateTime now) => !Expired(now) && now >= nextAttemptUtc;
    internal void Attempted(DateTime now)
    {
        deadlineUtc ??= now + Limit;
        nextAttemptUtc = now + Interval;
    }
    internal void Connected()
    {
        deadlineUtc = null;
        nextAttemptUtc = DateTime.MinValue;
    }
    internal void Reset()
    {
        url = null;
        Connected();
    }
}

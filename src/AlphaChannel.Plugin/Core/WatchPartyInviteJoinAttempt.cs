namespace AlphaChannel.Plugin;

// Prefer the stable account ID, but support relays that only resolve names.
internal sealed class WatchPartyInviteJoinAttempt(string hostAccountId, string? hostDisplayName)
{
    private bool triedDisplayName;

    internal string? TakeDisplayNameFallback(string? reason)
    {
        if (triedDisplayName || reason is not ("Host not found." or "Host is not streaming.") ||
            string.IsNullOrWhiteSpace(hostDisplayName) ||
            string.Equals(hostAccountId, hostDisplayName.Trim(), StringComparison.OrdinalIgnoreCase))
            return null;
        triedDisplayName = true;
        return hostDisplayName.Trim();
    }
}

namespace AlphaChannel.Plugin;

internal static class FriendWatchPartyStatus
{
    internal static string DisplayLabel(string label)
    {
        const string legacyLabel = "Hosting a watch-along";
        return string.Equals(label, legacyLabel, StringComparison.OrdinalIgnoreCase) ||
               label.StartsWith(legacyLabel + " (", StringComparison.OrdinalIgnoreCase)
            ? "Hosting a Watch Party" + label[legacyLabel.Length..]
            : label;
    }

    internal static bool CanJoin(bool online, bool hostingJoinableWatchParty, string? watchingLabel)
    {
        if (!online) return false;
        // Older relays send the public hosting label without the newer flag.
        // Private rooms use "Watching privately" and must not match this fallback.
        var displayLabel = watchingLabel is null ? null : DisplayLabel(watchingLabel);
        const string hostingLabel = "Hosting a Watch Party";
        return hostingJoinableWatchParty ||
               string.Equals(displayLabel, hostingLabel, StringComparison.OrdinalIgnoreCase) ||
               displayLabel?.StartsWith(hostingLabel + " (", StringComparison.OrdinalIgnoreCase) == true;
    }
}

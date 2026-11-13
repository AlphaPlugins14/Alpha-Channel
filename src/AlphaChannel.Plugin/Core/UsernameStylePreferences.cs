namespace AlphaChannel.Plugin;

internal static class UsernameStylePreferences
{
    // Renew before the verified grant ends, even when the selection is unchanged.
    // A failed/temporarily unapproved grant retries without erasing the selection.
    internal static DateTime PublishRefreshAt(DateTime now, string selectedEffect, string approvedEffect, long validUntil)
    {
        if (selectedEffect == "solid") return now.AddMinutes(30);
        var nowMilliseconds = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        if (approvedEffect != selectedEffect || validUntil <= nowMilliseconds)
            return now.AddMinutes(2);
        var remaining = validUntil - nowMilliseconds;
        return now.AddMilliseconds(Math.Min(remaining / 2d, 30 * 60_000d));
    }

    // Server styles seed a new installation. They must not overwrite a local
    // preference, including an explicit choice to use the default appearance.
    internal static bool RestoreIfMissing(string accountId, string? colour, string? effect,
        IDictionary<string, string> colours, IDictionary<string, string> effects, ISet<string> savedAccounts)
    {
        if (savedAccounts.Contains(accountId) || colours.ContainsKey(accountId) || effects.ContainsKey(accountId))
            return false;
        if (colour is not null) colours[accountId] = colour;
        if (effect is not null && effect != "solid") effects[accountId] = effect;
        savedAccounts.Add(accountId);
        return true;
    }
}

namespace AlphaChannel.Plugin;

public sealed partial class Plugin
{
    private readonly DiscordActivity discordActivity = new();
    private DateTime nextDiscordActivityUpdate;
    private int lastDiscordPrivacy = -1;

    private void TickDiscordActivity()
    {
        if (!Cfg.DiscordActivityEnabled || !ClientState.IsLoggedIn)
        { discordActivity.Update(null); return; }
        var privacy = (Cfg.DiscordActivityShowTitles ? 1 : 0) | (stream.IsPrivate ? 2 : 0);
        var privacyChanged = privacy != lastDiscordPrivacy;
        if (!privacyChanged && DateTime.UtcNow < nextDiscordActivityUpdate) return;
        lastDiscordPrivacy = privacy;
        nextDiscordActivityUpdate = DateTime.UtcNow.AddSeconds(1);
        discordActivity.Update(mainWindow.GetDiscordActivity(), urgent: privacyChanged);
    }
}

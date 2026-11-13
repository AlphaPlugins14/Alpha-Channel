using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    internal DiscordActivityState? GetDiscordActivity()
    {
        var engine = screenController.Engine;
        var inParty = stream.Mode != StreamMode.None;
        if (inParty && stream.IsPrivate)
            return new(0, "In a private Watch Party", "Alpha Channel");
        var status = inParty ? (stream.Mode == StreamMode.Hosting ? "Hosting a Watch Party" : "In a Watch Party") : "In Eorzea";
        var mediaUrl = stream.Mode == StreamMode.Viewing ? stream.CurrentRoomState?.Url : video.CurrentMediaUrl;
        var browserStream = DiscordActivityPrivacy.IsContent(mediaUrl, "web-browsing");
        var localVideoStream = DiscordActivityPrivacy.IsContent(mediaUrl, "local-video");
        var retroStream = DiscordActivityPrivacy.IsContent(mediaUrl, "retro-games");
        if (HalloweenActivityActive) return new(0, "Halloween lightshow", status);
        if (IsArcadeGameActive) return new(0, ActiveActivity switch
        {
            ActiveActivityKind.SkyRail => "Air Force EX", ActiveActivityKind.ChocoRaceBets => "Choco RaceBets",
            ActiveActivityKind.SlotMachine => "Slot Machine", ActiveActivityKind.LuckyWheel => "Lucky Wheel",
            ActiveActivityKind.PictionaryTeams => "Pictionary Teams", ActiveActivityKind.BlastParty => "Blast Party",
            _ => ActiveActivity.ToString()
        }, status);
        if (engine.IsPlayingGame || retroStream) return new(0, "Playing retro games", status);
        // Never send browser page titles/addresses, filenames, stream URLs or account IDs.
        if (engine.IsPlayingBrowser || browserStream) return new(0, "Web browsing", status);
        if (StudioOwnsPlaybackControls) return new(2, "Live DJing", status);
        if (video.State is VideoPlaybackState.Playing or VideoPlaybackState.Loading or VideoPlaybackState.Paused ||
            video.IsPlayingLocalVideo || video.IsPlayingImage)
        {
            var details = video.IsPlayingLocalVideo || localVideoStream ? "Watching a local video" : video.IsPlayingImage ? "Viewing images" :
                video.IsAudioOnly ? "Listening to music" : "Watching videos";
            var title = stream.Mode == StreamMode.Viewing ? stream.CurrentRoomState?.MediaTitle : queue.Current?.Title;
            if (Plugin.Cfg.DiscordActivityShowTitles && !video.IsPlayingLocalVideo && !localVideoStream && !video.IsPlayingImage)
                details = DiscordActivityPrivacy.PublicTitle(title) ?? details;
            if (video.State == VideoPlaybackState.Paused) status = "Paused · " + status;
            return new(video.IsAudioOnly ? 2 : 3, details, status);
        }
        return inParty ? new(0, "Watch Party", status) : null;
    }

    private void DrawDiscordActivitySettings()
    {
        DrawOtherSettingsPanel("##discordActivitySettings", 150f, () =>
        {
            SettingsSection("Discord activity", "Show what you are doing in Alpha Channel on your Discord profile.");
            var enabled = Plugin.Cfg.DiscordActivityEnabled;
            if (DrawSettingsToggle("##discordActivityEnabled", "Show Alpha Channel activity on Discord", ref enabled))
            { Plugin.Cfg.DiscordActivityEnabled = enabled; Plugin.Cfg.Save(); }
            var titles = Plugin.Cfg.DiscordActivityShowTitles;
            if (DrawSettingsToggle("##discordActivityTitles", "Include video and music titles", ref titles))
            { Plugin.Cfg.DiscordActivityShowTitles = titles; Plugin.Cfg.Save(); }
            DrawSettingsExplanation("Requires Discord desktop and activity sharing enabled in Discord. Browser details, local filenames and private-party details are hidden. No activity is shown while idle outside a party.");
        }, autoHeight: true);
    }
}

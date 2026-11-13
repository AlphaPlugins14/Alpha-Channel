namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private enum HeaderActivityKind { None, Halloween, Arcade, RetroGame, Browser, Studio, Dj, LocalVideo, Images, LiveStream }

    private HeaderActivityKind HeaderActivity
    {
        get
        {
            if (HalloweenActivityActive) return HeaderActivityKind.Halloween;
            if (!IsLocalActivityHost) return HeaderActivityKind.None;
            if (ActiveActivity != ActiveActivityKind.None) return HeaderActivityKind.Arcade;
            if (screenController.Engine.IsPlayingGame) return HeaderActivityKind.RetroGame;
            if (screenController.Engine.IsPlayingBrowser) return HeaderActivityKind.Browser;
            if (studioSessionActive) return HeaderActivityKind.Studio;
            if (djBroadcastingToWatchParty && IsPlayingOwnIcecastDjStream) return HeaderActivityKind.Dj;
            if (localVideoBroadcastArmed) return HeaderActivityKind.LocalVideo;
            if (screenController.Engine.IsPlayingImage) return HeaderActivityKind.Images;
            if (queue.Current is { IsTransient: true, Source: "Live Stream" }) return HeaderActivityKind.LiveStream;
            return HeaderActivityKind.None;
        }
    }

    private string HeaderActivityTitle => HeaderActivity switch
    {
        HeaderActivityKind.Halloween => "Halloween 2026",
        HeaderActivityKind.Arcade => ActivityTitle(ActiveActivity),
        HeaderActivityKind.RetroGame => "Retro game",
        HeaderActivityKind.Browser => "Browser",
        HeaderActivityKind.Studio => "Broadcast Studio",
        HeaderActivityKind.Dj => "DJ live broadcast",
        HeaderActivityKind.LocalVideo => "Local video broadcast",
        HeaderActivityKind.Images => "Images / Slideshow",
        HeaderActivityKind.LiveStream => "Live PC stream",
        _ => string.Empty,
    };

    private void EndHeaderActivity()
    {
        // Resolve again at click time: a room or activity may have changed since rendering.
        switch (HeaderActivity)
        {
            case HeaderActivityKind.Halloween:
                EndHalloween();
                break;
            case HeaderActivityKind.Arcade:
                EndCurrentActivity(ActiveActivity);
                break;
            case HeaderActivityKind.RetroGame:
                RequestGameExitConfirmation();
                break;
            case HeaderActivityKind.Browser:
                CloseCurrentBrowser();
                break;
            case HeaderActivityKind.LocalVideo:
                StopLocalVideoWatchPartyBroadcast();
                break;
            case HeaderActivityKind.Dj:
                EndPlaybackAndDespawnTv();
                if (!studioBroadcastRequested) djBroadcastingToWatchParty = false;
                break;
            case HeaderActivityKind.Studio:
            case HeaderActivityKind.Images:
            case HeaderActivityKind.LiveStream:
                EndPlaybackAndDespawnTv();
                break;
        }
    }

    private void CloseCurrentBrowser()
    {
        var engine = screenController.Engine;
        if (engine.Browser is { } browser) ReleaseBrowserKeys(browser);
        StopBrowserWatchPartyBroadcast();
        engine.StopVideo();
    }
}

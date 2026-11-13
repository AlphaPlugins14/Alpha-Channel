using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly SpotifyCoverPublisher spotifyCoverPublisher = new();

    private void UpdateSpotifyCover()
    {
        var enabled = spotifyOwnsStudio && Plugin.Cfg.SpotifyCoverOnTv && video.IsAudioOnly;
        spotifyCoverPublisher.Update(enabled && studioPublished && stream.Mode == StreamMode.Hosting
            ? spotifyClient?.Snapshot.ArtworkBase64 : null, enabled ? CurrentSession?.Token : null);
    }

    private AudioScreenTextSelection WithSpotifyCover(AudioScreenTextSelection text) => text with
    {
        CoverId = spotifyOwnsStudio && Plugin.Cfg.SpotifyCoverOnTv ? spotifyCoverPublisher.Id : "",
        CoverBase64 = spotifyOwnsStudio && Plugin.Cfg.SpotifyCoverOnTv ? spotifyClient?.Snapshot.ArtworkBase64 : null
    };
}

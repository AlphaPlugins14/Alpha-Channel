namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    internal void ResetViewerPlaybackStart()
    {
        viewerTvSpawnPromptShownForCurrentRoom = false;
        viewerTvSpawnPromptRequested = false;
        viewerTvPlacementPromptRequested = false;
        viewerTvSpawnPromptForQuiz = false;
        viewerTvSpawnPromptForPictionary = false;
        viewerTvSpawnPromptForRoulette = false;
    }

    internal void RequestViewerTvSpawnPrompt(bool applyPlaybackStartBehaviour = false)
    {
        if (stream.Mode != StreamMode.Viewing ||
            ViewerTvEnabled || screenController.Engine.AudioOnlyMode ||
            viewerTvSpawnPromptShownForCurrentRoom)
        {
            return;
        }

        // A territory transition can leave the player object or zone details
        // temporarily unavailable. Let the next host state retry the prompt.
        if (!WatchPartyTvLocation.IsLocalPlayerReady())
            return;

        viewerTvSpawnPromptShownForCurrentRoom =
            true;

        if (applyPlaybackStartBehaviour &&
            Plugin.Cfg.ViewerPlaybackStartBehaviour == ViewerPlaybackStartBehaviour.MiniPlayer)
        {
            OnMiniPlayerRequested?.Invoke();
            return;
        }

        if (PartySyncTvPlacement)
        {
            var placement = WatchPartyTvLocation.Assess(stream.CurrentRoomState);
            if (placement != ViewerTvPlacementStatus.Near)
            {
                RequestViewerTvPlacementPrompt(placement);
                return;
            }
        }

        if (applyPlaybackStartBehaviour &&
            Plugin.Cfg.ViewerPlaybackStartBehaviour == ViewerPlaybackStartBehaviour.SpawnTv)
        {
            RequestViewerTvSpawn();
            return;
        }

        viewerTvSpawnPromptRequested =
            true;

        viewerTvSpawnPromptForQuiz =
            false;
        viewerTvSpawnPromptForPictionary = false;
        viewerTvSpawnPromptForRoulette = false;
    }

}

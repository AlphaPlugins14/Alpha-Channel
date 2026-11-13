namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool TeamsCanChooseTvWord => teamsPhase == TeamsPhase.ChoosingWords &&
        TeamsLocalDrawerColour is 0 or 1 && !teamsSelected[TeamsLocalDrawerColour] &&
        teamsChoices[TeamsLocalDrawerColour].Length == 3;

    private bool TeamsCanPlayAgainOnTv => teamsPhase == TeamsPhase.Finished && stream.Mode == StreamMode.Hosting;

    internal bool PictionaryTvControlsInteractive =>
        (PictionaryTeamsActive && (teamsPhase == TeamsPhase.Lobby || TeamsCanChooseTvWord ||
            TeamsCanGuess || LocalCanDrawPictionaryOnTv || TeamsCanPlayAgainOnTv)) ||
        LocalCanSubmitPictionaryGuess || LocalCanDrawPictionaryOnTv ||
        (pictionaryPhase == PictionaryPhase.GameOver && stream.Mode == StreamMode.Hosting) ||
        pictionaryPhase == PictionaryPhase.ChoosingWord && LocalIsPictionaryDrawer &&
        pictionaryWordChoices.Length == 3;
}

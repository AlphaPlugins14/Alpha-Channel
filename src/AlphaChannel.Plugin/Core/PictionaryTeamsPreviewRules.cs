namespace AlphaChannel.Plugin;

internal static class PictionaryTeamsPreviewRules
{
#if DEBUG
    internal const int MinimumTeamSize = 1;
    internal const int MinimumPlayers = 1;
    internal static bool DrawerCanGuess(int teamSize) => teamSize == 1;
#else
    internal const int MinimumTeamSize = 2;
    internal const int MinimumPlayers = 4;
    internal static bool DrawerCanGuess(int teamSize) => false;
#endif
    internal static bool IsInactiveTeam(int teamSize, int otherTeamSize)
    {
#if DEBUG
        return teamSize == 0 && otherTeamSize == 1;
#else
        return false;
#endif
    }

    internal static bool TeamsHaveEnoughPlayers(int red, int blue) =>
        red >= MinimumTeamSize && blue >= MinimumTeamSize || IsInactiveTeam(red, blue) || IsInactiveTeam(blue, red);
}

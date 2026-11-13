namespace AlphaChannel.Plugin;

internal static class SkyRailRules
{
    // -1 represents Random; named routes keep their existing snapshot indices.
    internal static int NextLobbyMap(int choice, int direction, int mapCount) =>
        ((Math.Clamp(choice, -1, mapCount - 1) + 1 + (direction < 0 ? mapCount : 1)) % (mapCount + 1)) - 1;
    internal static int StartMap(bool custom, int choice, int randomMap, int mapCount) =>
        custom && choice >= 0 ? Math.Clamp(choice, 0, mapCount - 1) : Math.Clamp(randomMap, 0, mapCount - 1);

    internal const int BossWarningSeconds = 4;
    internal const double BossWarningFadeSeconds = 0.6;
    internal const double BossWarningTotalSeconds = BossWarningSeconds + BossWarningFadeSeconds;
    internal const double BossRetreatSeconds = 1.2;
    internal static float BossRetreatProgress(DateTime now, DateTime waveEnds) =>
        (float)Math.Clamp((now - waveEnds.AddSeconds(30)).TotalSeconds / BossRetreatSeconds, 0, 1);
    internal static double BossWarningOpacity(double elapsedSeconds) =>
        1 - Math.Clamp((elapsedSeconds - BossWarningSeconds) / BossWarningFadeSeconds, 0, 1);
    internal static int BossHealthForPlayers(int players) => 70 + Math.Max(0, players - 1) * 80;
    internal static int BossHitDamage(int map, float offsetX, float offsetY) =>
        (map == 0 ? offsetX is >= -57 and <= 57 && offsetY is >= -15 and <= 43
            : map == 1 ? offsetX is >= -45 and <= 45 && offsetY is >= -25 and <= 35
            : map == 2 ? offsetX is >= -40 and <= 40 && offsetY is >= 0 and <= 50
            : map == 3 ? offsetX is >= -34 and <= 34 && offsetY is >= -4 and <= 42
            : map == 4 ? offsetX is >= -34 and <= 34 && offsetY is >= -2 and <= 46
            : offsetX * offsetX + offsetY * offsetY <= 30 * 30) ? 2 : 1;
    internal static bool BossArriving(DateTime now, DateTime waveStarts, int bossHealth) =>
        bossHealth > 0 && now < waveStarts.AddSeconds(BossWarningTotalSeconds);
    internal static int Multiplier(int combo) => combo >= 9 ? 5 : combo >= 6 ? 3 : combo >= 3 ? 2 : 1;
    internal static (int Points, int Score, int Combo) ScoreShot(int score, int combo, int kind, bool special)
    {
        if (special) return (0, score, combo);
        var points = kind switch { 0 => 10, 1 => 25, 2 => 50, 3 => -100, _ => 0 };
        var nextCombo = points > 0 ? Math.Min(9, combo + 1) : 0;
        if (points > 0) points *= Multiplier(nextCombo);
        return (points, Math.Max(0, score + points), nextCombo);
    }
    // A boss adds at most 30 seconds to a wave; an abandoned encounter cannot stall a flight.
    internal static bool WaveComplete(DateTime now, DateTime waveEnds, int bossHealth) =>
        now >= waveEnds && (bossHealth <= 0 || now >= waveEnds.AddSeconds(30));
    internal static bool CurrentContribution(string session, int round, int wave,
        string currentSession, int currentRound, int currentWave) =>
        session == currentSession && round == currentRound && wave == currentWave;
    internal static int Rank(int score, IEnumerable<int> scores) => 1 + scores.Count(other => other > score);
}

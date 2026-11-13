namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool skyRailPersonalBestRecorded;

    private void ResetSkyRailPersonalBestRound() => skyRailPersonalBestRecorded = false;

    private void UpdateSkyRailPersonalBest()
    {
        ObserveArcadeScores();
        if (skyRailPersonalBestRecorded || skyRailPhase != SkyRailPhase.Results) return;
        skyRailPersonalBestRecorded = true;
        if (skyRailWave < skyRailWaveCount || !skyRailLocked.Contains(LocalSkyRailId) ||
            !skyRailPlayers.TryGetValue(LocalSkyRailId, out var player)) return;
        var scores = Plugin.Cfg.SkyRailPersonalBestScores ??= new();
        if (player.Score <= scores.GetValueOrDefault(LocalSkyRailId)) return;
        scores[LocalSkyRailId] = player.Score;
        Plugin.Cfg.Save();
        if (IsLocalActivityHost)
            AcceptSkyRailPersonalBest(LocalSkyRailId, skyRailSessionId!, skyRailRound, player.Score);
        else
            SendArcadePlayerAction("sky-rail", $"P|{skyRailSessionId}|{skyRailRound}|{player.Score}");
    }

    private void AcceptSkyRailPersonalBest(string playerId, string session, int round, int score)
    {
        if (!IsLocalActivityHost || skyRailPhase != SkyRailPhase.Results ||
            session != skyRailSessionId || round != skyRailRound || !skyRailLocked.Contains(playerId) ||
            !skyRailPlayers.TryGetValue(playerId, out var player) || player.Score != score || player.PersonalBest)
            return;
        skyRailPlayers[playerId] = player with { PersonalBest = true };
        skyRailRevision++;
        whiteboardTvDirty = true;
        SendSkyRailState();
    }
}

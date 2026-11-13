namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // One shared opportunity every 1-3 seconds. The first hop always happens
    // after each round resets; later opportunities have a 75% chance.
    private (int Lane, int Offset) ChocoRaceGateHop(DateTime now)
    {
        if (chocoRacePhase is not (ChocoRacePhase.WaitingForBet or ChocoRacePhase.Betting) ||
            chocoRaceRoundStartedUtc == DateTime.MinValue || chocoRaceSessionId is null)
            return (-1, 0);

        var elapsed = Math.Max(0d, (now - chocoRaceRoundStartedUtc).TotalSeconds);
        var slot = (int)(elapsed / 2d);
        uint hash = 2166136261u;
        foreach (var character in chocoRaceSessionId)
            hash = unchecked((hash ^ character) * 16777619u);
        hash = unchecked((hash ^ (uint)chocoRaceRound) * 16777619u);
        var seed = unchecked((int)hash);

        for (var candidate = Math.Max(1, slot - 1); candidate <= slot + 1; candidate++)
        {
            var key = unchecked(candidate * 3);
            var eventTime = candidate * 2d + ChocoRaceRandomUnit(seed, key) - 0.5d;
            const double hopDuration = 0.68d;
            var progress = (elapsed - eventTime) / hopDuration;
            if (progress is < 0d or >= 1d ||
                candidate != 1 && ChocoRaceRandomUnit(seed, key + 1) >= 0.75d)
                continue;

            var lane = Math.Min(5, (int)(ChocoRaceRandomUnit(seed, key + 2) * 6d));
            var offset = (int)Math.Round(10d * Math.Sin(Math.PI * progress));
            return (lane, offset);
        }
        return (-1, 0);
    }
}

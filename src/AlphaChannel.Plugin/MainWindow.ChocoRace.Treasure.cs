namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void PrepareChocoRaceTreasureChest()
    {
        if (Random.Shared.NextDouble() >= ChocoRaceTreasureChancePerRace) return;

        var chestLane = Random.Shared.Next(chocoRaceRacers.Length);
        var laneEvents = ChocoRaceObstaclesForLane(chestLane);
        var position = 0.44d + Random.Shared.NextDouble() * 0.14d;
        for (var attempt = 0; attempt < 16 &&
             laneEvents.Any(obstacle => Math.Abs(obstacle.CoursePosition - position) < 0.035d); attempt++)
            position = 0.44d + Random.Shared.NextDouble() * 0.14d;

        var hitSeconds = CalculateChocoRaceTimeForDistance(chocoRaceRacers[chestLane],
            chocoRaceCourseDistance * position, laneEvents);
        chocoRaceObstacles.Add(new ChocoRaceObstacle(chestLane, false, position,
            hitSeconds, 0d, 1d, false, IsChest: true));

        for (var lane = 0; lane < chocoRaceRacers.Length; lane++)
        {
            if (lane == chestLane) continue;
            var meteor = new ChocoRaceObstacle(lane, false, 0d,
                hitSeconds + ChocoRaceMeteorFallSeconds,
                ChocoRaceMeteorDurationSeconds, ChocoRaceMeteorSpeedMultiplier,
                false, IsMeteor: true);
            var eventIndexes = Enumerable.Range(0, chocoRaceObstacles.Count)
                .Where(index => chocoRaceObstacles[index].Lane == lane)
                .OrderBy(index => chocoRaceObstacles[index].CoursePosition).ToArray();
            var earlierEffects = new List<ChocoRaceObstacle> { meteor };
            foreach (var index in eventIndexes)
            {
                var trackEvent = chocoRaceObstacles[index];
                var correctedHit = CalculateChocoRaceTimeForDistance(chocoRaceRacers[lane],
                    chocoRaceCourseDistance * trackEvent.CoursePosition, earlierEffects);
                trackEvent = trackEvent with { HitSeconds = correctedHit };
                chocoRaceObstacles[index] = trackEvent;
                earlierEffects.Add(trackEvent);
            }
            chocoRaceObstacles.Add(meteor);
        }
    }
}

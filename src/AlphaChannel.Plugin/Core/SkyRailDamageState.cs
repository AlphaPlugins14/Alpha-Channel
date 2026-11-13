namespace AlphaChannel.Plugin;

internal sealed class SkyRailDamageState
{
    private readonly HashSet<int> escapedRockets = [];
    private int hits;
    private DateTime repairEndsUtc;
    private readonly DateTime[] hitTimes = new DateTime[6];
    internal bool Repairing(DateTime now)
    {
        if (repairEndsUtc != DateTime.MinValue && now >= repairEndsUtc)
        {
            hits = 0;
            repairEndsUtc = DateTime.MinValue;
            Array.Clear(hitTimes);
        }
        return now < repairEndsUtc;
    }
    internal int Hits(DateTime now) { Repairing(now); return hits; }
    internal bool LampFlashing(int lamp, DateTime now) => lamp >= 0 && lamp < Hits(now) &&
        now >= hitTimes[lamp] && now - hitTimes[lamp] < TimeSpan.FromSeconds(.35);
    internal void ObserveEscape(int rocketId, DateTime now)
    {
        if (!escapedRockets.Add(rocketId)) return;
        if (Repairing(now)) return;
        hitTimes[hits++] = now;
        if (hits == 6) repairEndsUtc = now.AddSeconds(3);
    }
    internal void Reset()
    {
        escapedRockets.Clear();
        hits = 0;
        repairEndsUtc = DateTime.MinValue;
        Array.Clear(hitTimes);
    }
}

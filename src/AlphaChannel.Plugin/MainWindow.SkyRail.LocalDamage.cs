namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string? skyRailDamageRound;
    private readonly SkyRailDamageState skyRailLocalDamage = new();

    private bool SkyRailRepairing(DateTime now) => skyRailPhase == SkyRailPhase.Playing &&
        skyRailLocked.Contains(LocalSkyRailId) && skyRailLocalDamage.Repairing(now);

    private void ResetSkyRailLocalDamage()
    {
        skyRailDamageRound = null;
        skyRailLocalDamage.Reset();
    }

    private void UpdateSkyRailLocalDamage(DateTime now)
    {
        var round = $"{skyRailSessionId}|{skyRailRound}|{skyRailWave}";
        if (skyRailDamageRound != round)
        {
            ResetSkyRailLocalDamage();
            skyRailDamageRound = round;
        }
        if (skyRailPhase != SkyRailPhase.Playing || !skyRailLocked.Contains(LocalSkyRailId)) return;
        // Host-confirmed escapes survive later snapshots and do not depend on
        // the viewer's clock matching the host's launch timestamps.
        foreach (var id in skyRailEscapedRocketIds)
            skyRailLocalDamage.ObserveEscape(id, now);
    }

    private void DrawSkyRailLocalDamage(byte[] pixels, int width, int height, DateTime now)
    {
        if (!SkyRailRepairing(now)) return;
        // Restrict corruption to the battle view, leaving the dashboard and settings usable.
        var tick = (uint)(now.Ticks / TimeSpan.TicksPerMillisecond / 65);
        var noise = unchecked(tick * 747796405u + 2891336453u);
        for (var y = 106; y < Math.Min(570, height); y += 4)
            for (var x = 28; x < Math.Min(1252, width); x += 6)
            {
                noise ^= noise << 13;
                noise ^= noise >> 17;
                noise ^= noise << 5;
                var shade = (byte)(noise & 255);
                var colour = ((uint)shade << 16) | ((uint)shade << 8) | shade;
                if ((noise & 7) < 3)
                    FillPictionaryPixels(pixels, width, height, x, y, 6, 3, colour);
            }
        for (var band = 0; band < 6; band++)
        {
            var y = 109 + (int)((tick * 17 + (uint)band * 79) % 449);
            FillPictionaryPixels(pixels, width, height, 28, y, 1224, 2,
                band % 2 == 0 ? 0xB6D5DEu : 0x302337u);
        }
        var red = tick % 6 < 3 ? 0xFF6577u : 0xBF334Bu;
        FillQuizTvPanel(pixels, width, height, 310, 275, 660, 116, 0x151724, red);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 298,
            "AIRSHIP SYSTEMS DAMAGED", 3, red);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 349,
            "REPAIRING..", 3, 0xF4F3FC);
    }
}

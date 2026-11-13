using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private int ChocoRaceCameraX(DateTime now)
    {
        if (!ChocoRaceScrollingTrackEnabled) return 0;
        var leadingProgress = Enumerable.Range(0, chocoRaceRacers.Length)
            .Max(lane => ChocoRaceProgress(lane, now));
        return Math.Clamp((int)Math.Round(49 + ChocoRaceLongTrackTravel * leadingProgress - 617),
            0, ChocoRaceLongTrackTravel - ChocoRaceTrackTravel + 1);
    }

    private static Vector2 ChocoRaceChipStackCenter(int lane) =>
        new(39 + lane * 165, 471);

    private void DrawChocoRaceBetStack(byte[] pixels, int lane, DateTime now)
    {
        var total = chocoRaceBets.Where(bet => bet.Lane == lane).Sum(bet => bet.Amount);
        if (total <= 0 || chocoRacePhase == ChocoRacePhase.Results) return;
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        var center = ChocoRaceChipStackCenter(lane);
        var x = (int)center.X;
        var y = (int)center.Y;
        if (chocoRaceHoveredBetLane == lane)
            RaceCircle(pixels, width, height, x, y + 3,
                15 + ChocoRaceHoverPulse(now), 0xF6BC4E, false, 2);
        RaceCircle(pixels, width, height, x + 2, y + 5, 13, 0x111018);
        for (var layer = 3; layer >= 0; layer--)
        {
            RaceCircle(pixels, width, height, x, y + layer * 2, 12, 0x6B3B91);
            RaceCircle(pixels, width, height, x, y + layer * 2, 10, 0xEAC366, false, 2);
        }
        RaceCircle(pixels, width, height, x, y, 10, 0x603BA0);
        var label = total >= 1000 ? $"{total / 1000d:0.#}K" : total.ToString();
        DrawPictionaryPixelTextCentered(pixels, width, height, x, y - 4, label, 1, 0xFFF5D8);
    }

    private void DrawChocoRaceBetStackTooltip(byte[] pixels)
    {
        var lane = chocoRaceHoveredBetLane;
        if (lane is < 0 or >= 6 || chocoRaceScoreboardOpen ||
            chocoRacePhase == ChocoRacePhase.Results) return;
        var bets = chocoRaceBets.Where(bet => bet.Lane == lane)
            .Select(bet => (Name: chocoRacePlayers.TryGetValue(bet.PlayerId, out var player)
                    ? player.Name : "Player", bet.Amount,bet.PlayerId))
            .OrderByDescending(bet => bet.Amount)
            .ThenBy(bet => bet.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (bets.Length == 0) return;

        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        const int boxWidth = 284;
        var center = ChocoRaceChipStackCenter(lane);
        var count = Math.Min(7, bets.Length);
        var boxHeight = 43 + count * 25 + (bets.Length > count ? 22 : 0);
        var x = (int)center.X + 24;
        if (x + boxWidth > 1110) x = (int)center.X - boxWidth - 22;
        x = Math.Clamp(x, 18, width - boxWidth - 18);
        var y = (int)center.Y - boxHeight - 18;
        y = Math.Clamp(y, 120, height - boxHeight - 16);

        FillQuizTvPanel(pixels, width, height, x, y, boxWidth, boxHeight, 0x21162F, 0xF6BC4E);
        DrawPictionaryPixelText(pixels, width, height, x + 12, y + 11,
            $"BETS ON #{lane + 1} {RaceLedgerText(chocoRaceRacers[lane].Name, 10)}", 2, 0xF6BC4E);
        for (var index = 0; index < count; index++)
            DrawUsernamePixelText(pixels, width, height, x + 13, y + 38 + index * 25,
                $"{RaceLedgerText(bets[index].Name, 14)}: {bets[index].Amount}", 2, 0xFFF5D8,bets[index].PlayerId);
        if (bets.Length > count)
            DrawPictionaryPixelText(pixels, width, height, x + 13, y + 38 + count * 25,
                $"+{bets.Length - count} MORE", 2, 0xB9A9CF);
    }

    private void DrawChocoRaceSlipTooltip(byte[] pixels)
    {
        if (chocoRaceHoveredControlTarget != 221 || chocoRaceScoreboardOpen ||
            chocoRaceSettingsOpen) return;
        var localId = LocalChocoRaceUserId;
        var entries = Enumerable.Range(0, 6)
            .Select(lane => (Lane: lane, Amount: ChocoRaceDraftTotal > 0
                ? chocoRaceDraftByLane[lane]
                : chocoRaceBets.Where(bet => bet.PlayerId == localId && bet.Lane == lane)
                    .Sum(bet => bet.Amount)))
            .Where(entry => entry.Amount > 0).ToArray();
        if (entries.Length == 0) return;
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        const int boxWidth = 270;
        var boxHeight = 45 + entries.Length * 24;
        const int x = 779;
        var y = 581 - boxHeight - 8;
        FillQuizTvPanel(pixels, width, height, x, y, boxWidth, boxHeight, 0x21162F, 0xF6BC4E);
        DrawPictionaryPixelText(pixels, width, height, x + 12, y + 10,
            ChocoRaceDraftTotal > 0 ? "PENDING SPREAD" : "LOCKED BET", 2, 0xF6BC4E);
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            DrawPictionaryPixelText(pixels, width, height, x + 13, y + 37 + index * 24,
                $"#{entry.Lane + 1} {RaceLedgerText(chocoRaceRacers[entry.Lane].Name, 10)}: {entry.Amount}",
                2, 0xFFF5D8);
        }
    }
}

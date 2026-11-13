using AlphaChannel.Plugin.ActivityPoints;
using System.Diagnostics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private long recentActivityPointsAward;
    private DateTime recentActivityPointsAwardUtc = DateTime.MinValue;
    private string? recentActivityPointsAccountId;
    private const int CanvasMinutesPerPoint = 20;
    private string? canvasPointsSessionId;
    private string? canvasPointsAccountId;
    private string? canvasPointsStintId;
    private long canvasPointsStartedAt;
    private int canvasPointsAwardedIntervals;

    private void ResetCanvasActivityPointsClock()
    {
        canvasPointsSessionId = null;
        canvasPointsAccountId = null;
        canvasPointsStintId = null;
        canvasPointsStartedAt = 0;
        canvasPointsAwardedIntervals = 0;
    }

    private void UpdateCanvasActivityPoints()
    {
        UpdateAdditionalArcadePoints();
        var accountId = CurrentSession?.AccountId;
        if (!StandaloneWhiteboardActive || whiteboardSessionId is null ||
            string.IsNullOrWhiteSpace(accountId) || stream.Mode == StreamMode.None)
        {
            ResetCanvasActivityPointsClock();
            return;
        }

        if (!string.Equals(canvasPointsSessionId, whiteboardSessionId, StringComparison.Ordinal) ||
            !string.Equals(canvasPointsAccountId, accountId, StringComparison.Ordinal))
        {
            canvasPointsSessionId = whiteboardSessionId;
            canvasPointsAccountId = accountId;
            canvasPointsStintId = Guid.NewGuid().ToString("N");
            canvasPointsStartedAt = Stopwatch.GetTimestamp();
            canvasPointsAwardedIntervals = 0;
            return;
        }

        var completedIntervals = (int)Math.Min(int.MaxValue,
            Stopwatch.GetElapsedTime(canvasPointsStartedAt).TotalMinutes / CanvasMinutesPerPoint);
        while (canvasPointsAwardedIntervals < completedIntervals)
        {
            canvasPointsAwardedIntervals++;
            AwardActivityPoints($"canvas:{whiteboardSessionId}:{canvasPointsStintId}:{canvasPointsAwardedIntervals}",
                ActivityPointsService.UnitsPerPoint);
        }
    }

    private void AwardActivityPoints(string eventId, long units)
    {
        var gained = activityPoints.Award(CurrentSession?.AccountId, eventId, units);
        if (gained <= 0) return;
        recentActivityPointsAward = gained;
        recentActivityPointsAwardUtc = DateTime.UtcNow;
        recentActivityPointsAccountId = CurrentSession?.AccountId;
    }

    private void AwardRouletteActivityPoints()
    {
        if (roulettePhase != RoulettePhase.Results || rouletteSessionId is null) return;
        var accountId = CurrentSession?.AccountId;
        if (string.IsNullOrWhiteSpace(accountId)) return;
        if (rouletteRefilledPlayerIds.Contains(accountId)) return;
        var stake = rouletteBets.Where(bet => bet.PlayerId == accountId).Sum(bet => (long)bet.Amount);
        var returned = rouletteWinners.Where(winner => winner.PlayerId == accountId)
            .Sum(winner => (long)winner.Winnings);
        var profit = Math.Max(0L, returned - stake);
        AwardActivityPoints($"roulette:{rouletteSessionId}:{rouletteRoundNumber}", profit * 10L);
    }

    private void AwardChocoRaceActivityPoints()
    {
        if (chocoRacePhase != ChocoRacePhase.Results || chocoRaceSessionId is null) return;
        var accountId = CurrentSession?.AccountId;
        if (string.IsNullOrWhiteSpace(accountId)) return;
        if (chocoRaceRefilledPlayerIds.Contains(accountId)) return;
        var payout = chocoRacePayouts.FirstOrDefault(result => result.PlayerId == accountId);
        if (payout is null) return;
        var profit = Math.Max(0L, (long)payout.Return - payout.Stake);
        AwardActivityPoints($"choco:{chocoRaceSessionId}:{chocoRaceRound}", profit);
    }

    private void AwardPictionaryActivityPoints()
    {
        ObserveArcadeScores();
        if (pictionaryPhase != PictionaryPhase.GameOver || pictionarySessionId is null) return;
        var accountId = CurrentSession?.AccountId;
        if (string.IsNullOrWhiteSpace(accountId)) return;
        var score = Math.Max(0, pictionaryScores.GetValueOrDefault(accountId));
        AwardActivityPoints($"pictionary:{pictionarySessionId}:complete",
            score * ActivityPointsService.UnitsPerPoint);
    }

    private void AwardQuizActivityPoints()
    {
        ObserveArcadeScores();
        if (quizPhase != QuizPhase.Finished || quizSessionId is null) return;
        var accountId = CurrentSession?.AccountId;
        if (string.IsNullOrWhiteSpace(accountId)) return;
        var score = Math.Max(0, quizScores.GetValueOrDefault(accountId));
        AwardActivityPoints($"quiz:{quizSessionId}:complete",
            score * ActivityPointsService.UnitsPerPoint);
    }

    private void DrawActivityPointsCard(Vector2 origin, float width)
    {
        var height = Ui(70f);
        var max = origin + new Vector2(width, height);
        var draw = ImGui.GetWindowDrawList();
        var secondsSinceAward = (DateTime.UtcNow - recentActivityPointsAwardUtc).TotalSeconds;
        var celebrating = secondsSinceAward is >= 0d and < 3d &&
            string.Equals(recentActivityPointsAccountId, CurrentSession?.AccountId, StringComparison.Ordinal);
        draw.AddRectFilled(origin, max, ImGui.GetColorU32(new Vector4(0.075f, 0.075f, 0.13f, 1f)), Ui(10f));
        draw.AddRect(origin, max, ImGui.GetColorU32(celebrating ? AccentHover : Accent),
            Ui(10f), ImDrawFlags.None, Ui(celebrating ? 2f : 1f));

        var star = FontAwesomeIcon.Star.ToIconString();
        var iconPos = origin + UiVec(18f, 24f);
        UiFonts.DrawIcon(draw, ImGui.GetFontSize() * 1.55f, iconPos, ImGui.GetColorU32(new Vector4(1f, 0.75f, 0.29f, 1f)), star);
        draw.AddText(origin + UiVec(58f, 11f), ImGui.GetColorU32(AccentHover), "ARCADE POINTS");

        var accountId = CurrentSession?.AccountId;
        var total = string.IsNullOrWhiteSpace(accountId) || !activityPoints.StorageHealthy
            ? "--" : activityPoints.GetPoints(accountId).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        UiFonts.DrawText(draw, ImGui.GetFontSize() * 1.6f, origin + UiVec(58f, 31f), ImGui.GetColorU32(new Vector4(1f, 0.75f, 0.29f, 1f)), total);
        if (celebrating)
            draw.AddText(origin + new Vector2(width - Ui(91f), Ui(36f)),
                ImGui.GetColorU32(Good), $"+{recentActivityPointsAward}");

        var infoCenter = origin + new Vector2(width - Ui(21f), height - Ui(21f));
        var infoColour = ImGui.GetColorU32(AccentHover);
        draw.AddCircle(infoCenter, Ui(9f), infoColour, 20, Ui(1.2f));
        // Draw the mark directly so its dot and stem stay centred at every UI scale.
        draw.AddCircleFilled(infoCenter + new Vector2(0f, -Ui(3.6f)), Ui(1.1f), infoColour);
        draw.AddRectFilled(infoCenter + UiVec(-1f, -1f),
            infoCenter + UiVec(1f, 4f), infoColour, Ui(1f));
        if (ImGui.IsMouseHoveringRect(infoCenter - UiVec(12f, 12f), infoCenter + UiVec(12f, 12f)))
            UiFonts.SetTooltip("Earn Arcade Points by playing games and contributing to creative activities.");
    }
}

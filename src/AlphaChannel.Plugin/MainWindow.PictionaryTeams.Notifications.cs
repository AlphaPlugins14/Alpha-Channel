namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly PictionaryGuessNotificationQueue teamsDrawerNotifications = new();

    private PictionaryGuessNotificationQueue.Notice? CurrentTeamsDrawerNotification =>
        teamsPhase == TeamsPhase.Drawing && TeamsLocalDrawerColour >= 0 ? teamsDrawerNotifications.Current : null;

    private void UpdateTeamsDrawerNotifications()
    {
        var changed = teamsDrawerNotifications.SetRound(teamsSession, teamsRound, TeamsLocalId,
            teamsPhase == TeamsPhase.Drawing && TeamsLocalDrawerColour >= 0);
        changed |= teamsDrawerNotifications.Advance(DateTime.UtcNow);
        if (changed) whiteboardTvDirty = true;
    }

    private void DrawTeamsDrawerTvNotification(byte[] pixels, int width, int height)
    {
        if (CurrentTeamsDrawerNotification is not { } notice) return;
        FillPictionaryPixels(pixels, width, height, 0, height - 38, width, 38, 0x11172A);
        DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, height - 27,
            TruncateQuizTvText(notice.Text.ToUpperInvariant(), Math.Max(1, (width - 32) / 12)),
            2, notice.Correct ? 0x5CE695u : 0xFF5764u);
    }
}

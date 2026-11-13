namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly PictionaryGuessNotificationQueue pictionaryDrawerNotifications = new();

    private void UpdatePictionaryDrawerNotifications()
    {
        var changed = pictionaryDrawerNotifications.SetRound(pictionarySessionId, pictionaryRound,
            pictionaryDrawerId, LocalIsPictionaryDrawer && pictionaryPhase == PictionaryPhase.Drawing);
        changed |= pictionaryDrawerNotifications.Advance(DateTime.UtcNow);
        if (changed) whiteboardTvDirty = true;
    }

    private PictionaryGuessNotificationQueue.Notice? CurrentPictionaryDrawerNotification =>
        LocalIsPictionaryDrawer && pictionaryPhase == PictionaryPhase.Drawing
            ? pictionaryDrawerNotifications.Current : null;

    private void QueuePictionaryDrawerGuess(PictionaryFeedItem item)
    {
        if (!item.GuessResult || item.SessionId != pictionarySessionId || item.Round != pictionaryRound ||
            pictionaryPhase != PictionaryPhase.Drawing) return;
        if (item.Success) pictionaryGuessedPlayers.Add(item.UserId);
        UpdatePictionaryDrawerNotifications();
        var text = item.Success ? $"{item.Name} guessed correctly!" : $"{item.Name} guessed {item.GuessText}";
        if (pictionaryDrawerNotifications.Enqueue(item.SessionId, item.Round, pictionaryDrawerId,
                item.ChatItemId, new(text, item.Success), DateTime.UtcNow))
            whiteboardTvDirty = true;
    }
}

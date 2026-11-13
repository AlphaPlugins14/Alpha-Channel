using AlphaChannel.Contracts;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static string ArchivedChatKey(string accountId, string conversationId) =>
        $"{accountId}:{conversationId}";

    private bool IsArchivedChat(ConversationSummaryDto chat) =>
        CurrentSession is { } session &&
        Plugin.Cfg.ArchivedGroupChats?.ContainsKey(
            ArchivedChatKey(session.AccountId, chat.ConversationId)) == true;

    private void ArchiveChat(ConversationSummaryDto chat)
    {
        if (CurrentSession is not { } session)
            return;

        Plugin.Cfg.ArchivedGroupChats ??= [];
        Plugin.Cfg.ArchivedGroupChats[ArchivedChatKey(session.AccountId, chat.ConversationId)] =
            new ArchivedGroupChatState
            {
                LastMessageAtUnix = chat.LastMessageAtUnix ?? 0,
                UnreadCount = chat.UnreadCount,
            };
        Plugin.Cfg.Save();

        if (openConversationId == chat.ConversationId)
            openConversationId = null;
    }

    private void RestoreArchivedChat(string accountId, string conversationId)
    {
        if (CurrentSession?.AccountId != accountId ||
            Plugin.Cfg.ArchivedGroupChats?.Remove(
                ArchivedChatKey(accountId, conversationId)) != true)
            return;

        Plugin.Cfg.Save();
    }

    private void RestoreArchivedChatsWithNewMessages(ConversationSummaryDto[] loaded)
    {
        if (CurrentSession is not { } session ||
            Plugin.Cfg.ArchivedGroupChats is not { Count: > 0 } archived)
            return;

        var changed = false;
        foreach (var chat in loaded)
        {
            if (!archived.TryGetValue(
                    ArchivedChatKey(session.AccountId, chat.ConversationId), out var baseline))
                continue;

            if ((chat.LastMessageAtUnix ?? 0) <= baseline.LastMessageAtUnix &&
                chat.UnreadCount <= baseline.UnreadCount)
                continue;

            archived.Remove(ArchivedChatKey(session.AccountId, chat.ConversationId));
            changed = true;
        }

        if (changed)
            Plugin.Cfg.Save();
    }
}

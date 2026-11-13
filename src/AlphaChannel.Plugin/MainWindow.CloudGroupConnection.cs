using AlphaChannel.Plugin.Auth;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool IsCurrentCloudGroupConnection(string token) => Volatile.Read(ref disposeStarted) == 0 &&
        (CurrentSession is null || CurrentSession.Token == token);

    private void OnCloudGroupsInterrupted(string token)
    {
        if (!IsCurrentCloudGroupConnection(token)) return;
        // A transport interruption must not erase messages or drafts already on screen.
        if (GroupChatClient.IsGroup(openConversationId)) {
            Interlocked.Increment(ref messageEncryptionCheckGeneration);
            messageCheckingKeys = false; messageEncryptionReady = false;
            messageNextKeyCheckUtc = DateTime.MinValue; cloudRefreshRequested = true;
        }
    }

    private void OnCloudGroupsLocked(string token)
    {
        if (!IsCurrentCloudGroupConnection(token)) return;
        ReconcileCloudGroupDisplayCache(clearAll: true);
    }

    private void ReconcileCloudGroupDisplayCache(bool clearAll = false)
    {
        var allowed = clearAll ? new HashSet<string>() : dmClient.Groups.State.Groups.Select(g => GroupChatClient.Prefix + g.Id).ToHashSet();
        ReconcileGroupImageCache(allowed, clearAll);
        bool RemoveConversation(string id) => GroupChatClient.IsGroup(id) && !allowed.Contains(id);
        bool RemoveMessage(string id) => GroupChatClient.IsGroup(id) && RemoveConversation(id[..Math.Max(GroupChatClient.Prefix.Length, id.LastIndexOf(':'))]);
        foreach (var key in decryptedCache.Keys.Where(RemoveMessage)) decryptedCache.TryRemove(key, out _);
        foreach (var key in chatsPagePreviews.Keys.Where(RemoveConversation)) chatsPagePreviews.TryRemove(key, out _);
        foreach (var key in chatsHubDrafts.Keys.Where(RemoveConversation).ToArray()) chatsHubDrafts.Remove(key);
        revealedCloudMessages.RemoveWhere(RemoveMessage);
        if (openConversationId is { } conversation && RemoveConversation(conversation)) {
            Interlocked.Increment(ref messagesRequestGeneration);
            Interlocked.Increment(ref messageEncryptionCheckGeneration);
            openMessages = []; openConversationId = null; dmRosterOpen = false;
            messageComposerInput = ""; messageReplyTarget = null;
            messageCheckingKeys = messagesLoading = messagesLoadingOlder = false;
            messageEncryptionReady = false; messageNextKeyCheckUtc = DateTime.MinValue;
            cloudRefreshRequested = false;
        }
    }
}

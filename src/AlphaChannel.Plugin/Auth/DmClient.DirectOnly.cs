using AlphaChannel.Contracts;

namespace AlphaChannel.Plugin.Auth;

internal sealed partial class DmClient
{
    private readonly object directConversationGate = new();
    private readonly SemaphoreSlim directDiscoveryGate = new(1, 1);
    private string? directConversationToken;
    private bool directSessionPinned;
    private readonly HashSet<Guid> directConversationIds = [];
    private readonly HashSet<Guid> disabledConversationIds = [];

    // Legacy server groups deliberately remain in the old protocol/code, but
    // this plugin no longer lists, opens, reads, writes or notifies for them.
    private static bool IsDirectSummary(ConversationSummaryDto chat) => !chat.IsGroup &&
        chat.Members is { Length: 1 } && Guid.TryParse(chat.ConversationId, out _) &&
        Guid.TryParse(chat.Members[0].AccountId, out _);

    internal void SetDirectSession(string? token)
    {
        lock (directConversationGate)
        {
            directSessionPinned = true;
            directConversationToken = token;
            directConversationIds.Clear(); disabledConversationIds.Clear();
        }
    }

    private bool AcceptDirectConversationAccount(string token)
    {
        if (directSessionPinned && directConversationToken != token) return false;
        if (directConversationToken == token) return true;
        directConversationToken = token;
        directConversationIds.Clear(); disabledConversationIds.Clear();
        return true;
    }

    private async Task<ConversationSummaryDto[]?> LoadDirectConversationsAsync(string token)
    {
        lock (directConversationGate) if (!AcceptDirectConversationAccount(token)) return null;
        var serverChats = await GetAsync<ConversationSummaryDto[]>(token, "/dm/conversations").ConfigureAwait(false);
        if (serverChats is null) return null;
        lock (directConversationGate)
        {
            if (!AcceptDirectConversationAccount(token)) return null;
            foreach (var chat in serverChats)
            {
                if (!Guid.TryParse(chat.ConversationId, out var id)) continue;
                if (IsDirectSummary(chat) && !disabledConversationIds.Contains(id)) directConversationIds.Add(id);
                else { disabledConversationIds.Add(id); directConversationIds.Remove(id); }
            }
            return serverChats.Where(chat => IsDirectSummary(chat) &&
                directConversationIds.Contains(Guid.Parse(chat.ConversationId))).ToArray();
        }
    }

    private void RememberCreatedDirectConversation(string token, string? conversation)
    {
        if (!Guid.TryParse(conversation, out var id)) return;
        lock (directConversationGate)
        {
            if (!AcceptDirectConversationAccount(token)) return;
            if (!disabledConversationIds.Contains(id)) directConversationIds.Add(id);
        }
    }

    internal bool IsKnownDirectConversation(string token, string? conversation)
    {
        if (!Guid.TryParse(conversation, out var id)) return false;
        lock (directConversationGate) return directConversationToken == token && directConversationIds.Contains(id);
    }

    private bool IsKnownDisabledConversation(string token, Guid id)
    {
        lock (directConversationGate) return directConversationToken == token && disabledConversationIds.Contains(id);
    }

    // A realtime event can precede the first conversation-list refresh. Resolve
    // its type before any notification, decryption or thread request, sharing a
    // single discovery fetch across concurrent messages. Known retired IDs never
    // trigger repeated requests merely because an old server keeps pushing them.
    internal async Task<bool> IsDirectConversationAsync(string token, string? conversation)
    {
        if (!Guid.TryParse(conversation, out var id)) return false;
        if (IsKnownDirectConversation(token, conversation)) return true;
        if (IsKnownDisabledConversation(token, id)) return false;
        await directDiscoveryGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsKnownDirectConversation(token, conversation)) return true;
            if (IsKnownDisabledConversation(token, id)) return false;
            if (await LoadDirectConversationsAsync(token).ConfigureAwait(false) is null) return false;
            if (IsKnownDirectConversation(token, conversation)) return true;
            lock (directConversationGate)
                if (directConversationToken == token) disabledConversationIds.Add(id);
            return false;
        }
        finally { directDiscoveryGate.Release(); }
    }
}

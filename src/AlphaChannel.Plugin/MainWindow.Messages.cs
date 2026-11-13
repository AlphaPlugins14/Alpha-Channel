using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Crypto;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Legacy direct messages use pairwise DmCipher envelopes; Cloudflare private groups
// use shared end-to-end keys, while public group encryption is server-managed.
// Background decryption populates the per-message plaintext cache used by rendering.
internal sealed partial class MainWindow
{
    private bool conversationsDirty = true;
    private bool conversationsLoading;
    private DateTime conversationsNextRetryUtc = DateTime.MinValue;
    private int conversationsRequestGeneration;
    private ConversationSummaryDto[] conversations = [];
    private string? openConversationId;
    private ConversationMemberDto[] openConversationMembers = [];
    private bool openConversationIsGroup;
    private string openConversationTitle = string.Empty;
    private MessageDto[] openMessages = [];
    private string? messagesNextCursor;
    private bool messagesLoadingOlder;
    private bool threadScrollToBottom;
    private readonly BoundedPlaintextCache decryptedCache = new(2048);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> messageDeletedGroups = new();
    private readonly Dictionary<string, string> memberDisplayNameCache = new();
    private string messageComposerInput = string.Empty;
    private bool messageComposerFocus;
    private MessageDto? messageReplyTarget;
    private string? messageJumpToGroupId;
    private string? messageHighlightedGroupId;
    private DateTime messageHighlightUntilUtc;
    private bool messageLoadingReplyTarget;
    private bool? messageEncryptionReady;
    private string? messageMissingKeyName;
    private string? messageEncryptionError;
    private bool messageEncryptionRepairAttempted;
    private bool messageCheckingKeys;
    private int messageEncryptionCheckGeneration;
    private DateTime messageNextKeyCheckUtc;
    private bool messagesLoading;
    private int messagesRequestGeneration;
    private string? messagesError;
    private int displayedConversationReadActive;
    private DateTime nextDisplayedConversationReadUtc = DateTime.MinValue;

    private bool IsDisplayingConversation(CharacterSession session, string conversationId) =>
        IsCurrentMessageSession(session) && IsOpen && !windowMinimized &&
        currentPage == HomePage.Messages && openConversationId == conversationId;

    private void MarkDisplayedConversationReadIfNeeded(CharacterSession session, string conversationId)
    {
        if (!IsDisplayingConversation(session, conversationId) ||
            DateTime.UtcNow < nextDisplayedConversationReadUtc ||
            !conversations.Any(chat => chat.ConversationId == conversationId && chat.UnreadCount > 0) ||
            Interlocked.CompareExchange(ref displayedConversationReadActive, 1, 0) != 0)
            return;
        nextDisplayedConversationReadUtc = DateTime.UtcNow.AddSeconds(5);
        _ = Task.Run(async () =>
        {
            try
            {
                if (!IsDisplayingConversation(session, conversationId)) return;
                await dmClient.MarkReadAsync(session.Token, conversationId);
                if (IsCurrentMessageSession(session) && !GroupChatClient.IsGroup(conversationId)) conversationsDirty = true;
            }
            catch (Exception)
            {
                AepLog.Warning("[Messages] Could not mark the displayed conversation as read.");
            }
            finally { Interlocked.Exchange(ref displayedConversationReadActive, 0); }
        });
    }

    private bool IsCurrentMessageSession(CharacterSession session) =>
        Volatile.Read(ref disposeStarted) == 0 &&
        string.Equals(CurrentSession?.AccountId, session.AccountId, StringComparison.Ordinal) &&
        string.Equals(CurrentSession?.Token, session.Token, StringComparison.Ordinal);

    private void ResetMessagesForSessionChange()
    {
        dmClient.SetDirectSession(CurrentSession?.Token);
        dmClient.Groups.Stop();
        Interlocked.Increment(ref messageEncryptionCheckGeneration);
        pendingWatchPartyInviteId = null;
        pendingWatchPartyInviteHostId = null;
        pendingWatchPartyInviteAttempt = null;
        pendingWatchPartyInviteStartedUtc = DateTime.MinValue;
        Interlocked.Increment(ref conversationsRequestGeneration);
        Interlocked.Increment(ref messagesRequestGeneration);
        conversationsDirty = true;
        conversationsLoading = false;
        conversationsNextRetryUtc = DateTime.MinValue;
        nextDisplayedConversationReadUtc = DateTime.MinValue;
        conversations = [];
        chatsPagePreviews.Clear(); chatsPageSearch=""; chatsPageFilter=0; chatsPageArchived=false;
        chatsCreationOpen=false; chatsDirectCreationTask=null;
        chatsHubTab = 0; chatsHubDirectId = chatsHubGroupId = null; chatsHubSearch = "";
        chatsDirectory = []; chatsDirectoryAccount = null; chatsDirectoryFetched = chatsDirectoryRetry = DateTime.MinValue;
        chatsDirectoryDetail = null; chatsDirectoryDetailOpen = false; chatsHubInvitationsOpen = false;
        chatsDirectoryLoading = false; chatsDirectoryError = null; chatsHubDrafts.Clear();
        EndFriendGroupSelection();
        openConversationId = null;
        openConversationMembers = [];
        openMessages = [];
        messagesNextCursor = null;
        messagesLoading = false;
        messagesLoadingOlder = false;
        messageLoadingReplyTarget = false;
        messagesError = null;
        messageReplyTarget = null;
        messageEncryptionReady = null;
        messageMissingKeyName = null;
        messageEncryptionError = null;
        messageEncryptionRepairAttempted = false;
        messageCheckingKeys = false;
        decryptedCache.Clear();
        messageDeletedGroups.Clear();
        memberDisplayNameCache.Clear();
    }


    private const string DecryptFailedMarker = "\u0001decrypt_failed";
    private const string RichDmPrefix = "[[AC_DM_V2]]";
    private sealed record RichDmContent(string Kind, string? Text = null, string? ReplyTo = null,
        string? Target = null, int? Emoji = null, bool? Active = null,
        string? HostAccountId = null, string? HostDisplayName = null, RoomDirectoryDto? Room = null);

    private static string EncodeRichDm(RichDmContent content) =>
        RichDmPrefix + System.Text.Json.JsonSerializer.Serialize(content);

    private static RichDmContent? DecodeRichDm(string? plaintext)
    {
        if (plaintext is null || !plaintext.StartsWith(RichDmPrefix, StringComparison.Ordinal))
            return null;
        try
        {
            var content = System.Text.Json.JsonSerializer.Deserialize<RichDmContent>(plaintext[RichDmPrefix.Length..]);
            return content?.Kind switch
            {
                "message" when content.Text is not null => content,
                "reaction" when content.Target is not null && content.Emoji is >= 0 &&
                                content.Emoji < ChatReactionIcons.Length &&
                                content.Active is not null => content,
                "watch_party_invite" when content.HostAccountId is { Length: > 0 } &&
                                          content.HostDisplayName is { Length: > 0 } => content,
                _ => null,
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private void DrawAlphaChatTab()
    {
        if (CurrentSession is not { } session)
        {
            DrawPlainEmpty("Private E2E chats need a signed-in account.", "Open Settings",
                () => currentPage = HomePage.Settings);
            return;
        }

        if (openConversationId is { } openId)
        {
            DrawModernDmThread(session, openId);
            return;
        }

        DrawChatsPage();
    }

    private void OpenCreatedGroupConversation(CharacterSession session, string conversationId,
        string name, FriendDto[] selectedFriends)
    {
        var members = dmClient.Groups.Conversations(session.Token).FirstOrDefault(c => c.ConversationId == conversationId)?.Members ?? selectedFriends.Select(friend =>
            new ConversationMemberDto(friend.AccountId, friend.Handle, friend.DisplayName)).ToArray();
        OpenConversation(session, conversationId, members, isGroup: true, name);
        currentPage = HomePage.Messages;
    }

    private static string ConversationTitle(ConversationSummaryDto conversation) =>
        conversation.IsGroup
            ? conversation.Name ?? "Group chat"
            : conversation.Members.FirstOrDefault()?.DisplayName ?? "Unknown";

    private static string FormatRelativeTime(long unixSeconds)
    {
        var then = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        var delta = DateTime.UtcNow - then;
        if (delta.TotalMinutes < 1)
        {
            return "Just now";
        }

        if (delta.TotalHours < 1)
        {
            return $"{(int)delta.TotalMinutes}m ago";
        }

        if (delta.TotalDays < 1)
        {
            return $"{(int)delta.TotalHours}h ago";
        }

        if (delta.TotalDays < 7)
        {
            return $"{(int)delta.TotalDays}d ago";
        }

        return then.ToLocalTime().ToString("MMM d");
    }

    // Called from Friends and profile "Message" buttons as well as this page. Starts or resumes a
    // 1:1 conversation and switches directly to its thread view.
    private void StartOrOpenConversation(CharacterSession session, string otherAccountId, string otherDisplayName,
        string? initialMessage = null)
    {
        currentPage = HomePage.Messages;
        _ = Task.Run(async () =>
        {
            try
            {
                var conversationId = await dmClient.CreateConversationAsync(session.Token, [otherAccountId]);
                if (conversationId is not null && IsCurrentMessageSession(session))
                {
                    await Plugin.Framework.RunOnFrameworkThread(() =>
                    {
                        if (IsCurrentMessageSession(session))
                        {
                            OpenConversation(session, conversationId,
                                [new ConversationMemberDto(otherAccountId, "", otherDisplayName)],
                                isGroup: false, otherDisplayName);
                            if (initialMessage is not null)
                                SendEncryptedDm(session, conversationId, initialMessage, showInFeed: true);
                        }
                    });
                }
            }
            catch (Exception)
            {
                AepLog.Warning("[Messages] Could not open conversation.");
            }
        });
    }

    private void OpenConversation(CharacterSession session, string conversationId, ConversationMemberDto[] members, bool isGroup, string title)
    {
        // Retain the legacy group UI/crypto implementation, but never enter it.
        if (!GroupChatClient.IsGroup(conversationId) &&
            (isGroup || members.Length != 1 || !dmClient.IsKnownDirectConversation(session.Token, conversationId))) return;
        if (openConversationId is { } previous) chatsHubDrafts[previous] = (messageComposerInput, messageReplyTarget);
        Interlocked.Increment(ref messageEncryptionCheckGeneration);
        openConversationId = conversationId;
        Interlocked.Increment(ref messagesRequestGeneration);
        openConversationMembers = members;
        openConversationIsGroup = isGroup;
        chatsHubTab = isGroup ? 2 : 1;
        if (isGroup) chatsHubGroupId = conversationId; else chatsHubDirectId = conversationId;
        cloudRemoveMember = null; cloudMissingDevices = [];
        dmRosterOpen=false; dmRosterSearch="";
        dmOpenActionsMessage=dmOpenReactionMessage=dmActionsRequested=dmReactionRequested=null;
        openConversationTitle = title;
        openMessages = [];
        messageDeletedGroups.Clear();
        messagesNextCursor = null;
        var draft = chatsHubDrafts.GetValueOrDefault(conversationId);
        messageComposerInput = draft.Text ?? string.Empty;
        messageReplyTarget = draft.Reply;
        messageJumpToGroupId = null;
        messageHighlightedGroupId = null;
        messageLoadingReplyTarget = false;
        messagesError = null;
        threadScrollToBottom = true;
        messageComposerFocus = true;
        messageEncryptionReady = null;
        messageMissingKeyName = null;
        messageEncryptionError = null;
        messageEncryptionRepairAttempted = false;
        messageCheckingKeys = false;
        messageNextKeyCheckUtc = DateTime.MinValue;

        memberDisplayNameCache.Clear();
        memberDisplayNameCache[session.AccountId] = CurrentDisplayName ?? session.DisplayName;
        foreach (var member in members)
        {
            memberDisplayNameCache[member.AccountId] = member.DisplayName;
        }

        RefreshMessages(session, reset: true);
        CheckMessageEncryption(session, conversationId);
    }

    private void CheckMessageEncryption(CharacterSession session, string conversationId, bool repair = false)
    {
        if (GroupChatClient.IsGroup(conversationId)) { CheckCloudGroupEncryption(session, conversationId, repair); return; }
        if (messageCheckingKeys || (!repair && DateTime.UtcNow < messageNextKeyCheckUtc))
            return;
        messageCheckingKeys = true;
        messageNextKeyCheckUtc = DateTime.UtcNow.AddSeconds(15);
        var members = openConversationMembers;
        var messagesToRetry = openMessages;
        var wasReady = messageEncryptionReady == true;
        var generation = Interlocked.Increment(ref messageEncryptionCheckGeneration);
        bool IsCurrentCheck() => IsCurrentMessageSession(session) && openConversationId == conversationId &&
            generation == Volatile.Read(ref messageEncryptionCheckGeneration);
        _ = Task.Run(async () =>
        {
            string? missing = null;
            string? error = null;
            var ready = false;
            try
            {
                if (!await keyVault.EnsureIdentityRegisteredAsync(session.AccountId, session.Token, forceCheck: repair))
                    error = keyVault.GetRegistrationError(session.AccountId) ?? "Your encryption key could not be verified. Click to retry.";
                else
                {
                    ready = true;
                    foreach (var member in members)
                    {
                        var available = await keyVault.CheckOtherPartyKeyAvailabilityAsync(member.AccountId, session.Token);
                        if (available != true)
                        {
                            ready = false;
                            if (available == false)
                            {
                                missing = member.DisplayName;
                                error = $"{missing}'s encryption key is missing or invalid. They need to open this chat and click to repair.";
                            }
                            else
                                error = "Member encryption keys could not be checked. Click to retry.";
                            break;
                        }
                    }
                }
                if (!IsCurrentCheck()) return;
                if (ready && (repair || !wasReady))
                {
                    var identity = await keyVault.EnsureIdentityAsync(session.AccountId, session.Token);
                    foreach (var message in messagesToRetry)
                    {
                        if (!IsCurrentCheck()) return;
                        if (decryptedCache.GetValueOrDefault(message.Id) == DecryptFailedMarker)
                            await DecryptAndCacheAsync(session, message, identity);
                    }
                }
            }
            catch (Exception)
            {
                AepLog.Warning("[Messages] Encryption status check failed.");
                ready = false;
                error = "Encryption could not be checked. Click to retry.";
            }
            finally
            {
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (!IsCurrentCheck()) return;
                    messageMissingKeyName = missing;
                    messageEncryptionError = error;
                    messageEncryptionReady = ready;
                    if (repair) messageEncryptionRepairAttempted = true;
                    messageCheckingKeys = false;
                    messageNextKeyCheckUtc = DateTime.UtcNow.AddSeconds(ready ? 300 : 15);
                });
            }
        });
    }

    // Fired from StreamClient's receive loop when a dm.message push arrives (see
    // AlphaChannel.Contracts.SocialSignalType) - the push carries this recipient's own sealed copy
    // (Ciphertext/Nonce/Tag), so a message to the currently-open thread can be decrypted and
    // appended immediately with no REST round-trip. Anything else just marks the conversation list
    // stale so its unread count catches up next time it's drawn.
    private void ApplyIncomingDm(SocialControl update)
    {
        var session = CurrentSession;
        if (session is null || update.ConversationId is not { } conversation) return;
        _ = Task.Run(async () =>
        {
            if (!await dmClient.IsDirectConversationAsync(session.Token, conversation).ConfigureAwait(false)) return;
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (IsCurrentMessageSession(session)) ApplyIncomingDmOnFramework(update);
            });
        });
    }

    private void ApplyIncomingDmOnFramework(SocialControl update)
    {
        if (CurrentSession is not { } active || !dmClient.IsKnownDirectConversation(active.Token, update.ConversationId)) return;
        conversationsDirty = true;
        if (update.ConversationId is { } previewConversation) chatsPagePreviews.TryRemove(previewConversation,out _);
        if (update.ConversationId is { Length: > 0 } notificationConversation &&
            update.MessageId is { Length: > 0 } && update.AccountId is { Length: > 0 } notificationSender &&
            CurrentSession is { } notificationSession && notificationSender != notificationSession.AccountId &&
            (update.GroupId is null || !messageDeletedGroups.ContainsKey(update.GroupId)))
            QueueNotificationSound(notificationConversation);
        if (update.ConversationId is { Length: > 0 } conversationId &&
            CurrentSession is { } currentSession &&
            Plugin.Cfg.ArchivedGroupChats?.ContainsKey(ArchivedChatKey(
                currentSession.AccountId, conversationId)) == true)
        {
            _ = Plugin.Framework.RunOnFrameworkThread(() =>
                RestoreArchivedChat(currentSession.AccountId, conversationId));
        }

        if (update.ConversationId != openConversationId ||
            update.MessageId is not { Length: > 0 } messageId ||
            update.AccountId is not { Length: > 0 } senderId ||
            update.Ciphertext is not { Length: > 0 } ciphertextBase64 ||
            update.Nonce is not { Length: > 0 } nonceBase64 ||
            update.Tag is not { Length: > 0 } tagBase64 ||
            CurrentSession is not { } session)
        {
            return;
        }

        if (update.GroupId is { } incomingGroupId && messageDeletedGroups.ContainsKey(incomingGroupId))
            return;

        // The push is always this recipient's own copy, so RecipientAccountId is the viewer.
        var message = new MessageDto(messageId, update.GroupId ?? Guid.NewGuid().ToString(), senderId,
            session.AccountId, ciphertextBase64, nonceBase64, tagBase64, update.TimestampUnix ?? 0, null);
        openMessages = [.. openMessages, message];
        threadScrollToBottom = true;

        _ = Task.Run(async () =>
        {
            try
            {
                var myIdentity = await keyVault.EnsureIdentityAsync(session.AccountId, session.Token);
                await DecryptAndCacheAsync(session, message, myIdentity);
                if (IsCurrentMessageSession(session)) UpdateChatsPreview(update.ConversationId!,message,decryptedCache.GetValueOrDefault(message.Id));
                if (IsDisplayingConversation(session, update.ConversationId!))
                {
                    await dmClient.MarkReadAsync(session.Token, update.ConversationId!);
                    if (IsCurrentMessageSession(session)) conversationsDirty = true;
                }
            }
            catch (Exception)
            {
                AepLog.Warning("[Messages] live decrypt failed.");
            }
        });
    }

    private void ApplyDeletedDm(SocialControl update)
    {
        var session = CurrentSession;
        if (session is null || update.ConversationId is not { } conversation) return;
        _ = Task.Run(async () =>
        {
            if (!await dmClient.IsDirectConversationAsync(session.Token, conversation).ConfigureAwait(false)) return;
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (IsCurrentMessageSession(session)) ApplyDeletedDmOnFramework(update);
            });
        });
    }

    private void ApplyDeletedDmOnFramework(SocialControl update)
    {
        if (CurrentSession is not { } active || !dmClient.IsKnownDirectConversation(active.Token, update.ConversationId)) return;
        conversationsDirty = true;
        if (update.ConversationId is { } deletedChat && chatsPagePreviews.TryGetValue(deletedChat,out var preview) && preview.GroupId==update.GroupId)
            chatsPagePreviews.TryRemove(deletedChat,out _);
        if (update.ConversationId is { } conversation && update.GroupId is { } deletedGroup && CurrentSession is { } session && !GroupChatClient.IsGroup(conversation)) {
            foreach(var key in groupImageViews.Where(p=>p.Value.Conversation==conversation && p.Value.MessageGroupId==deletedGroup).Select(p=>p.Key).ToArray()) {
                var view=groupImageViews[key];view.Revoked=true;view.Dispose();groupImageViews.Remove(key);
                decryptedCache.TryRemove(key,out _);if(groupImageViewer==key)groupImageViewer=null;
            }
            var messages=openMessages.Where(m=>m.GroupId==deletedGroup).ToArray();
            foreach(var message in messages) {
                if(groupImageViews.Remove(message.Id,out var view)){view.Revoked=true;view.Dispose();}
                decryptedCache.TryRemove(message.Id,out _);
                if(groupImageViewer==message.Id)groupImageViewer=null;
            }
            StartGroupImageWork(async _=>{await dmClient.Groups.ForgetDirectMessageImageAsync(session.Token,conversation,deletedGroup);});
        }
        if (update.ConversationId != openConversationId || update.GroupId is not { } groupId)
            return;
        messageDeletedGroups[groupId] = 0;
        openMessages = openMessages.Where(message => message.GroupId != groupId).ToArray();
        if (messageReplyTarget?.GroupId == groupId)
            messageReplyTarget = null;
    }

    private void RefreshConversations(string bearerToken)
    {
        conversationsDirty = false;
        conversationsLoading = true;
        var generation = Interlocked.Increment(ref conversationsRequestGeneration);
        _ = Task.Run(async () =>
        {
            try
            {
                var loaded = await dmClient.GetConversationsAsync(bearerToken);
                if (generation != Volatile.Read(ref conversationsRequestGeneration) ||
                    !string.Equals(CurrentSession?.Token, bearerToken, StringComparison.Ordinal)) return;
                if (loaded is null)
                {
                    conversationsDirty = true;
                    conversationsNextRetryUtc = DateTime.UtcNow.AddSeconds(15);
                }
                else
                {
                    conversations = loaded;
                    conversationsNextRetryUtc = DateTime.MinValue;
                    await Plugin.Framework.RunOnFrameworkThread(() =>
                    {
                        if (string.Equals(CurrentSession?.Token, bearerToken, StringComparison.Ordinal))
                            RestoreArchivedChatsWithNewMessages(loaded);
                    });
                }
            }
            catch (Exception)
            {
                AepLog.Warning("[Messages] conversations fetch failed.");
                if (generation == Volatile.Read(ref conversationsRequestGeneration))
                {
                    conversationsDirty = true;
                    conversationsNextRetryUtc = DateTime.UtcNow.AddSeconds(15);
                }
            }
            finally
            {
                if (generation == Volatile.Read(ref conversationsRequestGeneration))
                    conversationsLoading = false;
            }
        });
    }

    private void RefreshMessages(CharacterSession session, bool reset)
    {
        var conversationId = openConversationId;
        if (conversationId is null)
        {
            return;
        }

        if (reset)
        {
            messagesLoading = true;
        }
        else
        {
            if (messagesNextCursor is null || messagesLoadingOlder)
            {
                return;
            }

            messagesLoadingOlder = true;
        }

        string? before = reset ? null : messagesNextCursor;
        var generation = Interlocked.Increment(ref messagesRequestGeneration);
        _ = Task.Run(async () =>
        {
            try
            {
                var page = await dmClient.GetMessagesAsync(session.Token, conversationId, before);
                if (generation != Volatile.Read(ref messagesRequestGeneration) ||
                    !IsCurrentMessageSession(session)) return;
                if (page is null)
                {
                    messagesError = "Couldn't load messages.";
                    return;
                }

                var myIdentity = GroupChatClient.IsGroup(conversationId) ? null : await keyVault.EnsureIdentityAsync(session.AccountId, session.Token);
                // The server returns newest first; reversing preserves order even for messages
                // sent within the same second (the DTO timestamp has second precision).
                var ordered = page.Items.Reverse().ToArray();

                foreach (var message in ordered)
                {
                    await DecryptAndCacheAsync(session, message, myIdentity);
                    if (reset && IsCurrentMessageSession(session)) UpdateChatsPreview(conversationId,message,decryptedCache.GetValueOrDefault(message.Id));
                }

                if (generation != Volatile.Read(ref messagesRequestGeneration) ||
                    !IsCurrentMessageSession(session) || openConversationId != conversationId)
                    return;

                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (generation != Volatile.Read(ref messagesRequestGeneration) ||
                        !IsCurrentMessageSession(session) || openConversationId != conversationId) return;
                    if (reset)
                    {
                        var loadedIds = ordered.Select(message => message.GroupId).ToHashSet();
                        openMessages = [
                            .. ordered.Where(message => !messageDeletedGroups.ContainsKey(message.GroupId)),
                            .. openMessages.Where(message => !GroupChatClient.IsGroup(conversationId) && !loadedIds.Contains(message.GroupId) &&
                                !messageDeletedGroups.ContainsKey(message.GroupId))
                        ];
                        threadScrollToBottom = true;
                    }
                    else
                    {
                        // Prepend older page; keep scroll position roughly stable by not forcing bottom.
                        openMessages = [.. ordered.Where(message => !messageDeletedGroups.ContainsKey(message.GroupId)), .. openMessages];
                    }

                    // A receipt may arrive while this page is being decrypted.
                    if (GroupChatClient.IsGroup(conversationId))
                        openMessages = openMessages.Select(dmClient.Groups.WithReadReceipt).ToArray();

                    if (generation != Volatile.Read(ref messagesRequestGeneration) ||
                        !IsCurrentMessageSession(session) || openConversationId != conversationId) return;
                    messagesNextCursor = page.NextCursor;
                });
                if (reset && IsDisplayingConversation(session, conversationId))
                {
                    await dmClient.MarkReadAsync(session.Token, conversationId);
                    if (IsCurrentMessageSession(session) && !GroupChatClient.IsGroup(conversationId)) conversationsDirty = true;
                }
            }
            catch (Exception)
            {
                AepLog.Warning("[Messages] load failed.");
                if (generation == Volatile.Read(ref messagesRequestGeneration) &&
                    IsCurrentMessageSession(session))
                    messagesError = "Couldn't load messages.";
            }
            finally
            {
                if (generation == Volatile.Read(ref messagesRequestGeneration))
                {
                    messagesLoading = false;
                    messagesLoadingOlder = false;
                }
            }
        });
    }

    private void SendMessage(CharacterSession session, string conversationId, string plaintext,
        string? replyToGroupId = null)
    {
        var payload = replyToGroupId is null ? plaintext : EncodeRichDm(new RichDmContent(
            "message", Text: plaintext, ReplyTo: replyToGroupId));
        SendEncryptedDm(session, conversationId, payload, showInFeed: true,
            draftText: plaintext, draftReplyTo: replyToGroupId);
    }

    private void SendEncryptedDm(CharacterSession session, string conversationId, string plaintext,
        bool showInFeed, string? draftText = null, string? draftReplyTo = null)
    {
        if (GroupChatClient.IsGroup(conversationId)) { SendCloudGroupMessage(session, conversationId, plaintext, showInFeed, draftText, draftReplyTo); return; }
        if (!dmClient.IsKnownDirectConversation(session.Token, conversationId)) return;
        if (openConversationId == conversationId && IsOpenDmBlocked(session)) return;
        var members = openConversationId==conversationId ? openConversationMembers.ToArray() : conversations.FirstOrDefault(c=>c.ConversationId==conversationId)?.Members??[];
        var savedReply=messageReplyTarget;
        if(members.Length==0)return;
        Task Failure(string error,string? missing=null)=>Plugin.Framework.RunOnFrameworkThread(()=>{
            if(!IsCurrentMessageSession(session))return;
            RestoreDmDraft(conversationId,draftText,draftReplyTo,savedReply);
            if(openConversationId==conversationId){messagesError=error;if(missing is not null){messageMissingKeyName=missing;messageEncryptionReady=false;}}
        });
        _=Task.Run(async()=>{
            try {
                if(!await keyVault.EnsureIdentityRegisteredAsync(session.AccountId,session.Token)){await Failure("Your encryption key isn't registered yet. Please retry shortly.");return;}
                if(!IsCurrentMessageSession(session))return;
                var mine=await keyVault.EnsureIdentityAsync(session.AccountId,session.Token);
                var envelopes=new List<MessageEnvelope>();
                foreach(var member in members){
                    var other=await keyVault.GetOtherPartyKeyAsync(member.AccountId,session.Token);
                    if(!IsCurrentMessageSession(session))return;
                    if(other is null){await Failure($"{member.DisplayName} hasn't set up encryption yet — they need to sign in at least once.",member.DisplayName);return;}
                    var packet=DmCipher.Encrypt(mine,other,plaintext);
                    envelopes.Add(new(member.AccountId,Convert.ToBase64String(packet.Ciphertext),Convert.ToBase64String(packet.Nonce),Convert.ToBase64String(packet.Tag),Convert.ToBase64String(packet.CommitmentTag)));
                }
                var sent=await dmClient.SendMessageAsync(session.Token,conversationId,new([..envelopes]));
                if(sent is null){await Failure("Couldn't send that message.");return;}
                await Plugin.Framework.RunOnFrameworkThread(()=>{
                    if(!IsCurrentMessageSession(session))return;
                    decryptedCache[sent.Id]=plaintext;UpdateChatsPreview(conversationId,sent,plaintext);conversationsDirty=true;
                    if(openConversationId==conversationId && !openMessages.Any(m=>m.Id==sent.Id)){openMessages=[..openMessages,sent];if(showInFeed)threadScrollToBottom=true;}
                });
            } catch {AepLog.Warning("[Messages] send failed.");await Failure("Couldn't send that message.");}
        });
    }

    private void RestoreDmDraft(string conversationId, string? text, string? replyTo,MessageDto? savedReply=null)
    {
        if (text is null) return;
        if (openConversationId != conversationId) {
            if (!chatsHubDrafts.TryGetValue(conversationId,out var draft) || draft.Text.Length==0)
                chatsHubDrafts[conversationId]=(text,savedReply);
            return;
        }
        if (messageComposerInput.Length>0) return;
        messageComposerInput = text;
        if (replyTo is not null)
            messageReplyTarget = openMessages.FirstOrDefault(message => message.GroupId == replyTo);
    }

    // Which account's public key pairs with this specific message row - see DmMessage's doc
    // comment on why a message the viewer sent is decrypted using the RECIPIENT's key (their own
    // pairwise shared secret with that member), while a received message uses the SENDER's key.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,byte> dmPlaintextReloads=new();
    private readonly SemaphoreSlim dmPlaintextWorkers=new(2,2);
    private void QueueVisibleDmPlaintext(CharacterSession session,MessageDto message)
    {
        if(dmPlaintextReloads.Count>=16||!dmPlaintextReloads.TryAdd(message.Id,0))return;
        StartGroupImageWork(async cancellation=>{
            var entered=false;
            try {
                await dmPlaintextWorkers.WaitAsync(cancellation);entered=true;
                if(!IsCurrentMessageSession(session))return;
                var identity=GroupChatClient.IsMessage(message)?null:await keyVault.EnsureIdentityAsync(session.AccountId,session.Token);
                await DecryptAndCacheAsync(session,message,identity);
            }catch(OperationCanceledException)when(cancellation.IsCancellationRequested){}
            catch {if(IsCurrentMessageSession(session))decryptedCache[message.Id]=DecryptFailedMarker;}
            finally {if(entered)dmPlaintextWorkers.Release();dmPlaintextReloads.TryRemove(message.Id,out _);}
        });
    }
    private async Task DecryptAndCacheAsync(CharacterSession session, MessageDto message, DmIdentity? myIdentity)
    {
        if (decryptedCache.TryGetValue(message.Id, out var cached) && cached != DecryptFailedMarker)
        {
            return;
        }
        if (GroupChatClient.IsMessage(message))
        {
            var plaintext = await dmClient.Groups.DecryptAsync(session.Token, message);
            var conversation = message.Id[..message.Id.LastIndexOf(':')];
            if (IsCurrentMessageSession(session) && dmClient.Groups.IsCurrentAccount(session.AccountId, session.Token) &&
                dmClient.Groups.State.Groups.Any(g => GroupChatClient.Prefix + g.Id == conversation)) decryptedCache[message.Id] = plaintext;
            return;
        }

        if (!DmClient.ValidEnvelope(message.Ciphertext,message.Nonce,message.Tag) ||
            message.SenderAccountId!=session.AccountId && message.RecipientAccountId!=session.AccountId) {
            decryptedCache[message.Id]=DecryptFailedMarker; return;
        }
        var otherAccountId = message.SenderAccountId == session.AccountId ? message.RecipientAccountId : message.SenderAccountId;
        var otherKey = await keyVault.GetOtherPartyKeyAsync(otherAccountId, session.Token);
        if (!IsCurrentMessageSession(session)) return;
        if (otherKey is null)
        {
            decryptedCache[message.Id] = DecryptFailedMarker;
            return;
        }

        var opened = DmCipher.Decrypt(myIdentity!, otherKey,
            Convert.FromBase64String(message.Ciphertext), Convert.FromBase64String(message.Nonce), Convert.FromBase64String(message.Tag));
        decryptedCache[message.Id] = opened is not null ? opened.Plaintext : DecryptFailedMarker;
    }

    private void ReportMessage(CharacterSession session, MessageDto message, string revealedPlaintext)
    {
        if (GroupChatClient.IsMessage(message)) { RunCloudGroupAction(session, async () => { await dmClient.Groups.ReportAsync(session.Token, message); }); return; }
        if (GroupChatClient.ImageContent(revealedPlaintext) is { Image.Direct: true } imageContent)
        {
            RunCloudGroupAction(session, async () => {
                await dmClient.Groups.ReportDirectImageAsync(session.Token, imageContent.Image.GroupId, message, revealedPlaintext);
            });
            return;
        }
        // Never submit an attachment descriptor (and its image key) to the
        // legacy plaintext report endpoint, even if it cannot be parsed.
        if (revealedPlaintext.StartsWith("AC-IMAGE-1:", StringComparison.Ordinal)) {
            messagesError = "This image report could not be opened. Please retry from the image menu.";
            return;
        }
        if (revealedPlaintext is "Couldn't decrypt — key missing" or "…" or "")
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var myIdentity = await keyVault.EnsureIdentityAsync(session.AccountId, session.Token);
            var otherAccountId = message.SenderAccountId == session.AccountId ? message.RecipientAccountId : message.SenderAccountId;
            var otherKey = await keyVault.GetOtherPartyKeyAsync(otherAccountId, session.Token);
            if (otherKey is null)
            {
                return;
            }

            var opened = DmCipher.Decrypt(myIdentity, otherKey,
                Convert.FromBase64String(message.Ciphertext), Convert.FromBase64String(message.Nonce), Convert.FromBase64String(message.Tag));
            if (opened is null)
            {
                return;
            }

            await reportClient.SubmitAsync(session.Token, "harassment", null, message.SenderAccountId, message.Id,
                revealedPlaintext, Convert.ToBase64String(opened.FrankingKey));
        });
    }
}

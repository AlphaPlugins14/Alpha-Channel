using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using System.Collections.Concurrent;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Friends Channel: friend list (with live online status), incoming/outgoing requests, add-by-
// handle. REST-backed (FriendsClient) with live refresh triggered by StreamClient's
// OnFriendRequestReceived/OnFriendAccepted/OnFriendRemoved pushes (wired in MainWindow's
// constructor) rather than polling - see AlphaChannel.Contracts.SocialSignalType's own note on why
// those pushes exist.
internal sealed partial class MainWindow
{
    private static bool CanJoinFriendWatchParty(FriendDto friend) =>
        FriendWatchPartyStatus.CanJoin(friend.Online, friend.HostingJoinableWatchParty, friend.WatchingLabel);

    private bool friendsDirty = true;
    private bool friendsLoading;
    private int friendsRefreshGeneration;
    private FriendDto[] friends = [];
    private string? friendsAccountId;
    // Live global count from presence.onlineCount — not friends, every AlphaChannel /rt client.
    // Displayed total adds recent guest sessions to this signed-in realtime count.
    private FriendRequestsPage friendRequests = new([], []);

    private int FriendsSidebarNotificationCount()
    {
        if (CurrentSession is not { } session) return 0;
        long count = friendsAccountId == session.AccountId ? friendRequests.Incoming.Length : 0;
        return (int)Math.Min(int.MaxValue, count);
    }
    private string? friendsError;
    private bool friendsMessageIsSuccess;
    private AccountSummaryDto[] blockedAccounts = [];
    private string inviteCodeInput = string.Empty;
    private bool inviteCodeRedeeming;
    private string? inviteCodeError;

    // Live search-as-you-type, replacing a type-the-full-name-then-Send box. friendSearchGeneration
    // discards a stale response that lands after a newer keystroke already fired a fresher search -
    // same race the old exact-search box never had to worry about since it only ever fired once per
    // button click.
    private string friendSearchInput = string.Empty;
    private string friendSearchQuery = string.Empty;
    private long friendSearchGeneration;
    private bool friendSearchLoading;
    private FriendSearchResultDto[] friendSearchResults = [];
    private readonly HashSet<string> friendSearchSendingIds = [];
    private readonly ConcurrentDictionary<(string Token, string UserId), byte> partyFriendRequestsSending = new();

    private async Task SendPartyFriendRequestAsync(CharacterSession session, string userId, string displayName)
    {
        var token = session.Token;
        var key = (token, userId);
        if (!partyFriendRequestsSending.TryAdd(key, 0))
            return;

        try
        {
            var sent = await friendsClient.SendRequestAsync(token, displayName).ConfigureAwait(false);
            var requests = sent
                ? await friendsClient.GetRequestsAsync(token).ConfigureAwait(false)
                : null;
            if (Volatile.Read(ref disposeStarted) != 0)
                return;

            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0 ||
                    !ReferenceEquals(CurrentSession, session) || session.Token != token)
                    return;

                friendsDirty = true;
                if (requests is not null && friendsAccountId == session.AccountId)
                    friendRequests = requests;
                if (!sent)
                    Plugin.ChatGui.Print($"[AlphaChannel] Couldn't send a friend request to {displayName}. You may already be friends or have a pending request.");
            });
        }
        catch (Exception)
        {
            AepLog.Warning("[Friends] Watch Party friend request failed.");
        }
        finally
        {
            partyFriendRequestsSending.TryRemove(key, out _);
        }
    }

    private string friendListSearchInput =
      string.Empty;

    //
    // 0 = All
    // 1 = Online
    // 2 = In Watch Party
    //
    private int friendListStatusFilter;

    private string? friendsLoadError;
    private enum FriendGroupKeyState { Ready, Missing, Error }
    private bool friendGroupSelectionMode;
    private string friendGroupNameInput = string.Empty;
    private string? friendGroupError;
    private readonly HashSet<string> friendGroupSelectedIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FriendGroupKeyState> friendGroupKeyStates = new();
    private readonly ConcurrentDictionary<string, long> friendGroupKeyChecks = new();
    private long friendGroupKeyGeneration;
    private DateTime friendGroupNextKeyCheckUtc = DateTime.MinValue;
    private string? friendGroupAccountId;
    private string? friendGroupSessionToken;
    private Task<string?>? friendGroupCreationTask;
    private FriendDto[] friendGroupSubmittedFriends = [];
    private string friendGroupSubmittedName = string.Empty;

    // Called from Plugin.cs's right-click "Add Friend" context-menu handler - surfaces the result
    // the same way the in-page "Add a friend" flow does (friendsError + a refreshed request list),
    // and jumps straight to Friends so the outcome is actually visible instead of silent.
    internal void HandleAddFriendByCharacterResult(
        FriendsClient.FriendRequestOutcome outcome, string characterName)
    {
        friendsDirty = true;
        friendsMessageIsSuccess = outcome == FriendsClient.FriendRequestOutcome.Sent;
        friendsError = outcome switch
        {
            FriendsClient.FriendRequestOutcome.Sent => $"Friend request sent to {characterName}.",
            FriendsClient.FriendRequestOutcome.AlreadyFriends => $"You and {characterName} are already Alpha Channel friends.",
            FriendsClient.FriendRequestOutcome.AlreadyPending => $"A friend request with {characterName} is already pending.",
            FriendsClient.FriendRequestOutcome.NotFound => $"Couldn't find an Alpha Channel account linked to {characterName}.",
            _ => $"Couldn't send a friend request to {characterName}.",
        };
        currentPage = HomePage.Friends;
        SetMinimized(false);
        IsOpen = true;
    }

    private void DrawFriends()
    {
        if (CurrentSession is not { } session)
        {
            DrawPlainEmpty(
                "Sign in to see your friends.",
                "Open Settings",
                () => currentPage = HomePage.Settings);

            return;
        }

        if (friendGroupSelectionMode &&
            (friendGroupAccountId != session.AccountId || friendGroupSessionToken != session.Token))
            EndFriendGroupSelection();
        CompleteFriendGroupCreation(session);

        if (friendsDirty && !friendsLoading)
        {
            RefreshFriends(session.Token);
        }

        if (friendsClient.LastAccessDeniedReason is { } deniedReason)
        {
            ImGui.TextColored(
                Danger,
                deniedReason switch
                {
                    "lalafell_pending" =>
                        "Your account is pending review before Lalafell accounts can use Friends. Check back soon.",

                    "lalafell_denied" =>
                        "Social features aren't available for this account.",

                    _ =>
                        "Friends isn't available for this account right now.",
                });

            return;
        }

        // ---------------------------------------------------------
        // Display-name warning
        // ---------------------------------------------------------

        if (session.DisplayName == session.Handle)
        {
            ImGui.TextColored(
                Danger,
                "Pick a username in Settings so friends can find you.");

            ImGui.SameLine();

            if (ImGui.SmallButton("Open Settings"))
            {
                currentPage = HomePage.Settings;
            }

            ImGui.Dummy(UiVec(0f, 10f));
        }

        DrawModernFriendsPage(session);
    }

    private void DrawFriendGroupSelectionCard(FriendDto friend, float width)
    {
        var hasStatus = friendGroupKeyStates.TryGetValue(friend.AccountId, out var keyState);
        var ready = hasStatus && keyState == FriendGroupKeyState.Ready;
        var selected = friendGroupSelectedIds.Contains(friend.AccountId);
        var height = Ui(78f);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(8f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildBorderSize, Ui(selected ? 2f : 1f)))
        using (ImRaii.PushColor(ImGuiCol.Border, selected
                   ? Accent : new Vector4(Accent.X, Accent.Y, Accent.Z, 0.22f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg))
        using (var row = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##friendGroupRow", new Vector2(width, height), true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (!row)
                return;

            var origin = ImGui.GetWindowPos();
            var hovered = ImGui.IsWindowHovered();
            var draw = ImGui.GetWindowDrawList();
            if (ready && hovered && !selected)
                draw.AddRectFilled(origin + UiVec(1f, 1f),
                    origin + new Vector2(width, height) - UiVec(1f, 1f),
                    ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.08f)), Ui(8f));

            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha,
                       selected || ready && hovered ? 1f : ready ? 0.65f : 0.48f))
            {
                ImGui.SetCursorScreenPos(origin + UiVec(12f, 16f));
                DrawAvatarChip(friend.AvatarIcon, friend.AvatarColorHex, Ui(40f), friend.AvatarImageUrl);
                ImGui.SetCursorScreenPos(origin + UiVec(64f, 10f));
                DrawUsername(friend.AccountId,TruncateToWidth(friend.DisplayName, width - Ui(115f)),Vector4.One);
                ImGui.SetCursorScreenPos(origin + UiVec(64f, 30f));
                using (UiFonts.PushRelative(.90f)) ImGui.TextColored(friend.Online ? Good : MutedText,
                    friend.Online ? "● Online" : "● Offline");
            }

            if (!ready)
            {
                var warning = !hasStatus ? "Checking group chat access…" :
                    keyState == FriendGroupKeyState.Error ? "Couldn't check access — retrying" : "Needs to open the updated plugin";
                ImGui.SetCursorScreenPos(origin + UiVec(64f, 50f));
                using (UiFonts.PushRelative(.85f)) ImGui.TextColored(keyState == FriendGroupKeyState.Missing && hasStatus
                        ? new Vector4(1f, 0.72f, 0.37f, 1f) : MutedText, FitPartyTextToWidth(warning,width-Ui(80)));
                if (hasStatus && keyState == FriendGroupKeyState.Missing && ImGui.IsItemHovered())
                    UiFonts.SetTooltip("Ask your friend to open the updated plugin so their group encryption identity is registered.");
            }
            else
            {
                if (selected)
                {
                    var center = origin + new Vector2(width - Ui(28f), height * 0.5f);
                    draw.AddCircleFilled(center, Ui(16f), ImGui.GetColorU32(Accent));
                    UiFonts.DrawIcon(draw, ImGui.GetFontSize(), center - UiVec(6f, 7f), ImGui.GetColorU32(Vector4.One), FontAwesomeIcon.Check.ToIconString());
                }
                if (hovered && friendGroupCreationTask is null)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        if (!friendGroupSelectedIds.Add(friend.AccountId))
                            friendGroupSelectedIds.Remove(friend.AccountId);
                    }
                }
            }
        }
    }

    private void BeginFriendGroupSelection(CharacterSession session)
    {
        friendGroupSelectionMode = true;
        friendGroupAccountId = session.AccountId;
        friendGroupSessionToken = session.Token;
        friendGroupNameInput = string.Empty;
        friendGroupError = null;
        friendGroupSelectedIds.Clear();
        friendGroupKeyStates.Clear();
        friendGroupKeyChecks.Clear();
        Interlocked.Increment(ref friendGroupKeyGeneration);
        friendGroupNextKeyCheckUtc = DateTime.MinValue;
    }

    private void EndFriendGroupSelection()
    {
        friendGroupSelectionMode = false;
        friendGroupAccountId = null;
        friendGroupSessionToken = null;
        friendGroupNameInput = string.Empty;
        friendGroupError = null;
        friendGroupSelectedIds.Clear();
        friendGroupKeyStates.Clear();
        friendGroupKeyChecks.Clear();
        friendGroupCreationTask = null;
        Interlocked.Increment(ref friendGroupKeyGeneration);
    }

    private void RefreshFriendGroupKeys(CharacterSession session)
    {
        if (!friendGroupSelectionMode || DateTime.UtcNow < friendGroupNextKeyCheckUtc)
            return;

        friendGroupNextKeyCheckUtc = DateTime.UtcNow.AddMinutes(1);
        var generation = Interlocked.Read(ref friendGroupKeyGeneration);
        var token = session.Token;
        foreach (var friend in friends)
        {
            var accountId = friend.AccountId;
            if (!friendGroupKeyChecks.TryAdd(accountId, generation))
                continue;

            _ = Task.Run(async () =>
            {
                try
                {
                    bool? available = await dmClient.Groups.HasIdentityAsync(token, accountId);
                    if (Interlocked.Read(ref friendGroupKeyGeneration) == generation)
                        friendGroupKeyStates[accountId] = available switch
                        {
                            true => FriendGroupKeyState.Ready,
                            false => FriendGroupKeyState.Missing,
                            null => FriendGroupKeyState.Error,
                        };
                }
                catch (Exception)
                {
                    AepLog.Warning("[Messages] Friend encryption check failed.");
                    if (Interlocked.Read(ref friendGroupKeyGeneration) == generation)
                        friendGroupKeyStates[accountId] = FriendGroupKeyState.Error;
                }
                finally
                {
                    if (friendGroupKeyChecks.TryGetValue(accountId, out var active) && active == generation)
                        friendGroupKeyChecks.TryRemove(accountId, out _);
                }
            });
        }
    }

    private void CompleteFriendGroupCreation(CharacterSession session)
    {
        if (friendGroupCreationTask is not { IsCompleted: true } task)
            return;
        friendGroupCreationTask = null;

        string? conversationId;
        try
        {
            conversationId = task.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            AepLog.Warning("[Messages] Group creation failed.");
            conversationId = null;
        }

        if (conversationId is null)
        {
            friendGroupError = "Couldn't create the group. Your selection is still here; please try again.";
            return;
        }

        OpenCreatedGroupConversation(session, conversationId, friendGroupSubmittedName,
            friendGroupSubmittedFriends);
        if (Auth.GroupChatClient.IsGroup(conversationId) && dmClient.Groups.LastOperationWarning is { } warning) messagesError = warning;
        EndFriendGroupSelection();
    }

    private void DrawFriendGroupSelectionBar(CharacterSession session)
    {
        RefreshFriendGroupKeys(session);
        friendGroupSelectedIds.RemoveWhere(id =>
            !friends.Any(friend => friend.AccountId == id) ||
            friendGroupKeyStates.TryGetValue(id, out var state) && state != FriendGroupKeyState.Ready);

        var compact = ImGui.GetContentRegionAvail().X < Ui(760f);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(8f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg))
        using (var bar = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##friendGroupSelectionBar", UiVec(-1f, compact ? 93f : 55f),
                   false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (bar)
            {
                ImGui.SetCursorPos(ImGui.GetCursorPos() + UiVec(10f, 10f));
                ImGui.SetNextItemWidth(compact ? Ui(220f) : Ui(250f));
                ImGui.InputTextWithHint("##friendGroupName", "Group name", ref friendGroupNameInput, 48);
                ImGui.SameLine(0f, Ui(12f));
                ImGui.TextColored(AccentHover, friendGroupSelectedIds.Count < 2
                    ? $"{friendGroupSelectedIds.Count} friends selected · select at least 2"
                    : $"{friendGroupSelectedIds.Count} friends selected");
                if (compact)
                    ImGui.SetCursorPosX(Ui(10f));
                else
                    ImGui.SameLine(0f, Ui(14f));

                using (UiDisabled.Scope(friendGroupCreationTask is not null))
                {
                    if (ImGui.Button("Cancel##friendGroup", UiVec(80f, 32f)))
                        EndFriendGroupSelection();
                }
                ImGui.SameLine(0f, Ui(8f));
                using (UiDisabled.Scope(friendGroupCreationTask is not null ||
                           friendGroupSelectedIds.Count < 2 ||
                           string.IsNullOrWhiteSpace(friendGroupNameInput)))
                using (ImRaii.PushColor(ImGuiCol.Button, Accent))
                {
                    if (ImGui.Button(friendGroupCreationTask is null
                            ? "Create group##friendGroupConfirm" : "Creating...##friendGroupConfirm",
                            UiVec(126f, 32f)))
                    {
                        friendGroupSubmittedFriends = friends
                            .Where(friend => friendGroupSelectedIds.Contains(friend.AccountId))
                            .ToArray();
                        friendGroupSubmittedName = friendGroupNameInput.Trim();
                        friendGroupError = null;
                        friendGroupCreationTask = dmClient.CreateConversationAsync(session.Token,
                            friendGroupSubmittedFriends.Select(friend => friend.AccountId).ToArray(),
                            friendGroupSubmittedName);
                    }
                }
            }
        }
        if (friendGroupError is { Length: > 0 } error)
            ImGui.TextColored(Danger, error);
        ImGui.Dummy(UiVec(0f, 8f));
    }

    private void RequestFriendSearch(CharacterSession session, string query)
    {
        var trimmed = query.Trim();
        if (string.Equals(trimmed, friendSearchQuery, StringComparison.Ordinal))
        {
            return;
        }

        friendsAddNotice=null;
        friendSearchQuery = trimmed;
        var ticket = Interlocked.Increment(ref friendSearchGeneration);
        if (trimmed.Length < DisplayNameRules.MinLength)
        {
            friendSearchResults = [];
            friendSearchLoading = false;
            return;
        }

        friendSearchLoading = true;
        var token = session.Token;
        _ = Task.Run(async () =>
        {
            FriendSearchResultDto[]? results=null;
            try
            {
                await Task.Delay(300);
                if (Interlocked.Read(ref friendSearchGeneration)!=ticket || !ReferenceEquals(CurrentSession,session)) return;
                results=await friendsClient.SearchAsync(token,trimmed);
            }
            catch (Exception) { AepLog.Warning("[Friends] Search failed."); }
            await Plugin.Framework.RunOnFrameworkThread(()=>
            {
                if (Interlocked.Read(ref friendSearchGeneration)!=ticket || !ReferenceEquals(CurrentSession,session)) return;
                friendSearchResults=results??[];
                friendSearchLoading=false;
                if (results is null) { friendsAddNoticeSuccess=false; friendsAddNotice="Couldn't search right now. Please try again."; }
            });
        });
    }

    // Apply realtime presence locally instead of fetching the full friends list.
    private void ApplyPresenceUpdate(SocialControl update)
    {
        if (update.AccountId is not { Length: > 0 } accountId) return;
        for (var index=0;index<friends.Length;index++)
        {
            if (friends[index].AccountId!=accountId) continue;
            friends[index]=friends[index] with
            {
                Online=update.Online??false,
                WatchingLabel=update.WatchingLabel,
                HostingJoinableWatchParty=update.HostingJoinableWatchParty??false
            };
            break;
        }
    }

    private void RefreshFriends(
    string bearerToken)
    {
        var loadingSession = CurrentSession;
        var loadingAccountId = loadingSession?.AccountId;
        var refreshGeneration = ++friendsRefreshGeneration;
        friendsDirty =
            false;

        friendsLoading =
            true;

        friendsLoadError =
            null;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var friendsTask =
                        friendsClient.GetFriendsAsync(
                            bearerToken);

                    var requestsTask =
                        friendsClient.GetRequestsAsync(
                            bearerToken);

                    var blocksTask =
                        friendsClient.GetBlocksAsync(
                            bearerToken);

                    // Redeeming our code rotates it on the server before the
                    // FriendAccepted push. Refresh it along with the friends list.
                    var accountTask = authClient.GetMeAsync(bearerToken);

                    await Task.WhenAll(
                        friendsTask,
                        requestsTask,
                        blocksTask,
                        accountTask);

                    var account = await accountTask;
                    if (account is not null && Volatile.Read(ref disposeStarted) == 0)
                    {
                        await Plugin.Framework.RunOnFrameworkThread(() =>
                        {
                            if (Volatile.Read(ref disposeStarted) != 0 || refreshGeneration != friendsRefreshGeneration || loadingSession is null ||
                                !ReferenceEquals(CurrentSession, loadingSession) || loadingSession.Token != bearerToken ||
                                string.IsNullOrWhiteSpace(account.InviteCode) || loadingSession.InviteCode == account.InviteCode)
                                return;
                            loadingSession.InviteCode = account.InviteCode;
                            onSessionChanged(loadingSession);
                        });
                    }

                    var loadedFriends =
                        await friendsTask;

                    var loadedRequests =
                        await requestsTask;

                    var loadedBlocks =
                        await blocksTask;

                    if (loadedFriends is null ||
                        loadedRequests is null ||
                        loadedBlocks is null)
                    {
                        await Plugin.Framework.RunOnFrameworkThread(() =>
                        {
                            if (Volatile.Read(ref disposeStarted) == 0 && refreshGeneration == friendsRefreshGeneration && ReferenceEquals(CurrentSession, loadingSession) &&
                                friendsClient.LastAccessDeniedReason is null)
                                friendsLoadError = "Friends could not be loaded.";
                        });

                        return;
                    }

                    await Plugin.Framework.RunOnFrameworkThread(() =>
                    {
                        if (Volatile.Read(ref disposeStarted) != 0 || refreshGeneration != friendsRefreshGeneration || loadingSession is null ||
                            !ReferenceEquals(CurrentSession, loadingSession) || loadingSession.Token != bearerToken) return;
                        friends = loadedFriends;
                        friendsAccountId = loadingAccountId;
                        friendRequests = loadedRequests;
                        blockedAccounts = loadedBlocks;
                        if (loadingAccountId is not null)
                        {
                            ObservePatreonFriends(loadingAccountId, loadedFriends.Select(friend => friend.AccountId));
                            ConfirmRefreshedBlocks(loadingAccountId, loadedBlocks);
                        }
                    });
                }
                catch (Exception)
                {
                    if (Volatile.Read(ref disposeStarted) == 0)
                        await Plugin.Framework.RunOnFrameworkThread(() =>
                        {
                            if (Volatile.Read(ref disposeStarted) == 0 && refreshGeneration == friendsRefreshGeneration && ReferenceEquals(CurrentSession, loadingSession))
                                friendsLoadError = "Friends could not be loaded.";
                        });

                    AepLog.Warning(
                        "[Friends] Failed to refresh.");
                }
                finally
                {
                    if (Volatile.Read(ref disposeStarted) == 0)
                        await Plugin.Framework.RunOnFrameworkThread(() =>
                        {
                            if (Volatile.Read(ref disposeStarted) == 0 && refreshGeneration == friendsRefreshGeneration)
                            {
                                friendsLoading = false;
                                if (!ReferenceEquals(CurrentSession, loadingSession)) friendsDirty = true;
                            }
                        });
                }
            });
    }
}

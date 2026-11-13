using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Profile data loading and shared profile controls.
internal sealed partial class MainWindow
{
    private HomePage profileReturnPage = HomePage.Friends;
    private bool profilePageOpen;
    private string? profilePageAccountId;
    private string? profilePageFallbackName;
    private AccountProfileDto? profilePageData;
    private readonly ProfileBadgeWorkerClient profileBadgeWorker = new();
    private IReadOnlySet<string> profilePageUnlockedBadgeIds = new HashSet<string>(StringComparer.Ordinal);
    private bool profilePageLoading;
    private bool profilePageNotAvailable;
    private bool ProfileIsNonFriend => profilePageAccountId is { } id && CurrentSession is { } viewer &&
        id != viewer.AccountId && profilePageData?.FriendsSinceUnix is null &&
        !(friendsAccountId == viewer.AccountId && friends.Any(f => f.AccountId == id));

    private bool profileDetailsLoading;
    private RoomDirectoryDto? profilePageRoom;
    private ActivityEventDto[] profilePageRecentActivity = [];
    private long profilePageLoadGeneration;

    private ProfileFriendConfirmationAction profileFriendConfirmation;
    private bool profileFriendConfirmationPendingOpen;
    private bool profileFriendActionBusy;

    private enum ProfileFriendConfirmationAction
    {
        None,
        RemoveFriend,
        BlockUser,
    }

    private void OpenProfilePage(
          CharacterSession session,
      string accountId,
      string fallbackDisplayName)
    {
        if (!AllowInlineProfileNavigation() || AlphaChatBlocked(accountId)) return;
        if (currentPage != HomePage.Profile)
            profileReturnPage = currentPage;
        currentPage = HomePage.Profile;
        chatsCreationOpen = false;
        // Native context-menu actions run on Framework.Update, outside an
        // ImGui frame. Reset the page body only once it is being drawn.
        navigationScroll.RequestReset();

        var loadGeneration =
            Interlocked.Increment(
                ref profilePageLoadGeneration);

        profilePageAccountId =
            accountId;

        profilePageFallbackName =
            fallbackDisplayName;

        profileHistoryTab = false;
        profileSocialsTab = false;
        socialsPageGeneration++; socialsTarget=null; socialsViewer=null; socialsPage=null;
        socialEditorOpen=false;
        profileFriendsTab = false;
        ResetProfileFriends();
        profileHistoryGeneration++;
        profileHistoryTarget = null;
        profileHistoryViewer = null;
        profileHistoryPage = null;
        profileHistoryError = null;
        profileHistoryLoading = false;
        nextProfileHistoryRefresh = DateTime.MinValue;
        profilePageData =
            null;
        viewedProfileTheme = null;
        viewedProfileThemeAccount = null;
        nextProfileThemeRefresh = DateTime.MinValue;
        profilePageUnlockedBadgeIds = new HashSet<string>(StringComparer.Ordinal);

        profilePageNotAvailable =
            false;

        profilePageLoading =
            true;

        profileDetailsLoading =
            true;

        profilePageRoom =
            null;

        profilePageRecentActivity =
      [];

        profileFriendConfirmation =
            ProfileFriendConfirmationAction.None;

        profileFriendConfirmationPendingOpen =
            false;

        profileFriendActionBusy =
            false;

        profilePageOpen =
            true;

        var token =
            session.Token;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var profile =
                        await authClient
                            .GetProfileAsync(
                                token,
                                accountId)
                            .ConfigureAwait(false);

                    AccountProfileDto? directoryProfile = null;
                    try
                    {
                        directoryProfile = await CharacterDirectory.GetPublicProfileAsync(token, accountId, youtubeFeedToken).ConfigureAwait(false);
                    }
                    catch (Exception) { /* Directory outages must not break existing profiles. */ }
                    if (profile is null && directoryProfile is null && !AlphaChatBlocked(accountId))
                    {
                        try { directoryProfile = await friendsDirectoryApi.Profile(token, accountId, youtubeFeedToken).ConfigureAwait(false); }
                        catch (Exception) { /* Optional discovery profiles must not block the existing fallback. */ }
                    }
                    if (profile is not null)
                        profile = profile with { CharacterName = directoryProfile?.CharacterName,
                            CharacterWorld = directoryProfile?.CharacterWorld };
                    else if (!AlphaChatBlocked(accountId))
                        profile = directoryProfile;
                    if (profile is null && !AlphaChatBlocked(accountId))
                    {
                        var matches = await friendsClient.SearchAsync(token,fallbackDisplayName).ConfigureAwait(false);
                        var match = matches?.FirstOrDefault(p => p.AccountId == accountId);
                        profile = new AccountProfileDto(accountId,string.Empty,match?.DisplayName ?? fallbackDisplayName,
                            match?.AvatarIcon,match?.AvatarColorHex ?? "#9966FA",null,null,null,match?.AvatarImageUrl);
                    }

                    if (!IsCurrentProfileLoad(
                            loadGeneration))
                    {
                        return;
                    }

                    profilePageData =
                        profile;

                    profilePageNotAvailable =
                        profile is null;

                    if (profile is not null)
                        _ = LoadProfileBadgesAsync(session, accountId, loadGeneration);

                    await LoadProfileDetailsAsync(
                            token,
                            accountId,
                            loadGeneration)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (IsCurrentProfileLoad(
                            loadGeneration))
                    {
                        profilePageNotAvailable =
                            true;
                    }

                    AepLog.Warning(
                        "[Profile] Loading failed.");
                }
                finally
                {
                    if (IsCurrentProfileLoad(
                            loadGeneration))
                    {
                        profilePageLoading =
                            false;

                        profileDetailsLoading =
                            false;
                    }
                }
            });
    }

    private async Task LoadProfileBadgesAsync(CharacterSession session, string accountId, long loadGeneration)
    {
        try
        {
            var unlocked = await profileBadgeWorker.GetUnlockedAsync(session.Token, accountId, youtubeFeedToken,publicProfile:ProfileIsNonFriend)
                .ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (IsCurrentProfileLoad(loadGeneration) && CurrentSession?.AccountId == session.AccountId)
                    profilePageUnlockedBadgeIds = unlocked;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (youtubeFeedToken.IsCancellationRequested) { }
        catch (Exception)
        {
            // Badge outages must not prevent the profile/party/activity loading.
            if (IsCurrentProfileLoad(loadGeneration))
                AepLog.Warning("[Profile] Badge lookup failed; showing locked badges.");
        }
    }

    private async Task LoadProfileDetailsAsync(
     string bearerToken,
     string accountId,
     long loadGeneration)
    {
        if (ProfileIsNonFriend) return;
        RoomDirectoryDto? loadedRoom =
            null;

        ActivityEventDto[] loadedActivity =
            [];

        try
        {
            var rooms =
                await roomsClient
                    .ListAsync(
                        bearerToken)
                    .ConfigureAwait(false);

            if (!IsCurrentProfileLoad(
                    loadGeneration))
            {
                return;
            }

            loadedRoom =
                rooms.FirstOrDefault(
                    room =>
                        string.Equals(
                            room.HostAccountId,
                            accountId,
                            StringComparison.Ordinal));
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[Profile] Room lookup failed.");
        }

        if (!IsCurrentProfileLoad(
                loadGeneration))
        {
            return;
        }

        try
        {
            const int maximumInspected =
                100;

            const int maximumDisplayed =
                1;

            var matchingEvents =
                new List<ActivityEventDto>();

            var inspected =
                0;

            long? before =
                null;

            while (inspected <
                   maximumInspected)
            {
                var page =
                    await activityClient
                        .GetFeedAsync(
                            bearerToken,
                            before)
                        .ConfigureAwait(false);

                if (!IsCurrentProfileLoad(
                        loadGeneration))
                {
                    return;
                }

                if (page is null ||
                    page.Items.Length == 0)
                {
                    break;
                }

                var remaining =
                    maximumInspected -
                    inspected;

                foreach (var item in
                         page.Items.Take(
                             remaining))
                {
                    if (IsRemovedPostActivity(item) ||
                        string.Equals(item.Type, "FriendAccepted", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!string.Equals(
                            item.ActorAccountId,
                            accountId,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    matchingEvents.Add(
                        item);

                    if (matchingEvents.Count >=
                        maximumDisplayed)
                    {
                        break;
                    }
                }

                inspected +=
                    Math.Min(
                        page.Items.Length,
                        remaining);

                if (matchingEvents.Count >=
                        maximumDisplayed ||
                    string.IsNullOrWhiteSpace(
                        page.NextCursor) ||
                    !long.TryParse(
                        page.NextCursor,
                        out var nextCursor))
                {
                    break;
                }

                before =
                    nextCursor;
            }

            loadedActivity =
                matchingEvents
                    .OrderByDescending(
                        item =>
                            item.CreatedAtUnix)
                    .Take(
                        maximumDisplayed)
                    .ToArray();
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[Profile] Activity lookup failed.");
        }

        if (!IsCurrentProfileLoad(
                loadGeneration))
        {
            return;
        }

        //
        // Commit the supplemental results together only while this is
        // still the same open profile request.
        //

        profilePageRoom =
            loadedRoom;

        profilePageRecentActivity =
            loadedActivity;
    }

    private bool IsCurrentProfileLoad(
    long loadGeneration)
    {
        return
            Volatile.Read(ref disposeStarted) == 0 && profilePageOpen && currentPage == HomePage.Profile &&
            Volatile.Read(
                ref profilePageLoadGeneration) ==
            loadGeneration;
    }

    private void CloseProfilePage()
    {
        if (!AllowInlineProfileNavigation()) return;
        if (currentPage == HomePage.Profile)
            currentPage = profileReturnPage;
        profileFriendConfirmation = ProfileFriendConfirmationAction.None;
        profileFriendConfirmationPendingOpen = false;
        profilePageOpen =
            false;

        //
        // Invalidate every outstanding profile fetch. The requests may
        // finish in the background, but their results will be discarded.
        //

        Interlocked.Increment(
            ref profilePageLoadGeneration);
    }

    private void DrawProfileFriendConfirmation(
    Vector2 profilePosition,
    float profileWidth,
    float profileHeight)
    {
        if (profileFriendConfirmationPendingOpen)
        {
            ImGui.OpenPopup(
                "Confirm action##profileFriendConfirmation");

            profileFriendConfirmationPendingOpen =
                false;
        }

        if (profileFriendConfirmation ==
            ProfileFriendConfirmationAction.None)
        {
            return;
        }

        //
        // Darken the profile itself without dimming the rest of the game.
        //

        ImGui.GetWindowDrawList()
            .AddRectFilled(
                profilePosition,
                profilePosition +
                new Vector2(
                    profileWidth,
                    profileHeight),
                ImGui.GetColorU32(
                    new Vector4(
                        0f,
                        0f,
                        0f,
                        0.52f)),
                14f);

        var blocking =
            profileFriendConfirmation ==
            ProfileFriendConfirmationAction.BlockUser;

        var confirmationSize =
            blocking
                ? UiVec(480f, 255f)
                : UiVec(430f, 205f);

        ImGui.SetNextWindowPos(
            profilePosition +
            new Vector2(
                profileWidth * 0.5f,
                profileHeight * 0.5f),
            ImGuiCond.Always,
            new Vector2(
                0.5f,
                0.5f));

        ImGui.SetNextWindowSize(
            confirmationSize,
            ImGuiCond.Always);

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoDocking;

        using var popupStyle =
            ImRaii.PushStyle(
                    ImGuiStyleVar.PopupRounding,
                    12f)
                .Push(
                    ImGuiStyleVar.PopupBorderSize,
                    1f)
                .Push(
                    ImGuiStyleVar.WindowPadding,
                    UiVec(22f, 20f))
                .Push(
                    ImGuiStyleVar.FrameRounding,
                    8f);

        using var popupColors =
            ImRaii.PushColor(
                    ImGuiCol.PopupBg,
                    CardBg)
                .Push(
                    ImGuiCol.Border,
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.8f));

        if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup(
                "Confirm action##profileFriendConfirmation",
                flags)))
        {
            //
            // A normal popup can be dismissed by clicking outside it.
            // Clear the state so the profile is not left permanently dimmed.
            //

            profileFriendConfirmation =
                ProfileFriendConfirmationAction.None;

            return;
        }

        ImGui.TextColored(
            new Vector4(
                0.94f,
                0.92f,
                1f,
                1f),
            blocking
                ? "Block user?"
                : "Remove friend?");

        ImGui.Dummy(
            UiVec(0f, 7f));

        ImGui.Separator();

        ImGui.Dummy(
            UiVec(0f, 10f));

        if (blocking)
        {
            ImGui.TextWrapped(
                "Blocked users will not be able to communicate with you. " +
                "Messages from them in Watch Party chat will be hidden from you. " +
                "In parties that you host, they will automatically be kicked " +
                "from the room.");
        }
        else
        {
            ImGui.TextWrapped(
                $"Are you sure you want to remove " +
                $"{profilePageFallbackName ?? "this user"} from your friends?");
        }

        var cancelSize =
            UiVec(100f, 34f);

        var confirmSize =
            new Vector2(
                blocking
                    ? Ui(116f)
                    : Ui(138f),
                Ui(34f));

        var buttonY =
            ImGui.GetWindowHeight() -
            confirmSize.Y -
            20f;

        var buttonRight =
            ImGui.GetWindowWidth() -
            22f;

        ImGui.SetCursorPos(
            new Vector2(
                buttonRight -
                confirmSize.X -
                cancelSize.X -
                Ui(10f),
                buttonY));

        using (UiDisabled.Scope(
                   profileFriendActionBusy))
        {
            if (DrawProfileActionButton(
                    "##cancelProfileFriendAction",
                    FontAwesomeIcon.Times,
                    "Cancel",
                    cancelSize,
                    CardBg))
            {
                profileFriendConfirmation =
                    ProfileFriendConfirmationAction.None;

                ImGui.CloseCurrentPopup();
            }

            ImGui.SetCursorPos(
                new Vector2(
                    buttonRight -
                    confirmSize.X,
                    buttonY));

            if (DrawProfileActionButton(
                    "##confirmProfileFriendAction",
                    blocking
                        ? FontAwesomeIcon.Ban
                        : FontAwesomeIcon.UserMinus,
                    blocking
                        ? "Block user"
                        : "Remove friend",
                    confirmSize,
                    Danger))
            {
                ExecuteProfileFriendAction();
            }
        }

        ImGui.EndPopup();
    }

    private void ExecuteProfileFriendAction()
    {
        if (profileFriendActionBusy ||
            CurrentSession is not { } session ||
            string.IsNullOrWhiteSpace(
                profilePageAccountId) ||
            profileFriendConfirmation ==
            ProfileFriendConfirmationAction.None)
        {
            return;
        }

        var action =
            profileFriendConfirmation;

        var token =
            session.Token;

        var accountId =
            profilePageAccountId;

        profileFriendActionBusy =
            true;

        ImGui.CloseCurrentPopup();

        CloseProfilePage();

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var succeeded =
                        action ==
                        ProfileFriendConfirmationAction.BlockUser
                            ? await friendsClient
                                .BlockAsync(
                                    token,
                                    accountId)
                                .ConfigureAwait(false)
                            : await friendsClient
                                .RemoveFriendAsync(
                                    token,
                                    accountId)
                                .ConfigureAwait(false);

                    if (!succeeded)
                    {
                        AepLog.Warning(
                            "[Playback]");
                    }

                    if (succeeded && action == ProfileFriendConfirmationAction.BlockUser)
                        await Plugin.Framework.RunOnFrameworkThread(() =>
                        {
                            if (CurrentSession is { } activeSession && activeSession.Token == token)
                                RecordLocalBlockChange(activeSession.AccountId, accountId, true);
                        });

                    friendsDirty =
                        true;
                }
                finally
                {
                    profileFriendActionBusy =
                        false;

                    profileFriendConfirmation =
                        ProfileFriendConfirmationAction.None;
                }
            });
    }

    private void DrawProfileStatusBubble(
    string? status,
    Vector2 avatarOrigin,
    float avatarSize,
    float maximumWidth)
    {
        if (string.IsNullOrWhiteSpace(
                status))
        {
            return;
        }

        var trimmedStatus =
            status.Trim();

        //
        // Keep very long statuses compact in the profile header.
        // The complete text remains available in the tooltip.
        //

        var displayedStatus = TruncateToWidth(trimmedStatus, maximumWidth - Ui(24f));

        var textSize =
            ImGui.CalcTextSize(
                displayedStatus);

        var bubbleSize =
            new Vector2(
                Math.Clamp(
                    textSize.X + Ui(24f),
                    Ui(90f),
                    maximumWidth),
                Ui(32f));

        var bubblePosition =
       new Vector2(
           avatarOrigin.X + avatarSize * 0.60f,

           avatarOrigin.Y +
           avatarSize -
           Ui(36f));

        var bubbleMaximum =
            bubblePosition +
            bubbleSize;

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            bubblePosition,
            bubbleMaximum,
            ImGui.GetColorU32(
                EffectiveProfileTheme?.Background ?? CardBg),
            16f);

        drawList.AddRect(
            bubblePosition,
            bubbleMaximum,
            ImGui.GetColorU32(
                EffectiveProfileTheme?.Accent ?? new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.75f)),
            16f,
            ImDrawFlags.RoundCornersAll,
            1f);

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                bubblePosition.X +
                Ui(12f),

                bubblePosition.Y +
                (
                    bubbleSize.Y -
                    textSize.Y
                ) * 0.5f), ImGui.GetColorU32(
                new Vector4(
                    0.92f,
                    0.89f,
                    1f,
                    1f)), displayedStatus);

        //
        // Create an invisible hover target over the hand-drawn bubble.
        //

        ImGui.SetCursorScreenPos(
            bubblePosition);

        ImGui.InvisibleButton(
            "##profileStatusBubble",
            bubbleSize);

        if (ImGui.IsItemHovered() &&
            displayedStatus != trimmedStatus)
        {
            UiFonts.SetTooltip(
                trimmedStatus);
        }
    }

    private static bool DrawProfileActionButton(
    string id,
    FontAwesomeIcon icon,
    string text,
    Vector2 size,
    Vector4 background)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton(
            id,
            size);

        var hovered =
            ImGui.IsItemHovered();

        var held =
            ImGui.IsItemActive();

        var clicked =
            ImGui.IsItemClicked();

        var buttonColor =
            held
                ? AccentActive
                : hovered
                    ? AccentHover
                    : background;

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                buttonColor),
            8f);

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    hovered ? 0.72f : 0.32f)),
            8f,
            ImDrawFlags.RoundCornersAll,
            1f);

        var glyph =
            icon.ToIconString();

        Vector2 glyphSize;

        using (UiFonts.PushIcon())
        {
            glyphSize =
                ImGui.CalcTextSize(
                    glyph);
        }

        var textSize =
            ImGui.CalcTextSize(
                text);

        var gap = NativeUi(9f);

        var hasText =
            !string.IsNullOrWhiteSpace(
                text);

        var totalWidth =
            glyphSize.X +
            (hasText
                ? gap + textSize.X
                : 0f);

        var contentX =
            origin.X +
            (size.X - totalWidth) * 0.5f;

        using (UiFonts.PushIcon())
        {
            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                    contentX,
                    origin.Y +
                    (size.Y - glyphSize.Y) * 0.5f), ImGui.GetColorU32(
                    Vector4.One), glyph);
        }

        if (hasText)
        {
            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                    contentX +
                    glyphSize.X +
                    gap,
                    origin.Y +
                    (size.Y - textSize.Y) * 0.5f), ImGui.GetColorU32(
                    Vector4.One), text);
        }

        return clicked;
    }

    private static string ProfileActivityLabel(
    ActivityEventDto item)
    {
        return item.Type switch
        {
            "StartedWatching"
                when item.Metadata is { Length: > 0 } title =>
                    $"Started watching {title}",

            "StartedWatching" =>
                "Started watching",

            "JoinedWatchAlong"
                when item.Metadata is { Length: > 0 } host =>
                    $"Joined {host}'s Watch Party",

            "JoinedWatchAlong" =>
                "Joined a Watch Party",

            "FriendAccepted" =>
                "Accepted a friend request",

            "VenueSaved"
                when item.Metadata is { Length: > 0 } venue =>
                    $"Saved {venue}",

            "VenueSaved" =>
                "Saved a venue",

            "WentLive" =>
                "Went live",

            _ =>
                item.Type
        };
    }

    private static string FormatProfileRelativeTime(
        long timestampUnix)
    {
        var elapsed =
            DateTimeOffset.UtcNow -
            DateTimeOffset.FromUnixTimeSeconds(
                timestampUnix);

        if (elapsed < TimeSpan.Zero)
        {
            elapsed =
                TimeSpan.Zero;
        }

        if (elapsed.TotalMinutes < 1d)
        {
            return "Just now";
        }

        if (elapsed.TotalHours < 1d)
        {
            var minutes =
                Math.Max(
                    1,
                    (int)elapsed.TotalMinutes);

            return minutes == 1
                ? "1 minute ago"
                : $"{minutes} minutes ago";
        }

        if (elapsed.TotalDays < 1d)
        {
            var hours =
                Math.Max(
                    1,
                    (int)elapsed.TotalHours);

            return hours == 1
                ? "1 hour ago"
                : $"{hours} hours ago";
        }

        if (elapsed.TotalDays < 7d)
        {
            var days =
                Math.Max(
                    1,
                    (int)elapsed.TotalDays);

            return days == 1
                ? "Yesterday"
                : $"{days} days ago";
        }

        return DateTimeOffset
            .FromUnixTimeSeconds(
                timestampUnix)
            .LocalDateTime
            .ToString(
                "MMM d, yyyy");
    }


}

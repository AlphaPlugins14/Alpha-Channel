using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawProfilePage()
    {
        if (inlineProfileScrollToTop) { ImGui.SetScrollY(0f); inlineProfileScrollToTop=false; }
        if (CurrentSession is not { } session || !profilePageOpen)
        {
            ImGui.TextColored(MutedText, "Sign in to view this profile.");
            return;
        }

        if (profilePageLoading)
        {
            ImGui.TextColored(MutedText, "Loading profile...");
            return;
        }
        if (profilePageNotAvailable || profilePageData is not { } profile)
        {
            ImGui.TextWrapped("This profile couldn't be loaded.");
            if (ImGui.Button("Try again##profileRetry", UiVec(120f, 34f)))
                OpenProfilePage(session, profilePageAccountId!, profilePageFallbackName ?? "Profile");
            return;
        }

        if (InlineProfileEditing && inlineProfileError is { } editError)
        {
            using var color=ImRaii.PushColor(ImGuiCol.Text,Danger);
            ImGui.TextWrapped(editError);
        }
        using var profileId = ImRaii.PushId(profile.AccountId);
        var friend = friends.FirstOrDefault(candidate => candidate.AccountId == profile.AccountId);
        var width = ImGui.GetContentRegionAvail().X;
        var ownProfile = session.AccountId == profile.AccountId;
        var pageOrigin = ImGui.GetCursorScreenPos();
        var availableHeight = MathF.Max(Ui(360f), ImGui.GetWindowPos().Y + ImGui.GetWindowSize().Y
            - ImGui.GetStyle().WindowPadding.Y - pageOrigin.Y - ImGui.GetScrollY());
        var gap = Ui(12f);
        var wideHeader = width >= Ui(600f);
        var avatarSize = Ui(128f);
        var identityHeight = Ui(208f);
        var controlsWidth = Ui(206f);
        var identityWidth = wideHeader ? width - controlsWidth - gap : width;
        var controlsHeight = identityHeight;
        var headerHeight = wideHeader ? identityHeight : identityHeight + gap + controlsHeight;

        DrawProfilePageCard("identity", identityWidth, identityHeight, () =>
        {
            var origin = ImGui.GetCursorScreenPos();
            var innerWidth = ImGui.GetContentRegionAvail().X;
            var detailsX = avatarSize + Ui(20f);
            var detailsWidth = MathF.Max(Ui(80f), innerWidth - detailsX);
            if (InlineProfileEditing) DrawInlineProfileAvatar(origin,avatarSize,profile);
            else DrawAvatarChip(profile.AvatarIcon, profile.AvatarColorHex, avatarSize, profile.AvatarImageUrl);
            if (EffectiveProfileTheme is { } avatarTheme)
                DrawProfileThemeHalo(ImGui.GetWindowDrawList(), origin + new Vector2(avatarSize * .5f), avatarSize * .5f, avatarTheme);
            ImGui.GetWindowDrawList().AddCircle(origin + new Vector2(avatarSize * 0.5f),
                avatarSize * 0.5f, ImGui.GetColorU32(EffectiveProfileTheme?.Accent ?? AccentHover), 64, Ui(2f));
            var statusWidth=MathF.Max(Ui(90f), MathF.Min(Ui(360f), innerWidth - avatarSize * 0.60f));
            if (InlineProfileEditing) DrawInlineProfileStatus(origin,avatarSize,statusWidth);
            else DrawProfileStatusBubble(profile.StatusMessage, origin, avatarSize,statusWidth);

            var detailsOrigin = origin + new Vector2(detailsX, Ui(3f));
            ImGui.SetCursorScreenPos(detailsOrigin);
            ImGui.BeginGroup();
            var hasBadge = PatreonBadgeTier(profile.AccountId) > 0;
            var compactBadge = hasBadge && PatreonBadgeSize(PatreonBadgeTier(profile.AccountId), compact: false).X
                > detailsWidth * 0.7f;
            var badgeWidth = hasBadge
                ? PatreonBadgeSize(PatreonBadgeTier(profile.AccountId), compactBadge).X + Ui(8f) : 0f;
            var online = ownProfile || friend?.Online == true;
            using var uiFontScale4065=UiFonts.PushRelative(1.35f);
            ImGui.TextColored(online ? new Vector4(0.15f, 0.9f, 0.42f, 1f) : MutedText, "●");
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip(ProfileIsNonFriend ? "Add to friends to see online status" : online ? "Online" : "Offline");
            ImGui.SameLine(0f, Ui(7f));
            DrawUsername(profile.AccountId,TruncateToWidth(profile.DisplayName, MathF.Max(Ui(30f), detailsWidth - badgeWidth - Ui(20f))),Vector4.One);
            uiFontScale4065.Dispose();
            if (hasBadge)
            {
                ImGui.SameLine(0f, Ui(8f));
                DrawPatreonBadgeInline(profile.AccountId, compact: compactBadge);
            }
            ImGui.Dummy(UiVec(0f, 5f));
            if (!string.IsNullOrWhiteSpace(profile.CharacterName))
            {
                var linkedName = string.IsNullOrWhiteSpace(profile.CharacterWorld) ? profile.CharacterName :
                    $"{profile.CharacterName} @ {profile.CharacterWorld}";
                using (UiFonts.PushRelative(.90f)) ImGui.TextColored(MutedText, TruncateToWidth(linkedName, detailsWidth));
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip(linkedName);
            }
            ImGui.EndGroup();

            var footer = profile.FriendsSinceUnix is { } since
                ? $"Friends since {DateTimeOffset.FromUnixTimeSeconds(since).LocalDateTime:MMM d, yyyy}"
                : null;
            if (footer is not null && !ProfileIsNonFriend)
            {
                ImGui.SetCursorScreenPos(origin + new Vector2(detailsX, Ui(70f)));
                using var uiFontScale5637=UiFonts.PushRelative(0.85f);
                DrawProfileRoomLine(footer, detailsWidth, MutedText,
                    profile.FriendsSinceUnix is not null ? FontAwesomeIcon.UserFriends : FontAwesomeIcon.User, MutedText);
                uiFontScale5637.Dispose();
            }
            ImGui.SetCursorScreenPos(origin + new Vector2(0f, identityHeight - Ui(76f)));
            DrawProfileLastActivity(innerWidth);
        });

        var controlsOrigin = wideHeader
            ? pageOrigin + new Vector2(identityWidth + gap, 0f)
            : pageOrigin + new Vector2(0f, identityHeight + gap);
        ImGui.SetCursorScreenPos(controlsOrigin);
        DrawProfilePageCard("controls", wideHeader ? controlsWidth : width, controlsHeight, () =>
        {
            var origin = ImGui.GetCursorScreenPos();
            var innerWidth = ImGui.GetContentRegionAvail().X;
            // Keep the controls centered inside their own card.
            ImGui.SetCursorScreenPos(origin + new Vector2(MathF.Max(0f, (innerWidth - Ui(174f)) * 0.5f),
                0f));
            DrawProfilePageActions(profile, friend, ownProfile);
        });

        var sideBySide = width >= Ui(600f);
        var roomWidth = sideBySide ? (width - gap) * 0.6f : width;
        var lowerHeight = Ui(286f);
        if (profilePageRoom is { } room)
        {
            var tags = DirectoryRoomTags(room).Where(tag => tag.Icon != FontAwesomeIcon.Eye && tag.Text != "18+").ToArray();
            var rows = LayoutDirectoryRoomTags(ImGui.GetWindowDrawList(), Vector2.Zero, roomWidth - Ui(32f), tags, render: false);
            lowerHeight = Ui(272f) + rows * Ui(28f);
        }
        var achievementsHeight = sideBySide ? lowerHeight : Ui(286f);
        var lowerSectionHeight = sideBySide ? lowerHeight : lowerHeight + gap + achievementsHeight;
        var bio = string.IsNullOrWhiteSpace(profile.Bio) ? "No bio added yet." : profile.Bio;
        var bioHeight = ImGui.CalcTextSize(bio, false, MathF.Max(1f, width - Ui(32f))).Y;
        // Allocate spare height to About while keeping the room controls together.
        var aboutHeight = MathF.Max(MathF.Max(Ui(100f), bioHeight + Ui(100f)),
            availableHeight - headerHeight - lowerSectionHeight - gap * 2f - Ui(4f));
        ImGui.SetCursorScreenPos(pageOrigin + new Vector2(0f, headerHeight + gap));
        DrawProfilePageCard("about", width, aboutHeight, () =>
        {
            DrawProfileHistoryHeader(session,profile,ownProfile);
            ImGui.Dummy(UiVec(0f, 4f));
            if(profileSocialsTab) { DrawProfileSocialsContent(session,profile.AccountId,ownProfile); return; }
            if(ProfileFriendsEnabled&&profileFriendsTab) { DrawProfileFriendsContent(session,profile.AccountId); return; }
            if(profileHistoryTab) { DrawProfileHistoryContent(session,profile.AccountId); return; }
            var insetSize = new Vector2(ImGui.GetContentRegionAvail().X,
                MathF.Max(bioHeight + Ui(28f), ImGui.GetContentRegionAvail().Y - Ui(4f)));
            DrawProfileInset("about", insetSize, () =>
            {
                if (InlineProfileEditing && inlineAboutEditing)
                {
                    using var disabled=UiDisabled.Scope(inlineProfileSaving);
                    var available=ImGui.GetContentRegionAvail();
                    if (inlineAboutFocus) { ImGui.SetKeyboardFocusHere(); inlineAboutFocus=false; }
                    ImGui.InputTextMultiline("##inlineBio",ref inlineProfileDraft!.Bio,161,
                        new Vector2(available.X,MathF.Max(Ui(30f),available.Y-ImGui.GetTextLineHeightWithSpacing())));
                    ImGui.TextColored(MutedText,$"{inlineProfileDraft.Bio.Length} / 160");
                }
                else
                {
                    var textOrigin=ImGui.GetCursorScreenPos();
                    var textArea=ImGui.GetContentRegionAvail();
                    ImGui.TextWrapped(InlineProfileEditing ? inlineProfileDraft!.Bio : bio);
                    if (InlineProfileEditing)
                    {
                        var nextLine=ImGui.GetCursorScreenPos();
                        ImGui.SetCursorScreenPos(textOrigin);
                        using var disabled=UiDisabled.Scope(inlineProfileSaving);
                        if (ImGui.InvisibleButton("##inlineAboutBox",textArea)) inlineAboutEditing=inlineAboutFocus=true;
                        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Edit About (160 characters maximum)");
                        ImGui.SetCursorScreenPos(nextLine);
                    }
                }
            });
        });

        var lowerOrigin = pageOrigin + new Vector2(0f, headerHeight + aboutHeight + gap * 2f);
        ImGui.SetCursorScreenPos(lowerOrigin);
        DrawProfilePageCard("watchParty", roomWidth, lowerHeight, DrawProfilePageWatchParty);
        ImGui.SetCursorScreenPos(sideBySide ? lowerOrigin + new Vector2(roomWidth + gap, 0f)
            : lowerOrigin + new Vector2(0f, lowerHeight + gap));
        DrawProfilePageCard("achievements", sideBySide ? width - roomWidth - gap : width,
            achievementsHeight, () => DrawProfileBadgeGrid(profilePageUnlockedBadgeIds));
    }

    private static void DrawProfilePanelHeading(string title)
    {
        using var uiFontScale11458=UiFonts.PushRelative(1.3f);
        ImGui.TextColored(Hex(0xE2E7EF), title);
        uiFontScale11458.Dispose();
    }

    private void DrawProfileBadgeGrid(IReadOnlySet<string> unlockedBadgeIds)
    {
        DrawProfilePanelHeading("Badges");
        ImGui.Dummy(UiVec(0f, 8f));
        var origin = ImGui.GetCursorScreenPos();
        var available = ImGui.GetContentRegionAvail();
        var gap = Ui(18f);
        var diameter = MathF.Max(1f, MathF.Min(Ui(52f),
            MathF.Min((available.X - gap * 3f) / 4f, (available.Y - gap * 2f) / 3f)));
        var gridSize = new Vector2(diameter * 4f + gap * 3f, diameter * 3f + gap * 2f);
        var gridOrigin = origin + Vector2.Max(Vector2.Zero, (available - gridSize) * 0.5f);
        var draw = ImGui.GetWindowDrawList();
        var badges = ProfileBadgeCatalog.ForProfile(unlockedBadgeIds);
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 4; column++)
        {
            var position = gridOrigin + new Vector2(column, row) * (diameter + gap);
            var slot = row * 4 + column;
            var badge = slot < badges.Count ? badges[slot] : null;
            var unlocked = badge is not null && unlockedBadgeIds.Contains(badge.Id);
            if (badge is not null)
                DrawProfileBadgeDesign(draw, position, diameter, badge.Design, unlocked);
            else
                DrawProfileBadgePlaceholder(draw, position, diameter);
            if (ImGui.IsMouseHoveringRect(position, position + new Vector2(diameter)))
                UiFonts.SetTooltip(badge is null ? "Badge placeholder" :
                    badge.PreviewOnly ? badge.Label + " (preview)" :
                    unlocked ? badge.Label : badge.Label + " (locked)");
        }
        ImGui.Dummy(new Vector2(available.X, gridOrigin.Y - origin.Y + gridSize.Y));
    }

    private static void DrawProfileBadgePlaceholder(ImDrawListPtr draw, Vector2 position, float diameter)
    {
            var center = position + new Vector2(diameter * 0.5f);
            draw.AddCircleFilled(center, diameter * 0.5f,
                ImGui.GetColorU32(new Vector4(0.30f, 0.33f, 0.39f, 0.12f)), 48);
            draw.AddCircle(center, diameter * 0.5f,
                ImGui.GetColorU32(new Vector4(0.65f, 0.69f, 0.76f, 0.4f)), 48, Ui(1f));
            draw.AddCircle(center, diameter * 0.5f - Ui(3f),
                ImGui.GetColorU32(new Vector4(0.51f, 0.55f, 0.63f, 0.2f)), 48, Ui(1f));
            var sparkleRadius = diameter * 0.09f;
            var sparkleWidth = sparkleRadius * 0.22f;
            var sparkleColor = ImGui.GetColorU32(new Vector4(0.68f, 0.72f, 0.8f, 0.3f));
            draw.AddQuadFilled(center - new Vector2(0f, sparkleRadius),
                center + new Vector2(sparkleWidth, 0f), center + new Vector2(0f, sparkleRadius),
                center - new Vector2(sparkleWidth, 0f), sparkleColor);
            draw.AddQuadFilled(center - new Vector2(sparkleRadius, 0f),
                center - new Vector2(0f, sparkleWidth), center + new Vector2(sparkleRadius, 0f),
                center + new Vector2(0f, sparkleWidth), sparkleColor);
    }

    private void DrawProfilePageCard(string id, float width, float height, Action drawContent)
    {
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(16f, 16f))
            .Push(ImGuiStyleVar.ItemSpacing, UiVec(8f, 6f));
        var theme = EffectiveProfileTheme;
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, theme?.Background ?? CardBg)
            .Push(ImGuiCol.Border, theme is null ? BorderSubtle : theme.Accent with { W=.65f });
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild($"##profilePage_{id}", new Vector2(width, height), true,
                ImGuiWindowFlags.NoScrollbar)))
        {
            DrawProfileThemeDecoration(ImGui.GetWindowDrawList(), ImGui.GetWindowPos(), ImGui.GetWindowSize(), theme);
            drawContent();
        }
        ImGui.EndChild();
    }

    private void DrawProfileLastActivity(float width)
    {
        if (ProfileIsNonFriend)
        {
            DrawProfileInset("activity",new Vector2(width,Ui(48f)),()=>
            {
                using (UiFonts.PushIcon())
                    ImGui.TextColored(EffectiveProfileTheme?.Accent ?? AccentHover,FontAwesomeIcon.InfoCircle.ToIconString());
                ImGui.SameLine(0f,Ui(10f));
                ImGui.TextWrapped("Add to friends list to see last seen status");
            });
            return;
        }
        DrawProfileInset("activity", new Vector2(width, Ui(48f)), () =>
        {
            // The bottom strip uses the activity icon, title and time directly.
            var origin = ImGui.GetCursorScreenPos();
            var activity = profilePageRecentActivity.FirstOrDefault();
            var audioActivity = activity?.Type == "StartedWatching" && profilePageRoom is { HasMedia: true } room
                && GetPartyDirectoryContentType(room) is PartyDirectoryContentType.RadioAudio or PartyDirectoryContentType.DjingLive;
            var label = profileDetailsLoading ? "Loading activity..."
                : activity is null ? "No activity to show yet."
                : audioActivity ? "Listening to audio stream" : ProfileActivityLabel(activity);
            var icon = activity?.Type switch
            {
                "JoinedWatchAlong" => FontAwesomeIcon.UserFriends,
                "WentLive" => FontAwesomeIcon.BroadcastTower,
                "VenueSaved" => FontAwesomeIcon.MapMarkerAlt,
                _ => FontAwesomeIcon.Play,
            };
            var draw = ImGui.GetWindowDrawList();
            var iconSize = Ui(28f);
            var activityAccent = EffectiveProfileTheme?.Accent ?? AccentHover;
            ImGui.SetCursorScreenPos(origin + UiVec(38f, 0f));
            ImGui.BeginGroup();
            using var uiFontScale17497=UiFonts.PushRelative(1.1f);
            ImGui.TextColored(Hex(0xE2E7EF), TruncateToWidth(label, MathF.Max(Ui(40f), width - Ui(62f))));
            uiFontScale17497.Dispose();
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip(label);
            if (activity is not null)
            {
                using var uiFontScale17785=UiFonts.PushRelative(0.8f);
                ImGui.TextColored(new Vector4(MutedText.X, MutedText.Y, MutedText.Z, 0.8f),
                    FormatProfileRelativeTime(activity.CreatedAtUnix));
                uiFontScale17785.Dispose();
            }
            ImGui.EndGroup();
            var iconCenter = new Vector2(origin.X + iconSize * 0.5f,
                (ImGui.GetItemRectMin().Y + ImGui.GetItemRectMax().Y) * 0.5f);
            var iconOrigin = iconCenter - new Vector2(iconSize * 0.5f);
            draw.AddCircleFilled(iconCenter, iconSize * 0.5f,
                ImGui.GetColorU32(activityAccent with { W=.16f }), 32);
            if (audioActivity)
            {
                for (var i = 0; i < 5; i++)
                {
                    var barHeight = Ui(i == 2 ? 24f : i is 1 or 3 ? 16f : 8f);
                    var center = iconOrigin + new Vector2(Ui(5f + i * 4.5f), iconSize * 0.5f);
                    draw.AddLine(center - new Vector2(0f, barHeight * 0.5f),
                        center + new Vector2(0f, barHeight * 0.5f), ImGui.GetColorU32(activityAccent), Ui(2f));
                }
            }
            else
            {
                using var iconFont = UiFonts.PushIcon();
                var glyph = icon.ToIconString();
                UiFonts.DrawIcon(draw, ImGui.GetFontSize(), iconCenter - ImGui.CalcTextSize(glyph) * 0.5f, ImGui.GetColorU32(activityAccent), glyph);
            }
        });
    }

    private void DrawProfilePageActions(AccountProfileDto profile, FriendDto? friend, bool ownProfile)
    {
        if (ownProfile && InlineProfileEditing && CurrentSession is { } editingSession)
        { DrawInlineProfileControls(editingSession); return; }
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(8f));
        var actionTheme = EffectiveProfileTheme;
        var buttonAccent = actionTheme is null ? Accent : new Vector4(actionTheme.Accent.X*.45f,
            actionTheme.Accent.Y*.45f, actionTheme.Accent.Z*.45f,1f);
        using var colors = ImRaii.PushColor(ImGuiCol.Button, buttonAccent)
            .Push(ImGuiCol.ButtonHovered, buttonAccent).Push(ImGuiCol.ButtonActive, buttonAccent);
        var actionsOrigin = ImGui.GetCursorScreenPos();
        var canMessage = !ownProfile && friend is not null && CurrentSession is not null;
        using (UiDisabled.Scope(!canMessage))
        using (ImRaii.PushColor(ImGuiCol.Button, canMessage ? buttonAccent : FrameBg)
            .Push(ImGuiCol.Text, canMessage ? Vector4.One : MutedText))
        {
            if (ImGui.Button("Message##profilePage", UiVec(174f, 34f)) && CurrentSession is { } session)
            {
                StartOrOpenConversation(session, profile.AccountId, profile.DisplayName);
                CloseProfilePage();
            }
        }
        ImGui.SetCursorScreenPos(actionsOrigin + UiVec(0f, 42f));
        var canInvite = canMessage && CanInviteToCurrentWatchParty();
        using (UiDisabled.Scope(!canInvite))
        using (ImRaii.PushColor(ImGuiCol.Button, canInvite ? buttonAccent : FrameBg)
            .Push(ImGuiCol.Text, canInvite ? Vector4.One : MutedText))
        {
            if (ImGui.Button("Invite to Watch Party##profilePage", UiVec(174f, 34f)) && CurrentSession is { } session)
            {
                StartOrOpenConversation(session, profile.AccountId, profile.DisplayName, CreateWatchPartyInviteMessage());
                CloseProfilePage();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !canInvite)
            UiFonts.SetTooltip(ownProfile ? "You can't invite yourself." : "Host a Watch Party to invite your friend.");
        ImGui.SetCursorScreenPos(actionsOrigin + UiVec(0f, 86f));
        ImGui.Separator();
        ImGui.SetCursorScreenPos(actionsOrigin + UiVec(0f, 98f));
        if (ownProfile)
        {
            if (ImGui.Button("Edit profile##profilePage", UiVec(174f, 34f)))
            {
                if (CurrentSession is { } editSession) BeginInlineProfileEdit(editSession,profile);
            }
            return;
        }
        if (ProfileIsNonFriend)
        {
            var pending=friendRequests.Outgoing.Any(r=>r.OtherAccountId==profile.AccountId) ||
                friendRequests.Incoming.Any(r=>r.OtherAccountId==profile.AccountId);
            using (UiDisabled.Scope(pending || CurrentSession is null ||
                CurrentSession is { } requestSession && partyFriendRequestsSending.ContainsKey((requestSession.Token,profile.AccountId))))
                if (ImGui.Button(pending ? "Request pending##profilePage" : "Add friend##profilePage",UiVec(174f,34f)) && CurrentSession is { } addSession)
                    _=SendPartyFriendRequestAsync(addSession,profile.AccountId,profile.DisplayName);
        }
        using var dangerColors = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.20f, 0.10f, 0.14f, 1f))
            .Push(ImGuiCol.ButtonHovered, new Vector4(0.30f, 0.13f, 0.18f, 1f))
            .Push(ImGuiCol.ButtonActive, new Vector4(0.36f, 0.14f, 0.19f, 1f))
            .Push(ImGuiCol.Text, new Vector4(0.94f, 0.52f, 0.58f, 1f));
        if (!ProfileIsNonFriend && ImGui.Button("Remove friend##profilePage", UiVec(174f, 34f)))
        {
            profileFriendConfirmation = ProfileFriendConfirmationAction.RemoveFriend;
            profileFriendConfirmationPendingOpen = true;
        }
        ImGui.SetCursorScreenPos(actionsOrigin + UiVec(0f, 140f));
        if (ImGui.Button("Block user##profilePage", UiVec(174f, 34f)))
            QueueFriendBlockConfirmation(profile.AccountId, profile.DisplayName);
    }

    private void DrawProfileInset(string id, Vector2 size, Action drawContent)
    {
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(8f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(12f, 6f))
            .Push(ImGuiStyleVar.ItemSpacing, UiVec(6f, 3f));
        var theme = EffectiveProfileTheme;
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, theme is null ? FrameBg : ThemeInset(theme))
            .Push(ImGuiCol.Border, theme is null ? new Vector4(Accent.X, Accent.Y, Accent.Z, 0.12f) : theme.Accent with { W=.25f });
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild($"##profileInset_{id}", size, true, ImGuiWindowFlags.NoScrollbar)))
            drawContent();
        ImGui.EndChild();
    }

    private void DrawProfilePageWatchParty()
    {
        DrawProfilePanelHeading("Current Watch Party");
        ImGui.Dummy(UiVec(0f, 5f));
        var width = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetContentRegionAvail().Y;
        var origin = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var theme = EffectiveProfileTheme;
        if (ProfileIsNonFriend)
        {
            DrawProfileInset("partyFriendOnly",new Vector2(width,height),()=>
            {
                var area=ImGui.GetContentRegionAvail();
                var start=ImGui.GetCursorScreenPos();
                const string message="Add to friends to see live watch party status";
                var wrapWidth=MathF.Min(area.X,Ui(260f));
                var textSize=ImGui.CalcTextSize(message,false,wrapWidth);
                var textOrigin=start+new Vector2((area.X-textSize.X)*.5f,MathF.Max(0,(area.Y-textSize.Y)*.5f)+Ui(16f));
                using (UiFonts.PushIcon())
                {
                    var glyph=FontAwesomeIcon.UserLock.ToIconString();
                    var iconSize=ImGui.CalcTextSize(glyph);
                    UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), new Vector2(start.X+(area.X-iconSize.X)*.5f,textOrigin.Y-Ui(36f)), ImGui.GetColorU32(theme?.Accent ?? AccentHover), glyph);
                }
                // Draw inside the inset's clip region so the placeholder always covers the room.
                UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), textOrigin, ImGui.GetColorU32(MutedText), message, wrapWidth);
            });
            return;
        }
        if (profilePageRoom is not { } room)
        {
            var thumbnailSize = new Vector2(width, Math.Clamp(height - Ui(82f), Ui(80f), Ui(160f)));
            DrawPartyThumbnailFallback(new VideoQueueEntry(string.Empty, string.Empty, "Waiting", null, null),
                origin, thumbnailSize, usePlaybackState: false, compactIcon: FontAwesomeIcon.Users,
                compactIconFontSize: Ui(30f), accentTint: theme?.Accent,
                backgroundTint: theme is null ? null : ThemeInset(theme));
            ImGui.SetCursorScreenPos(origin + new Vector2(0f, thumbnailSize.Y + Ui(12f)));
            ImGui.TextWrapped(profileDetailsLoading ? "Loading Watch Party..." : "No Watch Party to show right now.");
            if (!profileDetailsLoading)
                DrawSocialHelper("Their next Watch Party will appear here.");
            return;
        }

        EnsurePartyDirectoryMediaLoaded(room);
        var media = GetPartyDirectoryMedia(room);
        var tags = DirectoryRoomTags(room).Where(tag => tag.Icon != FontAwesomeIcon.Eye && tag.Text != "18+").ToArray();
        if (theme is not null)
            tags = tags.Select(tag => (tag.Icon, tag.Text, theme.Accent)).ToArray();
        var rows = LayoutDirectoryRoomTags(draw, Vector2.Zero, width, tags, render: false);
        var canJoin = CurrentSession is not null && CurrentSession.AccountId != profilePageAccountId;
        var footerHeight = rows * Ui(28f) + Ui(58f);
        var thumbnailSizeRoom = new Vector2(width * 0.48f,
            Math.Clamp(height - footerHeight - Ui(16f), Ui(86f), Ui(160f)));
        DrawPartyDirectoryThumbnail(draw, room, media, origin, thumbnailSizeRoom, theme);
        DrawPartyDirectoryPlaybackBadge(draw, room, origin + UiVec(6f, 6f), theme?.Accent);
        DrawPartyDirectoryViewerBadge(draw, room.ViewerCount + 1,
            origin + new Vector2(thumbnailSizeRoom.X - Ui(6f), Ui(6f)));
        if (WatchPartyIsAdultOnly(room))
            DrawWatchPartyAdultTag(draw, origin + new Vector2(thumbnailSizeRoom.X - Ui(44f), thumbnailSizeRoom.Y - Ui(28f)));

        var detailsOrigin = origin + new Vector2(thumbnailSizeRoom.X + Ui(12f), Ui(2f));
        var detailsWidth = width - thumbnailSizeRoom.X - Ui(12f);
        ImGui.SetCursorScreenPos(detailsOrigin);
        ImGui.BeginGroup();
        DrawUsername(room.HostAccountId,TruncateToWidth($"{room.HostDisplayName}'s Watch Party",detailsWidth));
        TryReadWatchPartyMetadata(room, out _, out _, out _, out var description);
        if (!string.IsNullOrWhiteSpace(description)) DrawProfileRoomLine(description, detailsWidth, MutedText);
        if (!string.IsNullOrWhiteSpace(room.Location)) DrawProfileRoomLine(room.Location, detailsWidth, MutedText, FontAwesomeIcon.MapMarkerAlt);
        if (room.HasMedia && !string.IsNullOrWhiteSpace(media.Title)) DrawProfileRoomLine(media.Title, detailsWidth, MutedText,
            GetPartyDirectoryContentType(room) is PartyDirectoryContentType.RadioAudio or PartyDirectoryContentType.DjingLive
                ? FontAwesomeIcon.Music : FontAwesomeIcon.Play);
        DrawProfileRoomLine($"{room.ViewerCount + 1} watching", detailsWidth, MutedText, FontAwesomeIcon.UserFriends);
        ImGui.EndGroup();

        var tagOrigin = origin + new Vector2(0f, thumbnailSizeRoom.Y + Ui(12f));
        LayoutDirectoryRoomTags(draw, tagOrigin, width, tags, render: true);
        ImGui.SetCursorScreenPos(tagOrigin);
        ImGui.Dummy(new Vector2(width, rows * Ui(28f)));
        using (UiDisabled.Scope(!canJoin))
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(0f, height - Ui(48f)));
            var buttonAccent = theme is null ? Accent : theme.Accent * new Vector4(.45f,.45f,.45f,1f);
            using var colors = ImRaii.PushColor(ImGuiCol.Button, canJoin ? buttonAccent : FrameBg)
                .Push(ImGuiCol.ButtonHovered, theme is null ? AccentHover : buttonAccent * new Vector4(1.2f,1.2f,1.2f,1f))
                .Push(ImGuiCol.ButtonActive, theme is null ? AccentActive : buttonAccent);
            using var buttonStyle = ImRaii.PushStyle(ImGuiStyleVar.ButtonTextAlign, new Vector2(0.5f, 0.5f));
            if (ImGui.Button("Join Watch Party##profilePage", new Vector2(width, Ui(48f))))
            {
                JoinPartyDirectoryRoom(room);
                CloseProfilePage();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !canJoin)
            UiFonts.SetTooltip("You're already hosting this Watch Party.");
    }

    private void DrawProfileRoomLine(string text, float width, Vector4 color, FontAwesomeIcon? icon = null, Vector4? iconTint = null)
    {
        var origin = ImGui.GetCursorScreenPos();
        if (icon is { } glyph)
        {
            var iconColor = iconTint ?? EffectiveProfileTheme?.Accent ?? (glyph switch
            {
                FontAwesomeIcon.MapMarkerAlt => new Vector4(0.32f, 0.68f, 1f, 1f),
                FontAwesomeIcon.UserFriends => new Vector4(0.35f, 0.84f, 0.61f, 1f),
                _ => new Vector4(0.77f, 0.55f, 1f, 1f),
            });
            UiFonts.DrawIcon(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), origin, ImGui.GetColorU32(iconColor), glyph.ToIconString());
            ImGui.SetCursorScreenPos(origin + UiVec(24f, 0f));
            width -= Ui(24f);
        }
        var label = TruncateToWidth(text, width);
        ImGui.TextColored(color, label);
        if (label != text && ImGui.IsItemHovered()) UiFonts.SetTooltip(text);
        ImGui.SetCursorScreenPos(origin + new Vector2(0f, ImGui.GetTextLineHeightWithSpacing()));
    }
}

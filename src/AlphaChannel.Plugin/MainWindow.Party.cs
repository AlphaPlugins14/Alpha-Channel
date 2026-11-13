using AlphaChannel.Contracts;
using AlphaChannel.Plugin.BroadcastAllowance;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Config;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Org.BouncyCastle.Tls;

namespace AlphaChannel.Plugin;

// Watch party lives on Player: host/join/roster + ephemeral room chat (stream.chat).
internal sealed partial class MainWindow
{
    private string partyJoinInput = string.Empty;

    private enum PartyChatItemKind
    {
        Message,
        QueueNotice,
        Reaction,
        RoomPresence
    }

    private sealed record PartyChatItem(
        Guid Id,
        PartyChatItemKind Kind,
        string Name,
        string Text,
        DateTime? ReceivedAt = null,
        string UserId = "",
        Guid? ReplyToId = null,
        bool HasSharedMessageId = false,
        int QueueNoticeCount = 1);

    private readonly List<PartyChatItem>
        partyChatItems = [];

    private readonly Dictionary<string, ParticipantInfo> partyKnownRoster =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Name, DateTime SinceUtc)> partyPendingLeaves =
        new(StringComparer.OrdinalIgnoreCase);
    private int partyRosterGeneration = -1;
    private StreamMode partyRosterMode = StreamMode.None;
    private string? partyRosterHostId;
    private bool partyRosterSeen;

    private void AddPartyPresenceNotice(string name, string userId, bool joined)
    {
        var noticeId = Guid.NewGuid();
        partyChatItems.Add(new PartyChatItem(
            noticeId, PartyChatItemKind.RoomPresence,
            string.IsNullOrWhiteSpace(name) ? "Someone" : name,
            joined ? "joined the Watch Party" : "left the Watch Party",
            ReceivedAt: DateTime.Now, UserId: userId));
        RegisterPartyChatUnread(noticeId, kind: PartyChatItemKind.RoomPresence);
    }

    private void RegisterPartyChatUnread(Guid itemId, bool mentionsMe = false, bool playNotificationSound = true,
        PartyChatItemKind kind = PartyChatItemKind.Message)
    {
        if (!PartyChatNotificationEnabled(kind))
        {
            partyChatStickToBottom = partyChatWasAtBottom && partyChatFirstUnreadId is null;
            return;
        }
        if (mentionsMe) partyChatUnseenMentions.Add(itemId);
        if (playNotificationSound) QueueNotificationSound(partyChat: true, partyKind: kind);
        var readingFullChat = IsOpen && !windowMinimized &&
            currentPage == HomePage.WatchAlong && partyPanelTab == PartyPanelTab.Chat &&
            partyChatWasAtBottom && DateTime.UtcNow - partyChatLastDrawUtc < TimeSpan.FromSeconds(1);
        var readingMiniChat = IsMiniChatOpen?.Invoke() == true && miniChatWasAtBottom &&
            DateTime.UtcNow - miniChatLastReadDrawUtc < TimeSpan.FromSeconds(1);
        var readingLatest = readingFullChat || readingMiniChat;
        if (!readingLatest)
        {
            if (partyChatFirstUnreadId is null)
            {
                partyChatFirstUnreadId = itemId;
                partyChatUnreadDividerShownUtc = null;
            }
            partyChatUnreadIds.Add(itemId);
        }
        partyChatStickToBottom = partyChatWasAtBottom && partyChatFirstUnreadId is null;
    }

    // Framework-owned so incoming requests and badges also update while the
    // main window is closed. This path does not call ImGui.
    private DateTime nextPartyChatUpdateUtc;
    internal void UpdateWatchPartyChat()
    {
        var now = DateTime.UtcNow;
        if (now < nextPartyChatUpdateUtc) return;
        nextPartyChatUpdateUtc = now.AddMilliseconds(100);
        if (stream.Mode == StreamMode.None)
        {
            ResetPartyChatState();
            DrainPartyRosterNotices();
        }
        UpdateNotificationSounds();
        if (stream.Mode is StreamMode.Hosting or StreamMode.Viewing)
            DrainPartyChat();
    }

    private void DrainPartyRosterNotices()
    {
        if (stream.Mode == StreamMode.None)
        {
            partyKnownRoster.Clear();
            partyPendingLeaves.Clear();
            partyRosterGeneration = -1;
            partyRosterMode = StreamMode.None;
            partyRosterHostId = null;
            partyRosterSeen = false;
            stream.IncomingRosterSnapshots.Clear();
            return;
        }

        while (stream.IncomingRosterSnapshots.TryDequeue(out var snapshot))
        {
            var next = snapshot.Participants
                .Where(participant => !string.IsNullOrWhiteSpace(participant.UserId))
                .ToDictionary(participant => participant.UserId, StringComparer.OrdinalIgnoreCase);
            var newRoom = partyRosterSeen &&
                (partyRosterGeneration != snapshot.ConnectionGeneration ||
                 partyRosterMode != snapshot.Mode ||
                 !string.Equals(partyRosterHostId, stream.HostId, StringComparison.OrdinalIgnoreCase));
            if (!partyRosterSeen || newRoom)
            {
                ObservePatreonPeople(next.Keys.Concat(new[] { stream.HostId ?? string.Empty }));
                partyKnownRoster.Clear();
                partyPendingLeaves.Clear();
                // The host has no initial empty-roster event. Its first roster can
                // already contain the first guest; a joiner instead receives the
                // whole existing roster as a baseline.
                if (!partyRosterSeen && snapshot.Mode == StreamMode.Hosting)
                {
                    foreach (var participant in snapshot.Participants)
                        if (participant.UserId != stream.LocalUserId)
                            AddPartyPresenceNotice(participant.DisplayName, participant.UserId, true);
                }
                foreach (var (id, participant) in next)
                    partyKnownRoster[id] = participant;
                partyRosterGeneration = snapshot.ConnectionGeneration;
                partyRosterMode = snapshot.Mode;
                partyRosterHostId = stream.HostId;
                partyRosterSeen = true;
                continue;
            }

            ObservePatreonPeople(next.Keys.Where(id => !partyKnownRoster.ContainsKey(id)));
            foreach (var (id, participant) in next)
            {
                if (partyKnownRoster.ContainsKey(id))
                    continue;
                if (!partyPendingLeaves.Remove(id) && id != stream.LocalUserId)
                    AddPartyPresenceNotice(participant.DisplayName, id, true);
            }
            foreach (var (id, participant) in partyKnownRoster)
            {
                if (!next.ContainsKey(id) && id != stream.LocalUserId)
                    partyPendingLeaves.TryAdd(id, (participant.DisplayName, DateTime.UtcNow));
            }
            partyKnownRoster.Clear();
            foreach (var (id, participant) in next)
                partyKnownRoster[id] = participant;
        }

        if (!stream.IsConnected)
            return;
        foreach (var (id, leave) in partyPendingLeaves.ToArray())
        {
            if (DateTime.UtcNow - leave.SinceUtc < TimeSpan.FromSeconds(2))
                continue;
            AddPartyPresenceNotice(leave.Name, id, false);
            partyPendingLeaves.Remove(id);
        }
    }

    private sealed record PartyAvatarInfo(
        string? AvatarIcon,
        string? AvatarColorHex,
        string? AvatarImageUrl,
        DateTime RefreshedAt);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        PartyAvatarInfo> partyAvatarCache = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        byte> partyAvatarLookupsInFlight = new();

    private string partyChatInput = string.Empty;
    private bool partyChatStickToBottom = true;
    private VideoQueueEntry? viewerPartyNextQueueEntry;
    private int viewerPartyQueueCount;

    internal void AddPartyReactionToFeed(
      string userId,
      string displayName,
      string glyph)
    {
        if (stream.Mode is not
            (StreamMode.Hosting or StreamMode.Viewing))
        {
            return;
        }

        var reactionId = Guid.NewGuid();
        partyChatItems.Add(
     new PartyChatItem(
         reactionId,
         PartyChatItemKind.Reaction,
         string.IsNullOrWhiteSpace(displayName)
             ? "Someone"
             : displayName,
         glyph,
         ReceivedAt: DateTime.Now,
         UserId: userId));

        RegisterPartyChatUnread(reactionId,
            playNotificationSound: !string.Equals(userId, stream.LocalUserId, StringComparison.Ordinal),
            kind: PartyChatItemKind.Reaction);

        if (partyChatItems.Count > 200)
        {
            partyChatItems.RemoveRange(
                0,
                partyChatItems.Count - 200);
            RefreshPartyNotificationPreferences();
        }

        partyChatStickToBottom = partyChatWasAtBottom && partyChatFirstUnreadId is null;
    }

    private enum PartyPanelTab
    {
        Watching,
        NowPlaying,
        Chat,
    }

    private PartyPanelTab partyPanelTab = PartyPanelTab.Chat;
    private bool gameplayStreamOfferDismissed;
    private bool browserStreamOfferDismissed;

    private bool partySyncTvPlacement = true;
    internal bool PartySyncTvPlacement => hostScreenSyncPause.ShouldFollowHost(partySyncTvPlacement);

    private AudioVisualizerMode partyVisualizerMode =
     AudioVisualizerMode.ClassicBars;

    private AudioVisualizerTheme partyVisualizerTheme =
        AudioVisualizerTheme.AlphaPurple;

    private string? partyVisualizerMediaUrl;
    private AudioScreenTextSelection partyAudioScreenText = AudioScreenTextSelection.Empty;

    internal AudioVisualizerMode PartyVisualizerMode =>
        partyVisualizerMode;

    internal AudioVisualizerTheme PartyVisualizerTheme =>
        partyVisualizerTheme;

    internal string BuildPartyAudioPresentationUrl(string url)
    {
        EnsureMusicScene();
        return MusicSceneEffects.AddToUrl(AudioScreenTextSelection.AddToUrl(
            AudioVisualizerSelection.AddToUrl(url, partyVisualizerMode, partyVisualizerTheme),
            url.StartsWith("music-scene://", StringComparison.OrdinalIgnoreCase) ? partyAudioScreenText : ResolveMusicScreenText(partyAudioScreenText)), GetMusicEffects());
    }

    private bool ShouldShowBottomPlaybackBar =>
        currentPage is
            HomePage.Home or
            HomePage.Player or
            HomePage.Music or
            HomePage.VideoGrid or
            HomePage.Dailymotion or
            HomePage.InternetArchive;

    private void EnsurePartyAvatarLoaded(
     string userId,
     string displayName)
    {
        if (string.IsNullOrWhiteSpace(userId) ||
            CurrentSession is not { } session)
        {
            return;
        }

        // ---------------------------------------------------------
        // Keep a resolved server avatar for 60 seconds.
        //
        // Once stale, refresh it in the background while the old
        // avatar remains visible.
        // ---------------------------------------------------------

        if (partyAvatarCache.TryGetValue(
                userId,
                out var cachedAvatar) &&
            DateTime.UtcNow -
            cachedAvatar.RefreshedAt <
            TimeSpan.FromSeconds(60))
        {
            return;
        }

        // Only one lookup per participant at a time.
        if (!partyAvatarLookupsInFlight.TryAdd(
                userId,
                0))
        {
            return;
        }

        var token =
            session.Token;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    // -------------------------------------------------
                    // PRIMARY:
                    // Fetch the participant's real server profile.
                    //
                    // This is the exact route verified by our temporary
                    // Watch Party self-test.
                    // -------------------------------------------------

                    AccountProfileDto? profile =
                        null;

                    try
                    {
                        profile =
                            await authClient.GetProfileAsync(
                                token,
                                userId);
                    }
                    catch (Exception)
                    {
                        // Profile access may legitimately be unavailable
                        // for a non-friend. Continue to the public search
                        // fallback instead of killing the avatar lookup.
                        
                    }

                    if (profile is not null)
                    {
                        partyAvatarCache[userId] =
                            new PartyAvatarInfo(
                                profile.AvatarIcon,
                                profile.AvatarColorHex,
                                profile.AvatarImageUrl,
                                DateTime.UtcNow);

                        return;
                    }

                    // -------------------------------------------------
                    // FALLBACK:
                    // Public/display-name account search.
                    //
                    // Never trust display name alone. The returned
                    // AccountId must exactly match the UserId carried
                    // by the Watch Party message/reaction.
                    // -------------------------------------------------

                    if (string.IsNullOrWhiteSpace(
                            displayName))
                    {
                        //
                        // Cache the missing result too. Home room cards
                        // render every frame, so an unavailable avatar
                        // must not cause continuous profile requests.
                        //
                        partyAvatarCache[userId] =
                            new PartyAvatarInfo(
                                null,
                                null,
                                null,
                                DateTime.UtcNow);

                        return;
                    }

                    var results =
                        await friendsClient.SearchAsync(
                            token,
                            displayName);

                    if (results is null)
                    {
                        partyAvatarCache[userId] =
                            new PartyAvatarInfo(
                                null,
                                null,
                                null,
                                DateTime.UtcNow);

                        return;
                    }

                    var match =
                        results.FirstOrDefault(
                            result =>
                                string.Equals(
                                    result.AccountId,
                                    userId,
                                    StringComparison.Ordinal));

                    if (match is null)
                    {
                        partyAvatarCache[userId] =
                            new PartyAvatarInfo(
                                null,
                                null,
                                null,
                                DateTime.UtcNow);

                        return;
                    }

                    partyAvatarCache[userId] =
                        new PartyAvatarInfo(
                            match.AvatarIcon,
                            match.AvatarColorHex,
                            match.AvatarImageUrl,
                            DateTime.UtcNow);
                }
                catch (Exception)
                {
                    {}
                }
                finally
                {
                    partyAvatarLookupsInFlight.TryRemove(
                        userId,
                        out _);
                }
            });
    }

    private bool partyAudioSettingsOverlayOpen;
    private Video.VideoQueueEntry? partyAudioSettingsMedia;

    private void OpenAudioPresentationSettings(Video.VideoQueueEntry current)
    {
        EnsureMusicScene();
        // Queue entries may still contain the original presentation fragment.
        // The Music editor owns the latest scene; opening it must not restore
        // stale visualiser settings from the current station's queue entry.
        partyVisualizerMediaUrl = AudioVisualizerSelection.Parse(current.Url).MediaUrl;

        partyAudioSettingsMedia = current;
        currentPage = HomePage.Music;
        musicTab = MusicTab.Scene;
        partyAudioSettingsOverlayOpen = false;
    }

    private void ApplyPartyAudioPresentation(Video.VideoQueueEntry current) => ApplyMusicScene();

    private void DrawPartyVisualizerOption(
    Video.VideoQueueEntry current,
    AudioVisualizerMode mode)
    {
        var selected =
            partyVisualizerMode ==
            mode;

        var label =
            AudioVisualizerSelection.GetDisplayName(
                mode);

        if (ImGui.Selectable(
                label,
                selected))
        {
            partyVisualizerMode =
                mode;

            var transmittedUrl =
                BuildPartyAudioPresentationUrl(current.Url);

            //
            // VideoPlayer recognizes this as a presentation-only fragment
            // change and does not restart the audio.
            //
            video.Play(
                transmittedUrl);

            
        }

        if (selected)
        {
            ImGui.SetItemDefaultFocus();
        }
    }

    private void DrawPartyVisualizerThemeOption(
    Video.VideoQueueEntry current,
    AudioVisualizerTheme theme)
    {
        var selected =
            partyVisualizerTheme ==
            theme;

        var label =
            AudioVisualizerSelection.GetThemeDisplayName(
                theme);

        if (ImGui.Selectable(
                label,
                selected))
        {
            partyVisualizerTheme =
                theme;

            var transmittedUrl =
                BuildPartyAudioPresentationUrl(current.Url);

            //
            // The normalized media URL remains unchanged, so only the FFmpeg
            // visualizer graph is rebuilt.
            //
            video.Play(
                transmittedUrl);

            
        }

        if (selected)
        {
            ImGui.SetItemDefaultFocus();
        }
    }

    private void DrawPartyPanel()
    {
        if (partyPanelTab != PartyPanelTab.Watching ||
            stream.Mode is not (StreamMode.Hosting or StreamMode.Viewing))
            activePartyInviteOpen = false;
        if (CurrentSession is null)
        {
            DrawLegacyPartyPanel();
            return;
        }

        if (stream.Mode is not (StreamMode.Hosting or StreamMode.Viewing))
        {
            DrawLegacyPartyPanel();
            return;
        }

        DrawPartyTabButtons();

        ImGui.SetCursorPosY(
            ImGui.GetCursorPosY() +
            Ui(14f) -
            ImGui.GetStyle().ItemSpacing.Y);

        switch (partyPanelTab)
        {
            case PartyPanelTab.Watching:
                DrawPartyWatchingTab();
                break;

            case PartyPanelTab.NowPlaying:
                DrawPartyNowPlayingTab();
                break;

            case PartyPanelTab.Chat:
                DrawPartyChatTab();
                break;
        }
    }

    private bool TryGetExclusivePlaybackStreamOffer(out bool isBrowser)
    {
        isBrowser = false;
        if (stream.Mode != StreamMode.Hosting) return false;
        var engine = screenController.Engine;
        if (engine.IsPlayingBrowser && !browserBroadcastArmed && !engine.IsBrowserBroadcasting &&
            !browserStreamOfferDismissed)
        {
            isBrowser = true;
            return true;
        }
        return engine.IsPlayingGame && !gameplayStreamOfferDismissed && !gameBroadcastArmed &&
            !engine.IsSnesBroadcasting && !engine.IsGameBoyBroadcasting && !engine.IsNesBroadcasting &&
            !engine.IsGameBoyAdvanceBroadcasting && !engine.IsMasterSystemBroadcasting && !engine.IsGameGearBroadcasting;
    }

    private static string ExclusivePlaybackStreamOfferMessage(bool isBrowser) => isBrowser
        ? "Your browser is running locally. Start a stream to share it with this Watch Party."
        : "Your game is running locally. Start a stream to share your gameplay with this Watch Party.";

    private void DrawExclusivePlaybackStreamOfferInline(Vector2 origin, float width, bool isBrowser)
    {
        ImGui.SetCursorScreenPos(origin);
        ImGui.TextColored(AccentHover, isBrowser ? "Share your browser with the party" : "Share your gameplay with the party");
        var message = ExclusivePlaybackStreamOfferMessage(isBrowser);
        ImGui.SetCursorScreenPos(origin + UiVec(0f, 28f));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        ImGui.TextColored(MutedText, message);
        ImGui.PopTextWrapPos();
        var textHeight = ImGui.CalcTextSize(message, false, width).Y;
        ImGui.SetCursorScreenPos(origin + new Vector2(0f, Ui(42f) + textHeight));
        if (ActivePartyButton(isBrowser ? "Start Browser Stream" : "Start Gameplay Stream", UiVec(180f, 36f), primary: true))
        {
            if (isBrowser)
            {
                StartBrowserWatchPartyBroadcast();
                browserStreamOfferDismissed = true;
            }
            else
            {
                currentPage = HomePage.PlaySnes;
                activeGameDialog = "Game broadcast";
                gameplayStreamOfferDismissed = true;
            }
        }
        ImGui.SameLine(0f, Ui(12f));
        if (ActivePartyButton("Not Now##inlineStreamOffer", UiVec(100f, 36f)))
        {
            if (isBrowser) browserStreamOfferDismissed = true;
            else gameplayStreamOfferDismissed = true;
        }
    }

    private void DrawPartyHeaderCard() => DrawActivePartyRoomOverview();

    private void DrawPartyRoomEditForm(
        Vector2 cardPos,
        Vector2 cardSize)
    {
        ImGui.SetCursorScreenPos(
            cardPos +
            UiVec(
                20f,
                53f));

        DrawBrowserHelper("Update the current room without ending the Watch Party.");

        var formOrigin =
            cardPos +
            UiVec(
                20f,
                80f);

        var formWidth =
            cardSize.X -
            Ui(40f);

        ImGui.SetCursorScreenPos(
            formOrigin);

        //
        // Give the editable controls a visible border against the
        // dark Room Details panel.
        //
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.32f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   Ui(1f)))
        {
            DrawCreateRoomFields(
                formWidth, showPrivacyOptions: false);
        }

        // Leave space below the vibe tags before the age restriction.
        var settingsY = MathF.Max(ImGui.GetCursorScreenPos().Y + Ui(12f),
            cardPos.Y + cardSize.Y - Ui(76f));
        ImGui.SetCursorScreenPos(new Vector2(cardPos.X + Ui(20f), settingsY));
        DrawWatchPartyPrivacyOptions(formWidth);

        //
        // Footer controls.
        //
        var buttonWidth =
            Ui(140f);

        var buttonGap =
            Ui(10f);

        var buttonY =
            cardPos.Y +
            cardSize.Y -
            Ui(48f);

        var firstButtonX =
            cardPos.X +
            cardSize.X -
            Ui(20f) -
            buttonWidth * 2f -
            buttonGap;

        if (createRoomKindIndex == 1)
        {
            ImGui.SetCursorScreenPos(
                new Vector2(
                    firstButtonX - buttonGap - Ui(142f),
                    buttonY));

            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameRounding,
                       Ui(7f)))
            using (ImRaii.PushColor(
                       ImGuiCol.Button,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.12f)))
            using (ImRaii.PushColor(
                       ImGuiCol.ButtonHovered,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.25f)))
            using (ImRaii.PushColor(
                       ImGuiCol.ButtonActive,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.36f)))
            using (ImRaii.PushColor(
                       ImGuiCol.Border,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.50f)))
            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameBorderSize,
                       Ui(1f)))
            {
                if (ImGui.Button(
                        "Set new password",
                        new Vector2(
                            Ui(142f),
                            Ui(36f))))
                {
                    createRoomPassword =
                        string.Empty;

                    createLockedRoomPasswordError =
                        null;

                    createLockedRoomPasswordForRoomEdit =
                        true;

                    createLockedRoomPasswordPopupRequested =
                        true;
                }
            }
        }

        ImGui.SetCursorScreenPos(
            new Vector2(
                firstButtonX,
                buttonY));

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   Ui(7f)))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   FrameBg))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   FrameBgHover))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.55f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   Ui(1f)))
        {
            if (ImGui.Button(
                    "Cancel",
                    new Vector2(
                        buttonWidth,
                        Ui(36f))))
            {
                partyRoomEditing =
                    false;
            }
        }

        ImGui.SameLine(
            0f,
            buttonGap);

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   Ui(7f)))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   Accent))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   AccentHover))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonActive,
                   AccentActive))
        {
            if (ImGui.Button(
                    "Save changes",
                    new Vector2(
                        buttonWidth,
                        Ui(36f))))
            {
                RequestSavePartyRoomDetails();
            }
        }
    }

    private void DrawPartyRoomHostAvatar(
        ImDrawListPtr drawList,
        Vector2 avatarOrigin,
        float avatarSize,
        string hostName)
    {
        var drewAvatar =
            false;

        if (stream.Mode ==
                StreamMode.Hosting &&
            CurrentSession is not null)
        {
            DrawAvatarAt(
                avatarOrigin,
                CurrentSession.AvatarIcon,
                CurrentSession.AvatarColorHex,
                avatarSize,
                CurrentSession.AvatarImageUrl);

            drewAvatar =
                true;
        }
        else if (!string.IsNullOrWhiteSpace(
                     stream.HostId))
        {
            EnsurePartyAvatarLoaded(
                stream.HostId,
                hostName);

            if (partyAvatarCache.TryGetValue(
                    stream.HostId,
                    out var avatar))
            {
                DrawAvatarAt(
                    avatarOrigin,
                    avatar.AvatarIcon,
                    avatar.AvatarColorHex,
                    avatarSize,
                    avatar.AvatarImageUrl);

                drewAvatar =
                    true;
            }
        }

        if (drewAvatar)
        {
            return;
        }

        var center =
            avatarOrigin +
            new Vector2(
                avatarSize * 0.5f,
                avatarSize * 0.5f);

        drawList.AddCircleFilled(
            center,
            avatarSize * 0.5f,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.20f)));

        drawList.AddCircle(
            center,
            avatarSize * 0.5f,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.60f)),
            24,
            Ui(1f));

        using (UiFonts.PushIcon())
        {
            var glyph =
                FontAwesomeIcon.User
                    .ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), center -
                glyphSize *
                0.5f, ImGui.GetColorU32(
                    Accent), glyph);
        }
    }

    private void DrawPartyRoomDetailCell(
        string id,
        FontAwesomeIcon icon,
        string label,
        string value,
        Vector2 origin,
        float width,
        float leftInset = 0f)
    {
        origin.X += leftInset;
        width = MathF.Max(Ui(50f), width - leftInset);

        ImGui.PushID(
            id);

        var iconColor =
            label == "Rating" &&
            value.StartsWith(
                "18+",
                StringComparison.Ordinal)
                ? Danger
                : AccentHover;

        var iconText =
            icon.ToIconString();

        Vector2 iconSize;
        using (UiFonts.PushIcon())
        {
            iconSize = ImGui.CalcTextSize(iconText);

            var iconColumnWidth = Ui(20f);
            var iconContentHeight = Ui(42f);
            UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), new Vector2(
                    origin.X +
                    (iconColumnWidth - iconSize.X) * 0.5f,
                    origin.Y +
                    (iconContentHeight - iconSize.Y) * 0.5f), ImGui.GetColorU32(iconColor), iconText);
        }

        var textX =
            origin.X +
            Ui(29f);

        ImGui.SetCursorScreenPos(
            new Vector2(
                textX,
                origin.Y));

        using var uiFontScale48969=UiFonts.PushRelative(0.80f);

        ImGui.TextColored(
            MutedText,
            label);

        uiFontScale48969.Dispose();

        ImGui.SetCursorScreenPos(
            new Vector2(
                textX,
                origin.Y +
                Ui(21f)));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            Math.Max(
                Ui(50f),
                width -
                Ui(29f)));

        ImGui.TextColored(
            Vector4.One,
            value);

        ImGui.PopTextWrapPos();

        if (ImGui.IsItemHovered() &&
            ImGui.CalcTextSize(
                value).X >
            width -
            Ui(29f))
        {
            UiFonts.SetTooltip(
                value);
        }

        ImGui.PopID();
    }

    private void BeginPartyRoomEditing()
    {
        if (stream.Mode !=
            StreamMode.Hosting)
        {
            return;
        }

        ReadActivePartyRoomMetadata(
            out var visibleDescription,
            out var categoryIndex,
            out var adultOnly,
            out _);

        createRoomDescription =
            visibleDescription;

        createRoomLocation =
            stream.RoomLocation ??
            string.Empty;

        createRoomCategoryIndex =
            categoryIndex;

        createRoomAdultOnly =
            adultOnly;
        createRoomPrivate = stream.IsPrivate;
        createRoomPartyVibes.Clear();
        createRoomPartyVibes.UnionWith(PartyVibes.Read(stream.RoomDescription));

        createRoomKindIndex =
            stream.RoomKind switch
            {
                RoomKind.Locked =>
                    1,

                RoomKind.Venue =>
                    2,

                _ =>
                    0,
            };

        createRoomPassword =
            stream.RoomPassword ??
            string.Empty;

        partyRoomEditing =
            true;
    }

    private void RequestSavePartyRoomDetails()
    {
        if (stream.Mode !=
            StreamMode.Hosting)
        {
            partyRoomEditing =
                false;

            return;
        }

        if (createRoomKindIndex == 1 &&
            string.IsNullOrWhiteSpace(
                createRoomPassword))
        {
            createLockedRoomPasswordError =
                null;

            createLockedRoomPasswordForRoomEdit =
                true;

            createLockedRoomPasswordPopupRequested =
                true;

            return;
        }

        SavePartyRoomDetails();
    }

    private void SavePartyRoomDetails()
    {
        if (stream.Mode !=
            StreamMode.Hosting)
        {
            partyRoomEditing =
                false;

            return;
        }

        ApplyCreateRoomToStream();

        _ =
            stream.PublishRoomDetailsAsync();

        partyRoomEditing =
            false;

    }

    private void ReadActivePartyRoomMetadata(
       out string visibleDescription,
       out int categoryIndex,
       out bool adultOnly,
       out string serverName)
    {
        var rawDescription =
            stream.RoomDescription ??
            string.Empty;

        visibleDescription =
            rawDescription.Trim();

        categoryIndex =
            0;

        adultOnly =
            false;

        serverName =
            string.Empty;

        var tokenStart =
            rawDescription.IndexOf(
                "<#",
                StringComparison.Ordinal);

        if (tokenStart < 0)
        {
            return;
        }

        var tokenEnd =
            rawDescription.IndexOf(
                "#>",
                tokenStart + 2,
                StringComparison.Ordinal);

        if (tokenEnd < 0)
        {
            return;
        }

        var tokenContents =
            rawDescription.Substring(
                tokenStart + 2,
                tokenEnd -
                tokenStart -
                2);

        var tokenParts =
            tokenContents.Split(
                '#',
                StringSplitOptions.TrimEntries);

        if (tokenParts.Length < 2)
        {
            return;
        }

        var matchedCategory =
            Array.FindIndex(
                WatchPartyCategoryOptions,
                option =>
                    string.Equals(
                        option,
                        tokenParts[0],
                        StringComparison.OrdinalIgnoreCase));

        if (matchedCategory >= 0)
        {
            categoryIndex =
                matchedCategory;
        }

        adultOnly =
            tokenParts[1] == "1";

        if (tokenParts.Length >= 3)
        {
            serverName =
                tokenParts[2].Trim();
        }

        visibleDescription =
            (
                rawDescription[..tokenStart] +
                rawDescription[(tokenEnd + 2)..]
            ).Trim();
    }

    internal void RefreshHostedRoomWorldMetadata()
    {
        if (stream.HasPreservedDutyScreen) return;
        if (stream.Mode !=
            StreamMode.Hosting)
        {
            return;
        }

        var currentWorld =
            CurrentWorldName?
                .Trim();

        if (string.IsNullOrWhiteSpace(
                currentWorld))
        {
            return;
        }

        ReadActivePartyRoomMetadata(
            out var visibleDescription,
            out var categoryIndex,
            out var adultOnly,
            out var publishedWorld);

        if (string.Equals(
                currentWorld,
                publishedWorld,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var category =
            WatchPartyCategoryOptions[
                Math.Clamp(
                    categoryIndex,
                    0,
                    WatchPartyCategoryOptions.Length - 1)];

        var rating =
            adultOnly
                ? 1
                : 0;

        var metadata =
            PartyVibes.Marker(category, rating, currentWorld, PartyVibes.Read(stream.RoomDescription));

        stream.RoomDescription =
            string.IsNullOrWhiteSpace(
                visibleDescription)
                ? metadata
                : $"{metadata} {visibleDescription.Trim()}";

        _ =
            stream.PublishRoomDetailsAsync();
    }

    private static string PartyRoomCategoryName(
        int categoryIndex)
    {
        return WatchPartyCategoryOptions[
            Math.Clamp(
                categoryIndex,
                0,
                WatchPartyCategoryOptions.Length - 1)];
    }

    private static FontAwesomeIcon PartyRoomCategoryIcon(
        int categoryIndex)
    {
        return categoryIndex switch
        {
            0 =>
                FontAwesomeIcon.PlayCircle,

            1 =>
                FontAwesomeIcon.Film,

            2 =>
                FontAwesomeIcon.Tv,

            3 =>
                FontAwesomeIcon.CommentDots,

            4 =>
                FontAwesomeIcon.Smile,

            5 =>
                FontAwesomeIcon.BroadcastTower,

            6 =>
                FontAwesomeIcon.Gamepad,

            7 =>
                FontAwesomeIcon.Headphones,

            8 =>
                FontAwesomeIcon.Music,

            9 =>
                FontAwesomeIcon.Images,

            10 =>
                FontAwesomeIcon.Bullhorn,

            11 =>
                FontAwesomeIcon.Dragon,

            12 =>
                FontAwesomeIcon.Ghost,

            13 =>
                FontAwesomeIcon.Globe,

            14 =>
                FontAwesomeIcon.Forward,

            15 =>
                FontAwesomeIcon.UserFriends,

            16 =>
                FontAwesomeIcon.TicketAlt,

            17 =>
                FontAwesomeIcon.GlassCheers,

            18 =>
                FontAwesomeIcon.Ghost,

            _ =>
                FontAwesomeIcon.Video,
        };
    }

    private void DrawPartyTabButtons()
    {
        var width =
            ImGui.GetContentRegionAvail().X;

        var gap = Ui(8f);

        var tabWidth =
            (width -
             gap * 2f) /
            3f;

        // Host is separate from stream.Roster.
        var participantCount =
            stream.Roster.Length + 1;
        var compact = width < Ui(650f);

        DrawPartyTabButton(
            PartyPanelTab.Chat,
            FontAwesomeIcon.Comments,
            partyChatUnreadCount > 0
                ? $"{(compact ? "Chat" : "Party chat & react")} ({Math.Min(partyChatUnreadCount, 99)}{(partyChatMentionUnreadCount > 0 ? " @" : "")})"
                : compact ? "Chat & react" : "Party chat & react",
            tabWidth);

        ImGui.SameLine(
            0f,
            gap);

        DrawPartyTabButton(
            PartyPanelTab.Watching,
            FontAwesomeIcon.UserFriends,
            compact ? $"Members ({participantCount})" : $"Watch Party ({participantCount})",
            tabWidth);

        ImGui.SameLine(
            0f,
            gap);

        DrawPartyTabButton(
            PartyPanelTab.NowPlaying,
            FontAwesomeIcon.Tv,
            "Now Playing & Queue",
            tabWidth);
    }

    private void DrawPartyTabButton(
     PartyPanelTab tab,
     FontAwesomeIcon icon,
     string label,
     float width)
    {
        var selected =
            partyPanelTab == tab;

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f)
            .Push(
                ImGuiStyleVar.FramePadding,
                UiVec(12f, 9f)))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            selected
                ? Accent
                : FrameBg)
            .Push(
                ImGuiCol.ButtonHovered,
                selected
                    ? AccentHover
                    : FrameBgHover)
            .Push(
                ImGuiCol.ButtonActive,
                selected
                    ? AccentActive
                    : AccentFieldActive))
        {
            //
            // Draw an invisible-label button first.
            //
            if (ImGui.Button(
                $"##partyTab_{tab}",
                new Vector2(width, Ui(40f))))
            {
                if (tab == PartyPanelTab.Chat && partyPanelTab != PartyPanelTab.Chat)
                    partyChatWasAtBottom = false;
                partyPanelTab = tab;
            }

            var buttonMin =
                ImGui.GetItemRectMin();

            var buttonMax =
                ImGui.GetItemRectMax();

            var centerY =
                buttonMin.Y +
                (buttonMax.Y - buttonMin.Y) * 0.5f;

            var iconText =
                icon.ToIconString();

            Vector2 iconSize;

            //
            // Measure icon using the actual icon font.
            //
            using (UiFonts.PushIcon())
            {
                iconSize =
                    ImGui.CalcTextSize(
                        iconText);
            }

            //
            // Measure label using the normal font.
            //
            Vector2 labelSize;

            label = TruncateToWidth(label, MathF.Max(Ui(40f), width - iconSize.X - NativeUi(8f) - Ui(20f)));
            labelSize = ImGui.CalcTextSize(label);

            var gap = NativeUi(8f);

            var totalWidth =
                iconSize.X +
                gap +
                labelSize.X;

            var startX =
                buttonMin.X +
                ((buttonMax.X - buttonMin.X) - totalWidth) * 0.5f;

            //
            // Icon
            //
            UiFonts.DrawIcon(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), new Vector2(
                    startX,
                    centerY - iconSize.Y * 0.5f), ImGui.GetColorU32(
                    Vector4.One), iconText);

            //
            // Normal label
            //
            UiFonts.DrawText(ImGui.GetWindowDrawList(),
                ImGui.GetFontSize(),
                new Vector2(
                    startX + iconSize.X + gap,
                    centerY - labelSize.Y * 0.5f),
                ImGui.GetColorU32(
                    Vector4.One),
                label);
        }
    }

    private static bool IsFfxivSoundMuted()
    {
        return Plugin.GameConfig.TryGet(
                   SystemConfigOption.IsSndMaster,
                   out uint muted)
               && muted != 0;
    }

    private static void SetFfxivSoundMuted(
        bool muted)
    {
        Plugin.GameConfig.Set(
            SystemConfigOption.IsSndMaster,
            muted ? 1u : 0u);
    }

    private void DrawPartyTvSpawnButton(float width = 0f, float height = 42f)
    {
        if (stream.Mode is not
            (StreamMode.Viewing or StreamMode.Hosting))
        {
            return;
        }

        var isHost =
            stream.Mode == StreamMode.Hosting;

        var tvSpawned =
            isHost
                ? screenController.Engine.IsActive
                : ViewerTvEnabled;

        var label =
            tvSpawned
                ? "Despawn TV"
                : "Spawn TV";

        var buttonColor =
            tvSpawned
                ? FrameBg
                : Accent;

        var buttonHover =
            tvSpawned
                ? FrameBgHover
                : AccentHover;

        var buttonActive =
            tvSpawned
                ? AccentActive
                : AccentActive;

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            9f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            buttonColor)
            .Push(
                ImGuiCol.ButtonHovered,
                buttonHover)
            .Push(
                ImGuiCol.ButtonActive,
                buttonActive))
        {
            if (!ImGui.Button(
                    label,
                    new Vector2(
                        width > 0f ? width : Ui(210f),
                        Ui(height))))
            {
                return;
            }

            // =====================================================
            // VIEWER
            // =====================================================

            if (!isHost)
            {
                if (ViewerTvEnabled)
                {
                    DespawnViewerTv(
                        stopPlayback:
                            !(IsMiniPlayerOpen?.Invoke() ?? false));
                }
                else
                {
                    RequestViewerTvSpawn();
                }

                return;
            }

            // =====================================================
            // HOST
            // =====================================================

            var engine =
                screenController.Engine;

            if (!engine.IsActive)
            {
                engine.RespawnScreen();
                return;
            }

            engine.DespawnScreen();
        }
    }

    private void DrawPartyNowPlayingTab()
    {
        var current =
       queue.Current;

        var roomState =
            stream.CurrentRoomState;

        //
        // Gameplay and local-video broadcasts do not belong to the saved
        // video queue. Build a temporary display entry from stream.state so
        // the Watch Party page can still describe what is being shared.
        //

        if (roomState is
            {
                Url: { Length: > 0 } roomUrl
            } &&
       (
           stream.Mode == StreamMode.Viewing ||
           current is null ||
           !AudioVisualizerSelection.IsSameMedia(
               current.Url,
               roomUrl)
       ))
        {
            current = CreatePartyMediaDisplayEntry(roomUrl, roomState.MediaTitle ?? string.Empty, roomState.MediaThumbnailUrl);
        }

        var (position, duration, isPaused) =
            video.GetProgress();

        current = WithStudioPlaybackDisplay(current);
        DrawActivePartyPlaybackLayout(current, position, duration, isPaused);
    }

    private Video.VideoQueueEntry CreatePartyMediaDisplayEntry(string roomUrl, string roomTitle,
        string? thumbnailUrl)
    {
        var source =
                IsWhiteboardUrl(roomUrl)
                    ? PartyEntryCanvasActivity(roomUrl, roomTitle) == ActiveActivityKind.Whiteboard ? "Canvas" : "Arcade"
                    : VideoUrlResolver.IsYouTubeUrl(roomUrl)
                    ? "YouTube"
                    : roomTitle.StartsWith(
                    "Local Video",
                    StringComparison.OrdinalIgnoreCase)
                    ? "Local Video"
                    : roomTitle.StartsWith(
                        "Playing:",
                        StringComparison.OrdinalIgnoreCase) ||
                      roomTitle.EndsWith(
                          "Gameplay",
                          StringComparison.OrdinalIgnoreCase)
                        ? "Gameplay"
                        : string.Equals(
                            roomTitle,
                            "Live Streaming",
                            StringComparison.OrdinalIgnoreCase)
                            ? "Live Stream"
                            : Plugin.IsLiveBroadcastUrl(roomUrl) || roomTitle.Equals("Live DJ broadcast", StringComparison.OrdinalIgnoreCase)
                                ? "Broadcast"
                                : video.IsAudioOnly ? "Radio" : "Video";

        return new Video.VideoQueueEntry(roomUrl,
            WatchPartyMediaDisplay.Title(roomUrl, roomTitle, source == "Radio"), source, null, thumbnailUrl);
    }



    private void DrawPartyPlaybackSyncPanel(
        Vector2 origin,
        float width)
    {
        ImGui.SetCursorScreenPos(origin);
        ImGui.BeginGroup();
        using var uiFontScale71524=UiFonts.PushRelative(0.80f);
        ImGui.TextColored(Accent, "Playback sync");
        uiFontScale71524.Dispose();
        ImGui.Dummy(UiVec(0f, 10f));
        DrawPartyStatusLine(
            stream.Mode == StreamMode.Hosting
                ? "You're hosting playback"
                : $"Following {joinedHostDisplayName ?? "the host"}",
            Good);
        ImGui.Dummy(UiVec(0f, 4f));
        using var uiFontScale71926=UiFonts.PushRelative(0.76f);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        ImGui.TextColored(
            MutedText,
            stream.Mode == StreamMode.Hosting
                ? "Everyone follows your playback."
                : "You're synced with the host.");
        ImGui.PopTextWrapPos();
        uiFontScale71926.Dispose();
        ImGui.Dummy(UiVec(0f, 17f));
        if (stream.Mode == StreamMode.Viewing)
            DrawPartyResyncButton(width);
        ImGui.EndGroup();
    }

    private (string Status, Vector4 Color) HostPartyBroadcastStatus(BroadcastAllowanceMethod method)
    {
        if (method == BroadcastAllowanceMethod.None) return ("Off Air", MutedText);
        var engine = screenController.Engine;
        var isRunning = method switch
        {
            BroadcastAllowanceMethod.LocalVideo =>
                engine.IsLocalVideoBroadcasting,
            BroadcastAllowanceMethod.Browser =>
                engine.IsBrowserBroadcasting,
            BroadcastAllowanceMethod.RetroGame =>
                engine.IsSnesBroadcasting ||
                engine.IsGameBoyBroadcasting ||
                engine.IsNesBroadcasting ||
                engine.IsGameBoyAdvanceBroadcasting ||
                engine.IsMasterSystemBroadcasting ||
                engine.IsGameGearBroadcasting,
            BroadcastAllowanceMethod.DjLive =>
                djBroadcastingToWatchParty && djIsOnAir,
            BroadcastAllowanceMethod.Obs =>
                queue.Current is { IsTransient: true } current &&
                string.Equals(
                    current.Source,
                    "Live Stream",
                    StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        var failed = method switch
        {
            BroadcastAllowanceMethod.LocalVideo =>
                localVideoBroadcastStartFailed,
            BroadcastAllowanceMethod.Browser =>
                browserBroadcastStartFailed,
            BroadcastAllowanceMethod.RetroGame =>
                gameBroadcastStartFailed,
            _ => false,
        };
        var status = failed
            ? "Connection issue"
            : isRunning
                ? "Broadcasting Live"
                : stream.Roster.Length == 0
                    ? "Awaiting Viewers"
                    : "Starting broadcast…";
        var statusColor = failed
            ? Danger
            : isRunning
                ? Good
                : Gold;
        return (status, statusColor);
    }

    private void DrawHostBroadcastSyncPanel(Vector2 origin, float width, BroadcastAllowanceMethod method)
    {
        var (status, statusColor) = HostPartyBroadcastStatus(method);
        ImGui.SetCursorScreenPos(origin);
        ImGui.BeginGroup();
        using var uiFontScale74777=UiFonts.PushRelative(0.80f);
        ImGui.TextColored(Accent, "BROADCAST & SYNC");
        uiFontScale74777.Dispose();
        ImGui.Dummy(UiVec(0f, 8f));
        DrawPartyStatusLine(status, statusColor);
        ImGui.Dummy(UiVec(0f, 2f));
        using var uiFontScale75019=UiFonts.PushRelative(0.74f);
        ImGui.TextColored(MutedText, FitPartyTextToWidth(HostBroadcastDescription(method), width));
        uiFontScale75019.Dispose();
        ImGui.EndGroup();
    }

    private void DrawViewerBroadcastSyncPanel(
        Vector2 origin,
        float width,
        Video.VideoQueueEntry current)
    {
        var ledgerBlocked = BroadcastLedger.ViewerBlocked;
        var receivingStatus = video.State switch
        {
            Video.VideoPlaybackState.Failed => "Connection interrupted",
            Video.VideoPlaybackState.Idle or
            Video.VideoPlaybackState.Loading => "Connecting to broadcast",
            _ => "Receiving broadcast",
        };
        var receivingColor = video.State == Video.VideoPlaybackState.Failed
            ? Danger
            : video.State is Video.VideoPlaybackState.Idle or
                Video.VideoPlaybackState.Loading
                ? Gold
                : Good;
        if (ledgerBlocked)
        {
            receivingStatus = "Verification paused playback";
            receivingColor = Danger;
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.BeginGroup();
        using var uiFontScale76215=UiFonts.PushRelative(0.80f);
        ImGui.TextColored(Accent, "BROADCAST & SYNC");
        uiFontScale76215.Dispose();
        ImGui.Dummy(UiVec(0f, 10f));
        DrawPartyStatusLine(receivingStatus, receivingColor);
        ImGui.Dummy(UiVec(0f, 3f));
        using var uiFontScale76470=UiFonts.PushRelative(0.75f);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        DrawUsername($"{ViewerBroadcastDescription(current)} from {joinedHostDisplayName ?? "host"}",MutedText);
        ImGui.PopTextWrapPos();
        uiFontScale76470.Dispose();
        ImGui.Dummy(UiVec(0f, 14f));
        DrawPartyStatusLine(
            ledgerBlocked ? "Playback unavailable" :
            video.State == Video.VideoPlaybackState.Failed
                ? "Playback unavailable" : "Playback synced",
            ledgerBlocked || video.State == Video.VideoPlaybackState.Failed
                ? Danger : Good);
        ImGui.Dummy(UiVec(0f, 3f));
        using var uiFontScale77135=UiFonts.PushRelative(0.75f);
        ImGui.TextColored(MutedText, ledgerBlocked
            ? BroadcastLedger.ViewerMessage : "Following the host's playback.");
        uiFontScale77135.Dispose();
        ImGui.Dummy(UiVec(0f, 14f));
        if (ledgerBlocked)
        {
            if (ImGui.Button("Attempt to fix?##broadcastLedgerRetry", new Vector2(width, Ui(36f))))
                BroadcastLedger.RetryViewer();
        }
        else
        {
            DrawPartyResyncButton(width);
        }
        ImGui.EndGroup();
    }

    private void DrawPartyStatusLine(string text, Vector4 color)
    {
        ImGui.TextColored(color, "●");
        ImGui.SameLine(0f, Ui(7f));
        using var uiFontScale77832=UiFonts.PushRelative(1.03f);
        ImGui.TextColored(Vector4.One, text);
        uiFontScale77832.Dispose();
    }

    private void DrawPartyResyncButton(float width)
    {
        using (UiDisabled.Scope(stream.Mode != StreamMode.Viewing ||
            !(ViewerTvEnabled || screenController.Engine.AudioOnlyMode || (IsMiniPlayerOpen?.Invoke() ?? false))))
        {
            if (ImGui.Button(
                    "Re-sync##partyResync",
                    new Vector2(width, Ui(36f))))
            {
                OnPartyPlaybackResyncRequested?.Invoke();
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            UiFonts.SetTooltip(
                stream.Mode == StreamMode.Hosting
                    ? "The host is the playback source."
                    : "Video out of sync with host? Press here to attempt a re-sync");
        }
    }

    private static bool IsBroadcastPartyMedia(
        Video.VideoQueueEntry? current)
    {
        if (current is null)
        {
            return false;
        }

        return current.Source.Equals("Gameplay", StringComparison.OrdinalIgnoreCase) ||
               current.Source.Equals("Local Video", StringComparison.OrdinalIgnoreCase) ||
               current.Source.Equals("Live Stream", StringComparison.OrdinalIgnoreCase) ||
               current.Source.Equals("Broadcast", StringComparison.OrdinalIgnoreCase) ||
               current.Title.Equals("Live DJ broadcast", StringComparison.OrdinalIgnoreCase);
    }

    private static string ViewerBroadcastDescription(
        Video.VideoQueueEntry current)
    {
        if (current.Source.Equals("Gameplay", StringComparison.OrdinalIgnoreCase))
            return "Retro games";
        if (current.Source.Equals("Local Video", StringComparison.OrdinalIgnoreCase))
            return "Local video";
        if (current.Source.Equals("Live Stream", StringComparison.OrdinalIgnoreCase))
            return "PC stream";
        if (current.Title.Equals("Live DJ broadcast", StringComparison.OrdinalIgnoreCase))
            return "DJ Live";
        return "Live broadcast";
    }

    private string HostBroadcastDescription(
        BroadcastAllowanceMethod method)
    {
        var tier = ConfiguredPatreonTier();
        return method switch
        {
            BroadcastAllowanceMethod.RetroGame =>
                $"Retro games · {(tier >= 2 && Plugin.Cfg.GameBroadcastQuality == Video.GameBroadcastQuality.StandardNative ? "Standard Native Quality" : "Data Saver")}",
            BroadcastAllowanceMethod.Browser =>
                $"Browser · {Plugin.Cfg.BrowserBroadcastQuality.ForPatreonTier(tier) switch { Video.BrowserBroadcastQuality.High => "High Quality", Video.BrowserBroadcastQuality.Standard => "Standard Quality", _ => "Data Saver" }}",
            BroadcastAllowanceMethod.LocalVideo =>
                $"Local video · {tier switch { >= 3 => Plugin.Cfg.LocalVideoBroadcastQuality switch { Video.LocalVideoBroadcastQuality.High => "High", Video.LocalVideoBroadcastQuality.Standard => "Standard", _ => "Data Saver" }, >= 2 when Plugin.Cfg.LocalVideoBroadcastQuality != Video.LocalVideoBroadcastQuality.DataSaver => "Standard", _ => "Data Saver" }}",
            BroadcastAllowanceMethod.DjLive => "DJ Live",
            BroadcastAllowanceMethod.Obs => "PC streaming",
            _ => "Broadcast",
        };
    }

    private static string FormatBroadcastTimeRemaining(double minutes)
    {
        if (!double.IsFinite(minutes) || minutes <= 0d)
        {
            return "0m";
        }

        var roundedMinutes = (long)Math.Floor(minutes);
        var hours = roundedMinutes / 60;
        var remainder = roundedMinutes % 60;
        return hours > 0
            ? $"{hours}h {remainder}m"
            : $"{remainder}m";
    }

    private static void DrawPartyAllowanceBar(float width, float remaining)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, Ui(10f));
        var fillWidth = size.X * Math.Clamp(remaining, 0f, 1f);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(CardBg),
            Ui(5f));
        if (fillWidth > 0f)
        {
            var fillMax = origin + new Vector2(fillWidth, size.Y);
            draw.AddRectFilled(
                origin,
                fillMax,
                ImGui.GetColorU32(Accent),
                Ui(5f));
            draw.PushClipRect(origin, fillMax, true);
            for (var x = origin.X - size.Y;
                 x < fillMax.X + size.Y;
                 x += Ui(10f))
            {
                draw.AddLine(
                    new Vector2(x, origin.Y + size.Y),
                    new Vector2(x + size.Y, origin.Y),
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.16f)),
                    Ui(3f));
            }
            draw.PopClipRect();
        }
        draw.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(BorderSubtle),
            Ui(5f));
        ImGui.Dummy(size);
    }



    private static bool IsPartyLiveMedia(
        Video.VideoQueueEntry current)
    {
        if (current.IsTransient)
        {
            return true;
        }

        return current.Source.Equals(
                   "Radio",
                   StringComparison.OrdinalIgnoreCase) ||
               current.Source.Equals(
                   "Broadcast",
                   StringComparison.OrdinalIgnoreCase) ||
               current.Source.Equals(
                   "Live Stream",
                   StringComparison.OrdinalIgnoreCase) ||
               current.Source.Equals(
                   "Gameplay",
                   StringComparison.OrdinalIgnoreCase) ||
               current.Title.Contains(
                   "Live DJ",
                   StringComparison.OrdinalIgnoreCase);
    }



    private static string FitPartyTextToWidth(
        string text,
        float maximumWidth)
    {
        if (string.IsNullOrEmpty(text) ||
            ImGui.CalcTextSize(text).X <= maximumWidth)
        {
            return text;
        }

        const string ellipsis = "…";

        var low = 0;
        var high = text.Length;

        while (low < high)
        {
            var middle =
                (low + high + 1) / 2;

            var candidate =
                text[..middle].TrimEnd() +
                ellipsis;

            if (ImGui.CalcTextSize(candidate).X <= maximumWidth)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return text[..low].TrimEnd() + ellipsis;
    }



    private void DrawPartyScreenControllerAudio(
        float x,
        float y,
        float width)
    {
        var volume =
            Plugin.Cfg.Volume;

        ImGui.SetCursorScreenPos(
            new Vector2(
                x,
                y + Ui(2f)));

        ImGui.TextColored(
            Vector4.One,
            "TV Volume");

        var percent =
            $"{volume}%";

        var percentSize =
            ImGui.CalcTextSize(percent);

        ImGui.SetCursorScreenPos(
            new Vector2(
                x + width - percentSize.X,
                y + Ui(2f)));

        ImGui.TextColored(
            volume > 100
                ? Gold
                : Vector4.One,
            percent);

        ImGui.SetCursorScreenPos(
            new Vector2(
                x,
                y + Ui(28f)));

        ImGui.SetNextItemWidth(width);

        if (ImGui.SliderInt(
                "##partyControllerVolume",
                ref volume,
                0,
                130,
                ""))
        {
            Plugin.Cfg.Volume = volume;
            if (volume > 0) Plugin.Cfg.Muted = false;
            ApplyListeningVolume();
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            Plugin.Cfg.Save();
        }

        ImGui.SetCursorScreenPos(
            new Vector2(
                x,
                y + Ui(67f)));

        var muted =
            Plugin.Cfg.Muted;

        var buttonGap = Ui(8f);
        var buttonWidth = (width - buttonGap) * 0.5f;

        var ffxivButtonWidth = buttonWidth;

        if (ActivePartyButton(
                muted
                    ? "Unmute TV"
                    : "Mute TV",
                new Vector2(
                    buttonWidth,
                    Ui(36f)), primary: muted))
        {
            muted =
                !muted;

            Plugin.Cfg.Muted =
                muted;

            ApplyListeningVolume();

            Plugin.Cfg.Save();
        }

        ImGui.SameLine(0f, buttonGap);

        var ffxivMuted =
            IsFfxivSoundMuted();

        if (ActivePartyButton(
                ffxivMuted
                    ? "Unmute FFXIV Sounds"
                    : "Mute FFXIV sounds",
                new Vector2(
                    ffxivButtonWidth,
                    Ui(36f)), primary: ffxivMuted))
        {
            SetFfxivSoundMuted(
                !ffxivMuted);
        }
    }



    private void DrawPartyTransportPlayPauseButton(
     string id,
     float size,
     bool disabled,
     bool isPaused,
     bool rectangular = false)
    {
        using (UiDisabled.Scope(
            disabled || video.ManualTransportLocked))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            rectangular ? Ui(7f) : size * 0.5f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            FrameBg)
            .Push(
                ImGuiCol.ButtonHovered,
                Accent)
            .Push(
                ImGuiCol.ButtonActive,
                AccentActive))
        {
            if (ImGui.Button($"##{id}", new Vector2(size, size)))
            {
                video.Pause(!isPaused);
                queue.SaveCurrentProgress();
            }
        }

        var min =
            ImGui.GetItemRectMin();

        var max =
            ImGui.GetItemRectMax();

        var center =
            min +
            (max - min) *
            0.5f;

        var drawList =
            ImGui.GetWindowDrawList();

        var color =
            ImGui.GetColorU32(
                disabled
                    ? MutedText
                    : Vector4.One);

        if (isPaused)
        {
            // Play triangle.
            drawList.AddTriangleFilled(
                new Vector2(
                    center.X - Ui(4f),
                    center.Y - Ui(6f)),
                new Vector2(
                    center.X - Ui(4f),
                    center.Y + Ui(6f)),
                new Vector2(
                    center.X + Ui(6f),
                    center.Y),
                color);
        }
        else
        {
            // Pause bars.
            drawList.AddRectFilled(
                new Vector2(
                    center.X - Ui(5f),
                    center.Y - Ui(6f)),
                new Vector2(
                    center.X - Ui(1.5f),
                    center.Y + Ui(6f)),
                color,
                1f);

            drawList.AddRectFilled(
                new Vector2(
                    center.X + Ui(1.5f),
                    center.Y - Ui(6f)),
                new Vector2(
                    center.X + Ui(5f),
                    center.Y + Ui(6f)),
                color,
                1f);
        }

        if (ImGui.IsItemHovered(
                ImGuiHoveredFlags.AllowWhenDisabled))
        {
            var tooltip =
                isPaused
                    ? "Play"
                    : "Pause";

            UiFonts.SetTooltip(
                disabled
                    ? stream.Mode == StreamMode.Viewing
                        ? $"{tooltip} — controlled by the host"
                        : $"{tooltip} is unavailable for the current playback."
                    : tooltip);
        }
    }

    private void DrawPartyTransportRestartButton(
        string id,
        float size,
        bool disabled,
        string tooltip,
        bool rectangular = false)
    {
        using (UiDisabled.Scope(
            disabled || video.ManualTransportLocked))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            rectangular ? Ui(7f) : size * 0.5f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            FrameBg)
            .Push(
                ImGuiCol.ButtonHovered,
                Accent)
            .Push(
                ImGuiCol.ButtonActive,
                AccentActive))
        {
            if (ImGui.Button($"##{id}", new Vector2(size, size)))
            {
                if (!TryStudioSongTransport(next: false))
                {
                    video.Seek(0f);
                    queue.SaveCurrentProgress();
                }
            }
        }

        var min =
            ImGui.GetItemRectMin();

        var max =
            ImGui.GetItemRectMax();

        var center =
            min +
            (max - min) *
            0.5f;

        var drawList =
            ImGui.GetWindowDrawList();

        var color =
            ImGui.GetColorU32(
                disabled
                    ? MutedText
                    : Vector4.One);

        // ---------------------------------------------------------
        // Restart icon:
        //
        // |◀
        //
        // Simple and unmistakable:
        // jump back to the beginning.
        // ---------------------------------------------------------

        drawList.AddLine(
            new Vector2(
                center.X - Ui(5f),
                center.Y - Ui(6f)),
            new Vector2(
                center.X - Ui(5f),
                center.Y + Ui(6f)),
            color,
            2f);

        drawList.AddTriangleFilled(
            new Vector2(
                center.X + Ui(5f),
                center.Y - Ui(6f)),
            new Vector2(
                center.X + Ui(5f),
                center.Y + Ui(6f)),
            new Vector2(
                center.X - Ui(3f),
                center.Y),
            color);

        if (ImGui.IsItemHovered(
                ImGuiHoveredFlags.AllowWhenDisabled))
        {
            UiFonts.SetTooltip(
                disabled
                    ? $"{tooltip} — controlled by the host"
                    : tooltip);
        }
    }

    private void DrawPartyTransportNextButton(
        string id,
        float size,
        bool disabled,
        string tooltip,
        bool rectangular = false)
    {
        using (UiDisabled.Scope(
            disabled || video.ManualTransportLocked))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            rectangular ? Ui(7f) : size * 0.5f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            FrameBg)
            .Push(
                ImGuiCol.ButtonHovered,
                Accent)
            .Push(
                ImGuiCol.ButtonActive,
                AccentActive))
        {
            if (ImGui.Button($"##{id}", new Vector2(size, size)))
                if (!TryStudioSongTransport(next: true)) queue.Advance();
        }

        var min =
            ImGui.GetItemRectMin();

        var max =
            ImGui.GetItemRectMax();

        var drawList =
            ImGui.GetWindowDrawList();

        var center =
            min +
            (max - min) *
            0.5f;

        var color =
            ImGui.GetColorU32(
                disabled
                    ? MutedText
                    : Vector4.One);

        // Draw a proper "next track" icon manually:
        // triangle + vertical stop line.
        var triangleHalfHeight = Ui(5f);
        var triangleWidth = Ui(7f);

        var triangleCenter =
            new Vector2(
                center.X - Ui(2f),
                center.Y);

        drawList.AddTriangleFilled(
            new Vector2(
                triangleCenter.X -
                triangleWidth * 0.5f,
                triangleCenter.Y -
                triangleHalfHeight),
            new Vector2(
                triangleCenter.X -
                triangleWidth * 0.5f,
                triangleCenter.Y +
                triangleHalfHeight),
            new Vector2(
                triangleCenter.X +
                triangleWidth * 0.5f,
                triangleCenter.Y),
            color);

        drawList.AddLine(
            new Vector2(
                center.X + Ui(5f),
                center.Y - Ui(5f)),
            new Vector2(
                center.X + Ui(5f),
                center.Y + Ui(5f)),
            color,
            2f);

        if (ImGui.IsItemHovered(
                ImGuiHoveredFlags.AllowWhenDisabled))
        {
            UiFonts.SetTooltip(
                disabled
                    ? $"{tooltip} — controlled by the host"
                    : tooltip);
        }
    }

    private void DrawPartyPlacementToggle(
        ref bool value,
        bool disabled)
    {
        var width = Ui(38f);
        var height = Ui(20f);

        var origin =
            ImGui.GetCursorScreenPos();

        using (UiDisabled.Scope(
            disabled || video.ManualTransportLocked))
        {
            if (ImGui.InvisibleButton(
                    "##partyPlacementToggle",
                    new Vector2(
                        width,
                        height)))
            {
                value =
                    !value;
            }
        }

        if (disabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            UiFonts.SetTooltip("Can not be changed as host");
        }

        var drawList =
            ImGui.GetWindowDrawList();

        var fill =
            value
                ? new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    disabled
                        ? 0.45f
                        : 1f)
                : CardBg;

        drawList.AddRectFilled(
            origin,
            origin +
            new Vector2(
                width,
                height),
            ImGui.GetColorU32(
                fill),
            height * 0.5f);

        var knobX =
            value
                ? origin.X +
                  width -
                  height * 0.5f
                : origin.X +
                  height * 0.5f;

        drawList.AddCircleFilled(
            new Vector2(
                knobX,
                origin.Y +
                height * 0.5f),
            7f,
            ImGui.GetColorU32(
                disabled
                    ? new Vector4(
                        0.75f,
                        0.75f,
                        0.78f,
                        1f)
                    : Vector4.One));
    }

    private void DrawPartyChatTab()
    {
        if (stream.WaitingForHost) ImGui.TextColored(Gold, "Host disconnected. Waiting for them to resume the party...");
        using var spacing=ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing,UiVec(8,8));
        if (DateTime.UtcNow-partyChatLastDrawUtc > TimeSpan.FromSeconds(1)) partyChatWasAtBottom=false;
        partyChatLastDrawUtc=DateTime.UtcNow;
        var size=ImGui.GetContentRegionAvail();
        var toolbarHeight=Ui(size.X<Ui(620)?108:62);
        var composerHeight=Ui(partyChatReplyTarget is null?76:102);
        DrawPartyChatToolbar(size.X);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding,Ui(12)).Push(ImGuiStyleVar.WindowPadding,UiVec(16,14)))
            DrawPartyChatFeedModern(size.X,MathF.Max(Ui(120),size.Y-toolbarHeight-composerHeight-Ui(24)));
        DrawPartyChatComposer(alphaStyle:true);
    }

    private void DrawMiniChatPopoutButton()
    {
        var buttonSize = Ui(36f);
        var edgeInset = Ui(12f);
        var scrollbarInset = ImGui.GetStyle().ScrollbarSize;
        var panelPosition = ImGui.GetWindowPos();
        var panelSize = ImGui.GetWindowSize();

        ImGui.SetCursorScreenPos(
            new Vector2(
                panelPosition.X +
                panelSize.X -
                buttonSize -
                edgeInset -
                scrollbarInset,
                panelPosition.Y +
                edgeInset));

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            Ui(8f))
            .Push(
                ImGuiStyleVar.FrameBorderSize,
                0f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            FrameBg)
            .Push(
                ImGuiCol.ButtonHovered,
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.24f))
            .Push(
                ImGuiCol.ButtonActive,
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.34f)))
        using (UiFonts.PushIcon())
        {
            if (ImGui.Button(
                $"{FontAwesomeIcon.ExternalLinkAlt.ToIconString()}##openMiniPartyChat",
                new Vector2(
                    buttonSize,
                    buttonSize)))
            {
                OnMiniChatRequested?.Invoke();
            }
        }

        if (ImGui.IsItemHovered())
        {
            UiFonts.SetTooltip(
                "Open Mini Chat");
        }
    }



    private void ApplySavedViewerScreenPlacement()
    {
        screenController.Engine.SetScreenTransform(
            Plugin.Cfg.ScreenPosition,
            Plugin.Cfg.ScreenYaw,
            Plugin.Cfg.DisableFixedScreenScaleRatio,
            Plugin.Cfg.ScreenWidthScale,
            Plugin.Cfg.ScreenHeightScale);
    }

    private void DrawPartyWatchingTab() => DrawActivePartyMembers();

    private void DrawPartyMemberRow(
        string userId,
        string displayName,
        bool isHost,
        bool showManagementActions,
        Action? onMakeHost,
        float width = -1f)
    {
        var rowHeight = Ui(76f);
        var avatarSize = Ui(48f);
        var leftPadding = Ui(12f);
        var contentGap = Ui(12f);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            9f))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildBorderSize,
            Ui(1f)))
        using (ImRaii.PushColor(
            ImGuiCol.Border,
            new Vector4(
                Accent.X,
                Accent.Y,
                Accent.Z,
                0.20f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            CardBg))
        using (var row =
            UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                "##partyMemberRow",
                new Vector2(
                    width,
                    rowHeight),
                true,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (!row)
            {
                return;
            }

            var origin =
                ImGui.GetWindowPos();

            var rowWidth =
                ImGui.GetWindowWidth();

            var drawList =
                ImGui.GetWindowDrawList();

            // =====================================================
            // AVATAR
            // =====================================================

            var avatarMin =
                origin +
                new Vector2(
                    leftPadding,
                    (rowHeight -
                     avatarSize) *
                    0.5f);

            var drewAvatar =
                false;

            // -----------------------------------------------------
            // Local host:
            // don't round-trip to the server for our own row.
            // -----------------------------------------------------

            if (isHost &&
                stream.Mode ==
                StreamMode.Hosting &&
                CurrentSession is not null)
            {
                DrawAvatarAt(
                    avatarMin,
                    CurrentSession.AvatarIcon,
                    CurrentSession.AvatarColorHex,
                    avatarSize,
                    CurrentSession.AvatarImageUrl);

                drewAvatar =
                    true;
            }
            else
            {
                // -------------------------------------------------
                // Everyone else:
                // exact same server-avatar route used by Party Chat.
                // -------------------------------------------------

                if (!string.IsNullOrWhiteSpace(
                        userId))
                {
                    EnsurePartyAvatarLoaded(
                        userId,
                        displayName);

                    if (partyAvatarCache.TryGetValue(
                            userId,
                            out var avatar))
                    {
                        DrawAvatarAt(
                            avatarMin,
                            avatar.AvatarIcon,
                            avatar.AvatarColorHex,
                            avatarSize,
                            avatar.AvatarImageUrl);

                        drewAvatar =
                            true;
                    }
                }
            }

            // -----------------------------------------------------
            // Fallback user icon while the network avatar is loading.
            // -----------------------------------------------------

            if (!drewAvatar)
            {
                var avatarCenter =
                    avatarMin +
                    new Vector2(
                        avatarSize * 0.5f,
                        avatarSize * 0.5f);

                drawList.AddCircleFilled(
                    avatarCenter,
                    avatarSize * 0.5f,
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.18f)));

                drawList.AddCircle(
                    avatarCenter,
                    avatarSize * 0.5f,
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.55f)),
                    0,
                    1.2f);

                DrawAvatarIcon(drawList, avatarCenter, avatarSize);
            }

            // =====================================================
            // NAME
            // =====================================================

            var nameX =
                avatarMin.X +
                avatarSize +
                contentGap;

            var nameY =
                origin.Y +
                Ui(17f);

            ImGui.SetCursorScreenPos(
                new Vector2(
                    nameX,
                    nameY));

            using var uiFontScale130647=UiFonts.PushRelative(0.98f);

            DrawUsername(userId,FitPartyTextToWidth(displayName, rowWidth - (nameX - origin.X) - Ui(isHost ? 110f : 58f)));

            uiFontScale130647.Dispose();

            // =====================================================
            // HOST BADGE
            //
            // Same visual language as the Party Chat Host badge.
            // =====================================================

            if (isHost)
            {
                ImGui.SameLine(
                    0f,
                    8f);

                const string badgeText =
                    "Host";

                var badgeTextSize =
                    ImGui.CalcTextSize(
                        badgeText);

                var badgePadX = Ui(7f);

                var badgePadY = Ui(3f);

                var badgeMin =
                    ImGui.GetCursorScreenPos();

                var badgeSize =
                    new Vector2(
                        badgeTextSize.X +
                        badgePadX * 2f,
                        badgeTextSize.Y +
                        badgePadY * 2f);

                drawList.AddRectFilled(
                    badgeMin,
                    badgeMin +
                    badgeSize,
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.28f)),
                    6f);

                drawList.AddRect(
                    badgeMin,
                    badgeMin +
                    badgeSize,
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.55f)),
                    6f);

                UiFonts.DrawText(drawList, ImGui.GetFontSize(), badgeMin +
                    new Vector2(
                        badgePadX,
                        badgePadY - 1f), ImGui.GetColorU32(
                        Vector4.One), badgeText);

                ImGui.Dummy(
                    badgeSize);
            }

            // Small live status underneath.
            ImGui.SetCursorScreenPos(
                new Vector2(
                    nameX,
                    origin.Y +
                    Ui(46f)));

            using var uiFontScale133266=UiFonts.PushRelative(0.73f);

            ImGui.TextColored(
                Good,
                "● In Watch Party");

            uiFontScale133266.Dispose();

            var reactionMenuWidth = Ui(31f);
            var reactionMenuHeight = Ui(31f);
            var rightPadding = Ui(12f);
            var reactionMenuX = origin.X + rowWidth - rightPadding - reactionMenuWidth;
            var reactionMenuY = origin.Y + (rowHeight - reactionMenuHeight) * 0.5f;
            var isLocalUser =
                CurrentSession is { } localSession &&
                string.Equals(
                    userId,
                    localSession.AccountId,
                    StringComparison.Ordinal);

            ImGui.SetCursorScreenPos(new Vector2(reactionMenuX, reactionMenuY));
            using (UiDisabled.Scope(isLocalUser))
            using (UiFonts.PushIcon())
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7f)))
            using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero)
                       .Push(ImGuiCol.ButtonHovered, FrameBgHover)
                       .Push(ImGuiCol.ButtonActive, AccentActive))
            {
                if (ImGui.Button(
                        $"{FontAwesomeIcon.EllipsisH.ToIconString()}##reactionVisibility",
                        new Vector2(reactionMenuWidth, reactionMenuHeight)))
                {
                    ImGui.OpenPopup("##reactionVisibilityMenu");
                }
            }

            if (!isLocalUser && ImGui.IsItemHovered())
            {
                UiFonts.SetTooltip($"Actions for {displayName}");
            }

            if (!isLocalUser)
            {
                DrawPartyMemberReactionVisibilityMenu(userId, displayName, showManagementActions, onMakeHost);
            }

        }
    }

    private void DrawLegacyPartyPanel()
    {
        if (CurrentSession is null)
        {
            using var uiFontScale136603=UiFonts.PushRelative(1.15f);

            ImGui.TextColored(
                Vector4.One,
                "Watch party");

            uiFontScale136603.Dispose();

            ImGui.Dummy(UiVec(0f, 6f));

            ImGui.TextColored(
                MutedText,
                "Sign in to host or join a synced watch party.");

            ImGui.Dummy(UiVec(0f, 12f));

            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                8f))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                FrameBg)
                .Push(
                    ImGuiCol.ButtonHovered,
                    FrameBgHover)
                .Push(
                    ImGuiCol.ButtonActive,
                    AccentFieldActive))
            {
                if (ImGui.Button(
                    "Open Settings",
                    UiVec(120f, 34f)))
                {
                    currentPage = HomePage.Settings;
                }
            }

            return;
        }

        // ---------------------------------------------------------
        // Heading
        // ---------------------------------------------------------

        using var uiFontScale137835=UiFonts.PushRelative(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Watch party");

        uiFontScale137835.Dispose();

        ImGui.Dummy(UiVec(0f, 4f));

        switch (stream.Mode)
        {
            // -----------------------------------------------------
            // Hosting
            // -----------------------------------------------------

            case StreamMode.Hosting:
                {
                    // Temporary visual-only room name.
                    var previewRoomName =
                        $"{CurrentDisplayName ?? "Your"}'s Watch Party";

                    var isPrivate =
                        stream.IsPrivate;

                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.ChildRounding,
                        8f))
                    using (ImRaii.PushColor(
                        ImGuiCol.ChildBg,
                        CardBg))
                    using (var statusCard = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                        "##partyHosting",
                        new Vector2(-1f, Ui(154f)),
                        false,
                        ImGuiWindowFlags.NoScrollbar |
                        ImGuiWindowFlags.NoScrollWithMouse)))
                    {
                        if (statusCard)
                        {
                            // HOSTING
                            ImGui.SetCursorPos(
                                UiVec(14f, 12f));

                            ImGui.TextColored(
                                Good,
                                "HOSTING");

                            // Private party toggle in top-right.
                            ImGui.SetCursorPos(
                                new Vector2(
                                    ImGui.GetWindowWidth() - Ui(140f),
                                    Ui(9f)));

                            if (ImGui.Checkbox(
                                "Private party",
                                ref isPrivate))
                            {
                                stream.IsPrivate =
                                    isPrivate;
                            }

                            // Host
                            ImGui.SetCursorPos(
                                UiVec(14f, 39f));

                            DrawUsername($"Host: {CurrentDisplayName ?? "You"}",Vector4.One);

                            // Status
                            ImGui.SetCursorPos(
                                UiVec(14f, 63f));

                            using var uiFontScale140463=UiFonts.PushRelative(0.80f);

                            ImGui.TextColored(
                                MutedText,
                                $"{stream.Roster.Length} watching  •  Playback stays synced to you");

                            uiFontScale140463.Dispose();

                            // Room name label
                            ImGui.SetCursorPos(
                                UiVec(14f, 91f));

                            using var uiFontScale140915=UiFonts.PushRelative(0.78f);

                            ImGui.TextColored(
                                MutedText,
                                "Room name");

                            uiFontScale140915.Dispose();

                            // Room name input
                            ImGui.SetCursorPos(
                                UiVec(14f, 111f));

                            ImGui.SetNextItemWidth(
                                ImGui.GetWindowWidth() - 106f);

                            using (ImRaii.PushStyle(
                                ImGuiStyleVar.FrameRounding,
                                7f)
                                .Push(
                                    ImGuiStyleVar.FramePadding,
                                    UiVec(12f, 7f)))
                            using (ImRaii.PushColor(
                                ImGuiCol.FrameBg,
                                FrameBg)
                                .Push(
                                    ImGuiCol.FrameBgHovered,
                                    FrameBgHover)
                                .Push(
                                    ImGuiCol.FrameBgActive,
                                    AccentFieldActive))
                            {
                                ImGui.InputText(
                                    "##previewRoomName",
                                    ref previewRoomName,
                                    80);
                            }

                            ImGui.SameLine(0f, 8f);

                            using (ImRaii.PushStyle(
                                ImGuiStyleVar.FrameRounding,
                                7f))
                            using (ImRaii.PushColor(
                                ImGuiCol.Button,
                                Accent)
                                .Push(
                                    ImGuiCol.ButtonHovered,
                                    AccentHover)
                                .Push(
                                    ImGuiCol.ButtonActive,
                                    AccentActive))
                            {
                                ImGui.Button(
                                    "Save",
                                    UiVec(64f, 32f));
                            }
                        }
                    }

                    ImGui.Dummy(
                        UiVec(0f, 10f));

                    // Invite button directly below the card.
                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.FrameRounding,
                        8f))
                    using (ImRaii.PushColor(
                        ImGuiCol.Button,
                        FrameBg)
                        .Push(
                            ImGuiCol.ButtonHovered,
                            FrameBgHover)
                        .Push(
                            ImGuiCol.ButtonActive,
                            AccentFieldActive))
                    {
                        if (ImGui.Button(
                            "Copy party invite",
                            UiVec(150f, 32f)))
                        {
                            ImGui.SetClipboardText(
                                $"Come watch with me! Right-click my character and choose \"Join Stream\" " +
                                $"(or open AlphaChannel → Player and join \"{CurrentDisplayName}\").");
                        }
                    }

                    ImGui.Dummy(
                        UiVec(0f, 14f));

                    DrawRoster(
                        $"Watching ({stream.Roster.Length})",
                        allowPromote: true);

                    break;
                }

            // -----------------------------------------------------
            // Viewing
            // -----------------------------------------------------

            case StreamMode.Viewing:
                {
                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.ChildRounding,
                        8f))
                    using (ImRaii.PushColor(
                        ImGuiCol.ChildBg,
                        CardBg))
                    using (var statusCard = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                        "##partyViewing",
                        new Vector2(-1f, Ui(104f)),
                        false,
                        ImGuiWindowFlags.NoScrollbar |
                        ImGuiWindowFlags.NoScrollWithMouse)))
                    {
                        if (statusCard)
                        {
                            ImGui.SetCursorPos(
                                UiVec(14f, 12f));

                            ImGui.TextColored(
                                Good,
                                "IN ROOM");

                            ImGui.SetCursorPos(
                                UiVec(14f, 38f));

                            DrawUsername(joinedHostDisplayName is { } host
                                    ? $"{host}'s room"
                                    : "A friend's room",Vector4.One);

                            ImGui.SetCursorPos(
                                UiVec(14f, 64f));

                            using var uiFontScale146346=UiFonts.PushRelative(0.82f);

                            ImGui.TextColored(
                                MutedText,
                                $"{stream.Roster.Length} also here  •  Playback is synced to the host");

                            uiFontScale146346.Dispose();
                        }
                    }

                    ImGui.Dummy(UiVec(0f, 14f));

                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.FrameRounding,
                        8f))
                    using (ImRaii.PushColor(
                        ImGuiCol.Button,
                        FrameBg)
                        .Push(
                            ImGuiCol.ButtonHovered,
                            FrameBgHover)
                        .Push(
                            ImGuiCol.ButtonActive,
                            AccentFieldActive))
                    {
                        if (ImGui.Button(
                            "Leave room",
                            UiVec(120f, 34f)))
                        {
                            RequestLeaveWatchParty();
                        }
                    }

                    ImGui.Dummy(UiVec(0f, 20f));

                    DrawRoster(
                        $"Also here ({stream.Roster.Length})",
                        allowPromote: false);

                    break;
                }

            // -----------------------------------------------------
            // Not currently in a party
            // -----------------------------------------------------

            default:
                {
                    using var uiFontScale148020=UiFonts.PushRelative(0.88f);

                    ImGui.TextColored(
                        MutedText,
                        "Host automatically while playing, or join a friend's watch party.");

                    uiFontScale148020.Dispose();

                    ImGui.Dummy(UiVec(0f, 14f));

                    ImGui.TextColored(
                        MutedText,
                        "Join a party");

                    ImGui.Dummy(UiVec(0f, 4f));

                    ImGui.SetNextItemWidth(-118f);

                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.FrameRounding,
                        10f)
                        .Push(
                            ImGuiStyleVar.FramePadding,
                            UiVec(14f, 8f)))
                    using (ImRaii.PushColor(
                        ImGuiCol.FrameBg,
                        FrameBg)
                        .Push(
                            ImGuiCol.FrameBgHovered,
                            FrameBgHover)
                        .Push(
                            ImGuiCol.FrameBgActive,
                            AccentFieldActive))
                    {
                        if (playerFocusJoin)
                        {
                            ImGui.SetKeyboardFocusHere();
                            playerFocusJoin = false;
                        }

                        ImGui.InputTextWithHint(
                            "##hostName",
                            "Enter their AlphaChannel name",
                            ref joinHostNameInput,
                            32);
                    }
                }

                ImGui.SameLine(0f, 10f);

                using (ImRaii.PushStyle(
                    ImGuiStyleVar.FrameRounding,
                    8f))
                using (ImRaii.PushColor(
                    ImGuiCol.Button,
                    Accent)
                    .Push(
                        ImGuiCol.ButtonHovered,
                        AccentHover)
                    .Push(
                        ImGuiCol.ButtonActive,
                        AccentActive))
                {
                    if (ImGui.Button(
                        "Join",
                        UiVec(88f, 38f)))
                    {
                        DoJoin(joinHostNameInput, joinPasswordInput);
                    }
                }

                if (joinError is { } error)
                {
                    ImGui.Dummy(UiVec(0f, 8f));

                    ImGui.TextColored(
                        Danger,
                        error);
                }

                break;
        }
    }


    private void DrawPartySocialPanel()
    {
        DrainPartyChat();

        if (CurrentSession is null)
        {
            ImGui.TextColored(
                MutedText,
                "Sign in under Settings to use room chat and reactions.");

            return;
        }

        if (stream.Mode == StreamMode.None)
        {
            using var uiFontScale151140=UiFonts.PushRelative(1.15f);

            ImGui.TextColored(
                Vector4.One,
                "Chat");

            uiFontScale151140.Dispose();

            ImGui.Dummy(
                UiVec(0f, 6f));

            ImGui.TextColored(
                MutedText,
                "Join or host a watch party to use chat and reactions.");

            return;
        }

        DrawPartyChatFeed();

        ImGui.Dummy(
            UiVec(0f, 8f));

        DrawPartyChatComposer();
    }

    private void DrawPartyChatItem(
        PartyChatItem item,
        float rightReserve)
    {
        switch (item.Kind)
        {
            case PartyChatItemKind.Message:
                {
                    var avatarSize = Ui(42f);
                    var contentGap = Ui(12f);
                    var bottomSpacing = Ui(14f);

                    var origin =
                        ImGui.GetCursorScreenPos();

                    var drawList =
                        ImGui.GetWindowDrawList();

                    var senderName =
                        string.IsNullOrWhiteSpace(
                            item.Name)
                            ? "Someone"
                            : item.Name;

                    var hostName =
                        stream.Mode == StreamMode.Hosting
                            ? CurrentDisplayName
                            : joinedHostDisplayName;

                    var isHostMessage =
                        !string.IsNullOrWhiteSpace(
                            hostName) &&
                        string.Equals(
                            senderName,
                            hostName,
                            StringComparison.OrdinalIgnoreCase);

                    // =========================================================
                    // Avatar
                    // =========================================================

                    // Request the participant's avatar from the server.
                    // This is non-blocking and becomes a cheap cache check
                    // after the first successful lookup.
                    if (!string.IsNullOrWhiteSpace(
                            item.UserId))
                    {
                        EnsurePartyAvatarLoaded(
                            item.UserId,
                            senderName);
                    }

                    var avatarMin =
                        origin;

                    var drewRealAvatar =
                        false;

                    // Cached Watch Party participant avatar.
                    if (!string.IsNullOrWhiteSpace(
                            item.UserId) &&
                        partyAvatarCache.TryGetValue(
                            item.UserId,
                            out var partyAvatar))
                    {
                        DrawAvatarAt(
                            avatarMin,
                            partyAvatar.AvatarIcon,
                            partyAvatar.AvatarColorHex,
                            avatarSize,
                            partyAvatar.AvatarImageUrl);

                        drewRealAvatar =
                            true;
                    }


                    // User icon while a remote avatar is unavailable.
                    if (!drewRealAvatar)
                    {
                        var avatarCenter =
                            avatarMin +
                            new Vector2(
                                avatarSize * 0.5f,
                                avatarSize * 0.5f);

                        drawList.AddCircleFilled(
                            avatarCenter,
                            avatarSize * 0.5f,
                            ImGui.GetColorU32(
                                new Vector4(
                                    Accent.X,
                                    Accent.Y,
                                    Accent.Z,
                                    0.18f)));

                        drawList.AddCircle(
                            avatarCenter,
                            avatarSize * 0.5f,
                            ImGui.GetColorU32(
                                new Vector4(
                                    Accent.X,
                                    Accent.Y,
                                    Accent.Z,
                                    0.55f)),
                            0,
                            1.2f);

                        DrawAvatarIcon(drawList, avatarCenter, avatarSize);
                    }

                    // =========================================================
                    // Sender row
                    // =========================================================

                    var contentX =
                        origin.X +
                        avatarSize +
                        contentGap;

                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            contentX,
                            origin.Y));

                    using var uiFontScale156403=UiFonts.PushRelative(0.95f);

                    DrawUsername(senderName,AccentHover);

                    uiFontScale156403.Dispose();

                    // ---------------------------------------------------------
                    // Host badge
                    // ---------------------------------------------------------

                    if (isHostMessage)
                    {
                        ImGui.SameLine(
                            0f,
                            8f);

                        var badgeText =
                            "Host";

                        var badgeTextSize =
                            ImGui.CalcTextSize(
                                badgeText);

                        var badgeMin =
                            ImGui.GetCursorScreenPos();

                        var badgePadX = Ui(7f);
                        var badgePadY = Ui(3f);

                        var badgeSize =
                            new Vector2(
                                badgeTextSize.X +
                                badgePadX * 2f,
                                badgeTextSize.Y +
                                badgePadY * 2f);

                        drawList.AddRectFilled(
                            badgeMin,
                            badgeMin +
                            badgeSize,
                            ImGui.GetColorU32(
                                new Vector4(
                                    Accent.X,
                                    Accent.Y,
                                    Accent.Z,
                                    0.28f)),
                            6f);

                        drawList.AddRect(
                            badgeMin,
                            badgeMin +
                            badgeSize,
                            ImGui.GetColorU32(
                                new Vector4(
                                    Accent.X,
                                    Accent.Y,
                                    Accent.Z,
                                    0.55f)),
                            6f);

                        UiFonts.DrawText(drawList, ImGui.GetFontSize(), badgeMin +
                            new Vector2(
                                badgePadX,
                                badgePadY - 1f), ImGui.GetColorU32(
                                Vector4.One), badgeText);

                        ImGui.Dummy(
                            badgeSize);
                    }

                    // ---------------------------------------------------------
                    // Timestamp
                    // ---------------------------------------------------------

                    if (item.ReceivedAt is { } receivedAt)
                    {
                        ImGui.SameLine(
                            0f,
                            9f);

                        using var uiFontScale159545=UiFonts.PushRelative(0.72f);

                        ImGui.TextColored(
                            MutedText,
                            receivedAt.ToString(
                                "t"));

                        uiFontScale159545.Dispose();
                    }

                    // =========================================================
                    // Message body
                    // =========================================================

                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            contentX,
                            origin.Y + Ui(24f)));

                    using var uiFontScale160277=UiFonts.PushRelative(0.96f);

                    var wrapRight =
                        ImGui.GetWindowWidth() -
                        20f -
                        rightReserve;

                    ImGui.PushTextWrapPos(
                        wrapRight);

                    ImGui.TextColored(
                        Vector4.One,
                        item.Text);

                    ImGui.PopTextWrapPos();

                    uiFontScale160277.Dispose();

                    // =========================================================
                    // Advance cursor below both avatar and wrapped text
                    // =========================================================

                    var contentEndY =
                        ImGui.GetCursorScreenPos().Y;

                    var minimumEndY =
                        origin.Y +
                        avatarSize;

                    ImGui.SetCursorScreenPos(
                        new Vector2(
                            origin.X,
                            MathF.Max(
                                contentEndY,
                                minimumEndY) +
                            bottomSpacing));

                    break;
                }

            case PartyChatItemKind.QueueNotice:
                DrawModernQueueNotice(item, ImGui.GetContentRegionAvail().X);
                break;
            case PartyChatItemKind.Reaction:
                {
                    var avatarSize = Ui(30f);
                    var rowHeight = Ui(40f);

                    var origin =
                        ImGui.GetCursorScreenPos();

                    var drawList =
                        ImGui.GetWindowDrawList();

                    var senderName =
                        string.IsNullOrWhiteSpace(
                            item.Name)
                            ? "Someone"
                            : item.Name;

                    var hostName =
                        stream.Mode == StreamMode.Hosting
                            ? CurrentDisplayName
                            : joinedHostDisplayName;

                    var isHostReaction =
                        !string.IsNullOrWhiteSpace(
                            hostName) &&
                        string.Equals(
                            senderName,
                            hostName,
                            StringComparison.OrdinalIgnoreCase);

                    // ---------------------------------------------------------
                    // Avatar
                    // ---------------------------------------------------------

                    // Use the same server-avatar path as normal chat messages.
                    if (!string.IsNullOrWhiteSpace(
                            item.UserId))
                    {
                        EnsurePartyAvatarLoaded(
                            item.UserId,
                            senderName);
                    }

                    var drewRealAvatar =
                        false;

                    // Cached Watch Party participant avatar.
                    if (!string.IsNullOrWhiteSpace(
                            item.UserId) &&
                        partyAvatarCache.TryGetValue(
                            item.UserId,
                            out var partyAvatar))
                    {
                        DrawAvatarAt(
                            origin,
                            partyAvatar.AvatarIcon,
                            partyAvatar.AvatarColorHex,
                            avatarSize,
                            partyAvatar.AvatarImageUrl);

                        drewRealAvatar =
                            true;
                    }


                    // User icon while a remote avatar is unavailable.
                    if (!drewRealAvatar)
                    {
                        var avatarCenter =
                            origin +
                            new Vector2(
                                avatarSize * 0.5f,
                                avatarSize * 0.5f);

                        drawList.AddCircleFilled(
                            avatarCenter,
                            avatarSize * 0.5f,
                            ImGui.GetColorU32(
                                new Vector4(
                                    Accent.X,
                                    Accent.Y,
                                    Accent.Z,
                                    0.16f)));

                        DrawAvatarIcon(drawList, avatarCenter, avatarSize);
                    }

                    // ---------------------------------------------------------
                    // Activity pill
                    // ---------------------------------------------------------

                    var pillMin =
                        origin +
                        new Vector2(
                            avatarSize + Ui(8f),
                            Ui(2f));

                    var pillHeight =
                        30f;

                    var nameText =
                        $"{senderName} reacted with";

                    using var uiFontScale186059=UiFonts.PushRelative(0.82f);

                    var nameSize =
                        ImGui.CalcTextSize(
                            nameText);

                    Vector2 reactionSize;

                    using (UiFonts.PushIcon())
                    {
                        reactionSize =
                            ImGui.CalcTextSize(
                                item.Text);
                    }

                    var pillWidth =
                        nameSize.X +
                        reactionSize.X +
                        34f;

                    drawList.AddRectFilled(
                        pillMin,
                        pillMin +
                        new Vector2(
                            pillWidth,
                            pillHeight),
                        ImGui.GetColorU32(
                            CardBg),
                        15f);

                    UiFonts.DrawText(drawList, ImGui.GetFontSize(), pillMin +
                        UiVec(12f, 7f), ImGui.GetColorU32(
                            MutedText), nameText);

                    using (UiFonts.PushIcon())
                    {
                        UiFonts.DrawText(drawList, ImGui.GetFontSize(), pillMin +
                            new Vector2(
                                Ui(18f) + nameSize.X,
                                Ui(7f)), ImGui.GetColorU32(
                                GetReactionDisplayColor(
                                    item.Text)), item.Text);
                    }

                    uiFontScale186059.Dispose();

                    // Register the reaction row with ImGui's layout system.
                    //
                    // The reaction itself is drawn manually with the draw list, so simply
                    // moving the cursor does not reliably extend the scrollable content.
                    // A real Dummy item gives the chat child the correct content height,
                    // allowing its normal stick-to-bottom behaviour to work.
                    ImGui.SetCursorScreenPos(
                        origin);

                    ImGui.Dummy(
                        new Vector2(
                            0f,
                            rowHeight + Ui(4f)));

                    break;
                }
        }
    }

    private void DrawPartyChatFeed(
        float rightReserve = 0f)
    {
        // ---------------------------------------------------------
        // Explicit inset inside the outer chat container.
        //
        // Borderless ImGui child windows don't reliably inherit
        // WindowPadding, so physically inset the scrollable feed.
        // ---------------------------------------------------------

        var insetX = Ui(18f);
        var insetY = Ui(14f);

        var available =
            ImGui.GetContentRegionAvail();

        ImGui.SetCursorPos(
            ImGui.GetCursorPos() +
            new Vector2(
                insetX,
                insetY));

        var feedSize =
            new Vector2(
                MathF.Max(
                    1f,
                    available.X -
                    insetX * 2f),
                MathF.Max(
                    1f,
                    available.Y -
                    insetY * 2f));

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            8f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                Vector2.Zero))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            CardBg))
        using (var child =
            UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                "##partyChatLog",
                feedSize,
                false,
                ImGuiWindowFlags.None)))
        {
            if (!child)
            {
                return;
            }

            if (!partyChatItems.Any(ShouldDisplayPartyChatItem))
            {
                using var uiFontScale190408=UiFonts.PushRelative(0.88f);

                ImGui.TextColored(
                    MutedText,
                    "No messages yet.");

                uiFontScale190408.Dispose();
            }
            else
            {
                var chatContentInset = Ui(16f);

                ImGui.Indent(
                    chatContentInset);

                foreach (var item in partyChatItems)
                {
                    if (!ShouldDisplayPartyChatItem(item))
                    {
                        continue;
                    }

                    // Reserve matching space on the right for chat notices.
                    ImGui.SetNextItemWidth(
                        MathF.Max(
                            1f,
                            ImGui.GetContentRegionAvail().X -
                            chatContentInset -
                            rightReserve));

                    DrawPartyChatItem(
                        item,
                        rightReserve);
                }

                ImGui.Unindent(
                    chatContentInset);
            }

            if (partyChatStickToBottom)
            {
                ImGui.SetScrollHereY(
                    1f);

                partyChatStickToBottom =
                    false;
            }

            // Draw this in the scroll child's own input layer. Drawing it in
            // the parent panel leaves the child covering most of its hitbox,
            // even though the button itself appears above the feed visually.
            DrawMiniChatPopoutButton();
        }
    }

    private void DrawPartyMemberReactionVisibilityMenu(
        string userId,
        string displayName,
        bool showManagementActions = false,
        Action? onMakeHost = null)
    {
        var hasSession = CurrentSession is not null;
        var rows = 6 + (hasSession ? 2 : 0) + (showManagementActions ? 4 : 0);
        var separators = 2 + (hasSession ? 1 : 0) + (showManagementActions ? 1 : 0);
        var menuHeight = Ui(24f) + rows * ImGui.GetTextLineHeightWithSpacing() +
            separators * ImGui.GetStyle().ItemSpacing.Y;
        if (!UiFonts.Window(ImGui.GetFontSize(), BeginPartyPopup(
                "##reactionVisibilityMenu", new Vector2(Ui(270f), menuHeight))))
        {
            return;
        }

        if (CurrentSession is { } session)
        {
            using (UiDisabled.Scope(string.IsNullOrWhiteSpace(userId) || AlphaChatBlocked(userId)))
            {
                if (ImGui.MenuItem("View profile")) OpenProfilePage(session, userId, displayName);
            }

            if (friendsDirty && !friendsLoading)
                RefreshFriends(session.Token);

            var knownAccount = friendsAccountId == session.AccountId;
            var alreadyFriends = knownAccount && friends.Any(friend => friend.AccountId == userId);
            var requestPending = knownAccount &&
                friendRequests.Incoming.Concat(friendRequests.Outgoing).Any(request => request.OtherAccountId == userId);
            var sending = partyFriendRequestsSending.ContainsKey((session.Token, userId));
            var disabled = alreadyFriends || requestPending || sending ||
                string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(displayName) ||
                userId == session.AccountId;
            using (UiDisabled.Scope(disabled))
            {
                if (ImGui.MenuItem("Add Friend"))
                    _ = SendPartyFriendRequestAsync(session, userId, displayName);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                UiFonts.SetTooltip(alreadyFriends ? "Already friends" : requestPending ? "Friend request pending"
                    : sending ? "Sending friend request..." : $"Send a friend request to {displayName}");
            }
            ImGui.Separator();
        }

        if (showManagementActions)
        {
            DrawUsername(userId,displayName,MutedText);
            var transferBlocked = CurrentContentBlocksHostTransfer();
            using (UiDisabled.Scope(transferBlocked))
            {
                if (ImGui.MenuItem("Make host") && !transferBlocked) onMakeHost?.Invoke();
            }
            DrawHostTransferDisabledTooltip(transferBlocked);
            using (UiDisabled.Scope(partyModerationBusy || stream.Mode != StreamMode.Hosting))
            {
                if (ImGui.MenuItem("Kick from party")) ModeratePartyMember(userId, displayName, "kick");
                if (ImGui.MenuItem("Ban from party")) ModeratePartyMember(userId, displayName, "ban");
            }
            ImGui.Separator();
        }

        DrawUsername(userId,$"Reactions from {displayName}",MutedText);
        ImGui.Separator();

        var current =
            !string.IsNullOrWhiteSpace(userId) &&
            Plugin.Cfg.PartyReactionVisibilityByUserId.TryGetValue(userId, out var saved)
                ? saved
                : PartyReactionVisibility.ShowAll;

        DrawPartyReactionVisibilityOption(userId, "Show reactions", PartyReactionVisibility.ShowAll, current);
        DrawPartyReactionVisibilityOption(userId, "Show TV reactions only", PartyReactionVisibility.TvOnly, current);
        DrawPartyReactionVisibilityOption(userId, "Show chat reactions only", PartyReactionVisibility.ChatOnly, current);
        DrawPartyReactionVisibilityOption(userId, "Hide all reactions", PartyReactionVisibility.HideAll, current);

        ImGui.Separator();
        if (ImGui.MenuItem("Mention in party chat"))
        {
            partyChatInput += $"@{displayName} ";
            partyPanelTab = PartyPanelTab.Chat;
            partyChatComposerInputRevision++;
        }

        ImGui.EndPopup();
    }

    private static void DrawPartyReactionVisibilityOption(
        string userId,
        string label,
        PartyReactionVisibility option,
        PartyReactionVisibility current)
    {
        if (!ImGui.Selectable(label, option == current) || string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        if (option == PartyReactionVisibility.ShowAll)
        {
            Plugin.Cfg.PartyReactionVisibilityByUserId.Remove(userId);
        }
        else
        {
            Plugin.Cfg.PartyReactionVisibilityByUserId[userId] = option;
        }

        Plugin.Cfg.Save();
    }

    private void DrawPartyChatComposer(bool alphaStyle = false)
    {
        var dockHeight = Ui(partyChatReplyTarget is null ? 76f : 102f);
        var padding = Ui(12f);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            Ui(12f))
            .Push(
                ImGuiStyleVar.WindowPadding,
                new Vector2(
                    padding,
                    Ui(alphaStyle ? 12f : 8f))))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            CardBg))
        using (var dock =
            UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                "##partyChatDock",
                new Vector2(
                    -1f,
                    dockHeight),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse | (alphaStyle ? ImGuiWindowFlags.AlwaysUseWindowPadding : ImGuiWindowFlags.None))))
        {
            if (!dock)
            {
                return;
            }

            var availableWidth =
     ImGui.GetContentRegionAvail().X;

            // Save the dock content origin so the React section can be
            // positioned independently from the message controls.
            var dockContentStart =
                ImGui.GetCursorPos();

            if (partyChatReplyTarget is { } replyTarget)
            {
                DrawUsername(replyTarget.UserId,$"Replying to {replyTarget.Name}",AccentHover);
                ImGui.SameLine();
                if (ImGui.SmallButton("×##cancelPartyChatReply"))
                    partyChatReplyTarget = null;
            }

            var reactWidth = alphaStyle ? 0f : MathF.Min(Ui(300f), availableWidth * 0.30f);
            var sendWidth = Ui(92f);
            var optionsWidth = alphaStyle ? 0f : Ui(40f);
            var emojiWidth = Ui(alphaStyle ? 44f : 41f);
            var dividerGap = alphaStyle ? 0f : Ui(14f);
            var inputHeight = Ui(alphaStyle ? 48f : 47f);
            var controlsY = alphaStyle ? ImGui.GetCursorPosY() : (dockHeight - inputHeight) * 0.5f +
                (partyChatReplyTarget is null ? 0f : Ui(12.5f));

            var chatWidth =
                MathF.Max(
                    Ui(180f),
                    availableWidth -
                    reactWidth -
                    dividerGap);

            // =====================================================
            // LEFT — message composer
            // =====================================================

            ImGui.SetCursorPosY(controlsY);

            ImGui.BeginGroup();

            var inputWidth =
                chatWidth -
                sendWidth -
                optionsWidth -
                emojiWidth -
                Ui(alphaStyle ? 20f : 28f);

            var inputGroupStart = ImGui.GetCursorScreenPos();
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(9f)))
            using (ImRaii.PushColor(ImGuiCol.Button, Accent)
                .Push(ImGuiCol.ButtonHovered, AccentHover)
                .Push(ImGuiCol.ButtonActive, AccentActive))
            using (UiFonts.PushIcon())
            {
                if (ImGui.Button($"{FontAwesomeIcon.Smile.ToIconString()}##partyChatEmoji", new Vector2(emojiWidth, inputHeight)))
                    ImGui.OpenPopup("##partyChatEmojiPopup");
            }
            DrawChatComposerEmojiPopup("##partyChatEmojiPopup",
                emoji => partyChatInput += ChatReactionShortcodes[emoji]);
            ImGui.SameLine(0f, Ui(8f));

            ImGui.SetNextItemWidth(
                inputWidth);

            bool sent;

            using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(9f))
                .Push(ImGuiStyleVar.FramePadding,
                    UiVec(12f, alphaStyle ? MathF.Max(0f,(inputHeight-ImGui.GetFontSize())*0.5f)
                        : MathF.Max(Ui(5f), (inputHeight - ImGui.GetTextLineHeight()) * 0.5f - Ui(2f))))
                .Push(ImGuiStyleVar.FrameBorderSize, 0f))
            using (ImRaii.PushColor(ImGuiCol.FrameBg, FrameBg)
                .Push(ImGuiCol.FrameBgHovered, FrameBgHover)
                .Push(ImGuiCol.FrameBgActive, FrameBg))
            {
                if (partyChatFocusComposer)
                {
                    ImGui.SetKeyboardFocusHere();
                    partyChatFocusComposer = false;
                }
                if (alphaStyle)
                    ImGui.InputTextWithHint($"##partyChatInput{partyChatComposerInputRevision}",
                        "Message the watch party…",ref partyChatInput,200,
                        ImGuiInputTextFlags.CallbackAlways,PlacePartyChatCaretAfterMention);
                else ImGui.InputTextMultiline(
                    $"##partyChatInput{partyChatComposerInputRevision}",
                    ref partyChatInput,
                    200,
                    new Vector2(MathF.Max(Ui(80f), inputWidth), inputHeight),
                    ImGuiInputTextFlags.CallbackAlways, PlacePartyChatCaretAfterMention);
                sent = (ImGui.IsItemActive() || (alphaStyle && ImGui.IsItemDeactivated())) &&
                       ImGui.IsKeyPressed(ImGuiKey.Enter) &&
                       !ImGui.GetIO().KeyShift;
                if (!alphaStyle && partyChatInput.Length == 0 && !ImGui.IsItemActive())
                {
                    var hintPos = ImGui.GetItemRectMin() +
                                  new Vector2(Ui(12f), (inputHeight - ImGui.GetTextLineHeight()) * 0.5f);
                    ImGui.GetWindowDrawList().AddText(
                        hintPos, ImGui.GetColorU32(MutedText), "Message the watch party...");
                }
            }

            // Offer party members while an @mention is being typed. Selecting a name
            // completes it in the composer; the rendered message highlights it too.
            var mentionStart = partyChatInput.LastIndexOf('@');
            var mentionQuery = mentionStart >= 0 ? partyChatInput[(mentionStart + 1)..] : string.Empty;
            var composingMention = ImGui.IsItemActive() && mentionStart >= 0 &&
                                   !mentionQuery.Any(char.IsWhiteSpace) &&
                                   !mentionQuery.Contains('\n');
            if (composingMention)
            {
                var mentionNames = stream.Roster.Select(member => member.DisplayName)
                    .Append(joinedHostDisplayName ?? string.Empty)
                    .Append(CurrentDisplayName ?? CurrentSession?.DisplayName ?? string.Empty)
                    .Where(name => !string.IsNullOrWhiteSpace(name) &&
                                   name.StartsWith(mentionQuery, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8).ToArray();
                if (mentionNames.Length > 0)
                {
                    ImGui.OpenPopup("##partyChatMentionSuggestions");
                    ImGui.SetNextWindowPos(new Vector2(inputGroupStart.X + emojiWidth + Ui(8f),
                        inputGroupStart.Y - Ui(6f)), ImGuiCond.Always, new Vector2(0f, 1f));
                    if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup("##partyChatMentionSuggestions",
                            ImGuiWindowFlags.NoFocusOnAppearing)))
                    {
                        foreach (var name in mentionNames)
                        {
                            if (ImGui.Selectable(name))
                            {
                                partyChatInput = partyChatInput[..mentionStart] + "@" + name + " ";
                                partyChatComposerInputRevision++;
                                ImGui.CloseCurrentPopup();
                            }
                        }
                        ImGui.EndPopup();
                    }
                }
            }

            ImGui.SameLine(
                0f,
                Ui(10f));

            var hasMessage =
                partyChatInput.Trim().Length > 0;

            var sendClicked =
                false;

            using (UiDisabled.Scope(
       !hasMessage || !stream.IsConnected))
            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                Ui(9f)))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                Accent)
                .Push(
                    ImGuiCol.ButtonHovered,
                    AccentHover)
                .Push(
                    ImGuiCol.ButtonActive,
                    AccentActive))
            {
                sendClicked =
                    ImGui.Button(
                        "Send",
                        new Vector2(
                            sendWidth,
                            inputHeight));
            }

            if (!alphaStyle)
            {
            // =====================================================
            // Chat options
            // =====================================================

            ImGui.SameLine(
                0f,
                Ui(10f));

            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                Ui(9f)))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                FrameBg)
                .Push(
                    ImGuiCol.ButtonHovered,
                    FrameBgHover)
                .Push(
                    ImGuiCol.ButtonActive,
                    AccentFieldActive))
            {
                if (ImGui.Button(
                        "...##partyChatOptions",
                        new Vector2(
                            optionsWidth,
                            inputHeight)))
                {
                    ImGui.OpenPopup(
                        "##partyChatOptionsPopup");
                }
            }

            ImGui.SetNextWindowSize(
                UiVec(300f, 0f),
                ImGuiCond.Appearing);

            using (ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                UiVec(14f, 12f))
                .Push(
                    ImGuiStyleVar.PopupRounding,
                    10f))
            {
                if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup(
                        "##partyChatOptionsPopup")))
                {
                    DrawPartyChatOptionsContents();

                    ImGui.EndPopup();
                }
            }

            }

            if ((sendClicked || sent) &&
                hasMessage && stream.IsConnected)
            {
                var text =
                    partyChatInput.Trim();

                partyChatInput =
                    string.Empty;
                partyChatComposerInputRevision++;
                partyChatFocusComposer = true;

                SubmitPartyChatMessage(
                    text);

                partyChatStickToBottom =
                    true;
            }

            ImGui.EndGroup();

            if (!alphaStyle)
            {
            // =====================================================
            // DIVIDER
            // =====================================================

            var dividerX =
                dockContentStart.X +
                chatWidth +
                dividerGap * 0.5f;

            var dividerTop = controlsY;

            var windowPos =
                ImGui.GetWindowPos();

            ImGui.GetWindowDrawList()
                .AddLine(
                    windowPos +
                    new Vector2(
                        dividerX,
                        dividerTop),
                    windowPos +
                    new Vector2(
                        dividerX,
                        dividerTop + inputHeight),
                    ImGui.GetColorU32(
                        new Vector4(
                            1f,
                            1f,
                            1f,
                            0.13f)),
                    1f);

            // =====================================================
            // RIGHT — React Live
            //
            // Explicit positioning means this section no longer
            // depends on the cursor left behind by the composer.
            // =====================================================

            var reactX =
                dividerX +
                Ui(14f);

            var reactWidthAvailable =
                MathF.Max(
                    Ui(120f),
                    availableWidth -
                    (reactX - dockContentStart.X));

            ImGui.SetCursorPos(
                new Vector2(
                    reactX,
                    controlsY));

            DrawCompactReactions(reactWidthAvailable, inputHeight);
            }
        }
    }
}

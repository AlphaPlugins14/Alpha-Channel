using AlphaChannel.Contracts;
using AlphaChannel.Plugin.BroadcastAllowance;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool activePartyInviteOpen;
    private string activePartyInviteSearch = string.Empty;
    internal Action? OnPartyPlaybackResyncRequested { get; set; }

    private void DrawActivePartyRoomOverview()
    {
        var isHost = stream.Mode == StreamMode.Hosting;
        var hostName = isHost ? CurrentDisplayName ?? CurrentSession?.DisplayName ?? "You"
            : joinedHostDisplayName ?? "Host";
        ReadActivePartyRoomMetadata(out var description, out var category, out var adult, out var server);
        description = string.IsNullOrWhiteSpace(description) ? "No description set" : description;
        var width = ImGui.GetContentRegionAvail().X;
        var contentWidth = width - Ui(40f);
        var compact = width < Ui(680f);
        var descriptionHeight = ImGui.CalcTextSize(description, false, contentWidth).Y;
        var location = string.IsNullOrWhiteSpace(stream.RoomLocation)
            ? "Location not specified" : stream.RoomLocation!;
        var locationWidth = compact ? contentWidth : contentWidth * 0.68f - Ui(12f);
        var detailHeight = MathF.Max(Ui(52f), Ui(25f) +
            ImGui.CalcTextSize(location, false, MathF.Max(Ui(80f), locationWidth - Ui(29f))).Y);
        var identityY = compact ? Ui(102f) : Ui(64f);
        var descriptionY = identityY + Ui(compact ? 130f : 110f);
        var capacityY=descriptionY;
        var capacityHost=isHost?CurrentSession?.AccountId:stream.HostId;
        if(capacityHost is not null)
        {
            var showUpgrade=isHost&&AlphaChannel.Plugin.Auth.RoomCapacityClient.Shared.Cached(capacityHost) is {Tier:0};
            var countText=RoomCapacityText(capacityHost,stream.Roster.Count(p=>p.UserId!=capacityHost));
            var countHeight=MathF.Max(Ui(24f),ImGui.CalcTextSize(countText,false,contentWidth).Y+Ui(8f));
            var upgradeHeight=0f;
            if(showUpgrade)
            {
                using var uiFontScale4020=UiFonts.PushRelative(.85f);
                upgradeHeight=ImGui.CalcTextSize(RoomCapacityUpgradeText,false,contentWidth).Y+Ui(12f);
                uiFontScale4020.Dispose();
            }
            descriptionY+=countHeight+upgradeHeight;
        }
        var dividerY = descriptionY + Ui(25f) + descriptionHeight + Ui(16f);
        var height = dividerY + Ui(14f) + detailHeight + Ui(compact ? 78f : 20f);
        if (partyRoomEditing && isHost) height = Ui(440f);

        DrawActivePartyCard("##partyRoomDetailsCard", new Vector2(width, height), () =>
        {
            var origin = ImGui.GetWindowPos();
            var draw = ImGui.GetWindowDrawList();
            using var uiFontScale4699=UiFonts.PushRelative(1.18f);
            ImGui.TextColored(Hex(0xE2E7EF), partyRoomEditing && isHost ? "Edit room details" : "Room overview");
            uiFontScale4699.Dispose();
            if (partyRoomEditing && isHost)
            {
                DrawPartyRoomEditForm(origin, ImGui.GetWindowSize());
                return;
            }

            var actionsY = origin.Y + Ui(compact ? 53f : 15f);
            var actionWidth = Ui(154f);
            var editWidth = Ui(126f);
            ImGui.SetCursorScreenPos(new Vector2(origin.X + width - Ui(20f) - actionWidth, actionsY));
            if (ActivePartyButton(isHost ? "End Watch Party##overview" : "Leave Watch Party##overview",
                    new Vector2(actionWidth, Ui(36f)), danger: true))
            {
                RequestLeaveWatchParty();
                return;
            }
            if (isHost)
            {
                ImGui.SetCursorScreenPos(new Vector2(origin.X + width - Ui(32f) - actionWidth - editWidth, actionsY));
                if (ActivePartyButton("Edit room", new Vector2(editWidth, Ui(36f))))
                    BeginPartyRoomEditing();
            }

            var avatar = origin + new Vector2(Ui(20f), identityY);
            DrawPartyRoomHostAvatar(draw, avatar, Ui(82f), hostName);
            var identity = avatar + UiVec(98f, 2f);
            ImGui.SetCursorScreenPos(identity);
            using var uiFontScale6117=UiFonts.PushRelative(1.35f);
            ImGui.TextColored(Hex(0xE2E7EF), FitPartyTextToWidth($"{hostName}'s Watch Party", contentWidth - Ui(98f)));
            uiFontScale6117.Dispose();
            ImGui.SetCursorScreenPos(identity + UiVec(0f, 32f));
            DrawUsername(isHost ? "● You're hosting" : $"Hosted by {hostName}",AccentHover);
            var tag = compact ? avatar + UiVec(0f, 94f) : identity + UiVec(0f, 60f);
            tag.X += DrawWatchPartyTag(draw, tag, WatchPartyVisibilityIcon(stream.RoomKind),
                WatchPartyVisibilityText(stream.RoomKind), WatchPartyVisibilityColor(stream.RoomKind)) + Ui(7f);
            tag.X += DrawWatchPartyTag(draw, tag, PartyRoomCategoryIcon(category),
                PartyRoomCategoryName(category).ToUpperInvariant(), PartyRoomCategoryColor(category)) + Ui(7f);
            if (adult) DrawWatchPartyAdultTag(draw, tag);

            if(capacityHost is not null)
            {
                ImGui.SetCursorScreenPos(origin+new Vector2(Ui(20f),capacityY));
                using (UiFonts.PushRelative(.90f)) DrawCurrentRoomCapacity();
            }

            ImGui.SetCursorScreenPos(origin + new Vector2(Ui(20f), descriptionY));
            ImGui.TextColored(MutedText, "Description");
            ImGui.SetCursorScreenPos(origin + new Vector2(Ui(20f), descriptionY + Ui(25f)));
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + contentWidth);
            ImGui.TextUnformatted(description);
            ImGui.PopTextWrapPos();
            draw.AddLine(origin + new Vector2(Ui(20f), dividerY),
                origin + new Vector2(width - Ui(20f), dividerY), ImGui.GetColorU32(BorderSubtle));
            var details = origin + new Vector2(Ui(20f), dividerY + Ui(14f));
            DrawPartyRoomDetailCell("location", FontAwesomeIcon.MapMarkerAlt, "Location", location, details, locationWidth);
            var serverOrigin = compact ? details + new Vector2(0f, detailHeight + Ui(8f))
                : details + new Vector2(contentWidth * 0.68f + Ui(12f), 0f);
            if (!compact)
            {
                var dividerX = details.X + contentWidth * 0.68f;
                draw.AddLine(new Vector2(dividerX, details.Y), new Vector2(dividerX, details.Y + detailHeight),
                    ImGui.GetColorU32(BorderSubtle));
            }
            DrawPartyRoomDetailCell("server", FontAwesomeIcon.Server, "Server",
                string.IsNullOrWhiteSpace(server) ? "Server not available" : server,
                serverOrigin, compact ? contentWidth : contentWidth * 0.32f - Ui(12f));
        });
    }

    private void DrawActivePartyMembers()
    {
        DrawPartyHeaderCard();
        if (stream.Mode is not (StreamMode.Hosting or StreamMode.Viewing)) return;
        ActivePartyPanelGap();
        var isHost = stream.Mode == StreamMode.Hosting;
        var roster = stream.Roster;
        var count = roster.Length + 1;
        var width = ImGui.GetContentRegionAvail().X;
        var columns = width >= Ui(700f) ? 2 : 1;
        var rows = (count + columns - 1) / columns;
        var memberWidth = (width - Ui(40f) - Ui(12f) * (columns - 1)) / columns;
        var memberHeaderHeight = width < Ui(560f) ? 114f : 74f;
        var memberActionsY = width < Ui(560f) ? 52f : 14f;
        var height = Ui(memberHeaderHeight) + rows * Ui(76f) + (rows - 1) * Ui(12f) + Ui(18f);
        DrawActivePartyCard("##partyWatchingPanel", new Vector2(width, height), () =>
        {
            var origin = ImGui.GetWindowPos();
            using var uiFontScale9510=UiFonts.PushRelative(1.12f);
            DrawSectionTitle(FontAwesomeIcon.UserFriends, $"Party members · {count}");
            uiFontScale9510.Dispose();
            ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(296f), Ui(memberActionsY)));
            using (UiDisabled.Scope(!isHost || partyModerationBusy))
                if (ActivePartyButton("Manage bans", UiVec(124f, 36f))) OpenPartyBans();
            ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(164f), Ui(memberActionsY)));
            using (UiDisabled.Scope(!CanInviteToCurrentWatchParty()))
            {
                if (ActivePartyButton("Invite friends", UiVec(144f, 36f), primary: true))
                {
                    activePartyInviteOpen = true;
                    activePartyInviteSearch = string.Empty;
                    if (CurrentSession is { } session && !friendsLoading) RefreshFriends(session.Token);
                }
            }
            var hostName = isHost ? CurrentDisplayName ?? CurrentSession?.DisplayName ?? "You"
                : joinedHostDisplayName ?? "Host";
            var hostId = isHost ? CurrentSession?.AccountId ?? string.Empty : stream.HostId ?? string.Empty;
            for (var index = 0; index < count; index++)
            {
                ImGui.SetCursorScreenPos(origin + new Vector2(
                    Ui(20f) + index % columns * (memberWidth + Ui(12f)),
                    Ui(memberHeaderHeight) + index / columns * Ui(88f)));
                var member = index == 0 ? null : roster[index - 1];
                ImGui.PushID(index);
                DrawPartyMemberRow(index == 0 ? hostId : member!.UserId,
                    index == 0 ? hostName : string.IsNullOrWhiteSpace(member!.DisplayName) ? "Viewer" : member.DisplayName,
                    index == 0, isHost && index != 0,
                    index == 0 ? null : () => { _ = TransferWatchPartyHostAsync(member!.UserId); }, memberWidth);
                ImGui.PopID();
            }
        });
        DrawActivePartyInviteOverlay();
    }

    private void DrawActivePartyPlaybackLayout(VideoQueueEntry? current, float position, float duration, bool paused)
    {
        var broadcastMethod = stream.Mode == StreamMode.Hosting ? HostBroadcastPanelMethod() : BroadcastAllowanceMethod.None;
        var compactAudio = StudioTvTransportActive || broadcastMethod == BroadcastAllowanceMethod.DjLive ||
            video.IsAudioOnly && IsBroadcastPartyMedia(current);
        // Audio broadcasts use the same fixed-size player as radio. Keep the
        // larger preview for video broadcasts and exclusive playback offers.
        var expanded = TryGetExclusivePlaybackStreamOffer(out _) || !compactAudio &&
            (broadcastMethod != BroadcastAllowanceMethod.None || IsBroadcastPartyMedia(current));
        if (expanded)
            DrawActivePartyNowPlaying(current, position, duration, paused, ImGui.GetContentRegionAvail().X);
        else DrawCompactPartyPlayback(current, position, duration, paused);
        if (!expanded && compactAudio && stream.Mode == StreamMode.Hosting && current is not null)
        {
            ActivePartyPanelGap();
            DrawCompactPartyBroadcastControls(current, broadcastMethod);
        }
        ActivePartyPanelGap();
        DrawCompactPartyTvAudio(current);
        ActivePartyPanelGap();
        DrawWatchPartyQueuePanel();
    }

    private void DrawCompactPartyBroadcastControls(VideoQueueEntry current, BroadcastAllowanceMethod method)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var stacked = width < Ui(600);
        var allowance = method != BroadcastAllowanceMethod.None;
        var notice = djAutoMuteNoticeVisible && Plugin.Cfg.Muted &&
            AudioVisualizerSelection.IsSameMedia(current.Url, djAutoMutedStreamUrl ?? string.Empty);
        var allowanceHeight = allowance ? (stacked ? 162f : 112f) + (ConfiguredPatreonTier() == 0 ? 24f : 0f) : 0f;
        DrawActivePartyCard("##compactPartyBroadcastControls", new(width, Ui(76f + (notice ? 80f : 0f) + allowanceHeight)), () =>
        {
            var origin = ImGui.GetCursorScreenPos();
            var inner = ImGui.GetContentRegionAvail().X;
            if (ActivePartyButton("Adjust Music Scene", new(inner, Ui(36))))
                OpenAudioPresentationSettings(current);
            var extraY = 46f;
            if (notice)
            {
                ImGui.SetCursorScreenPos(origin + UiVec(0, extraY));
                using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
                    ImGui.TextWrapped("Your TV audio is muted to prevent DJ stream feedback.");
                if (ActivePartyButton("Unmute host TV", UiVec(148, 30)))
                {
                    Plugin.Cfg.Muted = false;
                    video.SetOutputMuted(false);
                    ApplyListeningVolume();
                    Plugin.Cfg.Save();
                    djAutoMuteNoticeVisible = false;
                    djAutoMutedStreamUrl = null;
                }
                extraY += 80f;
            }
            if (allowance)
                DrawActivePartyBroadcastAllowance(origin + UiVec(0, extraY), inner, method, stacked);
        });
    }

    private void DrawActivePartyNowPlaying(VideoQueueEntry? current, float position, float duration, bool paused, float width)
    {
        var contentWidth = width - Ui(40f);
        var stacked = width < Ui(760f);
        var compactHeader = width < Ui(760f);
        var mediaTop = Ui(compactHeader ? 108f : 62f);
        var broadcastMethod = stream.Mode == StreamMode.Hosting ? HostBroadcastPanelMethod() : BroadcastAllowanceMethod.None;
        var showStreamOffer = TryGetExclusivePlaybackStreamOffer(out var isBrowserOffer);
        var thumbWidth = stacked ? MathF.Min(contentWidth, Ui(360f)) : MathF.Min(contentWidth * 0.47f, Ui(430f));
        var thumbHeight = thumbWidth * 9f / 16f;
        var detailWidth = stacked ? contentWidth : contentWidth - thumbWidth - Ui(22f);
        var title = current is null ? "Nothing is playing" : PrepareTwitchDisplayText(
            WatchPartyMediaDisplay.Title(current.Url, current.Title, video.IsAudioOnly));
        using var uiFontScale13405=UiFonts.PushRelative(1.25f);
        var titleHeight = ImGui.CalcTextSize(title, false, detailWidth).Y;
        uiFontScale13405.Dispose();
        // Reserve the actual wrapped title height; long titles must not collide with controls.
        var detailHeight = titleHeight + Ui(155f);
        var mediaHeight = current is null ? Ui(86f) : stacked ? thumbHeight + Ui(16f) + detailHeight
            : MathF.Max(thumbHeight, detailHeight);
        if (showStreamOffer)
            mediaHeight = Ui(88f) + ImGui.CalcTextSize(ExclusivePlaybackStreamOfferMessage(isBrowserOffer), false, contentWidth).Y;
        var audioOptions = !showStreamOffer && current is not null && video.IsAudioOnly && stream.Mode == StreamMode.Hosting;
        var djNotice = !showStreamOffer && current is not null && djAutoMuteNoticeVisible && stream.Mode == StreamMode.Hosting &&
            Plugin.Cfg.Muted && AudioVisualizerSelection.IsSameMedia(current.Url, djAutoMutedStreamUrl ?? string.Empty);
        var extraHeight = Ui((audioOptions ? 48f : 0f) + (djNotice ? 55f : 0f));
        var queueHeight = Ui(broadcastMethod != BroadcastAllowanceMethod.None ? (stacked ? 182f : 112f) : stacked ? 152f : 82f);
        var height = mediaTop + mediaHeight + extraHeight + queueHeight + Ui(20f);
        DrawActivePartyCard("##partyNowPlayingHero", new Vector2(width, height), () =>
        {
            using var uiFontScale14788=UiFonts.PushRelative(1.12f);
            DrawSectionTitle(screenController.Engine.AudioOnlyMode ? FontAwesomeIcon.Headphones : FontAwesomeIcon.PlayCircle,
                screenController.Engine.AudioOnlyMode ? "Now playing · Audio Only" : "Now playing");
            uiFontScale14788.Dispose();
            if (DrawActivePartyRoomControls(width, compactHeader)) return;
            var origin = ImGui.GetWindowPos() + new Vector2(Ui(20f), mediaTop);
            if (showStreamOffer)
            {
                DrawExclusivePlaybackStreamOfferInline(origin, contentWidth, isBrowserOffer);
            }
            else if (current is null)
            {
                ImGui.SetCursorScreenPos(origin);
                ImGui.TextColored(Hex(0xE2E7EF), "Nothing is playing");
                DrawBrowserHelper(stream.Mode == StreamMode.Hosting
                    ? "Choose some media when you're ready." : "Waiting for the host to start something.");
                DrawPartyCountdownControls(UiVec(140f, 34f));
            }
            else
            {
                DrawActivePartyThumbnail(current, origin, new Vector2(thumbWidth, thumbHeight));
                var details = stacked ? origin + new Vector2(0f, thumbHeight + Ui(16f))
                    : origin + new Vector2(thumbWidth + Ui(22f), 0f);
                ImGui.SetCursorScreenPos(details);
                using var uiFontScale16084=UiFonts.PushRelative(1.25f);
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + detailWidth);
                ImGui.TextUnformatted(title);
                ImGui.PopTextWrapPos();
                uiFontScale16084.Dispose();
                ImGui.SetCursorScreenPos(details + new Vector2(0f, titleHeight + Ui(12f)));
                ImGui.TextColored(MutedText, FitPartyTextToWidth(current.Source ?? string.Empty, detailWidth));
                var stateY = titleHeight + Ui(43f);
                ImGui.SetCursorScreenPos(details + new Vector2(0f, stateY));
                var state = video.State;
                var status = state == VideoPlaybackState.Failed ? "Playback unavailable"
                    : state is VideoPlaybackState.Idle or VideoPlaybackState.Loading ? "Connecting…"
                    : paused ? "Paused" : "Playing";
                var statusColor = state == VideoPlaybackState.Failed ? Danger : paused ? Gold : Good;
                var arcadeActivity = current.Source?.Equals("Arcade", StringComparison.OrdinalIgnoreCase) == true ||
                    PartyEntryCanvasActivity(current.Url, current.Title) is not (ActiveActivityKind.None or ActiveActivityKind.Whiteboard);
                var hostBroadcastMedia = stream.Mode == StreamMode.Hosting &&
                    (broadcastMethod != BroadcastAllowanceMethod.None || IsBroadcastPartyMedia(current) ||
                     video.IsPlayingGame || video.IsPlayingBrowser);
                if (arcadeActivity)
                    (status, statusColor) = ("Gaming", Good);
                else if (hostBroadcastMedia)
                    (status, statusColor) = HostPartyBroadcastStatus(broadcastMethod);
                DrawPartyStatusLine(status, statusColor);
                if (!arcadeActivity && !hostBroadcastMedia && state is (VideoPlaybackState.Playing or VideoPlaybackState.Paused))
                {
                    ImGui.SameLine(0f, Ui(16f));
                    ImGui.TextColored(MutedText, !IsPartyLiveMedia(current) && duration > 0f
                        ? $"{FormatTime(position)} / {FormatTime(duration)}" : "LIVE");
                }
                ImGui.SetCursorScreenPos(details + new Vector2(0f, stateY + Ui(32f)));
                using (ImRaii.PushColor(ImGuiCol.PlotHistogram, Accent))
                    ImGui.ProgressBar(!arcadeActivity && !IsPartyLiveMedia(current) && duration > 0f
                        ? Math.Clamp(position / duration, 0f, 1f) : 0f,
                        new Vector2(detailWidth, Ui(7f)), string.Empty);
                var controls = details + new Vector2(0f, stateY + Ui(54f));
                var canTransport = !arcadeActivity && stream.Mode == StreamMode.Hosting &&
                    state is VideoPlaybackState.Playing or VideoPlaybackState.Paused &&
                    !video.IsPlayingGame && !video.IsPlayingBrowser && !video.IsPlayingWhiteboard &&
                    !IsBroadcastPartyMedia(current);
                ImGui.SetCursorScreenPos(controls);
                DrawPartyTransportRestartButton("partyRestart", Ui(42f), StudioTvTransportActive ? !CanSkipStudioSong : !canTransport || IsPartyLiveMedia(current),
                    StudioTvTransportActive ? "Previous song" : "Restart video");
                ImGui.SetCursorScreenPos(controls + UiVec(52f, 0f));
                DrawPartyTransportPlayPauseButton("partyPlayPause", Ui(42f), !canTransport, paused);
                ImGui.SetCursorScreenPos(controls + UiVec(104f, 0f));
                DrawPartyTransportNextButton("partyNext", Ui(42f),
                    StudioTvTransportActive ? !CanSkipStudioSong : !canTransport || video.IsPlayingLocalVideo ||
                    !queue.Entries.Any(entry => entry.Id != queue.Current?.Id),
                    StudioTvTransportActive ? "Next song" : "Next video");
                ImGui.SetCursorScreenPos(controls + UiVec(156f, 0f));
                DrawSubtitleButton("partySubtitles", Ui(42f));
                ImGui.SetCursorScreenPos(controls + new Vector2(detailWidth - Ui(138f), 0f));
                DrawPartyCountdownControls(UiVec(138f, 42f));
            }
            var extraY = origin.Y + mediaHeight;
            if (audioOptions)
            {
                ImGui.SetCursorScreenPos(new Vector2(origin.X, extraY + Ui(6f)));
                if (ActivePartyButton("Adjust Music Scene", new Vector2(contentWidth, Ui(36f))))
                    OpenAudioPresentationSettings(current!);
                extraY += Ui(48f);
            }
            if (djNotice)
            {
                ImGui.SetCursorScreenPos(new Vector2(origin.X, extraY + Ui(3f)));
                using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
                    ImGui.TextWrapped("Your TV audio is muted to prevent DJ stream feedback.");
                if (ActivePartyButton("Unmute host TV", UiVec(148f, 30f)))
                {
                    Plugin.Cfg.Muted = false;
                    video.SetOutputMuted(false);
                    ApplyListeningVolume();
                    Plugin.Cfg.Save();
                    djAutoMuteNoticeVisible = false;
                    djAutoMutedStreamUrl = null;
                }
            }
            var stripOrigin = new Vector2(origin.X, origin.Y + mediaHeight + extraHeight + Ui(10f));
            if (broadcastMethod != BroadcastAllowanceMethod.None)
                DrawActivePartyBroadcastAllowance(stripOrigin, contentWidth, broadcastMethod, stacked);
            else
                DrawActivePartyQueueStrip(stripOrigin, contentWidth, stacked);
        });
    }

    private void DrawActivePartyThumbnail(VideoQueueEntry entry, Vector2 origin, Vector2 size)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(FrameBg), Ui(9f));
        var thumbnail = thumbnails.Get(entry.ThumbnailUrl);
        if (thumbnail is not null)
        {
            draw.AddImageRounded(thumbnail.Handle, origin, origin + size, Vector2.Zero, Vector2.One, uint.MaxValue, Ui(9f));
            return;
        }
        DrawPartyThumbnailFallback(entry, origin, size);
    }

    private void DrawActivePartyBroadcastAllowance(Vector2 origin, float width,
        BroadcastAllowanceMethod method, bool stacked)
    {
        var bitrate = method == BroadcastAllowanceMethod.Obs
            ? Math.Max(0d, Interlocked.CompareExchange(ref obsMeasuredBitrateMbps, 0d, 0d)) : 0d;
        var snapshot = BroadcastAllowanceSnapshotFor(method, bitrate);
        ImGui.GetWindowDrawList().AddLine(origin, origin + new Vector2(width, 0f), ImGui.GetColorU32(BorderSubtle));
        ImGui.SetCursorScreenPos(origin + UiVec(0f, 18f));
        using var uiFontScale22944=UiFonts.PushRelative(0.85f);
        ImGui.TextColored(MutedText, "WEEKLY BROADCAST ALLOWANCE");
        var label = $"{FormatWholeCredits(snapshot.RemainingTokens)} / {FormatWholeCredits(snapshot.TotalTokens)} credits remaining";
        ImGui.SetCursorScreenPos(origin + new Vector2(stacked ? 0f : width - ImGui.CalcTextSize(label).X,
            Ui(stacked ? 43f : 18f)));
        ImGui.TextUnformatted(label);
        uiFontScale22944.Dispose();
        var barY = Ui(stacked ? 70f : 46f);
        ImGui.SetCursorScreenPos(origin + new Vector2(0f, barY));
        DrawPartyAllowanceBar(width, (float)snapshot.RemainingFraction);
        ImGui.SetCursorScreenPos(origin + new Vector2(0f, barY + Ui(24f)));
        using var uiFontScale23658=UiFonts.PushRelative(0.80f);
        var usage = recentBroadcastCreditsPerMinute is { } average
            ? $"Average: {average:0.##} credits/min · " + (average > 0.0001d
                ? $"Estimated remaining: {FormatBroadcastTimeRemaining(snapshot.RemainingTokens / average)}"
                : "No credits used in the recent sample")
            : "Calculating recent usage…";
        var reset = "Resets Monday (00:00 UTC)";
        var resetWidth = ImGui.CalcTextSize(reset).X;
        ImGui.TextColored(MutedText, FitPartyTextToWidth(usage, stacked ? width : width - resetWidth - Ui(20f)));
        ImGui.SetCursorScreenPos(origin + new Vector2(stacked ? 0f : width - resetWidth,
            barY + Ui(stacked ? 47f : 24f)));
        ImGui.TextColored(MutedText, reset);
        if (ConfiguredPatreonTier() == 0)
        {
            using var uiFontScale24510=UiFonts.PushRelative(0.68f);
            ImGui.SetCursorScreenPos(origin + new Vector2(0f, barY + Ui(stacked ? 70f : 47f)));
            DrawPatreonUpgradeLink();
        }
        uiFontScale23658.Dispose();
    }

    private void DrawActivePartyQueueStrip(Vector2 origin, float width, bool stacked)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.AddLine(origin, origin + new Vector2(width, 0f), ImGui.GetColorU32(BorderSubtle));
        ImGui.SetCursorScreenPos(origin + UiVec(0f, 18f));
        ImGui.TextColored(AccentHover, "Up next");
        var viewing = stream.Mode == StreamMode.Viewing;
        var count = viewing ? viewerPartyQueueCount : queue.Entries.Count;
        var next = viewing ? viewerPartyNextQueueEntry : queue.Entries.FirstOrDefault(entry => entry.Id != queue.Current?.Id);
        var buttonWidth = Ui(146f);
        var nextOrigin = origin + UiVec(stacked ? 0f : 100f, stacked ? 84f : 12f);
        var itemWidth = width - (nextOrigin.X - origin.X) - (stacked ? 0f : buttonWidth + Ui(14f));
        if (next is null || count == 0)
        {
            ImGui.SetCursorScreenPos(nextOrigin + UiVec(0f, 8f));
            ImGui.TextColored(MutedText, viewing ? "No next item shared by the host" : "Nothing queued next");
        }
        else
        {
            var size = UiVec(94f, 53f);
            DrawActivePartyThumbnail(next, nextOrigin, size);
            ImGui.SetCursorScreenPos(nextOrigin + UiVec(106f, 3f));
            ImGui.TextUnformatted(FitPartyTextToWidth(PrepareTwitchDisplayText(next.Title), itemWidth - Ui(106f)));
            ImGui.SetCursorScreenPos(nextOrigin + UiVec(106f, 29f));
            ImGui.TextColored(MutedText, FitPartyTextToWidth(next.Source ?? string.Empty, itemWidth - Ui(106f)));
        }
        ImGui.SetCursorScreenPos(origin + new Vector2(width - buttonWidth, Ui(14f)));
        using (UiDisabled.Scope(viewing))
        {
            if (ActivePartyButton($"View queue ({count})", new Vector2(buttonWidth, Ui(36f))))
            {
                currentPage = HomePage.Player;
                activePlayerDrawer = PlayerDrawer.Queue;
            }
        }
        if (viewing && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiFonts.SetTooltip("The host manages the queue. Their next item is shown here.");

        using var uiFontScale26874=UiFonts.PushRelative(0.85f);
        var switchWidth = ImGui.CalcTextSize("Switch queue").X + Ui(8f);
        ImGui.SetCursorScreenPos(origin + new Vector2(width - buttonWidth + (buttonWidth - switchWidth) * 0.5f, Ui(56f)));
        using (UiDisabled.Scope(viewing))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f))
        using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero)
            .Push(ImGuiCol.ButtonHovered, Vector4.Zero)
            .Push(ImGuiCol.ButtonActive, Vector4.Zero)
            .Push(ImGuiCol.Text, AccentHover))
        {
            if (ImGui.Button("Switch Queue##partySwitchQueue", new Vector2(switchWidth, Ui(22f))))
            {
                currentPage = HomePage.Player;
                activePlayerDrawer = PlayerDrawer.Queue;
            }
        }
        uiFontScale26874.Dispose();
        if (viewing && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiFonts.SetTooltip("The host manages the queue.");
    }

    private void DrawActivePartyDisplay(float width, float height)
    {
        DrawActivePartyCard("##partyDisplayPanel", new Vector2(width, height), () =>
        {
            using var uiFontScale28056=UiFonts.PushRelative(1.12f);
            DrawSectionTitle(FontAwesomeIcon.Tv, "TV & display");
            uiFontScale28056.Dispose();
            var origin = ImGui.GetWindowPos() + UiVec(20f, 62f);
            var innerWidth = width - Ui(40f);
            var isHost = stream.Mode == StreamMode.Hosting;
            var spawned = isHost ? screenController.Engine.IsActive : ViewerTvEnabled;
            ImGui.SetCursorScreenPos(origin);
            DrawPartyStatusLine(spawned ? "TV spawned" : "TV not spawned", spawned ? Good : MutedText);
            DrawBrowserHelper(screenController.Engine.AudioOnlyMode ? "Listening in Audio Only mode. Open a player to return to video." :
                spawned ? "Your watch-party screen is active." : "Spawn your TV to start watching.");
            ImGui.SetCursorScreenPos(origin + UiVec(0f, 56f));
            var displayButtonWidth = (innerWidth - Ui(10f)) * 0.5f;
            DrawPartyTvSpawnButton(displayButtonWidth);
            ImGui.SetCursorScreenPos(origin + new Vector2(displayButtonWidth + Ui(10f), Ui(56f)));
            var miniPlayerOpen = IsMiniPlayerOpen?.Invoke() ?? false;
            if (ActivePartyButton(miniPlayerOpen ? "Close mini player" : "Open mini player",
                    new Vector2(displayButtonWidth, Ui(42f)), primary: !miniPlayerOpen))
                OnMiniPlayerToggleRequested?.Invoke();
            var dividerY = origin.Y + Ui(112f);
            ImGui.GetWindowDrawList().AddLine(new Vector2(origin.X, dividerY),
                new Vector2(origin.X + innerWidth, dividerY), ImGui.GetColorU32(BorderSubtle));
            ImGui.SetCursorScreenPos(new Vector2(origin.X, dividerY + Ui(12f)));
            if (isHost)
            {
                ImGui.TextColored(AccentHover, "You're hosting the TV position");
                DrawBrowserHelper("Viewers can follow your screen placement.");
            }
            else
            {
                var sync = partySyncTvPlacement;
                DrawPartyPlacementToggle(ref sync, false);
                if (sync != partySyncTvPlacement)
                {
                    SetPartySyncTvPlacement(sync);
                    if (!sync) ApplySavedViewerScreenPlacement();
                }
                ImGui.SameLine(0f, Ui(9f));
                ImGui.TextUnformatted(innerWidth < Ui(340f)
                    ? "Follow host TV placement" : "Match TV position & size with host");
                ImGui.TextColored(MutedText, hostScreenSyncPause.IsPaused
                    ? "Host position sync is temporarily paused."
                    : sync ? "Following the host's screen placement." : "Using your own screen placement.");
            }
            ImGui.SetCursorScreenPos(new Vector2(origin.X, dividerY + Ui(66f)));
            var canEdit = isHost || (stream.Mode == StreamMode.Viewing && !PartySyncTvPlacement);
            using (UiDisabled.Scope(!canEdit))
                if (ActivePartyButton("Reset screen position", new Vector2(displayButtonWidth, Ui(34f))))
                    screenController.Engine.RecenterScreen();
            if (!canEdit && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                UiFonts.SetTooltip("Disable TV position sync to reset your screen position.");
            ImGui.SameLine(0f, Ui(10f));
            using (UiDisabled.Scope(!canEdit))
                if (ActivePartyButton("Screen settings", new Vector2(displayButtonWidth, Ui(34f)))) currentPage = HomePage.Screen;
            if (!canEdit && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                UiFonts.SetTooltip("Disable TV position sync to customize your screen.");
        });
    }

    private void DrawActivePartyAudio(VideoQueueEntry? current, float width, float height,
        BroadcastAllowanceMethod method, bool viewerBroadcast)
    {
        DrawActivePartyCard("##partyAudioSyncPanel", new Vector2(width, height), () =>
        {
            using var uiFontScale31948=UiFonts.PushRelative(1.12f);
            DrawSectionTitle(FontAwesomeIcon.VolumeUp, "Audio & playback sync");
            uiFontScale31948.Dispose();
            var origin = ImGui.GetWindowPos() + UiVec(20f, 62f);
            var innerWidth = width - Ui(40f);
            DrawPartyScreenControllerAudio(origin.X, origin.Y, innerWidth);
            var dividerY = origin.Y + Ui(112f);
            ImGui.GetWindowDrawList().AddLine(new Vector2(origin.X, dividerY),
                new Vector2(origin.X + innerWidth, dividerY), ImGui.GetColorU32(BorderSubtle));
            var syncOrigin = new Vector2(origin.X, dividerY + Ui(14f));
            if (stream.Mode == StreamMode.Hosting &&
                (method != BroadcastAllowanceMethod.None || IsBroadcastPartyMedia(current) ||
                 video.IsPlayingGame || video.IsPlayingBrowser))
                DrawHostBroadcastSyncPanel(syncOrigin, innerWidth, method);
            else if (viewerBroadcast) DrawViewerBroadcastSyncPanel(syncOrigin, innerWidth, current!);
            else DrawPartyPlaybackSyncPanel(syncOrigin, innerWidth);
        });
    }

    private bool DrawActivePartyRoomControls(float width, bool compact)
    {
        var isHost = stream.Mode == StreamMode.Hosting;
        var host = isHost ? CurrentDisplayName ?? "You" : joinedHostDisplayName ?? "Host";
        var origin = ImGui.GetWindowPos();
        var text = $"{stream.Roster.Length} watching · Host: {host}";
        var maxTextWidth = MathF.Max(Ui(40f), width - Ui(compact ? 254f : 434f));
        text = FitPartyTextToWidth(text, maxTextWidth);
        var textWidth = ImGui.CalcTextSize(text).X;
        var rowY = Ui(compact ? 64f : 25f);
        var textX = compact ? Ui(20f) : width - Ui(224f) - textWidth;
        ImGui.SetCursorScreenPos(origin + new Vector2(textX, rowY));
        using (UiFonts.PushIcon())
            ImGui.TextColored(MutedText, FontAwesomeIcon.UserFriends.ToIconString());
        ImGui.SameLine(0f, Ui(8f));
        ImGui.TextColored(MutedText, text);
        ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(174f), rowY - Ui(10f)));
        if (!ActivePartyButton(isHost ? "End Watch Party##nowPlaying" : "Leave Watch Party##nowPlaying",
                UiVec(154f, 36f), danger: true)) return false;
        RequestLeaveWatchParty();
        return true;
    }

    private void DrawActivePartyInviteOverlay()
    {
        if (!activePartyInviteOpen) return;
        if (CurrentSession is not { } session || !CanInviteToCurrentWatchParty())
        {
            activePartyInviteOpen = false;
            return;
        }
        var parent = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var size = new Vector2(MathF.Min(Ui(540f), parentSize.X - Ui(36f)), MathF.Min(Ui(480f), parentSize.Y - Ui(36f)));
        ImGui.SetNextWindowPos(parent, ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##activePartyInviteOverlay", flags)) { ImGui.End(); return; }
        ImGui.GetWindowDrawList().AddRectFilled(parent, parent + parentSize,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.58f)));
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) activePartyInviteOpen = false;
        ImGui.SetCursorScreenPos(parent + (parentSize - size) * 0.5f);
        DrawActivePartyCard("##activePartyInvitePanel", size, () =>
        {
            var origin = ImGui.GetCursorScreenPos();
            var innerWidth = ImGui.GetContentRegionAvail().X;
            using (UiFonts.PushRelative(1f, emphasis: true))
                DrawSectionTitle(FontAwesomeIcon.UserFriends, "Invite friends");
            var afterHeading = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(origin + new Vector2(innerWidth - Ui(28f), 0f));
            if (ActivePartyButton("×##closePartyInvites", UiVec(28f, 28f))) activePartyInviteOpen = false;
            ImGui.SetCursorScreenPos(afterHeading + UiVec(0f, 8f));
            DrawBrowserHelper("Send a Watch Party invitation in a direct message.");
            ImGui.SetNextItemWidth(innerWidth);
            ImGui.InputTextWithHint("##partyInviteSearch", "Search friends...", ref activePartyInviteSearch, 100);
            using var list = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##partyInviteList", new Vector2(innerWidth, ImGui.GetContentRegionAvail().Y), false));
            if (!list) return;
            if (friendsLoading) ImGui.TextColored(MutedText, "Loading friends…");
            else if (friendsLoadError is { } error)
            {
                ImGui.TextColored(Danger, error);
                if (ImGui.Button("Retry##partyInviteFriends")) RefreshFriends(session.Token);
            }
            else
            {
                var matches = friends.Where(friend => string.IsNullOrWhiteSpace(activePartyInviteSearch) ||
                    friend.DisplayName.Contains(activePartyInviteSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 0) ImGui.TextColored(MutedText, "No friends found.");
                foreach (var friend in matches)
                {
                    ImGui.PushID(friend.AccountId);
                    var row = ImGui.GetCursorScreenPos();
                    var rowWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X - Ui(12f));
                    DrawUsername(friend.AccountId,FitPartyTextToWidth(friend.DisplayName, rowWidth - Ui(100f)),Vector4.One);
                    ImGui.SetCursorScreenPos(row + new Vector2(rowWidth - Ui(80f), 0f));
                    if (ActivePartyButton("Invite", UiVec(80f, 32f), primary: true))
                    {
                        activePartyInviteOpen = false;
                        StartOrOpenConversation(session, friend.AccountId, friend.DisplayName, CreateWatchPartyInviteMessage());
                        ImGui.PopID();
                        break;
                    }
                    ImGui.SetCursorScreenPos(row + UiVec(0f, 44f));
                    ImGui.PopID();
                }
            }
        });
        ImGui.End();
    }
}

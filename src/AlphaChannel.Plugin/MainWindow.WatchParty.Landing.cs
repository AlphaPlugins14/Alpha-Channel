using AlphaChannel.Contracts;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawWatchPartyLandingContent()
    {
        RefreshHomeWatchPartiesIfNeeded();
        if (friendsDirty && !friendsLoading && CurrentSession is { } session) RefreshFriends(session.Token);
        var width = ImGui.GetContentRegionAvail().X;
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8, 8)).Push(ImGuiStyleVar.FrameRounding, Ui(8));
        DrawWatchPartyWelcome(width);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Ui(4));
        DrawWatchPartyLandingActions(width);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Ui(4));
        DrawWatchPartyHostingFriends(width, homeWatchPartyRooms);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Ui(4));
        DrawWatchPartyBrowseFooter(width, homeWatchPartyRooms);
    }

    private void DrawWatchPartyWelcome(float width)
    {
        var origin = ImGui.GetCursorScreenPos();
        var showScene = width >= Ui(640);
        var columns = width >= Ui(660) ? 4 : 2;
        var copyHeight = Ui(showScene ? 144 : 132);
        var featureHeight = Ui(columns == 4 ? 46 : 72);
        var size = new Vector2(width, copyHeight + featureHeight);
        var draw = ImGui.GetWindowDrawList();
        DrawWatchPartyLandingPanel(origin, size);
        var copyWidth = showScene ? width * .52f : width;
        var title = origin + UiVec(24, 32);
        using (UiFonts.PushRelative(width < Ui(430) ? 1.6f : 1.85f, emphasis: true)) {
            UiFonts.DrawText(draw, ImGui.GetFontSize(), title, ImGui.GetColorU32(Vector4.One), "Better ");
            var firstWidth = ImGui.CalcTextSize("Better ").X;
            UiFonts.DrawText(draw, ImGui.GetFontSize(), title + new Vector2(firstWidth, 0), ImGui.GetColorU32(AccentHover), "together.");
        }
        UiFonts.DrawText(draw, ImGui.GetFontSize(), origin + UiVec(24, 74), ImGui.GetColorU32(MutedText),
            "Watch, listen and play with friends across Eorzea.", copyWidth - Ui(48));
        if (showScene) DrawWatchPartyLandingScene(origin + new Vector2(copyWidth, Ui(2)), new Vector2(width - copyWidth - Ui(12), copyHeight - Ui(4)));
        var featureOrigin = origin + new Vector2(0, copyHeight);
        draw.AddLine(featureOrigin + UiVec(1, 0), featureOrigin + new Vector2(width - Ui(1), 0), ImGui.GetColorU32(BorderSubtle));
        (string Label, FontAwesomeIcon Icon, Vector4 Colour)[] features = [
            ("Synced playback", FontAwesomeIcon.SyncAlt, new(1f, .68f, .24f, 1)),
            ("Party chat", FontAwesomeIcon.Comments, new(.25f, .75f, .96f, 1)),
            ("Live reactions", FontAwesomeIcon.Smile, new(.98f, .36f, .68f, 1)),
            ("Multiplayer games", FontAwesomeIcon.Gamepad, new(.3f, .85f, .56f, 1))];
        using (UiFonts.PushRelative(.85f)) for (var i = 0; i < features.Length; i++) {
            var item = features[i]; var cell = featureOrigin + new Vector2(width / columns * (i % columns), columns == 4 ? 0 : Ui(36) * (i / columns));
            var cellWidth = width / columns; var cellHeight = columns == 4 ? featureHeight : Ui(36);
            var x = MathF.Max(Ui(10), (cellWidth - ImGui.CalcTextSize(item.Label).X - Ui(28)) * .5f);
            UiFonts.DrawCenteredIcon(draw, Ui(19), cell + new Vector2(x + Ui(9), cellHeight * .5f), ImGui.GetColorU32(item.Colour), item.Icon.ToIconString());
            draw.AddText(cell + new Vector2(x + Ui(28), (cellHeight - ImGui.GetFontSize()) * .5f), ImGui.GetColorU32(Vector4.One), item.Label);
            if (i % columns != columns - 1) draw.AddLine(cell + new Vector2(cellWidth, Ui(12)), cell + new Vector2(cellWidth, cellHeight - Ui(12)), ImGui.GetColorU32(BorderSubtle));
        }
        ImGui.SetCursorScreenPos(origin); ImGui.Dummy(size);
    }

    // Shared by the existing create/join overlay as well as the landing page.
    private void DrawWatchPartyLandingText(ImDrawListPtr draw, Vector2 position, string text, float width, bool heading = false)
    {
        using var font = UiFonts.PushRelative(heading ? 1f : .90f, emphasis: heading);
        UiFonts.DrawText(draw, ImGui.GetFontSize(), position, ImGui.GetColorU32(heading ? Hex(0xE2E7EF) : MutedText), ArcadeFitText(text, width));
    }

    private void DrawWatchPartyLandingPanel(Vector2 origin, Vector2 size)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(CardBg), Ui(10));
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(AccentInk(.45f)), Ui(10));
    }

    private void DrawWatchPartyLandingActions(float width)
    {
        var origin = ImGui.GetCursorScreenPos(); var stacked = width < Ui(600);
        var cardWidth = stacked ? width : (width - Ui(12)) * .5f;
        var height = Ui(248);
        DrawWatchPartyLandingActionCard(origin, cardWidth, height, create: true);
        DrawWatchPartyLandingActionCard(origin + (stacked ? new Vector2(0, height + Ui(12)) : new Vector2(cardWidth + Ui(12), 0)), cardWidth, height, create: false);
        ImGui.SetCursorScreenPos(origin); ImGui.Dummy(new Vector2(width, stacked ? height * 2 + Ui(12) : height));
        if (!string.IsNullOrWhiteSpace(joinError)) ImGui.TextColored(Danger, joinError);
    }

    private void DrawWatchPartyLandingActionCard(Vector2 origin, float width, float height, bool create)
    {
        DrawWatchPartyLandingPanel(origin, new(width, height));
        var draw = ImGui.GetWindowDrawList(); var iconCenter = origin + UiVec(42, 40);
        draw.AddCircleFilled(iconCenter, Ui(23), ImGui.GetColorU32(AccentInk(.25f)));
        UiFonts.DrawCenteredIcon(draw, Ui(25), iconCenter, ImGui.GetColorU32(AccentHover), (create ? FontAwesomeIcon.UserPlus : FontAwesomeIcon.Users).ToIconString());
        using (UiFonts.PushRelative(1f, emphasis: true))
            UiFonts.DrawText(draw, ImGui.GetFontSize(), origin + UiVec(80, 22), ImGui.GetColorU32(Vector4.One),
                FitPartyTextToWidth(create ? "Create a Watch Party" : "Join a Watch Party", width - Ui(98)));
        DrawWatchPartyLandingText(draw, origin + UiVec(80, 46), create ? "Your friends. Your party." : "Already have a host in mind?", width - Ui(98));
        if (create) {
            UiFonts.DrawText(draw, ImGui.GetFontSize(), origin + UiVec(20, 90), ImGui.GetColorU32(MutedText),
                "Choose something to watch, listen to or play. Invite friends when you're ready.", width - Ui(40));
            draw.AddLine(origin + UiVec(20, 153), origin + new Vector2(width - Ui(20), Ui(153)), ImGui.GetColorU32(BorderSubtle));
            UiFonts.DrawCenteredIcon(draw, Ui(15), origin + UiVec(28, 174), ImGui.GetColorU32(AccentHover), FontAwesomeIcon.Magic.ToIconString());
            DrawWatchPartyLandingText(draw, origin + UiVec(46, 167), "Set up your party in a few clicks.", width - Ui(66));
        }
        else {
            using var frame = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(10, 7));
            using var colour = ImRaii.PushColor(ImGuiCol.FrameBg, FrameBg).Push(ImGuiCol.FrameBgHovered, FrameBgHover);
            ImGui.SetCursorScreenPos(origin + UiVec(20, 77));
            using (UiFonts.PushRelative(.9f)) ImGui.TextColored(MutedText, "Host username");
            ImGui.SetCursorScreenPos(origin + UiVec(20, 96)); ImGui.SetNextItemWidth(width - Ui(40));
            ImGui.InputTextWithHint("##watchPartyLandingHost", "Enter Alpha Channel username", ref joinHostNameInput, 32);
            ImGui.SetCursorScreenPos(origin + UiVec(20, 132));
            using (UiFonts.PushRelative(.9f)) ImGui.TextColored(MutedText, "Password (optional)");
            ImGui.SetCursorScreenPos(origin + UiVec(20, 151)); ImGui.SetNextItemWidth(width - Ui(40));
            ImGui.InputTextWithHint("##watchPartyLandingPassword", "Only needed for locked parties", ref joinPasswordInput, 64, ImGuiInputTextFlags.Password);
        }
        ImGui.SetCursorScreenPos(origin + new Vector2(Ui(18), height - Ui(50)));
        using (UiDisabled.Scope(!create && (string.IsNullOrWhiteSpace(joinHostNameInput) || stream.WatchPartyJoinBlockReason is not null)))
        if (AlphaChatButton(create ? "landingCreate" : "landingJoin", create ? "Create party" : "Join party",
            create ? FontAwesomeIcon.Plus : FontAwesomeIcon.Users, width - Ui(36), Ui(36), primary: true)) {
            if (create) watchPartyCreateOverlayRequested = true;
            else DoJoin(joinHostNameInput.Trim(), joinPasswordInput);
        }
        if (!create && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && stream.WatchPartyJoinBlockReason is { } reason) UiFonts.SetTooltip(reason);
    }

    private void DrawWatchPartyHostingFriends(float width, RoomDirectoryDto[] rooms)
    {
        var hosts = friends.Where(f => CanJoinFriendWatchParty(f) && !AlphaChatBlocked(f.AccountId))
            .OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        var shown = hosts.Take(3).ToArray();
        var origin = ImGui.GetCursorScreenPos();
        var height = shown.Length == 0 ? Ui(112) : Ui(50 + shown.Length * 64 + (hosts.Length > shown.Length ? 32 : 4));
        DrawWatchPartyLandingPanel(origin, new(width, height));
        var draw = ImGui.GetWindowDrawList();
        UiFonts.DrawCenteredIcon(draw, Ui(21), origin + UiVec(28, 25), ImGui.GetColorU32(AccentHover), FontAwesomeIcon.Users.ToIconString());
        using (UiFonts.PushRelative(1f, emphasis: true))
            UiFonts.DrawText(draw, ImGui.GetFontSize(), origin + UiVec(48, 17), ImGui.GetColorU32(Vector4.One), "Friends hosting now");
        if (hosts.Length > 0 && width >= Ui(530)) DrawWatchPartyLandingText(draw, origin + UiVec(224, 18), $"{hosts.Length} active {(hosts.Length == 1 ? "party" : "parties")}", width - Ui(286));
        ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(46), Ui(10)));
        using (UiDisabled.Scope(homeWatchPartyLoading || friendsLoading))
        if (AlphaChatButton("landingRefreshHosts", "", FontAwesomeIcon.SyncAlt, Ui(32), Ui(30))) {
            homeWatchPartyFetchedAt = -999; RefreshHomeWatchPartiesIfNeeded();
            if (CurrentSession is { } session) RefreshFriends(session.Token);
        }
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Refresh friends and parties");
        if (shown.Length == 0) {
            var empty = friendsLoading ? "Loading your friends…" : "No friends are hosting yet.";
            UiFonts.DrawText(draw, ImGui.GetFontSize(), origin + UiVec(48, 54), ImGui.GetColorU32(MutedText), empty, width - Ui(68));
            DrawWatchPartyLandingText(draw, origin + UiVec(48, 80), friendsLoading ? "Your friends' parties will appear here." : "Create a party and invite your friends.", width - Ui(68));
        }
        for (var i = 0; i < shown.Length; i++) {
            var friend = shown[i]; var row = origin + UiVec(16, 48 + i * 64);
            var room = rooms.FirstOrDefault(r => r.HostAccountId == friend.AccountId);
            using var id = ImRaii.PushId(friend.AccountId);
            draw.AddLine(row, row + new Vector2(width - Ui(32), 0), ImGui.GetColorU32(BorderSubtle));
            ImGui.SetCursorScreenPos(row + UiVec(4, 12));
            DrawAvatarChip(friend.AvatarIcon, friend.AvatarColorHex, Ui(40), friend.AvatarImageUrl);
            draw.AddCircleFilled(row + UiVec(58, 21), Ui(4), ImGui.GetColorU32(new Vector4(.16f, .85f, .47f, 1)));
            var separateCount = room is not null && width >= Ui(600);
            var textWidth = width - Ui(separateCount ? 310 : 212);
            DrawUsernameAt(draw, row + UiVec(72, 11), FitPartyTextToWidth(friend.DisplayName, textWidth), Vector4.One, friend.AccountId);
            var activity = room is null ? "Hosting a Watch Party" : GetPartyDirectoryMedia(room).Title;
            if (string.IsNullOrWhiteSpace(activity)) activity = "Waiting for content";
            if (room is not null && !separateCount) activity += $" · {room.ViewerCount + 1} members";
            DrawWatchPartyLandingText(draw, row + UiVec(72, 34), activity, textWidth);
            if (separateCount) {
                var count = new Vector2(origin.X + width - Ui(194), row.Y + Ui(29));
                UiFonts.DrawCenteredIcon(draw, Ui(17), count, ImGui.GetColorU32(AccentHover), FontAwesomeIcon.Users.ToIconString());
                draw.AddText(count + UiVec(17, -7), ImGui.GetColorU32(MutedText), (room!.ViewerCount + 1).ToString());
            }
            ImGui.SetCursorScreenPos(new Vector2(origin.X + width - Ui(112), row.Y + Ui(14)));
            using (UiDisabled.Scope(stream.WatchPartyJoinBlockReason is not null))
            if (AlphaChatButton("landingFriendJoin", room?.Kind == RoomKind.Locked ? "Join…" : "Join",
                room?.Kind == RoomKind.Locked ? FontAwesomeIcon.Lock : FontAwesomeIcon.Users, Ui(92), Ui(34), primary: true)) {
                if (room is not null) JoinPartyDirectoryRoom(room);
                else OpenPlayerAndJoin(friend.DisplayName);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && stream.WatchPartyJoinBlockReason is { } blocked) UiFonts.SetTooltip(blocked);
        }
        if (hosts.Length > shown.Length) {
            ImGui.SetCursorScreenPos(origin + new Vector2(Ui(20), height - Ui(31)));
            if (AlphaChatTextAction("landingMoreHosts", $"See all friends · {hosts.Length - shown.Length} more hosting", FontAwesomeIcon.ArrowRight, AccentHover, width - Ui(40), Ui(25))) currentPage = HomePage.Friends;
        }
        ImGui.SetCursorScreenPos(origin); ImGui.Dummy(new Vector2(width, height));
    }

    private void DrawWatchPartyBrowseFooter(float width, RoomDirectoryDto[] rooms)
    {
        var origin = ImGui.GetCursorScreenPos(); var compact = width < Ui(620);
        var height = Ui(compact ? 124 : 76);
        DrawWatchPartyLandingPanel(origin, new(width, height));
        var draw = ImGui.GetWindowDrawList();
        draw.AddCircleFilled(origin + UiVec(36, 38), Ui(20), ImGui.GetColorU32(AccentInk(.25f)));
        UiFonts.DrawCenteredIcon(draw, Ui(23), origin + UiVec(36, 38), ImGui.GetColorU32(AccentHover), FontAwesomeIcon.Globe.ToIconString());
        var textWidth = compact ? width - Ui(92) : width - Ui(322);
        DrawWatchPartyLandingText(draw, origin + UiVec(68, 17), "Looking for more people?", textWidth, heading: true);
        DrawWatchPartyLandingText(draw, origin + UiVec(68, 42), "Find an open party and join the fun.", textWidth);
        ImGui.SetCursorScreenPos(origin + (compact ? UiVec(18, 78) : new Vector2(width - Ui(228), Ui(12))));
        if (AlphaChatButton("landingBrowseParties", "Browse public parties", FontAwesomeIcon.Globe, Ui(210), Ui(34))) currentPage = HomePage.PartyDirectory;
        var count = rooms.Count(r => r.Kind is RoomKind.Public or RoomKind.Venue);
        var availability = roomsClient.LastFailure is not null ? "Party list unavailable" : homeWatchPartyLoading && rooms.Length == 0 ? "Loading parties…" : $"{count} open {(count == 1 ? "party" : "parties")}";
        if (!compact) {
            using var statusFont = UiFonts.PushRelative(.9f);
            var label = ArcadeFitText(availability, Ui(210));
            var status = origin + new Vector2(width - Ui(228) + (Ui(210) - ImGui.CalcTextSize(label).X) * .5f, Ui(51));
            UiFonts.DrawText(draw, ImGui.GetFontSize(), status, ImGui.GetColorU32(MutedText), label);
        }
        else if (width >= Ui(440)) DrawWatchPartyLandingText(draw, origin + UiVec(246, 89), availability, width - Ui(264));
        ImGui.SetCursorScreenPos(origin); ImGui.Dummy(new Vector2(width, height));
    }
}

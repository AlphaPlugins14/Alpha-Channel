using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Home dashboard: featured media, resume history, parties, popular feeds and radio.
internal sealed partial class MainWindow
{
    private int homeFeaturedIndex, homePopularProvider;
    private double homeFeaturedChangedAt;

    private void DrawHome()
    {
        using var id = ImRaii.PushId("home");
        DrawHomeFeatured();
        DrawHomeShortcuts();
        DrawHomePlayback();
        var history = Plugin.Cfg.RecentlyWatchedVideos.Where(IsResumableHistoryMedia)
            .Where(v => v.WatchedSeconds > 0 && v.WatchedSeconds < v.DurationSeconds * .95)
            .Take(ImGui.GetContentRegionAvail().X >= Ui(860) ? 3 : 2).ToArray();
        if (history.Length > 0)
        {
            DrawHomeContinueWatching(history);
        }
        DrawHomeParties();
        DrawHomePopular();
        DrawHomeRadio();
        // Include the final standard section gap in ImGui's scrollable extent.
        ImGui.Dummy(Vector2.Zero);
    }

    private void EndHomeSection(Vector2 origin, float width, float height)
    {
        // Reserve the entire surface, including draw-list artwork and positioned controls.
        // Dummy advances by the standard item spacing, matching the header-to-banner gap.
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, height));
    }

    private void DrawHomePanel(Vector2 origin, float width, float height)
        => DrawArcadeGradientPanel(ImGui.GetWindowDrawList(), origin, new(width, height),
            HubSurface(0x0C1728), HubSurface(0x080F1D), HubTint(0x24456B), 9);

    private void DrawHomeHeading(Vector2 origin, float width, string title, FontAwesomeIcon icon,
        string? action = null, Action? onAction = null)
    {
        var draw = ImGui.GetWindowDrawList();
        UiFonts.DrawCenteredIcon(draw, Ui(21), origin + UiVec(23, 25),
            ImGui.GetColorU32(icon == FontAwesomeIcon.Fire ? Hex(0xFF4268) : AccentHover), icon.ToIconString());
        HomeText(origin + UiVec(43, 15), title, width - Ui(action is null ? 58 : action == "Browse stations" ? 203 : 138),
            strong: true, fontScale: 1.12f);
        if (action is null || onAction is null) return;
        using var font = UiFonts.PushRegularRelative(.94f);
        var label = action + "  ›";
        var labelWidth = ImGui.CalcTextSize(label).X;
        var start = origin + new Vector2(width - labelWidth - Ui(16), Ui(13));
        ImGui.SetCursorScreenPos(start);
        if (ImGui.InvisibleButton("heading" + title, new(labelWidth, Ui(27)))) onAction();
        draw.AddText(start + UiVec(0, 4), ImGui.GetColorU32(ImGui.IsItemHovered() ? Vector4.One : AccentHover), label);
    }

    private void HomeDuration(Vector2 imageOrigin, Vector2 imageSize, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        using var font = UiFonts.PushRegularRelative(.82f);
        var label = HomeTime(seconds);
        var size = ImGui.CalcTextSize(label) + UiVec(8, 5);
        var start = imageOrigin + imageSize - size - UiVec(5, 8);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + size, ImGui.GetColorU32(new Vector4(0, 0, 0, .86f)), Ui(3));
        draw.AddText(start + UiVec(4, 2), uint.MaxValue, label);
    }

    private void HomeText(Vector2 position, string text, float width, bool strong = false,
        float fontScale = 1f, Vector4? color = null)
    {
        using var font = UiFonts.PushRelative(fontScale, emphasis: strong);
        ImGui.GetWindowDrawList().AddText(position, ImGui.GetColorU32(color ?? (strong ? Vector4.One : MutedText)),
            TruncateToWidth(text, MathF.Max(1, width)));
    }

    private void HomeThumbnail(string? url, Vector2 origin, Vector2 size, FontAwesomeIcon fallback)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(FrameBg), Ui(7));
        var texture = thumbnails.Get(url);
        if (texture is not null)
        {
            var (uv0, uv1) = CoverUvs(texture.Width, texture.Height, size.X, size.Y);
            draw.AddImageRounded(texture.Handle, origin, origin + size, uv0, uv1, uint.MaxValue, Ui(7));
        }
        else
            UiFonts.DrawCenteredIcon(draw, Ui(25), origin + size * .5f,
                ImGui.GetColorU32(AccentHover), fallback.ToIconString());
    }

    private void DrawHomeFeatured()
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var narrow = width < Ui(540);
        var height = Ui(narrow ? 220 : 204);
        DrawHomePanel(origin, width, height);
        var slides = featuredSlideResults;
        homeFeaturedIndex = slides.Length == 0 ? 0 : Math.Clamp(homeFeaturedIndex, 0, slides.Length - 1);
        var hovered = ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(origin, origin + new Vector2(width, height));
        var now = ImGui.GetTime();
        if (homeFeaturedChangedAt == 0 || hovered) homeFeaturedChangedAt = now;
        if (slides.Length > 1 && now - homeFeaturedChangedAt >= 5.5)
        {
            homeFeaturedIndex = (homeFeaturedIndex + 1) % slides.Length;
            homeFeaturedChangedAt = now;
        }
        var featured = slides.ElementAtOrDefault(homeFeaturedIndex);
        var draw = ImGui.GetWindowDrawList();
        HomeThumbnail(featured?.ThumbnailUrl, origin + UiVec(1, 1), new(width - Ui(2), height - Ui(2)), FontAwesomeIcon.Film);
        // Round the shading to the image too: a rectangular overlay covers the
        // border's corner arcs even when the thumbnail itself is rounded.
        var shadeOrigin = origin + UiVec(1, 1);
        var shadeSize = new Vector2(width - Ui(2), height - Ui(2));
        var radius = Ui(7);
        var rows = Math.Max(1, (int)MathF.Ceiling(shadeSize.Y / Ui(2)));
        for (var row = 0; row < rows; row++)
        {
            var y = shadeSize.Y * row / rows;
            var nextY = shadeSize.Y * (row + 1) / rows;
            var edge = MathF.Min(y, shadeSize.Y - nextY);
            var inset = edge < radius ? radius - MathF.Sqrt(MathF.Max(0, radius * radius - MathF.Pow(radius - edge, 2))) : 0;
            var shade = HubSurface(0x070F1D);
            var left = ImGui.GetColorU32(shade with { W = .97f - .02f * y / shadeSize.Y });
            var right = ImGui.GetColorU32(shade with { W = .30f + .25f * y / shadeSize.Y });
            draw.AddRectFilledMultiColor(shadeOrigin + new Vector2(inset, y), shadeOrigin + new Vector2(shadeSize.X - inset, nextY),
                left, right, right, left);
        }
        HomeText(origin + UiVec(22, 20), "FEATURED", width - Ui(44), color: AccentHover, fontScale: .92f);
        var textWidth = narrow ? width - Ui(44) : width * .72f;
        using (UiFonts.PushRelative(narrow ? 1.25f : 1.5f, emphasis: true))
            DrawWrappedLines(draw, origin + UiVec(22, 43), textWidth, ImGui.GetTextLineHeight(), 2,
                ImGui.GetColorU32(Vector4.One), featured?.Title ?? (featuredLoadRunning ? "Loading featured videos…" : "Featured videos unavailable"));
        UiFonts.DrawCenteredIcon(draw, Ui(16), origin + new Vector2(Ui(30), height - Ui(80)),
            ImGui.GetColorU32(AccentHover), FontAwesomeIcon.PlayCircle.ToIconString());
        HomeText(origin + new Vector2(Ui(45), height - Ui(88)), featured?.ChannelName ?? "YouTube", textWidth - Ui(23), fontScale: 1f, color: Vector4.One);
        ImGui.SetCursorScreenPos(origin + new Vector2(Ui(22), height - Ui(58)));
        using (UiDisabled.Scope(featured is null))
        {
            if (GameLayoutButton("Watch now", FontAwesomeIcon.Play, Ui(132), true))
            {
                HandlePlayNow(new VideoQueueEntry(featured!.Url, featured.Title, featured.ChannelName, featured.Duration, featured.ThumbnailUrl));
                if (!ShouldUseViewerMediaActions) video.Pause(false);
            }
        }
        ImGui.SameLine(0, Ui(8));
        if (GameLayoutButton("Watch together", FontAwesomeIcon.UserFriends, Ui(164)))
            currentPage = HomePage.WatchAlong;
        for (var i = 0; i < slides.Length; i++)
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(20 + (slides.Length - i) * 14), height - Ui(16)));
            if (ImGui.InvisibleButton($"slide{i}", UiVec(12, 12)))
            {
                homeFeaturedIndex = i;
                homeFeaturedChangedAt = now;
            }
            var dot = ImGui.GetItemRectMin() + UiVec(6, 6);
            if (i == homeFeaturedIndex)
                draw.AddRectFilled(dot - UiVec(5, 2.5f), dot + UiVec(5, 2.5f), ImGui.GetColorU32(AccentHover), Ui(2.5f));
            else draw.AddCircleFilled(dot, Ui(2.5f), ImGui.GetColorU32(MutedText with { W = .45f }), 12);
        }
        // Paint the outline last so neither the image nor its shading hides it.
        draw.AddRect(origin, origin + new Vector2(width, height), ImGui.GetColorU32(HubTint(0x24456B)), Ui(9), ImDrawFlags.None, Ui(1));
        EndHomeSection(origin, width, height);
    }

    private void DrawHomeShortcuts()
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var columns = width >= Ui(520) ? 5 : 3;
        var gap = Ui(8);
        var cardWidth = (width - gap * (columns - 1)) / columns;
        var shortcuts = new (string Label, FontAwesomeIcon Icon, HomePage Page, Vector4 Color)[]
        {
            ("Videos", FontAwesomeIcon.Film, HomePage.VideoGrid, AccentHover),
            ("Music / DJ", FontAwesomeIcon.Music, HomePage.Music, Hex(0xEC4899)),
            ("Arcade", FontAwesomeIcon.Dice, HomePage.Arcade, Hex(0xA78BFA)),
            ("Retro Games", FontAwesomeIcon.Gamepad, HomePage.PlaySnes, Gold),
            ("Browser", FontAwesomeIcon.Globe, HomePage.Browser, Hex(0x2DD4BF)),
        };
        for (var i = 0; i < shortcuts.Length; i++)
        {
            var shortcut = shortcuts[i];
            var start = origin + new Vector2(i % columns * (cardWidth + gap), i / columns * (Ui(88) + gap));
            ImGui.SetCursorScreenPos(start);
            if (ImGui.InvisibleButton($"shortcut{i}", new(cardWidth, Ui(88)))) currentPage = shortcut.Page;
            DrawArcadeGradientPanel(ImGui.GetWindowDrawList(), start, new(cardWidth, Ui(88)),
                ImGui.IsItemHovered() ? HubSurface(0x152640) : HubSurface(0x0D192D), HubSurface(0x080F1D), HubTint(0x24456B), 8);
            UiFonts.DrawCenteredIcon(ImGui.GetWindowDrawList(), Ui(34), start + new Vector2(cardWidth * .5f, Ui(31)), ImGui.GetColorU32(shortcut.Color), shortcut.Icon.ToIconString());
            using var font = UiFonts.PushRegularRelative(1f);
            var label = TruncateToWidth(shortcut.Label, cardWidth - Ui(8));
            ImGui.GetWindowDrawList().AddText(start + new Vector2((cardWidth - ImGui.CalcTextSize(label).X) * .5f, Ui(62)),
                ImGui.GetColorU32(Vector4.One), label);
        }
        EndHomeSection(origin, width, Ui(88) * MathF.Ceiling(shortcuts.Length / (float)columns) + gap * (MathF.Ceiling(shortcuts.Length / (float)columns) - 1));
    }

    private void DrawHomePlayback()
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var viewing = stream.Mode == StreamMode.Viewing;
        var room = viewing ? stream.CurrentRoomState : null;
        var current = viewing ? null : queue.Current;
        if (room is { Url: { Length: > 0 } url })
            current = CreatePartyMediaDisplayEntry(url, room.MediaTitle ?? "Watch Party media", room.MediaThumbnailUrl);
        current = WithStudioPlaybackDisplay(current);
        var playing = current is not null || video.IsPlayingGame || video.IsPlayingBrowser || video.IsPlayingLocalVideo || video.IsPlayingImage;
        var narrow = width < Ui(540);
        var height = Ui(playing ? narrow ? 154 : 108 : 44);
        DrawHomePanel(origin, width, height);
        if (!playing)
        {
            UiFonts.DrawCenteredIcon(ImGui.GetWindowDrawList(), Ui(17), origin + UiVec(23, 22), ImGui.GetColorU32(AccentHover), FontAwesomeIcon.Play.ToIconString());
            HomeText(origin + UiVec(42, 13), "Nothing playing", width - Ui(190));
            ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(133), Ui(6)));
            var addPosition = ImGui.GetCursorScreenPos();
            var addClicked = ImGui.InvisibleButton("addMedia", UiVec(124, 32));
            DrawPlayerActionButtonContent(addPosition, UiVec(124, 32), FontAwesomeIcon.Plus, "Add media", ImGui.IsItemHovered() ? Vector4.One : AccentHover);
            if (addClicked)
            {
                showingAddMediaSources = true;
                activePlayerDrawer = PlayerDrawer.PlayVideo;
                currentPage = HomePage.Player;
            }
        }
        else
        {
            var (title, source, icon) = GetPlayerStatusMedia(current);
            HomeThumbnail(current?.ThumbnailUrl, origin + UiVec(9, 9), UiVec(narrow ? 90 : 135, 86), icon);
            var (position, duration, paused) = video.GetProgress();
            HomeDuration(origin + UiVec(9, 9), UiVec(narrow ? 90 : 135, 86), duration);
            var textX = Ui(narrow ? 112 : 158);
            var textWidth = width - textX - Ui(narrow ? 14 : 194);
            HomeText(origin + new Vector2(textX, Ui(15)), "NOW PLAYING", textWidth, fontScale: .84f, color: AccentHover);
            HomeText(origin + new Vector2(textX, Ui(36)), title, textWidth, strong: true, fontScale: 1.1f);
            var context = HomeMediaSource(current?.Url, source);
            if (viewing || stream.Mode == StreamMode.Hosting) context += " · Watching with your party";
            HomeText(origin + new Vector2(textX, Ui(61)), context, textWidth, fontScale: .94f);
            var progressWidth = MathF.Max(1, textWidth - Ui(100));
            var progress = duration > 0 ? (float)Math.Clamp(position / duration, 0, 1) : 0;
            var bar = origin + new Vector2(textX, Ui(88));
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(bar, bar + new Vector2(progressWidth, Ui(6)), ImGui.GetColorU32(HubSurface(0x243651)), Ui(3));
            draw.AddRectFilled(bar, bar + new Vector2(progressWidth * progress, Ui(6)), ImGui.GetColorU32(Accent), Ui(3));
            if (duration > 0) HomeText(bar + new Vector2(progressWidth + Ui(7), -Ui(4)), $"{HomeTime(position)} / {HomeTime(duration)}", Ui(96), fontScale: .82f);
            ImGui.SetCursorScreenPos(origin + (narrow ? UiVec(112, 110) : new Vector2(width - Ui(177), Ui(35))));
            if (GameLayoutButton(viewing ? "Open party" : "Open player", FontAwesomeIcon.Play, Ui(126), true))
            {
                activePlayerDrawer = PlayerDrawer.Player;
                currentPage = viewing ? HomePage.WatchAlong : HomePage.Player;
            }
            ImGui.SameLine(0, Ui(6));
            using var pauseId = ImRaii.PushId("homePause");
            using (UiDisabled.Scope(video.ManualTransportLocked || viewing || video.IsPlayingGame || video.IsPlayingBrowser || video.IsPlayingImage))
                if (GameLayoutButton(string.Empty, paused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause, Ui(34))) video.Pause(!paused);
        }
        EndHomeSection(origin, width, height);
    }

    private static string HomeTime(double seconds) => !double.IsFinite(seconds) || seconds <= 0 ? "0:00"
        : seconds >= 3600 ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss") : $"{(int)seconds / 60}:{(int)seconds % 60:00}";

    private static string HomeMediaSource(string? url, string fallback)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return fallback;
        var host = uri.Host;
        if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) || host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)) return "YouTube";
        if (host.Equals("dai.ly", StringComparison.OrdinalIgnoreCase) || host.Equals("dailymotion.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".dailymotion.com", StringComparison.OrdinalIgnoreCase)) return "Dailymotion";
        if (host.Equals("archive.org", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".archive.org", StringComparison.OrdinalIgnoreCase)) return "Internet Archive";
        return fallback;
    }

    private void DrawHomeContinueWatching(RecentlyWatchedVideoRecord[] history)
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var columns = width >= Ui(860) ? 3 : width < Ui(460) ? 1 : 2;
        var cardWidth = (width - Ui(28 + (columns - 1) * 12)) / columns;
        var imageHeight = MathF.Min(Ui(150), cardWidth * 9 / 16);
        var cardHeight = imageHeight + Ui(73);
        var height = Ui(51) + cardHeight * MathF.Ceiling(history.Length / (float)columns);
        DrawHomePanel(origin, width, height);
        DrawHomeHeading(origin, width, "Continue watching", FontAwesomeIcon.Clock, "See all", () => continueWatchingOverlayOpen = true);
        for (var i = 0; i < history.Length; i++)
        {
            using var id = ImRaii.PushId(i);
            var entry = history[i];
            var start = origin + new Vector2(Ui(14) + i % columns * (cardWidth + Ui(12)), Ui(49) + i / columns * cardHeight);
            ImGui.SetCursorScreenPos(start);
            if (ImGui.InvisibleButton("resumeThumbnail", new(cardWidth, imageHeight))) ResumeHomeVideo(entry);
            HomeThumbnail(entry.ThumbnailUrl, start, new(cardWidth, imageHeight), FontAwesomeIcon.Film);
            HomeDuration(start, new(cardWidth, imageHeight), entry.DurationSeconds);
            var draw = ImGui.GetWindowDrawList();
            var progress = entry.DurationSeconds > 0 ? (float)Math.Clamp(entry.WatchedSeconds / entry.DurationSeconds, 0, 1) : 0;
            var trackWidth = cardWidth - Ui(6);
            var track = start + new Vector2(Ui(3), imageHeight - Ui(6));
            draw.AddRectFilled(track, track + new Vector2(trackWidth, Ui(4)), ImGui.GetColorU32(HubSurface(0x243651)), Ui(2));
            draw.AddRectFilled(track, track + new Vector2(trackWidth * progress, Ui(4)), ImGui.GetColorU32(Accent), Ui(2));
            HomeText(start + new Vector2(0, imageHeight + Ui(8)), entry.Title, cardWidth, strong: true, fontScale: 1f);
            HomeText(start + new Vector2(0, imageHeight + Ui(34)), entry.ChannelName, cardWidth - Ui(114), fontScale: .94f);
            ImGui.SetCursorScreenPos(start + new Vector2(cardWidth - Ui(102), imageHeight + Ui(29)));
            using (UiDisabled.Scope(video.IsPlayingLocalVideo))
                if (GameLayoutButton("Resume", FontAwesomeIcon.Play, Ui(102), true, height: 30)) ResumeHomeVideo(entry);
        }
        EndHomeSection(origin, width, height);
    }

    private void DrawHomeParties()
    {
        RefreshHomeWatchPartiesIfNeeded();
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var narrow = width < Ui(610);
        var inParty = stream.Mode is StreamMode.Hosting or StreamMode.Viewing;
        var activeHostId = stream.Mode == StreamMode.Hosting ? CurrentSession?.AccountId
            : stream.Mode == StreamMode.Viewing ? stream.HostId : null;
        var rooms = GetRankedHomeWatchPartyRooms().OrderByDescending(r => r.HostAccountId == activeHostId).Take(2).ToArray();
        var headerHeight = Ui(narrow && !inParty ? 91 : 51);
        var rowHeight = Ui(width < Ui(540) ? 142 : 100);
        var height = headerHeight + rooms.Length * (rowHeight + Ui(8)) + Ui(6);
        DrawHomePanel(origin, width, height);
        DrawHomeHeading(origin, width, "Watch Parties", FontAwesomeIcon.Users);
        if (!inParty)
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(narrow ? Ui(14) : width - Ui(272), Ui(narrow ? 48 : 10)));
            if (GameLayoutButton("Create party", FontAwesomeIcon.Plus, Ui(126), height: 32)) StartWatchParty(goToPlayer: false);
            ImGui.SameLine(0, Ui(8));
        }
        else ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(138), Ui(10)));
        if (GameLayoutButton("Find parties", FontAwesomeIcon.Search, Ui(126), height: 32)) OpenWatchPartyPage();
        for (var i = 0; i < rooms.Length; i++)
        {
            var room = rooms[i];
            using var id = ImRaii.PushId(room.HostAccountId);
            var start = origin + new Vector2(Ui(12), headerHeight + i * (rowHeight + Ui(8)));
            var rowWidth = width - Ui(24);
            DrawHomePanel(start, rowWidth, rowHeight);
            EnsurePartyDirectoryMediaLoaded(room);
            var media = GetPartyDirectoryMedia(room);
            var thumbnailSize = UiVec(width < Ui(540) ? 88 : 126, 78);
            DrawPartyDirectoryThumbnail(ImGui.GetWindowDrawList(), room, media, start + UiVec(8, 8), thumbnailSize);
            var own = stream.Mode == StreamMode.Hosting && room.HostAccountId == CurrentSession?.AccountId;
            var currentRoom = inParty && room.HostAccountId == activeHostId;
            var actionLabel = own ? "Open your party" : currentRoom ? "Open party" : "Join room";
            float actionWidth;
            using (UiFonts.PushIcon()) actionWidth = ImGui.CalcTextSize(FontAwesomeIcon.Users.ToIconString()).X;
            actionWidth = MathF.Max(Ui(146), actionWidth + ImGui.CalcTextSize(actionLabel).X + Ui(36));
            var textX = thumbnailSize.X + Ui(20);
            var textWidth = rowWidth - textX - (width < Ui(540) ? Ui(14) : actionWidth + Ui(24));
            TryReadWatchPartyMetadata(room, out _, out _, out _, out var description);
            HomeText(start + new Vector2(textX, Ui(13)),
                string.IsNullOrWhiteSpace(description) ? $"{room.HostDisplayName}'s Watch Party" : description,
                textWidth, strong: true, fontScale: 1.1f);
            var people = own && room.ViewerCount == 0 ? "Just you"
                : $"{room.ViewerCount} {(room.ViewerCount == 1 ? "viewer" : "viewers")}";
            HomeText(start + new Vector2(textX, Ui(37)),
                $"{(own ? "You are hosting" : currentRoom ? "You are watching" : "Hosted by " + room.HostDisplayName)} · {people}", textWidth, fontScale: .94f);
            var badge = start + new Vector2(textX, Ui(62));
            var kind = room.Kind.ToString().ToUpperInvariant();
            using (UiFonts.PushRegularRelative(.8f))
            {
                var badgeSize = ImGui.CalcTextSize(kind) + UiVec(13, 5);
                var draw = ImGui.GetWindowDrawList();
                var color = room.Kind == RoomKind.Public ? Hex(0x22DB85) : AccentHover;
                draw.AddRectFilled(badge, badge + badgeSize, ImGui.GetColorU32(color with { W = .10f }), Ui(4));
                draw.AddRect(badge, badge + badgeSize, ImGui.GetColorU32(color with { W = .65f }), Ui(4));
                draw.AddText(badge + UiVec(6, 2), ImGui.GetColorU32(color), kind);
                HomeText(badge + new Vector2(badgeSize.X + Ui(8), Ui(2)),
                    room.HasMedia ? room.Paused ? "Paused" : "Playing" : "Waiting for content",
                    textWidth - badgeSize.X - Ui(8), fontScale: 1f);
            }
            ImGui.SetCursorScreenPos(start + (width < Ui(540) ? new Vector2(textX, Ui(98)) : new Vector2(rowWidth - actionWidth - Ui(12), Ui(32))));
            if (GameLayoutButton(actionLabel, FontAwesomeIcon.Users, actionWidth, true, height: 34))
            {
                if (currentRoom) currentPage = HomePage.WatchAlong;
                else JoinHomeWatchParty(room);
            }
        }
        EndHomeSection(origin, width, height);
    }

    private void DrawHomePopular()
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var columns = width >= Ui(620) ? 3 : width >= Ui(420) ? 2 : 1;
        var cardWidth = (width - Ui(24 + (columns - 1) * 12)) / columns;
        var imageHeight = MathF.Min(Ui(160), cardWidth * 9 / 16);
        var cardHeight = imageHeight + Ui(116);
        var results = new List<UnifiedSearchResult>();
        string? error;
        bool loading;
        if (homePopularProvider == 0)
        {
            results = GetPopularVideos().Where(v => !IsYouTubeChannelHidden(v)).Take(columns)
                .Select(v => new UnifiedSearchResult(UnifiedSearchProvider.YouTube, v, null)).ToList();
            error = null;
            loading = popularListLoading;
        }
        else if (homePopularProvider == 1)
        {
            RefreshDailymotionPopular();
            results = (Plugin.Cfg.DailymotionPopularVideos ?? []).Where(v => DailymotionClient.ValidId(v.VideoId)).Take(columns)
                .Select(v => new UnifiedSearchResult(UnifiedSearchProvider.Dailymotion, v.ToEntry(), null)).ToList();
            error = dailymotionPopularFailure;
            loading = dailymotionPopularLoading;
        }
        else
        {
            RefreshArchivePopular();
            results = (Plugin.Cfg.ArchivePopularItems ?? []).Take(columns)
                .Select(v => new UnifiedSearchResult(UnifiedSearchProvider.InternetArchive, null, v)).ToList();
            error = archivePopularFailure;
            loading = archivePopularLoading;
        }
        var height = Ui(96) + (results.Count > 0 ? cardHeight : Ui(62));
        DrawHomePanel(origin, width, height);
        DrawHomeHeading(origin, width, "Popular on Alpha Channel", FontAwesomeIcon.Fire, "See all", () =>
        {
            if (homePopularProvider == 0) { browseVideoSectionTab = 4; youtubeBrowseShowingSearch = false; viewedChannelId = null; currentPage = HomePage.VideoGrid; }
            else if (homePopularProvider == 1) { dailymotionSearchTab = false; currentPage = HomePage.Dailymotion; }
            else { internetArchivePopularTab = true; currentPage = HomePage.InternetArchive; }
        });
        var labels = new[] { "YouTube", "Dailymotion", "Internet Archive" };
        var tabWidth = (width - Ui(40)) / 3;
        var icons = new[] { FontAwesomeIcon.PlayCircle, FontAwesomeIcon.Video, FontAwesomeIcon.Film };
        using (UiFonts.PushRegularRelative(.94f))
        for (var i = 0; i < labels.Length; i++)
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(Ui(12) + i * (tabWidth + Ui(8)), Ui(48)));
            var start = ImGui.GetCursorScreenPos();
            var size = new Vector2(tabWidth, Ui(32));
            if (ImGui.InvisibleButton("provider" + i, size)) homePopularProvider = i;
            var selected = homePopularProvider == i;
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(start, start + size,
                ImGui.GetColorU32(selected ? Accent : ImGui.IsItemHovered() ? HubSurface(0x172940) : HubSurface(0x0B1526)), Ui(6));
            draw.AddRect(start, start + size, ImGui.GetColorU32(selected ? AccentHover : HubTint(0x24456B)), Ui(6));
            DrawPlayerActionButtonContent(start, size, icons[i], labels[i], Vector4.One);
        }
        if (results.Count == 0)
        {
            ImGui.SetCursorScreenPos(origin + UiVec(16, 99));
            using (ImRaii.TextWrapPos(origin.X + width - Ui(16) - ImGui.GetWindowPos().X))
                ImGui.TextColored(MutedText, error ?? (loading ? "Loading popular videos…" : "Videos will appear here as the community watches."));
        }
        for (var i = 0; i < results.Count; i++)
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(Ui(12) + i * (cardWidth + Ui(12)), Ui(88)));
            DrawHomePopularCard(results[i], cardWidth, imageHeight, cardHeight, i);
        }
        EndHomeSection(origin, width, height);
    }

    private void DrawHomePopularCard(UnifiedSearchResult result, float width, float imageHeight, float height, int index)
    {
        var origin = ImGui.GetCursorScreenPos();
        if (!ImGui.IsRectVisible(origin, origin + new Vector2(width, height))) return;
        using var id = ImRaii.PushId($"popular{index}_{result.ExactKey}");
        if (result.Provider == UnifiedSearchProvider.YouTube && result.Video is { } youtube)
            result = result with { Video = VisibleYouTubeCardMetadata(youtube) };
        if (ImGui.InvisibleButton("thumbnail", new(width, imageHeight))) PlayUnifiedResult(result);
        HomeThumbnail(result.ThumbnailUrl, origin, new(width, imageHeight), FontAwesomeIcon.Film);
        HomeDuration(origin, new(width, imageHeight), result.Duration?.TotalSeconds ?? 0);
        var draw = ImGui.GetWindowDrawList();
        using (UiFonts.PushRelative(1.03f, emphasis: true))
            DrawWrappedLines(draw, origin + new Vector2(0, imageHeight + Ui(9)), width,
                ImGui.GetTextLineHeight(), 2, ImGui.GetColorU32(Vector4.One), result.Title);
        var creator = origin + new Vector2(0, imageHeight + Ui(49));
        UiFonts.DrawCenteredIcon(draw, Ui(12), creator + UiVec(6, 7), ImGui.GetColorU32(AccentHover), FontAwesomeIcon.User.ToIconString());
        HomeText(creator + UiVec(17, 0), result.Creator, width - Ui(17), fontScale: .94f);
        var collection = result.Archive is { Episodes.Count: > 1 };
        ImGui.SetCursorScreenPos(origin + new Vector2(0, height - Ui(37)));
        if (GameLayoutButton(collection ? "Episodes" : "Play", collection ? FontAwesomeIcon.List : FontAwesomeIcon.Play,
                width - Ui(47), true, height: 34)) PlayUnifiedResult(result);
        ImGui.SameLine(0, Ui(7));
        if (result.Archive is { } archive)
            DrawInternetArchiveQueueOptions(archive, collection ? null : archive.Episodes.FirstOrDefault(), 34);
        else if (result.Video is { } videoResult)
        {
            if (result.Provider == UnifiedSearchProvider.YouTube)
            {
                var anchor = ImGui.GetCursorScreenPos();
                if (GameLayoutButton(string.Empty, FontAwesomeIcon.EllipsisH, Ui(40), height: 34)) OpenYouTubeCardMenu(videoResult, anchor);
            }
            else DrawUnifiedVideoOptions(videoResult);
        }
    }

    private void DrawHomeRadio()
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = Ui(218);
        DrawHomePanel(origin, width, height);
        DrawHomeHeading(origin, width, "Live Radio", FontAwesomeIcon.Music, "Browse stations", () =>
        {
            musicTab = MusicTab.Radio;
            currentPage = HomePage.Music;
        });
        float browseWidth;
        using (UiFonts.PushRegularRelative(.94f)) browseWidth = ImGui.CalcTextSize("Browse stations  ›").X;
        var settingsOrigin = origin + new Vector2(width - browseWidth - Ui(52), Ui(11));
        ImGui.SetCursorScreenPos(settingsOrigin);
        if (ImGui.InvisibleButton("radioSettings", UiVec(28, 28))) OpenHomeRadioSettings();
        UiFonts.DrawCenteredIcon(ImGui.GetWindowDrawList(), Ui(17), settingsOrigin + UiVec(14, 14),
            ImGui.GetColorU32(ImGui.IsItemHovered() ? Vector4.One : AccentHover), FontAwesomeIcon.Cog.ToIconString());
        var choices = GetHomeRadioChoices();
        DrawHomeRadioRow(ResolveHomeRadioChoice(Plugin.Cfg.HomeRadioStation1Url, 0, choices), origin + UiVec(12, 49), width - Ui(24), 0);
        DrawHomeRadioRow(ResolveHomeRadioChoice(Plugin.Cfg.HomeRadioStation2Url, 1, choices), origin + UiVec(12, 130), width - Ui(24), 1);
        EndHomeSection(origin, width, height);
    }

    private void DrawHomeRadioRow(RadioStationChoice station, Vector2 origin, float width, int index)
    {
        using var id = ImRaii.PushId("radio" + index);
        DrawHomePanel(origin, width, Ui(73));
        var draw = ImGui.GetWindowDrawList();
        var art = origin + UiVec(9, 10);
        draw.AddRectFilled(art, art + UiVec(52, 52), ImGui.GetColorU32(HubSurface(0x10243E)), Ui(6));
        UiFonts.DrawCenteredIcon(draw, Ui(26), art + UiVec(26, 26), ImGui.GetColorU32(AccentHover), station.Icon.ToIconString());
        HomeText(origin + UiVec(73, 14), station.Name, width - Ui(135), strong: true, fontScale: 1f);
        HomeText(origin + UiVec(73, 39), station.Description, width - Ui(135), fontScale: .94f);
        ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(47), Ui(19)));
        if (GameLayoutButton(string.Empty, FontAwesomeIcon.Play, Ui(35), height: 35)) PlayDjUrl(station.Url, station.Name, "Radio");
    }
}

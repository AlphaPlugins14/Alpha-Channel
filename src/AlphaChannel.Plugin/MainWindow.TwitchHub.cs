using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private enum TwitchHubTab { MyChannel, Discover, Favourites }
    private TwitchHubTab twitchHubTab;
    private string? twitchOwnInput;
    private string? twitchOwnError;
    private TwitchStreamInfo? twitchOwnStream;
    private bool twitchOwnChecking, twitchOwnOffline, twitchOwnEditing;
    private DateTime? twitchOwnCheckedAt;
    private int twitchOwnGeneration;
    private bool twitchDiscoverGlobal, twitchTrendingLoading;
    private string? twitchTrendingToken;
    private DateTime twitchTrendingRefreshAt;
    private DateTime? twitchTrendingUpdatedAt;
    private bool twitchTrendingStale, twitchTrendingFailed;
    private string twitchCategoryFilter = "All categories";
    private Auth.TwitchPopularChannel[] twitchPopularChannels = [];
    private string? twitchPopularToken, twitchPopularError;
    private DateTime twitchPopularRefreshAt;
    private bool twitchPopularLoading;
    private string twitchFavouriteFilter = string.Empty;
    private bool twitchFavouritesLiveOnly;

    private void DrawTwitchHubPage()
    {
        using var id = ImRaii.PushId("twitchHub");
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(10, 10));
        using var fields = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7))
            .Push(ImGuiStyleVar.FrameBorderSize, Ui(1))
            .Push(ImGuiStyleVar.FramePadding, new Vector2(Ui(10), Math.Max(1, (Ui(36) - ImGui.GetFontSize()) / 2)));
        using var fieldColors = ImRaii.PushColor(ImGuiCol.FrameBg, HubSurface(0x0A1526))
            .Push(ImGuiCol.Border, HubTint(0x345476));
        DrawTwitchHubBanner();
        SetGameLayoutRowGap(Ui(8));
        var tabs = new[] { "My Channel", "Discover", "Favourites" };
        var icons = new[] { FontAwesomeIcon.User, FontAwesomeIcon.Compass, FontAwesomeIcon.Star };
        var width = ImGui.GetContentRegionAvail().X;
        var tabWidth = width / tabs.Length;
        var origin = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        for (var i = 0; i < tabs.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, 0);
            var position = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton("tab" + i, new(tabWidth, Ui(42)))) twitchHubTab = (TwitchHubTab)i;
            var selected = twitchHubTab == (TwitchHubTab)i;
            using (UiFonts.PushRelative(.96f, emphasis: selected))
            {
                var text = tabs[i];
                var color = ImGui.GetColorU32(selected ? AccentHover : MutedText);
                var x = (tabWidth - ImGui.CalcTextSize(text).X - Ui(25)) / 2;
                UiFonts.DrawIcon(draw, ImGui.GetFontSize(), position + new Vector2(x, Ui(12)), color, icons[i].ToIconString());
                draw.AddText(position + new Vector2(x + Ui(25), Ui(12)), color, text);
            }
            if (selected) draw.AddRectFilled(position + UiVec(0, 39), position + new Vector2(tabWidth, Ui(42)), ImGui.GetColorU32(Accent));
        }
        draw.AddLine(origin + UiVec(0, 42), origin + new Vector2(width, Ui(42)), ImGui.GetColorU32(BorderSubtle));
        SetGameLayoutRowGap(Ui(10));
        switch (twitchHubTab)
        {
            case TwitchHubTab.MyChannel: DrawTwitchOwnChannel(); break;
            case TwitchHubTab.Discover: DrawTwitchDiscover(); break;
            case TwitchHubTab.Favourites: DrawTwitchHubFavourites(); break;
        }
    }

    private void DrawTwitchHubBanner()
    {
        var width = ImGui.GetContentRegionAvail().X;
        DrawPlayerHubPanel("##twitchHero", width, Ui(110), true, () =>
        {
            var origin = ImGui.GetCursorScreenPos();
            var inner = ImGui.GetContentRegionAvail().X;
            var artwork = width >= Ui(650);
            var textWidth = inner - Ui(artwork ? 220 : 0);
            var draw = ImGui.GetWindowDrawList();
            using (UiFonts.PushRegularRelative(.8f)) draw.AddText(origin, ImGui.GetColorU32(AccentHover), "TWITCH · WATCH TOGETHER");
            float titleBottom;
            using (UiFonts.PushRelative(1.5f, emphasis: true))
                titleBottom = DrawWrappedLines(draw, origin + UiVec(0, 19), textWidth, ImGui.GetTextLineHeight(), 2,
                    ImGui.GetColorU32(Vector4.One), "Your stream, on their TVs.");
            using (UiFonts.PushRegularRelative(.92f))
                DrawWrappedLines(draw, new Vector2(origin.X, titleBottom + Ui(8)), textWidth, ImGui.GetTextLineHeight(), 2,
                    ImGui.GetColorU32(MutedText), "Add your Twitch channel and let your Watch Party watch you live in Eorzea.");
            if (artwork) DrawTwitchHubDesk(draw, origin + new Vector2(inner - Ui(185), Ui(2)), .9f);
        }, contentPadding: 14);
    }

    private static void DrawTwitchHubDesk(ImDrawListPtr draw, Vector2 origin, float scale = 1f)
    {
        // Integer-aligned pixel shapes, like the Music and Retro Games banners.
        void Block(float x, float y, float w, float h, int color) =>
            draw.AddRectFilled(origin + UiVec(x * .7f * scale, y * .7f * scale), origin + UiVec((x + w) * .7f * scale, (y + h) * .7f * scale), ImGui.GetColorU32(Hex(color)));
        Block(0, 100, 200, 7, 0x365272); Block(8, 107, 6, 15, 0x243852); Block(182, 107, 6, 15, 0x243852);
        Block(22, 28, 101, 65, 0x526A8B); Block(27, 33, 91, 54, 0x111D35);
        Block(32, 38, 81, 44, 0x193B66); Block(65, 93, 14, 7, 0x526A8B);
        Block(53, 97, 38, 3, 0x6C89AC); Block(62, 17, 22, 11, 0x526A8B); Block(69, 20, 8, 5, 0x5ED9F5);
        Block(54, 46, 41, 26, 0x8B60D9); Block(59, 50, 31, 17, 0xC8B7FF);
        Block(65, 53, 5, 9, 0x5835A0); Block(78, 53, 5, 9, 0x5835A0); Block(61, 69, 9, 7, 0x8B60D9);
        Block(130, 39, 48, 52, 0x3A506E); Block(134, 43, 40, 44, 0x102138);
        for (var i = 0; i < 5; i++) { Block(139, 49 + i * 7, 5, 3, 0x51CDE6); Block(148, 49 + i * 7, 20, 3, 0x607FA5); }
        Block(148, 91, 11, 9, 0x526A8B); Block(40, 111, 79, 6, 0x6C89AC);
        Block(188, 60, 9, 24, 0x78C8EB); Block(185, 76, 3, 12, 0x456D92);
        Block(188, 85, 9, 3, 0x456D92); Block(191, 88, 3, 12, 0x456D92);
    }

    private bool IsSharingOwnTwitch => stream.Mode == StreamMode.Hosting &&
        TwitchChannelChecker.TryNormalizeChannelName(queue.Current?.Url ?? "", out var channel, out _) &&
        channel == Plugin.Cfg.OwnTwitchChannel;

    private void DrawTwitchOwnChannel()
    {
        twitchOwnInput ??= Plugin.Cfg.OwnTwitchChannel;
        var configured = Plugin.Cfg.OwnTwitchChannel.Length > 0;
        var width = ImGui.GetContentRegionAvail().X;
        DrawPlayerHubPanel("##twitchOwnSetup", width, Ui(98), false, () =>
        {
            using var setupSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8, 4));
            using (UiFonts.PushRelative(1.06f, emphasis: true)) ImGui.TextUnformatted("Your Twitch channel");
            var inner = ImGui.GetContentRegionAvail().X;
            var editing = twitchOwnEditing || Plugin.Cfg.OwnTwitchChannel.Length == 0;
            ImGui.SetNextItemWidth(Math.Max(1, inner - Ui(100)));
            var submitted = ImGui.InputTextWithHint("##ownTwitch", "Your Twitch username or channel URL", ref twitchOwnInput, 2048,
                editing ? ImGuiInputTextFlags.EnterReturnsTrue : ImGuiInputTextFlags.ReadOnly);
            ImGui.SameLine();
            var editPosition = ImGui.GetCursorScreenPos();
            if (GameLayoutButton(editing ? "Save" : "Edit", editing ? FontAwesomeIcon.Check : FontAwesomeIcon.PencilAlt, Ui(90), height: 36) || submitted)
            {
                if (!editing) twitchOwnEditing = true;
                else if (TwitchChannelChecker.TryNormalizeChannelName(twitchOwnInput, out var name, out var error))
                {
                    Plugin.Cfg.OwnTwitchChannel = twitchOwnInput = name; Plugin.Cfg.Save();
                    twitchOwnEditing = false; twitchOwnStream = null; twitchOwnCheckedAt = null;
                    twitchOwnOffline = twitchOwnChecking = false; twitchOwnError = null; twitchOwnGeneration++;
                    StartTwitchOwnCheck();
                }
                else twitchOwnError = error;
            }
            using (UiFonts.PushRegularRelative(.85f))
            using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + Math.Max(Ui(40), inner - Ui(100)))) ImGui.TextColored(MutedText,
                "Add your channel, go live on Twitch, then watch together in Eorzea.");
            if (!editing && Plugin.Cfg.OwnTwitchChannel.Length > 0)
            {
                var resumePosition = ImGui.GetCursorScreenPos();
                using var removeFont = UiFonts.PushRegularRelative(.8f);
                ImGui.SetCursorScreenPos(editPosition + new Vector2((Ui(90) - ImGui.CalcTextSize("Remove").X) / 2, Ui(40)));
                ImGui.TextColored(Danger, "Remove");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    UiFonts.SetTooltip("Remove your saved Twitch username.");
                }
                if (ImGui.IsItemClicked())
                {
                    Plugin.Cfg.OwnTwitchChannel = twitchOwnInput = string.Empty;
                    twitchOwnEditing = false; twitchOwnStream = null; twitchOwnCheckedAt = null;
                    twitchOwnOffline = twitchOwnChecking = false; twitchOwnError = null; twitchOwnGeneration++;
                    Plugin.Cfg.Save();
                }
                ImGui.SetCursorScreenPos(resumePosition);
            }
        }, contentPadding: 12);
        configured = Plugin.Cfg.OwnTwitchChannel.Length > 0;
        if (twitchOwnError is { } errorText)
            using (ImRaii.TextWrapPos(0)) ImGui.TextColored(Danger, errorText);
        SetGameLayoutRowGap(Ui(8));
        DrawTwitchOwnSteps(width);
        SetGameLayoutRowGap(Ui(8));
        var sideBySide = width >= Ui(660);
        var leftWidth = sideBySide ? (width - Ui(12)) * .58f : width;
        var rightWidth = sideBySide ? width - leftWidth - Ui(12) : width;
        // Keep the preview compact as the window widens; preserve 16:9 for live
        // thumbnails and the setup illustration rather than stretching either.
        var previewWidth = Math.Min(leftWidth - Ui(28), Ui(288));
        var previewHeight = previewWidth * 9 / 16;
        // Size the cards for their content so the dashboard button and footer
        // remain visible at the normal minimum window height.
        float headingHeight, channelHeight;
        using (UiFonts.PushRelative(1.12f, emphasis: true)) headingHeight = ImGui.GetTextLineHeight();
        using (UiFonts.PushRelative(1.08f, emphasis: true)) channelHeight = ImGui.GetTextLineHeight();
        var descriptionHeight = !configured
            ? ImGui.CalcTextSize("See your live preview, stream title, category and viewer count here after adding your channel.", false, leftWidth - Ui(28)).Y
            : ImGui.GetTextLineHeight() * (twitchOwnStream is null ? 1 : 4);
        var leftHeight = previewHeight + headingHeight + channelHeight + descriptionHeight +
            Ui(28 + 32 + (twitchOwnStream is null ? 24 : 36));
        var height = Math.Max(Ui(stream.Mode == StreamMode.None ? 342 : 318), leftHeight);
        DrawPlayerHubPanel("##twitchOwnPreview", leftWidth, height, false, () =>
        {
            using var previewSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8, 6));
            using (UiFonts.PushRelative(1.12f, emphasis: true)) ImGui.TextUnformatted("Your stream preview");
            var inner = ImGui.GetContentRegionAvail().X;
            var image = ImGui.GetCursorScreenPos() + new Vector2(Math.Max(0, (inner - previewWidth) / 2), 0);
            var imageSize = new Vector2(previewWidth, previewHeight);
            if (twitchOwnStream is null) DrawTwitchOwnPreviewPlaceholder(image, imageSize);
            else DrawTwitchHubThumbnail(twitchOwnStream, image, imageSize);
            ImGui.Dummy(new Vector2(inner, previewHeight));
            using (UiFonts.PushRelative(1.08f, emphasis: true)) ImGui.TextUnformatted(configured ? Plugin.Cfg.OwnTwitchChannel : "Your stream, on their TVs");
            if (!configured)
            {
                using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText,
                    "See your live preview, stream title, category and viewer count here after adding your channel.");
            }
            else
            ImGui.TextColored(twitchOwnStream is null ? MutedText : Good,
                twitchOwnChecking ? "Checking live status…" : twitchOwnStream is not null ? "Live on Twitch" :
                twitchOwnOffline ? "Offline" : "Not checked yet");
            if (twitchOwnStream is { } info)
            {
                var title = PrepareTwitchDisplayText(info.Title);
                var titleAt = ImGui.GetCursorScreenPos();
                DrawWrappedLines(ImGui.GetWindowDrawList(), titleAt, inner, ImGui.GetTextLineHeight(), 2,
                    ImGui.GetColorU32(Vector4.One), title);
                ImGui.Dummy(new(inner, ImGui.GetTextLineHeight() * 2));
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip(title);
                ImGui.TextColored(MutedText, FitTwitchText(TwitchHubMetadata(info, false), inner));
            }
            ImGui.SetCursorPosY(height - Ui(46));
            using (UiDisabled.Scope(Plugin.Cfg.OwnTwitchChannel.Length == 0))
                if (GameLayoutButton("Open Twitch Dashboard", FontAwesomeIcon.ExternalLinkAlt, inner, height: 32))
                    Dalamud.Utility.Util.OpenLink("https://dashboard.twitch.tv/u/" + Plugin.Cfg.OwnTwitchChannel + "/home");
        }, contentPadding: 14);
        if (sideBySide) ImGui.SameLine(0, Ui(12)); else SetGameLayoutRowGap(Ui(12));
        DrawPlayerHubPanel("##twitchOwnShare", rightWidth, height, true, () =>
        {
            using var shareSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8, 6));
            var inner = ImGui.GetContentRegionAvail().X;
            using (UiFonts.PushRelative(1.12f, emphasis: true)) ImGui.TextUnformatted("Watch with your friends");
            using (UiFonts.PushRegularRelative(.92f))
            using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText,
                "Put your Twitch stream on your friends’ TVs and hang out in Eorzea.");
            DrawTwitchShareStatus(!configured ? "Add your Twitch channel" : twitchOwnChecking ? "Checking your stream…" : twitchOwnStream is not null ? "Channel live" : twitchOwnOffline ? "Not live yet" : "Go live on Twitch",
                !configured ? "Save your username above to get started" : twitchOwnCheckedAt is { } checkedAt ? "Checked " + FormatTwitchFavouriteCheckAge(checkedAt) : "Start your broadcast, then check below",
                twitchOwnStream is not null);
            using (UiDisabled.Scope(twitchOwnChecking || Plugin.Cfg.OwnTwitchChannel.Length == 0))
                if (GameLayoutButton(twitchOwnChecking ? "Checking…" : "Check live status", FontAwesomeIcon.SyncAlt, inner, height: 36)) StartTwitchOwnCheck();
            if (!configured && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                UiFonts.SetTooltip("Save your Twitch username above, then check whether your broadcast is live.");
            DrawTwitchShareStatus(stream.Mode switch
            {
                StreamMode.Hosting => "Party ready",
                StreamMode.Viewing => "You're in another party",
                _ => "No Watch Party yet"
            }, stream.Mode == StreamMode.Hosting ? $"Hosting · {stream.Roster.Length + 1} people" :
                stream.Mode == StreamMode.Viewing ? "Leave that party to host your own" : "Create a Watch Party when you're ready", stream.Mode == StreamMode.Hosting, showMarker: false);
            if (stream.Mode == StreamMode.None)
            {
                using (UiDisabled.Scope(CurrentSession is null))
                    if (GameLayoutButton("Create Watch Party", FontAwesomeIcon.UserFriends, inner, true, height: 36))
                    {
                        ClearPendingWatchPartySelection();
                        pendingWatchPartyMediaKind = PendingWatchPartyMediaKind.TwitchRoom;
                        OpenPendingWatchPartyCreation();
                    }
            }
            var sharing = IsSharingOwnTwitch;
            var fresh = twitchOwnCheckedAt is { } checkedAtUtc && DateTime.UtcNow - checkedAtUtc < TimeSpan.FromMinutes(2);
            using (UiDisabled.Scope(stream.Mode != StreamMode.Hosting || (!sharing && (twitchOwnStream is null || !fresh || twitchOwnChecking || twitchOwnEditing))))
                if (GameLayoutButton(sharing ? "Stop sharing" : "Watch my stream together", sharing ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play, inner, !sharing, height: 36))
                {
                    if (sharing) queue.StopPlayback();
                    else if (twitchOwnStream is { } info) PlayTwitchHubStream(info);
                }
            if (!configured && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                UiFonts.SetTooltip("Once your channel is live, share it with your Watch Party from here.");
            if (!fresh && twitchOwnStream is not null && !sharing)
                using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText, "Check live status again before sharing.");
        }, contentPadding: 14);
        SetGameLayoutRowGap(Ui(10));
        using (UiFonts.PushRegularRelative(.86f))
        using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText,
            "Stopping sharing in Alpha Channel will not stop your Twitch broadcast.");
    }

    private void DrawTwitchOwnPreviewPlaceholder(Vector2 origin, Vector2 size)
    {
        var draw = ImGui.GetWindowDrawList();
        DrawArcadeGradientPanel(draw, origin, size, HubSurface(0x142846), HubSurface(0x091323), HubTint(0x345476), 7);
        draw.PushClipRect(origin, origin + size, true);
        var scale = Math.Min(1.65f, (size.X - Ui(32)) / Ui(140));
        var artSize = UiVec(140 * scale, 86 * scale);
        DrawTwitchHubDesk(draw, origin + new Vector2((size.X - artSize.X) / 2, Math.Max(Ui(4), (size.Y - Ui(25) - artSize.Y) / 2)), scale);
        using (UiFonts.PushRegularRelative(.76f))
        {
            const string label = "YOUR LIVE STREAM PREVIEW";
            var textSize = ImGui.CalcTextSize(label);
            draw.AddText(origin + new Vector2((size.X - textSize.X) / 2, size.Y - Ui(24)), ImGui.GetColorU32(AccentHover), label);
        }
        draw.PopClipRect();
    }

    private void DrawTwitchOwnSteps(float width)
    {
        var labels = new[] { "Go live on Twitch", "Create your watch party", "Watch together" };
        var sharing = IsSharingOwnTwitch;
        var liveReady = sharing || twitchOwnStream is not null && !twitchOwnChecking && !twitchOwnEditing &&
            twitchOwnCheckedAt is { } checkedAt && DateTime.UtcNow - checkedAt < TimeSpan.FromMinutes(2);
        var completed = new[] { liveReady, stream.Mode == StreamMode.Hosting, sharing };
        var nextStep = Array.FindIndex(completed, ready => !ready);
        var origin = ImGui.GetCursorScreenPos();
        var columnWidth = width / labels.Length;
        var textWidth = Math.Max(Ui(40), columnWidth - Ui(12));
        var draw = ImGui.GetWindowDrawList();
        float labelHeight = 0;
        for (var i = 0; i < labels.Length; i++)
        {
            var center = origin + new Vector2(columnWidth * (i + .5f), Ui(14));
            var color = completed[i] ? Good : i == nextStep ? AccentHover : MutedText;
            if (i < labels.Length - 1)
                draw.AddLine(center + UiVec(24, 0), center + new Vector2(columnWidth - Ui(24), 0),
                    ImGui.GetColorU32(completed[i] ? AccentInk(.5f) : BorderSubtle), Ui(1));
            draw.AddCircleFilled(center, Ui(14), ImGui.GetColorU32(completed[i] ? Good : i == nextStep ? Accent : HubTint(0x344157)));
            if (completed[i])
                UiFonts.DrawCenteredIcon(draw, Ui(14), center, ImGui.GetColorU32(Vector4.One), FontAwesomeIcon.Check.ToIconString());
            else
            {
                var number = (i + 1).ToString();
                draw.AddText(center - ImGui.CalcTextSize(number) / 2, ImGui.GetColorU32(Vector4.One), number);
            }
            using (UiFonts.PushRelative(.9f, emphasis: completed[i] || i == nextStep))
            {
                var measured = ImGui.CalcTextSize(labels[i], false, textWidth);
                var textAt = origin + new Vector2(columnWidth * (i + .5f) - Math.Min(textWidth, measured.X) / 2, Ui(35));
                DrawWrappedLines(draw, textAt, textWidth, ImGui.GetTextLineHeight(), 2, ImGui.GetColorU32(color), labels[i]);
                labelHeight = Math.Max(labelHeight, Math.Min(measured.Y, ImGui.GetTextLineHeight() * 2));
            }
        }
        ImGui.Dummy(new Vector2(width, Ui(39) + labelHeight));
    }

    private void DrawTwitchShareStatus(string title, string detail, bool ready, bool showMarker = true)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var textOffset = showMarker ? Ui(20) : 0;
        var textWidth = Math.Max(Ui(40), width - textOffset);
        var titleHeight = ImGui.CalcTextSize(title, false, textWidth).Y;
        var origin = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        if (showMarker)
            draw.AddCircleFilled(origin + new Vector2(Ui(7), ImGui.GetTextLineHeight() / 2), Ui(5), ImGui.GetColorU32(ready ? Good : MutedText));
        DrawWrappedLines(draw, origin + new Vector2(textOffset, 0), textWidth, ImGui.GetTextLineHeight(), 2, ImGui.GetColorU32(ready ? Good : Vector4.One), title);
        float detailHeight;
        using (UiFonts.PushRegularRelative(.79f))
        {
            detailHeight = Math.Min(ImGui.CalcTextSize(detail, false, textWidth).Y, ImGui.GetTextLineHeight() * 2);
            DrawWrappedLines(draw, origin + new Vector2(textOffset, titleHeight + Ui(5)), textWidth, ImGui.GetTextLineHeight(), 2,
                ImGui.GetColorU32(MutedText), detail);
        }
        ImGui.Dummy(new Vector2(width, Math.Max(Ui(44), titleHeight + Ui(5) + detailHeight)));
    }

    private void StartTwitchOwnCheck()
    {
        if (twitchOwnChecking || !TwitchChannelChecker.TryNormalizeChannelName(Plugin.Cfg.OwnTwitchChannel, out var channel, out _)) return;
        var path = screenController.Engine.Resources.GetLocationYTDLP();
        if (path is null) { twitchOwnError = "The playback helper is still downloading. Try again shortly."; return; }
        twitchOwnChecking = true; twitchOwnError = null;
        var generation = ++twitchOwnGeneration;
        _ = CheckTwitchOwnAsync(path, channel, generation);
    }

    private async Task CheckTwitchOwnAsync(string path, string channel, int generation)
    {
        var (info, error, offline) = await twitchChecker.CheckLiveAsync(path, channel, youtubeFeedToken).ConfigureAwait(false);
        if (Volatile.Read(ref disposeStarted) != 0 || youtubeFeedToken.IsCancellationRequested) return;
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (Volatile.Read(ref disposeStarted) != 0 || generation != twitchOwnGeneration || Plugin.Cfg.OwnTwitchChannel != channel) return;
            twitchOwnStream = info; twitchOwnError = offline ? null : error; twitchOwnOffline = offline;
            twitchOwnCheckedAt = DateTime.UtcNow; twitchOwnChecking = false;
        }).ConfigureAwait(false);
    }

    private void DrawTwitchDiscover()
    {
        if (twitchTrendingToken != CurrentSession?.Token) trendingStreams = [];
        if (twitchPopularToken != CurrentSession?.Token) twitchPopularChannels = [];
        DrawTwitchCheck(includeFavourites: false);
        var width = ImGui.GetContentRegionAvail().X;
        if (GameLayoutButton("Popular on Alpha Channel", FontAwesomeIcon.Users, Ui(228), !twitchDiscoverGlobal, height: 36)) twitchDiscoverGlobal = false;
        ImGui.SameLine();
        if (GameLayoutButton("Trending on Twitch", FontAwesomeIcon.BroadcastTower, Ui(190), twitchDiscoverGlobal, height: 36)) twitchDiscoverGlobal = true;
        if (twitchDiscoverGlobal)
        {
            var categories = new[] { "All categories" }.Concat(trendingStreams.Select(s => s.GameName)
                .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)).ToArray();
            if (width >= Ui(630)) ImGui.SameLine();
            TwitchHubChoice("twitchCategory", categories, twitchCategoryFilter, width >= Ui(630) ? width - Ui(438) : width,
                selected => twitchCategoryFilter = categories[selected]);
        }
        SetGameLayoutRowGap(Ui(12));
        var heading = ImGui.GetCursorScreenPos();
        using (UiFonts.PushRelative(1.08f, emphasis: true)) ImGui.TextUnformatted(twitchDiscoverGlobal ? "Trending on Twitch" : "Popular on Alpha Channel");
        ImGui.SetCursorScreenPos(heading + new Vector2(width - Ui(36), -Ui(3)));
        using (UiDisabled.Scope(twitchDiscoverGlobal ? twitchTrendingLoading : twitchPopularLoading))
            if (GameLayoutButton("", FontAwesomeIcon.SyncAlt, Ui(36), height: 36))
            {
                if (twitchDiscoverGlobal) { twitchTrendingRefreshAt = DateTime.MinValue; RefreshTwitchTrending(); }
                else RefreshTwitchPopular(force: true);
            }
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Refresh channels");
        ImGui.SetCursorScreenPos(heading + UiVec(0, 43));
        if (twitchDiscoverGlobal)
        {
            RefreshTwitchTrending();
            if (twitchTrendingUpdatedAt is { } updatedAt)
                using (UiFonts.PushRegularRelative(.82f))
                using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText,
                    $"Updated {FormatTwitchFavouriteCheckAge(updatedAt)} · Refreshes every 10 minutes" +
                    (twitchTrendingStale || twitchTrendingFailed ? ". Showing the last available results while retrying." : "."));
            if (trendingStreams.Length == 0) ImGui.TextWrapped(twitchTrendingLoading ? "Loading Twitch streams…" : "Trending streams aren't available right now. You can still search for a channel or use Favourites.");
            var filtered = trendingStreams.Where(s => twitchCategoryFilter == "All categories" || string.Equals(s.GameName, twitchCategoryFilter, StringComparison.OrdinalIgnoreCase));
            filtered = filtered.OrderByDescending(s => s.ViewerCount).ThenBy(s => s.ChannelName, StringComparer.OrdinalIgnoreCase);
            var entries = filtered.Select(s => (new TwitchStreamInfo(s.Title, s.Url, s.ChannelName, s.ThumbnailUrl, s.GameName, s.ViewerCount), string.Empty)).ToArray();
            if (entries.Length == 0 && trendingStreams.Length > 0) ImGui.TextWrapped("No channels in this category in the current trending list. Choose All categories to see the others.");
            DrawTwitchDiscoveryGrid(entries);
        }
        else
        {
            RefreshTwitchPopular();
            using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText, "Watched in public parties first, then other live channels, then offline channels. Up to 30 other channels are shown.");
            if (twitchPopularError is not null) ImGui.TextWrapped(twitchPopularError);
            var entries = twitchPopularChannels.Select(channel => new TwitchDiscoveryEntry(
                new TwitchStreamInfo(channel.Title, channel.Url, channel.ChannelName, channel.ThumbnailUrl, channel.Category, channel.ViewerCount),
                channel.PartyCount > 0 ? $"{channel.PartyViewers} watching in {channel.PartyCount} public {(channel.PartyCount == 1 ? "party" : "parties")}" :
                    channel.IsLive == true ? "Live · Not currently watched here" : channel.IsLive == false ? "Offline" : "Live status not available",
                channel.IsLive)).ToArray();
            if (entries.Length == 0 && twitchPopularError is null) ImGui.TextWrapped(twitchPopularLoading ? "Loading channels…" : "No channels have been shared in public Watch Parties yet. Search above to find one.");
            DrawTwitchDiscoveryGrid(entries);
        }
    }

    private void TwitchHubChoice(string id, IReadOnlyList<string> choices, string preview, float width, Action<int> changed)
    {
        ImGui.SetNextItemWidth(Math.Max(Ui(80), width));
        using var border = ImRaii.PushStyle(ImGuiStyleVar.PopupBorderSize, Ui(1));
        using var colors = ImRaii.PushColor(ImGuiCol.PopupBg, CardBg).Push(ImGuiCol.Border, HubTint(0x345476));
        if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##" + id, FitTwitchText(preview, width - Ui(35))))) return;
        foreach (var (label, index) in choices.Select((label, index) => (label, index)))
            if (ImGui.Selectable(label, label == preview)) changed(index);
        ImGui.EndCombo();
    }

    private void DrawTwitchHubFavourites()
    {
        RefreshTwitchFavouritesIfStale();
        var width = ImGui.GetContentRegionAvail().X;
        var favourites = GetFavouriteTwitchChannels();
        using (UiFonts.PushRelative(1.08f, emphasis: true)) ImGui.TextUnformatted($"Favourite channels ({favourites.Count})");
        ImGui.SetNextItemWidth(width - Ui(46));
        ImGui.InputTextWithHint("##twitchFavouriteFilter", "Filter favourite channels…", ref twitchFavouriteFilter, 100);
        ImGui.SameLine(0, Ui(10));
        using (UiDisabled.Scope(isRefreshingTwitchFavourites))
            if (GameLayoutButton("", FontAwesomeIcon.SyncAlt, Ui(36), height: 36)) RefreshTwitchFavouritesIfStale(forceRefresh: true);
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Refresh favourite channels");
        if (favourites.Count == 0)
        {
            DrawEmptyTwitchFavourites();
            if (GameLayoutButton("Find channels", FontAwesomeIcon.Search, Ui(160), true, height: 34)) twitchHubTab = TwitchHubTab.Discover;
            return;
        }
        var entries = favourites.Select(channel => (Channel: channel, Status: twitchFavouriteStatuses.GetValueOrDefault(channel)))
            .Where(entry => entry.Channel.Contains(twitchFavouriteFilter, StringComparison.OrdinalIgnoreCase) ||
                entry.Status?.Stream?.Title.Contains(twitchFavouriteFilter, StringComparison.OrdinalIgnoreCase) == true).ToArray();
        var live = entries.Where(entry => entry.Status?.Stream is not null).OrderBy(entry => entry.Channel, StringComparer.OrdinalIgnoreCase).ToArray();
        ImGui.Checkbox("Live only", ref twitchFavouritesLiveOnly);
        ImGui.SameLine(0, Ui(18));
        using (UiFonts.PushRegularRelative(.82f)) ImGui.TextColored(MutedText, isRefreshingTwitchFavourites ? "Checking channels…" :
            twitchFavouritesLastRefreshUtc is { } refreshed ? "Checked " + FormatTwitchFavouriteCheckAge(refreshed) : "Not checked yet");
        if (live.Length > 0)
        {
            using (UiFonts.PushRelative(1.06f, emphasis: true)) ImGui.TextUnformatted($"Live now ({live.Length})");
            DrawTwitchDiscoveryGrid(live.Select(entry => (entry.Status!.Stream!, string.Empty)).ToArray());
        }
        var other = entries.Where(entry => entry.Status?.Stream is null).OrderBy(entry => entry.Channel, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!twitchFavouritesLiveOnly && other.Length > 0)
        {
            using (UiFonts.PushRelative(1.06f, emphasis: true)) ImGui.TextUnformatted($"Other channels ({other.Length})");
            var columns = width >= Ui(620) ? 2 : 1;
            var cardWidth = (width - Ui(12) * (columns - 1)) / columns;
            for (var i = 0; i < other.Length; i++)
            {
                if (i % columns != 0) ImGui.SameLine(0, Ui(12));
                if (DrawTwitchFavouriteCard(other[i].Channel, other[i].Status, cardWidth)) break;
            }
        }
        if (entries.Length == 0 || twitchFavouritesLiveOnly && live.Length == 0)
            ImGui.TextColored(MutedText, "No favourite channels match this filter.");
    }

    private void RefreshTwitchTrending()
    {
        if (CurrentSession is not { } session || twitchTrendingLoading) return;
        if (twitchTrendingToken != session.Token)
        {
            trendingStreams = []; twitchTrendingRefreshAt = DateTime.MinValue; twitchTrendingUpdatedAt = null;
            twitchTrendingStale = twitchTrendingFailed = false;
        }
        if (DateTime.UtcNow < twitchTrendingRefreshAt) return;
        twitchTrendingToken = session.Token; twitchTrendingLoading = true;
        twitchTrendingRefreshAt = DateTime.UtcNow.AddMinutes(10);
        _ = LoadTwitchTrendingAsync(session.Token);
    }

    private async Task LoadTwitchTrendingAsync(string token)
    {
        var feed = await twitchClient.GetTrendingAsync(youtubeFeedToken).ConfigureAwait(false);
        if (Volatile.Read(ref disposeStarted) != 0) return;
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (Volatile.Read(ref disposeStarted) != 0) return;
            twitchTrendingLoading = false;
            if (CurrentSession?.Token != token) return;
            twitchTrendingFailed = feed is null;
            if (feed is not null)
            {
                trendingStreams = feed.Streams;
                twitchTrendingUpdatedAt = DateTimeOffset.FromUnixTimeSeconds(feed.LastUpdatedUnix).UtcDateTime;
                twitchTrendingStale = feed.Stale;
                var nextUpdate = twitchTrendingUpdatedAt.Value.AddMinutes(10).AddSeconds(5);
                twitchTrendingRefreshAt = nextUpdate > DateTime.UtcNow ? nextUpdate : DateTime.UtcNow.AddMinutes(1);
            }
            if (feed is null || feed.Stale) twitchTrendingRefreshAt = DateTime.UtcNow.AddMinutes(1);
        }).ConfigureAwait(false);
    }

    // This also records public Twitch playback while the main window is closed.
    internal void UpdateTwitchPopular()
    {
        if (twitchPopularLoading || DateTime.UtcNow < twitchPopularRefreshAt || CurrentSession is null) return;
        if (queue.Current is { } current && TwitchChannelChecker.TryNormalizeChannelName(current.Url, out _, out _) &&
            Uri.TryCreate(current.Url, UriKind.Absolute, out var url) && url.Host.EndsWith("twitch.tv", StringComparison.OrdinalIgnoreCase))
            RefreshTwitchPopular();
    }

    private void RefreshTwitchPopular(bool force = false)
    {
        if (CurrentSession is not { } session || twitchPopularLoading || Volatile.Read(ref disposeStarted) != 0) return;
        if (twitchPopularToken != session.Token)
        {
            twitchPopularChannels = []; twitchPopularRefreshAt = DateTime.MinValue; twitchPopularError = null;
        }
        if (!force && DateTime.UtcNow < twitchPopularRefreshAt) return;
        twitchPopularLoading = true; twitchPopularToken = session.Token;
        twitchPopularRefreshAt = DateTime.UtcNow.AddSeconds(75);
        _ = LoadTwitchPopularAsync(session.Token);
    }

    private async Task LoadTwitchPopularAsync(string token)
    {
        Auth.TwitchPopularChannel[]? channels = null;
        try { channels = await Auth.TwitchPopularClient.GetAsync(token, youtubeFeedToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (youtubeFeedToken.IsCancellationRequested) { return; }
        catch { /* Keep the last good list; display a retryable error instead of clearing it. */ }
        if (Volatile.Read(ref disposeStarted) != 0 || youtubeFeedToken.IsCancellationRequested) return;
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (Volatile.Read(ref disposeStarted) != 0) return;
            twitchPopularLoading = false;
            if (CurrentSession?.Token != token) return;
            if (channels is not null) twitchPopularChannels = channels;
            twitchPopularError = channels is null ? "Popular channels couldn't be refreshed. Please try again shortly." : null;
        }).ConfigureAwait(false);
    }

    private sealed record TwitchDiscoveryEntry(TwitchStreamInfo Info, string Detail, bool? IsLive);

    private void DrawTwitchDiscoveryGrid((TwitchStreamInfo Info, string Detail)[] entries, bool verifiedLive = true) =>
        DrawTwitchDiscoveryGrid(entries.Select(entry => new TwitchDiscoveryEntry(entry.Info, entry.Detail, verifiedLive ? true : null)).ToArray());

    private void DrawTwitchDiscoveryGrid(TwitchDiscoveryEntry[] entries)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var columns = width >= Ui(660) ? 3 : width >= Ui(440) ? 2 : 1;
        var cardWidth = (width - Ui(12) * (columns - 1)) / columns;
        for (var i = 0; i < entries.Length; i++)
        {
            if (i % columns != 0) ImGui.SameLine(0, Ui(12));
            using var id = ImRaii.PushId(i);
            var (info, detail, isLive) = entries[i];
            var favouriteChannel = TwitchChannelChecker.TryNormalizeChannelName(info.Url, out var login, out _) ? login : info.ChannelName;
            var innerWidth = cardWidth - Ui(20);
            var imageHeight = innerWidth * 9 / 16;
            var titleLineHeight = ImGui.GetTextLineHeight();
            var cardHeight = imageHeight + titleLineHeight * 2 + Ui(138);
            DrawPlayerHubPanel("##twitchDiscoveryCard", cardWidth, cardHeight, false, () =>
            {
                var origin = ImGui.GetCursorScreenPos();
                var draw = ImGui.GetWindowDrawList();
                var imageSize = new Vector2(innerWidth, imageHeight);
                if (isLive != true && thumbnails.Get(info.ThumbnailUrl) is { } profile)
                {
                    draw.AddRectFilled(origin, origin + imageSize, ImGui.GetColorU32(HubSurface(0x1B283D)), Ui(6));
                    var inset = new Vector2((imageSize.X - imageSize.Y) / 2, 0);
                    draw.AddImageRounded(profile.Handle, origin + inset, origin + inset + new Vector2(imageSize.Y),
                        Vector2.Zero, Vector2.One, uint.MaxValue, Ui(6));
                }
                else DrawTwitchHubThumbnail(info, origin, imageSize, showLive: isLive == true);
                if (isLive == false)
                {
                    draw.AddRectFilled(origin, origin + imageSize, ImGui.GetColorU32(new Vector4(0, 0, 0, .35f)), Ui(5));
                    draw.AddText(origin + UiVec(7, 7), ImGui.GetColorU32(Vector4.One), "OFFLINE");
                }
                ImGui.SetCursorScreenPos(origin + new Vector2(innerWidth - Ui(32), Ui(4)));
                DrawTwitchFavouriteButton("discoveryFavourite", favouriteChannel, IsFavouriteTwitchChannel(favouriteChannel), UiVec(28, 28));
                if (isLive == true && info.ViewerCount is { } viewers)
                {
                    using var font = UiFonts.PushRegularRelative(.78f);
                    var label = FormatArchiveDownloads(viewers) + " viewers";
                    var badgeSize = ImGui.CalcTextSize(label) + UiVec(10, 7);
                    var badgeAt = origin + imageSize - badgeSize - UiVec(4, 4);
                    draw.AddRectFilled(badgeAt, badgeAt + badgeSize, ImGui.GetColorU32(new Vector4(0, 0, 0, .8f)), Ui(4));
                    draw.AddText(badgeAt + UiVec(5, 3), ImGui.GetColorU32(Vector4.One), label);
                }
                ImGui.SetCursorScreenPos(origin + new Vector2(0, imageHeight + Ui(10)));
                using (UiFonts.PushRelative(1f, emphasis: true)) ImGui.TextUnformatted(FitTwitchText(info.ChannelName, innerWidth));
                var title = PrepareTwitchDisplayText(info.Title);
                var titleAt = origin + new Vector2(0, imageHeight + Ui(34));
                DrawWrappedLines(draw, titleAt, innerWidth, titleLineHeight, 2, ImGui.GetColorU32(Vector4.One), title);
                ImGui.SetCursorScreenPos(titleAt);
                ImGui.InvisibleButton("##twitchCardTitle", new(innerWidth, titleLineHeight * 2));
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip(title);
                var categoryAt = titleAt + new Vector2(0, titleLineHeight * 2 + Ui(7));
                using (UiFonts.PushRegularRelative(.82f))
                {
                    draw.AddText(categoryAt, ImGui.GetColorU32(MutedText), FitTwitchText(info.Category ?? "Twitch", innerWidth));
                    if (detail.Length > 0) draw.AddText(categoryAt + UiVec(0, 20), ImGui.GetColorU32(isLive == false ? MutedText : AccentHover), FitTwitchText(detail, innerWidth));
                }
                ImGui.SetCursorPosY(cardHeight - Ui(42));
                using (UiDisabled.Scope(isLive == false))
                    if (GameLayoutButton(isLive == false ? "Offline" : "Watch", FontAwesomeIcon.Play, Math.Max(Ui(60), innerWidth - Ui(40)), true, height: 32)) PlayTwitchHubStream(info);
                ImGui.SameLine(0, Ui(8));
                if (GameLayoutButton("", FontAwesomeIcon.EllipsisH, Ui(32), height: 32)) ImGui.OpenPopup("twitchCardMenu");
                using var menuBorder = ImRaii.PushStyle(ImGuiStyleVar.PopupBorderSize, Ui(1));
                using var menuColor = ImRaii.PushColor(ImGuiCol.PopupBg, CardBg).Push(ImGuiCol.Border, HubTint(0x345476));
                using var popup = ImRaii.Popup("twitchCardMenu");
                if (popup)
                {
                    if (ImGui.Selectable("Open on Twitch")) Dalamud.Utility.Util.OpenLink(info.Url);
                    if (ImGui.Selectable("Add to queue")) HandleAddToQueue(new VideoQueueEntry(info.Url, info.Title, info.ChannelName, null, info.ThumbnailUrl));
                    if (ImGui.Selectable(IsFavouriteTwitchChannel(favouriteChannel) ? "Remove from favourites" : "Add to favourites")) ToggleFavouriteTwitchChannel(favouriteChannel);
                }
            }, contentPadding: 10);
        }
    }
}

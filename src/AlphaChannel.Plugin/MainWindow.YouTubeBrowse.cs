using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.YouTube;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string youtubeBrowseQuery = string.Empty;
    private bool youtubeBrowseShowingSearch;
    private bool youtubeBrowseRefreshingSubscriptions;
    private bool youtubeBrowseRefreshingLikes;
    private readonly YouTubeSearchUsage youtubeSearchUsage = new();
    private const int YouTubeBrowseVideoLimit = 150;
    private List<VideoSearchEntry>? searchDisplaySource, searchDisplayVideos;
    private int searchDisplayRevision = -1, searchDisplayHidden = -1;
    private VideoSearchOrder searchDisplayOrder;
    private VideoDurationFilter searchDisplayDuration;

    private bool youtubeBrowseRefreshDrawn;

    private void DrawYouTubeBrowsePage()
    {
        youtubeBrowseRefreshDrawn = false;
        youtubeHubCacheUpdating = false;
        DrawYouTubeHubHeader();
        DrawYouTubeSearchUsage();

        if (viewedChannelId is not null)
        {
            DrawVideoGrid();
            return;
        }

        if (youtubeBrowseShowingSearch)
            DrawYouTubeBrowseSearchResults();
        else
            DrawVideoGrid();
    }

    private void DrawYouTubeSearchUsage()
    {
        if (youtubeSearchUsage.ShowWarning)
        {
            ImGui.TextWrapped("Please be aware that searching too often can result in rate limiting by YouTube which can temporarily reduce your ability to fetch content from YouTube");
            if (youtubeSearchUsage.BlockedSeconds > 0)
                ImGui.TextColored(MutedText, $"Please wait {youtubeSearchUsage.BlockedSeconds} seconds before searching again.");
            ImGui.Dummy(UiVec(0f, 8f));
        }

    }

    private static string GetYouTubeBrowseChannelKey(VideoSearchEntry video) =>
        !string.IsNullOrWhiteSpace(video.ChannelId)
            ? video.ChannelId
            : "name:" + video.ChannelName;

    private void DrawYouTubeBrowseSubscriptionSelector(float selectorWidth)
    {
        var channels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in Plugin.Cfg.SubscribedYouTubeChannelIds.Where(id => !string.IsNullOrWhiteSpace(id)))
            channels[id] = GetSubscriptionDisplayName(id);
        if (CanSyncYouTubeSubscriptionVideos)
            foreach (var video in youtubeSubscriptionFeedResults ?? [])
            {
                var key = GetYouTubeBrowseChannelKey(video);
                if (!channels.ContainsKey(key)) channels[key] = video.ChannelName;
            }

        selectedSubscriptionChannels.RemoveWhere(id => !channels.ContainsKey(id));
        var summary = selectedSubscriptionChannels.Count switch
        {
            0 => "All subscriptions",
            1 => channels.GetValueOrDefault(selectedSubscriptionChannels.First(), "1 selected"),
            _ => $"{selectedSubscriptionChannels.Count} selected"
        };
        ImGui.SetNextItemWidth(selectorWidth);
        if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##youtubeBrowseSubscriptions", summary))) return;
        var all = selectedSubscriptionChannels.Count == 0;
        using (UiDisabled.Scope(all))
            if (ImGui.Checkbox("All subscriptions", ref all)) selectedSubscriptionChannels.Clear();
        ImGui.Separator();
        foreach (var channel in channels.OrderBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase))
        {
            var selected = selectedSubscriptionChannels.Contains(channel.Key);
            if (!ImGui.Checkbox($"{channel.Value}##youtubeBrowseChannel_{channel.Key}", ref selected)) continue;
            if (selected) selectedSubscriptionChannels.Add(channel.Key);
            else selectedSubscriptionChannels.Remove(channel.Key);
        }
        ImGui.EndCombo();
    }

    private void DrawYouTubeBrowseAccount(float width)
    {
        var connected = YouTubeEmbeddedBrowserSession.IsConnected(Plugin.Cfg);
        var needsReconnect = connected && Plugin.Cfg.YouTubeSessionNeedsReconnect;
        var origin = ImGui.GetCursorScreenPos();
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7f)))
        using (ImRaii.PushColor(ImGuiCol.Button, needsReconnect
                   ? new Vector4(0.24f, 0.075f, 0.095f, 1f)
                   : CardBg)
                   .Push(ImGuiCol.ButtonHovered, needsReconnect
                       ? new Vector4(0.31f, 0.10f, 0.12f, 1f)
                       : FrameBgHover))
        {
            if (ImGui.Button("##youtubeBrowseAccount", new Vector2(width, Ui(44f))))
            {
                settingsTab = SettingsTab.YouTube;
                currentPage = HomePage.Settings;
            }
        }
        if (ImGui.IsItemHovered())
            UiFonts.SetTooltip(needsReconnect
                ? "YouTube session expired. Please Reconnect"
                : connected ? "Manage YouTube account"
                    : "Connect your YouTube account in Settings to sync likes and subscriptions.");

        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(origin, origin + UiVec(width, 44f), true);
        DrawAvatarAt(origin + UiVec(7f, 5f), connected ? "User" : "SignInAlt", null, Ui(34f),
            connected ? Plugin.Cfg.YouTubeConnectedAvatarUrl : null);
        var name = connected && !string.IsNullOrWhiteSpace(Plugin.Cfg.YouTubeConnectedDisplayName)
            ? Plugin.Cfg.YouTubeConnectedDisplayName
            : connected ? "YouTube connected" : "YouTube not connected";
        var detail = connected
            ? Plugin.Cfg.YouTubeConnectedHandle ?? Plugin.Cfg.YouTubeConnectedChannelName ?? "Connected account"
            : "Connect account in Settings";
        draw.AddText(origin + UiVec(50f, 5f), ImGui.GetColorU32(Vector4.One),
            TruncateToWidth(name, MathF.Max(Ui(30), width - Ui(83))));
        if (needsReconnect)
        {
            var warningX = MathF.Min(Ui(58f) + ImGui.CalcTextSize(name).X, width - Ui(56f));
            using var iconFont = UiFonts.PushIcon();
            UiFonts.DrawText(draw, ImGui.GetFontSize(), origin + new Vector2(warningX, Ui(6f)), ImGui.GetColorU32(Danger), FontAwesomeIcon.ExclamationTriangle.ToIconString());
        }
        draw.AddText(origin + UiVec(50f, 24f), ImGui.GetColorU32(MutedText),
            TruncateToWidth(detail, MathF.Max(Ui(30), width - Ui(83))));
        using (UiFonts.PushIcon())
            UiFonts.DrawText(draw, ImGui.GetFontSize(), origin + new Vector2(width - Ui(23f), Ui(13f)), ImGui.GetColorU32(Accent), FontAwesomeIcon.Cog.ToIconString());
        draw.PopClipRect();
    }

    private void DrawYouTubeBrowseInlineStatus()
    {
        DrawYouTubeBrowseInlineStatusCore();
        DrawYouTubeBrowseResultsRefresh();
    }

    private void DrawYouTubeBrowseResultsRefresh()
    {
        if (currentPage != HomePage.VideoGrid || youtubeBrowseShowingSearch || viewedChannelId is not null || youtubeBrowseRefreshDrawn) return;
        youtubeBrowseRefreshDrawn = true;
        var refreshing = browseVideoSectionTab switch {
            0 => isLoadingBrowseVideos,
            1 => youtubeBrowseRefreshingSubscriptions || isLoadingSubscriptionVideos,
            2 => youtubeBrowseRefreshingLikes || isLoadingFavouriteVideos,
            3 => isSyncingYouTubeRecommendedVideos,
            _ => popularListLoading };
        Action refresh = browseVideoSectionTab switch {
            0 => () => { isLoadingBrowseVideos = true; _ = LoadBrowseVideosAsync(true); },
            1 => () => _ = RefreshYouTubeBrowseAccountVideosAsync(true),
            2 => () => _ = RefreshYouTubeBrowseAccountVideosAsync(false),
            3 => RefreshYouTubeBrowseRecommendations,
            _ => () => RefreshPopularList(force: true) };
        var toolbarNextCursor = ImGui.GetCursorScreenPos();
        DrawYouTubeHubResultsSort();
        ImGui.SameLine(0f, Ui(8f));
        ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - Ui(30));
        DrawDjActionButton("##youtubeResultsRefresh", FontAwesomeIcon.Sync, "", UiVec(30, 26), refreshing, refresh);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiFonts.SetTooltip(refreshing ? "Refreshing videos…" : "Refresh videos");
        ImGui.SetCursorScreenPos(new Vector2(toolbarNextCursor.X, MathF.Max(toolbarNextCursor.Y, ImGui.GetCursorScreenPos().Y)));
    }

    private void DrawYouTubeBrowseInlineStatusCore()
    {
        var now = DateTime.UtcNow;
        var guestAvailable = YouTubeRequestScheduler.Shared.GetPausedUntilUtc(YouTubeRequestIdentity.Guest) <= now;
        var accountAvailable = YouTubeRequestScheduler.Shared.GetPausedUntilUtc(YouTubeRequestIdentity.Account) <= now;
        var feedUpdating = browseVideoSectionTab switch
        {
            0 => guestAvailable && isLoadingBrowseVideos,
            1 => (accountAvailable && isSyncingYouTubeSubscriptionVideos) || (guestAvailable && isLoadingSubscriptionVideos),
            2 => (accountAvailable && isSyncingYouTubeLikedVideos) || (guestAvailable && isLoadingFavouriteVideos),
            3 => accountAvailable && isSyncingYouTubeRecommendedVideos,
            _ => popularListLoading
        };
        youtubeHubCacheUpdating = feedUpdating || (guestAvailable &&
            ((youtubeCardMetadataLoading && youtubeVisibleMetadata.PendingCount > 0) ||
             (browseVideoSectionTab == 4 && popularCountLookupBusy)));
    }

    private async Task RefreshYouTubeBrowseAccountVideosAsync(bool subscriptions)
    {
        youtubeMetadataReloadVersion++;
        if (subscriptions)
        {
            if (youtubeBrowseRefreshingSubscriptions) return;
            youtubeBrowseRefreshingSubscriptions = true;
        }
        else
        {
            if (youtubeBrowseRefreshingLikes) return;
            youtubeBrowseRefreshingLikes = true;
        }

        try
        {
            var now = DateTime.UtcNow;
            if (subscriptions)
            {
                if (CanSyncYouTubeSubscriptionVideos && !isSyncingYouTubeSubscriptionVideos &&
                    now - Plugin.Cfg.YouTubeSubscriptionVideosLastManualFetchUtc >= YouTubeSubscriptionVideoRetryInterval)
                    await FetchYouTubeSubscriptionVideosAsync(manual: true);
                await LoadSubscriptionVideosAsync(forceRefresh: true);
            }
            else
            {
                if (CanSyncYouTubeLikedVideos && !isSyncingYouTubeLikedVideos &&
                    now - Plugin.Cfg.YouTubeLikedVideosLastManualFetchUtc >= YouTubeLikedVideoRetryInterval)
                    await FetchYouTubeLikedVideosAsync(manual: true);
                await LoadFavouriteVideosAsync(forceRefresh: true);
            }
        }
        finally
        {
            if (subscriptions) youtubeBrowseRefreshingSubscriptions = false;
            else youtubeBrowseRefreshingLikes = false;
        }
    }

    private void StartYouTubeBrowseSearch()
    {
        var query = youtubeBrowseQuery.Trim();
        if (query.Length == 0) return;
        if (IsYouTubeBrowseVideoUrl(query))
        {
            HandlePlayNow(new VideoQueueEntry(query, query, string.Empty, null, null));
            return;
        }
        if (isSearching) return;
        if (!youtubeSearchUsage.TrySearch()) return;
        searchQuery = query;
        searchResults = null;
        youtubeBrowseShowingSearch = true;
        isSearching = true;
        _ = RunSearchAsync(query);
    }

    private static bool IsYouTubeBrowseVideoUrl(string value)
    {
        var url = value.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http"))
            return false;

        var host = uri.Host;
        if (!host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("www.youtu.be", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".youtube-nocookie.com", StringComparison.OrdinalIgnoreCase))
            return false;

        return GetYouTubeVideoId(url) is not null;
    }

    private void DrawYouTubeBrowseSearchResults()
    {
        if (ImGui.Button("Back to videos##youtubeBrowseBack"))
        {
            youtubeBrowseShowingSearch = false;
            return;
        }
        ImGui.SameLine(0f, Ui(12f));
        ImGui.TextColored(MutedText, $"Search results for {searchQuery}");
        if (searchResolver.UsedCachedSearch(searchQuery))
            ImGui.TextWrapped("YouTube search is currently experiencing issues, so results may be limited.");
        ImGui.Dummy(UiVec(0f, 10f));

        if (isSearching)
        {
            ImGui.TextColored(MutedText, "Searching YouTube...");
            return;
        }
        if (searchResults is not { Count: > 0 } results)
        {
            DrawBrowseVideoEmptyState("No videos found", "Try another YouTube search.");
            return;
        }


        if (!ReferenceEquals(results, searchDisplaySource) || searchDisplayRevision != youtubeMetadataRevision ||
            searchDisplayHidden != youtubeHiddenChannelsRevision || searchDisplayOrder != youtubeSearchOrder ||
            searchDisplayDuration != youtubeDurationFilter)
        {
            searchDisplayVideos = FilterAndOrderSearchResults(results, youtubeSearchOrder, youtubeDurationFilter)
                .Where(video => !IsYouTubeChannelHidden(video)).ToList();
            searchDisplaySource = results; searchDisplayRevision = youtubeMetadataRevision;
            searchDisplayHidden = youtubeHiddenChannelsRevision; searchDisplayOrder = youtubeSearchOrder;
            searchDisplayDuration = youtubeDurationFilter;
        }
        var displayed = searchDisplayVideos!;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(MutedText, $"{displayed.Count} videos");
        DrawVideoSearchFilters("youtubeBrowse", ref youtubeSearchOrder, ref youtubeDurationFilter);
        ImGui.Dummy(UiVec(0f, 6f));

        var contentWidth = YouTubeCardGridWidth;
        var columns = BrowseVideoGridColumns(contentWidth);
        var gap = Ui(12f);
        var cardWidth = (contentWidth - gap * (columns - 1)) / columns;
        var rowStart = ImGui.GetCursorPos();
        for (var index = 0; index < displayed.Count; index++)
        {
            if (index > 0)
            {
                if (index % columns == 0)
                    ImGui.SetCursorPos(rowStart + new Vector2(0f, (index / columns) * Ui(278f)));
                else
                    ImGui.SameLine(0f, gap);
            }
            ImGui.PushID($"youtubeBrowseResult_{index}");
            DrawBrowseVideoCardSurface(displayed[index], null, cardWidth, BrowseVideoCardHeight, showFavouriteAction: true);
            ImGui.PopID();
        }
    }
}

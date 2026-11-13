using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Video;
using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using YoutubeExplode.Videos;

namespace AlphaChannel.Plugin;

// Shared media cards, party presentation, featured-feed loading and Home search.
internal sealed partial class MainWindow
{
    private static readonly Vector4[] AvatarPalette =
    [
        new(0.55f, 0.35f, 0.95f, 1f),
        new(0.95f, 0.45f, 0.55f, 1f),
        new(0.35f, 0.65f, 0.95f, 1f),
        new(0.95f, 0.70f, 0.30f, 1f),
        new(0.40f, 0.85f, 0.65f, 1f),
    ];
    // Player source tabs: Home CTAs set this before navigating to Player.
    private int playerSourceTab;
    private string homeSearch = string.Empty;
    private string? pendingPlayerSearch;
    private bool homeSearchPopupOpen;
    private int homeSearchInputVersion;

    private static bool IsHomeSearchUrl(string query)
        => Uri.TryCreate(query, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private bool HomeSearchHasParty => stream.Mode is StreamMode.Hosting or StreamMode.Viewing;

    private void SubmitHomeSearch(string input, int action = 0)
    {
        var query = input.Trim();
        if (query.Length == 0)
        {
            return;
        }

        if (IsHomeSearchUrl(query))
        {
            if (action == 0)
                HandlePlayNow(new VideoQueueEntry(query, query, string.Empty, null, null));
            else if (action == 1 || action == 2 && HomeSearchHasParty)
                BeginHomeSearchQueueAddition(query, action == 2);
        }
        else if (action == 1)
        {
            OpenPlayerSearch(2, query);
        }
        else
        {
            OpenUnifiedVideoSearch(query);
        }

        homeSearch = string.Empty;
        homeSearchPopupOpen = false;
        homeSearchInputVersion++;
    }

    private Vector2 homeSearchInputPos;
    private readonly Dictionary<string, ISharedImmediateTexture?> capabilityImages = new();

    private const string FeaturedChannelId = "UCpx2BZg8ABgaDV50sGJtWAg";
    private const int FeaturedSlideCount = 6;
    private static readonly TimeSpan FeaturedSlideCacheDuration = TimeSpan.FromHours(3);
    private VideoSearchEntry[] featuredSlideResults = [];
    private bool featuredCacheRestored;
    private int featuredRetryCount;
    private DateTime featuredNextRefreshUtc = DateTime.MinValue;
    private volatile bool featuredLoadRunning;

    private ISharedImmediateTexture? GetCapabilityImage(string fileName)
{
    if (capabilityImages.TryGetValue(fileName, out var cached))
    {
        return cached;
    }

    var path = Path.Combine(
        Plugin.PluginInterface.AssemblyLocation.DirectoryName!,
        "Assets",
        fileName);

    ISharedImmediateTexture? image = null;

    if (File.Exists(path))
    {
        image = Plugin.TextureProvider.GetFromFile(path);
    }

    capabilityImages[fileName] = image;
    return image;
}

    private void TryRefreshFeaturedSlides()
    {
        if (Volatile.Read(ref disposeStarted) != 0) return;
        RestoreFeaturedSlidesCache();
        if (featuredLoadRunning ||
            DateTime.UtcNow < featuredNextRefreshUtc)
        {
            return;
        }

        featuredLoadRunning = true;
        _ = LoadFeaturedSlidesAsync();
    }

    private void DrawHomePageHeader()
    {
        var headerLayout = CalculateHomeHeaderLayout(ImGui.GetCursorPos(), ImGui.GetContentRegionAvail().X);
        TryRefreshFeaturedSlides();
        if (Plugin.Cfg.ShowHomeHeroImage) EnsureHomeHeroLoaded();

        // Draw the input first so the dropdown uses this frame's exact bounds.
        DrawHomeHeader(headerLayout);
        var query = homeSearch.Trim();
        if (!homeSearchPopupOpen || query.Length == 0) return;

        var position = homeSearchInputPos + new Vector2(0, homeSearchInputHeight + Ui(6f));
        var width = headerLayout.SearchWidth + Ui(2f);
        var selected = DrawHomeSearchDropdown(position, width, query, IsHomeSearchUrl(query));
        if (selected is { } action)
            SubmitHomeSearch(query, action);
        else if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            homeSearchPopupOpen = false;
        else if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
                 !ImGui.IsMouseHoveringRect(homeSearchInputPos, homeSearchInputPos + new Vector2(width, homeSearchInputHeight), false) &&
                 !ImGui.IsMouseHoveringRect(position, position + new Vector2(width, HomeSearchDropdownHeight(IsHomeSearchUrl(query))), false))
            homeSearchPopupOpen = false;
    }

    private static string? GetYouTubeVideoId(
    string url)
    {
        var videoId =
            VideoId.TryParse(url);

        return videoId?.Value;
    }

    private static readonly Vector4[] BrowseTopicTagPalette =
[
    new(0.55f, 0.32f, 0.95f, 1f),
    new(0.18f, 0.58f, 0.92f, 1f),
    new(0.10f, 0.68f, 0.56f, 1f),
    new(0.90f, 0.45f, 0.20f, 1f),
    new(0.82f, 0.30f, 0.52f, 1f),
    new(0.68f, 0.52f, 0.12f, 1f),
    new(0.35f, 0.52f, 0.92f, 1f),
    new(0.72f, 0.28f, 0.82f, 1f)
];

    private static Vector4 GetBrowseTopicTagColour(
        string topic)
    {
        unchecked
        {
            uint hash =
                2166136261;

            foreach (var character in topic)
            {
                hash ^=
                    char.ToUpperInvariant(
                        character);

                hash *=
                    16777619;
            }

            return BrowseTopicTagPalette[
                hash %
                BrowseTopicTagPalette.Length];
        }
    }

    private void DrawHomeYouTubeCard(
        VideoSearchEntry result,
        float width,
        float height,
        bool browseLayout = false,
        string? browseTopic = null,
        bool showBrowseFavouriteAction = false,
        bool sizingPreview = false,
        IDalamudTextureWrap? previewThumbnail = null,
        float? previewThumbnailHeight = null)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height);
        if (!ImGui.IsRectVisible(origin, origin + size)) { ImGui.Dummy(size); return; }
        if (!sizingPreview) result = VisibleYouTubeCardMetadata(result);
        var drawList = ImGui.GetWindowDrawList();

        // Reserve the complete card for grid layout, but keep its playback
        // hit target separate from the footer's real ImGui buttons.
        var hasFooter = browseLayout;
        var footerHeight = Ui(34f);
        var footerPadding = Ui(8f);
        ImGui.Dummy(size);
        var nextCardCursor = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##homeYoutubeCard", new Vector2(width,
            hasFooter ? height - footerPadding - footerHeight : height));
        var hovered = !sizingPreview && ImGui.IsItemHovered();
        // The card owns the active ImGui ID on mouse-down. Window hover checks
        // would reject its drawn controls as blocked by that ID; item hover
        // still respects clipping and occlusion while the card is active.
        var cardClicked = hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        var actionClicked = false;
        ImGui.SetCursorScreenPos(nextCardCursor);

        var current =
            queue.Current;

        var isNowPlaying =
            current is not null &&
            string.Equals(
                current.Url,
                result.Url,
                StringComparison.OrdinalIgnoreCase);

        var thumbnailHeight = sizingPreview && previewThumbnailHeight is { } previewHeight
            ? previewHeight : (browseLayout && currentPage == HomePage.VideoGrid ? BrowseVideoThumbnailHeight : Ui(116f));

        // ---------------------------------------------------------
        // Thumbnail
        // ---------------------------------------------------------

        var thumbnail =
            sizingPreview ? previewThumbnail : thumbnails.Get(result.ThumbnailUrl);

        if (thumbnail is not null)
        {
            drawList.AddImageRounded(
                thumbnail.Handle,
                origin,
                origin + new Vector2(
                    width,
                    thumbnailHeight),
                Vector2.Zero,
                Vector2.One,
                uint.MaxValue,
                9f);
        }
        else
        {
            drawList.AddRectFilled(
                origin,
                origin + new Vector2(
                    width,
                    thumbnailHeight),
                ImGui.GetColorU32(CardBg),
                9f);
        }

        // Slight darkening at thumbnail bottom helps the duration badge.
        drawList.AddRectFilled(
            origin + new Vector2(
                0f,
                thumbnailHeight - Ui(22f)),
            origin + new Vector2(
                width,
                thumbnailHeight),
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.18f)),
            0f);

        // ---------------------------------------------------------
        // Now Playing badge
        // ---------------------------------------------------------

        if (isNowPlaying)
        {
            const string badgeText =
                "NOW PLAYING";

            var badgeSize =
                ImGui.CalcTextSize(
                    badgeText);

            var dateBadgeOffset = result.UploadDate is { } playingUploadDate
                ? ImGui.CalcTextSize(FormatRelativeUploadDate(playingUploadDate)).Y + Ui(9f)
                : 0f;

            var badgeMin =
                origin +
                UiVec(7f, 6f) + new Vector2(0f, dateBadgeOffset);

            var badgeMax =
                badgeMin +
                new Vector2(
                    badgeSize.X + Ui(10f),
                    badgeSize.Y + Ui(5f));

            drawList.AddRectFilled(
                badgeMin,
                badgeMax,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.95f)),
                5f);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), badgeMin +
                UiVec(5f, 2f), ImGui.GetColorU32(
                    Vector4.One), badgeText);
        }

        // ---------------------------------------------------------
        // Upload date badge
        // ---------------------------------------------------------

        using (browseLayout ? UiFonts.PushRelative(0.90f) : UiFonts.Empty)
        if (result.UploadDate is { } uploadDate)
        {
            var dateText =
                FormatRelativeUploadDate(uploadDate);

            var dateSize =
                ImGui.CalcTextSize(dateText);

            var badgeMin =
                origin +
                UiVec(7f, 6f);

            var badgeMax =
                badgeMin +
                new Vector2(
                    dateSize.X + Ui(10f),
                    dateSize.Y + Ui(5f));

            drawList.AddRectFilled(
                badgeMin,
                badgeMax,
                ImGui.GetColorU32(
                    new Vector4(
                        0f,
                        0f,
                        0f,
                        0.75f)),
                5f);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), badgeMin +
                UiVec(5f, 2f), ImGui.GetColorU32(Vector4.One), dateText);
        }

        // ---------------------------------------------------------
        // Duration
        // ---------------------------------------------------------

        using (browseLayout ? UiFonts.PushRelative(0.90f) : UiFonts.Empty)
        if (result.Duration is { } duration)
        {
            var durationText =
                FormatTime(
                    (float)duration.TotalSeconds);

            var durationSize =
                ImGui.CalcTextSize(durationText);

            var badgeMin =
                new Vector2(
                    origin.X +
                    width -
                    durationSize.X -
                    Ui(10f),
                    origin.Y +
                    thumbnailHeight -
                    Ui(20f));

            var badgeMax =
                new Vector2(
                    origin.X +
                    width -
                    Ui(4f),
                    origin.Y +
                    thumbnailHeight -
                    Ui(4f));

            drawList.AddRectFilled(
                badgeMin,
                badgeMax,
                ImGui.GetColorU32(
                    new Vector4(
                        0f,
                        0f,
                        0f,
                        0.82f)),
                4f);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                    badgeMin.X + Ui(3f),
                    badgeMin.Y +
                    ((badgeMax.Y - badgeMin.Y) - durationSize.Y) * 0.5f), ImGui.GetColorU32(Vector4.One), durationText);
        }

        // ---------------------------------------------------------
        // Browse topic badge
        // ---------------------------------------------------------

        var hasBrowseTopic =
            browseLayout &&
            !string.IsNullOrWhiteSpace(
                browseTopic);

        if (hasBrowseTopic)
        {
            var topicText =
                browseTopic!.Trim();

            var maximumTopicWidth =
                MathF.Max(
                    width *
                    0.56f,
                    Ui(55f));

            while (topicText.Length > 1 &&
                   ImGui.CalcTextSize(
                       topicText).X +
                   Ui(14f) >
                   maximumTopicWidth)
            {
                topicText =
                    topicText[..^1];
            }

            if (!string.Equals(
                    topicText,
                    browseTopic,
                    StringComparison.Ordinal))
            {
                topicText =
                    topicText.TrimEnd() +
                    "…";
            }

            var topicTextSize =
                ImGui.CalcTextSize(
                    topicText);

            var topicColour =
                GetBrowseTopicTagColour(
                    browseTopic!);

            var topicMin =
                new Vector2(
                    origin.X +
                    Ui(5f),
                    origin.Y +
                    thumbnailHeight -
                    Ui(24f));

            var topicMax =
                topicMin +
                new Vector2(
                    topicTextSize.X +
                    Ui(14f),
                    Ui(19f));

            drawList.AddRectFilled(
                topicMin,
                topicMax,
                ImGui.GetColorU32(
                    new Vector4(
                        topicColour.X,
                        topicColour.Y,
                        topicColour.Z,
                        0.94f)),
                Ui(5f));

            drawList.AddRect(
                topicMin,
                topicMax,
                ImGui.GetColorU32(
                    new Vector4(
                        1f,
                        1f,
                        1f,
                        0.24f)),
                Ui(5f),
                ImDrawFlags.None,
                Ui(1f));

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                    topicMin.X +
                    Ui(7f),
                    topicMin.Y +
                    (Ui(19f) -
                     topicTextSize.Y) *
                    0.5f), ImGui.GetColorU32(
                    Vector4.One), topicText);
        }

        // ---------------------------------------------------------
        // ---------------------------------------------------------
        // ---------------------------------------------------------
        // Live playback progress
        // ---------------------------------------------------------

        if (isNowPlaying)
        {
            var (position, playbackDuration, _) =
    video.GetProgress();

            if (playbackDuration > 0f)
            {
                var progress =
                    Math.Clamp(
                        position / playbackDuration,
                        0f,
                        1f);

                var progressHeight = Ui(3f);

                var progressY =
                    origin.Y +
                    thumbnailHeight -
                    progressHeight;

                // Remaining track.
                drawList.AddRectFilled(
                    new Vector2(
                        origin.X,
                        progressY),
                    new Vector2(
                        origin.X + width,
                        origin.Y + thumbnailHeight),
                    ImGui.GetColorU32(
                        new Vector4(
                            1f,
                            1f,
                            1f,
                            0.16f)));

                // Played portion.
                drawList.AddRectFilled(
                    new Vector2(
                        origin.X,
                        progressY),
                    new Vector2(
                        origin.X +
                        width * progress,
                        origin.Y + thumbnailHeight),
                    ImGui.GetColorU32(
                        Accent));
            }
        }


        // ---------------------------------------------------------
        // Text beneath thumbnail
        // ---------------------------------------------------------

        var textInset = 2f + (0f);
        var textX = origin.X + textInset;
        var textWidth = MathF.Max(width - textInset * 2f, 40f);
        using var hubTitleFont = browseLayout && currentPage == HomePage.VideoGrid ? UiFonts.PushRelative(1f, emphasis: true) : UiFonts.Empty;
        var lineHeight = ImGui.GetTextLineHeight();

        var titleY =
            origin.Y +
            thumbnailHeight +
            (Ui(10f));


        var cardTitle = YouTubeMetadataCache.HasTitle(result.Title) ? result.Title :
            youtubeVisibleMetadata.IsPending(result.Url) ? "Fetching video details…" : "Video details unavailable";
        var titleBottom = DrawWrappedLines(
            drawList,
            new Vector2(
                textX,
                titleY),
            textWidth,
            lineHeight,
            2,
            ImGui.GetColorU32(browseLayout ? Hex(0xE2E7EF) : Vector4.One),
            cardTitle,
            out var titleTruncated);

        if (titleTruncated && hovered && ImGui.IsMouseHoveringRect(
                new Vector2(textX, titleY), new Vector2(textX + textWidth, titleBottom)))
        {
            UiFonts.BeginTooltip();
            ImGui.PushTextWrapPos(Ui(450f));
            ImGui.TextUnformatted(cardTitle);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
        hubTitleFont.Dispose();
        var channel =
         TruncateHomeMediaText(
             result.ChannelName,
             17);

        var metadataY =
            titleY +
            lineHeight *
            2f +
            (Ui(5f));



        var channelY =
    metadataY;

        var userIcon =
            FontAwesomeIcon.User.ToIconString();

        float iconWidth;

        using (UiFonts.PushIcon())
        {
            iconWidth =
                ImGui.CalcTextSize(userIcon).X;

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                    textX,
                    channelY), ImGui.GetColorU32(
                    new Vector4(
                        AccentHover.X,
                        AccentHover.Y,
                        AccentHover.Z,
                        0.78f)), userIcon);
        }

        // ---------------------------------------------------------
        // Plugin-managed YouTube subscription button
        //
        // Subscriptions are keyed by YouTube channel ID.
        // ---------------------------------------------------------

        var isSubscribed =
            !string.IsNullOrWhiteSpace(result.ChannelId) &&
            Plugin.Cfg.SubscribedYouTubeChannelIds.Contains(
                result.ChannelId,
                StringComparer.OrdinalIgnoreCase);

        var subscribedOnYouTube = !isSubscribed &&
            CanSyncYouTubeSubscriptionVideos &&
            !string.IsNullOrWhiteSpace(result.ChannelId) &&
            Plugin.Cfg.YouTubeSubscriptionVideoCache.Any(video =>
                string.Equals(video.ChannelId, result.ChannelId, StringComparison.OrdinalIgnoreCase));

        var subscribeButtonSize = Ui(24f);

        var subscribeMin =
            new Vector2(
                origin.X + width - subscribeButtonSize - Ui(2f),
                channelY - Ui(2f));

        var subscribeMax =
            subscribeMin +
            new Vector2(
                subscribeButtonSize,
                subscribeButtonSize);

        var subscribeHovered =
            hovered &&
            ImGui.IsMouseHoveringRect(subscribeMin, subscribeMax);

        if (browseLayout && currentPage == HomePage.VideoGrid)
        {
            drawList.AddRectFilled(subscribeMin, subscribeMax, ImGui.GetColorU32(HubSurface(0x14243C)), Ui(5));
            drawList.AddRect(subscribeMin, subscribeMax, ImGui.GetColorU32(HubTint(0x395A84)), Ui(5));
        }

        // Leave enough room so a long channel name doesn't run
        // underneath the subscribe button.
        var channelTextMaxWidth =
            MathF.Max(
                subscribeMin.X -
                (textX + iconWidth + 5f) -
                7f,
                20f);

        var displayChannel = channel;

        while (displayChannel.Length > 1 &&
               ImGui.CalcTextSize(displayChannel).X >
               channelTextMaxWidth)
        {
            displayChannel =
                displayChannel[..^1];
        }

        if (!string.Equals(
                displayChannel,
                channel,
                StringComparison.Ordinal))
        {
            displayChannel =
                displayChannel.TrimEnd() + "…";
        }

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                textX + iconWidth + Ui(5f),
                channelY), ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.82f)), displayChannel);

        var channelTextMin =
    new Vector2(
        textX + iconWidth + Ui(5f),
        channelY);

        var channelTextSize =
            ImGui.CalcTextSize(
                displayChannel);

        var channelTextMax =
            channelTextMin +
            channelTextSize;

        var channelHovered =
            // Respect window occlusion and the grid's clipping rectangle,
            // including the playback bar drawn over partially visible cards.
            hovered &&
            ImGui.IsMouseHoveringRect(channelTextMin, channelTextMax);

        if (channelHovered)
        {
            ImGui.SetMouseCursor(
                ImGuiMouseCursor.Hand);

            UiFonts.SetTooltip(
                $"View {result.ChannelName}");

            if (cardClicked)
            {
                actionClicked = true;
                if (!string.IsNullOrWhiteSpace(
                        result.ChannelId))
                {
                    OpenYouTubeChannel(
                        result.ChannelId,
                        result.ChannelName);
                }
            }
        }

        // ---------------------------------------------------------
        // Minimal subscribe button
        // ---------------------------------------------------------

        if (subscribeHovered || isSubscribed)
        {
            drawList.AddCircleFilled(
                subscribeMin +
                new Vector2(
                    subscribeButtonSize * 0.5f,
                    subscribeButtonSize * 0.5f),
                subscribeButtonSize * 0.5f,
                ImGui.GetColorU32(
                    isSubscribed
                        ? new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.22f)
                        : new Vector4(
                            1f,
                            1f,
                            1f,
                            0.08f)));
        }

        // ---------------------------------------------------------
        // + / check icon
        // ---------------------------------------------------------

        var subscribeIcon =
            isSubscribed
                ? FontAwesomeIcon.Check
                : subscribedOnYouTube
                    ? FontAwesomeIcon.Play
                    : FontAwesomeIcon.Plus;

        var subscribeGlyph =
            subscribeIcon.ToIconString();

        Vector2 subscribeGlyphSize;

        using (UiFonts.PushIcon())
        {
            subscribeGlyphSize =
                ImGui.CalcTextSize(
                    subscribeGlyph);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), subscribeMin +
                new Vector2(
                    (subscribeButtonSize - subscribeGlyphSize.X) * 0.5f,
                    (subscribeButtonSize - subscribeGlyphSize.Y) * 0.5f), ImGui.GetColorU32(
                    isSubscribed
                        ? AccentHover
                        : subscribeHovered
                            ? Vector4.One
                            : MutedText), subscribeGlyph);
        }

 

        // ---------------------------------------------------------
        // Subscribe interaction
        // ---------------------------------------------------------

        if (subscribeHovered)
        {
            ImGui.SetMouseCursor(
                ImGuiMouseCursor.Hand);

            UiFonts.SetTooltip(
                isSubscribed
                    ? $"Unsubscribe from {result.ChannelName}"
                    : subscribedOnYouTube
                        ? "Subscribed on YouTube, click to sub on Alpha Channel"
                        : $"Subscribe to {result.ChannelName}");

            if (cardClicked)
            {
                actionClicked = true;
                if (!string.IsNullOrWhiteSpace(
         result.ChannelId))
                {
                    if (isSubscribed)
                    {
                        Plugin.Cfg.SubscribedYouTubeChannelIds.RemoveAll(
                            id => string.Equals(
                                id,
                                result.ChannelId,
                                StringComparison.OrdinalIgnoreCase));

                        Plugin.Cfg.SubscribedYouTubeChannelNames.Remove(
                            result.ChannelId);
                    }
                    else
                    {
                        Plugin.Cfg.SubscribedYouTubeChannelIds.Add(
                            result.ChannelId);

                        Plugin.Cfg.SubscribedYouTubeChannelNames[
                            result.ChannelId] =
                            result.ChannelName;
                    }

                    Plugin.Cfg.Save();
                }
            }
        }

        if (result.ViewCount is { } views)
        {
            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                    textX,
                    channelY +
                    lineHeight +
                    Ui(3f)), ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.58f)), FormatViewCount(views));
        }

        // Hover overlay
        // ---------------------------------------------------------

        var favouriteVideoId =
            GetYouTubeVideoId(result.Url);

        var isFavourite =
            favouriteVideoId is not null &&
            Plugin.Cfg.FavouriteYouTubeVideoIds.Contains(
                favouriteVideoId,
                StringComparer.OrdinalIgnoreCase);
        if (hovered &&
            !browseLayout)
        {
            drawList.AddRectFilled(
                origin,
                origin +
                new Vector2(
                    width,
                    thumbnailHeight),
                ImGui.GetColorU32(
                    new Vector4(
                        0f,
                        0f,
                        0f,
                        0.52f)),
                9f);

            drawList.AddRect(
                origin,
                origin +
                new Vector2(
                    width,
                    thumbnailHeight),
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.70f)),
                9f,
                ImDrawFlags.None,
                1f);

            // -----------------------------------------------------
            // Favourite
            // -----------------------------------------------------

            var favouriteButtonSize = Ui(27f);

            var favouriteMin =
                new Vector2(
                    origin.X + width - favouriteButtonSize - Ui(7f),
                    origin.Y + Ui(7f));

            var favouriteMax =
                favouriteMin +
                new Vector2(
                    favouriteButtonSize,
                    favouriteButtonSize);

            var mouse =
                ImGui.GetMousePos();

            var favouriteHovered =
                ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(origin, origin + size) && mouse.X >= favouriteMin.X &&
                mouse.X <= favouriteMax.X &&
                mouse.Y >= favouriteMin.Y &&
                mouse.Y <= favouriteMax.Y;

            // Small dark floating button.
            drawList.AddCircleFilled(
                favouriteMin +
                new Vector2(
                    favouriteButtonSize * 0.5f,
                    favouriteButtonSize * 0.5f),
                favouriteButtonSize * 0.5f,
                ImGui.GetColorU32(
                    favouriteHovered
                        ? new Vector4(
                            0.10f,
                            0.12f,
                            0.17f,
                            0.96f)
                        : new Vector4(
                            0.05f,
                            0.06f,
                            0.09f,
                            0.86f)));

            // Subtle border.
            drawList.AddCircle(
                favouriteMin +
                new Vector2(
                    favouriteButtonSize * 0.5f,
                    favouriteButtonSize * 0.5f),
                favouriteButtonSize * 0.5f,
                ImGui.GetColorU32(
                    favouriteHovered
                        ? AccentHover
                        : new Vector4(
                            1f,
                            1f,
                            1f,
                            0.28f)),
                24,
                1f);

            // Heart icon.
            var favouriteIcon =
                isFavourite
                    ? FontAwesomeIcon.Heart
                    : FontAwesomeIcon.Heart;

            var favouriteGlyph =
                favouriteIcon.ToIconString();

            Vector2 favouriteGlyphSize;

            using (UiFonts.PushIcon())
            {
                favouriteGlyphSize =
                    ImGui.CalcTextSize(favouriteGlyph);

                UiFonts.DrawText(drawList, ImGui.GetFontSize(), favouriteMin +
                    new Vector2(
                        (favouriteButtonSize - favouriteGlyphSize.X) * 0.5f,
                        (favouriteButtonSize - favouriteGlyphSize.Y) * 0.5f), ImGui.GetColorU32(
                        isFavourite
                            ? AccentHover
                            : favouriteHovered
                                ? Vector4.One
                                : new Vector4(
                                    1f,
                                    1f,
                                    1f,
                                    0.82f)), favouriteGlyph);
            }

            if (favouriteHovered)
            {
                ImGui.SetMouseCursor(
                    ImGuiMouseCursor.Hand);
                UiFonts.SetTooltip(isFavourite ? "Unlike Video" : "Like Video");
            }

            var buttonGap = Ui(6f);
            var buttonHeight = Ui(28f);

            var availableButtonWidth =
                MathF.Max(width - 16f, 80f);

            var playWidth =
                availableButtonWidth * 0.48f;

            var queueWidth =
                availableButtonWidth -
                playWidth -
                buttonGap;

            var buttonY =
                origin.Y +
                thumbnailHeight -
                buttonHeight -
                8f;

            var playMin =
                new Vector2(
                    origin.X + Ui(8f),
                    buttonY);

            var playMax =
                playMin +
                new Vector2(
                    playWidth,
                    buttonHeight);

            var queueMin =
                new Vector2(
                    playMax.X + buttonGap,
                    buttonY);

            var queueMax =
                queueMin +
                new Vector2(
                    queueWidth,
                    buttonHeight);

            var playHovered =
                ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(origin, origin + size) && mouse.X >= playMin.X &&
                mouse.X <= playMax.X &&
                mouse.Y >= playMin.Y &&
                mouse.Y <= playMax.Y;

            var queueHovered =
                ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(origin, origin + size) && mouse.X >= queueMin.X &&
                mouse.X <= queueMax.X &&
                mouse.Y >= queueMin.Y &&
                mouse.Y <= queueMax.Y;

            // -----------------------------------------------------
            // Play
            // -----------------------------------------------------

            drawList.AddRectFilled(
                playMin,
                playMax,
                ImGui.GetColorU32(
                    playHovered
                        ? AccentHover
                        : Accent),
                6f);

            const string playLabel = "Play";

            var playLabelSize =
                ImGui.CalcTextSize(playLabel);

            Vector2 playGlyphSize;

            using (UiFonts.PushIcon())
            {
                playGlyphSize =
                    ImGui.CalcTextSize(
                        FontAwesomeIcon.Play.ToIconString());
            }

            var playTotalWidth =
                playGlyphSize.X +
                6f +
                playLabelSize.X;

            var playStartX =
                playMin.X +
                (playWidth - playTotalWidth) * 0.5f;

            using (UiFonts.PushIcon())
            {
                UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                        playStartX,
                        playMin.Y +
                        (buttonHeight - playGlyphSize.Y) * 0.5f), ImGui.GetColorU32(Vector4.One), FontAwesomeIcon.Play.ToIconString());
            }

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                    playStartX +
                    playGlyphSize.X +
                    Ui(6f),
                    playMin.Y +
                    (buttonHeight - playLabelSize.Y) * 0.5f), ImGui.GetColorU32(Vector4.One), playLabel);

            // -----------------------------------------------------
            // Queue
            // -----------------------------------------------------

            drawList.AddRectFilled(
                queueMin,
                queueMax,
                ImGui.GetColorU32(
                    queueHovered
                        ? CardBgHover
                        : CardBg),
                6f);

            drawList.AddRect(
                queueMin,
                queueMax,
                ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.25f)),
                6f,
                ImDrawFlags.None,
                1f);

            const string queueLabel = "Queue";

            var queueLabelSize =
                ImGui.CalcTextSize(queueLabel);

            Vector2 queueGlyphSize;

            using (UiFonts.PushIcon())
            {
                queueGlyphSize =
                    ImGui.CalcTextSize(
                        FontAwesomeIcon.Plus.ToIconString());
            }

            var queueTotalWidth =
                queueGlyphSize.X +
                6f +
                queueLabelSize.X;

            var queueStartX =
                queueMin.X +
                (queueWidth - queueTotalWidth) * 0.5f;

            using (UiFonts.PushIcon())
            {
                UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                        queueStartX,
                        queueMin.Y +
                        (buttonHeight - queueGlyphSize.Y) * 0.5f), ImGui.GetColorU32(Vector4.One), FontAwesomeIcon.Plus.ToIconString());
            }

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                    queueStartX +
                    queueGlyphSize.X +
                    Ui(6f),
                    queueMin.Y +
                    (buttonHeight - queueLabelSize.Y) * 0.5f), ImGui.GetColorU32(Vector4.One), queueLabel);

            // -----------------------------------------------------
            // Manual click handling
            // -----------------------------------------------------

            if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                // Favourite
                if (favouriteHovered)
                {
                    actionClicked = true;

                    if (favouriteVideoId is not null)
                    {
                        var isNowFavourite =
         !isFavourite;

                        if (isFavourite)
                        {
                            Plugin.Cfg.FavouriteYouTubeVideoIds.RemoveAll(
                                id =>
                                    string.Equals(
                                        id,
                                        favouriteVideoId,
                                        StringComparison.OrdinalIgnoreCase));
                        }
                        else
                        {
                            Plugin.Cfg.FavouriteYouTubeVideoIds.RemoveAll(
                                id =>
                                    string.Equals(
                                        id,
                                        favouriteVideoId,
                                        StringComparison.OrdinalIgnoreCase));

                            Plugin.Cfg.FavouriteYouTubeVideoIds.Insert(
                                0,
                                favouriteVideoId);
                        }

                        UpdateFavouriteVideoCacheAfterToggle(
                            result,
                            isNowFavourite);
                    }
                }

                // Play
                else if (playHovered)
                {
                    actionClicked = true;

                    HandlePlayNow(
                        new VideoQueueEntry(
                            result.Url,
                            result.Title,
                            result.ChannelName,
                            result.Duration,
                            result.ThumbnailUrl));
                }

                // Queue
                else if (queueHovered)
                {
                    actionClicked = true;

                    OpenYouTubeCardMenu(result, queueMin);
                }
            }
        }

        if (hasFooter)
        {
            using var previewAlpha = ImRaii.PushStyle(ImGuiStyleVar.DisabledAlpha,
                sizingPreview ? 1f : ImGui.GetStyle().DisabledAlpha);
            using var previewDisabled = UiDisabled.Scope(sizingPreview);
            actionClicked |= DrawYouTubeCardFooter(result, origin, width, height);
        }
        if ((browseLayout) &&
     showBrowseFavouriteAction &&
     favouriteVideoId is not null)
        {
            var favouriteButtonSize =
                Ui(28f);

            var favouriteMin =
                new Vector2(
                    origin.X +
                    width -
                    favouriteButtonSize -
                    Ui(7f),
                    origin.Y +
                    Ui(7f));

            var favouriteMax =
                favouriteMin +
                new Vector2(
                    favouriteButtonSize,
                    favouriteButtonSize);

            var mouse =
                ImGui.GetMousePos();

            var browseFavouriteHovered =
                !sizingPreview && ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(origin, origin + size) && mouse.X >=
                favouriteMin.X &&
                mouse.X <=
                favouriteMax.X &&
                mouse.Y >=
                favouriteMin.Y &&
                mouse.Y <=
                favouriteMax.Y;

            drawList.AddCircleFilled(
                favouriteMin +
                new Vector2(
                    favouriteButtonSize *
                    0.5f,
                    favouriteButtonSize *
                    0.5f),
                favouriteButtonSize *
                0.5f,
                ImGui.GetColorU32(
                    browseFavouriteHovered
                        ? new Vector4(
                            0.12f,
                            0.14f,
                            0.20f,
                            0.98f)
                        : new Vector4(
                            0.04f,
                            0.05f,
                            0.08f,
                            0.88f)));

            var heartGlyph =
                FontAwesomeIcon.Heart.ToIconString();

            Vector2 heartSize;

            using (
                UiFonts.PushIcon())
            {
                heartSize =
                    ImGui.CalcTextSize(
                        heartGlyph);

                UiFonts.DrawText(drawList, ImGui.GetFontSize(), favouriteMin +
                    new Vector2(
                        (favouriteButtonSize -
                         heartSize.X) *
                        0.5f,
                        (favouriteButtonSize -
                         heartSize.Y) *
                        0.5f), ImGui.GetColorU32(
                        isFavourite
                            ? AccentHover
                            : Vector4.One), heartGlyph);
            }

            if (browseFavouriteHovered)
            {
                ImGui.SetMouseCursor(
                    ImGuiMouseCursor.Hand);

                UiFonts.SetTooltip(
                    isFavourite
                        ? "Unlike Video"
                        : "Like Video");

                if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(
                        ImGuiMouseButton.Left))
                {
                    actionClicked =
                        true;

                    var isNowFavourite =
                        !isFavourite;

                    Plugin.Cfg.FavouriteYouTubeVideoIds.RemoveAll(
                        id =>
                            string.Equals(
                                id,
                                favouriteVideoId,
                                StringComparison.OrdinalIgnoreCase));

                    if (isNowFavourite)
                    {
                        Plugin.Cfg.FavouriteYouTubeVideoIds.Insert(
                            0,
                            favouriteVideoId);
                    }

                    UpdateFavouriteVideoCacheAfterToggle(
                        result,
                        isNowFavourite);
                }
            }
        }

        // ---------------------------------------------------------
        // Clicking elsewhere on the card = Play
        // ---------------------------------------------------------

        if (!actionClicked &&
            !subscribeHovered &&
            !channelHovered &&
            hovered &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            var mouse =
                ImGui.GetMousePos();

            // Don't treat the bottom overlay-control strip as
            // a generic card click.
            var actionAreaTop =
                origin.Y +
                thumbnailHeight -
                36f;

            if (mouse.Y < actionAreaTop ||
                mouse.Y > origin.Y + thumbnailHeight)
            {
                HandlePlayNow(
                    new VideoQueueEntry(
                        result.Url,
                        result.Title,
                        result.ChannelName,
                        result.Duration,
                        result.ThumbnailUrl));
            }
        }
        // Restore the card as the last layout item: footer widgets must not
        // move the next SameLine card down to the footer's row.
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(size);
    }



    private static string TruncateHomeMediaText(
    string text,
    int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            text.Length <= maxLength)
        {
            return text;
        }

        return text[..Math.Max(0, maxLength - 1)] + "…";
    }

    private void RefreshHomeWatchPartiesIfNeeded()
    {
        var now = ImGui.GetTime();
        if (now - homeWatchPartyFetchedAt < 5d)
        {
            return;
        }

        var session = CurrentSession;
        var token = session?.Token;
        if (string.IsNullOrEmpty(token))
        {
            homeWatchPartyRooms = [];
            homeWatchPartyFetchedAt = now;
            return;
        }

        if (homeWatchPartyLoading)
        {
            return;
        }

        homeWatchPartyFetchedAt = now;
        homeWatchPartyLoading = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var rooms = await roomsClient.ListAsync(token).ConfigureAwait(false);
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (Volatile.Read(ref disposeStarted) != 0 || !ReferenceEquals(CurrentSession, session)) return;
                    homeWatchPartyRooms = rooms;
                    if (roomsClient.LastFailure is null)
                        ObservePatreonPeople(rooms.Select(room => room.HostAccountId));
                }).ConfigureAwait(false);
            }
            finally
            {
                homeWatchPartyLoading = false;
            }
        });
    }

    private void OpenWatchPartyPage()
    {
        currentPage =
            HomePage.PartyDirectory;
    }

    private void JoinHomeWatchParty(
    RoomDirectoryDto room)
    {
        if(!EnsureWatchPartyJoinAllowed()) return;
        //
        // Home Watch Party cards should behave like Party Directory
        // results and take the user directly to the Watch Party page.
        //
        currentPage =
            HomePage.WatchAlong;

        if (room.Kind ==
            RoomKind.Locked)
        {
            //
            // Use the same global password prompt as the Party
            // Directory instead of navigating to the manual join form.
            //
            partyDirectoryPasswordRoom =
                room;

            partyDirectoryPassword =
                string.Empty;

            partyDirectoryPasswordError =
                null;

            partyDirectoryPasswordPopupRequested =
                true;

            return;
        }

        DoJoin(
            room.HostDisplayName);
    }

    private enum WatchPartyCategory
    {
        Unknown,
        YouTube,
        Movies,
        Tv,
        Twitch,
        Cartoons,
        LiveStream,
        Gaming,
        Dj,
        Music,
        Images,
        Promotional,
        Anime,
        Horror,
        Halloween,
        WebBrowsing,
        Marathon,
        Social,
        Cinema,
        Club,
    }

    private static bool TryReadWatchPartyMetadata(
        RoomDirectoryDto room,
        out WatchPartyCategory category,
        out bool adultOnly,
        out string serverName,
        out string visibleDescription)
    {
        category =
            WatchPartyCategory.Unknown;

        adultOnly =
            false;

        serverName =
            string.Empty;

        var rawDescription =
            room.Description ??
            string.Empty;

        visibleDescription =
            rawDescription.Trim();

        var tokenStart =
            rawDescription.IndexOf(
                "<#",
                StringComparison.Ordinal);

        if (tokenStart < 0)
        {
            return false;
        }

        var tokenEnd =
            rawDescription.IndexOf(
                "#>",
                tokenStart + 2,
                StringComparison.Ordinal);

        if (tokenEnd < 0)
        {
            return false;
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

        // Existing rooms use:
        // <#YouTube#0#>
        //
        // New rooms use:
        // <#YouTube#0#Balmung#>
        if (tokenParts.Length < 2)
        {
            return false;
        }

        category =
            tokenParts[0]
                .Trim()
                .ToUpperInvariant() switch
            {
                "YOUTUBE" =>
                    WatchPartyCategory.YouTube,

                "MOVIES" =>
                    WatchPartyCategory.Movies,

                "TV" =>
                    WatchPartyCategory.Tv,

                "TWITCH" =>
                    WatchPartyCategory.Twitch,

                "CARTOONS" =>
                    WatchPartyCategory.Cartoons,

                "LIVE STREAM" =>
                    WatchPartyCategory.LiveStream,

                "LIVESTREAM" =>
                    WatchPartyCategory.LiveStream,

                "GAMING" =>
                    WatchPartyCategory.Gaming,

                "DJ" =>
                    WatchPartyCategory.Dj,

                "MUSIC" =>
                    WatchPartyCategory.Music,

                "IMAGES" =>
                    WatchPartyCategory.Images,

                "PROMOTIONAL" =>
                    WatchPartyCategory.Promotional,

                "ANIME" =>
                    WatchPartyCategory.Anime,

                "HORROR" =>
                    WatchPartyCategory.Horror,

                "HALLOWEEN" =>
                    WatchPartyCategory.Halloween,

                "WEB BROWSING" =>
                    WatchPartyCategory.WebBrowsing,

                "WEBBROWSING" =>
                    WatchPartyCategory.WebBrowsing,

                "MARATHON" =>
                    WatchPartyCategory.Marathon,

                "SOCIAL" =>
                    WatchPartyCategory.Social,

                "CINEMA" =>
                    WatchPartyCategory.Cinema,

                "CLUB" =>
                    WatchPartyCategory.Club,

                _ =>
                    WatchPartyCategory.Unknown,
            };

        if (category ==
            WatchPartyCategory.Unknown)
        {
            return false;
        }

        var ratingText =
            tokenParts[1].Trim();

        if (ratingText != "0" &&
            ratingText != "1")
        {
            category =
                WatchPartyCategory.Unknown;

            return false;
        }

        adultOnly =
            ratingText == "1";

        if (tokenParts.Length >= 3)
        {
            serverName =
                tokenParts[2].Trim();
        }

        // Hide the entire metadata marker from the visible description.
        visibleDescription =
            (
                rawDescription[..tokenStart] +
                rawDescription[(tokenEnd + 2)..]
            ).Trim();

        return true;
    }

    private static string WatchPartyDescription(
        RoomDirectoryDto room)
    {
        TryReadWatchPartyMetadata(
            room,
            out _,
            out _,
            out _,
            out var visibleDescription);

        if (!string.IsNullOrWhiteSpace(
                visibleDescription))
        {
            return visibleDescription;
        }

        return "No description set for this Watch Party.";
    }

    private static WatchPartyCategory GetWatchPartyCategory(
        RoomDirectoryDto room)
    {
        TryReadWatchPartyMetadata(
            room,
            out var category,
            out _,
            out _,
            out _);

        return category;
    }

    private static bool WatchPartyIsAdultOnly(
        RoomDirectoryDto room)
    {
        return
            TryReadWatchPartyMetadata(
                room,
                out _,
                out var adultOnly,
                out _,
                out _) &&
            adultOnly;
    }

    private static string WatchPartyServer(
        RoomDirectoryDto room)
    {
        TryReadWatchPartyMetadata(
            room,
            out _,
            out _,
            out var serverName,
            out _);

        return serverName;
    }

    private static string WatchPartyLocation(
        RoomDirectoryDto room)
    {
        return !string.IsNullOrWhiteSpace(
                room.Location)
            ? room.Location!
            : "Location not specified";
    }
    private static string WatchPartyVisibilityText(RoomKind kind) =>
        kind switch
        {
            RoomKind.Locked => "LOCKED",
            RoomKind.Venue => "VENUE",
            _ => "PUBLIC",
        };

    private static FontAwesomeIcon WatchPartyVisibilityIcon(RoomKind kind) =>
        kind switch
        {
            RoomKind.Locked => FontAwesomeIcon.Lock,
            RoomKind.Venue => FontAwesomeIcon.Video,
            _ => FontAwesomeIcon.Globe,
        };

    private static Vector4 WatchPartyVisibilityColor(RoomKind kind) =>
        kind switch
        {
            RoomKind.Locked => new Vector4(
                1.00f,
                0.35f,
                0.20f,
                1f),

            RoomKind.Venue => new Vector4(
                1.00f,
                0.72f,
                0.20f,
                1f),

            _ => new Vector4(
                0.35f,
                0.90f,
                0.48f,
                1f),
        };

    private static string WatchPartyPlaybackStateText(
        RoomDirectoryDto room)
    {
        if (!room.HasMedia)
        {
            return "WAITING";
        }

        return room.Paused
            ? "PAUSED"
            : "PLAYING";
    }

    private static Vector4 WatchPartyPlaybackStateColor(
        RoomDirectoryDto room)
    {
        if (!room.HasMedia)
        {
            return new Vector4(
                0.48f,
                0.70f,
                1.00f,
                1f);
        }

        if (room.Paused)
        {
            return new Vector4(
                1.00f,
                0.72f,
                0.20f,
                1f);
        }

        return new Vector4(
            0.35f,
            0.90f,
            0.48f,
            1f);
    }

    private static FontAwesomeIcon WatchPartyCategoryIcon(
       RoomDirectoryDto room)
    {
        return GetWatchPartyCategory(
            room) switch
        {
            WatchPartyCategory.YouTube =>
                FontAwesomeIcon.PlayCircle,

            WatchPartyCategory.Movies =>
                FontAwesomeIcon.Film,

            WatchPartyCategory.Tv =>
                FontAwesomeIcon.Tv,

            WatchPartyCategory.Twitch =>
                FontAwesomeIcon.CommentDots,

            WatchPartyCategory.Cartoons =>
                FontAwesomeIcon.Smile,

            WatchPartyCategory.LiveStream =>
                FontAwesomeIcon.BroadcastTower,

            WatchPartyCategory.Gaming =>
                FontAwesomeIcon.Gamepad,

            WatchPartyCategory.Dj =>
                FontAwesomeIcon.Headphones,

            WatchPartyCategory.Music =>
                FontAwesomeIcon.Music,

            WatchPartyCategory.Images =>
                FontAwesomeIcon.Images,

            WatchPartyCategory.Promotional =>
                FontAwesomeIcon.Bullhorn,

            WatchPartyCategory.Anime =>
                FontAwesomeIcon.Dragon,

            WatchPartyCategory.Horror =>
                FontAwesomeIcon.Ghost,

            WatchPartyCategory.Halloween =>
                FontAwesomeIcon.Ghost,

            WatchPartyCategory.WebBrowsing =>
                FontAwesomeIcon.Globe,

            WatchPartyCategory.Marathon =>
                FontAwesomeIcon.Forward,

            WatchPartyCategory.Social =>
                FontAwesomeIcon.UserFriends,

            WatchPartyCategory.Cinema =>
                FontAwesomeIcon.TicketAlt,

            WatchPartyCategory.Club =>
                FontAwesomeIcon.GlassCheers,

            _ =>
                FontAwesomeIcon.Video,
        };
    }

    private static string WatchPartyCategoryText(
        RoomDirectoryDto room)
    {
        return GetWatchPartyCategory(
            room) switch
        {
            WatchPartyCategory.YouTube =>
                "YOUTUBE",

            WatchPartyCategory.Movies =>
                "MOVIES",

            WatchPartyCategory.Tv =>
                "TV",

            WatchPartyCategory.Twitch =>
                "TWITCH",

            WatchPartyCategory.Cartoons =>
                "CARTOONS",

            WatchPartyCategory.LiveStream =>
                "LIVE STREAM",

            WatchPartyCategory.Gaming =>
                "GAMING",

            WatchPartyCategory.Dj =>
                "DJ",

            WatchPartyCategory.Music =>
                "MUSIC",

            WatchPartyCategory.Images =>
                "IMAGES",

            WatchPartyCategory.Promotional =>
                "PROMOTIONAL",

            WatchPartyCategory.Anime =>
                "ANIME",

            WatchPartyCategory.Horror =>
                "HORROR",

            WatchPartyCategory.Halloween =>
                "HALLOWEEN",

            WatchPartyCategory.WebBrowsing =>
                "WEB BROWSING",

            WatchPartyCategory.Marathon =>
                "MARATHON",

            WatchPartyCategory.Social =>
                "SOCIAL",

            WatchPartyCategory.Cinema =>
                "CINEMA",

            WatchPartyCategory.Club =>
                "CLUB",

            _ =>
                "MEDIA",
        };
    }

    private RoomDirectoryDto[] GetRankedHomeWatchPartyRooms()
    {
        //
        // Discovery priority:
        //
        // 1. Public + Venue rooms.
        // 2. Highest viewer count first.
        // 3. Locked rooms fill remaining slots.
        //
        // A large locked room therefore does not displace an available
        // Public or Venue room.
        //
        return homeWatchPartyRooms
            .OrderBy(
                room =>
                    room.Kind == RoomKind.Locked
                        ? 1
                        : 0)
            .ThenByDescending(
                room => room.ViewerCount)
            .ThenBy(
                room => room.HostDisplayName,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private float DrawWatchPartyTag(
       ImDrawListPtr drawList,
       Vector2 origin,
       FontAwesomeIcon icon,
       string text,
       Vector4 color)
    {
        var glyph =
            icon.ToIconString();

        const float iconScale =
            0.82f;

        Vector2 glyphSize;

        using (UiFonts.PushIcon())
        {
            using var uiFontScale192004=UiFonts.PushRelative(iconScale);

            glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            uiFontScale192004.Dispose();
        }

        var textSize =
            ImGui.CalcTextSize(
                text);

        var height =
            Ui(22f);

        var width =
            Ui(7f) +
            glyphSize.X +
            Ui(5f) +
            textSize.X +
            Ui(8f);

        var max =
            origin +
            new Vector2(
                width,
                height);

        drawList.AddRectFilled(
            origin,
            max,
            ImGui.GetColorU32(
                new Vector4(
                    color.X,
                    color.Y,
                    color.Z,
                    0.09f)),
            Ui(5f));

        drawList.AddRect(
            origin,
            max,
            ImGui.GetColorU32(
                new Vector4(
                    color.X,
                    color.Y,
                    color.Z,
                    0.72f)),
            Ui(5f));

        using (UiFonts.PushIcon())
        {
            using var uiFontScale193245=UiFonts.PushRelative(iconScale);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                    origin.X +
                    Ui(7f),
                    origin.Y +
                    (height -
                     glyphSize.Y) *
                    0.5f), ImGui.GetColorU32(
                    color), glyph);

            uiFontScale193245.Dispose();
        }

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                origin.X +
                Ui(7f) +
                glyphSize.X +
                Ui(5f),
                origin.Y +
                (height -
                 textSize.Y) *
                0.5f), ImGui.GetColorU32(
                color), text);

        return width;
    }

    private float DrawWatchPartyAdultTag(
     ImDrawListPtr drawList,
     Vector2 origin,
     bool onThumbnail = false)
    {
        const string text =
            "18+";

        var textSize =
            ImGui.CalcTextSize(
                text);

        var color =
            new Vector4(
                1.00f,
                0.28f,
                0.32f,
                1f);

        var height =
            Ui(22f);

        var width =
            textSize.X +
            Ui(14f);

        var max =
            origin +
            new Vector2(
                width,
                height);

        drawList.AddRectFilled(
            origin,
            max,
            ImGui.GetColorU32(
                onThumbnail ? new Vector4(0.015f, 0.02f, 0.035f, 0.92f) : new Vector4(
                    color.X,
                    color.Y,
                    color.Z,
                    0.12f)),
            Ui(5f));

        drawList.AddRect(
            origin,
            max,
            ImGui.GetColorU32(
                new Vector4(
                    color.X,
                    color.Y,
                    color.Z,
                    0.82f)),
            Ui(5f));

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                origin.X +
                (width -
                 textSize.X) *
                0.5f,
                origin.Y +
                (height -
                 textSize.Y) *
                0.5f), ImGui.GetColorU32(
                color), text);

        return width;
    }

    private static string FormatViewCount(
    long views)
    {
        if (views >= 1_000_000_000)
        {
            return $"{views / 1_000_000_000d:0.#}B views";
        }

        if (views >= 1_000_000)
        {
            return $"{views / 1_000_000d:0.#}M views";
        }

        if (views >= 1_000)
        {
            return $"{views / 1_000d:0.#}K views";
        }

        return $"{views:N0} views";
    }

    private static string FormatRelativeUploadDate(
    DateTimeOffset uploadDate)
    {
        var age =
            DateTimeOffset.UtcNow -
            uploadDate.ToUniversalTime();

        if (age.TotalDays < 1)
        {
            var hours =
                Math.Max(
                    1,
                    (int)age.TotalHours);

            return hours == 1
                ? "1 hour ago"
                : $"{hours} hours ago";
        }

        if (age.TotalDays < 30)
        {
            var days =
                Math.Max(
                    1,
                    (int)age.TotalDays);

            return days == 1
                ? "1 day ago"
                : $"{days} days ago";
        }

        if (age.TotalDays < 365)
        {
            var months =
                Math.Max(
                    1,
                    (int)(age.TotalDays / 30));

            return months == 1
                ? "1 month ago"
                : $"{months} months ago";
        }

        var years =
            Math.Max(
                1,
                (int)(age.TotalDays / 365));

        return years == 1
            ? "1 year ago"
            : $"{years} years ago";
    }

    // UV crop so the image fills the box (cover) without stretching.
    private static (Vector2 Uv0, Vector2 Uv1) CoverUvs(float texW, float texH, float boxW, float boxH)
    {
        if (texW <= 0 || texH <= 0 || boxW <= 0 || boxH <= 0)
        {
            return (Vector2.Zero, Vector2.One);
        }

        var texAspect = texW / texH;
        var boxAspect = boxW / boxH;
        if (texAspect > boxAspect)
        {
            var visible = boxAspect / texAspect;
            var pad = (1f - visible) * 0.5f;
            return (new Vector2(pad, 0f), new Vector2(1f - pad, 1f));
        }

        var visibleV = texAspect / boxAspect;
        var padV = (1f - visibleV) * 0.5f;
        return (new Vector2(0f, padV), new Vector2(1f, 1f - padV));
    }

    // Word-wrap into at most maxLines; oversized unbroken tokens (common in CJK
    // titles and URLs) fall back to Unicode text-element wrapping so they can
    // never escape the card. Returns Y just below the last drawn line.
    private static float DrawWrappedLines(ImDrawListPtr drawList, Vector2 pos, float wrapWidth,
        float lineHeight, int maxLines, uint color, string text)
        => DrawWrappedLines(drawList, pos, wrapWidth, lineHeight, maxLines, color, text, out _);

    private static float DrawWrappedLines(ImDrawListPtr drawList, Vector2 pos, float wrapWidth,
        float lineHeight, int maxLines, uint color, string text, out bool wasClipped)
    {
        wasClipped = false;
        var y = pos.Y;
        if (maxLines <= 0 || string.IsNullOrWhiteSpace(text))
        {
            return y;
        }

        var wrappedLines = new List<string>();
        var line = string.Empty;

        void CommitLine()
        {
            if (line.Length == 0)
            {
                return;
            }

            wrappedLines.Add(line);
            line = string.Empty;
        }

        void PlaceWord(string word)
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (ImGui.CalcTextSize(candidate).X <= wrapWidth)
            {
                line = candidate;
                return;
            }

            CommitLine();

            if (ImGui.CalcTextSize(word).X <= wrapWidth)
            {
                line = word;
                return;
            }

            // Keep grapheme clusters intact while splitting a token that has
            // no usable word boundary within the available card width.
            var elementEnumerator = StringInfo.GetTextElementEnumerator(word);
            while (elementEnumerator.MoveNext())
            {
                var element = elementEnumerator.GetTextElement();
                var elementCandidate = line + element;
                if (line.Length > 0 && ImGui.CalcTextSize(elementCandidate).X > wrapWidth)
                {
                    CommitLine();
                    elementCandidate = element;
                }

                line = elementCandidate;
            }
        }

        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            PlaceWord(word);
        }

        CommitLine();

        wasClipped = wrappedLines.Count > maxLines;
        var visibleCount = Math.Min(wrappedLines.Count, maxLines);
        for (var index = 0; index < visibleCount; index++)
        {
            var visibleLine = wrappedLines[index];
            if (wasClipped && index == visibleCount - 1)
            {
                visibleLine = EllipsizeWrappedLine(visibleLine, wrapWidth);
            }

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(pos.X, y), color, visibleLine);
            y += lineHeight;
        }

        return y;
    }

    private static string EllipsizeWrappedLine(string text, float width)
    {
        const string ellipsis = "…";
        if (ImGui.CalcTextSize(text + ellipsis).X <= width)
        {
            return text + ellipsis;
        }

        var elements = StringInfo.ParseCombiningCharacters(text);
        for (var count = elements.Length - 1; count > 0; count--)
        {
            var candidate = text[..elements[count]] + ellipsis;
            if (ImGui.CalcTextSize(candidate).X <= width)
            {
                return candidate;
            }
        }

        return ellipsis;
    }

    private static void DrawAvatarStack(ParticipantInfo[] participants, int maxShown)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var radius = NativeUi(12f);
        var overlap = NativeUi(16f);
        var shown = Math.Min(participants.Length, maxShown);
        for (var index = 0; index < shown; index++)
        {
            var center = origin + new Vector2(radius + index * overlap, radius);
            drawList.AddCircleFilled(center, radius + 1.5f, ImGui.GetColorU32(WindowBg));
            drawList.AddCircleFilled(center, radius, ImGui.GetColorU32(AvatarPalette[index % AvatarPalette.Length]));
            DrawAvatarIcon(drawList, center, radius * 2f);
        }

        ImGui.Dummy(new Vector2(radius * 2 + Math.Max(0, shown - 1) * overlap, radius * 2));
        if (participants.Length > maxShown)
        {
            ImGui.SameLine();
            ImGui.TextColored(MutedText, $"+{participants.Length - maxShown}");
        }
    }

    private void DoJoin(
        string hostName, string? password = null, string? visibleHostName = null,
        string? inviteMessageId = null)
    {
        if(!EnsureWatchPartyJoinAllowed()) return;
        if (hostName.Length == 0)
        {
            return;
        }

        if (CurrentSession is null)
        {
            joinError = "Sign in to join a watch party.";
            return;
        }

        currentPage = HomePage.WatchAlong;
        partyPanelTab = PartyPanelTab.Chat;
        partyChatWasAtBottom = false;

        pendingWatchPartyInviteId = inviteMessageId;
        pendingWatchPartyInviteHostId = inviteMessageId is null ? null : hostName.Trim();
        pendingWatchPartyInviteStartedUtc = inviteMessageId is null ? DateTime.MinValue : DateTime.UtcNow;
        pendingWatchPartyInviteAttempt = inviteMessageId is null ? null :
            new WatchPartyInviteJoinAttempt(hostName.Trim(), visibleHostName);

        //
        // This is a new room, so its content may offer the TV prompt once.
        //
        ResetViewerTvSpawnPrompt();

        joinedHostIdentity = null;
        joinedHostDisplayName =
            string.IsNullOrWhiteSpace(visibleHostName)
                ? hostName.Trim()
                : visibleHostName.Trim();

        gameplayStreamOfferDismissed =
    false;

        _ = stream.JoinAsync(
            hostName.Trim(),
            string.IsNullOrWhiteSpace(password) ? null : password.Trim());

        if (!stream.IsConnected)
        {
            joinError = "Connecting to the relay…";
        }
    }
}

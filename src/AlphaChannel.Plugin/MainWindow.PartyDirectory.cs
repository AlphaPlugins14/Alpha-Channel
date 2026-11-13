using AlphaChannel.Plugin.Core;
using System.Collections.Concurrent;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private enum PartyDirectoryContentType
    {
        OnlineVideos,
        Twitch,
        StreamingLive,
        RadioAudio,
        DjingLive,
        ImagesSlideshows,
        LocalVideoStreams,
        RetroGamesStreams,
        Activities,
        WebBrowsing,
        Other,
    }

    private const int PartyDirectoryInitialVisibleCount =
        12;

    private const int PartyDirectoryLoadMoreCount =
        12;

    private static readonly TimeSpan PartyDirectoryRefreshInterval =
        TimeSpan.FromSeconds(15);

    private static readonly TimeSpan PartyDirectoryMetadataLifetime =
        TimeSpan.FromMinutes(5);

    private sealed record PartyDirectoryMediaInfo(
        string? Title,
        string? ThumbnailUrl,
        DateTime RefreshedAt);

    private readonly ConcurrentDictionary<
        string,
        PartyDirectoryMediaInfo> partyDirectoryMediaCache =
            new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<
        string,
        byte> partyDirectoryMediaRequestsInFlight =
            new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<RoomKind> partyDirectoryRoomKinds =
        new(Enum.GetValues<RoomKind>());

    private readonly HashSet<WatchPartyCategory> partyDirectoryCategories =
        new(
            Enum.GetValues<WatchPartyCategory>()
                .Where(
                    category =>
                        category !=
                        WatchPartyCategory.Unknown));

    private RoomDirectoryDto[] partyDirectoryRooms =
        [];
    private string? partyDirectoryAccountId;

    private string partyDirectorySearch =
        string.Empty;

    private volatile bool partyDirectoryLoading;

    private bool partyDirectoryRequested;

    private DateTime partyDirectoryRefreshedAt =
        DateTime.MinValue;

    private int partyDirectoryRatingFilter;
    private PartyDirectoryContentType? partyDirectoryContentTypeFilter;
    private string? partyDirectoryServer;

    private int partyDirectoryVisibleCount =
     PartyDirectoryInitialVisibleCount;

    //
    // The selected locked room is retained while its password prompt
    // is open. Locked-room joins are only submitted after the player
    // confirms a non-empty password.
    //
    private RoomDirectoryDto? partyDirectoryPasswordRoom;

    private string partyDirectoryPassword =
        string.Empty;

    private string? partyDirectoryPasswordError;

    private bool partyDirectoryPasswordPopupRequested;

    private bool partyDirectoryFocusSearchRequested;

    private void DrawPartyDirectoryPage()
    {
        RefreshPartyDirectoryIfNeeded();

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   Ui(8f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FramePadding,
                   UiVec(10f, 8f)))
        {
            DrawPartyDirectorySearch();

            ImGui.Dummy(
                UiVec(
                    0f,
                    10f));

            DrawPartyDirectoryResults();
        }

    }

    private void RefreshPartyDirectoryIfNeeded(
        bool force = false)
    {
        if (CurrentSession is not { } session)
        {
            partyDirectoryRooms =
                [];

            partyDirectoryLoading =
                false;

            return;
        }

        if (partyDirectoryAccountId != session.AccountId)
        {
            partyDirectoryRooms = [];
            partyDirectoryRequested = false;
        }

        var expired =
            DateTime.UtcNow -
            partyDirectoryRefreshedAt >=
            PartyDirectoryRefreshInterval;

        if (!force &&
            partyDirectoryRequested &&
            !expired)
        {
            return;
        }

        if (partyDirectoryLoading)
        {
            return;
        }

        partyDirectoryRequested =
            true;

        partyDirectoryLoading =
            true;

        var token =
            session.Token;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var rooms =
                        await roomsClient
                            .ListAsync(token)
                            .ConfigureAwait(false);

                    await Plugin.Framework.RunOnFrameworkThread(() =>
                    {
                        if (Volatile.Read(ref disposeStarted) != 0 || !ReferenceEquals(CurrentSession, session)) return;
                        partyDirectoryRooms = rooms;
                        partyDirectoryAccountId = session.AccountId;
                        partyDirectoryRefreshedAt = DateTime.UtcNow;
                        partyDirectoryVisibleCount = Math.Max(partyDirectoryVisibleCount, PartyDirectoryInitialVisibleCount);
                        if (roomsClient.LastFailure is null)
                        {
                            ObservePatreonPeople(rooms.Select(room => room.HostAccountId));
                        }
                    }).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    AepLog.Warning(
                        "[PartyDirectory] Refresh failed.");
                }
                finally
                {
                    if (Volatile.Read(ref disposeStarted) == 0)
                        await Plugin.Framework.RunOnFrameworkThread(() => partyDirectoryLoading = false).ConfigureAwait(false);
                }
            });
    }

    private void DrawPartyDirectorySearch()
    {
        if (partyDirectoryFocusSearchRequested)
        {
            ImGui.SetKeyboardFocusHere();
            partyDirectoryFocusSearchRequested = false;
        }

        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var gap =
            Ui(10f);

        var refreshSize =
            ImGui.GetFrameHeight();

        var filterCount = PartyDirectoryActiveFilterCount();
        var filterLabel = filterCount == 0 ? "Filters" : $"Filters ({filterCount})";
        var filterWidth = ImGui.CalcTextSize(filterLabel).X + Ui(56f);
        var showClearFilters = filterCount > 0;
        var clearFiltersWidth = showClearFilters ? ImGui.CalcTextSize("Clear filters").X + Ui(36f) : 0f;

        var searchWidth =
            Math.Max(
                Ui(120f),
                availableWidth -
                refreshSize -
                filterWidth - clearFiltersWidth - gap * (showClearFilters ? 3f : 2f));

        using (ImRaii.PushColor(
                   ImGuiCol.FrameBg,
                   FrameBg))
        using (ImRaii.PushColor(
                   ImGuiCol.FrameBgHovered,
                   FrameBgHover))
        using (ImRaii.PushColor(
                   ImGuiCol.FrameBgActive,
                   AccentFieldActive))
        {
            ImGui.SetNextItemWidth(
                searchWidth);

            if (ImGui.InputTextWithHint(
                    "##partyDirectorySearch",
                    "Search hosts, rooms, locations, categories or content",
                    ref partyDirectorySearch,
                    120))
            {
                partyDirectoryVisibleCount =
                    PartyDirectoryInitialVisibleCount;
            }
        }

        ImGui.SameLine(
            0f,
            gap);

        if (showClearFilters)
        {
            if (DrawDirectoryToolbarButton("##clearDirectoryFilters", "Clear filters",
                    FontAwesomeIcon.Times, clearFiltersWidth, refreshSize))
                ResetPartyDirectoryFilters();
            ImGui.SameLine(0f, gap);
        }
        DrawPartyDirectoryFilterButton(filterLabel, filterWidth, refreshSize);
        ImGui.SameLine(0f, gap);

        var refreshClicked =
            false;

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
                       0.28f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonActive,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.42f)))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.58f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   Ui(1f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, Vector2.Zero))
        using (ImRaii.PushStyle(ImGuiStyleVar.ButtonTextAlign, new Vector2(0.5f, 0.5f)))
        using (UiFonts.PushIcon())
        {
            refreshClicked =
                ImGui.Button(
                    FontAwesomeIcon
                        .SyncAlt
                        .ToIconString() +
                    "##refreshPartyDirectory",
                    new Vector2(
                        refreshSize,
                        refreshSize));
        }

        if (ImGui.IsItemHovered())
        {
            UiFonts.SetTooltip(
                "Refresh Watch Parties");
        }

        if (refreshClicked)
        {
            RefreshPartyDirectoryIfNeeded(
                force: true);
        }
    }

    private void DrawPartyDirectoryRoomTypeFilter(
        float width)
    {
        var preview =
            partyDirectoryRoomKinds.Count switch
            {
                3 =>
                    "Room type: All",

                0 =>
                    "Room type: None",

                1 =>
                    $"Room type: {PartyDirectoryRoomKindText(
                        partyDirectoryRoomKinds.First())}",

                _ =>
                    $"Room type: {partyDirectoryRoomKinds.Count} selected",
            };

        ImGui.SetNextItemWidth(
            width);

        if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo(
                "##partyDirectoryRoomTypes",
                preview)))
        {
            return;
        }

        foreach (var kind in
                 Enum.GetValues<RoomKind>())
        {
            var selected =
                partyDirectoryRoomKinds.Contains(
                    kind);

            if (ImGui.Checkbox(
                    $"{PartyDirectoryRoomKindText(kind)}##directoryKind_{kind}",
                    ref selected))
            {
                if (selected)
                {
                    partyDirectoryRoomKinds.Add(
                        kind);
                }
                else
                {
                    partyDirectoryRoomKinds.Remove(
                        kind);
                }

                partyDirectoryVisibleCount =
                    PartyDirectoryInitialVisibleCount;
            }
        }

        ImGui.EndCombo();
    }

    private void DrawPartyDirectoryCategoryFilter(
        float width)
    {
        var availableCategoryCount =
            Enum.GetValues<WatchPartyCategory>()
                .Count(
                    category =>
                        category !=
                        WatchPartyCategory.Unknown);

        var preview =
            partyDirectoryCategories.Count switch
            {
                0 =>
                    "Categories: None",

                var count when
                    count ==
                    availableCategoryCount =>
                    "Categories: All",

                1 =>
                    $"Category: {PartyDirectoryCategoryName(
                        partyDirectoryCategories.First())}",

                _ =>
                    $"Categories: {partyDirectoryCategories.Count} selected",
            };

        ImGui.SetNextItemWidth(
            width);

        if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo(
                "##partyDirectoryCategories",
                preview)))
        {
            return;
        }

        foreach (var category in
                 Enum.GetValues<WatchPartyCategory>()
                     .OrderBy(category => category == WatchPartyCategory.Halloween ? 0 : 1))
        {
            if (category ==
                WatchPartyCategory.Unknown)
            {
                continue;
            }

            var selected =
                partyDirectoryCategories.Contains(
                    category);

            if (ImGui.Checkbox(
                    $"{PartyDirectoryCategoryName(category)}##directoryCategory_{category}",
                    ref selected))
            {
                if (selected)
                {
                    partyDirectoryCategories.Add(
                        category);
                }
                else
                {
                    partyDirectoryCategories.Remove(
                        category);
                }

                partyDirectoryVisibleCount =
                    PartyDirectoryInitialVisibleCount;
            }
        }

        ImGui.EndCombo();
    }

    private static string PartyDirectoryContentTypeName(PartyDirectoryContentType type) => type switch
    {
        PartyDirectoryContentType.OnlineVideos => "Online Videos",
        PartyDirectoryContentType.Twitch => "Twitch",
        PartyDirectoryContentType.StreamingLive => "Streaming Live",
        PartyDirectoryContentType.RadioAudio => "Radio / Audio",
        PartyDirectoryContentType.DjingLive => "DJing Live",
        PartyDirectoryContentType.ImagesSlideshows => "Images / Slideshows",
        PartyDirectoryContentType.LocalVideoStreams => "Local Video Streams",
        PartyDirectoryContentType.RetroGamesStreams => "Retro Games Streams",
        PartyDirectoryContentType.Activities => "Activities",
        PartyDirectoryContentType.WebBrowsing => "Web Browsing",
        _ => "Other",
    };

    private void DrawPartyDirectoryContentTypeFilter(float width)
    {
        ImGui.SetNextItemWidth(width);
        var preview = partyDirectoryContentTypeFilter is { } selected
            ? $"Content type: {PartyDirectoryContentTypeName(selected)}"
            : "Content type: All";
        if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##partyDirectoryContentType", preview))) return;

        if (ImGui.Selectable("All", partyDirectoryContentTypeFilter is null))
        {
            partyDirectoryContentTypeFilter = null;
            partyDirectoryVisibleCount = PartyDirectoryInitialVisibleCount;
        }

        foreach (var type in Enum.GetValues<PartyDirectoryContentType>())
        {
            if (!ImGui.Selectable(PartyDirectoryContentTypeName(type),
                    partyDirectoryContentTypeFilter == type)) continue;
            partyDirectoryContentTypeFilter = type;
            partyDirectoryVisibleCount = PartyDirectoryInitialVisibleCount;
        }

        ImGui.EndCombo();
    }

    private void DrawPartyDirectoryRatingFilter(
        float width)
    {
        var preview =
            partyDirectoryRatingFilter switch
            {
                1 =>
                    "Rating: Not 18+",

                2 =>
                    "Rating: 18+ only",

                _ =>
                    "Rating: All",
            };

        ImGui.SetNextItemWidth(
            width);

        if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo(
                "##partyDirectoryRating",
                preview)))
        {
            return;
        }

        if (ImGui.Selectable(
                "All",
                partyDirectoryRatingFilter == 0))
        {
            partyDirectoryRatingFilter =
          0;

            partyDirectoryServer =
                null;

            partyDirectoryVisibleCount =
                PartyDirectoryInitialVisibleCount;
        }

        if (ImGui.Selectable(
                "Not 18+",
                partyDirectoryRatingFilter == 1))
        {
            partyDirectoryRatingFilter =
                1;

            partyDirectoryVisibleCount =
                PartyDirectoryInitialVisibleCount;
        }

        if (ImGui.Selectable(
                "18+ only",
                partyDirectoryRatingFilter == 2))
        {
            partyDirectoryRatingFilter =
                2;

            partyDirectoryVisibleCount =
                PartyDirectoryInitialVisibleCount;
        }

        ImGui.EndCombo();
    }

    private void DrawPartyDirectoryServerPlaceholder(
    float width)
    {
        var servers =
            partyDirectoryRooms
                .Select(
                    WatchPartyServer)
                .Where(
                    server =>
                        !string.IsNullOrWhiteSpace(
                            server))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    server =>
                        server,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        ImGui.SetNextItemWidth(
            width);

        var preview =
            string.IsNullOrWhiteSpace(
                partyDirectoryServer)
                ? "Server: All servers"
                : $"Server: {partyDirectoryServer}";

        if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo(
                "##partyDirectoryServer",
                preview)))
        {
            return;
        }

        var allSelected =
            string.IsNullOrWhiteSpace(
                partyDirectoryServer);

        if (ImGui.Selectable(
                "All servers",
                allSelected))
        {
            partyDirectoryServer =
                null;

            partyDirectoryVisibleCount =
                PartyDirectoryInitialVisibleCount;
        }

        foreach (var server in servers)
        {
            var selected =
                string.Equals(
                    partyDirectoryServer,
                    server,
                    StringComparison.OrdinalIgnoreCase);

            if (ImGui.Selectable(
                    server,
                    selected))
            {
                partyDirectoryServer =
                    server;

                partyDirectoryVisibleCount =
                    PartyDirectoryInitialVisibleCount;
            }
        }

        ImGui.EndCombo();
    }
    private void ResetPartyDirectoryFilters()
    {
        partyDirectorySearch =
            string.Empty;

        partyDirectoryRoomKinds.Clear();

        foreach (var kind in
                 Enum.GetValues<RoomKind>())
        {
            partyDirectoryRoomKinds.Add(
                kind);
        }

        partyDirectoryCategories.Clear();

        foreach (var category in
                 Enum.GetValues<WatchPartyCategory>())
        {
            if (category !=
                WatchPartyCategory.Unknown)
            {
                partyDirectoryCategories.Add(
                    category);
            }
        }

        partyDirectoryRatingFilter =
            0;

        partyDirectoryContentTypeFilter = null;

        partyDirectoryServer = null;
        partyDirectoryVibeFilters.Clear();

        partyDirectoryVisibleCount =
            PartyDirectoryInitialVisibleCount;
    }

    private void DrawPartyDirectoryResults()
    {
        var headerOrigin = ImGui.GetCursorScreenPos();
        var headerWidth = ImGui.GetContentRegionAvail().X;
        var filteredRooms =
            GetFilteredPartyDirectoryRooms();

        var totalCount =
            filteredRooms.Length;

        var displayedCount =
            Math.Min(
                totalCount,
                partyDirectoryVisibleCount);

        ImGui.TextColored(
            Vector4.One,
            $"{totalCount} Watch {(totalCount == 1 ? "Party" : "Parties")}");
        var countRight = ImGui.GetItemRectMax().X;

        var sortWidth = Ui(225f);

        ImGui.SameLine();

        ImGui.SetCursorPosX(
            Math.Max(
                ImGui.GetCursorPosX(),
                ImGui.GetWindowWidth() -
                sortWidth -
                Ui(18f)));

        ImGui.SetNextItemWidth(sortWidth);
        var sortOrigin = ImGui.GetCursorScreenPos();
        var sortWasFeaturedFirst = Plugin.Cfg.PartyDirectoryFeaturedFirst;
        using (ImRaii.PushColor(ImGuiCol.FrameBg, FrameBg))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(8f, 5f)))
        {
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##partyDirectorySort", Plugin.Cfg.PartyDirectoryFeaturedFirst
                    ? "Sort: Featured First" : "Sort: Number of Viewers")))
            {
                if (ImGui.Selectable("Featured First", Plugin.Cfg.PartyDirectoryFeaturedFirst))
                    SetPartyDirectorySort(true);
                if (ImGui.Selectable("Number of Viewers", !Plugin.Cfg.PartyDirectoryFeaturedFirst))
                    SetPartyDirectorySort(false);
                ImGui.EndCombo();
            }
        }
        var afterHeader = ImGui.GetCursorScreenPos();
        var filtersChanged = DrawDirectoryVibeFilterRow(headerOrigin, headerWidth, countRight, sortOrigin, afterHeader);
        if (filtersChanged || sortWasFeaturedFirst != Plugin.Cfg.PartyDirectoryFeaturedFirst)
        {
            filteredRooms = GetFilteredPartyDirectoryRooms();
            totalCount = filteredRooms.Length;
            displayedCount = Math.Min(totalCount, partyDirectoryVisibleCount);
        }

        ImGui.Dummy(
            UiVec(
                0f,
                6f));

        var resultsHeight =
            Math.Max(
                Ui(120f),
                ImGui.GetContentRegionAvail().Y);

        using (var results =
               UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                   "##partyDirectoryResults",
                   new Vector2(
                       0f,
                       resultsHeight),
                   false,
                   ImGuiWindowFlags.None)))
        {
            if (!results)
            {
                return;
            }

            if (CurrentSession is null)
            {
                DrawPartyDirectoryEmptyState(
                    FontAwesomeIcon.UserLock,
                    "Sign in to browse Watch Parties",
                    "The public directory is available after signing in.");

                return;
            }

            if (partyDirectoryLoading &&
                partyDirectoryRooms.Length == 0)
            {
                DrawPartyDirectoryEmptyState(
                    FontAwesomeIcon.SyncAlt,
                    "Loading Watch Parties",
                    "Fetching the latest public rooms and venues.");

                return;
            }

            if (totalCount == 0)
            {
                DrawPartyDirectoryEmptyState(
                    FontAwesomeIcon.Search,
                    "No Watch Parties found",
                    "Try clearing or changing your search filters.");

                return;
            }

            var rowHeight =
                Ui(180f);

            var rowGap =
                Ui(10f);

            for (var index = 0;
                 index < displayedCount;
                 index++)
            {
                var room =
                    filteredRooms[index];

                ImGui.PushID(
                    $"partyDirectoryRoom_{room.HostAccountId}_{index}");

                DrawPartyDirectoryRoom(
                    room,
                    ImGui.GetContentRegionAvail().X,
                    rowHeight);

                ImGui.PopID();

                if (index <
                    displayedCount - 1)
                {
                    ImGui.Dummy(
                        UiVec(
                            0f,
                            rowGap));
                }
            }

            ImGui.Dummy(
                UiVec(
                    0f,
                    12f));

            using (UiFonts.PushRelative(.90f))
            {
            var showingText =
                $"Showing {displayedCount} of {totalCount} {(totalCount == 1 ? "room" : "rooms")}";

            var showingWidth =
                ImGui.CalcTextSize(
                    showingText).X;

            ImGui.SetCursorPosX(
                Math.Max(
                    ImGui.GetCursorPosX(),
                    (
                        ImGui.GetWindowWidth() -
                        showingWidth
                    ) *
                    0.5f));

            ImGui.TextColored(
                MutedText,
                showingText);

            }

            if (displayedCount <
                totalCount)
            {
                ImGui.Dummy(
                    UiVec(
                        0f,
                        5f));

                var loadMoreWidth =
                    Ui(150f);

                ImGui.SetCursorPosX(
                    Math.Max(
                        ImGui.GetCursorPosX(),
                        (
                            ImGui.GetWindowWidth() -
                            loadMoreWidth
                        ) *
                        0.5f));

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
                               0.28f)))
                using (ImRaii.PushColor(
                           ImGuiCol.Border,
                           new Vector4(
                               Accent.X,
                               Accent.Y,
                               Accent.Z,
                               0.58f)))
                using (ImRaii.PushStyle(
                           ImGuiStyleVar.FrameBorderSize,
                           Ui(1f)))
                {
                    if (ImGui.Button(
                            "Load more",
                            new Vector2(
                                loadMoreWidth,
                                0f)))
                    {
                        partyDirectoryVisibleCount +=
                            PartyDirectoryLoadMoreCount;
                    }
                }
            }

            ImGui.Dummy(
                UiVec(
                    0f,
                    10f));
        }
    }

    private RoomDirectoryDto[] GetFilteredPartyDirectoryRooms()
    {
        var query =
            partyDirectorySearch.Trim();

        var filtered = partyDirectoryRooms
            .Where(room => PartyVibes.Matches(room.Description, partyDirectoryVibeFilters))
            .Where(
                room =>
                    partyDirectoryRoomKinds.Contains(
                        room.Kind))
            .Where(room => partyDirectoryContentTypeFilter is not { } selected ||
                GetPartyDirectoryContentType(room) == selected)
            .Where(
                room =>
                {
                    var category =
                        GetWatchPartyCategory(
                            room);

                    return category !=
                               WatchPartyCategory.Unknown &&
                           partyDirectoryCategories.Contains(
                               category);
                })
            .Where(
                room =>
                {
                    var adultOnly =
                        WatchPartyIsAdultOnly(
                            room);

                    return partyDirectoryRatingFilter switch
                    {
                        1 =>
                            !adultOnly,

                        2 =>
                            adultOnly,

                        _ =>
                            true,
                    };
                })
            .Where(
                room =>
                    string.IsNullOrWhiteSpace(
                        partyDirectoryServer) ||
                    string.Equals(
                        WatchPartyServer(
                            room),
                        partyDirectoryServer,
                        StringComparison.OrdinalIgnoreCase))
            .Where(
                room =>
                    string.IsNullOrWhiteSpace(
                        query) ||
                    PartyDirectoryMatchesSearch(
                        room,
                        query));

        if (Plugin.Cfg.PartyDirectoryFeaturedFirst)
            return FeaturedWatchParties.Order(filtered, CurrentSession?.AccountId, PatreonBadgeTier).ToArray();

        return filtered.OrderByDescending(
                room =>
                    room.ViewerCount + 1)
            .ThenBy(
                room =>
                    room.HasMedia
                        ? 0
                        : 1)
            .ThenBy(
                room =>
                    room.Kind ==
                    RoomKind.Locked
                        ? 1
                        : 0)
            .ThenBy(
                room =>
                    room.HostDisplayName,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool PartyDirectoryMatchesSearch(
        RoomDirectoryDto room,
        string query)
    {
        TryReadWatchPartyMetadata(
              room,
              out var category,
              out _,
              out var serverName,
              out var visibleDescription);

        var media =
            GetPartyDirectoryMedia(
                room);

        return PartyDirectoryContains(
                    room.HostDisplayName,
                    query) ||
                PartyDirectoryContains(
                    visibleDescription,
                    query) ||
                PartyDirectoryContains(
                    room.Location,
                    query) ||
                PartyDirectoryContains(
                    serverName,
                    query) ||
                PartyDirectoryContains(
                    PartyDirectoryCategoryName(
                        category),
                    query) ||
                PartyDirectoryContains(
                    PartyDirectoryRoomKindText(
                        room.Kind),
                    query) ||
                PartyDirectoryContains(
                    PartyDirectoryContentTypeName(GetPartyDirectoryContentType(room)),
                    query) ||
                PartyDirectoryContains(
                    media.Title,
                    query);
    }

    private static bool PartyDirectoryContains(
        string? value,
        string query)
    {
        return !string.IsNullOrWhiteSpace(
                   value) &&
               value.Contains(
                   query,
                   StringComparison.OrdinalIgnoreCase);
    }

    private void DrawPartyDirectoryRoom(
        RoomDirectoryDto room,
        float width,
        float height)
    {
        EnsurePartyDirectoryMediaLoaded(
            room);

        var media =
            GetPartyDirectoryMedia(
                room);

        var origin =
            ImGui.GetCursorScreenPos();

        var compactRoom = width < Ui(800f);
        var plannedThumbnailWidth = Math.Clamp(width * 0.27f,
            Ui(compactRoom ? 100f : 190f), Ui(compactRoom ? 185f : 275f));
        var roomTags = DirectoryRoomTags(room);
        var tagRows = LayoutDirectoryRoomTags(ImGui.GetWindowDrawList(), Vector2.Zero,
            width - plannedThumbnailWidth - Ui(44f), roomTags, render: false);
        height += (tagRows - 1) * Ui(28f);

        var size =
            new Vector2(
                width,
                height);

        var cardMax =
            origin +
            size;

        var drawList =
            ImGui.GetWindowDrawList();

        var cardHovered =
            ImGui.IsMouseHoveringRect(
                origin,
                cardMax);

        drawList.AddRectFilled(
            origin,
            cardMax,
            ImGui.GetColorU32(
                cardHovered
                    ? AccentPanelHover
                    : CardBg),
            Ui(11f));

        drawList.AddRect(
            origin,
            cardMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    cardHovered
                        ? 0.62f
                        : 0.34f)),
            Ui(11f),
            ImDrawFlags.None,
            Ui(1f));

        var padding =
            Ui(12f);

        var thumbnailWidth = plannedThumbnailWidth;

        var thumbnailMin =
            origin +
            new Vector2(
                padding,
                padding);

        var thumbnailSize =
            new Vector2(
                thumbnailWidth,
                height -
                padding * 2f);

        var thumbnailMax =
            thumbnailMin +
            thumbnailSize;

        DrawPartyDirectoryThumbnail(
            drawList,
            room,
            media,
            thumbnailMin,
            thumbnailSize);

        DrawPartyDirectoryPlaybackBadge(
            drawList,
            room,
            thumbnailMin +
            UiVec(
                8f,
                8f));

        DrawPartyDirectoryViewerBadge(
            drawList,
            room.ViewerCount + 1,
            new Vector2(
                thumbnailMax.X -
                Ui(8f),
                thumbnailMin.Y +
                Ui(8f)),room.HostAccountId);

        DrawPartyDirectoryCategoryBubble(
            drawList,
            room,
            new Vector2(
                thumbnailMin.X +
                thumbnailSize.X *
                0.5f,
                thumbnailMax.Y -
                Ui(2f)));

        var actionWidth = Ui(compactRoom ? 100f : 160f);

        var lockButtonSize =
            Ui(42f);

        var actionGap =
            Ui(8f);

        var hasLockButton =
            room.Kind ==
            RoomKind.Locked;

        var actionTotalWidth =
            actionWidth +
            (
                hasLockButton
                    ? lockButtonSize +
                      actionGap
                    : 0f
            );

        var actionStartX =
            cardMax.X -
            padding -
            actionTotalWidth;

        var contentX =
            thumbnailMax.X +
            Ui(20f);

        var contentRight =
            actionStartX -
            Ui(18f);

        var contentWidth =
            Math.Max(
                1f,
                contentRight -
                contentX);

        var hostName =
            string.IsNullOrWhiteSpace(
                room.HostDisplayName)
                ? "Unknown Host"
                : room.HostDisplayName;

        var vibeWidth = DrawDirectoryPartyVibes(drawList, room,
            new Vector2(cardMax.X - padding, origin.Y + Ui(18f)),
            MathF.Max(0f, MathF.Min(Ui(compactRoom ? 180f : 220f),
                cardMax.X - padding - contentX - Ui(90f))));
        var hostWidth = vibeWidth > 0f ? MathF.Min(contentWidth,
            cardMax.X - padding - vibeWidth - Ui(12f) - contentX) : contentWidth;

        DrawPartyDirectoryHost(
            drawList,
            room,
            hostName,
            new Vector2(
                contentX,
                origin.Y +
                padding),
            Ui(34f), MathF.Max(1f, hostWidth));

        var titleY =
            origin.Y +
            Ui(56f);

        var hasSafeActivityTitle =
            TryGetPartyDirectoryActivityTitle(
                room,
                out var safeActivityTitle);

        // Locked rooms keep ordinary media private, but the built-in
        // activity names are safe to advertise and help people find a game.
        var roomTitle =
            hasSafeActivityTitle
                ? safeActivityTitle
                : room.Kind ==
                  RoomKind.Locked
                    ? "Locked Room - Content Hidden"
                    : media.Title;

        var displayTitle =
            PartyDirectoryTrimToWidth(
                roomTitle,
                contentWidth);

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                contentX,
                titleY), ImGui.GetColorU32(
                Vector4.One), displayTitle);

        if (!string.Equals(displayTitle, roomTitle, StringComparison.Ordinal) &&
            ImGui.IsMouseHoveringRect(
                new Vector2(contentX, titleY),
                new Vector2(contentX + contentWidth, titleY + ImGui.GetTextLineHeight())))
        {
            UiFonts.SetTooltip(roomTitle);
        }

        using (UiFonts.PushRelative(.90f))
        {
        var description =
            WatchPartyDescription(
                room);

        var descriptionY =
            titleY +
            Ui(28f);

        var displayDescription =
            PartyDirectoryTrimToWidth(
                description,
                contentWidth);

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                contentX,
                descriptionY), ImGui.GetColorU32(
                MutedText), displayDescription);

        if (!string.Equals(displayDescription, description, StringComparison.Ordinal) && ImGui.IsMouseHoveringRect(
                new Vector2(
                    contentX,
                    descriptionY),
                new Vector2(
                    contentX +
                    contentWidth,
                    descriptionY +
                    ImGui.GetTextLineHeight())))
        {
            UiFonts.SetTooltip(
                description);
        }

        var location =
            WatchPartyLocation(
                room);

        var locationY =
            descriptionY +
            Ui(27f);

        var locationGlyph =
            FontAwesomeIcon
                .MapMarkerAlt
                .ToIconString();

        Vector2 locationGlyphSize;

        using (UiFonts.PushIcon())
        {
            locationGlyphSize =
                ImGui.CalcTextSize(
                    locationGlyph);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                    contentX,
                    locationY), ImGui.GetColorU32(
                    AccentHover), locationGlyph);
        }

        var locationTextX =
            contentX +
            locationGlyphSize.X +
            Ui(7f);

        var locationTextWidth =
            Math.Max(
                Ui(60f),
                contentRight -
                locationTextX);

        var displayLocation =
            PartyDirectoryTrimToWidth(
                location,
                locationTextWidth);

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                locationTextX,
                locationY), ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.94f)), displayLocation);

        if (!string.Equals(displayLocation, location, StringComparison.Ordinal) && ImGui.IsMouseHoveringRect(
                new Vector2(
                    locationTextX,
                    locationY),
                new Vector2(
                    locationTextX +
                    locationTextWidth,
                    locationY +
                    ImGui.GetTextLineHeight())))
        {
            UiFonts.SetTooltip(
                location);
        }

        }
        LayoutDirectoryRoomTags(drawList, new Vector2(contentX, origin.Y + Ui(143f)),
            cardMax.X - padding - contentX, roomTags, render: true);

        var buttonY = origin.Y + Ui(69f);

        ImGui.SetCursorScreenPos(
            new Vector2(
                actionStartX,
                buttonY));

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
                    "Join room",
                    new Vector2(
                        actionWidth,
                        Ui(42f))))
            {
                JoinPartyDirectoryRoom(
                    room);
            }
        }

        if (hasLockButton)
        {
            ImGui.SameLine(
                0f,
                actionGap);

            var lockClicked =
                false;

            using (ImRaii.PushColor(
                       ImGuiCol.Button,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.14f)))
            using (ImRaii.PushColor(
                       ImGuiCol.ButtonHovered,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.30f)))
            using (ImRaii.PushColor(
                       ImGuiCol.Border,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.62f)))
            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameBorderSize,
                       Ui(1f)))
            using (UiFonts.PushIcon())
            {
                lockClicked =
                    ImGui.Button(
                        FontAwesomeIcon
                            .Lock
                            .ToIconString() +
                        "##directoryLockedRoom",
                        new Vector2(
                            lockButtonSize,
                            Ui(42f)));
            }

            if (ImGui.IsItemHovered())
            {
                UiFonts.SetTooltip(
                    "Password required");
            }

            if (lockClicked)
            {
                JoinPartyDirectoryRoom(
                    room);
            }
        }

        ImGui.SetCursorScreenPos(
            origin);

        ImGui.Dummy(
            size);
    }

    private void DrawPartyDirectoryThumbnail(
      ImDrawListPtr drawList,
      RoomDirectoryDto room,
      PartyDirectoryMediaInfo media,
      Vector2 origin,
      Vector2 size, ProfileThemeDesign? profileTheme = null)
    {
        if (DirectoryCountdown(room.HostAccountId, room.Url) is { } countdown)
        {
            DrawCountdownThumbnail(countdown, origin, size, profileTheme?.Accent,
                profileTheme is null ? null : ThemeInset(profileTheme));
            return;
        }
        IDalamudTextureWrap? image =
            null;

        //
        // Locked rooms deliberately do not receive their real media
        // URL or thumbnail. Neutral fallback artwork is retained
        // underneath the locked-room treatment.
        //
        if (room.Kind !=
                RoomKind.Locked &&
            !string.IsNullOrWhiteSpace(
                media.ThumbnailUrl))
        {
            image =
                thumbnails.Get(
                    media.ThumbnailUrl);
        }

        if (image is not null)
        {
            var (uv0, uv1) =
                CoverUvs(
                    image.Width,
                    image.Height,
                    size.X,
                    size.Y);

            drawList.AddImageRounded(
                image.Handle,
                origin,
                origin + size,
                uv0,
                uv1,
                uint.MaxValue,
                Ui(8f));
        }
        else
        {
            var source = !room.HasMedia ? "Waiting" : GetPartyDirectoryContentType(room) switch
            {
                PartyDirectoryContentType.RetroGamesStreams => "Gameplay",
                PartyDirectoryContentType.Activities => "Arcade",
                PartyDirectoryContentType.WebBrowsing => "Browser",
                PartyDirectoryContentType.RadioAudio or PartyDirectoryContentType.DjingLive => "Radio",
                PartyDirectoryContentType.StreamingLive => "Live Stream",
                PartyDirectoryContentType.LocalVideoStreams => "Local Video",
                _ => "Video",
            };
            var entry = new VideoQueueEntry(room.Url ?? string.Empty,
                media.Title ?? room.MediaTitle ?? string.Empty, source, null, null);
            DrawPartyThumbnailFallback(entry, origin, size, usePlaybackState: false,
                accentTint: profileTheme?.Accent, backgroundTint: profileTheme is null ? null : ThemeInset(profileTheme));
        }

        //
        // General image treatment.
        //
        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    0.005f,
                    0.008f,
                    0.020f,
                    0.14f)),
            Ui(8f));

        if (room.Kind ==
            RoomKind.Locked)
        {
            //
            // Place a neutral semi-transparent grey layer over the
            // entire thumbnail so it cannot be mistaken for visible
            // room content.
            //
            drawList.AddRectFilled(
                origin,
                origin + size,
                ImGui.GetColorU32(
                    FrameBgHover),
                Ui(8f));

            var lockCenter =
                origin +
                size *
                0.5f;

            var lockCircleRadius =
                Ui(25f);

            drawList.AddCircleFilled(
                lockCenter,
                lockCircleRadius,
                ImGui.GetColorU32(
                    CardBg),
                32);

            drawList.AddCircle(
                lockCenter,
                lockCircleRadius,
                ImGui.GetColorU32(
                    new Vector4(
                        0.82f,
                        0.84f,
                        0.90f,
                        0.72f)),
                32,
                Ui(1.5f));

            using (UiFonts.PushIcon())
            {
                var lockGlyph =
                    FontAwesomeIcon.Lock
                        .ToIconString();

                var lockGlyphSize =
                    ImGui.CalcTextSize(
                        lockGlyph);

                UiFonts.DrawText(drawList, ImGui.GetFontSize(), lockCenter -
                    lockGlyphSize *
                    0.5f, ImGui.GetColorU32(
                        new Vector4(
                            0.94f,
                            0.95f,
                            0.98f,
                            1f)), lockGlyph);
            }
        }

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    (profileTheme?.Accent ?? Accent).X,
                    (profileTheme?.Accent ?? Accent).Y,
                    (profileTheme?.Accent ?? Accent).Z,
                    room.Kind ==
                            RoomKind.Locked
                        ? 0.44f
                        : 0.28f)),
            Ui(8f),
            ImDrawFlags.None,
            Ui(1f));
    }

    private void DrawPartyDirectoryPlaybackBadge(
        ImDrawListPtr drawList,
        RoomDirectoryDto room,
        Vector2 origin, Vector4? accentTint = null)
    {
        var text =
            WatchPartyPlaybackStateText(
                room);

        var color =
            accentTint ?? WatchPartyPlaybackStateColor(
                room);

        var textSize =
            ImGui.CalcTextSize(
                text);

        var size =
            new Vector2(
                textSize.X +
                Ui(22f),
                Ui(23f));

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                CardBg),
            Ui(5f));

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                color),
            Ui(5f),
            ImDrawFlags.None,
            Ui(1f));

        drawList.AddCircleFilled(
            new Vector2(
                origin.X +
                Ui(8f),
                origin.Y +
                size.Y *
                0.5f),
            Ui(2.6f),
            ImGui.GetColorU32(
                color),
            16);

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                origin.X +
                Ui(14f),
                origin.Y +
                (
                    size.Y -
                    textSize.Y
                ) *
                0.5f), ImGui.GetColorU32(
                color), text);
    }

    private void DrawPartyDirectoryViewerBadge(
        ImDrawListPtr drawList,
        int totalViewers,
        Vector2 topRight,string? hostId=null)
    {
        var capacity = hostId is null ? null : AlphaChannel.Plugin.Auth.RoomCapacityClient.Shared.Cached(hostId);
        var countText = hostId is null ? totalViewers.ToString()
            : $"{totalViewers}/{(capacity is null ? "…" : (capacity.Capacity + 1).ToString())}";

        var eye =
            FontAwesomeIcon.Eye
                .ToIconString();

        Vector2 eyeSize;

        using (UiFonts.PushIcon())
        {
            eyeSize =
                ImGui.CalcTextSize(
                    eye);
        }

        var countSize =
            ImGui.CalcTextSize(
                countText);

        var badgeSize =
            new Vector2(
                eyeSize.X +
                countSize.X +
                Ui(20f),
                Ui(23f));

        var badgeMin =
            new Vector2(
                topRight.X -
                badgeSize.X,
                topRight.Y);

        drawList.AddRectFilled(
            badgeMin,
            badgeMin +
            badgeSize,
            ImGui.GetColorU32(
                CardBg),
            Ui(5f));

        using (UiFonts.PushIcon())
        {
            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                    badgeMin.X +
                    Ui(7f),
                    badgeMin.Y +
                    (
                        badgeSize.Y -
                        eyeSize.Y
                    ) *
                    0.5f), ImGui.GetColorU32(
                    Vector4.One), eye);
        }

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                badgeMin.X +
                Ui(11f) +
                eyeSize.X,
                badgeMin.Y +
                (
                    badgeSize.Y -
                    countSize.Y
                ) *
                0.5f), ImGui.GetColorU32(
                Vector4.One), countText);
    }

    private void DrawPartyDirectoryCategoryBubble(
        ImDrawListPtr drawList,
        RoomDirectoryDto room,
        Vector2 center)
    {
        var categoryColor = WatchPartyCategoryColor(room);
        var radius =
            Ui(22f);

        drawList.AddCircleFilled(
            center,
            radius +
            Ui(3f),
            ImGui.GetColorU32(
                CardBg),
            32);

        drawList.AddCircleFilled(
            center,
            radius,
            ImGui.GetColorU32(
                new Vector4(
                    categoryColor.X,
                    categoryColor.Y,
                    categoryColor.Z,
                    0.30f)),
            32);

        drawList.AddCircle(
            center,
            radius,
            ImGui.GetColorU32(
                categoryColor),
            32,
            Ui(1f));

        var glyph =
            WatchPartyCategoryIcon(
                room)
                .ToIconString();

        using (UiFonts.PushIcon())
        {
            var glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), center -
                glyphSize *
                0.5f, ImGui.GetColorU32(
                    GetWatchPartyCategory(room) == WatchPartyCategory.Halloween ? categoryColor : Vector4.One), glyph);
        }
    }

    private void DrawPartyDirectoryHost(
        ImDrawListPtr drawList,
        RoomDirectoryDto room,
        string hostName,
        Vector2 origin,
        float avatarSize,
        float? maxWidth = null)
    {
        if (!string.IsNullOrWhiteSpace(
                room.HostAccountId))
        {
            EnsurePartyAvatarLoaded(
                room.HostAccountId,
                hostName);
        }

        PartyAvatarInfo? avatar =
            null;

        if (!string.IsNullOrWhiteSpace(
                room.HostAccountId) &&
            partyAvatarCache.TryGetValue(
                room.HostAccountId,
                out var cachedAvatar))
        {
            avatar =
                cachedAvatar;
        }

        var hasAvatar =
            avatar is not null &&
            (
                !string.IsNullOrWhiteSpace(
                    avatar.AvatarIcon) ||
                !string.IsNullOrWhiteSpace(
                    avatar.AvatarImageUrl)
            );

        if (hasAvatar &&
            avatar is not null)
        {
            DrawAvatarAt(
                origin,
                avatar.AvatarIcon,
                avatar.AvatarColorHex,
                avatarSize,
                avatar.AvatarImageUrl);
        }
        else
        {
            var center =
                origin +
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
                        0.22f)),
                32);

            drawList.AddCircle(
                center,
                avatarSize * 0.5f,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.62f)),
                32,
                Ui(1f));

            var glyph =
                FontAwesomeIcon.User
                    .ToIconString();

            using (UiFonts.PushIcon())
            {
                var glyphSize =
                    ImGui.CalcTextSize(
                        glyph);

                UiFonts.DrawText(drawList, ImGui.GetFontSize(), center -
                    glyphSize *
                    0.5f, ImGui.GetColorU32(
                        AccentHover), glyph);
            }
        }

        var hostText = $"Hosted by: {hostName}";
        var verificationWidth = AccountVerified(room.HostAccountId) ? Ui(4f) + ImGui.GetFontSize() : 0f;
        if (maxWidth is { } availableHostWidth)
        {
            var badgeWidth = PatreonBadgeTier(room.HostAccountId) > 0 ? Ui(34f) : 0f;
            hostText = PartyDirectoryTrimToWidth(hostText,
                MathF.Max(1f, availableHostWidth - avatarSize - Ui(15f) - verificationWidth - badgeWidth));
        }

        DrawUsernameAt(drawList,new Vector2(
                origin.X +
                avatarSize +
                Ui(9f),
                origin.Y +
                (
                    avatarSize -
                    ImGui.GetTextLineHeight()
                ) *
                0.5f),hostText,
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.98f),room.HostAccountId);
        DrawPatreonBadgeAt(new Vector2(origin.X + avatarSize + Ui(15f) + ImGui.CalcTextSize(hostText).X + verificationWidth,
            origin.Y + (avatarSize - Ui(19f)) * 0.5f), room.HostAccountId,
            "partyDirectoryHost" + room.HostAccountId);
    }

    private void DrawPartyDirectoryEmptyState(FontAwesomeIcon icon, string title, string description)
    {
        var available = ImGui.GetContentRegionAvail();
        var origin = ImGui.GetCursorScreenPos();
        var center = origin + new Vector2(available.X * .5f, MathF.Min(available.Y * .42f, Ui(180f)));
        using (UiFonts.PushIcon())
        {
            var glyph = icon.ToIconString();
            var glyphSize = ImGui.CalcTextSize(glyph);
            ImGui.SetCursorScreenPos(new Vector2(center.X - glyphSize.X * .5f, center.Y - Ui(35f)));
            ImGui.TextColored(AccentHover, glyph);
        }
        using (UiFonts.PushRelative(1f, emphasis: true))
        {
            var titleSize = ImGui.CalcTextSize(title);
            ImGui.SetCursorScreenPos(new Vector2(center.X - titleSize.X * .5f, center.Y));
            ImGui.TextColored(Hex(0xE2E7EF), title);
        }
        using (UiFonts.PushRelative(.90f))
        {
            var wrapWidth = MathF.Max(Ui(40f), available.X - Ui(24f));
            var descriptionSize = ImGui.CalcTextSize(description, false, wrapWidth);
            var textX = MathF.Max(origin.X + Ui(12f), center.X - descriptionSize.X * .5f);
            ImGui.SetCursorScreenPos(new Vector2(textX, center.Y + Ui(28f)));
            using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + wrapWidth))
                ImGui.TextColored(MutedText, description);
        }
    }

    private static bool IsAlphaChannelDjStream(
    RoomDirectoryDto room)
    {
        if (string.IsNullOrWhiteSpace(
                room.Url))
        {
            return false;
        }

        var url =
            room.Url.Trim();

        return RelayRadioUrl.IsMatch(url, RelayServerProfile.ServerUrl, Configuration.ProductionRelayServerUrl);
    }

    private static PartyDirectoryContentType GetPartyDirectoryContentType(RoomDirectoryDto room)
    {
        if (!room.HasMedia) return PartyDirectoryContentType.Other;
        if (IsAlphaChannelDjStream(room)) return PartyDirectoryContentType.DjingLive;

        var url = room.Url?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            // Locked-room URLs are withheld by the directory. Use only titles
            // that the host already publishes as safe display metadata.
            if (TryGetPartyDirectoryActivityTitle(room, out _))
                return PartyDirectoryContentType.Activities;
            if (string.Equals(room.MediaTitle, "Live Streaming", StringComparison.OrdinalIgnoreCase))
                return PartyDirectoryContentType.StreamingLive;
            if (string.Equals(room.MediaTitle, "Web Browser", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(room.MediaTitle, "Web Browsing", StringComparison.OrdinalIgnoreCase))
                return PartyDirectoryContentType.WebBrowsing;
            if (string.Equals(room.MediaTitle, "Local Video", StringComparison.OrdinalIgnoreCase))
                return PartyDirectoryContentType.LocalVideoStreams;
            if (string.Equals(room.MediaTitle, "Gameplay", StringComparison.OrdinalIgnoreCase))
                return PartyDirectoryContentType.RetroGamesStreams;
            return PartyDirectoryContentType.Other;
        }

        if (url.StartsWith(GameBroadcastStatusUrlPrefix, StringComparison.OrdinalIgnoreCase))
            return PartyDirectoryContentType.RetroGamesStreams;
        if (IsPartyDirectoryCanvasActivity(room))
            return PartyDirectoryContentType.Activities;
        if (url.Contains("#acmedia=", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("&acmedia=", StringComparison.OrdinalIgnoreCase))
            return PartyDirectoryContentType.ImagesSlideshows;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return PartyDirectoryContentType.Other;

        if (LiveBroadcastStream.AppliesTo(url))
        {
            var query = uri.Query;
            if (query.Contains("ac_content=local-video", StringComparison.OrdinalIgnoreCase))
                return PartyDirectoryContentType.LocalVideoStreams;
            if (query.Contains("ac_content=retro-games", StringComparison.OrdinalIgnoreCase))
                return PartyDirectoryContentType.RetroGamesStreams;
            if (query.Contains("ac_content=web-browsing", StringComparison.OrdinalIgnoreCase))
                return PartyDirectoryContentType.WebBrowsing;
            return PartyDirectoryContentType.StreamingLive;
        }

        if (uri.Host.Equals("twitch.tv", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".twitch.tv", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".ttvnw.net", StringComparison.OrdinalIgnoreCase))
            return PartyDirectoryContentType.Twitch;

        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp")
            return PartyDirectoryContentType.ImagesSlideshows;
        if (extension is ".mp3" or ".aac" or ".ogg" or ".opus" or ".flac" or ".wav" or ".m4a" ||
            url.Contains("#vis=", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("&vis=", StringComparison.OrdinalIgnoreCase))
            return PartyDirectoryContentType.RadioAudio;

        return uri.Scheme is "http" or "https"
            ? PartyDirectoryContentType.OnlineVideos
            : PartyDirectoryContentType.Other;
    }

    private void EnsurePartyDirectoryMediaLoaded(
        RoomDirectoryDto room)
    {
        if (PartyCountdownSignal.IsCountdown(room.Url)) return;
        if (!room.HasMedia ||
      string.IsNullOrWhiteSpace(
          room.Url) ||
      IsPartyDirectoryCanvasActivity(
          room) ||
      IsAlphaChannelDjStream(
          room))
        {
            return;
        }

        //
        // If the server eventually supplies metadata, there is no
        // reason to perform an additional local lookup.
        //
        if (!string.IsNullOrWhiteSpace(
                room.MediaTitle) ||
            !string.IsNullOrWhiteSpace(
                room.MediaThumbnailUrl))
        {
            return;
        }

        var url =
            room.Url.Trim();

        if (partyDirectoryMediaCache.TryGetValue(
                url,
                out var cached) &&
            DateTime.UtcNow -
            cached.RefreshedAt <
            PartyDirectoryMetadataLifetime)
        {
            return;
        }

        if (!partyDirectoryMediaRequestsInFlight.TryAdd(
                url,
                0))
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var metadata =
                        await searchResolver
                            .GetVideoEntryAsync(
                                url,
                                CancellationToken.None, section: YouTube.YouTubeFetchSection.PartyDirectory)
                            .ConfigureAwait(false);

                    partyDirectoryMediaCache[url] =
                        new PartyDirectoryMediaInfo(
                            metadata?.Title,
                            metadata?.ThumbnailUrl,
                            DateTime.UtcNow);
                }
                catch (Exception)
                {
                    {}

                    partyDirectoryMediaCache[url] =
                        new PartyDirectoryMediaInfo(
                            null,
                            null,
                            DateTime.UtcNow);
                }
                finally
                {
                    partyDirectoryMediaRequestsInFlight.TryRemove(
                        url,
                        out _);
                }
            });
    }

    private PartyDirectoryMediaInfo GetPartyDirectoryMedia(
       RoomDirectoryDto room)
    {
        if (PartyCountdownSignal.IsCountdown(room.Url)) return new(string.IsNullOrWhiteSpace(room.MediaTitle) ? "Starting soon" : room.MediaTitle, null, DateTime.UtcNow);
        if (TryGetPartyDirectoryActivityTitle(
                room,
                out var activityTitle))
        {
            return new PartyDirectoryMediaInfo(
                activityTitle,
                null,
                DateTime.UtcNow);
        }

        //
        if (room.HasMedia && GetPartyDirectoryContentType(room) == PartyDirectoryContentType.WebBrowsing)
            return new PartyDirectoryMediaInfo("Web Browsing", null, DateTime.UtcNow);

        // Use the host's broadcast title for Icecast mounts without exposing
        // or trying to resolve the stream URL.
        //
        // Locked rooms retain their privacy-safe title.
        //
        if (room.Kind != RoomKind.Locked &&
            IsAlphaChannelDjStream(
                room))
        {
            return new PartyDirectoryMediaInfo(
                WatchPartyMediaDisplay.Title(room.Url,
                    string.IsNullOrWhiteSpace(room.MediaTitle) ? "Live DJ Stream" : room.MediaTitle, audioOnly: true),
                null,
                DateTime.UtcNow);
        }

        if (!string.IsNullOrWhiteSpace(
                room.MediaTitle) ||
            !string.IsNullOrWhiteSpace(
                room.MediaThumbnailUrl))
        {
            return new PartyDirectoryMediaInfo(
                string.IsNullOrWhiteSpace(
                    room.MediaTitle)
                    ? PartyDirectoryFallbackMediaTitle(
                        room)
                    : WatchPartyMediaDisplay.Title(room.Url, room.MediaTitle,
                        GetPartyDirectoryContentType(room) == PartyDirectoryContentType.RadioAudio),
                room.MediaThumbnailUrl,
                DateTime.UtcNow);
        }

        if (!string.IsNullOrWhiteSpace(
                room.Url) &&
            partyDirectoryMediaCache.TryGetValue(
                room.Url,
                out var cached))
        {
            return cached with
            {
                Title =
                    string.IsNullOrWhiteSpace(
                        cached.Title)
                        ? PartyDirectoryFallbackMediaTitle(
                            room)
                        : WatchPartyMediaDisplay.Title(room.Url, cached.Title,
                            GetPartyDirectoryContentType(room) == PartyDirectoryContentType.RadioAudio),
            };
        }

        return new PartyDirectoryMediaInfo(
            PartyDirectoryFallbackMediaTitle(
                room),
            null,
            DateTime.UtcNow);
    }

    private static string PartyDirectoryFallbackMediaTitle(
       RoomDirectoryDto room)
    {
        //
        // Locked rooms always use one neutral title. This avoids
        // revealing whether the host currently has content loaded.
        //
        if (room.Kind ==
            RoomKind.Locked)
        {
            return "Locked Room - Content Hidden";
        }

        if (!room.HasMedia)
        {
            return "Waiting for content";
        }

        if (InternetArchiveMediaDisplay.IsArchiveUrl(room.Url))
            return InternetArchiveMediaDisplay.FallbackTitle;
        return GetPartyDirectoryContentType(room) switch
        {
            PartyDirectoryContentType.OnlineVideos => "Watching an online video",
            PartyDirectoryContentType.Twitch => "Watching a Twitch stream",
            PartyDirectoryContentType.StreamingLive => "Watching a live stream",
            PartyDirectoryContentType.RadioAudio => "Listening to audio stream",
            PartyDirectoryContentType.DjingLive => "Listening to a live DJ",
            PartyDirectoryContentType.ImagesSlideshows => "Viewing images or a slideshow",
            PartyDirectoryContentType.LocalVideoStreams => "Watching a local video stream",
            PartyDirectoryContentType.RetroGamesStreams => "Streaming retro games",
            PartyDirectoryContentType.Activities => "Playing an activity",
            PartyDirectoryContentType.WebBrowsing => "Browsing the web",
            _ => "Watching shared content",
        };
    }

    private static bool IsPartyDirectoryCanvasActivity(
        RoomDirectoryDto room) =>
        room.Url?.StartsWith(
            WhiteboardUrlPrefix,
            StringComparison.OrdinalIgnoreCase) == true;

    private static bool TryGetPartyDirectoryActivityTitle(
        RoomDirectoryDto room,
        out string title)
    {
        title = string.Empty;

        // The synthetic canvas URL carries the exact activity. Prefer it to
        // display metadata, which may be absent or stale on older servers.
        var canvasActivity = ActivityFromCanvasUrl(room.Url);
        if (canvasActivity is not (ActiveActivityKind.None or ActiveActivityKind.Whiteboard))
        {
            title = ActivityTitle(canvasActivity);
            return true;
        }

        // New hosts publish the activity name as display metadata alongside
        // the synthetic canvas URL. Locked rooms intentionally omit the URL
        // from directory results, so accept only the known safe labels
        // there rather than exposing arbitrary media titles.
        if (string.Equals(
                room.MediaTitle,
                "Quiz",
                StringComparison.OrdinalIgnoreCase))
        {
            title = "Quiz";
            return true;
        }

        if (string.Equals(
                room.MediaTitle,
                "Pictionary",
                StringComparison.OrdinalIgnoreCase))
        {
            title = "Pictionary";
            return true;
        }

        if (string.Equals(room.MediaTitle, "Pictionary Teams", StringComparison.OrdinalIgnoreCase))
        {
            title = "Pictionary Teams";
            return true;
        }

        if (string.Equals(room.MediaTitle, "Roulette", StringComparison.OrdinalIgnoreCase))
        {
            title = "Roulette";
            return true;
        }

        if (string.Equals(room.MediaTitle, "Bingo", StringComparison.OrdinalIgnoreCase))
        {
            title = "Bingo";
            return true;
        }

        if (string.Equals(room.MediaTitle, "Air Force EX", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(room.MediaTitle, "Sky Rail", StringComparison.OrdinalIgnoreCase))
        {
            title = "Air Force EX";
            return true;
        }

        if (string.Equals(room.MediaTitle, "Slot Machine", StringComparison.OrdinalIgnoreCase))
        {
            title = "Slot Machine";
            return true;
        }

        if (string.Equals(room.MediaTitle, "Poker", StringComparison.OrdinalIgnoreCase))
        {
            title = "Poker";
            return true;
        }

        if (string.Equals(room.MediaTitle, "Choco RaceBets", StringComparison.OrdinalIgnoreCase))
        {
            title = "Choco RaceBets";
            return true;
        }

        if (string.Equals(
                room.MediaTitle,
                "Whiteboard",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(room.MediaTitle, "Canvas", StringComparison.OrdinalIgnoreCase) ||
            (IsPartyDirectoryCanvasActivity(room) &&
             string.IsNullOrWhiteSpace(room.MediaTitle)))
        {
            title = "Canvas";
            return true;
        }

        return false;
    }

    private void JoinPartyDirectoryRoom(
       RoomDirectoryDto room)
    {
        if(!EnsureWatchPartyJoinAllowed()) return;
        if (room.Kind !=
            RoomKind.Locked)
        {
            currentPage =
                HomePage.WatchAlong;

            JoinHomeWatchParty(
                room);

            return;
        }

        //
        // Locked rooms must collect their password before navigating
        // away from the directory or submitting the join request.
        //
        partyDirectoryPasswordRoom =
            room;

        partyDirectoryPassword =
            string.Empty;

        partyDirectoryPasswordError =
            null;

        partyDirectoryPasswordPopupRequested =
            true;
    }

    private void DrawPartyDirectoryPasswordPopup()
    {
        if (!partyDirectoryPasswordPopupRequested)
        {
            return;
        }

        var room =
            partyDirectoryPasswordRoom;

        if (room is null)
        {
            partyDirectoryPasswordPopupRequested =
                false;

            partyDirectoryPasswordError =
                null;

            partyDirectoryPassword =
                string.Empty;

            return;
        }

        var popupWidth = Ui(470f);

        var popupHeight = Ui(285f);

        //
        // Use the same Alpha Channel overlay treatment as the locked
        // room creation, username and controller configuration prompts.
        //
        var parentPos =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var popupPos =
            new Vector2(
                parentPos.X +
                (parentSize.X -
                 popupWidth) *
                0.5f,

                parentPos.Y +
                (parentSize.Y -
                 popupHeight) *
                0.5f);

        ImGui.SetNextWindowPos(
            parentPos,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(
            0f);

        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;

        if (!BeginWindowOverlay(
                "##partyDirectoryPasswordOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

        //
        // Darken the Alpha Channel window behind the prompt.
        //
        drawList.AddRectFilled(
            parentPos,
            parentPos +
            parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.48f)));

        var popupMax =
            popupPos +
            new Vector2(
                popupWidth,
                popupHeight);

        drawList.AddRectFilled(
            popupPos,
            popupMax,
            ImGui.GetColorU32(
                CardBg),
            10f);

        drawList.AddRect(
            popupPos,
            popupMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.45f)),
            10f,
            ImDrawFlags.RoundCornersAll,
            1f);

        var padding = NativeUi(20f);

        var contentWidth =
            popupWidth -
            padding *
            2f;

        //
        // Lock icon.
        //
        var iconCenter =
            popupPos +
            new Vector2(
                padding +
                Ui(18f),
                Ui(31f));

        drawList.AddCircleFilled(
            iconCenter,
            18f,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.24f)),
            24);

        using (UiFonts.PushIcon())
        {
            var lockGlyph =
                FontAwesomeIcon.Lock
                    .ToIconString();

            var lockGlyphSize =
                ImGui.CalcTextSize(
                    lockGlyph);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), iconCenter -
                lockGlyphSize *
                0.5f, ImGui.GetColorU32(
                    Accent), lockGlyph);
        }

        //
        // Heading.
        //
        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding +
                Ui(48f),
                Ui(20f)));

        using var uiFontScale83357=UiFonts.PushRelative(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Join locked room");

        uiFontScale83357.Dispose();

        var hostName =
            string.IsNullOrWhiteSpace(
                room.HostDisplayName)
                ? "this host"
                : room.HostDisplayName;

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(63f)));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            contentWidth);

        ImGui.TextColored(
            MutedText,
            $"Enter the password for {hostName}'s room to join the Watch Party.");

        ImGui.PopTextWrapPos();

        //
        // Password field.
        //
        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(113f)));

        ImGui.TextColored(
            MutedText,
            "Room password");

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(137f)));

        ImGui.SetNextItemWidth(
            contentWidth);

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        var submitRequested =
            false;

        using (ImRaii.PushColor(
                   ImGuiCol.FrameBg,
                   FrameBg))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.75f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   1f))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   5f))
        {
            submitRequested =
                ImGui.InputTextWithHint(
                    "##partyDirectoryRoomPassword",
                    "Enter the room password",
                    ref partyDirectoryPassword,
                    64,
                    ImGuiInputTextFlags.Password |
                    ImGuiInputTextFlags.EnterReturnsTrue);
        }

        if (partyDirectoryPasswordError is { } passwordError)
        {
            ImGui.SetCursorScreenPos(
                popupPos +
                new Vector2(
                    padding,
                    Ui(176f)));

            ImGui.TextColored(
                Danger,
                passwordError);
        }

        //
        // Footer.
        //
        var dividerY =
            popupMax.Y -
            65f;

        drawList.AddLine(
            new Vector2(
                popupPos.X +
                padding,
                dividerY),
            new Vector2(
                popupMax.X -
                padding,
                dividerY),
            ImGui.GetColorU32(
                BorderSubtle),
            1f);

        var buttonGap = Ui(10f);

        var buttonWidth =
            (
                contentWidth -
                buttonGap
            ) /
            2f;

        var cancelRequested =
            false;

        ImGui.SetCursorScreenPos(
            new Vector2(
                popupPos.X +
                padding,
                popupMax.Y -
                Ui(50f)));

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   FrameBg))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   FrameBgHover))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonActive,
                   AccentFieldActive))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.55f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   1f))
        {
            cancelRequested =
                ImGui.Button(
                    "Cancel",
                    new Vector2(
                        buttonWidth,
                        Ui(36f)));
        }

        ImGui.SameLine(
            0f,
            buttonGap);

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
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
                    "Join room",
                    new Vector2(
                        buttonWidth,
                        Ui(36f))))
            {
                submitRequested =
                    true;
            }
        }

        var joinConfirmed =
            false;

        if (cancelRequested)
        {
            partyDirectoryPasswordPopupRequested =
                false;

            partyDirectoryPasswordRoom =
                null;

            partyDirectoryPassword =
                string.Empty;

            partyDirectoryPasswordError =
                null;
        }
        else if (submitRequested)
        {
            partyDirectoryPassword =
                partyDirectoryPassword.Trim();

            if (string.IsNullOrWhiteSpace(
                    partyDirectoryPassword))
            {
                partyDirectoryPasswordError =
                    "Please enter the room password.";
            }
            else
            {
                partyDirectoryPasswordPopupRequested =
                    false;

                partyDirectoryPasswordError =
                    null;

                joinConfirmed =
                    true;
            }
        }

        ImGui.End();

        //
        // Submit the existing join flow only after closing the popup's
        // ImGui window.
        //
        if (joinConfirmed && EnsureWatchPartyJoinAllowed())
        {
            joinHostNameInput =
                room.HostDisplayName;

            joinPasswordInput =
                partyDirectoryPassword;

            currentPage =
                HomePage.WatchAlong;

            partyDirectoryPasswordRoom =
                null;

            partyDirectoryPassword =
                string.Empty;

            DoJoin(
                joinHostNameInput,
                joinPasswordInput);
        }
    }

    private static string PartyDirectoryRoomKindText(
        RoomKind kind)
    {
        return kind switch
        {
            RoomKind.Locked =>
                "Locked",

            RoomKind.Venue =>
                "Venue",

            _ =>
                "Public",
        };
    }

    private static string PartyDirectoryCategoryName(
        WatchPartyCategory category)
    {
        return category switch
        {
            WatchPartyCategory.YouTube =>
                "YouTube",

            WatchPartyCategory.Movies =>
                "Movies",

            WatchPartyCategory.Tv =>
                "TV",

            WatchPartyCategory.Twitch =>
                "Twitch",

            WatchPartyCategory.Cartoons =>
                "Cartoons",

            WatchPartyCategory.LiveStream =>
                "Live Stream",

            WatchPartyCategory.Gaming =>
                "Gaming",

            WatchPartyCategory.Dj =>
                "DJ",

            WatchPartyCategory.Music =>
                "Music",

            WatchPartyCategory.Images =>
                "Images",

            WatchPartyCategory.Promotional =>
                "Promotional",

            WatchPartyCategory.Anime =>
                "Anime",

            WatchPartyCategory.Horror =>
                "Horror",

            WatchPartyCategory.Halloween =>
                "Halloween",

            WatchPartyCategory.WebBrowsing =>
                "Web Browsing",

            WatchPartyCategory.Marathon =>
                "Marathon",

            WatchPartyCategory.Social =>
                "Social",

            WatchPartyCategory.Cinema =>
                "Cinema",

            WatchPartyCategory.Club =>
                "Club",

            _ =>
                "Media",
        };
    }

    private static string PartyDirectoryTrimToWidth(
        string? text,
        float width)
    {
        if (string.IsNullOrWhiteSpace(
                text))
        {
            return string.Empty;
        }

        var result =
            text.Trim();

        if (ImGui.CalcTextSize(
                result).X <=
            width)
        {
            return result;
        }

        while (result.Length > 1 &&
               ImGui.CalcTextSize(
                   result + "…").X >
               width)
        {
            result =
                result[..^1];
        }

        return result.TrimEnd() +
               "…";
    }
}

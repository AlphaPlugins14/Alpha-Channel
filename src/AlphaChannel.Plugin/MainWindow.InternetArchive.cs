using System.Collections.Concurrent;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Internet Archive data and playback. InternetArchiveHub provides the compact
// themed header and metadata layout, using the same request lifetime.
internal sealed partial class MainWindow
{
    private sealed record ArchiveCategory(string Key, string Label, string Query, string Sort);

    private static readonly ArchiveCategory[] ArchiveCategories =
    [
        new("films", "Films", "collection:feature_films", "downloads desc"),
        new("animation", "Animation", "(subject:animation OR subject:cartoon)", "downloads desc"),
        new("documentaries", "Documentaries", "subject:documentary", "downloads desc"),
        new("television", "Vintage TV", "(subject:television OR collection:classic_tv)", "downloads desc"),
        new("history", "Historical", "collection:prelinger", "downloads desc"),
    ];

    private static readonly TimeSpan InternetArchiveRefreshInterval = TimeSpan.FromHours(3);

    private readonly InternetArchiveClient internetArchive = new();
    private readonly InternetArchiveCacheStorage internetArchiveCacheStorage = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<InternetArchiveItem>> archiveRows =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, InternetArchiveItem> archiveSearchCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> archiveCategoryLastPages =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> archiveCategoriesLoading =
        new(StringComparer.Ordinal);
    private readonly object internetArchiveCacheSync = new();
    private CancellationTokenSource? internetArchiveCts;
    private CancellationTokenSource? internetArchiveSearchCts;
    private CancellationTokenSource? internetArchiveLoadMoreCts;
    private bool internetArchiveStarted;
    private volatile bool internetArchiveLoading;
    private volatile bool internetArchiveSearchLoading;
    private volatile bool internetArchiveLoadMoreLoading;
    private string internetArchiveSearchText = string.Empty;
    private string? internetArchiveViewTitle;
    private string? internetArchiveViewCategoryKey;
    private IReadOnlyList<InternetArchiveItem> internetArchiveViewResults = Array.Empty<InternetArchiveItem>();
    private string? internetArchiveError;
    private string? internetArchiveLoadMoreMessage;
    private int internetArchiveSearchGeneration;
    private InternetArchiveItem? internetArchiveEpisodeItem;
    private DateTime internetArchiveLastRefreshUtc = DateTime.MinValue;
    private string? internetArchiveScrollView;
    private bool internetArchiveSortByName;

    private void DrawInternetArchivePage()
    {
        EnsureInternetArchiveStarted();

        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(9f, 9f));
        DrawInternetArchiveHubHeader();
        SetGameLayoutRowGap(Ui(12));

        using var content = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##internetArchiveContent", new Vector2(0f, -1f), false));
        if (!content) return;
        var view = currentPage + (internetArchivePopularTab ? "##archivePopular" : internetArchiveViewTitle ?? "##archiveHome") + (internetArchiveSortByName ? "|name" : "|views");
        if (internetArchiveScrollView != view)
        {
            internetArchiveScrollView = view;
            ImGui.SetScrollY(0f);
        }

        if (internetArchivePopularTab) { DrawArchivePopular(); return; }

        if (!string.IsNullOrWhiteSpace(internetArchiveError))
        {
            using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(Danger.X, Danger.Y, Danger.Z, 0.08f))
                .Push(ImGuiCol.Border, new Vector4(Danger.X, Danger.Y, Danger.Z, 0.52f)))
            using (var errorPanel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##archiveError", UiVec(ImGui.GetContentRegionAvail().X, 58f), true,
                       ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
            {
                if (errorPanel)
                {
                    ImGui.TextColored(Danger, internetArchiveError);
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Try again"))
                    {
                        internetArchiveError = null;
                        StartInternetArchiveHomeLoad(force: true);
                    }
                }
            }
            ImGui.Dummy(UiVec(0f, 4f));
        }

        if (internetArchiveViewTitle is not null)
        {
            DrawInternetArchiveResults();
            return;
        }

        var allItems = ArchiveCategories.SelectMany(category =>
                archiveRows.TryGetValue(category.Key, out var items) ? items : Array.Empty<InternetArchiveItem>())
            .DistinctBy(item => item.Identifier, StringComparer.OrdinalIgnoreCase).ToArray();
        if (allItems.Length > 0) DrawInternetArchiveGrid(SortInternetArchiveItems(allItems));
        else if (internetArchiveLoading) DrawInternetArchiveLoadingGrid();
        else ImGui.TextColored(MutedText, "No playable videos found. Try refreshing the Archive.");
    }

    private void DrawInternetArchiveSearch(bool stacked)
    {
        var searchButtonWidth = Ui(42f);
        var refreshButtonWidth = Ui(42f);
        var sortWidth = Ui(148f);
        using var frameStyle = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7f))
            .Push(ImGuiStyleVar.FramePadding, UiVec(13f, 12f));
        using var fieldColor = ImRaii.PushColor(ImGuiCol.FrameBg, FrameBg);
        using var fieldBorder = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, Ui(1));
        using var searchColors = ImRaii.PushColor(ImGuiCol.Border, HubTint(0x395A84))
            .Push(ImGuiCol.Button, HubSurface(0x14243C))
            .Push(ImGuiCol.ButtonHovered, HubSelected(0x17345D));
        ImGui.SetNextItemWidth(MathF.Max(
            1f,
            stacked ? ImGui.GetContentRegionAvail().X :
            ImGui.GetContentRegionAvail().X - searchButtonWidth - refreshButtonWidth - sortWidth - Ui(67f)));
        bool submitted;
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(38, 12)))
        submitted = ImGui.InputTextWithHint(
            "##internetArchiveSearch",
            "Search films, programmes, creators, or subjects...",
            ref internetArchiveSearchText,
            300,
            ImGuiInputTextFlags.EnterReturnsTrue);
        UiFonts.DrawCenteredIcon(ImGui.GetWindowDrawList(), Ui(16),
            ImGui.GetItemRectMin() + new Vector2(Ui(18), ImGui.GetItemRectSize().Y / 2),
            ImGui.GetColorU32(MutedText), FontAwesomeIcon.Search.ToIconString());
        if (!stacked) ImGui.SameLine(0f, Ui(9f));
        using (UiDisabled.Scope(string.IsNullOrWhiteSpace(internetArchiveSearchText) || internetArchiveSearchLoading))
        {
            submitted |= GameLayoutButton(
                string.Empty,
                FontAwesomeIcon.Search,
                searchButtonWidth,
                true, height: 40f);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiFonts.SetTooltip(internetArchiveSearchLoading ? "Searching the Internet Archive..." : "Search Internet Archive");
        DrawSearchQueueButton("archive", internetArchiveSearchText);
        ImGui.SameLine(0f, Ui(9f));
        using (UiDisabled.Scope(internetArchivePopularTab ? archivePopularLoading : internetArchiveLoading))
        {
            if (GameLayoutButton(
                    string.Empty,
                    FontAwesomeIcon.Sync,
                    refreshButtonWidth, height: 40f))
            {
                internetArchiveError = null;
                if (internetArchivePopularTab) RefreshArchivePopular(force: true);
                else StartInternetArchiveHomeLoad(force: true);
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            UiFonts.SetTooltip("Refresh Internet Archive");
        }
        ImGui.SameLine(0f, Ui(9f));
        ImGui.SetNextItemWidth(sortWidth);
        var sortLabel = internetArchivePopularTab ? "Sort: Popularity" : internetArchiveSortByName ? "Sort: Name" : "Sort: Views";
        using (UiDisabled.Scope(internetArchivePopularTab))
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##archiveSort", sortLabel)))
        {
            if (ImGui.Selectable("Name (A–Z)", internetArchiveSortByName)) internetArchiveSortByName = true;
            if (ImGui.Selectable("Views (highest first)", !internetArchiveSortByName)) internetArchiveSortByName = false;
            ImGui.EndCombo();
        }

        if (submitted && !string.IsNullOrWhiteSpace(internetArchiveSearchText))
        {
            StartInternetArchiveSearch(
                $"Search results for “{internetArchiveSearchText.Trim()}”",
                InternetArchiveClient.BuildTextSearchQuery(internetArchiveSearchText),
                "downloads desc",
                internetArchiveSearchText);
        }
    }

    private void DrawInternetArchiveResults()
    {
        using var uiFontScale12251=UiFonts.PushRelative(1.16f);
        ImGui.TextColored(Hex(0xE2E7EF), internetArchiveViewTitle ?? "Results");
        uiFontScale12251.Dispose();
        var resultsAreLoading = internetArchiveViewCategoryKey is { } loadingCategoryKey
            ? (archiveCategoriesLoading.TryGetValue(loadingCategoryKey, out var categoryLoads) && categoryLoads > 0) ||
              internetArchiveLoadMoreLoading
            : internetArchiveSearchLoading;
        if (resultsAreLoading)
        {
            ImGui.SameLine(0f, Ui(8f));
            DrawBrowseLoadingSpinner(Ui(7f));
        }
        ImGui.Dummy(UiVec(0f, 7f));

        var results = internetArchiveViewCategoryKey is { } categoryKey &&
                      archiveRows.TryGetValue(categoryKey, out var categoryResults)
            ? categoryResults
            : internetArchiveViewResults;
        if (internetArchiveSearchLoading)
        {
            ImGui.TextColored(
                MutedText,
                results.Count > 0
                    ? "Showing cached matches while additional live results are checked..."
                    : "Searching the Internet Archive and checking playable files...");
            if (results.Count == 0)
            {
                DrawInternetArchiveLoadingGrid();
                return;
            }
            ImGui.Dummy(UiVec(0f, 4f));
        }

        if (results.Count == 0)
        {
            GameLayoutPanel("##archiveNoResults", ImGui.GetContentRegionAvail().X, Ui(118f), () =>
            {
                GameLayoutHeading("No playable videos found", FontAwesomeIcon.Search);
                ImGui.TextColored(MutedText,
                    "Try a broader search. Audio, books, software, and items without a playable video file are excluded.");
            });
            return;
        }

        DrawInternetArchiveGrid(SortInternetArchiveItems(results));
        if (internetArchiveViewCategoryKey is { } loadMoreCategoryKey)
        {
            DrawInternetArchiveLoadMore(loadMoreCategoryKey);
        }
    }

    private void DrawInternetArchiveGrid(IReadOnlyList<InternetArchiveItem> items)
    {
        var gap = Ui(12f);
        var availableWidth = MathF.Max(1, ImGui.GetContentRegionAvail().X - Ui(12));
        var columns = BrowseVideoGridColumns(availableWidth);
        var width = (availableWidth - gap * (columns - 1)) / columns;
        var origin = ImGui.GetCursorScreenPos();
        for (var index = 0; index < items.Count; index++)
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(index % columns * (width + gap),
                index / columns * (BrowseVideoCardHeight + gap)));
            DrawInternetArchiveCard(items[index], width);
        }
        var rows = (items.Count + columns - 1) / columns;
        ImGui.SetCursorScreenPos(origin + new Vector2(0, rows * (BrowseVideoCardHeight + gap)));
    }

    private InternetArchiveItem[] SortInternetArchiveItems(IEnumerable<InternetArchiveItem> items) =>
        (internetArchiveSortByName
            ? items.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Identifier, StringComparer.OrdinalIgnoreCase)
            : items.OrderByDescending(item => item.Downloads).ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase))
        .ToArray();

    private void DrawInternetArchiveLoadMore(string categoryKey)
    {
        ImGui.Dummy(UiVec(0f, 12f));
        using (UiDisabled.Scope(internetArchiveLoadMoreLoading || internetArchiveLoading))
        {
            if (GameLayoutButton(
                    internetArchiveLoadMoreLoading ? "Loading more..." : "Load More",
                    internetArchiveLoadMoreLoading ? FontAwesomeIcon.Spinner : FontAwesomeIcon.Plus,
                    ImGui.GetContentRegionAvail().X,
                    true))
            {
                StartInternetArchiveLoadMore(categoryKey);
            }
        }

        if (!string.IsNullOrWhiteSpace(internetArchiveLoadMoreMessage))
        {
            ImGui.Dummy(UiVec(0f, 4f));
            ImGui.TextColored(Danger, internetArchiveLoadMoreMessage);
        }
    }

    private void DrawInternetArchiveCard(InternetArchiveItem item, float width) =>
        DrawSourceBrowseVideoCard(new UnifiedSearchResult(UnifiedSearchProvider.InternetArchive, null, item), width);

    private static void DrawArchiveMissingThumbnail(Vector2 origin, Vector2 size, ImDrawListPtr draw)
    {
        const string text = "NO THUMBNAIL AVAILABLE";
        var textSize = ImGui.CalcTextSize(text);
        draw.AddText(origin + (size - textSize) * 0.5f, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.48f)), text);
    }

    private void DrawInternetArchiveLoadingRow()
    {
        var gap = Ui(12f);
        var availableWidth = MathF.Max(1, ImGui.GetContentRegionAvail().X - Ui(12));
        var count = BrowseVideoGridColumns(availableWidth);
        var width = (availableWidth - gap * (count - 1)) / count;
        for (var index = 0; index < count; index++)
        {
            if (index > 0) ImGui.SameLine(0f, gap);
            DrawInternetArchiveLoadingCard(width, index);
        }
    }

    private void DrawInternetArchiveLoadingGrid()
    {
        ImGui.Dummy(UiVec(0f, 8f));
        DrawInternetArchiveLoadingRow();
    }

    private void DrawInternetArchiveLoadingCard(float width, int index)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, BrowseVideoCardHeight);
        ImGui.InvisibleButton("##archiveLoading" + index, size);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(CardBg), Ui(9f));
        draw.AddRectFilled(origin, origin + new Vector2(width, BrowseVideoThumbnailHeight + Ui(6)), ImGui.GetColorU32(FrameBg), Ui(9f));
        draw.AddRectFilled(origin + UiVec(9f, 124f), origin + new Vector2(width * 0.78f, Ui(136f)),
            ImGui.GetColorU32(BorderSubtle), Ui(3f));
        draw.AddRectFilled(origin + UiVec(9f, 149f), origin + new Vector2(width * 0.58f, Ui(159f)),
            ImGui.GetColorU32(BorderSubtle), Ui(3f));
    }

    private void PlayInternetArchiveItem(InternetArchiveItem item)
    {
        if (item.Episodes.Count > 1)
        {
            OpenInternetArchiveEpisodes(item);
            return;
        }

        HandlePlayNow(CreateInternetArchiveQueueEntry(item, item.Episodes[0]));
    }

    private static VideoQueueEntry CreateInternetArchiveQueueEntry(
        InternetArchiveItem item,
        InternetArchiveEpisode episode) =>
        new(
            episode.VideoUrl,
            item.Episodes.Count > 1 ? $"{item.Title} — {episode.Title}" : item.Title,
            string.IsNullOrWhiteSpace(item.Creator) ? "Internet Archive" : item.Creator,
            episode.Duration,
            item.ThumbnailUrl);

    private void OpenInternetArchiveEpisodes(InternetArchiveItem item)
    {
        internetArchiveEpisodeItem = item;
        activeGameDialog = "Internet Archive episodes";
    }

    private void DrawInternetArchiveEpisodeDialog()
    {
        if (activeGameDialog != "Internet Archive episodes") return;
        var item = internetArchiveEpisodeItem;
        if (item is null || item.Episodes.Count == 0)
        {
            activeGameDialog = null;
            return;
        }

        if (!BeginGameDialog(
                "Internet Archive episodes",
                "Choose an episode",
                FontAwesomeIcon.List,
                780f,
                680f))
        {
            return;
        }

        using var uiFontScale25757=UiFonts.PushRelative(1.12f);
        ImGui.TextColored(Hex(0xE2E7EF), item.Title);
        uiFontScale25757.Dispose();
        using (UiFonts.PushRelative(.90f))
        ImGui.TextColored(MutedText, $"{item.Episodes.Count} playable episodes found in this Archive item.");
        ImGui.Dummy(UiVec(0f, 6f));

        var bulkGap = Ui(8f);
        var bulkWidth = ImGui.GetContentRegionAvail().X - bulkGap - Ui(40f);
        var bulkDisabled = ShouldUseViewerMediaActions || video.IsPlayingLocalVideo;
        using (UiDisabled.Scope(bulkDisabled))
        {
            if (GameLayoutButton("Play All", FontAwesomeIcon.Play, bulkWidth, true))
            {
                PlayAllInternetArchiveEpisodes(item);
                activeGameDialog = null;
            }
        }
        if (bulkDisabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            UiFonts.SetTooltip(video.IsPlayingLocalVideo
                ? "Stop the local video before adding Archive episodes."
                : "Bulk episode actions are available to the Watch Party host. You can still select one episode below.");
        }
        ImGui.SameLine(0f, bulkGap);
        DrawInternetArchiveQueueOptions(item, null, 36f);

        ImGui.Dummy(UiVec(0f, 5f));
        using (var episodeList = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                   "##archiveEpisodeList",
                   new Vector2(
                       ImGui.GetContentRegionAvail().X,
                       MathF.Max(1f, ImGui.GetContentRegionAvail().Y - Ui(64f))),
                   false)))
        {
            if (episodeList)
            {
                for (var index = 0; index < item.Episodes.Count; index++)
                {
                    var episode = item.Episodes[index];
                    ImGui.PushID(index);
                    using var rowPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(12f, 10f));
                    using var rowRounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(8f));
                    using var rowColors = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
                        .Push(ImGuiCol.Border, BorderSubtle);
                    using (var row = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                               "##episodeRow",
                               new Vector2(ImGui.GetContentRegionAvail().X, Ui(64f)),
                               true,
                               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
                    {
                        if (row)
                        {
                            var actionWidth = Ui(90f);
                            var optionsWidth = Ui(40f);
                            var actionGap = Ui(7f);
                            var detailWidth = MathF.Max(
                                Ui(120f),
                                ImGui.GetContentRegionAvail().X - actionWidth - optionsWidth - actionGap - Ui(18f));
                            ImGui.BeginGroup();
                            ImGui.TextColored(Hex(0xE2E7EF), TruncateToWidth(episode.Title, detailWidth));
                            if (ImGui.IsItemHovered()) UiFonts.SetTooltip(episode.Title);
                            using (UiFonts.PushRelative(.90f))
                            ImGui.TextColored(
                                MutedText,
                                episode.Duration is { } duration
                                    ? FormatArchiveDuration(duration)
                                    : $"Episode {index + 1}");
                            ImGui.EndGroup();
                            ImGui.SameLine();
                            ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - actionWidth - optionsWidth - actionGap);
                            if (GameLayoutButton("Play", FontAwesomeIcon.Play, actionWidth, true, height: 34f))
                            {
                                HandlePlayNow(CreateInternetArchiveQueueEntry(item, episode));
                                activeGameDialog = null;
                            }
                            ImGui.SameLine(0f, actionGap);
                            DrawInternetArchiveQueueOptions(item, episode, 34f);
                        }
                    }
                    ImGui.PopID();
                    ImGui.Dummy(UiVec(0f, 6f));
                }
            }
        }

        ImGui.Dummy(UiVec(0f, 4f));
        if (GameLayoutButton("Close", FontAwesomeIcon.Times, ImGui.GetContentRegionAvail().X))
        {
            activeGameDialog = null;
            internetArchiveEpisodeItem = null;
        }
        EndGameDialog();
    }

    private void PlayAllInternetArchiveEpisodes(InternetArchiveItem item)
    {
        if (item.Episodes.Count == 0) return;
        HandlePlayNow(CreateInternetArchiveQueueEntry(item, item.Episodes[0]));
        foreach (var episode in item.Episodes.Skip(1))
        {
            HandleAddToQueue(CreateInternetArchiveQueueEntry(item, episode));
        }
    }

    private static string FormatArchiveDuration(TimeSpan duration) =>
        duration.TotalHours >= 1d
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";

    private static string FormatArchiveDetails(InternetArchiveItem item)
    {
        var creator = string.IsNullOrWhiteSpace(item.Creator) ? "Internet Archive" : item.Creator;
        var year = item.Year is null ? string.Empty : $"  •  {item.Year}";
        var episodes = item.Episodes.Count > 1 ? $"  •  {item.Episodes.Count} episodes" : string.Empty;
        return creator + year + episodes;
    }

    private static string FormatArchiveDownloads(long value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000d:0.#}M",
        >= 1_000 => $"{value / 1_000d:0.#}K",
        _ => value.ToString(),
    };

    private void EnsureInternetArchiveStarted()
    {
        if (internetArchiveStarted) return;
        internetArchiveStarted = true;
        var cached = internetArchiveCacheStorage.Load();
        internetArchiveLastRefreshUtc = cached.LastRefreshUtc;
        foreach (var (key, items) in cached.Categories)
        {
            if (items.Count > 0 && ArchiveCategories.Any(category => category.Key == key))
                archiveRows[key] = items;
        }
        foreach (var category in ArchiveCategories)
        {
            if (cached.CategoryLastPages.TryGetValue(category.Key, out var lastPage))
                archiveCategoryLastPages[category.Key] = Math.Max(1, lastPage);
            else if (archiveRows.ContainsKey(category.Key))
                archiveCategoryLastPages[category.Key] = 1;
        }
        foreach (var item in cached.SearchItems)
        {
            archiveSearchCache[item.Identifier] = item;
        }
        internetArchive.SeedResolvedItems(
            cached.Categories.Values.SelectMany(items => items).Concat(cached.SearchItems));
        internetArchive.SeedNoVideoItems(cached.NoVideoItems);
        StartInternetArchiveHomeLoad(force: false);
    }

    private void StartInternetArchiveHomeLoad(bool force)
    {
        if (internetArchiveLoading)
        {
            if (!force) return;
            internetArchiveCts?.Cancel();
        }
        if (!force && InternetArchiveCacheIsFreshAndComplete())
        {
            internetArchiveLoading = false;
            return;
        }
        internetArchiveCts?.Cancel();
        internetArchiveCts?.Dispose();
        internetArchiveCts = new CancellationTokenSource();
        internetArchiveLoading = true;
        _ = LoadInternetArchiveHomeAsync(internetArchiveCts.Token);
    }

    private async Task LoadInternetArchiveHomeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tasks = ArchiveCategories.Select(category => LoadInternetArchiveRowAsync(category, cancellationToken));
            var refreshed = await Task.WhenAll(tasks).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (refreshed.Any(success => success))
            {
                internetArchiveLastRefreshUtc = DateTime.UtcNow;
                SaveInternetArchiveCache();
                internetArchiveError = null;
            }
            else if (!archiveRows.Values.Any(items => items.Count > 0))
            {
                internetArchiveError = "The Internet Archive could not be reached. Check your connection and try again.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            AepLog.Warning("[InternetArchive] Page load failed.");
            internetArchiveError = "The Internet Archive could not be reached. Check your connection and try again.";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) internetArchiveLoading = false;
        }
    }

    private async Task<bool> LoadInternetArchiveRowAsync(
        ArchiveCategory category,
        CancellationToken cancellationToken)
    {
        archiveCategoriesLoading.AddOrUpdate(category.Key, 1, (_, count) => count + 1);
        try
        {
            var items = await internetArchive.SearchAsync(
                category.Query,
                category.Sort,
                12,
                cancellationToken,
                item => PublishInternetArchiveCategoryItem(category.Key, item, cancellationToken),
                priorityCandidateCount: 6)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            archiveCategoryLastPages.AddOrUpdate(category.Key, 1, (_, current) => Math.Max(current, 1));
            if (items.Count == 0)
            {
                SaveInternetArchiveCache();
                return false;
            }
            archiveRows.TryGetValue(category.Key, out var cached);
            archiveRows[category.Key] = MergeFreshArchiveItems(items, cached ?? Array.Empty<InternetArchiveItem>(), 200);
            SaveInternetArchiveCache();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            AepLog.Warning("[InternetArchive] [redacted] load failed.");
            return false;
        }
        finally
        {
            archiveCategoriesLoading.AddOrUpdate(category.Key, 0, (_, count) => Math.Max(0, count - 1));
        }
    }

    private void PublishInternetArchiveCategoryItem(
        string categoryKey,
        InternetArchiveItem item,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;
        archiveRows.AddOrUpdate(
            categoryKey,
            _ => new[] { item },
            (_, cached) => MergeFreshArchiveItems(new[] { item }, cached, 200));
    }

    private void StartInternetArchiveLoadMore(string categoryKey)
    {
        if (internetArchiveLoadMoreLoading) return;
        var category = ArchiveCategories.FirstOrDefault(candidate => candidate.Key == categoryKey);
        if (category is null) return;

        internetArchiveLoadMoreCts?.Cancel();
        internetArchiveLoadMoreCts?.Dispose();
        internetArchiveLoadMoreCts = new CancellationTokenSource();
        internetArchiveLoadMoreMessage = null;
        internetArchiveLoadMoreLoading = true;
        _ = LoadMoreInternetArchiveCategoryAsync(category, internetArchiveLoadMoreCts.Token);
    }

    private async Task LoadMoreInternetArchiveCategoryAsync(
        ArchiveCategory category,
        CancellationToken cancellationToken)
    {
        try
        {
            archiveRows.TryGetValue(category.Key, out var existingItems);
            var knownIdentifiers = new ConcurrentDictionary<string, byte>(
                (existingItems ?? Array.Empty<InternetArchiveItem>())
                .Select(item => new KeyValuePair<string, byte>(item.Identifier, 0)),
                StringComparer.OrdinalIgnoreCase);
            var lastPage = archiveCategoryLastPages.GetOrAdd(
                category.Key,
                existingItems is { Count: > 0 } ? 1 : 0);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = lastPage + 1;
                var addedCount = 0;
                void PublishNewItem(InternetArchiveItem item)
                {
                    if (cancellationToken.IsCancellationRequested ||
                        !knownIdentifiers.TryAdd(item.Identifier, 0))
                        return;
                    AppendInternetArchiveCategoryItem(category.Key, item);
                    Interlocked.Increment(ref addedCount);
                }

                var results = await internetArchive.SearchAsync(
                    category.Query,
                    category.Sort,
                    12,
                    cancellationToken,
                    PublishNewItem,
                    priorityCandidateCount: 6,
                    page: page).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var item in results) PublishNewItem(item);
                lastPage = page;
                archiveCategoryLastPages[category.Key] = lastPage;
                SaveInternetArchiveCache();
                if (addedCount > 0)
                {
                    internetArchiveLoadMoreMessage = null;
                    return;
                }
            }

            internetArchiveLoadMoreMessage = "Unable to load more videos";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            AepLog.Warning("[InternetArchive] Could not load more [redacted].");
            internetArchiveLoadMoreMessage = "Unable to load more videos";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) internetArchiveLoadMoreLoading = false;
        }
    }

    private void AppendInternetArchiveCategoryItem(string categoryKey, InternetArchiveItem item)
    {
        archiveRows.AddOrUpdate(
            categoryKey,
            _ => new[] { item },
            (_, cached) => cached.Any(existing =>
                existing.Identifier.Equals(item.Identifier, StringComparison.OrdinalIgnoreCase))
                ? cached
                : cached.Append(item).Take(200).ToArray());
    }

    private void CancelInternetArchiveLoadMore()
    {
        internetArchiveLoadMoreCts?.Cancel();
        internetArchiveLoadMoreCts?.Dispose();
        internetArchiveLoadMoreCts = null;
        internetArchiveLoadMoreLoading = false;
        internetArchiveLoadMoreMessage = null;
    }

    private void ShowInternetArchiveCategory(ArchiveCategory category)
    {
        internetArchivePopularTab = false;
        CancelInternetArchiveSearch();
        CancelInternetArchiveLoadMore();
        internetArchiveViewTitle = category.Label;
        internetArchiveViewCategoryKey = category.Key;
        internetArchiveViewResults = archiveRows.TryGetValue(category.Key, out var cached)
            ? cached
            : Array.Empty<InternetArchiveItem>();
        internetArchiveError = null;
        if (internetArchiveViewResults.Count == 0) StartInternetArchiveHomeLoad(force: false);
    }

    private void StartInternetArchiveSearch(
        string title,
        string query,
        string sort,
        string cacheSearchText)
    {
        internetArchivePopularTab = false;
        CancelInternetArchiveLoadMore();
        internetArchiveSearchCts?.Cancel();
        internetArchiveSearchCts?.Dispose();
        internetArchiveSearchCts = new CancellationTokenSource();
        var generation = Interlocked.Increment(ref internetArchiveSearchGeneration);
        internetArchiveViewTitle = title;
        internetArchiveViewCategoryKey = null;
        internetArchiveViewResults = SearchInternetArchiveCache(cacheSearchText, 40);
        internetArchiveSearchLoading = true;
        internetArchiveError = null;
        _ = LoadInternetArchiveSearchAsync(
            title,
            query,
            sort,
            internetArchiveViewResults,
            generation,
            internetArchiveSearchCts.Token);
    }

    private async Task LoadInternetArchiveSearchAsync(
        string title,
        string query,
        string sort,
        IReadOnlyList<InternetArchiveItem> cachedResults,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await internetArchive.SearchAsync(
                query,
                sort,
                12,
                cancellationToken,
                item => PublishInternetArchiveSearchItem(item, generation, cancellationToken),
                priorityCandidateCount: 12)
                .ConfigureAwait(false);
            if (generation != internetArchiveSearchGeneration || cancellationToken.IsCancellationRequested) return;
            foreach (var item in results)
            {
                archiveSearchCache[item.Identifier] = item;
            }
            internetArchiveViewResults = MergeCachedThenFreshArchiveItems(cachedResults, results, 60);
            internetArchiveViewTitle = title;
            SaveInternetArchiveCache();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (generation != internetArchiveSearchGeneration) return;
            AepLog.Warning("[InternetArchive] Search failed.");
            internetArchiveError = "That Internet Archive search failed. Please try again.";
        }
        finally
        {
            if (generation == internetArchiveSearchGeneration) internetArchiveSearchLoading = false;
        }
    }

    private void PublishInternetArchiveSearchItem(
        InternetArchiveItem item,
        int generation,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || generation != internetArchiveSearchGeneration) return;
        lock (internetArchiveCacheSync)
        {
            if (cancellationToken.IsCancellationRequested || generation != internetArchiveSearchGeneration) return;
            archiveSearchCache[item.Identifier] = item;
            internetArchiveViewResults = MergeCachedThenFreshArchiveItems(
                internetArchiveViewResults,
                new[] { item },
                60);
        }
    }

    private void CancelInternetArchiveSearch()
    {
        Interlocked.Increment(ref internetArchiveSearchGeneration);
        internetArchiveSearchCts?.Cancel();
        internetArchiveSearchCts?.Dispose();
        internetArchiveSearchCts = null;
        internetArchiveSearchLoading = false;
    }

    private bool InternetArchiveCacheIsFreshAndComplete()
    {
        if (internetArchiveLastRefreshUtc == DateTime.MinValue ||
            DateTime.UtcNow - internetArchiveLastRefreshUtc >= InternetArchiveRefreshInterval)
            return false;
        return ArchiveCategories.All(category =>
            archiveRows.TryGetValue(category.Key, out var items) && items.Count > 0);
    }

    private IReadOnlyList<InternetArchiveItem> SearchInternetArchiveCache(string searchText, int maximum)
    {
        var terms = searchText
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return Array.Empty<InternetArchiveItem>();

        return archiveRows.Values
            .SelectMany(items => items)
            .Concat(archiveSearchCache.Values)
            .GroupBy(item => item.Identifier, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(item =>
            {
                var searchable = string.Join(
                    ' ',
                    item.Identifier,
                    item.Title,
                    item.Creator,
                    item.Description,
                    string.Join(' ', item.Episodes.Select(episode => episode.Title)));
                return terms.All(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
            })
            .OrderByDescending(item => item.Downloads)
            .Take(maximum)
            .ToArray();
    }

    private static IReadOnlyList<InternetArchiveItem> MergeFreshArchiveItems(
        IReadOnlyList<InternetArchiveItem> fresh,
        IReadOnlyList<InternetArchiveItem> cached,
        int maximum)
    {
        var result = new List<InternetArchiveItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in fresh.Concat(cached))
        {
            if (result.Count >= maximum) break;
            if (seen.Add(item.Identifier)) result.Add(item);
        }
        return result;
    }

    private static IReadOnlyList<InternetArchiveItem> MergeCachedThenFreshArchiveItems(
        IReadOnlyList<InternetArchiveItem> cached,
        IReadOnlyList<InternetArchiveItem> fresh,
        int maximum)
    {
        var freshByIdentifier = fresh.ToDictionary(item => item.Identifier, StringComparer.OrdinalIgnoreCase);
        var result = new List<InternetArchiveItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cachedItem in cached)
        {
            var item = freshByIdentifier.GetValueOrDefault(cachedItem.Identifier) ?? cachedItem;
            if (seen.Add(item.Identifier)) result.Add(item);
        }
        foreach (var item in fresh)
        {
            if (result.Count >= maximum) break;
            if (seen.Add(item.Identifier)) result.Add(item);
        }
        return result.Take(maximum).ToArray();
    }

    private void SaveInternetArchiveCache()
    {
        lock (internetArchiveCacheSync)
        {
            var categories = archiveRows.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Take(200).ToList(),
                StringComparer.Ordinal);
            var searchItems = archiveSearchCache.Values
                .OrderByDescending(item => item.PublicDate ?? DateTime.MinValue)
                .Take(300)
                .ToList();
            internetArchiveCacheStorage.Save(new(
                internetArchiveLastRefreshUtc,
                categories,
                searchItems,
                internetArchive.SnapshotNoVideoItems(),
                archiveCategoryLastPages.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal)));
        }
    }

    private static string FormatArchiveCacheAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalMinutes < 1d) return "less than a minute";
        if (age.TotalHours < 1d) return $"{Math.Max(1, (int)age.TotalMinutes)} minutes";
        if (age.TotalDays < 1d) return $"{Math.Max(1, (int)age.TotalHours)} hours";
        return $"{Math.Max(1, (int)age.TotalDays)} days";
    }

    private void DisposeInternetArchive()
    {
        internetArchiveCts?.Cancel();
        internetArchiveCts?.Dispose();
        internetArchiveCts = null;
        CancelInternetArchiveSearch();
        CancelInternetArchiveLoadMore();
        if (internetArchiveStarted) SaveInternetArchiveCache();
        internetArchive.Dispose();
    }
}

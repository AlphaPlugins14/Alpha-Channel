using System.Collections.Concurrent;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.YouTube;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Text.Json;


namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly VideoUrlResolver searchResolver = new();
    private CancellationTokenSource? youtubeSearchCts;
    private int youtubeSearchGeneration;
    private readonly TwitchChannelChecker twitchChecker = new();
    private string searchQuery = string.Empty;

    private enum VideoSearchOrder
    {
        Relevance,
        Shortest,
        Longest
    }

    private enum VideoDurationFilter
    {
        Any,
        UnderFiveMinutes,
        FiveToTwentyMinutes,
        OverTwentyMinutes
    }

    private volatile bool isSearching;

    private volatile List<VideoSearchEntry>? searchResults;

    private VideoSearchOrder youtubeSearchOrder =
        VideoSearchOrder.Relevance;

    private VideoDurationFilter youtubeDurationFilter =
        VideoDurationFilter.Any;

    // ---------------------------------------------------------
    // Shared Topics cache / Home YouTube shelf
    // ---------------------------------------------------------
    //
    // Home never performs its own YouTube searches. It selects up to five
    // enabled topics and builds its shelf entirely from the shared Browse
    // Topics cache.
    //
    private volatile bool isLoadingHomeYouTube;

    private volatile List<VideoSearchEntry>?
        homeYouTubeResults;

    private readonly Random trendingRandom =
        new();

    private readonly HashSet<string>
        homeYouTubeSelectedTopics =
            new(StringComparer.OrdinalIgnoreCase);

    //
    // Ensures startup cache preparation runs once per plugin load.
    //
    private bool topicVideoStartupRequested;

    //
    // Debounces topic changes made through Settings so several nearby changes
    // result in one cache refresh.
    //
    private int topicSettingsRefreshGeneration;

    // Browse Videos full-page discovery
    private static readonly TimeSpan BrowseVideoCacheDuration =
        TimeSpan.FromHours(3);

    private const int BrowseVideoCacheLimitPerTopic = 60;

    private volatile bool isLoadingBrowseVideos;

    private volatile Dictionary<string, List<VideoSearchEntry>>?
        browseVideoResults;

    private DateTime browseVideoCacheTime;

    //
    // Identifies the exact subscribed-topic collection used to build the
    // current Browse cache. Changing topic settings forces a refresh even
    // when it is less than three hours old; unchanged topics keep their videos.
    //
    private string? browseVideoTopicSignature;

    private int browseVideoExpectedTopicCount;

    private bool browseVideoRequested;

    private CancellationTokenSource? browseVideosCts;

    //
    // Prevents an older cancelled request from publishing over a newer
    // refresh if cancellation and completion happen at nearly the same time.
    //
    private int browseVideoLoadGeneration;

    // Youtube Trending
    // s
    private sealed record TrendingTopic(
        string Name,
        string[] SearchQueries);

    // FFXIV-specific discovery shelf.
    private volatile bool isLoadingFfxivYouTube;
    private volatile List<VideoSearchEntry>? ffxivYouTubeResults;

    private string twitchChannelInput =
        string.Empty;

    private volatile bool isCheckingTwitch;

    private volatile TwitchStreamInfo?
        twitchResult;

    private volatile string?
        twitchError;

    private volatile bool twitchResultIsOffline;

    private volatile string?
        twitchCheckedChannelName;

    private bool trendingDirty =
        true;

    private TwitchStreamDto[] trendingStreams =
        [];

    private const int MaximumFavouriteTwitchChannels =
        20;

    private static readonly TimeSpan TwitchFavouriteCacheDuration =
        TimeSpan.FromMinutes(15);

    private sealed record TwitchFavouriteStatus(
        string ChannelName,
        TwitchStreamInfo? Stream,
        bool IsOffline,
        string? Error,
        DateTime CheckedAtUtc);

    private readonly ConcurrentDictionary<string, TwitchFavouriteStatus>
        twitchFavouriteStatuses =
            new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim twitchFavouriteCheckGate =
        new(
            2,
            2);

    private volatile bool isRefreshingTwitchFavourites;

    private DateTime? twitchFavouritesLastRefreshUtc;

    private static List<VideoSearchEntry> FilterAndOrderSearchResults(
    IReadOnlyList<VideoSearchEntry> results,
    VideoSearchOrder order,
    VideoDurationFilter durationFilter)
    {
        IEnumerable<VideoSearchEntry> filtered =
            results;

        filtered =
            durationFilter switch
            {
                VideoDurationFilter.UnderFiveMinutes =>
                    filtered.Where(
                        result =>
                            result.Duration is { } duration &&
                            duration <
                            TimeSpan.FromMinutes(5)),

                VideoDurationFilter.FiveToTwentyMinutes =>
                    filtered.Where(
                        result =>
                            result.Duration is { } duration &&
                            duration >=
                            TimeSpan.FromMinutes(5) &&
                            duration <=
                            TimeSpan.FromMinutes(20)),

                VideoDurationFilter.OverTwentyMinutes =>
                    filtered.Where(
                        result =>
                            result.Duration is { } duration &&
                            duration >
                            TimeSpan.FromMinutes(20)),

                _ =>
                    filtered
            };

        filtered =
            order switch
            {
                VideoSearchOrder.Shortest =>
                    filtered
                        .OrderBy(
                            result =>
                                result.Duration ??
                                TimeSpan.MaxValue),

                VideoSearchOrder.Longest =>
                    filtered
                        .OrderByDescending(
                            result =>
                                result.Duration ??
                                TimeSpan.Zero),

                _ =>
                    filtered
            };

        return filtered.ToList();
    }

    private static string GetVideoSearchOrderName(
        VideoSearchOrder order)
    {
        return order switch
        {
            VideoSearchOrder.Shortest =>
                "Shortest",

            VideoSearchOrder.Longest =>
                "Longest",

            _ =>
                "Relevance"
        };
    }

    private static string GetVideoDurationFilterName(
        VideoDurationFilter filter)
    {
        return filter switch
        {
            VideoDurationFilter.UnderFiveMinutes =>
                "Under 5 min",

            VideoDurationFilter.FiveToTwentyMinutes =>
                "5–20 min",

            VideoDurationFilter.OverTwentyMinutes =>
                "Over 20 min",

            _ =>
                "Any"
        };
    }

    private void DrawVideoSearchFilters(
    string id,
    ref VideoSearchOrder order,
    ref VideoDurationFilter durationFilter)
    {
        var orderWidth = Ui(170f);

        var durationWidth = Ui(160f);

        var gap = NativeUi(10f);

        var controlsWidth =
            orderWidth +
            durationWidth +
            gap;

        var rightEdge =
            ImGui.GetWindowPos().X +
            ImGui.GetContentRegionMax().X;

        //
        // Keep both controls on the same row as the result count.
        //

        ImGui.SameLine();

        var currentPosition =
            ImGui.GetCursorScreenPos();

        ImGui.SetCursorScreenPos(
            new Vector2(
                rightEdge -
                controlsWidth,
                currentPosition.Y));

        ImGui.SetNextItemWidth(
            orderWidth);

        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo(
                $"##searchOrder_{id}",
                $"Order: {GetVideoSearchOrderName(order)}")))
        {
            foreach (var option in new[]
                     {
                     VideoSearchOrder.Relevance,
                     VideoSearchOrder.Shortest,
                     VideoSearchOrder.Longest
                 })
            {
                var selected =
                    order == option;

                if (ImGui.Selectable(
                        GetVideoSearchOrderName(option),
                        selected))
                {
                    order =
                        option;
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine(
            0f,
            gap);

        ImGui.SetNextItemWidth(
            durationWidth);

        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo(
                $"##searchDuration_{id}",
                $"Duration: {GetVideoDurationFilterName(durationFilter)}")))
        {
            foreach (var option in new[]
                     {
                     VideoDurationFilter.Any,
                     VideoDurationFilter.UnderFiveMinutes,
                     VideoDurationFilter.FiveToTwentyMinutes,
                     VideoDurationFilter.OverTwentyMinutes
                 })
            {
                var selected =
                    durationFilter == option;

                if (ImGui.Selectable(
                        GetVideoDurationFilterName(option),
                        selected))
                {
                    durationFilter =
                        option;
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }
    }

    private List<TrendingTopic> GetEnabledTrendingTopics()
    {
        var topics = new List<TrendingTopic>();

        // Entertainment
        if (Plugin.Cfg.TrendingGaming)
        {
            topics.Add(new(
                "Gaming",
                [
                    "trending gaming videos",
                "gaming news",
                "new game releases"
                ]));
        }

        if (Plugin.Cfg.TrendingMMORPG)
        {
            topics.Add(new(
                "MMORPG",
                [
                    "MMORPG news",
                "new MMORPG releases",
                "MMORPG gameplay"
                ]));
        }

        if (Plugin.Cfg.TrendingFinalFantasy)
        {
            topics.Add(new(
                "Final Fantasy",
                [
                    "Final Fantasy series news",
                    "Final Fantasy games",
                    "Final Fantasy gameplay"
                ]));
        }

        if (Plugin.Cfg.TrendingFfxiv)
        {
            topics.Add(new(
                "FFXIV",
                [
                    "ffxiv",
                    "ff14",
                    "final fantasy xiv"
                ]));
        }

        if (Plugin.Cfg.TrendingAnime)
        {
            topics.Add(new(
                "Anime",
                [
                    "anime trailers",
                "anime trending",
                "anime news"
                ]));
        }

        if (Plugin.Cfg.TrendingMovies)
        {
            topics.Add(new(
                "Movies",
                [
                    "movie trailers",
                "movie news",
                "best movies"
                ]));
        }

        if (Plugin.Cfg.TrendingTvShows)
        {
            topics.Add(new(
                "TV Shows",
                [
                    "new TV shows",
                "TV show trailers",
                "TV show news"
                ]));
        }

        if (Plugin.Cfg.TrendingMusic)
        {
            topics.Add(new(
                "Music",
                [
                    "new music releases",
                "music trending",
                "latest songs"
                ]));
        }

        if (Plugin.Cfg.TrendingMemes)
        {
            topics.Add(new(
                "Memes",
                [
                    "funny memes",
                "viral memes",
                "meme compilation"
                ]));
        }

        // World & Knowledge
        if (Plugin.Cfg.TrendingWildlife)
        {
            topics.Add(new(
                "Wildlife",
                [
                    "amazing wildlife documentary",
                "wildlife discoveries",
                "animal documentary"
                ]));
        }

        if (Plugin.Cfg.TrendingArchitecture)
        {
            topics.Add(new(
                "Architecture",
                [
                    "amazing architecture",
                "modern architecture design",
                "unique buildings"
                ]));
        }

        if (Plugin.Cfg.TrendingScience)
        {
            topics.Add(new(
                "Science",
                [
                    "science discoveries",
                "latest science news",
                "amazing science"
                ]));
        }

        if (Plugin.Cfg.TrendingSpace)
        {
            topics.Add(new(
                "Space",
                [
                    "space discoveries",
                "NASA news",
                "universe documentary"
                ]));
        }

        if (Plugin.Cfg.TrendingHistory)
        {
            topics.Add(new(
                "History",
                [
                    "history documentary",
                "historical discoveries",
                "ancient history"
                ]));
        }

        if (Plugin.Cfg.TrendingTechnology)
        {
            topics.Add(new(
                "Technology",
                [
                    "latest technology news",
                "new technology",
                "future technology"
                ]));
        }

        // Lifestyle
        if (Plugin.Cfg.TrendingPets)
        {
            topics.Add(new(
                "Pets",
                [
                    "cute pets",
                "funny animals",
                "adorable pets"
                ]));
        }

        if (Plugin.Cfg.TrendingFood)
        {
            topics.Add(new(
                "Food",
                [
                    "amazing food",
                "cooking videos",
                "food discoveries"
                ]));
        }

        if (Plugin.Cfg.TrendingTravel)
        {
            topics.Add(new(
                "Travel",
                [
                    "beautiful places travel",
                "travel discoveries",
                "amazing destinations"
                ]));
        }

        if (Plugin.Cfg.TrendingCars)
        {
            topics.Add(new(
                "Cars",
                [
                    "car news",
                "supercars",
                "car reviews"
                ]));
        }

        if (Plugin.Cfg.TrendingSports)
        {
            topics.Add(new(
                "Sports",
                [
                    "sports highlights",
                "sports news",
                "best sports moments"
                ]));
        }
        if (Plugin.Cfg.TrendingCartoons)
        {
            topics.Add(new(
                "Cartoons",
                [
                    "new cartoons",
            "animated shows",
            "cartoon clips"
                ]));
        }

        if (Plugin.Cfg.TrendingHorror)
        {
            topics.Add(new(
                "Horror",
                [
                    "horror movies",
            "scary videos",
            "horror stories"
                ]));
        }

        if (Plugin.Cfg.TrendingSciFi)
        {
            topics.Add(new(
                "Sci-Fi",
                [
                    "science fiction movies",
            "sci-fi news",
            "science fiction videos"
                ]));
        }

        if (Plugin.Cfg.TrendingComedy)
        {
            topics.Add(new(
                "Comedy",
                [
                    "comedy videos",
            "funny sketches",
            "stand up comedy"
                ]));
        }

        if (Plugin.Cfg.TrendingMinecraft)
        {
            topics.Add(new(
                "Minecraft",
                [
                    "Minecraft videos",
            "Minecraft builds",
            "Minecraft gameplay"
                ]));
        }

        if (Plugin.Cfg.TrendingArtsAndCrafts)
        {
            topics.Add(new(
                "Arts & Crafts",
                [
                    "arts and crafts",
            "creative craft ideas",
            "art projects"
                ]));
        }

        if (Plugin.Cfg.TrendingCosplaying)
        {
            topics.Add(new(
                "Cosplaying",
                [
                    "cosplay tutorials",
            "cosplay showcases",
            "cosplay conventions"
                ]));
        }

        if (Plugin.Cfg.TrendingDiy)
        {
            topics.Add(new(
                "DIY",
                [
                    "DIY projects",
            "DIY tutorials",
            "creative DIY ideas"
                ]));
        }

        if (Plugin.Cfg.TrendingUrbanExploration)
        {
            topics.Add(new(
                "Urban Exploration",
                [
                    "urban exploration",
            "abandoned places exploration",
            "exploring abandoned buildings"
                ]));
        }

        if (Plugin.Cfg.TrendingFashion)
        {
            topics.Add(new(
                "Fashion",
                [
                    "fashion trends",
            "fashion inspiration",
            "style ideas"
                ]));
        }

        if (Plugin.Cfg.TrendingDisney)
        {
            topics.Add(new(
                "Disney",
                [
                    "Disney news",
            "Disney movies",
            "Disney parks"
                ]));
        }

        if (Plugin.Cfg.TrendingFantasy)
        {
            topics.Add(new(
                "Fantasy",
                [
                    "fantasy movies",
            "fantasy worlds",
            "fantasy stories"
                ]));
        }

        return topics;
    }

    private int GetSubscribedTopicCount() =>
    GetEnabledTrendingTopics().Count;

    private static double GetTrendingScore(VideoSearchEntry video)
    {
        var views = video.ViewCount ?? 0;

        var viewScore = Math.Log10(
            Math.Max(views, 1));

        var ageBonus = 0d;

        if (video.UploadDate is { } uploadDate)
        {
            var ageDays =
                Math.Max(
                    0,
                    (DateTime.UtcNow - uploadDate).TotalDays);

            // Strong boost for recent uploads.
            // Falls off over roughly a month.
            ageBonus =
                Math.Max(
                    0,
                    30 - ageDays) / 30.0 * 3.0;
        }

        return viewScore + ageBonus;
    }

    private async Task LoadFeaturedSlidesAsync()
    {
        var loadedSuccessfully = false;
        try
        {
            using var listingTimeout = CancellationTokenSource.CreateLinkedTokenSource(youtubeFeedToken);
            listingTimeout.CancelAfter(TimeSpan.FromMinutes(2));
            // Fetch a few extra uploads so an entry lacking the essentials can
            // be skipped without leaving an empty carousel position.
            var uploads = await searchResolver.GetChannelUploadsAsync(
                    FeaturedChannelId, FeaturedSlideCount + 4, listingTimeout.Token,
                    priority: YouTubeRequestPriority.Manual,
                    networkTimeout: TimeSpan.FromSeconds(18))
                .ConfigureAwait(false);
            if (Volatile.Read(ref disposeStarted) != 0) return;
            var loaded = uploads.Where(IsCompleteFeaturedSlide)
                .Take(FeaturedSlideCount).ToArray();
            if (loaded.Length == 0)
                throw new InvalidOperationException("No complete channel uploads were returned.");

            // Keep previously known counts visible while fresh metadata loads.
            var cachedByUrl = featuredSlideResults.ToDictionary(
                slide => slide.Url, StringComparer.OrdinalIgnoreCase);
            var results = loaded.Select(upload =>
                cachedByUrl.TryGetValue(upload.Url, out var cached)
                    ? ApplyCachedFeaturedMetadata(upload, cached)
                    : upload).ToArray();
            featuredSlideResults = results;
            loadedSuccessfully = true;

            // Each video gets its own timeout. A slow or failed request must not
            // discard counts already returned for the other slides.
            var pending = loaded.Select((upload, index) =>
                FetchFeaturedMetadataAsync(upload, index)).ToList();
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);
                var (index, metadata) = await completed.ConfigureAwait(false);
                if (metadata is null) continue;

                results[index] = MergeFeaturedMetadata(results[index], metadata);
                featuredSlideResults = (VideoSearchEntry[])results.Clone();
            }

            if (Volatile.Read(ref disposeStarted) == 0)
                PersistFeaturedSlidesCache(results);
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[Home] Failed to load FFXIV channel videos.");
        }
        finally
        {
            featuredRetryCount = loadedSuccessfully
                ? 0
                : Math.Min(featuredRetryCount + 1, 5);
            featuredNextRefreshUtc = loadedSuccessfully
                ? DateTime.UtcNow.Add(FeaturedSlideCacheDuration)
                : featuredSlideResults.Length == 0
                    ? DateTime.UtcNow.AddSeconds(Math.Min(15 * (1 << (featuredRetryCount - 1)), 240))
                    : DateTime.UtcNow.AddMinutes(Math.Min(1 << featuredRetryCount, 30));
            featuredLoadRunning = false;
        }
    }

    private async Task<(int Index, VideoSearchEntry? Metadata)> FetchFeaturedMetadataAsync(
        VideoSearchEntry upload, int index)
    {
        try
        {
            if (Volatile.Read(ref disposeStarted) != 0) return (index, null);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(youtubeFeedToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var metadata = await searchResolver.GetVideoEntryAsync(
                    upload.Url, timeout.Token, forceRefresh: true, section: YouTube.YouTubeFetchSection.Featured)
                .WaitAsync(TimeSpan.FromSeconds(32))
                .ConfigureAwait(false);
            return (index, metadata);
        }
        catch (Exception)
        {
            
            return (index, null);
        }
    }

    private void RestoreFeaturedSlidesCache()
    {
        if (featuredCacheRestored) return;
        featuredCacheRestored = true;

        try
        {
            var cached = (Plugin.Cfg.FeaturedSlideCache ?? [])
                .Select(record => new VideoSearchEntry(
                    record.Title, record.Url, record.ChannelName,
                    record.DurationSeconds is { } seconds
                        ? TimeSpan.FromSeconds(seconds) : null,
                    record.ThumbnailUrl, record.ViewCount, record.UploadDate,
                    record.ChannelId, record.ViewCountObservedAtUnix))
                .Where(IsCompleteFeaturedSlide)
                .Take(FeaturedSlideCount)
                .ToArray();
            if (cached.Length == 0) return;

            featuredSlideResults = cached;
            var updatedAt = Plugin.Cfg.FeaturedSlideCacheUpdatedUtc;
            var expiresAt = updatedAt.Add(FeaturedSlideCacheDuration);
            if (updatedAt != default && expiresAt > DateTime.UtcNow)
                featuredNextRefreshUtc = expiresAt;
        }
        catch (Exception)
        {
            AepLog.Warning("[Home] Failed to restore featured videos.");
        }
    }

    private void PersistFeaturedSlidesCache(VideoSearchEntry[] results)
    {
        Plugin.Cfg.FeaturedSlideCache = results.Select(video =>
            new CachedBrowseVideoRecord
            {
                Title = video.Title,
                Url = video.Url,
                ChannelName = video.ChannelName,
                DurationSeconds = video.Duration?.TotalSeconds,
                ThumbnailUrl = video.ThumbnailUrl,
                ViewCount = video.ViewCount,
                ViewCountObservedAtUnix = video.ViewCountObservedAtUnix,
                UploadDate = video.UploadDate,
                ChannelId = video.ChannelId,
            }).ToList();
        Plugin.Cfg.FeaturedSlideCacheUpdatedUtc = DateTime.UtcNow;
        Plugin.Cfg.Save();
    }

    private static VideoSearchEntry ApplyCachedFeaturedMetadata(
        VideoSearchEntry upload, VideoSearchEntry cached) => upload with
    {
        Duration = upload.Duration ?? cached.Duration,
        ThumbnailUrl = FeaturedThumbnailRank(cached.ThumbnailUrl) >
            FeaturedThumbnailRank(upload.ThumbnailUrl)
                ? cached.ThumbnailUrl : upload.ThumbnailUrl,
        ViewCount = upload.ViewCount ?? cached.ViewCount,
        ViewCountObservedAtUnix = upload.ViewCount is not null ? upload.ViewCountObservedAtUnix : cached.ViewCountObservedAtUnix,
        UploadDate = upload.UploadDate ?? cached.UploadDate,
    };

    private static bool IsCompleteFeaturedSlide(VideoSearchEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.Title) &&
        VideoUrlResolver.IsYouTubeUrl(entry.Url) &&
        Uri.TryCreate(entry.ThumbnailUrl, UriKind.Absolute, out var thumbnail) &&
        thumbnail.Scheme is "https" or "http";

    private static VideoSearchEntry MergeFeaturedMetadata(VideoSearchEntry upload,
        VideoSearchEntry? metadata)
    {
        if (metadata is null) return upload;
        return upload with
        {
            Title = string.IsNullOrWhiteSpace(metadata.Title) ? upload.Title : metadata.Title,
            ChannelName = string.IsNullOrWhiteSpace(metadata.ChannelName)
                ? upload.ChannelName : metadata.ChannelName,
            Duration = metadata.Duration ?? upload.Duration,
            ThumbnailUrl = FeaturedThumbnailRank(metadata.ThumbnailUrl) >
                FeaturedThumbnailRank(upload.ThumbnailUrl)
                    ? metadata.ThumbnailUrl : upload.ThumbnailUrl,
            ViewCount = metadata.ViewCount ?? upload.ViewCount,
            ViewCountObservedAtUnix = metadata.ViewCount is not null ? metadata.ViewCountObservedAtUnix : upload.ViewCountObservedAtUnix,
            UploadDate = metadata.UploadDate ?? upload.UploadDate,
            ChannelId = metadata.ChannelId ?? upload.ChannelId,
        };
    }

    private static int FeaturedThumbnailRank(string? url)
    {
        if (url is null) return -1;
        if (url.Contains("maxres", StringComparison.OrdinalIgnoreCase)) return 5;
        if (url.Contains("hq720", StringComparison.OrdinalIgnoreCase)) return 4;
        if (url.Contains("sddefault", StringComparison.OrdinalIgnoreCase)) return 3;
        if (url.Contains("hqdefault", StringComparison.OrdinalIgnoreCase)) return 2;
        if (url.Contains("mqdefault", StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    private void SelectNewHomeYouTubeTopics(
        IEnumerable<string>? availableTopics = null)
    {
        var availableTopicSet =
            availableTopics?.ToHashSet(
                StringComparer.OrdinalIgnoreCase);

        var enabledTopics =
            GetEnabledTrendingTopics()
                .Where(
                    topic =>
                        availableTopicSet is null ||
                        availableTopicSet.Contains(
                            topic.Name))
                .Select(
                    topic =>
                        topic.Name)
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    _ =>
                        trendingRandom.Next())
                .ToList();

        homeYouTubeSelectedTopics.Clear();

        foreach (var topic in enabledTopics)
        {
            homeYouTubeSelectedTopics.Add(
                topic);
        }
    }

    private void RebuildHomeYouTubeFromBrowseCache(
        bool chooseNewTopics,
        bool selectOnlyCachedTopics = false)
    {
        RestorePersistedBrowseVideoCache();

        var cache =
            browseVideoResults;

        if (chooseNewTopics ||
            homeYouTubeSelectedTopics.Count == 0)
        {
            SelectNewHomeYouTubeTopics(
                selectOnlyCachedTopics
                    ? cache?.Keys
                    : null);
        }

        if (cache is not { Count: > 0 } ||
            homeYouTubeSelectedTopics.Count == 0)
        {
            homeYouTubeResults =
                [];

            isLoadingHomeYouTube =
                isLoadingBrowseVideos;

            return;
        }

        var candidates =
            homeYouTubeSelectedTopics
                .Where(
                    topic =>
                        cache.ContainsKey(
                            topic))
                .SelectMany(
                    topic =>
                        cache[topic])
                .Where(
                    video =>
                        !string.IsNullOrWhiteSpace(
                            video.Url))
                .GroupBy(
                    video =>
                        GetYouTubeVideoId(
                            video.Url) ??
                        video.Url,
                    StringComparer.OrdinalIgnoreCase)
                .Select(
                    group =>
                        group.First())
                .OrderByDescending(
                    GetTrendingScore)
                .ToList();

        homeYouTubeResults =
            candidates;

        isLoadingHomeYouTube =
            false;
    }

    private async Task PrepareTopicVideosForLaunchAsync(
        bool forceRefresh)
    {
        try
        {
            RestorePersistedBrowseVideoCache();

            //
            // Immediately populate Home from the previous cache if possible.
            // A stale cache remains useful while its replacement downloads.
            //
            RebuildHomeYouTubeFromBrowseCache(
                chooseNewTopics: true);

            if (homeYouTubeResults is not
                { Count: > 0 })
            {
                isLoadingHomeYouTube =
                    true;
            }

            await LoadBrowseVideosAsync(
                    forceRefresh)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[Topic Videos] Startup preparation failed.");

            isLoadingHomeYouTube =
                false;
        }
    }

    private void EnsureTopicVideoStartup()
    {
        if (topicVideoStartupRequested)
        {
            return;
        }

        topicVideoStartupRequested =
            true;

        _ =
            PrepareTopicVideosForLaunchAsync(
                forceRefresh: false);
    }

    private void NotifyTopicSettingsChanged()
    {
        //
        // Give the Home shelf a new selection immediately from whatever
        // matching cached results are currently available.
        //
        RebuildHomeYouTubeFromBrowseCache(
            chooseNewTopics: true);

        var refreshGeneration =
            Interlocked.Increment(
                ref topicSettingsRefreshGeneration);

        _ =
            RefreshTopicVideosAfterSettingsChangeAsync(
                refreshGeneration);
    }

    private async Task RefreshTopicVideosAfterSettingsChangeAsync(
        int refreshGeneration)
    {
        try
        {
            //
            // A short debounce prevents several quickly changed checkboxes
            // from starting and cancelling several complete YouTube pulls.
            //
            await Task.Delay(
                    TimeSpan.FromMilliseconds(
                        500))
                .ConfigureAwait(false);

            if (refreshGeneration !=
                topicSettingsRefreshGeneration)
            {
                return;
            }

            browseVideoRequested =
                true;

            await LoadBrowseVideosAsync(
                    forceRefresh: true)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[Topic Videos] Settings refresh failed.");
        }
    }

    private static string GetBrowseTopicSignature(
    IEnumerable<TrendingTopic> topics)
    {
        return string.Join(
            "\u001F",
            topics
                .Select(
                    topic =>
                        topic.Name)
                .OrderBy(
                    name =>
                        name,
                    StringComparer.OrdinalIgnoreCase));
    }

    private void RestorePersistedBrowseVideoCache()
    {
        if (browseVideoResults is not null)
        {
            return;
        }

        var persisted =
            Plugin.Cfg.BrowseVideoTopicCache;

        if (persisted.Count == 0 ||
            Plugin.Cfg.BrowseVideoCacheUpdatedUtc ==
            default ||
            string.IsNullOrWhiteSpace(
                Plugin.Cfg.BrowseVideoCacheTopicSignature))
        {
            return;
        }

        try
        {
            var restored =
                new Dictionary<string, List<VideoSearchEntry>>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var topic in persisted)
            {
                var videos =
                    topic.Value
                        .Where(
                            video =>
                                !string.IsNullOrWhiteSpace(
                                    video.Url))
                        .Take(BrowseVideoCacheLimitPerTopic)
                        .Select(
                            video =>
                                new VideoSearchEntry(
                                    video.Title,
                                    video.Url,
                                    video.ChannelName,
                                    video.DurationSeconds is { } seconds
                                        ? TimeSpan.FromSeconds(
                                            seconds)
                                        : null,
                                    video.ThumbnailUrl,
                                    video.ViewCount,
                                    video.UploadDate,
                                    video.ChannelId, video.ViewCountObservedAtUnix))
                        .ToList();

                if (videos.Count > 0)
                {
                    restored[topic.Key] =
                        videos;
                }
            }

            if (restored.Count == 0)
            {
                return;
            }

            browseVideoResults =
                restored;

            browseVideoCacheTime =
                Plugin.Cfg.BrowseVideoCacheUpdatedUtc;

            browseVideoTopicSignature =
                Plugin.Cfg.BrowseVideoCacheTopicSignature;
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[Browse Videos] Failed to restore cached videos.");

            Plugin.Cfg.BrowseVideoTopicCache.Clear();

            Plugin.Cfg.BrowseVideoCacheUpdatedUtc =
                default;

            Plugin.Cfg.BrowseVideoCacheTopicSignature =
                null;

            Plugin.Cfg.Save();
        }
    }

    private void PersistBrowseVideoCache()
    {
        if (browseVideoResults is not { Count: > 0 } ||
            string.IsNullOrWhiteSpace(
                browseVideoTopicSignature) ||
            browseVideoCacheTime ==
            default)
        {
            return;
        }

        Plugin.Cfg.BrowseVideoTopicCache =
            browseVideoResults.ToDictionary(
                topic =>
                    topic.Key,
                topic =>
                    topic.Value
                        .Take(BrowseVideoCacheLimitPerTopic)
                        .Select(
                            video =>
                                new CachedBrowseVideoRecord
                                {
                                    Title =
                                        video.Title,

                                    Url =
                                        video.Url,

                                    ChannelName =
                                        video.ChannelName,

                                    DurationSeconds =
                                        video.Duration?.TotalSeconds,

                                    ThumbnailUrl =
                                        video.ThumbnailUrl,

                                    ViewCount =
                                        video.ViewCount,

                                    ViewCountObservedAtUnix = video.ViewCountObservedAtUnix,

                                    UploadDate =
                                        video.UploadDate,

                                    ChannelId =
                                        video.ChannelId
                                })
                        .ToList(),
                StringComparer.OrdinalIgnoreCase);

        Plugin.Cfg.BrowseVideoCacheUpdatedUtc =
            browseVideoCacheTime;

        Plugin.Cfg.BrowseVideoCacheTopicSignature =
            browseVideoTopicSignature;

        Plugin.Cfg.Save();
    }

    private void ClearPersistedBrowseVideoCache()
    {
        Plugin.Cfg.BrowseVideoTopicCache.Clear();

        Plugin.Cfg.BrowseVideoCacheUpdatedUtc =
            default;

        Plugin.Cfg.BrowseVideoCacheTopicSignature =
            null;

        Plugin.Cfg.Save();
    }

    private static List<VideoSearchEntry> MergeBrowseTopicVideos(
        IReadOnlyList<VideoSearchEntry>? previous,
        IReadOnlyList<VideoSearchEntry> discovered)
    {
        var previousUrls = new HashSet<string>(
            previous?.Select(video => video.Url) ?? [],
            StringComparer.OrdinalIgnoreCase);
        var refreshedByUrl = discovered
            .GroupBy(video => video.Url, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var merged = new List<VideoSearchEntry>(BrowseVideoCacheLimitPerTopic);
        var includedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // New discoveries go first. Already cached videos retain their place,
        // so only entries that have been in the cache longest are evicted.
        foreach (var video in discovered)
        {
            if (!previousUrls.Contains(video.Url) && includedUrls.Add(video.Url))
                merged.Add(video);
        }

        if (previous is not null)
        {
            foreach (var video in previous)
            {
                if (!includedUrls.Add(video.Url)) continue;
                merged.Add(refreshedByUrl.GetValueOrDefault(video.Url) ?? video);
                if (merged.Count >= BrowseVideoCacheLimitPerTopic) break;
            }
        }

        return merged.Take(BrowseVideoCacheLimitPerTopic).ToList();
    }

    private async Task LoadBrowseVideosAsync(
        bool forceRefresh = false)
    {
        RestorePersistedBrowseVideoCache();

        var topics =
            GetEnabledTrendingTopics();

        var topicSignature =
            GetBrowseTopicSignature(
                topics);

        var cacheIsUsable =
            !forceRefresh &&
            browseVideoResults is { Count: > 0 } &&
            string.Equals(
                browseVideoTopicSignature,
                topicSignature,
                StringComparison.Ordinal) &&
            DateTime.UtcNow -
            browseVideoCacheTime <
            BrowseVideoCacheDuration;

        if (cacheIsUsable)
        {
            browseVideoExpectedTopicCount =
                topics.Count;

            isLoadingBrowseVideos =
                false;

            RebuildHomeYouTubeFromBrowseCache(
                chooseNewTopics:
                    homeYouTubeSelectedTopics.Count == 0);

            return;
        }

        browseVideosCts?.Cancel();

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(youtubeFeedToken);
        var token = cancellation.Token;

        browseVideosCts =
            cancellation;

        var loadGeneration =
            Interlocked.Increment(
                ref browseVideoLoadGeneration);

        isLoadingBrowseVideos =
            true;

        browseVideoExpectedTopicCount =
            topics.Count;

        var previousTopicSignature =
            browseVideoTopicSignature;

        browseVideoTopicSignature =
            topicSignature;

        try
        {
            if (topics.Count == 0)
            {
                browseVideoResults =
                    [];

                browseVideoCacheTime =
                    DateTime.UtcNow;

                ClearPersistedBrowseVideoCache();

                return;
            }

            var previousResultsMatchTopics =
                browseVideoResults is { Count: > 0 } &&
                string.Equals(
                    previousTopicSignature,
                    topicSignature,
                    StringComparison.Ordinal);

            var selectedTopicNames = topics
                .Select(topic => topic.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var workingResults = browseVideoResults is { } previousResults
                ? previousResults
                    .Where(topic => selectedTopicNames.Contains(topic.Key))
                    .ToDictionary(topic => topic.Key, topic => topic.Value,
                        StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, List<VideoSearchEntry>>(
                    StringComparer.OrdinalIgnoreCase);

            browseVideoResults =
                new Dictionary<string, List<VideoSearchEntry>>(
                    workingResults,
                    StringComparer.OrdinalIgnoreCase);

            var selectedTopics =
                topics.ToList();

            const int batchSize =
                3;

            var initialHomeTopicTarget =
                Math.Min(
                    batchSize,
                    selectedTopics.Count);

            var initialHomePublished =
                previousResultsMatchTopics ||
                homeYouTubeResults is { Count: > 0 };

            const int resultsPerTopic =
                15;

            for (var batchStart = 0;
                 batchStart < selectedTopics.Count;
                 batchStart += batchSize)
            {
                token
                    .ThrowIfCancellationRequested();

                var batch =
                    selectedTopics
                        .Skip(
                            batchStart)
                        .Take(
                            batchSize)
                        .ToList();

                var pendingSearches =
                    batch
                        .Select(
                            topic =>
                                (
                                    Topic: topic,
                                    Query:
                                        topic.SearchQueries[
                                            trendingRandom.Next(
                                                topic.SearchQueries.Length)]
                                ))
                        .Select(
                            async request =>
                            {
                                //
                                // SearchAsync always performs a fresh discovery
                                // request. Metadata enrichment remains cache-aware.
                                //
                                var searchResults =
                                    await searchResolver
                                        .SearchAsync(
                                            request.Query,
                                            resultsPerTopic,
                                            token,
                                            forceRefresh ? YouTubeRequestPriority.Manual : YouTubeRequestPriority.Background)
                                        .ConfigureAwait(false);

                                var candidates =
                                    searchResults
                                        .GroupBy(
                                            result =>
                                                result.Url,
                                            StringComparer.OrdinalIgnoreCase)
                                        .Select(
                                            group =>
                                                group.First())
                                        .Take(
                                            resultsPerTopic)
                                        .ToList();

                                return
                                    (
                                        request.Topic.Name,
                                        Results: candidates
                                    );
                            })
                        .ToArray();

                var loadedBatch =
                    await Task.WhenAll(
                            pendingSearches)
                        .ConfigureAwait(false);

                token
                    .ThrowIfCancellationRequested();

                if (loadGeneration !=
                    browseVideoLoadGeneration)
                {
                    return;
                }

                var enrichedBatch = loadedBatch.Select(topic => (topic.Name,
                    Results: topic.Results.Select(searchResolver.ApplyCachedMetadata).ToList())).ToArray();

                var updatedResults =
                    new Dictionary<string, List<VideoSearchEntry>>(
                        workingResults,
                        StringComparer.OrdinalIgnoreCase);

                foreach (var loadedTopic in
                         enrichedBatch)
                {
                    workingResults.TryGetValue(
                        loadedTopic.Name,
                        out var previousTopicVideos);
                    var merged = MergeBrowseTopicVideos(
                        previousTopicVideos,
                        loadedTopic.Results);

                    if (merged.Count > 0)
                    {
                        updatedResults[
                            loadedTopic.Name] =
                            merged;
                    }
                }

                workingResults =
                    updatedResults;

                if (Volatile.Read(ref disposeStarted) != 0 || token.IsCancellationRequested) return;
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (Volatile.Read(ref disposeStarted) != 0 || token.IsCancellationRequested || loadGeneration != browseVideoLoadGeneration) return;
                    browseVideoResults =
                        new Dictionary<string, List<VideoSearchEntry>>(
                            workingResults,
                            StringComparer.OrdinalIgnoreCase);

                    // A new installation has no persisted Topics cache. Publish
                    // Home as soon as three topic feeds have usable results rather
                    // than waiting for every subscribed topic to finish. Restrict
                    // the random Home selection to completed topics for this first
                    // publication; the final rebuild below uses the full cache.
                    if (!initialHomePublished &&
                        workingResults.Count >=
                        initialHomeTopicTarget)
                    {
                        RebuildHomeYouTubeFromBrowseCache(
                            chooseNewTopics: true,
                            selectOnlyCachedTopics: true);

                        initialHomePublished =
                            homeYouTubeResults is { Count: > 0 };
                    }
                }).ConfigureAwait(false);
            }

            if (Volatile.Read(ref disposeStarted) != 0 || token.IsCancellationRequested) return;
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0 || token.IsCancellationRequested) return;
                if (loadGeneration ==
            browseVideoLoadGeneration)
                {
                    browseVideoCacheTime =
                        DateTime.UtcNow;

                    PersistBrowseVideoCache();

                    //
                    // Every successful shared-cache rebuild gives Home a new selection.
                    //
                    RebuildHomeYouTubeFromBrowseCache(
                        chooseNewTopics: true);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal when a newer refresh or topic change replaces this load.
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[Browse Videos] Failed to load videos.");

            if (Volatile.Read(ref disposeStarted) == 0)
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (Volatile.Read(ref disposeStarted) != 0 || token.IsCancellationRequested) return;
                    if (loadGeneration ==
                            browseVideoLoadGeneration &&
                        browseVideoResults is null)
                    {
                        browseVideoResults =
                            [];
                    }
                }).ConfigureAwait(false);
        }
        finally
        {
            if (Volatile.Read(ref disposeStarted) == 0)
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (Volatile.Read(ref disposeStarted) != 0) return;
                    if (loadGeneration ==
                        browseVideoLoadGeneration)
                    {
                        isLoadingBrowseVideos =
                            false;

                        if (homeYouTubeResults is not
                            { Count: > 0 })
                        {
                            RebuildHomeYouTubeFromBrowseCache(
                                chooseNewTopics:
                                    homeYouTubeSelectedTopics.Count == 0);
                        }

                        isLoadingHomeYouTube =
                            false;
                    }
                    if (ReferenceEquals(browseVideosCts, cancellation)) browseVideosCts = null;
                }).ConfigureAwait(false);
        }
    }

    private async Task RunSearchAsync(string query)
    {
        youtubeSearchCts?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(youtubeFeedToken);
        youtubeSearchCts = cancellation;
        var token = cancellation.Token;
        var generation = ++youtubeSearchGeneration;
        try
        {
            var results = await searchResolver.SearchAsync(query, 50, token,
                YouTubeRequestPriority.Interactive).ConfigureAwait(false);
            if (token.IsCancellationRequested || Volatile.Read(ref disposeStarted) != 0) return;
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) == 0 && !token.IsCancellationRequested &&
                    generation == youtubeSearchGeneration) searchResults = results;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (Volatile.Read(ref disposeStarted) == 0) AepLog.Warning("[YouTube] Search failed.");
        }
        finally
        {
            if (Volatile.Read(ref disposeStarted) == 0)
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (Volatile.Read(ref disposeStarted) == 0 && ReferenceEquals(youtubeSearchCts, cancellation))
                    { isSearching = false; youtubeSearchCts = null; }
                }).ConfigureAwait(false);
        }
    }

    private IReadOnlyList<string> GetFavouriteTwitchChannels()
    {
        Plugin.Cfg.FavouriteTwitchChannels ??=
            [];

        return Plugin.Cfg.FavouriteTwitchChannels
            .Where(
                channel =>
                    !string.IsNullOrWhiteSpace(
                        channel))
            .Distinct(
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(
                channel =>
                    channel,
                StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool IsFavouriteTwitchChannel(
        string? channelName)
    {
        if (!TwitchChannelChecker.TryNormalizeChannelName(
                channelName,
                out var normalizedChannel,
                out _))
        {
            return false;
        }

        return Plugin.Cfg.FavouriteTwitchChannels.Any(
            savedChannel =>
                string.Equals(
                    savedChannel,
                    normalizedChannel,
                    StringComparison.OrdinalIgnoreCase));
    }

    private bool TryAddFavouriteTwitchChannel(
        string? channelName,
        out string? error)
    {
        error =
            null;

        if (!TwitchChannelChecker.TryNormalizeChannelName(
                channelName,
                out var normalizedChannel,
                out var normalizationError))
        {
            error =
                normalizationError;

            return false;
        }

        Plugin.Cfg.FavouriteTwitchChannels ??=
            [];

        if (Plugin.Cfg.FavouriteTwitchChannels.Any(
                savedChannel =>
                    string.Equals(
                        savedChannel,
                        normalizedChannel,
                        StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (Plugin.Cfg.FavouriteTwitchChannels.Count >=
            MaximumFavouriteTwitchChannels)
        {
            error =
                $"You can save up to " +
                $"{MaximumFavouriteTwitchChannels} Twitch channels.";

            return false;
        }

        Plugin.Cfg.FavouriteTwitchChannels.Add(
            normalizedChannel);

        Plugin.Cfg.FavouriteTwitchChannels =
            Plugin.Cfg.FavouriteTwitchChannels
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    savedChannel =>
                        savedChannel,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        Plugin.Cfg.Save();

        return true;
    }

    private void RemoveFavouriteTwitchChannel(
        string? channelName)
    {
        if (!TwitchChannelChecker.TryNormalizeChannelName(
                channelName,
                out var normalizedChannel,
                out _))
        {
            return;
        }

        Plugin.Cfg.FavouriteTwitchChannels ??=
            [];

        Plugin.Cfg.FavouriteTwitchChannels.RemoveAll(
            savedChannel =>
                string.Equals(
                    savedChannel,
                    normalizedChannel,
                    StringComparison.OrdinalIgnoreCase));

        Plugin.Cfg.Save();

        twitchFavouriteStatuses.TryRemove(
            normalizedChannel,
            out _);
    }

    private void ToggleFavouriteTwitchChannel(
        string? channelName)
    {
        if (IsFavouriteTwitchChannel(
                channelName))
        {
            RemoveFavouriteTwitchChannel(
                channelName);

            return;
        }

        if (!TryAddFavouriteTwitchChannel(
                channelName,
                out var error))
        {
            twitchError =
                error;

            return;
        }

        RefreshTwitchFavouritesIfStale(
            forceRefresh: true);
    }

    private void RefreshTwitchFavouritesIfStale(
        bool forceRefresh = false)
    {
        if (isRefreshingTwitchFavourites)
        {
            return;
        }

        var favourites =
            GetFavouriteTwitchChannels();

        if (favourites.Count == 0)
        {
            return;
        }

        var now =
            DateTime.UtcNow;

        var hasStaleChannel =
            favourites.Any(
                channel =>
                    !twitchFavouriteStatuses.TryGetValue(
                        channel,
                        out var status) ||
                    now -
                    status.CheckedAtUtc >=
                    TwitchFavouriteCacheDuration);

        if (!forceRefresh &&
            !hasStaleChannel)
        {
            return;
        }

        _ = RefreshTwitchFavouritesAsync(
            forceRefresh);
    }

    private async Task RefreshTwitchFavouritesAsync(bool forceRefresh)
    {
        if (isRefreshingTwitchFavourites || youtubeFeedToken.IsCancellationRequested) return;
        var path = screenController.Engine.Resources.GetLocationYTDLP();
        if (string.IsNullOrWhiteSpace(path)) { twitchError = "yt-dlp isn't downloaded yet - try again in a moment."; return; }
        var now = DateTime.UtcNow;
        var channels = GetFavouriteTwitchChannels().Where(channel => forceRefresh ||
            !twitchFavouriteStatuses.TryGetValue(channel, out var status) || now - status.CheckedAtUtc >= TwitchFavouriteCacheDuration).ToArray();
        isRefreshingTwitchFavourites = true;
        try
        {
            await Task.WhenAll(channels.Select(async channel =>
            {
                await twitchFavouriteCheckGate.WaitAsync(youtubeFeedToken).ConfigureAwait(false);
                try
                {
                    var (stream, error, offline) = await twitchChecker.CheckLiveAsync(path, channel, youtubeFeedToken).ConfigureAwait(false);
                    if (youtubeFeedToken.IsCancellationRequested || Volatile.Read(ref disposeStarted) != 0) return;
                    await Plugin.Framework.RunOnFrameworkThread(() =>
                    {
                        if (!youtubeFeedToken.IsCancellationRequested && Volatile.Read(ref disposeStarted) == 0 && IsFavouriteTwitchChannel(channel))
                            twitchFavouriteStatuses[channel] = new TwitchFavouriteStatus(channel, stream, offline, error, DateTime.UtcNow);
                    }).ConfigureAwait(false);
                }
                finally { twitchFavouriteCheckGate.Release(); }
            })).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (youtubeFeedToken.IsCancellationRequested) { }
        catch (Exception)
        { if (Volatile.Read(ref disposeStarted) == 0) AepLog.Warning("[Twitch] Favourite check failed."); }
        finally
        {
            if (Volatile.Read(ref disposeStarted) == 0)
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (Volatile.Read(ref disposeStarted) == 0)
                    { twitchFavouritesLastRefreshUtc = DateTime.UtcNow; isRefreshingTwitchFavourites = false; }
                }).ConfigureAwait(false);
        }
    }


    private bool DrawTwitchFavouriteButton(
    string id,
    string channelName,
    bool favourite,
    Vector2 size)
    {
        var clicked =
            false;

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   7f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   favourite
                       ? new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.28f)
                       : CardBg)
               .Push(
                   ImGuiCol.ButtonHovered,
                   favourite
                       ? new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.44f)
                       : CardBg)
               .Push(
                   ImGuiCol.ButtonActive,
                   AccentActive))
        {
            if (ImGui.Button(
                    $"##{id}",
                    size))
            {
                ToggleFavouriteTwitchChannel(
                    channelName);

                clicked =
                    true;
            }
        }

        var buttonMin =
            ImGui.GetItemRectMin();

        var buttonMax =
            ImGui.GetItemRectMax();

        var star =
            FontAwesomeIcon.Star.ToIconString();

        using (UiFonts.PushIcon())
        {
            var starSize =
                ImGui.CalcTextSize(
                    star);

            var starPosition =
                new Vector2(
                    buttonMin.X +
                    ((buttonMax.X -
                      buttonMin.X -
                      starSize.X) *
                     0.5f),

                    buttonMin.Y +
                    ((buttonMax.Y -
                      buttonMin.Y -
                      starSize.Y) *
                     0.5f));

            UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), starPosition, ImGui.GetColorU32(
                    favourite
                        ? Vector4.One
                        : MutedText), star);
        }

        if (ImGui.IsItemHovered())
        {
            UiFonts.SetTooltip(
                favourite
                    ? "Remove from favourites"
                    : "Add to favourites");
        }

        return clicked;
    }

    private static string FitTwitchText(
        string? text,
        float maximumWidth)
    {
        if (string.IsNullOrWhiteSpace(
                text))
        {
            return string.Empty;
        }

        var value =
            text.Trim();

        if (maximumWidth <= 0f ||
            ImGui.CalcTextSize(
                value).X <=
            maximumWidth)
        {
            return value;
        }

        const string suffix =
            "...";

        var low =
            0;

        var high =
            value.Length;

        while (low < high)
        {
            var middle =
                (low +
                 high +
                 1) /
                2;

            var candidate =
                value[..middle] +
                suffix;

            if (ImGui.CalcTextSize(
                    candidate).X <=
                maximumWidth)
            {
                low =
                    middle;
            }
            else
            {
                high =
                    middle -
                    1;
            }
        }

        return value[..low] +
               suffix;
    }

    private static string PrepareTwitchDisplayText(
    string? text)
    {
        if (string.IsNullOrWhiteSpace(
                text))
        {
            return string.Empty;
        }

        var output =
            new System.Text.StringBuilder(
                text.Length);

        var replacedSymbol =
            false;

        for (var index = 0;
             index < text.Length;
             index++)
        {
            var character =
                text[index];

            var isSurrogatePair =
                char.IsHighSurrogate(
                    character) &&
                index + 1 <
                text.Length &&
                char.IsLowSurrogate(
                    text[index + 1]);

            var isUnsupportedSymbol =
                isSurrogatePair ||
                character is >= '\u2600' and <= '\u27BF';

            if (isUnsupportedSymbol)
            {
                if (!replacedSymbol &&
                    output.Length > 0 &&
                    output[^1] != ' ')
                {
                    output.Append(
                        " • ");
                }

                replacedSymbol =
                    true;

                if (isSurrogatePair)
                {
                    index++;
                }

                continue;
            }

            // Remove emoji presentation selectors and joiners.
            if (character is '\uFE0E' or
                '\uFE0F' or
                '\u200D')
            {
                continue;
            }

            output.Append(
                character);

            if (!char.IsWhiteSpace(
                    character))
            {
                replacedSymbol =
                    false;
            }
        }

        return output
            .ToString()
            .Trim()
            .Trim('•')
            .Trim();
    }

    private static string FormatTwitchFavouriteCheckAge(
        DateTime checkedAtUtc)
    {
        var age =
            DateTime.UtcNow -
            checkedAtUtc;

        if (age <
            TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age <
            TimeSpan.FromHours(1))
        {
            var minutes =
                Math.Max(
                    1,
                    (int)age.TotalMinutes);

            return
                $"{minutes} min ago";
        }

        var hours =
            Math.Max(
                1,
                (int)age.TotalHours);

        return
            $"{hours} hr ago";
    }

    private async Task RunTwitchCheckAsync(
    string input)
    {
        if (!TwitchChannelChecker.TryNormalizeChannelName(
                input,
                out var channelName,
                out var normalizationError))
        {
            twitchResult =
                null;

            twitchError =
                normalizationError;

            twitchResultIsOffline =
                false;

            twitchCheckedChannelName =
                null;

            isCheckingTwitch =
                false;

            return;
        }

        twitchChannelInput =
            channelName;

        twitchCheckedChannelName =
            channelName;

        var ytdlpPath =
            screenController.Engine.Resources
                .GetLocationYTDLP();

        if (ytdlpPath is null)
        {
            twitchError =
                "yt-dlp isn't downloaded yet - try again in a moment.";

            twitchResultIsOffline =
                false;

            isCheckingTwitch =
                false;

            return;
        }

        try
        {
            var (stream, error, offline) = await twitchChecker.CheckLiveAsync(ytdlpPath, channelName, youtubeFeedToken).ConfigureAwait(false);
            if (youtubeFeedToken.IsCancellationRequested || Volatile.Read(ref disposeStarted) != 0) return;
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (youtubeFeedToken.IsCancellationRequested || Volatile.Read(ref disposeStarted) != 0 || twitchCheckedChannelName != channelName) return;
                twitchResult = stream; twitchResultIsOffline = offline; twitchError = offline ? null : error;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (youtubeFeedToken.IsCancellationRequested) { }
        catch (Exception)
        {
            if (Volatile.Read(ref disposeStarted) == 0)
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (Volatile.Read(ref disposeStarted) == 0 && twitchCheckedChannelName == channelName)
                        twitchError = "The channel status could not be checked.";
                }).ConfigureAwait(false);
            
        }
        finally
        {
            if (Volatile.Read(ref disposeStarted) == 0)
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (Volatile.Read(ref disposeStarted) == 0 && twitchCheckedChannelName == channelName) isCheckingTwitch = false;
                }).ConfigureAwait(false);
        }
    }
}

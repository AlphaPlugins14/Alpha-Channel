using System.Text.Json;
using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.YouTube;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private enum UnifiedSearchProvider
    {
        YouTube,
        Dailymotion,
        InternetArchive,
    }

    private sealed record UnifiedSearchResult(
        UnifiedSearchProvider Provider,
        VideoSearchEntry? Video,
        InternetArchiveItem? Archive)
    {
        public string Title => Video?.Title ?? Archive?.Title ?? string.Empty;
        public string Creator => Video?.ChannelName ?? Archive?.Creator ?? string.Empty;
        public string? ThumbnailUrl => Video?.ThumbnailUrl ?? Archive?.ThumbnailUrl;
        public TimeSpan? Duration => Video?.Duration ?? Archive?.Duration;
        public string ExactKey => Provider == UnifiedSearchProvider.InternetArchive
            ? Archive?.Identifier ?? string.Empty
            : Video?.Url ?? string.Empty;
    }

    private CancellationTokenSource? unifiedSearchCts;
    private string unifiedSearchQuery = string.Empty;
    private string unifiedSearchCompletedQuery = string.Empty;
    private volatile IReadOnlyList<VideoSearchEntry> unifiedYouTubeResults = Array.Empty<VideoSearchEntry>();
    private volatile IReadOnlyList<VideoSearchEntry> unifiedDailymotionResults = Array.Empty<VideoSearchEntry>();
    private volatile IReadOnlyList<InternetArchiveItem> unifiedArchiveResults = Array.Empty<InternetArchiveItem>();
    private volatile bool unifiedYouTubeSearching;
    private volatile bool unifiedDailymotionSearching;
    private volatile bool unifiedArchiveSearching;
    private volatile string? unifiedYouTubeError;
    private volatile string? unifiedDailymotionError;
    private volatile string? unifiedArchiveError;
    private int unifiedSearchGeneration;
    private bool unifiedShowYouTube = true;
    private bool unifiedShowDailymotion = true;
    private bool unifiedShowArchive = true;
    private VideoSearchOrder unifiedSearchOrder = VideoSearchOrder.Relevance;
    private VideoDurationFilter unifiedDurationFilter = VideoDurationFilter.Any;

    private void OpenUnifiedVideoSearch(string query)
    {
        currentPage = HomePage.UnifiedSearch;
        unifiedSearchQuery = query.Trim();
        StartUnifiedSearch();
    }

    private bool IsUnifiedSearchRunning =>
        unifiedYouTubeSearching || unifiedDailymotionSearching || unifiedArchiveSearching;

    private List<UnifiedSearchResult> BuildUnifiedSearchResults()
    {
        IEnumerable<UnifiedSearchResult> youtube = unifiedShowYouTube
            ? unifiedYouTubeResults
                .Where(video => !IsYouTubeChannelHidden(video))
                .GroupBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
                .Select(group => new UnifiedSearchResult(UnifiedSearchProvider.YouTube, group.First(), null))
            : [];
        IEnumerable<UnifiedSearchResult> dailymotion = unifiedShowDailymotion
            ? unifiedDailymotionResults
                .GroupBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
                .Select(group => new UnifiedSearchResult(UnifiedSearchProvider.Dailymotion, group.First(), null))
            : [];
        IEnumerable<UnifiedSearchResult> archive = unifiedShowArchive
            ? unifiedArchiveResults
                .GroupBy(item => item.Identifier, StringComparer.OrdinalIgnoreCase)
                .Select(group => new UnifiedSearchResult(UnifiedSearchProvider.InternetArchive, null, group.First()))
            : [];

        var providerLists = new[] { youtube.ToList(), dailymotion.ToList(), archive.ToList() };
        var merged = new List<UnifiedSearchResult>();
        for (var index = 0; providerLists.Any(list => index < list.Count); index++)
            foreach (var list in providerLists)
                if (index < list.Count) merged.Add(list[index]);

        merged = unifiedDurationFilter switch
        {
            VideoDurationFilter.UnderFiveMinutes => merged.Where(item => item.Duration is { } value && value < TimeSpan.FromMinutes(5)).ToList(),
            VideoDurationFilter.FiveToTwentyMinutes => merged.Where(item => item.Duration is { } value && value >= TimeSpan.FromMinutes(5) && value <= TimeSpan.FromMinutes(20)).ToList(),
            VideoDurationFilter.OverTwentyMinutes => merged.Where(item => item.Duration is { } value && value > TimeSpan.FromMinutes(20)).ToList(),
            _ => merged,
        };
        return unifiedSearchOrder switch
        {
            VideoSearchOrder.Shortest => merged.OrderBy(item => item.Duration ?? TimeSpan.MaxValue).ToList(),
            VideoSearchOrder.Longest => merged.OrderByDescending(item => item.Duration ?? TimeSpan.Zero).ToList(),
            _ => merged,
        };
    }

    private void PlayUnifiedResult(UnifiedSearchResult result)
    {
        if (result.Archive is { } archive) PlayInternetArchiveItem(archive);
        else if (result.Video is { } videoResult)
            HandlePlayNow(new VideoQueueEntry(videoResult.Url, videoResult.Title, videoResult.ChannelName, videoResult.Duration, videoResult.ThumbnailUrl));
    }

    private void StartUnifiedSearch()
    {
        var query = unifiedSearchQuery.Trim();
        if (query.Length == 0) return;
        EnsureInternetArchiveStarted();
        unifiedSearchCts?.Cancel();
        unifiedSearchCts = CancellationTokenSource.CreateLinkedTokenSource(youtubeFeedToken);
        var generation = Interlocked.Increment(ref unifiedSearchGeneration);
        unifiedSearchCompletedQuery = query;
        unifiedYouTubeResults = SearchUnifiedYouTubeCache(query, 50);
        unifiedDailymotionResults = Array.Empty<VideoSearchEntry>();
        unifiedArchiveResults = SearchInternetArchiveCache(query, 40);
        unifiedYouTubeError = unifiedDailymotionError = unifiedArchiveError = null;
        unifiedYouTubeSearching = unifiedDailymotionSearching = unifiedArchiveSearching = true;
        var allowYouTube = youtubeSearchUsage.TrySearch();
        if (!allowYouTube)
        { unifiedYouTubeSearching = false; unifiedYouTubeError = "Please wait one minute before searching YouTube again."; }
        _ = RunUnifiedSearchAsync(query, generation, unifiedSearchCts, allowYouTube);
    }

    private async Task RunUnifiedSearchAsync(string query, int generation, CancellationTokenSource cancellation, bool allowYouTube)
    {
        using (cancellation)
        {
            var token = cancellation.Token;
            try
            {
                await Task.WhenAll(allowYouTube ? SearchUnifiedYouTubeAsync(query, generation, token) : Task.CompletedTask,
                    SearchUnifiedDailymotionAsync(query, generation, token), SearchUnifiedArchiveAsync(query, generation, token)).ConfigureAwait(false);
            }
            finally
            {
                if (Volatile.Read(ref disposeStarted) == 0)
                    await Plugin.Framework.RunOnFrameworkThread(() =>
                    {
                        if (Volatile.Read(ref disposeStarted) == 0 && ReferenceEquals(unifiedSearchCts, cancellation)) unifiedSearchCts = null;
                    }).ConfigureAwait(false);
            }
        }
    }

    private async Task SearchUnifiedYouTubeAsync(string query, int generation, CancellationToken token)
    {
        try
        {
            var cached = unifiedYouTubeResults;
            var results = await searchResolver.SearchAsync(
                query, 50, token, YouTubeRequestPriority.Interactive).ConfigureAwait(false);
            await CommitUnifiedSearchAsync(generation, token, () =>
            {
                unifiedYouTubeResults = cached.Concat(results)
                    .GroupBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .Take(75)
                    .ToArray();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            AepLog.Warning("[UnifiedSearch] YouTube failed.");
            await CommitUnifiedSearchAsync(generation, token, () => unifiedYouTubeError = "Search failed").ConfigureAwait(false);
        }
        finally { await CommitUnifiedSearchAsync(generation, token, () => unifiedYouTubeSearching = false, allowCancelled: true).ConfigureAwait(false); }
    }

    private IReadOnlyList<VideoSearchEntry> SearchUnifiedYouTubeCache(string query, int maximum)
    {
        RestorePersistedBrowseVideoCache();
        var terms = query.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return Array.Empty<VideoSearchEntry>();

        try
        {
            var cached = Enumerable.Empty<VideoSearchEntry>();
            if (browseVideoResults is { } topics) cached = cached.Concat(topics.Values.SelectMany(items => items));
            if (subscriptionVideoResults is { } subscriptions) cached = cached.Concat(subscriptions.Values.SelectMany(items => items));
            if (homeYouTubeResults is { } home) cached = cached.Concat(home);
            if (ffxivYouTubeResults is { } ffxiv) cached = cached.Concat(ffxiv);
            if (favouriteVideoResults is { } favourites) cached = cached.Concat(favourites);

            return cached
                .Where(item => terms.All(term =>
                    item.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    item.ChannelName.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(maximum)
                .ToArray();
        }
        catch (InvalidOperationException)
        {
            // A discovery feed may publish a replacement cache while this snapshot is being made.
            // Live provider results still complete the search, so retrying on the UI thread is unnecessary.
            return Array.Empty<VideoSearchEntry>();
        }
    }

    private async Task SearchUnifiedDailymotionAsync(string query, int generation, CancellationToken token)
    {
        try
        {
            var results = (await dailymotionClient.SearchAsync(query, token).ConfigureAwait(false)).Select(v => v.ToEntry()).ToList();
            await CommitUnifiedSearchAsync(generation, token, () => unifiedDailymotionResults = results).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            AepLog.Warning("[UnifiedSearch] Dailymotion failed.");
            await CommitUnifiedSearchAsync(generation, token, () => unifiedDailymotionError = "Search failed").ConfigureAwait(false);
        }
        finally { await CommitUnifiedSearchAsync(generation, token, () => unifiedDailymotionSearching = false, allowCancelled: true).ConfigureAwait(false); }
    }

    private async Task SearchUnifiedArchiveAsync(string query, int generation, CancellationToken token)
    {
        try
        {
            var cached = unifiedArchiveResults;
            var results = await internetArchive.SearchAsync(
                InternetArchiveClient.BuildTextSearchQuery(query),
                "downloads desc",
                24,
                token,
                item => PublishUnifiedArchiveResult(item, generation, token),
                priorityCandidateCount: 12).ConfigureAwait(false);
            await CommitUnifiedSearchAsync(generation, token, () =>
            {
                foreach (var item in results) archiveSearchCache[item.Identifier] = item;
                unifiedArchiveResults = cached.Concat(results)
                    .GroupBy(item => item.Identifier, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First()).ToArray();
                SaveInternetArchiveCache();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            AepLog.Warning("[UnifiedSearch] Internet Archive failed.");
            await CommitUnifiedSearchAsync(generation, token, () => unifiedArchiveError = "Search failed").ConfigureAwait(false);
        }
        finally { await CommitUnifiedSearchAsync(generation, token, () => unifiedArchiveSearching = false, allowCancelled: true).ConfigureAwait(false); }
    }

    private Task CommitUnifiedSearchAsync(int generation, CancellationToken token, Action publish, bool allowCancelled = false)
    {
        if (Volatile.Read(ref disposeStarted) != 0 || (!allowCancelled && token.IsCancellationRequested)) return Task.CompletedTask;
        return Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (Volatile.Read(ref disposeStarted) == 0 && (allowCancelled || !token.IsCancellationRequested) && generation == unifiedSearchGeneration)
                publish();
        });
    }

    private void PublishUnifiedArchiveResult(InternetArchiveItem item, int generation, CancellationToken token)
    {
        _ = PublishAsync();
        async Task PublishAsync()
        {
            try
            {
                await CommitUnifiedSearchAsync(generation, token, () =>
                {
                    archiveSearchCache[item.Identifier] = item;
                    unifiedArchiveResults = unifiedArchiveResults.Append(item)
                        .GroupBy(result => result.Identifier, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First()).ToArray();
                }).ConfigureAwait(false);
            }
            catch (Exception)
            { if (Volatile.Read(ref disposeStarted) == 0) { } }
        }
    }
}

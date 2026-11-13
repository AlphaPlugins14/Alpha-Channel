using YoutubeExplode;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;
using YoutubeExplode.Channels;
using AlphaChannel.Plugin.YouTube;

namespace AlphaChannel.Plugin.Video;

internal sealed record VideoMetadata(string Title, string Source, TimeSpan? Duration, string? ThumbnailUrl);

internal sealed record ResolvedStream(string VideoUrl, string? AudioUrl, string QualityLabel);

// Uses YoutubeExplode for YouTube search, metadata, and stream-resolution helpers.
// The main video playback path uses mpv and yt-dlp through VideoEngine.
//
// YouTube only serves muxed (single-file, audio+video together) streams up to 720p - anything
// higher only exists as separate video-only and audio-only streams. This resolves adaptive
// streams first (best video-only <= the requested cap, paired with the best audio-only track)
// and only falls back to a muxed stream if no adaptive pair is available, so quality above 720p
// is actually reachable rather than the dropdown just listing numbers that silently clamp.
internal sealed class VideoUrlResolver
{
    // YouTube's own practical ceiling for muxed streams. Below this, always prefer muxed even if
    // a particular video's own muxed ceiling happens to be lower than what adaptive could offer
    // at the same cap - the adaptive (external audio-file) path is new and its track-selection
    // behavior under Wine isn't fully verified yet, so it's scoped to only the quality tier that
    // has no muxed option at all, rather than opportunistically replacing muxed playback that
    // was already known to work.
    private const int MuxedCeiling = 720;

    private readonly YoutubeClient youtube = new();
    private static readonly HttpClient noCookieMetadataHttp = new(new YouTubeGuestNoCookieHandler(
        new HttpClientHandler { UseCookies = false }));
    private static readonly YoutubeClient noCookieMetadataYoutube = new(noCookieMetadataHttp);
    private static int noCookieMetadataConfirmed;

    private readonly YouTubeRequestScheduler youtubeRequests = YouTubeRequestScheduler.Shared;
    private static readonly HttpClient playbackMetadataHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = 64 * 1024
    };
    private static readonly YouTubePlaybackMetadataFetcher playbackMetadata = new(playbackMetadataHttp,
        token => YouTubeRequestScheduler.Shared.WaitAsync(YouTubeRequestIdentity.Guest,
            YouTubeRequestPriority.Interactive, 1, token), reportFailure: AepLog.YouTubeFetchFailure,
        isPaused: () => YouTubeRequestScheduler.Shared.GetPausedUntilUtc(YouTubeRequestIdentity.Guest) > DateTime.UtcNow);

    private static string MetadataKey(string url) => VideoId.TryParse(url)?.Value ?? url;
    private bool TryGetCachedVideo(string url, out VideoSearchEntry? entry) =>
        YouTubeMetadataCache.Shared.TryGet(MetadataKey(url), out entry);
    public bool TryGetVideoMetadata(string url, out VideoSearchEntry? entry) => TryGetCachedVideo(url, out entry);
    public VideoSearchEntry ApplyCachedMetadata(VideoSearchEntry entry) =>
        TryGetCachedVideo(entry.Url, out var cached) && cached is not null
            ? YouTubeMetadataCache.Merge(entry, cached) with { Url = entry.Url } : entry;
    public void RememberVideoMetadata(IEnumerable<VideoSearchEntry> entries)
    {
        foreach (var entry in entries)
            if (IsYouTubeUrl(entry.Url))
                YouTubeMetadataCache.Shared.Store(MetadataKey(entry.Url), entry, replace: false);
    }
    public static bool IsYouTubeUrl(string url) => VideoId.TryParse(url) is not null;

    public static bool IsDailymotionUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            return false;

        var host = uri.Host;
        return host.Equals("dai.ly", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("dailymotion.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".dailymotion.com", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<(ResolvedStream? Stream, string? Error)> ResolveAsync(string url, int maxHeight,
        CancellationToken token)
    {
        try
        {
            youtubeRequests.RecordPlayback(url, YouTubeRequestIdentity.Guest);
            var manifest = await youtube.Videos.Streams.GetManifestAsync(url, token).ConfigureAwait(false);

            var video = manifest.GetVideoOnlyStreams()
                .Where(stream => stream.VideoQuality.MaxHeight <= maxHeight)
                .OrderByDescending(stream => stream.VideoQuality.MaxHeight)
                .ThenByDescending(stream => stream.Bitrate)
                .FirstOrDefault();
            var audio = manifest.GetAudioOnlyStreams().OrderByDescending(stream => stream.Bitrate).FirstOrDefault();

            var muxed = manifest.GetMuxedStreams().Where(stream => stream.VideoQuality.MaxHeight <= maxHeight)
                .OrderByDescending(stream => stream.VideoQuality.MaxHeight).FirstOrDefault();

            // Only reach for adaptive when the requested quality is above what muxed can ever
            // offer - not just above what this particular video's muxed ceiling happens to be.
            if (false && maxHeight > MuxedCeiling && video is not null && audio is not null)
            {
                var label = video.VideoQuality.Label;
                
                return (new ResolvedStream(video.Url, audio.Url, label), null);
            }

            muxed ??= manifest.GetMuxedStreams().OrderBy(stream => stream.VideoQuality.MaxHeight).FirstOrDefault();
            if (muxed is not null)
            {
                
                return (new ResolvedStream(muxed.Url, null, muxed.VideoQuality.Label), null);
            }

            return (null, "No playable stream found for this video.");
        }
        catch (OperationCanceledException)
        {
            return (null, null);
        }
        catch (Exception exception)
        {
            youtubeRequests.ReportFailure(YouTubeRequestIdentity.Guest, exception.Message);
            return (null, $"Failed to resolve YouTube URL: {exception.Message}");
        }
    }

    // ---------------------------------------------------------
    // Channel uploads
    //
    // Used by AlphaChannel's plugin-managed subscriptions.
    // Takes a stable YouTube channel ID and returns recent
    // uploads. Visible cards resolve missing metadata through the shared cache.
    // ---------------------------------------------------------

    public async Task<List<VideoSearchEntry>> GetChannelUploadsAsync(
    string channelId,
    int maxResults,
    CancellationToken token,
    YouTubeRequestPriority priority = YouTubeRequestPriority.Background,
    TimeSpan? networkTimeout = null)
    {
        var results =
            new List<VideoSearchEntry>();

        IDisposable? gateLease = null;
        CancellationTokenSource? networkTimeoutCts = null;

        try
        {
            var parsedChannelId =
                ChannelId.TryParse(
                    channelId);

            if (parsedChannelId is null)
            {
                return results;
            }

            gateLease = await youtubeRequests.WaitAsync(
                YouTubeRequestIdentity.Guest, priority, 4, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            // Queue pacing is separate from the time allowed for YouTube to respond.
            if (networkTimeout is { } timeout)
            {
                networkTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                networkTimeoutCts.CancelAfter(timeout);
                token = networkTimeoutCts.Token;
            }

            await foreach (
                var upload in youtube.Channels
                    .GetUploadsAsync(
                        parsedChannelId.Value,
                        token)
                    .ConfigureAwait(false))
            {
                if (results.Count >= maxResults)
                {
                    break;
                }

                try
                {
                    var thumbnail =
                        upload.Thumbnails
                            .OrderByDescending(
                                t => t.Resolution.Area)
                            .FirstOrDefault();

                    results.Add(
                        new VideoSearchEntry(
                            upload.Title,
                            upload.Url,
                            upload.Author.ChannelTitle,
                            upload.Duration,
                            thumbnail?.Url,
                            ChannelId: upload.Author.ChannelId.Value));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    youtubeRequests.ReportFailure(YouTubeRequestIdentity.Guest, exception.Message);
                    AepLog.Warning(
                        "[Subscriptions] Failed to process upload [redacted].");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation.
        }
        catch (Exception exception)
        {
            youtubeRequests.ReportFailure(YouTubeRequestIdentity.Guest, exception.Message);
            AepLog.Warning(
                "[Subscriptions] Failed to load channel [redacted].");
        }
        finally
        {
            networkTimeoutCts?.Dispose();
            gateLease?.Dispose();
        }

        return results;
    }

    public async Task<string?> GetChannelNameAsync(
    string channelId,
    CancellationToken token)
    {
        IDisposable? gateLease = null;

        try
        {
            var parsedChannelId =
                ChannelId.TryParse(
                    channelId);

            if (parsedChannelId is null)
            {
                return null;
            }

            gateLease = await youtubeRequests.WaitAsync(
                YouTubeRequestIdentity.Guest, YouTubeRequestPriority.Manual, 1, token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            var channel =
                await youtube.Channels
                    .GetAsync(
                        parsedChannelId.Value,
                        token)
                    .ConfigureAwait(false);

            return channel.Title;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            youtubeRequests.ReportFailure(YouTubeRequestIdentity.Guest, exception.Message);
            {}

            return null;
        }
        finally
        {
            gateLease?.Dispose();
        }
    }




    public async Task<List<VideoSearchEntry>> SearchLatestAggregatedAsync(
    IReadOnlyList<string> queries,
    int maxResults,
    CancellationToken token)
    {
        // ---------------------------------------------------------
        // Run all search queries concurrently.
        // ---------------------------------------------------------

        var searchTasks =
            queries.Select(
                async query =>
                {
                    try
                    {
                        return await SearchAsync(
                                query,
                                6,
                                token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return [];
                    }
                    catch (Exception)
                    {
                        AepLog.Warning(
                            "[Video] FFXIV aggregate search failed for '[redacted]'.");

                        return [];
                    }
                })
            .ToArray();

        var searchResults =
            await Task.WhenAll(searchTasks)
                .ConfigureAwait(false);

        // ---------------------------------------------------------
        // Deduplicate results shared between queries.
        // ---------------------------------------------------------

        var candidates =
            new Dictionary<string, VideoSearchEntry>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var resultSet in searchResults)
        {
            foreach (var result in resultSet)
            {
                candidates.TryAdd(
                    result.Url,
                    result);
            }
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        // Discovery returns immediately. Visible cards request missing details.
        return candidates.Values.Select(ApplyCachedMetadata)
            .OrderByDescending(
                item =>
                    item.UploadDate ??
                    DateTimeOffset.MinValue)
            .Take(maxResults)
            .ToList();
    }


    public async Task<VideoMetadata?> ResolveMetadataAsync(
    string url,
    CancellationToken token)
    {
        if (VideoId.TryParse(url) is { } id)
        {
            if (TryGetCachedVideo(url,out var local) && local is not null && YouTubeMetadataCache.HasPlaybackMetadata(local))
            {
                SharedYouTubeCache.Current?.Observe(local, YouTubeFetchSection.Playback);
                return new VideoMetadata(local.Title,local.ChannelName,local.Duration,local.ThumbnailUrl);
            }
            if (SharedYouTubeCache.Current is { } shared)
            {
                await shared.LookupAsync(id.Value,token).ConfigureAwait(false);
                if (TryGetCachedVideo(url,out var known) && known is not null && YouTubeMetadataCache.HasPlaybackMetadata(known))
                    return new VideoMetadata(known.Title,known.ChannelName,known.Duration,known.ThumbnailUrl);
            }
            var presentation = await playbackMetadata.GetAsync(id.Value, token, YouTubeFetchSection.Playback).ConfigureAwait(false);
            if (presentation is not null && YouTubeMetadataCache.HasPlaybackMetadata(presentation))
                return new VideoMetadata(presentation.Title, presentation.ChannelName,
                    presentation.Duration, presentation.ThumbnailUrl);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        VideoSearchEntry? entry;
        try
        {
            entry = await GetVideoEntryAsync(url, timeout.Token, priority: YouTubeRequestPriority.Manual, section: YouTubeFetchSection.Playback)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        {
            AepLog.YouTubeFetchFailure(YouTubeFetchDiagnostic.Classify(YouTubeFetchSection.Playback, YouTubeFetchData.FullMetadata, YouTubeFetchMethod.FullDetails, error));
            entry = TryGetCachedVideo(url, out var partial) ? partial : null;
        }
        return entry is null ? null : new VideoMetadata(entry.Title, entry.ChannelName,
            entry.Duration, entry.ThumbnailUrl);
    }

#if DEBUG
    internal async Task<string?> GetYouTubeAdminTitleAsync(string id, CancellationToken token)
    {
        var url = "https://www.youtube.com/watch?v=" + id;
        if (TryGetCachedVideo(url, out var cached) && cached is not null && YouTubeMetadataCache.HasTitle(cached.Title))
            return cached.Title;
        var presentation = await playbackMetadata.GetAsync(id, token, YouTubeFetchSection.Admin).ConfigureAwait(false);
        return presentation is not null && YouTubeMetadataCache.HasTitle(presentation.Title) ? presentation.Title : null;
    }
#endif

    internal async Task<VideoSearchEntry?> GetVisibleCardEntryAsync(string url, CancellationToken token, bool presentationOnly = false,
        YouTubeFetchSection section = YouTubeFetchSection.Background)
    {
        // Popular rankings supply IDs. Recover presentation independently of
        // player/engagement lookups, which can fail for otherwise playable videos.
        if (presentationOnly && (!TryGetCachedVideo(url, out var known) || known is null || !YouTubeMetadataCache.HasTitle(known.Title)) &&
            VideoId.TryParse(url) is { } id)
        {
            var presentation = await playbackMetadata.GetAsync(id.Value, token, section).ConfigureAwait(false);
            if (presentation is not null && YouTubeMetadataCache.HasTitle(presentation.Title)) return presentation;
        }
        // Popular obtains engagement counts through the shared Worker claim.
        // Its ordinary card queue must not launch an uncoordinated full lookup.
        if (presentationOnly) return TryGetCachedVideo(url, out var presentationCache) ? presentationCache : null;
        // Still consult the shared cache while YouTube is paused or budget-limited.
        // Defer an actual YouTube miss without consuming a short fetch deadline.
        if (VideoId.TryParse(url) is { } cacheId && SharedYouTubeCache.Current is { } shared)
        {
            await shared.LookupAsync(cacheId.Value,token).ConfigureAwait(false);
            if (TryGetCachedVideo(url,out var sharedEntry) && sharedEntry is not null && YouTubeMetadataCache.IsComplete(sharedEntry))
                return sharedEntry;
        }
        var resume=youtubeRequests.GuestMetadataResumeUtc();
        if (resume>DateTime.UtcNow) throw new YouTubeMetadataDeferredException(resume);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            return await GetVideoEntryAsync(url, deadline.Token, priority: YouTubeRequestPriority.Manual, section: section).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        {
            AepLog.YouTubeFetchFailure(YouTubeFetchDiagnostic.Classify(section, YouTubeFetchData.FullMetadata, YouTubeFetchMethod.FullDetails, error));
            return TryGetCachedVideo(url, out var partial) ? partial : null;
        }
    }

    public async Task<VideoSearchEntry?> GetVideoEntryAsync(string url, CancellationToken token,
        bool forceRefresh = false, YouTubeRequestPriority priority = YouTubeRequestPriority.Background,
        YouTubeFetchSection section = YouTubeFetchSection.Background, YouTubeFetchData data = YouTubeFetchData.FullMetadata,
        bool bypassSharedCache = false)
    {
        token.ThrowIfCancellationRequested();
        bool Ready(VideoSearchEntry entry) => data == YouTubeFetchData.ViewCount ? entry.ViewCount is not null : YouTubeMetadataCache.IsComplete(entry);
        if (!forceRefresh && TryGetCachedVideo(url, out var cached) && cached is not null && Ready(cached))
        {
            if (!bypassSharedCache) SharedYouTubeCache.Current?.Observe(cached, section);
            return cached;
        }
        return await YouTubeMetadataCache.Shared.GetOrCreateAsync(MetadataKey(url), async request =>
        {
            if (!forceRefresh && TryGetCachedVideo(url, out var ready) && ready is not null && Ready(ready)) return ready;
            var shared = bypassSharedCache ? null : SharedYouTubeCache.Current;
            SharedYouTubeCache.FetchPermission permission = new(true);
            if (shared is not null && VideoId.TryParse(url) is { } sharedId)
            {
                TryGetCachedVideo(url,out var local);
                permission = await shared.PrepareFetchAsync(sharedId.Value,local,request.Token).ConfigureAwait(false);
                if (!permission.Fetch) return TryGetCachedVideo(url,out var knownShared) ? knownShared : null;
            }
            VideoSearchEntry? fetched = null;
            var stage = YouTubeFetchStage.SchedulerWait;
            void Track(YouTubeFetchStage current)
            {
                stage = current;
                request.SetCancellationContext(error => new YouTubeFetchCancelledException(
                    YouTubeFetchMethod.YoutubeExplodeNoCookie, current,
                    current == YouTubeFetchStage.SchedulerWait &&
                        youtubeRequests.GetPausedUntilUtc(YouTubeRequestIdentity.Guest) > DateTime.UtcNow, error));
            }
            Track(YouTubeFetchStage.SchedulerWait);
            try
            {
                using var lease = await youtubeRequests.WaitAsync(YouTubeRequestIdentity.Guest,
                    (YouTubeRequestPriority)request.Priority, 6, request.Token,
                    () => (YouTubeRequestPriority)request.Priority).ConfigureAwait(false);
                Track(YouTubeFetchStage.Fetching);
                var video = await noCookieMetadataYoutube.Videos.GetAsync(url, request.Token).ConfigureAwait(false);
                var thumbnail = video.Thumbnails.OrderByDescending(t => t.Resolution.Area).FirstOrDefault();
                var result = new VideoSearchEntry(video.Title, url, video.Author.ChannelTitle, video.Duration,
                    thumbnail?.Url, video.Engagement.ViewCount, video.UploadDate, video.Author.ChannelId.Value,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (Interlocked.CompareExchange(ref noCookieMetadataConfirmed, 1, 0) == 0)
                    AepLog.YouTubeGuestMetadataConfirmed();
                fetched = result;
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                youtubeRequests.ReportFailure(YouTubeRequestIdentity.Guest, error.Message);
                AepLog.YouTubeFetchFailure(YouTubeFetchDiagnostic.Classify(section, data,
                    YouTubeFetchMethod.YoutubeExplodeNoCookie, error) with { Stage = stage });
                return TryGetCachedVideo(url, out var partial) ? partial : null;
            }
            finally
            {
                if (shared is not null && VideoId.TryParse(url) is { } reportId)
                    _ = shared.ReportAsync(reportId.Value,permission,fetched);
            }
        }, token, (int)priority).ConfigureAwait(false);
    }

    // ---------------------------------------------------------
    // YouTube search with full metadata.
    //
    // Home Trending and Browse Videos need ViewCount,
    // UploadDate and ChannelId, which the lightweight search
    // result alone does not fully provide.
    // ---------------------------------------------------------

    public async Task<List<VideoSearchEntry>> SearchWithMetadataAsync(
     string query,
     int maxResults,
     CancellationToken token)
    {
        var basicResults =
            await SearchAsync(
                    query,
                    maxResults,
                    token)
                .ConfigureAwait(false);

        if (basicResults.Count == 0)
        {
            return [];
        }

        var metadataTasks =
            basicResults
                .Select(
                    result =>
                        EnrichSearchResultAsync(
                            result,
                            token))
                .ToArray();

        var enriched =
            await Task.WhenAll(metadataTasks)
                .ConfigureAwait(false);

        return enriched.ToList();
    }

    public async Task<VideoSearchEntry> EnrichSearchResultAsync(VideoSearchEntry result,
        CancellationToken token, YouTubeRequestPriority priority = YouTubeRequestPriority.Background)
    {
        RememberVideoMetadata([result]);
        var details = await GetVideoEntryAsync(result.Url, token, priority: priority, section: YouTubeFetchSection.Search).ConfigureAwait(false);
        return details is null ? result : YouTubeMetadataCache.Merge(result, details) with { Url = result.Url };
    }

    // YoutubeExplode's own search (scrapes YouTube's search results) - no API key needed, same
    // dependency already used for playback resolution and metadata enrichment above.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,long> cachedSearchQueries = new();
    internal bool UsedCachedSearch(string query) => cachedSearchQueries.TryGetValue(query,out var until) && until>Environment.TickCount64;

    public async Task<List<VideoSearchEntry>> SearchAsync(
    string query,
    int maxResults,
    CancellationToken token,
    YouTubeRequestPriority priority = YouTubeRequestPriority.Background)
    {
        if (SharedYouTubeCache.Current is { } pausedShared &&
            youtubeRequests.GetPausedUntilUtc(YouTubeRequestIdentity.Guest) > DateTime.UtcNow)
        {
            try
            {
                var fallback = await pausedShared.SearchAsync(query,maxResults,token).ConfigureAwait(false);
                cachedSearchQueries[query]=Environment.TickCount64+300000;
                return fallback.ToList();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return []; }
            catch { return []; }
        }
        var results =
            new List<VideoSearchEntry>();

        IDisposable? gateLease = null;
        var searchFailed = false;

        try
        {
            gateLease = await youtubeRequests.WaitAsync(
                YouTubeRequestIdentity.Guest, priority, 2, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            await foreach (
                var video in youtube.Search
                    .GetVideosAsync(
                        query,
                        token)
                    .ConfigureAwait(false))
            {
                if (results.Count >= maxResults)
                {
                    break;
                }

                var thumbnail =
                    video.Thumbnails
                        .OrderByDescending(
                            t => t.Resolution.Area)
                        .FirstOrDefault();

                results.Add(
                    new VideoSearchEntry(
                        video.Title,
                        video.Url,
                        video.Author.ChannelTitle,
                        video.Duration,
                        thumbnail?.Url,
                        ChannelId: video.Author.ChannelId.Value));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation.
        }
        catch (Exception exception)
        {
            searchFailed = true;
            youtubeRequests.ReportFailure(YouTubeRequestIdentity.Guest, exception.Message);
            AepLog.Warning(
                "[Video] YouTube search failed for '[redacted]'.");
        }
        finally
        {
            gateLease?.Dispose();
        }

        // Both Home and YouTube searches already supply playback metadata.
        // Preserve it before a selection starts playback or a visible-card
        // enrichment request is cancelled by navigation.
        cachedSearchQueries.TryRemove(query,out _);
        if (searchFailed && !token.IsCancellationRequested && SharedYouTubeCache.Current is { } shared)
        {
            try
            {
                var fallback = await shared.SearchAsync(query,maxResults,token).ConfigureAwait(false);
                cachedSearchQueries[query] = Environment.TickCount64+300000;
                if (cachedSearchQueries.Count>100) foreach (var old in cachedSearchQueries.Keys.Take(50)) cachedSearchQueries.TryRemove(old,out _);
                results = results.Concat(fallback).DistinctBy(v=>MetadataKey(v.Url)).Take(maxResults).ToList();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch { /* Keep the existing local/search results if shared search also fails. */ }
        }
        RememberVideoMetadata(results);
        foreach (var entry in results) SharedYouTubeCache.Current?.QueueContribution(entry);
        return results.Select(ApplyCachedMetadata).ToList();
    }
}

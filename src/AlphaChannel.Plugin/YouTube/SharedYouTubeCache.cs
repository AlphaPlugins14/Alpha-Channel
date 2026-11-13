using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlphaChannel.Plugin.Net;
using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin.YouTube;

// Public video details only. Feed membership, cookies and stream URLs never enter
// this service. All queues are bounded; network/disk work runs off the UI thread.
internal sealed class SharedYouTubeCache : IDisposable
{
    internal static SharedYouTubeCache? Current;
    private readonly object gate = new();
    private readonly Func<string?> accountToken;
    private readonly Func<HttpClient> createClient;
    private readonly bool useDefaultTimeout;
    private readonly SemaphoreSlim refreshSlot = new(1,1);
    private readonly Action<Exception, YouTubeFetchMethod> failure;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, TaskCompletionSource<CachedVideo?>> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> nextRead = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> nextRefresh = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> freshUntil = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (VideoSearchEntry Entry,long Until)> observed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PopularVideoMetadata> contributions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PopularVideoMetadata> submitted = new(StringComparer.Ordinal);
    private readonly HashSet<string> refreshing = new(StringComparer.Ordinal);
    private readonly string? storagePath;
    private readonly Task persistence;
    private bool reading, contributing;
    private long unavailableUntil, nextFailureLog, writePausedUntil;
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    internal sealed record CachedVideo(string VideoId, PopularVideoMetadata? Metadata, long RefreshAfterUnix = 0);
    private sealed record BatchReply(CachedVideo[] Videos, int? NextOffset = null);
    internal sealed record FetchPermission(bool Fetch, string? LeaseId = null, string? AccountToken = null);

    internal SharedYouTubeCache(Func<string?> accountToken, string? storagePath = null,
        Func<HttpClient>? createClient = null, Action<Exception, YouTubeFetchMethod>? failure = null)
    {
        this.accountToken = accountToken; this.storagePath = storagePath;
        useDefaultTimeout = createClient is null;
        this.createClient = createClient ?? (() =>
        {
            var http = PluginHttpClients.CreateMetadataClient();
            http.BaseAddress = new Uri(PopularWorkerClient.WorkerUrl);
            http.Timeout = TimeSpan.FromSeconds(4);
            return http;
        });
        this.failure = failure ?? ((_, _) => { });
        persistence = storagePath is null ? Task.CompletedTask : Task.Run(PersistAsync);
    }

    internal static PopularVideoMetadata ToMetadata(VideoSearchEntry entry)
    {
        static string? Clean(string? text, int limit) => text is null ? null :
            new string(text.Where(c => !char.IsControl(c)).Take(limit).ToArray()).Trim() is {Length:>0} value ? value : null;
        return new(YouTubeMetadataCache.HasTitle(entry.Title) ? Clean(entry.Title,512) : null,
            Clean(entry.ChannelName,256),Clean(entry.ChannelId,128),entry.ThumbnailUrl,
            entry.Duration?.TotalSeconds,entry.UploadDate,entry.ViewCount,entry.ViewCountObservedAtUnix,entry.MetadataObservedAtUnix);
    }
    internal static VideoSearchEntry ToEntry(CachedVideo video)
    {
        var m = video.Metadata!;
        return new(m.Title ?? string.Empty,"https://www.youtube.com/watch?v="+video.VideoId,m.ChannelName ?? string.Empty,
            m.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,m.ThumbnailUrl,m.ViewCount,m.UploadDate,
            m.ChannelId,m.ViewCountObservedAtUnix,m.MetadataObservedAtUnix);
    }
    internal static bool Fresh(VideoSearchEntry entry) => YouTubeMetadataCache.IsComplete(entry) &&
        !string.IsNullOrWhiteSpace(entry.ThumbnailUrl) &&
        entry.MetadataObservedAtUnix is { } details && details + 30*86400 > Now &&
        entry.ViewCountObservedAtUnix is { } views && views + 7*86400 > Now;
    private bool IsFresh(VideoSearchEntry entry)
    {
        if (Fresh(entry)) return true;
        var id=YoutubeExplode.Videos.VideoId.TryParse(entry.Url)?.Value;
        lock (gate) return id is not null && YouTubeMetadataCache.IsComplete(entry) &&
            !string.IsNullOrWhiteSpace(entry.ThumbnailUrl) && freshUntil.TryGetValue(id,out var after) && after>Now;
    }

    internal void Observe(VideoSearchEntry entry, YouTubeFetchSection section = YouTubeFetchSection.Background)
    {
        if (lifetime.IsCancellationRequested) return;
        lock (gate)
        {
            if (observed.TryGetValue(entry.Url,out var prior) && prior.Entry==entry && prior.Until>Now) return;
            observed[entry.Url]=(entry,Now+60); Trim(observed);
        }
        QueueContribution(entry);
        if (!IsFresh(entry)) _ = ObserveAsync(entry, section);
    }
    private async Task ObserveAsync(VideoSearchEntry entry, YouTubeFetchSection section)
    {
        try
        {
            var id = YoutubeExplode.Videos.VideoId.TryParse(entry.Url)?.Value;
            if (id is null) return;
            await LookupAsync(id,lifetime.Token).ConfigureAwait(false);
            if (YouTubeMetadataCache.Shared.TryGet(id,out var cached) && cached is not null && IsFresh(cached)) return;
            // Incomplete cards already have a serialized, visible-card fetch
            // queue. Only refresh usable stale records in the background.
            if (!YouTubeMetadataCache.IsComplete(entry)) return;
            lock (gate)
            {
                if (nextRefresh.TryGetValue(id,out var after) && after>Now || !refreshing.Add(id)) return;
                nextRefresh[id]=Now+900; Trim(nextRefresh);
            }
            try
            {
                await refreshSlot.WaitAsync(lifetime.Token).ConfigureAwait(false);
                try
                {
                    if (YouTubeMetadataCache.Shared.TryGet(id,out cached) && cached is not null && IsFresh(cached)) return;
                    if (!YouTubeRequestScheduler.Shared.CanRefreshCachedMetadata())
                    {
                        lock (gate) nextRefresh[id]=Now+60;
                        return;
                    }
                    // Existing cards stay on screen while a bounded, claimed refresh runs.
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(20));
                    await new VideoUrlResolver().GetVideoEntryAsync(entry.Url,deadline.Token,forceRefresh:true,section:section).ConfigureAwait(false);
                    // Another viewer may own the claim. Recheck twice without
                    // launching another YouTube request or an endless retry loop.
                    foreach (var delay in new[] {3,7})
                    {
                        if (YouTubeMetadataCache.Shared.TryGet(id,out cached) && cached is not null && IsFresh(cached)) break;
                        await Task.Delay(TimeSpan.FromSeconds(delay),deadline.Token).ConfigureAwait(false);
                        lock (gate) nextRead.Remove(id);
                        await LookupAsync(id,deadline.Token).ConfigureAwait(false);
                    }
                }
                finally { refreshSlot.Release(); }
            }
            finally { lock (gate) refreshing.Remove(id); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { LogFailure(error,YouTubeFetchMethod.CloudflareFeed); }
    }

    internal Task<CachedVideo?> LookupAsync(string id, CancellationToken token)
    {
        Task<CachedVideo?> task;
        lock (gate)
        {
            if (lifetime.IsCancellationRequested || unavailableUntil > Now) return Task.FromResult<CachedVideo?>(null);
            if (pending.TryGetValue(id,out var existing)) return existing.Task.WaitAsync(token);
            if (nextRead.TryGetValue(id,out var after) && after>Now) return Task.FromResult<CachedVideo?>(null);
            if (pending.Count>=1000) return Task.FromResult<CachedVideo?>(null);
            var completion = new TaskCompletionSource<CachedVideo?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id]=completion; task=completion.Task;
            if (!reading) { reading=true; _=Task.Run(ReadBatchesAsync); }
        }
        return task.WaitAsync(token);
    }
    private async Task ReadBatchesAsync()
    {
        try
        {
            await Task.Delay(80,lifetime.Token).ConfigureAwait(false);
            while (!lifetime.IsCancellationRequested)
            {
                KeyValuePair<string,TaskCompletionSource<CachedVideo?>>[] batch;
                lock (gate) batch=pending.Take(50).ToArray();
                if (batch.Length==0) return;
                Dictionary<string,CachedVideo> found = new(StringComparer.Ordinal);
                try
                {
                    var response=await PostAsync<BatchReply>("/api/videos/batch",new {videoIds=batch.Select(p=>p.Key).ToArray()},null,lifetime.Token).ConfigureAwait(false);
                    Validate(response);
                    foreach (var video in response.Videos)
                    {
                        if (!batch.Any(p=>p.Key==video.VideoId)) throw new InvalidDataException("Unexpected shared video ID.");
                        found[video.VideoId]=video;
                        if (video.Metadata is not null) RememberSubmitted(video.VideoId,video.Metadata);
                        lock (gate) { freshUntil[video.VideoId]=video.RefreshAfterUnix; Trim(freshUntil); }
                        if (video.Metadata is not null) YouTubeMetadataCache.Shared.Store(video.VideoId,ToEntry(video));
                    }
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (Exception error) { LogFailure(error,YouTubeFetchMethod.CloudflareFeed); }
                lock (gate)
                {
                    foreach (var pair in batch)
                    {
                        found.TryGetValue(pair.Key,out var video);
                        nextRead[pair.Key]=Now+300;
                        pending.Remove(pair.Key); pair.Value.TrySetResult(video);
                    }
                    Trim(nextRead); 
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (gate)
            {
                reading=false;
                foreach (var completion in pending.Values) completion.TrySetResult(null);
                pending.Clear();
            }
        }
    }

    internal async Task<FetchPermission> PrepareFetchAsync(string id, VideoSearchEntry? local, CancellationToken token)
    {
        var shared=await LookupAsync(id,token).ConfigureAwait(false);
        if (shared?.Metadata?.IsComplete==true && shared.RefreshAfterUnix>Now) return new(false);
        if (YouTubeMetadataCache.Shared.TryGet(id,out var known) && known is not null && IsFresh(known)) return new(false);
        var auth=accountToken();
        if (auth is null || unavailableUntil>Now || Volatile.Read(ref writePausedUntil)>Now) return new(true);
        try
        {
            var metadata=local is null ? null : ToMetadata(local);
            var reply=await PostAsync<PopularMetadataReply>("/api/videos/claim",new {videoId=id,
                cachedMetadata=PopularWorkerClient.ValidMetadata(metadata,id)?metadata:null},auth,token).ConfigureAwait(false);
            if (!PopularWorkerClient.ValidMetadata(reply.Metadata,id) || reply.State is not ("available" or "busy" or "claimed") ||
                reply.State=="available" && reply.Metadata?.IsComplete!=true ||
                reply.State=="claimed" && (reply.LeaseId is not {Length:32} || reply.LeaseId.Any(c=>!char.IsAsciiHexDigit(c))))
                throw new InvalidDataException("Invalid shared claim.");
            if (reply.Metadata is not null) YouTubeMetadataCache.Shared.Store(id,ToEntry(new(id,reply.Metadata,reply.RefreshAfterUnix)));
            if (reply.Metadata is not null) RememberSubmitted(id,reply.Metadata);
            lock (gate) { freshUntil[id]=reply.RefreshAfterUnix; Trim(freshUntil); }
            return new(reply.State=="claimed",reply.LeaseId,auth);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { LogFailure(error,YouTubeFetchMethod.CloudflareClaim); return new(true); }
    }
    internal async Task ReportAsync(string id, FetchPermission permission, VideoSearchEntry? details)
    {
        if (permission.LeaseId is null || permission.AccountToken is null) { if (details is not null) QueueContribution(details); return; }
        try
        {
            var reply=await PostAsync<PopularMetadataReply>("/api/videos/report",new {videoId=id,leaseId=permission.LeaseId,
                metadata=details is null?null:ToMetadata(details)},permission.AccountToken,lifetime.Token).ConfigureAwait(false);
            if (!reply.Updated || !PopularWorkerClient.ValidMetadata(reply.Metadata,id)) throw new InvalidDataException("Invalid shared metadata report.");
            if (reply.Metadata is not null) YouTubeMetadataCache.Shared.Store(id,ToEntry(new(id,reply.Metadata,reply.RefreshAfterUnix)));
            if (reply.Metadata is not null) RememberSubmitted(id,reply.Metadata);
            lock (gate) { freshUntil[id]=reply.RefreshAfterUnix; Trim(freshUntil); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            LogFailure(error,YouTubeFetchMethod.CloudflareReport);
            if (details is not null) QueueContribution(details);
        }
    }
    internal void QueueContribution(VideoSearchEntry entry)
    {
        if (accountToken() is null || lifetime.IsCancellationRequested || Volatile.Read(ref writePausedUntil)>Now) return;
        var id=YoutubeExplode.Videos.VideoId.TryParse(entry.Url)?.Value;
        var metadata=ToMetadata(entry);
        if (id is null || !PopularWorkerClient.ValidMetadata(metadata,id) || metadata.Title is null) return;
        lock (gate)
        {
            if (submitted.TryGetValue(id,out var old) && !Improves(metadata,old) || contributions.Count>=1000) return;
            contributions[id]=metadata;
            if (!contributing) { contributing=true; _=Task.Run(ContributeAsync); }
        }
    }
    private async Task ContributeAsync()
    {
        try
        {
            await Task.Delay(1000,lifetime.Token).ConfigureAwait(false);
            while (!lifetime.IsCancellationRequested)
            {
                KeyValuePair<string,PopularVideoMetadata>[] batch;
                lock (gate)
                {
                    var bytes=0;
                    batch=contributions.Take(50).TakeWhile(pair =>
                    {
                        bytes+=System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(pair.Value))+100;
                        return bytes<180000;
                    }).ToArray();
                    foreach (var pair in batch) contributions.Remove(pair.Key);
                }
                if (batch.Length==0) return;
                var auth=accountToken();
                if (auth is null) return;
                var wait=Volatile.Read(ref unavailableUntil)-Now;
                if (wait>0) await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait,60)),lifetime.Token).ConfigureAwait(false);
                auth=accountToken();
                if (auth is null) return;
                var payload=new {videos=batch.Select(p=>new {videoId=p.Key,metadata=p.Value}).ToArray()};
                try { await PostAsync<JsonElement>("/api/videos/contribute",payload,auth,lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    LogFailure(error,YouTubeFetchMethod.CloudflareReport);
                    if (error is PopularWorkerException {ErrorCode:"cache_daily_limit"}) return;
                    // One delayed retry preserves a transiently failed upload.
                    // It runs independently of card display and YouTube slots.
                    await Task.Delay(TimeSpan.FromMinutes(1),lifetime.Token).ConfigureAwait(false);
                    await PostAsync<JsonElement>("/api/videos/contribute",payload,auth,lifetime.Token).ConfigureAwait(false);
                }
                lock (gate)
                {
                    foreach (var pair in batch) RememberSubmitted(pair.Key,pair.Value);
                    Trim(submitted);
                }
                await Task.Delay(1000,lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { LogFailure(error,YouTubeFetchMethod.CloudflareReport); }
        finally { lock (gate) { contributing=false; contributions.Clear(); } }
    }
    private static bool Improves(PopularVideoMetadata value,PopularVideoMetadata known) =>
        (value.MetadataObservedAtUnix ?? 0)>(known.MetadataObservedAtUnix ?? 0) ||
        known.Title is null && value.Title is not null || known.ChannelName is null && value.ChannelName is not null ||
        known.ChannelId is null && value.ChannelId is not null || known.ThumbnailUrl is null && value.ThumbnailUrl is not null ||
        known.DurationSeconds is null && value.DurationSeconds is not null || known.UploadDate is null && value.UploadDate is not null ||
        value.ViewCount is not null && (known.ViewCount is null || (value.ViewCountObservedAtUnix ?? 0)>(known.ViewCountObservedAtUnix ?? 0));

    private void RememberSubmitted(string id,PopularVideoMetadata metadata)
    {
        lock (gate)
        {
            submitted[id]=submitted.TryGetValue(id,out var prior)
                ? ToMetadata(YouTubeMetadataCache.Merge(ToEntry(new(id,prior)),ToEntry(new(id,metadata)))) : metadata;
            Trim(submitted);
        }
    }
    internal async Task<VideoSearchEntry[]> SearchAsync(string query,int maximum,CancellationToken token)
    {
        var results=new List<VideoSearchEntry>(); int? offset=0; var pages=0;
        do
        {
            var response=await PostAsync<BatchReply>("/api/videos/search",new {query,offset},null,token).ConfigureAwait(false);
            Validate(response);
            if (response.NextOffset is { } next && (next<=offset || next>1000)) throw new InvalidDataException("Invalid shared search pagination.");
            results.AddRange(response.Videos.Where(v=>v.Metadata is not null).Select(ToEntry));
            offset=response.NextOffset;
        } while (++pages<4 && results.Count<Math.Min(maximum,200) && offset is >=0 and <=1000);
        foreach (var entry in results) YouTubeMetadataCache.Shared.Store(YoutubeExplode.Videos.VideoId.Parse(entry.Url).Value,entry);
        return results.Take(maximum).ToArray();
    }
    private static void Validate(BatchReply reply)
    {
        if (reply.Videos is null || reply.Videos.Length>50 || reply.Videos.Any(v=>v is null || !PopularWorkerClient.ValidVideoId(v.VideoId) ||
            !PopularWorkerClient.ValidMetadata(v.Metadata,v.VideoId) || v.RefreshAfterUnix<0)) throw new InvalidDataException("Invalid shared metadata batch.");
    }
    private async Task<T> PostAsync<T>(string path,object payload,string? auth,CancellationToken token)
    {
        using var http=createClient();
        if (useDefaultTimeout) http.Timeout=TimeSpan.FromSeconds(path.EndsWith("/contribute",StringComparison.Ordinal) ||
            path.EndsWith("/report",StringComparison.Ordinal) ? 30 : path.EndsWith("/claim",StringComparison.Ordinal) ? 10 : 4);
        if (auth is not null) http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",auth);
        try
        {
            using var response=await http.PostAsJsonAsync(path,payload,token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var code="cache_request_failed";
                if ((int)response.StatusCode==429)
                {
                    try
                    {
                        await response.Content.LoadIntoBufferAsync(4096,token).ConfigureAwait(false);
                        var body=await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken:token).ConfigureAwait(false);
                        if (body.TryGetProperty("error",out var value) && value.ValueKind==JsonValueKind.String && value.GetString()=="cache_daily_limit") code="cache_daily_limit";
                    }
                    catch (Exception error) when (error is JsonException or HttpRequestException or InvalidOperationException) { }
                }
                var retry=response.Headers.RetryAfter?.Delta?.TotalSeconds ??
                    (response.Headers.RetryAfter?.Date is { } after ? (after-DateTimeOffset.UtcNow).TotalSeconds : 60);
                throw new PopularWorkerException((int)response.StatusCode,code,(int)Math.Clamp(retry,1,86400));
            }
            await response.Content.LoadIntoBufferAsync(512*1024,token).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken:token).ConfigureAwait(false) ?? throw new InvalidDataException("Empty shared cache response.");
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Shared video cache request timed out.",error); }
    }
    private void LogFailure(Exception error,YouTubeFetchMethod method)
    {
        lock (gate)
        {
            if (error is PopularWorkerException {HttpStatus:429,ErrorCode:"cache_daily_limit"} quota)
                writePausedUntil=Now+Math.Max(60,quota.RetryAfterSeconds);
            else unavailableUntil=Now+60;
            if (nextFailureLog>Now) return;
            nextFailureLog=Now+60;
        }
        failure(error,method);
    }
    private static void Trim<T>(Dictionary<string,T> values)
    {
        while (values.Count>1000) values.Remove(values.Keys.First());
    }
    private async Task PersistAsync()
    {
        try
        {
            if (File.Exists(storagePath) && new FileInfo(storagePath!).Length<=4*1024*1024)
            {
                var saved=JsonSerializer.Deserialize<CachedVideo[]>(await File.ReadAllTextAsync(storagePath!,lifetime.Token).ConfigureAwait(false));
                foreach (var video in (saved ?? []).Take(1000))
                    if (video is not null && PopularWorkerClient.ValidVideoId(video.VideoId) && video.Metadata is not null && PopularWorkerClient.ValidMetadata(video.Metadata,video.VideoId))
                        YouTubeMetadataCache.Shared.Store(video.VideoId,ToEntry(video));
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        try
        {
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    var saved=YouTubeMetadataCache.Shared.Snapshot().Select(p=>new CachedVideo(p.Key,ToMetadata(p.Value))).ToArray();
                    var json=JsonSerializer.Serialize(saved);
                    if (System.Text.Encoding.UTF8.GetByteCount(json)>4*1024*1024) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(storagePath!)!);
                    await File.WriteAllTextAsync(storagePath+".tmp",json,lifetime.Token).ConfigureAwait(false);
                    File.Move(storagePath+".tmp",storagePath!,true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (OperationCanceledException) { }
    }
    public void Dispose()
    {
        lifetime.Cancel();
        lock (gate) { foreach (var completion in pending.Values) completion.TrySetCanceled(); pending.Clear(); }
        if (ReferenceEquals(Current,this)) Current=null;
        // In-flight workers own the cancellation source until they finish.
    }
}

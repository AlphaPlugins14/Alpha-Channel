namespace AlphaChannel.Plugin.YouTube;

internal enum YouTubeRequestPriority
{
    Interactive,
    Manual,
    Background
}

internal enum YouTubeRequestIdentity
{
    Guest,
    Account
}

/// <summary>
/// Paces plugin-initiated YouTube discovery and metadata work. A lease covers a
/// logical operation; cost is a conservative estimate of the HTTP work hidden
/// inside YoutubeExplode or yt-dlp. Playback never waits for a lease.
/// </summary>
internal sealed class YouTubeRequestScheduler
{
    internal static YouTubeRequestScheduler Shared { get; } = new();

    private readonly object gate = new();
    private readonly List<PendingRequest> pending = [];
    private readonly List<Usage> history = [];
    private Configuration? configuration;
    private DateTime accountPausedUntilUtc;
    private DateTime guestPausedUntilUtc;
    private DateTime backgroundPausedUntilUtc;
    private DateTime lastAccountStartUtc;
    private DateTime lastGuestStartUtc;
    private DateTime lastAccountFailureUtc;
    private DateTime lastGuestFailureUtc;
    private string? lastPlaybackUrl;
    private YouTubeRequestIdentity lastPlaybackIdentity;
    private DateTime lastPlaybackUtc;
    private int accountActive;
    private int guestActive;
    private int interactiveActive;
    private int accountFailureCount;
    private int guestFailureCount;
    private long nextSequence;
    private bool pumpRunning;
    private bool stopped;

    private YouTubeRequestScheduler() { }

    internal void Configure(Configuration config)
    {
        lock (gate)
        {
            configuration = config;
            stopped = false;
            accountPausedUntilUtc = config.YouTubeAccountRequestsPausedUntilUtc;
            guestPausedUntilUtc = config.YouTubeGuestRequestsPausedUntilUtc;
        }
    }

    internal static TimeSpan FailureCooldown(int count)
    {
        return count switch
        {
            1 => TimeSpan.FromMinutes(15),
            2 => TimeSpan.FromHours(1),
            _ => TimeSpan.FromHours(4)
        };
    }

    internal Task<IDisposable> WaitAsync(
        YouTubeRequestIdentity identity, YouTubeRequestPriority priority,
        int estimatedCost, CancellationToken cancellationToken,
        Func<YouTubeRequestPriority>? currentPriority = null)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<IDisposable>(cancellationToken);

        var request = new PendingRequest(identity, priority, Math.Clamp(estimatedCost, 1, 10),
            Interlocked.Increment(ref nextSequence), currentPriority);
        lock (gate)
        {
            if (stopped)
                return Task.FromCanceled<IDisposable>(new CancellationToken(true));
            pending.Add(request);
            request.Cancellation = cancellationToken.Register(() => Cancel(request, cancellationToken));
            Dispatch(DateTime.UtcNow);
            if (pending.Count > 0 && !pumpRunning)
            {
                pumpRunning = true;
                _ = PumpAsync();
            }
        }
        return request.Completion.Task;
    }

    internal void RecordPlayback(string url, YouTubeRequestIdentity identity)
    {
        lock (gate)
        {
            var now = DateTime.UtcNow;
            // A renderer retry or repeated watch-party state must not charge
            // the same playback start on every frame.
            if (lastPlaybackIdentity == identity &&
                string.Equals(lastPlaybackUrl, url, StringComparison.OrdinalIgnoreCase) &&
                now - lastPlaybackUtc < TimeSpan.FromSeconds(15))
                return;
            lastPlaybackUrl = url;
            lastPlaybackIdentity = identity;
            lastPlaybackUtc = now;
            Prune(now);
            history.Add(new Usage(now, identity, 4));
            backgroundPausedUntilUtc = now.AddSeconds(30);
            
        }
    }

    internal void ReportFailure(YouTubeRequestIdentity identity, string? error)
    {
        if (string.IsNullOrWhiteSpace(error) ||
            !(error.Contains("429", StringComparison.OrdinalIgnoreCase) ||
              error.Contains("too many requests", StringComparison.OrdinalIgnoreCase) ||
              error.Contains("confirm you are not a bot", StringComparison.OrdinalIgnoreCase) ||
              error.Contains("sign in to confirm", StringComparison.OrdinalIgnoreCase) ||
              error.Contains("rate limit", StringComparison.OrdinalIgnoreCase)))
            return;

        Configuration? config;
        DateTime until;
        lock (gate)
        {
            var now = DateTime.UtcNow;
            var last = identity == YouTubeRequestIdentity.Account ? lastAccountFailureUtc : lastGuestFailureUtc;
            if (now - last < TimeSpan.FromMinutes(1))
                return;
            var count = now - last < TimeSpan.FromHours(2)
                ? (identity == YouTubeRequestIdentity.Account ? accountFailureCount : guestFailureCount) + 1
                : 1;
            until = now.Add(FailureCooldown(count));
            if (identity == YouTubeRequestIdentity.Account)
            {
                accountFailureCount = count;
                lastAccountFailureUtc = now;
                accountPausedUntilUtc = until;
            }
            else
            {
                guestFailureCount = count;
                lastGuestFailureUtc = now;
                guestPausedUntilUtc = until;
            }
            config = configuration;
            if (config is not null)
            {
                if (identity == YouTubeRequestIdentity.Account)
                    config.YouTubeAccountRequestsPausedUntilUtc = until;
                else
                    config.YouTubeGuestRequestsPausedUntilUtc = until;
            }
        }
        config?.Save();
        {}
    }

    internal DateTime GetPausedUntilUtc(YouTubeRequestIdentity identity)
    {
        lock (gate)
            return identity == YouTubeRequestIdentity.Account ? accountPausedUntilUtc : guestPausedUntilUtc;
    }

    // Optional refreshes must leave capacity for missing visible-card metadata,
    // searches and playback. This does not relax any existing request limit.
    internal bool CanRefreshCachedMetadata()
    {
        lock (gate)
        {
            var now=DateTime.UtcNow;
            Prune(now);
            return !stopped && now>=guestPausedUntilUtc && now>=backgroundPausedUntilUtc &&
                history.Where(item=>item.At>=now.AddMinutes(-10)).Sum(item=>item.Cost)+6<=40 &&
                history.Sum(item=>item.Cost)+6<=120;
        }
    }

    internal DateTime GuestMetadataResumeUtc()
    {
        lock (gate)
        {
            var now=DateTime.UtcNow;
            Prune(now);
            var earliest=guestPausedUntilUtc>now?guestPausedUntilUtc:now;
            foreach (var at in history.SelectMany(item=>new[]{item.At.AddMinutes(10).AddTicks(1),item.At.AddHours(1).AddTicks(1)})
                .Append(earliest).Where(at=>at>=earliest).Distinct().OrderBy(at=>at))
                if (BudgetAllows(YouTubeRequestIdentity.Guest,6,at)) return at;
            return earliest;
        }
    }

    internal void CancelAllPending()
    {
        lock (gate)
        {
            stopped = true;
            foreach (var request in pending)
            {
                request.Cancellation.Unregister();
                request.Completion.TrySetCanceled();
            }
            pending.Clear();
        }
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            await Task.Delay(500).ConfigureAwait(false);
            lock (gate)
            {
                Dispatch(DateTime.UtcNow);
                if (pending.Count == 0)
                {
                    pumpRunning = false;
                    return;
                }
            }
        }
    }

    private void Cancel(PendingRequest request, CancellationToken token)
    {
        lock (gate)
        {
            if (!pending.Remove(request))
                return;
            request.Completion.TrySetCanceled(token);
        }
    }

    private void Dispatch(DateTime now)
    {
        Prune(now);
        while (true)
        {
            var next = pending
                .Where(request => CanStart(request, now))
                .OrderBy(request => request.Priority)
                .ThenBy(request => request.Sequence)
                .FirstOrDefault();
            if (next is null)
                return;

            pending.Remove(next);
            // Unregister without waiting for a callback while holding gate.
            // A canceled request that has already been granted owns its lease.
            next.Cancellation.Unregister();
            var grantedPriority = next.Priority;
            history.Add(new Usage(now, next.Identity, next.Cost));
            if (grantedPriority == YouTubeRequestPriority.Interactive)
                interactiveActive++;
            else if (next.Identity == YouTubeRequestIdentity.Account)
                accountActive++;
            else
                guestActive++;

            if (next.Identity == YouTubeRequestIdentity.Account)
                lastAccountStartUtc = now;
            else
                lastGuestStartUtc = now;
            
            next.Completion.TrySetResult(new Lease(this, next.Identity, grantedPriority));
        }
    }

    private bool CanStart(PendingRequest request, DateTime now)
    {
        var account = request.Identity == YouTubeRequestIdentity.Account;
        if (now < (account ? accountPausedUntilUtc : guestPausedUntilUtc))
            return false;
        if (request.Priority == YouTubeRequestPriority.Interactive)
            return interactiveActive < 2;
        if (request.Priority == YouTubeRequestPriority.Background && now < backgroundPausedUntilUtc)
            return false;
        if (account ? accountActive >= 1 : guestActive >= 2)
            return false;
        if (now - (account ? lastAccountStartUtc : lastGuestStartUtc) <
            (account ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(3)))
            return false;

        return BudgetAllows(request.Identity,request.Cost,now);
    }

    private bool BudgetAllows(YouTubeRequestIdentity identity,int cost,DateTime now)
    {
        var account=identity==YouTubeRequestIdentity.Account;
        var tenMinutesAgo = now.AddMinutes(-10);
        var hourAgo = now.AddHours(-1);
        var tenMinuteTotal = history.Where(item => item.At >= tenMinutesAgo).Sum(item => item.Cost);
        var hourTotal = history.Where(item => item.At >= hourAgo).Sum(item => item.Cost);
        var laneTenMinute = history.Where(item => item.Identity == identity && item.At >= tenMinutesAgo)
            .Sum(item => item.Cost);
        var laneHour = history.Where(item => item.Identity == identity && item.At >= hourAgo)
            .Sum(item => item.Cost);
        return tenMinuteTotal + cost <= 120 && hourTotal + cost <= 360 &&
               laneTenMinute + cost <= (account ? 40 : 100) &&
               laneHour + cost <= (account ? 150 : 300);
    }

    private void Prune(DateTime now) => history.RemoveAll(item => item.At < now.AddHours(-1));

    private void Release(YouTubeRequestIdentity identity, YouTubeRequestPriority priority)
    {
        lock (gate)
        {
            if (priority == YouTubeRequestPriority.Interactive)
                interactiveActive--;
            else if (identity == YouTubeRequestIdentity.Account)
                accountActive--;
            else
                guestActive--;
            Dispatch(DateTime.UtcNow);
        }
    }

    private sealed record Usage(DateTime At, YouTubeRequestIdentity Identity, int Cost);

    private sealed class PendingRequest(
        YouTubeRequestIdentity identity, YouTubeRequestPriority priority, int cost, long sequence,
        Func<YouTubeRequestPriority>? currentPriority)
    {
        internal YouTubeRequestIdentity Identity { get; } = identity;
        internal YouTubeRequestPriority Priority => currentPriority?.Invoke() ?? priority;
        internal int Cost { get; } = cost;
        internal long Sequence { get; } = sequence;
        internal TaskCompletionSource<IDisposable> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Cancellation { get; set; }
    }

    private sealed class Lease(YouTubeRequestScheduler owner, YouTubeRequestIdentity identity,
        YouTubeRequestPriority priority) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                owner.Release(identity, priority);
        }
    }
}

internal sealed class YouTubeMetadataDeferredException(DateTime resumeUtc)
    : Exception("Metadata request deferred until its request budget is available.")
{
    internal DateTime ResumeUtc { get; } = resumeUtc;
}

using System.Collections.Concurrent;
using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal static class VideoPageChoice
{
    internal sealed class Request(string page, Task<WebMediaUrlResolver.Discovery> discovery, CancellationToken token, Func<bool>? current, bool forceChoice = false)
    {
        internal readonly bool ForceChoice = forceChoice;
        internal readonly string Page = page;
        internal readonly Task<WebMediaUrlResolver.Discovery> Discovery = discovery;
        internal readonly CancellationToken Token = token;
        internal readonly Func<bool>? Current = current;
        internal readonly TaskCompletionSource<ResolvedMediaRequest> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Active => !Token.IsCancellationRequested && !Completion.Task.IsCompleted && (Current?.Invoke() ?? true);
        private readonly object detailsGate = new();
        private readonly Dictionary<string, Task<ResolvedMediaRequest>> details = new(StringComparer.Ordinal);
        private CancellationTokenSource? detailsLifetime;

        internal Task<ResolvedMediaRequest> Details(ResolvedMediaRequest media, string? ffmpeg)
        {
            lock (detailsGate)
            {
                if (media.Url is null || !Active) return Task.FromResult(media);
                if (details.TryGetValue(media.Url, out var cached)) return cached;
                if (details.Count >= 32) return Task.FromResult(media);
                detailsLifetime ??= CancellationTokenSource.CreateLinkedTokenSource(Token);
                var token = detailsLifetime.Token;
                var work = Task.Run(() => WebVideoMetadataProbe.ReadAsync(ffmpeg, media, token));
                details[media.Url] = work;
                return work;
            }
        }

        internal void CancelDetails()
        {
            lock (detailsGate)
            {
                detailsLifetime?.Cancel();
                detailsLifetime?.Dispose();
                detailsLifetime = null;
            }
        }
    }

    internal static readonly ConcurrentQueue<Request> Pending = new();

    internal static async Task<ResolvedMediaRequest> ResolveAsync(Resources resources, string page, CancellationToken token,
        Func<bool>? current = null, bool forceChoice = false)
    {
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task<WebMediaUrlResolver.Discovery> Discover()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(requestLifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            await resources.PrepareMediaAsync(true).WaitAsync(deadline.Token).ConfigureAwait(false);
            return await WebMediaUrlResolver.DiscoverAsync(resources, page, deadline.Token, includeHtmlAlternatives: forceChoice).ConfigureAwait(false);
        }
        try { return await ChooseAsync(page, Discover(), requestLifetime.Token, current, forceChoice).ConfigureAwait(false); }
        finally { requestLifetime.Cancel(); }
    }

    internal static async Task<ResolvedMediaRequest> ChooseAsync(string page, Task<WebMediaUrlResolver.Discovery> discovery,
        CancellationToken token, Func<bool>? current = null, bool forceChoice = false)
    {
        var request = new Request(page, discovery, token, current, forceChoice);
        Pending.Enqueue(request);
        _ = discovery.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        try { return await request.Completion.Task.WaitAsync(token).ConfigureAwait(false); }
        finally { request.CancelDetails(); }
    }
}

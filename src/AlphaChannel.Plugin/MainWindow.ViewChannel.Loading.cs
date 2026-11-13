using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.YouTube;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // ---------------------------------------------------------
    // YouTube channel view
    // ---------------------------------------------------------


    private string? viewedChannelId;
    private string? viewedChannelName;


    private List<VideoSearchEntry>? viewedChannelVideos;
    private bool isLoadingViewedChannelVideos;
    private int viewedChannelLoadGeneration;
    private CancellationTokenSource? viewedChannelLoadCancellation;

    private void OpenYouTubeChannel(
        string channelId,
        string channelName)
    {
        previousBrowseVideoSectionTab =
            browseVideoSectionTab;

        currentPage = HomePage.VideoGrid;

        viewedChannelId = channelId;
        viewedChannelName = channelName;

        viewedChannelVideos = null;

        _ = LoadViewedChannelVideosAsync();
    }
    private async Task LoadViewedChannelVideosAsync()
    {
        var channelId = viewedChannelId;
        if (string.IsNullOrEmpty(channelId))
        {
            return;
        }

        viewedChannelLoadCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(youtubeFeedToken);
        viewedChannelLoadCancellation = cancellation;
        var generation = ++viewedChannelLoadGeneration;
        isLoadingViewedChannelVideos = true;
        bool IsCurrent() => Volatile.Read(ref disposeStarted) == 0 &&
            generation == viewedChannelLoadGeneration && viewedChannelId == channelId &&
            !cancellation.IsCancellationRequested;

        try
        {
            var videos =
                await searchResolver
                    .GetChannelUploadsAsync(
                        channelId,
                        15,
                        cancellation.Token,
                        YouTubeRequestPriority.Interactive)
                    .ConfigureAwait(false);

            var ordered =
                videos
                    .OrderByDescending(
                        video =>
                            video.UploadDate ??
                            DateTimeOffset.MinValue)
                    .Take(15)
                    .ToList();
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (IsCurrent()) viewedChannelVideos = ordered;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (!IsCurrent()) return;
                AepLog.Warning("[Channel View] Failed to load channel videos.");
                viewedChannelVideos = [];
            }).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (Volatile.Read(ref disposeStarted) == 0)
                    await Plugin.Framework.RunOnFrameworkThread(() =>
                    {
                        if (generation != viewedChannelLoadGeneration) return;
                        isLoadingViewedChannelVideos = false;
                        if (ReferenceEquals(viewedChannelLoadCancellation, cancellation))
                            viewedChannelLoadCancellation = null;
                    }).ConfigureAwait(false);
            }
            finally { cancellation.Dispose(); }
        }
    }

    private void CloseYouTubeChannel()
    {
        viewedChannelLoadCancellation?.Cancel();
        viewedChannelId = null;
        viewedChannelName = null;
        viewedChannelVideos = null;
        browseVideoSectionTab = previousBrowseVideoSectionTab;
    }
}

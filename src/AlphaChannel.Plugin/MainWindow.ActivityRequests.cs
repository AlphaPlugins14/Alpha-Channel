using AlphaChannel.Contracts;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private Task RefreshActivityAsync(string bearerToken, bool reset, long? before = null)
    {
        if (CurrentSession is not { } session) return Task.CompletedTask;
        var request = CaptureAccountRequest(session);
        activityDirty = false;
        activityLoading = true;
        var generation = Interlocked.Increment(ref activityRequestGeneration);
        bool IsCurrent() => IsCurrentAccountRequest(request) &&
            generation == activityRequestGeneration &&
            string.Equals(request.Session.Token, bearerToken, StringComparison.Ordinal);
        return Task.Run(async () =>
        {
            try
            {
                var page = await activityClient.GetFeedAsync(bearerToken, before).ConfigureAwait(false);
                var applied = false;
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (!IsCurrent()) return;
                    if (page is null)
                    {
                        activityDirty = true;
                        activityNextRetryUtc = DateTime.UtcNow.AddSeconds(15);
                        return;
                    }
                    activityNextRetryUtc = DateTime.MinValue;
                    var visibleItems = page.Items.Where(item => !IsRemovedPostActivity(item)).ToArray();
                    activityItems = reset ? visibleItems : [.. activityItems, .. visibleItems];
                    activityNextCursor = page.NextCursor;
                    applied = true;
                }).ConfigureAwait(false);
                if (applied && page is { Items.Length: > 0 } && reset)
                    await activityClient.MarkReadAsync(bearerToken, page.Items[0].CreatedAtUnix).ConfigureAwait(false);
            }
            catch (Exception)
            {
                AepLog.Warning("[Activity] Refresh failed.");
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (!IsCurrent()) return;
                    activityDirty = true;
                    activityNextRetryUtc = DateTime.UtcNow.AddSeconds(15);
                }).ConfigureAwait(false);
            }
            finally
            {
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (IsCurrent()) activityLoading = false;
                }).ConfigureAwait(false);
            }
        });
    }

    private void ResetActivityForAccount(string? accountId, string? token)
    {
        Interlocked.Increment(ref activityRequestGeneration);
        activityAccountId = accountId;
        activitySessionToken = token;
        activityItems = [];
        activityNextCursor = null;
        activityDirty = true;
        activityLoading = false;
        activityNextRetryUtc = DateTime.MinValue;
    }

    private static bool IsRemovedPostActivity(ActivityEventDto item) =>
        item.Type is "PostLiked" or "PostReplied" or "Mentioned";
}

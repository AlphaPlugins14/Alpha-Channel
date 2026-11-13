namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private async Task LoadSubscriptionChannelNameAsync(string channelId)
    {
        try
        {
            var channelName = await searchResolver.GetChannelNameAsync(channelId, CancellationToken.None).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0 || string.IsNullOrWhiteSpace(channelName) ||
                    !Plugin.Cfg.SubscribedYouTubeChannelIds.Contains(channelId, StringComparer.OrdinalIgnoreCase)) return;
                Plugin.Cfg.SubscribedYouTubeChannelNames[channelId] = channelName;
                Plugin.Cfg.Save();
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            {}
        }
        finally
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) == 0) subscriptionNamesLoading.Remove(channelId);
            }).ConfigureAwait(false);
        }
    }

    private async Task AddManualYouTubeSubscriptionAsync(string requestedChannelName)
    {
        try
        {
            var results = await searchResolver.SearchWithMetadataAsync(requestedChannelName, 12, CancellationToken.None).ConfigureAwait(false);
            var matchingVideo = results.FirstOrDefault(result => !string.IsNullOrWhiteSpace(result.ChannelId) &&
                string.Equals(result.ChannelName.Trim(), requestedChannelName.Trim(), StringComparison.OrdinalIgnoreCase));
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0) return;
                if (matchingVideo is null || string.IsNullOrWhiteSpace(matchingVideo.ChannelId))
                {
                    subscriptionMessageIsError = true;
                    subscriptionMessage = $"Couldn't find an exact channel named \"{requestedChannelName}\".";
                    return;
                }
                var channelId = matchingVideo.ChannelId;
                if (Plugin.Cfg.SubscribedYouTubeChannelIds.Contains(channelId, StringComparer.OrdinalIgnoreCase))
                    subscriptionMessage = $"Already subscribed to {matchingVideo.ChannelName}.";
                else
                {
                    Plugin.Cfg.SubscribedYouTubeChannelIds.Add(channelId);
                    Plugin.Cfg.SubscribedYouTubeChannelNames[channelId] = matchingVideo.ChannelName;
                    Plugin.Cfg.Save();
                    subscriptionMessage = $"Subscribed to {matchingVideo.ChannelName}.";
                }
                subscriptionChannelInput = string.Empty;
                subscriptionMessageIsError = false;
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            AepLog.Warning("[Subscriptions] Manual subscription lookup failed.");
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0) return;
                subscriptionMessageIsError = true;
                subscriptionMessage = "Couldn't search for that YouTube channel.";
            }).ConfigureAwait(false);
        }
        finally
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) == 0) isAddingManualSubscription = false;
            }).ConfigureAwait(false);
        }
    }
}

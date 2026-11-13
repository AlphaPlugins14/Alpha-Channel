using System.Net.Http.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

internal sealed record TwitchTrendingFeed(TwitchStreamDto[] Streams, long LastUpdatedUnix, bool Stale);

internal sealed class TwitchClient
{
    internal TwitchClient(Configuration _) { }

    internal async Task<TwitchTrendingFeed?> GetTrendingAsync(CancellationToken cancellationToken = default)
    {
        // Trending is public cached metadata. Never forward account tokens or fall back to the relay.
        using var http = PluginHttpClients.CreateMetadataClient();
        http.BaseAddress = new Uri("https://twitch-directory.example.invalid");
        http.Timeout = TimeSpan.FromSeconds(15);
        try
        {
            using var response = await http.GetAsync("/api/twitch/trending", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                NetworkFailureClassifier.FromResponse("Twitch", "Fetch trending streams", response);
                return null;
            }
            var feed = await response.Content.ReadFromJsonAsync<TwitchTrendingFeed>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (feed?.Streams is not { Length: > 0 and <= 20 } || feed.LastUpdatedUnix <= 0 ||
                feed.LastUpdatedUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60 ||
                feed.Streams.Any(s => s is null || s.ViewerCount < 0 ||
                    s.ChannelName is null || s.ChannelName.Length > 100 || s.Title is null || s.Title.Length > 512 ||
                    s.GameName is null || s.GameName.Length > 128 ||
                    !Video.TwitchChannelChecker.TryNormalizeChannelName(s.Url, out _, out _)))
                throw new InvalidOperationException("Invalid Twitch Trending response.");
            NetworkFailureClassifier.Clear("Twitch");
            return feed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Twitch", "Fetch trending streams", exception);
            return null;
        }
    }
}

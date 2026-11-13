using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Core;
using System.Text.Json;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private DateTime nextStatisticsHeartbeat;
    private WorkerPushConnection? statisticsConnection;
    private string? statisticsToken, statisticsLastPayload;
    private int statisticsRoomsRequested;
    private bool statisticsRoomsBusy;
    private Task statisticsClosing = Task.CompletedTask;
    private DateTime statisticsNextChange;

    private void CloseStatisticsConnection()
    {
        var closing = statisticsConnection;
        var token = statisticsToken;
        closing?.Dispose(); statisticsConnection = null;
        var previousClosing = statisticsClosing;
        if (closing is not null && token is not null)
            statisticsClosing = Task.Run(async () => {
                try {
                    await previousClosing.ConfigureAwait(false);
                    await closing.Completion.ConfigureAwait(false);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await DiscordFeedClient.ActivityStopped(token,
                        typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "Unknown", timeout.Token).ConfigureAwait(false);
                } catch { /* Best-effort final interval; never hold up logout/unload. */ }
            });
        statisticsToken = statisticsLastPayload = null;
        Interlocked.Exchange(ref statisticsRoomsRequested, 0);
    }

    internal void TickStatistics()
    {
        if(!Plugin.ClientState.IsLoggedIn||CurrentSession is not { } session)
        { CloseStatisticsConnection(); return; }
        if(statisticsToken != session.Token)
        {
            CloseStatisticsConnection(); statisticsToken = session.Token;
            nextStatisticsHeartbeat = statisticsNextChange = DateTime.MinValue;
            var token = session.Token;
            statisticsConnection = new WorkerPushConnection(CharacterDirectoryClient.WorkerUrl.Replace("https:","wss:") + "/api/statistics/socket", token, message => {
                using var doc = JsonDocument.Parse(message);
                if(doc.RootElement.TryGetProperty("type", out var type) && type.GetString() == "rooms_requested" && statisticsToken == token)
                    Interlocked.Exchange(ref statisticsRoomsRequested, 1);
            }, beforeConnecting: statisticsClosing);
        }
        if(!statisticsRoomsBusy && Interlocked.Exchange(ref statisticsRoomsRequested, 0) != 0)
        {
            statisticsRoomsBusy = true;
            _ = Task.Run(async () => {
                try { await DiscordFeedClient.Rooms(session.Token).ConfigureAwait(false); }
                catch { /* Another connected reporter may retry the shared aggregate later. */ }
                finally { await Plugin.Framework.RunOnFrameworkThread(() => statisticsRoomsBusy = false); }
            });
        }
        if(DateTime.UtcNow < statisticsNextChange)return;
        statisticsNextChange = DateTime.UtcNow.AddSeconds(2);
        // Send coarse activity only: no media URLs, titles, locations or messages.
        var hosting=stream.Mode==StreamMode.Hosting&&stream.IsConnected;
        var engine=screenController.Engine;
        var type=ActiveActivity!=ActiveActivityKind.None?"arcade":engine.IsPlayingGame?"retro":
            engine.IsPlayingBrowser?"browser":video.IsAudioOnly?"audio":AdminBroadcastContentType() switch
            {"DJ / Radio"=>"dj","Live stream"=>"live",_=>"videos"};
        var playing=video.State==Video.VideoPlaybackState.Playing&&!video.GetProgress().Paused;
        var activity=type is "arcade" or "retro"?"game":playing?(type is "audio" or "dj"?"audio":"video"):"none";
        var payload=new{activity,contentType=type,hosting,sessionId=hosting?stream.HostingSessionId:null,
            version=typeof(Plugin).Assembly.GetName().Version?.ToString()??"Unknown"};
        var json = JsonSerializer.Serialize(payload);
        if(json == statisticsLastPayload && DateTime.UtcNow < nextStatisticsHeartbeat)return;
        statisticsLastPayload = json;
        statisticsNextChange = DateTime.UtcNow.AddSeconds(2);
        nextStatisticsHeartbeat = DateTime.UtcNow.AddMinutes(5);
        statisticsConnection?.Publish(json);
    }
}

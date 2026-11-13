using System.Text.Json;
using Windows.Media.Control;
using Windows.Media;

// Reads the local desktop media session; playback commands require explicit UI actions.
var command = args.Length > 0 ? args[0] : "read";
var selectedId = args.Length > 1 && !string.IsNullOrEmpty(args[1]) ? args[1] : null;
var seekSeconds = args.Length > 2 && double.TryParse(args[2], System.Globalization.NumberStyles.Float,
    System.Globalization.CultureInfo.InvariantCulture, out var requestedSeconds) && double.IsFinite(requestedSeconds)
    ? Math.Clamp(requestedSeconds, 0, TimeSpan.MaxValue.TotalSeconds / 2) : 30;
string? artworkBase64 = null;
var fields = new Dictionary<string, string>();
string status;
string? automationStatus = null;
void Read(string name, Func<object?> get)
{
    try
    {
        var value = get();
        fields[name] = value is null || value is string text && string.IsNullOrWhiteSpace(text)
            ? "Not supplied" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "Not supplied";
    }
    catch (Exception e) { fields[name] = "Unavailable (" + e.GetType().Name + ")"; }
}
try
{
    if (command == "play-link")
    {
        try { automationStatus = await SpotifyWindowAutomation.PlayAsync(args.ElementAtOrDefault(2), selectedId, fields); }
        catch (Exception e) { automationStatus = "Could not start the Spotify song: " + e.Message; }
        command = "read";
    }
    var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
    var sessions = manager.GetSessions();
    fields["Session / Windows API"] = "Available";
    fields["Session / Total media sessions"] = sessions.Count.ToString();
    fields["Session / Application IDs found"] = string.Join("; ", sessions.Select(s => s.SourceAppUserModelId));
    var matches = sessions.Where(s => s.SourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase)).ToArray();
    fields["Session / Spotify sessions found"] = matches.Length.ToString();
    var current = manager.GetCurrentSession();
    var session = selectedId is not null ? matches.FirstOrDefault(s => s.SourceAppUserModelId == selectedId) :
        matches.FirstOrDefault(s => s.SourceAppUserModelId == current?.SourceAppUserModelId) ?? matches.FirstOrDefault();
    if (session is null)
        status = "No Spotify desktop media session found. Open Spotify and play a song.";
    else
    {
        status = "Spotify desktop media session detected";
        fields["Session / Selected application ID"] = session.SourceAppUserModelId;
        fields["Session / Is Windows current media application"] = (session.SourceAppUserModelId == current?.SourceAppUserModelId).ToString();
        if (command != "read")
        {
            if (selectedId is null) throw new InvalidOperationException("Choose the detected Spotify session first.");
            fields["Command / Command"] = command;
            fields["Command / Target session"] = session.SourceAppUserModelId;
            var controls = session.GetPlaybackInfo().Controls;
            bool enabled = command switch
            {
                "play" => controls.IsPlayEnabled, "pause" => controls.IsPauseEnabled,
                "toggle" => controls.IsPlayPauseToggleEnabled, "next" => controls.IsNextEnabled,
                "previous" => controls.IsPreviousEnabled, "seek" => controls.IsPlaybackPositionEnabled,
                "shuffle" => controls.IsShuffleEnabled, "repeat" => controls.IsRepeatEnabled,
                _ => false
            };
            fields["Command / Supported at execution"] = enabled.ToString();
            if (!enabled) fields["Command / Result"] = "Not sent: command unavailable";
            else
            {
                var accepted = command switch
                {
                    "play" => await session.TryPlayAsync(), "pause" => await session.TryPauseAsync(),
                    "toggle" => await session.TryTogglePlayPauseAsync(), "next" => await session.TrySkipNextAsync(),
                    "previous" => await session.TrySkipPreviousAsync(),
                    "shuffle" => await session.TryChangeShuffleActiveAsync(!(session.GetPlaybackInfo().IsShuffleActive ?? false)),
                    "repeat" => await session.TryChangeAutoRepeatModeAsync(session.GetPlaybackInfo().AutoRepeatMode switch
                    { MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.Track, MediaPlaybackAutoRepeatMode.Track => MediaPlaybackAutoRepeatMode.List, _ => MediaPlaybackAutoRepeatMode.None }),
                    "seek" => await session.TryChangePlaybackPositionAsync(Math.Clamp(TimeSpan.FromSeconds(seekSeconds).Ticks,
                        session.GetTimelineProperties().MinSeekTime.Ticks, session.GetTimelineProperties().MaxSeekTime.Ticks)),
                    _ => false
                };
                fields["Command / Result"] = accepted ? "Accepted by Windows; verify observed effect" : "Windows returned false";
            }
        }
        try
        {
            var media = await session.TryGetMediaPropertiesAsync();
            Read("Track / Title", () => media.Title);
            Read("Track / Artist", () => media.Artist);
            Read("Track / Album", () => media.AlbumTitle);
            fields["Artwork / Supplied"] = (media.Thumbnail is not null).ToString();
            if (media.Thumbnail is not null)
            {
                using var artwork = await media.Thumbnail.OpenReadAsync();
                Read("Artwork / Content type", () => artwork.ContentType);
                Read("Artwork / Byte size", () => artwork.Size);
                try
                {
                    var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(artwork);
                    Read("Artwork / Width", () => decoder.PixelWidth);
                    Read("Artwork / Height", () => decoder.PixelHeight);
                    // Bound IPC and GPU uploads. The Windows session supplies a thumbnail,
                    // not arbitrary downloaded artwork; keep it in memory only.
                    if (artwork.Size <= 512 * 1024 && decoder.PixelWidth <= 1024 && decoder.PixelHeight <= 1024)
                    {
                        artwork.Seek(0);
                        using var reader = new Windows.Storage.Streams.DataReader(artwork);
                        var count = await reader.LoadAsync((uint)artwork.Size);
                        var bytes = new byte[count]; reader.ReadBytes(bytes);
                        artworkBase64 = Convert.ToBase64String(bytes);
                    }
                }
                catch (Exception e) { fields["Artwork / Image dimensions"] = "Unavailable (" + e.GetType().Name + ")"; }
            }
        }
        catch (Exception e) { fields["Track / Read error"] = e.GetType().Name + ": " + e.Message; }
        try
        {
            var playback = session.GetPlaybackInfo();
            Read("Playback / Status", () => playback.PlaybackStatus);
            Read("Playback / Shuffle active", () => playback.IsShuffleActive);
            Read("Playback / Repeat mode", () => playback.AutoRepeatMode);
            var controls = playback.Controls;
            Read("Controls / Play enabled", () => controls.IsPlayEnabled);
            Read("Controls / Pause enabled", () => controls.IsPauseEnabled);
            Read("Controls / Play-pause toggle enabled", () => controls.IsPlayPauseToggleEnabled);
            Read("Controls / Next enabled", () => controls.IsNextEnabled);
            Read("Controls / Previous enabled", () => controls.IsPreviousEnabled);
            Read("Controls / Seek enabled", () => controls.IsPlaybackPositionEnabled);
            Read("Controls / Shuffle enabled", () => controls.IsShuffleEnabled);
            Read("Controls / Repeat enabled", () => controls.IsRepeatEnabled);
        }
        catch (Exception e) { fields["Playback / Read error"] = e.GetType().Name + ": " + e.Message; }
        try
        {
            var timeline = session.GetTimelineProperties();
            Read("Timeline / Position", () => timeline.Position.ToString());
            Read("Timeline / Start", () => timeline.StartTime.ToString());
            Read("Timeline / Reported duration", () => (timeline.EndTime - timeline.StartTime).ToString());
            Read("Timeline / Last position update (UTC)", () => timeline.LastUpdatedTime.ToUniversalTime().ToString("O"));
        }
        catch (Exception e) { fields["Timeline / Read error"] = e.GetType().Name + ": " + e.Message; }

    }
}
catch (Exception e) { status = "Windows media session probe failed: " + e.GetType().Name + ": " + e.Message; }
if (automationStatus is not null) status = automationStatus;
Console.WriteLine(JsonSerializer.Serialize(new { Status = status, SampledAtUtc = DateTime.UtcNow, Fields = fields, ArtworkBase64 = artworkBase64 }));

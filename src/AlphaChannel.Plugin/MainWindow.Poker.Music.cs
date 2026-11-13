using System.Text.Json;
using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly RouletteMusicPlayer pokerMusicPlayer = new();
    private Task<(int Generation, List<IncompetechTrack> Tracks)>? pokerMusicLoadTask;
    private readonly List<IncompetechTrack> pokerMusicPlaylist = [];
    private int pokerMusicPlaylistIndex;
    private int pokerMusicGeneration;
    private string pokerMusicTitle = string.Empty;
    private string pokerMusicUrl = string.Empty;
    private DateTime pokerMusicStartedUtc = DateTime.MinValue;
    private string? pokerMusicFallbackAttemptedUrl;
    private int PokerMusicVolume => Plugin.Cfg.PokerMusicEnabled && !Plugin.Cfg.Muted ? Plugin.Cfg.Volume : 0;

    private void BeginPokerMusicLoad()
    {
        var generation = ++pokerMusicGeneration;
        pokerMusicLoadTask = Task.Run(async () =>
        {
            try
            {
                using var http = Net.PluginHttpClients.CreateMetadataClient();
                var json = await http.GetStringAsync(
                    "https://incompetech.com/music/royalty-free/pieces.json").ConfigureAwait(false);
                var tracks = JsonSerializer.Deserialize<List<IncompetechTrack>>(
                    json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                return (generation, tracks
                    .Where(track => !string.IsNullOrWhiteSpace(track.Filename) &&
                                    track.Genre.Split(',', StringSplitOptions.TrimEntries).Contains("11"))
                    .Select(track => track with { Title = track.Title.Trim(), Filename = track.Filename.Trim() })
                    .ToList());
            }
            catch (Exception)
            {
                AepLog.Warning("[Poker Music] Could not load the Incompetech catalogue.");
                return (generation, new List<IncompetechTrack>());
            }
        });
    }

    private void UpdatePokerMusic()
    {
        pokerMusicPlayer.SetVolume(PokerMusicVolume);
        if (stream.Mode == StreamMode.Viewing)
        {
            SynchronizePokerMusic();
            return;
        }
        if (!IsLocalActivityHost) return;
        if (pokerMusicLoadTask is { IsCompletedSuccessfully: true } completed)
        {
            pokerMusicLoadTask = null;
            var result = completed.Result;
            if (result.Generation == pokerMusicGeneration && result.Tracks.Count > 0)
            {
                pokerMusicPlaylist.Clear();
                pokerMusicPlaylist.AddRange(result.Tracks.OrderBy(_ => Random.Shared.Next()));
                pokerMusicPlaylistIndex = 0;
                PlayNextPokerMusicTrack();
            }
        }
        else if (pokerMusicLoadTask is { IsFaulted: true })
        {
            pokerMusicLoadTask = null;
        }

        if (pokerMusicPlaylist.Count > 0 && pokerMusicPlayer.Finished)
            PlayNextPokerMusicTrack();
    }

    private void PlayNextPokerMusicTrack()
    {
        if (pokerMusicPlaylist.Count == 0 || !PokerActive) return;
        if (pokerMusicPlaylistIndex >= pokerMusicPlaylist.Count)
        {
            var shuffled = pokerMusicPlaylist.OrderBy(_ => Random.Shared.Next()).ToArray();
            pokerMusicPlaylist.Clear();
            pokerMusicPlaylist.AddRange(shuffled);
            pokerMusicPlaylistIndex = 0;
        }
        var track = pokerMusicPlaylist[pokerMusicPlaylistIndex++];
        pokerMusicTitle = track.Title;
        pokerMusicUrl = "https://incompetech.com/music/royalty-free/mp3-royaltyfree/" + Uri.EscapeDataString(track.Filename);
        pokerMusicStartedUtc = DateTime.UtcNow;
        pokerMusicPlayer.Play(pokerMusicUrl, 0d, PokerMusicVolume);
        pokerRevision++;
        SendPokerState();
    }

    private void SynchronizePokerMusic()
    {
        if (string.IsNullOrWhiteSpace(pokerMusicUrl))
        {
            pokerMusicPlayer.Stop();
            return;
        }
        if (string.Equals(pokerMusicPlayer.CurrentUrl, pokerMusicUrl, StringComparison.Ordinal))
        {
            if (!pokerMusicPlayer.Failed ||
                string.Equals(pokerMusicFallbackAttemptedUrl, pokerMusicUrl, StringComparison.Ordinal))
                return;

            // Some remote MP3 servers reject or mishandle a mid-file start. Retry this
            // track once from its beginning so a viewer still gets music.
            pokerMusicFallbackAttemptedUrl = pokerMusicUrl;
            pokerMusicPlayer.Stop();
            pokerMusicPlayer.Play(pokerMusicUrl, 0d, PokerMusicVolume);
            return;
        }

        pokerMusicFallbackAttemptedUrl = null;
        var offset = pokerMusicStartedUtc == DateTime.MinValue
            ? 0d : Math.Max(0d, (DateTime.UtcNow - pokerMusicStartedUtc).TotalSeconds);
        pokerMusicPlayer.Play(pokerMusicUrl, offset, PokerMusicVolume);
    }

    private void ResetPokerMusic()
    {
        pokerMusicGeneration++;
        pokerMusicLoadTask = null;
        pokerMusicPlaylist.Clear();
        pokerMusicPlaylistIndex = 0;
        pokerMusicTitle = string.Empty;
        pokerMusicUrl = string.Empty;
        pokerMusicStartedUtc = DateTime.MinValue;
        pokerMusicFallbackAttemptedUrl = null;
        pokerMusicPlayer.Stop();
    }
}

namespace AlphaChannel.Plugin.Video;

internal sealed record AudioTrack(int Id, string Language, string Name, int? FfmpegStreamIndex, string Codec);

internal partial class MpvRenderer
{
    private AudioTrack[] audioTracks = [];
    private DateTime audioTracksRefreshUtc, audioSelectionRefreshUtc;
    private int? selectedAudioTrackId;
    private string? audioTracksSource;

    // Called under the MPV lock when a new file starts. Track IDs belong to
    // that file; let MPV choose its normal default for a different source.
    private void ResetAudioTracksForLoad(string source)
    {
        audioTracks = [];
        audioTracksRefreshUtc = audioSelectionRefreshUtc = DateTime.MinValue;
        selectedAudioTrackId = null;
        if (audioTracksSource != source)
            mpv_command(_mpvCtx, ["set", "aid", "auto", null!]);
        audioTracksSource = source;
    }

    internal IReadOnlyList<AudioTrack> GetAudioTracks()
    {
        if (_closed) return [];
        lock (_mpvLock)
        {
            if (_closed || _mpvCtx == IntPtr.Zero) return [];
            var now = DateTime.UtcNow;
            if (now < audioTracksRefreshUtc) return audioTracks;
            var tracks = new List<AudioTrack>();
            if (int.TryParse(ReadPropertyString("track-list/count"), out var count))
                for (var i = 0; i < Math.Min(count, 128); i++)
                {
                    var prefix = $"track-list/{i}/";
                    if (ReadPropertyString(prefix + "type") != "audio" ||
                        ReadPropertyString(prefix + "external") == "yes" ||
                        !int.TryParse(ReadPropertyString(prefix + "id"), out var id)) continue;
                    var ffIndex = int.TryParse(ReadPropertyString(prefix + "ff-index"), out var index) && index >= 0 ? index : (int?)null;
                    tracks.Add(new(id, ReadPropertyString(prefix + "lang") ?? "", ReadPropertyString(prefix + "title") ?? "",
                        ffIndex, ReadPropertyString(prefix + "codec") ?? ""));
                }
            audioTracks = tracks.ToArray();
            audioTracksRefreshUtc = now.AddMilliseconds(tracks.Count == 0 ? 250 : 1000);
            return audioTracks;
        }
    }

    internal int? GetSelectedAudioTrackId()
    {
        if (_closed) return null;
        lock (_mpvLock)
        {
            if (_closed || _mpvCtx == IntPtr.Zero) return null;
            var now = DateTime.UtcNow;
            if (now >= audioSelectionRefreshUtc)
            {
                var actual = ReadPropertyString("current-tracks/audio/id") ?? ReadPropertyString("aid");
                selectedAudioTrackId = int.TryParse(actual, out var id) ? id : null;
                audioSelectionRefreshUtc = now.AddMilliseconds(250);
            }
            return selectedAudioTrackId;
        }
    }

    internal bool SelectAudioTrack(int id)
    {
        if (_closed) return false;
        lock (_mpvLock)
        {
            if (_closed || _mpvCtx == IntPtr.Zero || !GetAudioTracks().Any(track => track.Id == id)) return false;
            if (mpv_command(_mpvCtx, ["set", "aid", id.ToString(System.Globalization.CultureInfo.InvariantCulture), null!]) < 0) return false;
            selectedAudioTrackId = id;
            audioSelectionRefreshUtc = DateTime.UtcNow.AddMilliseconds(250);
            InvalidateProperties();
            return true;
        }
    }
}

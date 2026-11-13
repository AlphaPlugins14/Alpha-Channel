namespace AlphaChannel.BroadcastStudio;

internal interface IStudioDeckDecoder : IDisposable
{
    double Duration { get; }
    string? Error { get; }
    bool Finished { get; }
    bool TryRead(out byte[] block);
}

// Runs only on the audio thread. Two bounded decoders feed one mix and one encoder.
// The factory also lets regression checks exercise actual transport without devices.
internal sealed class StudioDeckPlayback(Func<StudioTrack, double, IStudioDeckDecoder> createDecoder) : IDisposable
{
    private sealed class Deck
    {
        internal StudioTrack? Track;
        internal IStudioDeckDecoder? Decoder;
        internal double Position, Duration;
        internal bool Playing, RemovedFromQueue;
    }
    private readonly Deck[] decks = [new(), new()];
    private StudioTrack[] tracks = [];
    private readonly HashSet<string> removedTrackIds = [];
    internal string[] RemovedTrackIds => removedTrackIds.ToArray();
    private StudioSettings settings = new();
    private StudioTrack? cachedNext;
    private string? cachedNextSource;
    private bool nextCached;
    internal StudioTrack? UpcomingTrack => decks[1 - ActiveDeck].Track is { } prepared && !decks[1 - ActiveDeck].Playing
        ? prepared : NextTrack(ActiveDeck, 1);
    private int fadeTarget = -1;
    private float fadeStart;
    private double fadeElapsed, fadeDuration;
    internal float Crossfader { get; private set; }
    internal bool Fading => fadeTarget >= 0;
    internal bool Playing => decks.Any(d => d.Playing);
    internal int ActiveDeck => Crossfader < .5f ? 0 : 1;
    internal string? Error { get; private set; }
    internal StudioDeckSnapshot Snapshot(int index)
    {
        var d = decks[index];
        return new() { Track = d.Track, TrackId = d.Track?.Id, Playing = d.Playing, Position = d.Position,
            Duration = d.Decoder?.Duration > 0 ? d.Decoder.Duration : d.Duration };
    }
    internal void Settings(StudioSettings next)
    {
        if (next.MusicSource != settings.MusicSource) PauseAll();
        if (next.Shuffle != settings.Shuffle || next.Repeat != settings.Repeat) nextCached = false;
        settings = next;
    }
    internal void SetCrossfader(float value) { Crossfader = Math.Clamp(value, 0, 1); fadeTarget = -1; }
    internal void Queue(StudioTrack[] queue)
    {
        // Keep consumed songs out even if an older queue command arrives before
        // the plugin acknowledges their removal. Loaded decks own their tracks.
        var remaining = queue.Where(t => !removedTrackIds.Contains(t.Id)).ToArray();
        if (!tracks.SequenceEqual(remaining)) nextCached = false;
        tracks = remaining;
        removedTrackIds.RemoveWhere(id => !queue.Any(t => t.Id == id));
        for (var i = 0; i < 2; i++)
            if (decks[i].Track is { } track && !decks[i].RemovedFromQueue && !tracks.Any(t => t.Id == track.Id)) Clear(i);
        if (fadeTarget >= 0 && decks[fadeTarget].Track == null) fadeTarget = -1;
    }
    private void Clear(int index)
    {
        var d = decks[index]; d.Decoder?.Dispose(); d.Decoder = null;
        d.Track = null; d.Playing = d.RemovedFromQueue = false; d.Position = d.Duration = 0;
    }
    internal void Load(int index, string? id)
    {
        var track = tracks.FirstOrDefault(t => t.Id == id);
        if (track == null) return;
        fadeTarget = -1;
        Start(index, track, 0, false);
    }
    internal void Unload(int index) { fadeTarget = -1; Clear(index); }
    private void Start(int index, StudioTrack track, double position, bool play)
    {
        var d = decks[index]; d.Decoder?.Dispose(); d.Decoder = null; d.Playing = false;
        d.RemovedFromQueue = d.Track?.Id == track.Id && d.RemovedFromQueue;
        d.Track = track; d.Position = position; d.Duration = 0; Error = null;
        d.Decoder = createDecoder(track, position); d.Playing = play;
    }
    internal void Play(int index)
    {
        var d = decks[index];
        var track = d.Track ?? tracks.FirstOrDefault();
        if (track == null) return;
        if (d.Decoder == null || d.Decoder.Finished) Start(index, track, 0, true);
        else d.Playing = true;
    }
    internal void Pause(int index) { decks[index].Playing = false; fadeTarget = -1; }
    internal void PauseAll() { foreach (var d in decks) d.Playing = false; fadeTarget = -1; }
    internal void StopDeck(int index)
    {
        var d = decks[index]; d.Decoder?.Dispose(); d.Decoder = null;
        d.Playing = false; d.Position = 0; fadeTarget = -1;
    }
    internal void Seek(int index, double position)
    {
        var d = decks[index]; if (d.Track == null) return;
        fadeTarget = -1;
        Start(index, d.Track, Math.Clamp(position, 0, Snapshot(index).Duration), d.Playing);
    }
    private StudioTrack? NextTrack(int index, int direction)
    {
        if (direction != 1) return ChooseNextTrack(index, direction);
        var source = decks[index].Track?.Id;
        if (!nextCached || cachedNextSource != source)
        { cachedNext = ChooseNextTrack(index, direction); cachedNextSource = source; nextCached = true; }
        return cachedNext;
    }
    private StudioTrack? ChooseNextTrack(int index, int direction)
    {
        if (tracks.Length == 0) return null;
        var current = Array.FindIndex(tracks, t => t.Id == decks[index].Track?.Id);
        if (current < 0) return tracks[settings.Shuffle ? Random.Shared.Next(tracks.Length) : 0];
        var next = current + direction;
        if (settings.Shuffle && tracks.Length > 1)
        { next = Random.Shared.Next(tracks.Length - 1); if (next >= current) next++; }
        if (next < 0 || next >= tracks.Length)
        {
            if (!settings.Repeat) return direction < 0 ? tracks[0] : null;
            next = (next + tracks.Length) % tracks.Length;
        }
        return tracks[next];
    }
    internal void LegacyPlay(string? id)
    {
        var index = ActiveDeck;
        if (id != null && id != decks[index].Track?.Id)
        {
            var track = tracks.FirstOrDefault(t => t.Id == id); if (track == null) return;
            StopDeck(1 - index); Crossfader = index; Start(index, track, 0, true);
        }
        else Play(index);
    }
    internal void Next(int direction)
    {
        var index = ActiveDeck;
        if (direction < 0 && decks[index].Position > 3) { Seek(index, 0); Play(index); return; }
        var next = NextTrack(index, direction);
        if (next == null) { PauseAll(); return; }
        StopDeck(1 - index); Crossfader = index; Start(index, next, 0, true);
    }
    internal void FadeTo(int target)
    {
        if (settings.MusicSource != StudioMusicSource.SongQueue) return;
        if (decks[target].Track == null)
        {
            var next = NextTrack(1 - target, 1); if (next == null) return;
            Start(target, next, 0, false);
        }
        Play(target);
        if (Math.Abs(Crossfader - target) < .0001f) { fadeTarget = -1; return; }
        fadeStart = Crossfader; fadeElapsed = 0;
        fadeDuration = Math.Clamp(settings.FadeSeconds, 1, 20);
        var outgoing = Snapshot(1 - target);
        if (outgoing.Playing && outgoing.Duration > outgoing.Position)
            fadeDuration = Math.Min(fadeDuration, outgoing.Duration - outgoing.Position);
        var incoming = Snapshot(target);
        if (incoming.Duration > incoming.Position)
            fadeDuration = Math.Min(fadeDuration, incoming.Duration - incoming.Position);
        fadeTarget = target;
    }
    internal (byte[] A, byte[] B) Read(double seconds)
    {
        // Use a preloaded idle deck if present; otherwise prepare the next queue song.
        var active = ActiveDeck; var source = Snapshot(active);
        if (!Fading && settings.AutoNext && source.Playing && source.Duration > 0 &&
            source.Duration - source.Position <= Math.Clamp(settings.FadeSeconds, 1, 20) &&
            !decks[1 - active].Playing)
        {
            var next = decks[1 - active].Track ?? NextTrack(active, 1);
            if (next != null)
            {
                if (decks[1 - active].Track == null) Start(1 - active, next, 0, false);
                FadeTo(1 - active);
            }
        }
        var a = ReadDeck(0); var b = ReadDeck(1);
        if (Fading && (fadeTarget == 0 ? a.Length : b.Length) > 0)
        {
            // FFmpeg may report duration after playback has started. Finish a
            // short incoming track's fade before it runs out of audio.
            var incoming = Snapshot(fadeTarget);
            if (incoming.Duration > 0)
                fadeDuration = Math.Min(fadeDuration, Math.Max(.02, incoming.Duration - incoming.Position + fadeElapsed + seconds));
            fadeElapsed += seconds;
            var amount = Math.Clamp(fadeElapsed / Math.Max(.02, fadeDuration), 0, 1);
            Crossfader = (float)(fadeStart + (fadeTarget - fadeStart) * amount);
            if (amount >= 1)
            {
                var outgoing = 1 - fadeTarget; fadeTarget = -1; Clear(outgoing);
            }
        }
        return (a, b);
    }
    private byte[] ReadDeck(int index)
    {
        var d = decks[index]; if (!d.Playing || d.Decoder == null) return [];
        if (d.Decoder.Duration > 0) d.Duration = d.Decoder.Duration;
        if (d.Decoder.TryRead(out var block))
        {
            if (block.Length > 0 && settings.RemoveOnPlay && d.Track is { } track && tracks.Any(t => t.Id == track.Id))
            {
                nextCached = false;
                tracks = tracks.Where(t => t.Id != track.Id).ToArray();
                removedTrackIds.Add(track.Id);
                foreach (var deck in decks)
                    if (deck.Track?.Id == track.Id) deck.RemovedFromQueue = true;
            }
            d.Position += block.Length / 192000d;
            return block;
        }
        if (!d.Decoder.Finished) return [];
        Error = d.Decoder.Error; d.Playing = false;
        // Unknown-duration formats still advance when decoding completes.
        if (!Fading && settings.AutoNext && index == ActiveDeck && Error == null)
        {
            var target = 1 - index; var next = decks[target].Track ?? NextTrack(index, 1);
            if (next != null)
            {
                if (decks[target].Track == null) Start(target, next, 0, false);
                FadeTo(target);
            }
        }
        if (fadeTarget == index) fadeTarget = -1;
        return [];
    }
    internal void Stop() { fadeTarget = -1; Clear(0); Clear(1); }
    public void Dispose() => Stop();
}

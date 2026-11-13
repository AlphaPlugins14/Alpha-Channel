namespace AlphaChannel.BroadcastStudio;

// Commands and immutable snapshots cross a private, current-user named pipe.
// Credentials are never placed in process arguments or persisted in playlists.
public sealed record StudioTrack(string Id, string Path, string Name);
public sealed record StudioDevice(string Id, string Name);
public enum StudioMusicSource { SongQueue, Application }
public sealed record StudioApplication(int ProcessId, long StartedUtcTicks, string Name)
{
    public string ProcessName { get; init; } = "";
}
public sealed record StudioSettings
{
    public StudioMusicSource MusicSource { get; init; }
    // Process identity and capture consent are session-only; never restore capture on launch.
    public int ApplicationProcessId { get; init; }
    public long ApplicationStartedUtcTicks { get; init; }
    public bool ApplicationCaptureEnabled { get; init; }
    public bool MonitorApplication { get; init; }
    public float MusicVolume { get; init; } = .75f;
    public float DeckAVolume { get; init; } = .75f;
    public float DeckBVolume { get; init; } = .75f;
    public bool DecksConfigured { get; init; }
    public float Crossfader { get; init; }
    public double FadeSeconds { get; init; } = 5;
    public float MicVolume { get; init; } = .8f;
    public float MasterVolume { get; init; } = 1;
    public float MonitorVolume { get; init; } = .7f;
    public bool MicEnabled { get; init; }
    public bool MonitorMic { get; init; }
    public bool PushToTalk { get; init; }
    public bool TalkHeld { get; init; }
    public bool DuckMusic { get; init; } = true;
    public float DuckAmount { get; init; } = .5f;
    public bool AutoNext { get; init; } = true;
    public bool Shuffle { get; init; }
    public bool Repeat { get; init; }
    public bool RemoveOnPlay { get; init; }
    public string MicDevice { get; init; } = "";
    public string OutputDevice { get; init; } = "";
    public int Bitrate { get; init; } = 192;
}
public sealed record StudioDestination(string Host, int Port, string Mount, string User, string Password, string Name)
{
    public bool UseTls { get; init; }
}
public sealed record StudioCommand(string Kind)
{
    public int ExcludedProcessId { get; init; }
    public string? Ffmpeg { get; init; }
    public StudioSettings? Settings { get; init; }
    public StudioTrack[]? Tracks { get; init; }
    public string? TrackId { get; init; }
    public int Deck { get; init; }
    public double Position { get; init; }
    public StudioDestination? Destination { get; init; }
}
public sealed record StudioDeckSnapshot
{
    public StudioTrack? Track { get; init; }
    public string? TrackId { get; init; }
    public bool Playing { get; init; }
    public double Position { get; init; }
    public double Duration { get; init; }
}
public sealed record StudioSnapshot
{
    public string? PreviewUrl { get; init; }
    public string[] RemovedTrackIds { get; init; } = [];
    public StudioTrack? NextTrack { get; init; }
    public StudioDeckSnapshot DeckA { get; init; } = new();
    public StudioDeckSnapshot DeckB { get; init; } = new();
    public float Crossfader { get; init; }
    public bool Fading { get; init; }
    public bool ApplicationCaptureSupported { get; init; }
    public bool ApplicationCapturing { get; init; }
    public string? ApplicationError { get; init; }
    public StudioApplication[] Applications { get; init; } = [];
    public bool Ready { get; init; }
    public bool Playing { get; init; }
    public string? TrackId { get; init; }
    public double Position { get; init; }
    public double Duration { get; init; }
    public float MusicLevel { get; init; }
    public float MicLevel { get; init; }
    public float OutputLeft { get; init; }
    public float OutputRight { get; init; }
    public bool Clipping { get; init; }
    public bool MicActive { get; init; }
    public string BroadcastState { get; init; } = "OFF AIR";
    public double BroadcastSeconds { get; init; }
    public string? Error { get; init; }
    public string? DeviceError { get; init; }
    public StudioDevice[] Microphones { get; init; } = [];
    public StudioDevice[] Outputs { get; init; } = [];
}
public sealed class StudioPlaylist
{
    public string Name { get; set; } = "";
    public List<StudioTrack> Tracks { get; set; } = [];
}

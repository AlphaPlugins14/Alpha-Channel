namespace AlphaChannel.Plugin.Video;

// A delayed startup completion must never clear or re-enable a newer video's message.
internal sealed class LoadingConnectionState
{
    private readonly object gate = new();
    private long version;
    private bool waiting;
    internal const string YouTubeMessage = "Establishing connection to YouTube";
    private string message = YouTubeMessage;
    internal long Version { get { lock (gate) return version; } }
    internal bool Waiting { get { lock (gate) return waiting; } }
    internal string Message { get { lock (gate) return message; } }
    internal void Reset() { lock (gate) { version++; waiting = false; } }
    internal bool Set(long expectedVersion, bool value, string text = YouTubeMessage)
    {
        lock (gate)
        {
            if (version != expectedVersion) return false;
            waiting = value; message = text; return true;
        }
    }
}

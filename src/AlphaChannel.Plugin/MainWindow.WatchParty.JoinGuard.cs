namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private const string JoinActivityMessage = "End your current activity before joining a Watch Party.";
    private volatile bool viewerJoinPlaybackPrepared;
    private bool ActivityBlocksWatchPartyJoin => PlaybackActivityOwnsTv || StudioOwnsPlaybackControls || IsPlayingOwnIcecastDjStream;

    private bool EnsureWatchPartyJoinAllowed(bool silent=false)
    {
        if(silent&&stream.CapacityPrompt is not null)return false;
        if (stream.IsClosingPreviousRoomConnection)
        {
            if (!silent) ShowRoomClosingNotice();
            return false;
        }
        var reason=stream.WatchPartyJoinBlockReason ?? (ActivityBlocksWatchPartyJoin ? JoinActivityMessage : null);
        if(reason is null) return true;
        if(!silent)
        {
            joinError=reason;
            Plugin.ChatGui.Print("[AlphaChannel] "+reason);
        }
        return false;
    }

    // Called on the framework thread immediately before the join is sent,
    // including capacity retries, reconnects and invitation fallbacks.
    internal bool PrepareWatchPartyViewerJoin()
    {
        if (Volatile.Read(ref disposeStarted) != 0) return false;
        if (stream.Mode == StreamMode.Hosting) return false;
        if (stream.Mode == StreamMode.Viewing) return true; // reconnect an existing viewer
        if (!stream.IsJoiningWatchParty) return false; // cancelled while waiting for the framework
        if (ActivityBlocksWatchPartyJoin)
        {
            joinError = JoinActivityMessage;
            NotifyActivityStartBlocked();
            return false;
        }
        // This disarms auto-advance and saves progress without deleting any
        // personal queue entries. Exclusive activities never reach this path.
        personalQueue.StopPlayback();
        viewerJoinPlaybackPrepared = true;
        return true;
    }
}

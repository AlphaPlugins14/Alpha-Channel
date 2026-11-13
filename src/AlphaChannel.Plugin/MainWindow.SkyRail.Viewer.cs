namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool skyRailViewerSnapshotLogged, skyRailViewerFrameLogged;

    private void ActivateSkyRailViewerCanvas(string canvasId)
    {
        // A validated snapshot is sufficient to initialise the game canvas.
        // Never let an old game reclaim a TV that now belongs to another activity.
        if (stream.Mode != StreamMode.Viewing || !SkyRailActive ||
            ActivityFromCanvasUrl(stream.CurrentRoomState?.Url) != ActiveActivityKind.SkyRail ||
            !string.Equals(canvasId, whiteboardSessionId, StringComparison.Ordinal)) return;
        skyRailWhiteboardSessionId = canvasId;
        whiteboardActive = true;
        whiteboardTvDirty = true;
        if (ShouldRenderArcadeTv && !screenController.Engine.IsPlayingWhiteboard)
            screenController.Engine.StartWhiteboard();
        if (!skyRailViewerSnapshotLogged)
        {
            skyRailViewerSnapshotLogged = true;
            
        }
    }

    private static Action<Exception>? AirForceRelayFailureObserver(string game)
    {
        if (game != "sky-rail") return null;
        long lastWarning = 0;
        return exception =>
        {
            var now = Environment.TickCount64;
            var previous = Volatile.Read(ref lastWarning);
            if (previous != 0 && now - previous < 30000 ||
                Interlocked.CompareExchange(ref lastWarning, now, previous) != previous) return;
            AepLog.Warning("[Air Force EX] Cloudflare connection failed: [redacted].");
        };
    }
}

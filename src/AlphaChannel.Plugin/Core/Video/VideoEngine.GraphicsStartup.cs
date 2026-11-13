namespace AlphaChannel.Plugin.Video;

internal sealed partial class VideoEngine
{
    private readonly object graphicsStartupLock = new();
    private bool graphicsStartupFailed;
    private bool keepGameUiVisibleOverTv = true;
    private TvFrameSelection frameSelection = new();
    private MusicSceneEffects musicSceneEffects = MusicSceneEffects.Default;
    private bool musicSceneActive;
    private AudioScreenTextSelection audioScreenText = AudioScreenTextSelection.Empty;
    private Core.PartyCountdownView? partyCountdown;
    private const string GraphicsUnavailable = "Alpha Channel could not start its graphics. Reload the plugin to try again.";

    internal bool TryInitializeGraphics()
    {
        if (_disposing) return false;
        if (graphics is not null) return true;
        if (!Plugin.Framework.IsInFrameworkUpdateThread && !Plugin.IsDrawingPluginUi)
        {
            LastError = "Graphics startup requires the game thread. Try starting playback again from Alpha Channel.";
            return false;
        }
        lock (graphicsStartupLock)
        {
            if (_disposing) return false;
            if (graphics is not null) return true;
            if (graphicsStartupFailed)
            {
                LastError = GraphicsUnavailable;
                return false;
            }

            try
            {
                // The framework tick and Dalamud UI draw run on the game's
                // presentation path. Waiting for the next Present there
                // would block the same thread needed to deliver it.
                DxHandler.Initialise(Plugin.PluginInterface, onGameThread: true);
                graphics = DxHandler.RunOnRenderThreadAndWait(
                    "AlphaChannel.VideoEngine.Initialize", CreateConfiguredGpuResources,
                    TimeSpan.FromSeconds(30));
                return true;
            }
            catch (DxHandler.PreviousGraphicsCleanupException)
            {
                LastError = "Graphics are still shutting down from the previous plugin session. Try again in a moment.";
                return false;
            }
            catch (Exception exception)
            {
                // Automatic previews and party updates must not retry a
                // failing hook on every frame.
                graphicsStartupFailed = true;
                LastError = GraphicsUnavailable;
                AepLog.Warning($"[Graphics] Initialization failed ({exception.GetType().Name}, 0x{exception.HResult:X8}).");
                try { DxHandler.Dispose(); }
                catch (Exception cleanupError)
                {
                    AepLog.CleanupFailure(ShutdownOwner.Plugin, ShutdownResource.DirectXHook, cleanupError);
                }
                Plugin.ChatGui.Print("[AlphaChannel] " + GraphicsUnavailable);
                return false;
            }
        }
    }

    private StartupGpuResources CreateConfiguredGpuResources()
    {
        var resources = CreateStartupGpuResources();
        var painter = resources.ScreenPainter;
        painter.WorldScreenVisible = () => !AudioOnlyMode && !DutyScreen.Hidden && !localScreenHidden;
        painter.VisualsSuspended = () => AudioOnlyMode;
        try
        {
            painter.KeepGameUiVisibleOverTv = keepGameUiVisibleOverTv;
            painter.FrameSelection = frameSelection;
            painter.SetMusicSceneEffects(musicSceneEffects, musicSceneActive);
            painter.SetClassicBarsTheme(_audioVisualizerTheme);
            painter.SetTransform(ScreenPosition, ScreenYaw, ScreenWidthScale, ScreenHeightScale);
            painter.SetAudioScreenText(audioScreenText);
            painter.SetPartyCountdown(partyCountdown);
            return resources;
        }
        catch
        {
            resources.Dispose();
            throw;
        }
    }
}

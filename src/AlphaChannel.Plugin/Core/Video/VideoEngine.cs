using System.Text.RegularExpressions;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Keys;
using System.Runtime.InteropServices;
using AlphaChannel.Plugin.YouTube;
namespace AlphaChannel.Plugin.Video;

internal sealed partial class VideoEngine : IDisposable
{
    // Temporary YouTube account-session test switch. Set this to false to
    // restore the normal Android -> PO-token fallback policy. This is kept in
    // one place so the test can be reverted without changing saved settings.

    internal const int ScreenWidth = 1920;
    internal const int ScreenHeight = 1080;

    //Default placement for a freshly (re)spawned screen: 2 units in front of the local player, facing
    //back towards them. Matches AlphaChannel's SpawnScreenInFrontOfLocalPlayer 1:1.
    private const float DefaultScreenSpawnDistance = 2.0f;
    internal const float MaximumScreenDistance = 60f;
    private const float DefaultScreenHeightOffset = 1.0f;
    private int retroControlHintShown;

    private void ShowRetroControlHintOnce()
    {
        if (Interlocked.Exchange(ref retroControlHintShown, 1) != 0) return;
        Plugin.ChatGui.Print("[AlphaChannel] Need to force control input back to FFXIV while playing a retro game? Press ctrl + f12");
    }

    // Allowed screen scale range.
    internal const float MinScreenScale = 0.1f;
    internal const float MaxScreenScale = 8.0f;

    // Temporarily disabled until independent width/height values can be
    // carried reliably through every deployed Watch Party relay.
    internal const bool IndependentScreenScalingEnabled = false;

    // Position sliders extend this far in either direction from ScreenSpawnAnchor.
    // The range is relative to the last spawn, recenter, or applied preset;
    // it does not impose bounds on the screen's world coordinates.
    internal const float ScreenPositionSliderRange = 10f;

    private volatile StartupGpuResources? graphics;
    private ScreenPainter? _screenPainter => graphics?.ScreenPainter;
    internal ScreenPainter? DebugScenePainter => _screenPainter;

    private readonly List<ScreenPositionPreset> _screenPresets = [];
    private readonly ScreenPresetAutoApply screenPresetAutoApply = new();
    private readonly ScreenPresetSpawnNotice screenPresetSpawnNotice = new();
    private DateTime nextPresetNoticeLocationCheckUtc;
    internal void ResetScreenPresetSpawnNotice() => screenPresetSpawnNotice.Reset();
    internal void InvalidateScreenPresetSpawnNotice(ScreenPositionPreset preset) => screenPresetSpawnNotice.Invalidate(preset);

    private string? CurrentPresetNoticeLocation() =>
        WatchPartyTvLocation.TryReadCurrent(out var location) ? location.Encode() : null;

    internal void AnnounceScreenPresetSpawn(ScreenPositionPreset preset)
    {
        if (screenPresetSpawnNotice.TryAnnounce(preset, CurrentPresetNoticeLocation()))
            Plugin.ChatGui.Print($"[AlphaChannel] TV spawned at saved location. Preset: {preset.Name}");
    }
    private DateTime nextPresetCheckUtc;
    private bool presetScreenWasActive;
    private bool presetSpawnNoticePending;
    internal ScreenPositionPreset? AppliedScreenPreset { get; private set; }

    internal void UpdateScreenPresetAutoApply(bool followingHost)
    {
        if (DateTime.UtcNow >= nextPresetNoticeLocationCheckUtc)
        {
            nextPresetNoticeLocationCheckUtc = DateTime.UtcNow.AddMilliseconds(500);
            if (CurrentPresetNoticeLocation() is { } currentLocation)
                screenPresetSpawnNotice.ObserveLocation(currentLocation);
        }
        if (!IsActive || DutyScreen.Hidden || followingHost)
        {
            presetScreenWasActive = IsActive;
            presetSpawnNoticePending = false;
            screenPresetAutoApply.Reset();
            return;
        }
        if (!presetScreenWasActive)
        {
            presetScreenWasActive = true;
            presetSpawnNoticePending = true;
            nextPresetCheckUtc = DateTime.MinValue;
        }
        if (DateTime.UtcNow < nextPresetCheckUtc) return;
        nextPresetCheckUtc = DateTime.UtcNow.AddMilliseconds(500);
        if (!WatchPartyTvLocation.TryReadCurrent(out var location))
        {
            screenPresetAutoApply.Reset();
            return;
        }
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null) return;
        var preset = screenPresetAutoApply.Observe(Plugin.Cfg.ScreenPresets, location.Encode(), player.Position,
            WatchPartyTvLocation.IsResidentialInterior());
        if (preset is not null) ApplyScreenPreset(preset);
        if (presetSpawnNoticePending && AppliedScreenPreset is { } applied)
            AnnounceScreenPresetSpawn(applied);
        presetSpawnNoticePending = false;
    }

    internal Vector3 ScreenPosition { get; private set; }
    // Auto-spawned TVs have a valid placement too. IsFixed only controls recentering.
    internal bool HasWorldPlacement => hasScreenPlacement;
    private bool hasScreenPlacement;
    private bool screenPlacementFromWatchParty;
    private readonly LocalScreenPlacement localScreenPlacement = new();
    internal bool ShouldRecenterLocalScreenOnSpawn => localScreenPlacement.ShouldRecenter(followingHost: false);

    private long nextPlacementContextCheck;
    private uint placementContextTerritory;
    private bool placementContextLoggedIn;
    internal void RefreshLocalScreenPlacementContext(bool force = false)
    {
        var now = Environment.TickCount64;
        var territory = Plugin.ClientState.TerritoryType;
        var loggedIn = Plugin.ClientState.IsLoggedIn;
        if (!force && now < nextPlacementContextCheck && territory == placementContextTerritory && loggedIn == placementContextLoggedIn) return;
        nextPlacementContextCheck = now + 500; placementContextTerritory = territory; placementContextLoggedIn = loggedIn;
        var location = CurrentPresetNoticeLocation();
        if (!localScreenPlacement.Observe(Plugin.ClientState.IsLoggedIn,
                Plugin.ClientState.TerritoryType, location)) return;
        AppliedScreenPreset = null;
        // Keep coordinates intact for party snapshots, including duty transfer.
        // Only local spawn eligibility changes when this visit/session ends.
        if (!screenPlacementFromWatchParty) hasScreenPlacement = false;
        screenPresetAutoApply.Reset();
        nextPresetCheckUtc = DateTime.MinValue;
    }
    private volatile bool localScreenHidden;

    internal float ScreenYaw { get; private set; }

    //
    // ScreenScale remains as the compatibility value used by older code and
    // older Watch Party clients.
    //
    internal float ScreenScale { get; private set; } = 1.0f;

    internal bool DisableFixedScreenScaleRatio { get; private set; }

    internal float ScreenWidthScale { get; private set; } = 1.0f;

    internal float ScreenHeightScale { get; private set; } = 1.0f;

    //Center the Casting tab's position sliders around - updated only on a deliberate re-placement
    //(spawn/recenter/preset apply), never while just dragging the sliders themselves, so the window
    //doesn't shrink out from under the slider mid-drag.
    internal Vector3 ScreenSpawnAnchor { get; private set; }

    private readonly PlaybackRendererSlot<MpvRenderer> rendererSlot = new();
    private MpvRenderer? _mpvRenderer => rendererSlot.Current;

    private readonly ImageRenderer _imageRenderer =
    new();

    private static readonly TimeSpan ImageFadeDuration =
    TimeSpan.FromSeconds(
        0.75);

    private DateTime _imageClockStartedUtc =
    DateTime.MinValue;

    private double _imageElapsedBeforeClockStart;

    private bool _imagePlaybackPaused;

    private CancellationTokenSource? _imagePlaybackCancellation;

    private Task? _imagePlaybackTask;

    private bool _isPlayingImage;

    private ImageMediaSelection? _currentImageSelection;

    private AudioVisualizerMode _audioVisualizerMode =
    AudioVisualizerMode.ClassicBars;

    private AudioVisualizerTheme _audioVisualizerTheme =
    AudioVisualizerTheme.AlphaPurple;

    private Snes9xRenderer? _snesRenderer;
    private bool _isPlayingSnes;

    private GambatteRenderer? _gameBoyRenderer;
    private bool _isPlayingGameBoy;

    private GambatteRenderer? _nesRenderer;
    private bool _isPlayingNes;

    private GambatteRenderer? _gameBoyAdvanceRenderer;
    private bool _isPlayingGameBoyAdvance;

    private GambatteRenderer? _masterSystemRenderer;
    private bool _isPlayingMasterSystem;

    private GambatteRenderer? _gameGearRenderer;
    private bool _isPlayingGameGear;

    // Alpha Channel embedded-browser integration.
    private BrowserRendererBase? _browserRenderer;
    private bool _isPlayingBrowser;

    private bool _isPlayingLocalVideo;

    private readonly LocalVideoBroadcastEncoder
        _localVideoBroadcastEncoder =
            new();

    private Texture2D _screenTexture => graphics!.ScreenTexture;
    private Texture2D _visualizerTexture => graphics!.VisualizerTexture;
    private ShaderResourceView _visualizerPreviewShaderResourceView => graphics!.VisualizerPreviewShaderResourceView;
    private WhiteboardRenderer _whiteboardRenderer => graphics!.WhiteboardRenderer;
    private Texture2D _imageTransitionTexture => graphics!.ImageTransitionTexture;
    private ShaderResourceView _previewShaderResourceView => graphics!.PreviewShaderResourceView;
    private static readonly Texture2DDescription ScreenTextureDescription = new()
    {
        Width = ScreenWidth,
        Height = ScreenHeight,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm,
        BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        CpuAccessFlags = CpuAccessFlags.None,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        OptionFlags = ResourceOptionFlags.None,
    };
    private CancellationTokenSource _renderCancellation = new();
    private readonly object _renderCancellationLock = new();

    private DateTime _lastLoadYT = DateTime.MinValue;
    private static readonly Regex YtRegex = new(@"^\w+://[^/]*youtube\.\w+/|^\w+://youtu\.be/", RegexOptions.Compiled);
    private static bool IsYTURL(string url) => YtRegex.IsMatch(url);

    private bool _isActive; // whether the screen should currently be drawing for the local player
    private volatile bool _stopRequested;
    private bool _lastIdle = true;
    private int _pendingVolume = 60;
    private volatile bool _pendingOutputMuted;

    private volatile bool _rendererFailed;
    private BroadcastDiagnosticsSnapshot _lastBroadcastDiagnostics = BroadcastDiagnosticsSnapshot.Idle;
    private DateTime _lastHandledBroadcastFailureUtc = DateTime.MinValue;
    private Task? _renderTask;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> playbackWorkers = new();
    private int _playbackGeneration;
    private int _pendingMediaLoadGeneration = -1;
    internal bool MediaLoadPending => Volatile.Read(ref _pendingMediaLoadGeneration) == _playbackGeneration;
    private volatile bool _disposing;
    private int _disposeStarted;
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    private volatile bool _webResolverFallbackRunning;
    private Task? _webResolverFallbackTask;

    internal bool WebResolverFallbackRunning =>
        _webResolverFallbackRunning;

    private DateTime _lastAudioLevelUpdate =
    DateTime.MinValue;

    // Read fresh at Play() time by MpvRenderer.Initialize so a settings change takes effect on
    // the next video, not the current one - matching how the old VideoPlayer read these.
    internal bool HardwareDecoding { get; set; }

    internal int MaxQualityHeight { get; set; } = 1080;

    internal bool AllowInsecureDirectUrls { get; set; }

    // Filtered Netscape cookie jar created only after the player explicitly
    // connects the account in Alpha Channel's embedded browser. It is never
    // selected for the initial request; only the authenticated PO-token retry
    // reads it.
    internal string? CookiesPath { get; set; }

    private volatile bool halloweenPlayback;
    internal bool HalloweenPlayback
    {
        get => halloweenPlayback;
        set
        {
            if (halloweenPlayback == value) return;
            halloweenPlayback = value;
            if (value) SetAudioOnlyMode(false);
            // Recalculate from the user's base volume; never change the slider or compound the boost.
            ApplyVolume(behaviourBaseVolume, preserveOutputMute: true);
        }
    }
    internal volatile bool PreserveScreenOnPlaybackFailure;
    internal bool HalloweenPreparing { get; set; }
    internal Resources Resources { get; }
    internal event Action<int>? YouTubeSetupBlocked;
    internal readonly YouTubeAgeRestrictionCache YouTubeAgeRestrictions = new();
    internal Func<bool>? YouTubeConnectionUsable;
    internal event Action<string>? YouTubeAgeRestricted;
    private bool HasUsableYouTubeConnection() => YouTubeConnectionUsable?.Invoke() ??
        YouTubeEmbeddedBrowserSession.HasUnexpiredAuthenticationCookies(CookiesPath);
    internal bool TryBlockAgeRestrictedYouTube(string url)
    {
        if (!YouTubeAgeRestrictions.Contains(url) || HasUsableYouTubeConnection()) return false;
        YouTubeAgeRestricted?.Invoke(url);
        return true;
    }
    private bool StopForYouTubeAgeRestriction(string url, string failure, int expectedPlaybackGeneration)
    {
        if (!YouTubeAgeRestrictionCache.IsAgeRestrictedError(failure)) return false;
        var connected = HasUsableYouTubeConnection();
        // Cookie validation reads local storage. A newer load/Stop may have won
        // while that check was in progress; never fail the replacement decoder.
        if (_disposing || expectedPlaybackGeneration != Volatile.Read(ref _playbackGeneration)) return true;
        if (!YouTubeAgeRestrictions.Record(url, failure, connected)) return false;
        _webResolverFallbackRunning = false;
        _rendererFailed = true;
        LastError = "YouTube age restricted video";
        _isActive = !localScreenHidden && (PreserveScreenOnPlaybackFailure || HalloweenPlayback);
        _screenPainter?.SetLoading(_isActive);
        _screenPainter?.SetTarget(_isActive ? _screenTexture : null);
        YouTubeAgeRestricted?.Invoke(url);
        return true;
    }

    internal bool TryPrepareYouTubePlayback(string url)
    {
        if (TryBlockAgeRestrictedYouTube(url)) return false;
        if (!VideoUrlResolver.IsYouTubeUrl(url) || !Resources.PoTokens.PlaybackMustWait)
            return true;
        _ = Resources.PoTokens.EnsureInstalledAsync();
        YouTubeSetupBlocked?.Invoke(Resources.PoTokens.EstimatedWaitSeconds);
        return false;
    }

    internal VideoEngine()
    {
        Resources = new Resources();
        try
        {
            Resources.NativeLoader.Register(Resources);
            _screenPresets.AddRange(Plugin.Cfg.ScreenPresets);
            MpvRenderer.Setup(Resources);
        }
        catch
        {
            Resources.Dispose();
            throw;
        }
    }

    private static StartupGpuResources CreateStartupGpuResources()
    {
        var device =
            DxHandler.Device ??
            throw new InvalidOperationException(
                "The DirectX device is unavailable during video initialization.");

        Texture2D? screenTexture = null;
        Texture2D? visualizerTexture = null;
        ShaderResourceView? visualizerPreviewShaderResourceView = null;
        WhiteboardRenderer? whiteboardRenderer = null;
        Texture2D? imageTransitionTexture = null;
        ShaderResourceView? previewShaderResourceView = null;
        ScreenPainter? screenPainter = null;

        try
        {
            screenTexture =
                new Texture2D(
                    device,
                    ScreenTextureDescription);

            var visualizerDescription = ScreenTextureDescription;
            visualizerDescription.Width = AudioVisualizerFilter.FrameWidth;
            visualizerDescription.Height = AudioVisualizerFilter.FrameHeight;
            visualizerTexture = new Texture2D(device, visualizerDescription);
            visualizerPreviewShaderResourceView = new ShaderResourceView(device, visualizerTexture);

            whiteboardRenderer =
                new WhiteboardRenderer();

            imageTransitionTexture =
                new Texture2D(
                    device,
                    ScreenTextureDescription);

            previewShaderResourceView =
                new ShaderResourceView(
                    device,
                    screenTexture);

            screenPainter =
                new ScreenPainter();

            return new StartupGpuResources(
                screenTexture,
                whiteboardRenderer,
                imageTransitionTexture,
                previewShaderResourceView,
                screenPainter,
                visualizerTexture,
                visualizerPreviewShaderResourceView);
        }
        catch
        {
            ResourceCleanup.DisposeAll(screenPainter, visualizerPreviewShaderResourceView, visualizerTexture,
                previewShaderResourceView, imageTransitionTexture, whiteboardRenderer, screenTexture);
            throw;
        }
    }

    private sealed record StartupGpuResources(
        Texture2D ScreenTexture,
        WhiteboardRenderer WhiteboardRenderer,
        Texture2D ImageTransitionTexture,
        ShaderResourceView PreviewShaderResourceView,
        ScreenPainter ScreenPainter,
        Texture2D VisualizerTexture,
        ShaderResourceView VisualizerPreviewShaderResourceView) : IDisposable
    {
        public void Dispose() => ResourceCleanup.DisposeAll(ScreenPainter,
            VisualizerPreviewShaderResourceView, VisualizerTexture, PreviewShaderResourceView,
            ImageTransitionTexture, WhiteboardRenderer, ScreenTexture);
    }

    internal readonly DutyScreenState DutyScreen = new();
    internal event Action? LocalScreenPlaced;
    internal bool IsActive => _isActive && !AudioOnlyMode && !DutyScreen.Hidden && !localScreenHidden;
    internal event Action? ExternalPlaybackTakingOver;
    internal bool HasVideoPlaybackSession =>
        _mpvRenderer is not null ||
        (_renderTask is { IsCompleted: false } && !_stopRequested);
    internal bool IsShowingWaitingScreen => _screenPainter?.IsLoading ?? false;
    internal AlphaChannel.Plugin.Core.PartyCountdownView? PartyCountdown =>
        Volatile.Read(ref partyCountdown) is { Visible: true } view ? view : null;
    internal void SetPartyCountdown(AlphaChannel.Plugin.Core.PartyCountdownView? countdown)
    {
        Volatile.Write(ref partyCountdown, countdown);
        _screenPainter?.SetPartyCountdown(countdown);
    }
    internal bool IsShowingScreensaver => _screenPainter?.IsScreensaver ?? false;
    internal nint ScreensaverTextureHandle => _screenPainter?.ScreensaverTextureHandle ?? nint.Zero;
    internal float ScreensaverAnimationTime => _screenPainter?.ScreensaverAnimationTime ?? 0f;
    internal float LoadingAnimationTime => _screenPainter?.LoadingAnimationTime ?? 0f;
    internal bool KeepGameUiVisibleOverTv
    {
        get => keepGameUiVisibleOverTv;
        set
        {
            keepGameUiVisibleOverTv = value;
            if (_screenPainter is { } painter) painter.KeepGameUiVisibleOverTv = value;
        }
    }
    internal void SetIdleScreensaver(string? status) => _screenPainter?.SetScreensaver(status);
    internal bool IsPlayingSnes => _isPlayingSnes;
    internal bool IsPlayingGameBoy => _isPlayingGameBoy;
    internal bool IsPlayingNes => _isPlayingNes;
    internal bool IsPlayingGameBoyAdvance => _isPlayingGameBoyAdvance;
    internal bool IsPlayingMasterSystem => _isPlayingMasterSystem;
    internal bool IsPlayingGameGear => _isPlayingGameGear;
    internal bool IsPlayingGame => _isPlayingSnes || _isPlayingGameBoy || _isPlayingNes || _isPlayingGameBoyAdvance || _isPlayingMasterSystem || _isPlayingGameGear;
    internal DateTime LastGameInputUtc => _isPlayingSnes
        ? _snesRenderer?.LastInputUtc ?? DateTime.MinValue
        : (_isPlayingNes ? _nesRenderer
            : _isPlayingGameBoyAdvance ? _gameBoyAdvanceRenderer
            : _isPlayingMasterSystem ? _masterSystemRenderer
            : _isPlayingGameGear ? _gameGearRenderer
            : _gameBoyRenderer)?.LastInputUtc ?? DateTime.MinValue;
    internal bool IsPlayingBrowser => _isPlayingBrowser;
    internal bool IsBrowserBroadcasting => _browserRenderer?.IsBroadcasting == true;
    internal BrowserRendererBase? Browser => _browserRenderer;
    internal bool IsPlayingLocalVideo => _isPlayingLocalVideo;


    internal bool IsPlayingImage =>
    _isPlayingImage;

    internal bool IsPlayingWhiteboard { get; private set; }

    // These renderers take over the TV without using MPV, so VideoPlayer.State
    // is intentionally reset to Idle while they are active.
    internal bool HasActiveNonVideoPlayback =>
        IsPlayingGame ||
        _isPlayingBrowser ||
        IsPlayingWhiteboard ||
        _isPlayingImage;

    internal Func<bool>? CanStartOtherMedia { get; set; }
    internal event Action? ActivityStartBlocked;
    private bool WhiteboardBlocksMediaStart(bool videoPlayback = false)
    {
        if (!videoPlayback && !(CanStartOtherMedia?.Invoke() ?? true)) return true;
        if (!IsPlayingWhiteboard)
        {
            return false;
        }

        LastError = "Stop the active TV activity before starting other media.";
        ActivityStartBlocked?.Invoke();
        Plugin.ChatGui.Print("[AlphaChannel] Stop the active TV activity before starting other media.");
        return true;
    }

    internal void StartWhiteboard()
    {
        if (!(CanStartOtherMedia?.Invoke() ?? true)) return;
        if (_disposing)
        {
            return;
        }

        if (!TryInitializeGraphics()) return;

        if (!IsPlayingWhiteboard)
        {
            // Clear VideoPlayer/queue ownership before the canvas takes over the shared TV,
            // matching the browser and emulator paths.
            StopVideoForExternalPlayback();
        }

        LastError = null;
        IsAudioOnly = false;
        IsPlayingWhiteboard = true;
        AssignScreenForSession(_whiteboardRenderer.Texture);
        _screenPainter?.SetAudioOnly(false);
        _screenPainter?.SetAudioLevel(0f);
        _screenPainter?.SetLoading(false);
        _screenPainter?.SetTransform(
            ScreenPosition,
            ScreenYaw,
            ScreenWidthScale,
            ScreenHeightScale);
        _isActive = true;
    }

    internal void UpdateWhiteboard(byte[] pixels)
    {
        if (!IsPlayingWhiteboard)
        {
            StartWhiteboard();
        }

        if (IsPlayingWhiteboard) _whiteboardRenderer.Upload(pixels);
    }

    // Refresh the plugin preview without enabling a viewer's hidden TV.
    internal void UpdateWhiteboardPreview(byte[] pixels)
    {
        if (TryInitializeGraphics()) _whiteboardRenderer.Upload(pixels);
    }

    internal nint WhiteboardPreviewTextureHandle => graphics?.WhiteboardRenderer.PreviewHandle ?? nint.Zero;

    internal ImageMediaSelection? CurrentImageSelection =>
        _currentImageSelection;

    internal bool IsLocalVideoBroadcasting =>
    _localVideoBroadcastEncoder.IsRunning;

    internal string? LocalVideoBroadcastError =>
        _localVideoBroadcastEncoder.LastError;

    internal BroadcastDiagnosticsSnapshot BroadcastDiagnostics
    {
        get
        {
            var snapshots = new[]
            {
                _browserRenderer?.BroadcastDiagnostics,
                _snesRenderer?.BroadcastDiagnostics,
                _gameBoyRenderer?.BroadcastDiagnostics,
                _nesRenderer?.BroadcastDiagnostics,
                _gameBoyAdvanceRenderer?.BroadcastDiagnostics,
                _masterSystemRenderer?.BroadcastDiagnostics,
                _gameGearRenderer?.BroadcastDiagnostics,
                _localVideoBroadcastEncoder.Diagnostics,
            };

            return snapshots.FirstOrDefault(snapshot => snapshot?.Active == true)
                   ?? snapshots.FirstOrDefault(snapshot => snapshot?.Health == BroadcastHealth.Failed)
                   ?? (_lastBroadcastDiagnostics.Health == BroadcastHealth.Failed
                       ? _lastBroadcastDiagnostics
                       : null)
                   ?? BroadcastDiagnosticsSnapshot.Idle;
        }
    }

    internal bool IsSnesBroadcasting =>
                _snesRenderer?.IsBroadcasting ==
        true;

    internal bool IsGameBoyBroadcasting =>
        _gameBoyRenderer?.IsBroadcasting ==
        true;

    internal bool IsNesBroadcasting =>
        _nesRenderer?.IsBroadcasting == true;

    internal bool IsGameBoyAdvanceBroadcasting =>
        _gameBoyAdvanceRenderer?.IsBroadcasting == true;

    internal bool IsMasterSystemBroadcasting =>
        _masterSystemRenderer?.IsBroadcasting == true;

    internal bool IsGameGearBroadcasting =>
        _gameGearRenderer?.IsBroadcasting == true;

    internal bool SnesControlsEnabled
    {
        get;
        private set;
    } = true;

    internal bool GameBoyControlsEnabled
    {
        get;
        private set;
    } = true;

    internal bool BrowserControlsEnabled
    {
        get;
        private set;
    }

    internal bool BlockAllFfxivKeyboardInput
    {
        get;
        private set;
    }

    private bool _forceFfxivResetChordWasDown;

    internal bool GameBoyCrtFilterEnabled
    {
        get;
        private set;
    }

    internal bool NesCrtFilterEnabled { get; private set; }
    internal bool GameBoyAdvanceCrtFilterEnabled { get; private set; }
    internal bool MasterSystemCrtFilterEnabled { get; private set; }
    internal bool GameGearCrtFilterEnabled { get; private set; }

    internal bool SnesCrtFilterEnabled
    {
        get;
        private set;
    }

    internal void SetBlockAllFfxivKeyboardInput(
    bool enabled)
    {
        BlockAllFfxivKeyboardInput =
            enabled;

        if (!enabled)
        {
            _forceFfxivResetChordWasDown =
                false;
        }
    }

    internal void SetSnesCrtFilterEnabled(
    bool enabled)
    {
        SnesCrtFilterEnabled =
            enabled;

        _snesRenderer?.SetCrtFilterEnabled(
            enabled);
    }

    internal void SetGameBoyCrtFilterEnabled(
    bool enabled)
    {
        GameBoyCrtFilterEnabled =
            enabled;

        _gameBoyRenderer?
            .SetCrtFilterEnabled(
                enabled);
    }

    internal void SetBrowserControlsEnabled(bool enabled)
    {
        BrowserControlsEnabled = enabled && _isPlayingBrowser && _browserRenderer is not null;
        _forceFfxivResetChordWasDown = false;
        _browserRenderer?.Focus(BrowserControlsEnabled);
    }

    internal void SetNesCrtFilterEnabled(bool enabled)
    {
        NesCrtFilterEnabled = enabled;
        _nesRenderer?.SetCrtFilterEnabled(enabled);
    }

    internal void SetGameBoyAdvanceCrtFilterEnabled(bool enabled)
    {
        GameBoyAdvanceCrtFilterEnabled = enabled;
        _gameBoyAdvanceRenderer?.SetCrtFilterEnabled(enabled);
    }

    internal void SetMasterSystemCrtFilterEnabled(bool enabled)
    {
        MasterSystemCrtFilterEnabled = enabled;
        _masterSystemRenderer?.SetCrtFilterEnabled(enabled);
    }

    internal void SetGameGearCrtFilterEnabled(bool enabled)
    {
        GameGearCrtFilterEnabled = enabled;
        _gameGearRenderer?.SetCrtFilterEnabled(enabled);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(
      int virtualKey);


    private static bool IsSnesKeyHeld(
        VirtualKey key)
    {
        return (
            GetAsyncKeyState(
                (int)key) &
            0x8000) != 0;
    }


    //
    // =============================================================
    // Emulator keyboard ownership
    // =============================================================
    //

    private static bool IsMouseVirtualKey(
        VirtualKey key)
    {
        return key is
            VirtualKey.LBUTTON or
            VirtualKey.RBUTTON or
            VirtualKey.MBUTTON or
            VirtualKey.XBUTTON1 or
            VirtualKey.XBUTTON2;
    }


    internal static void SuppressAllFfxivKeyboardInput()
    {
        //
        // Clear every keyboard key FFXIV considers valid, but leave
        // mouse buttons untouched. Emulator input still works because
        // it is read directly through GetAsyncKeyState before this.
        //

        foreach (var key in
                 Plugin.KeyState
                     .GetValidVirtualKeys())
        {
            if (IsMouseVirtualKey(
                    key))
            {
                continue;
            }

            if (Plugin.KeyState[key])
            {
                Plugin.KeyState[key] =
                    false;
            }
        }
    }
    
    internal bool IsAudioOnly { get; private set; }

    // Hides the world-space TV without stopping or disposing playback.
    // Playback can continue while the local TV is hidden.
    internal void DespawnScreen()
    {
        localScreenHidden = true;
        if (!_isActive)
        {
            return;
        }

        _isActive = false;

        _screenPainter?.SetLoading(false);
        _screenPainter?.SetTarget(null);
        presetScreenWasActive = false;
    }

    // Restores a previously-despawned TV without restarting playback.
    // On first use, initialize placement exactly as starting content does.
    internal void RespawnScreen(bool recenter = true, bool preserveExactPlacement = false)
    {
        RefreshLocalScreenPlacementContext(force: true);
        var wasHidden = DutyScreen.Hidden;
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (DutyScreen.InDuty || localPlayer is null || !TryInitializeGraphics()) return;
        SetAudioOnlyMode(false);
        // Ordinary respawns follow this visit's placement policy. Preset,
        // manual and remote placements retain their chosen coordinates.
        if (IsActive && !wasHidden) return;
        screenPresetAutoApply.Reset();
        nextPresetCheckUtc = DateTime.MinValue;
        presetScreenWasActive = false;
        localScreenHidden = false;
        if (!preserveExactPlacement && localScreenPlacement.ShouldRecenter(screenPlacementFromWatchParty))
        {
            DutyScreen.Place();
            SpawnScreenInFrontOfLocalPlayer();
            LocalScreenPlaced?.Invoke();
        }
        // Arcade activities and Canvas retain their own texture while hidden.
        // Reattach that live frame rather than the unused video texture, even
        // when the activity has no new frame to upload after respawning.
        AssignScreenForSession(IsPlayingWhiteboard ? _whiteboardRenderer.Texture : _screenTexture);

        _isActive = true;

        _screenPainter?.SetTransform(
            ScreenPosition,
            ScreenYaw,
            ScreenWidthScale,
            ScreenHeightScale);
    }

    internal nint PreviewTextureHandle =>
        AudioOnlyMode || graphics is null ? nint.Zero : IsPlayingWhiteboard
            ? _whiteboardRenderer.PreviewHandle
            : _mpvRenderer?.AudioSpectrumEnabled == true
                ? _visualizerPreviewShaderResourceView.NativePointer
                : _previewShaderResourceView.NativePointer;

    internal nint MusicPreviewTextTexture => _screenPainter?.MusicPreviewTextTexture ?? nint.Zero;
    internal void SetMusicPreviewText(AudioScreenTextSelection text)
    {
        if (TryInitializeGraphics()) _screenPainter?.SetMusicPreviewText(text);
    }

    internal nint AudioScreenTextTextureHandle =>
        AudioOnlyMode ? nint.Zero : _screenPainter?.AudioScreenTextTextureHandle ?? nint.Zero;

    internal AudioScreenTextSelection AudioScreenTextSelection =>
        _screenPainter?.AudioScreenTextSelection ?? AudioScreenTextSelection.Empty;

    internal float AudioScreenTextAnimationTime =>
        _screenPainter?.AudioScreenTextAnimationTime ?? 0f;

    internal bool ClassicBarsActive => IsAudioOnly && _screenPainter?.ClassicBarsActive == true;
    internal float ClassicBarsAudioLevel => _screenPainter?.AudioLevel ?? 0f;

    // Set only from PlayVideo's background task on a genuine init/decode failure (e.g. mpv/yt-dlp
    // never downloaded, so mpv_create() throws DllNotFoundException) - VideoPlayer polls this from
    // GetProgress() to flip its own State/LastError, since PlayVideo itself returns long before
    // the failure is known and its caller's try/catch never sees it.
    internal string? LastError { get; private set; }
    internal void BeginProgressFrame() => _mpvRenderer?.InvalidateProperties();

    internal void PlayImage(
    ImageMediaSelection selection)
    {
        if (WhiteboardBlocksMediaStart())
        {
            return;
        }

        if (_disposing)
        {
            return;
        }

        if (selection.ImageUrls.Count == 0)
        {
            LastError =
                "The image selection contains no images.";

            return;
        }

        //
        // Slideshow scheduling will be added after still-image playback has
        // been verified. For now load the first image from either descriptor.
        //
        var imageUrl =
            selection.PrimaryImageUrl;

        //
        // Stop whichever renderer currently owns the shared TV texture.
        // StopVideo also invalidates delayed mpv work before we assign image
        // ownership to the same texture.
        //
        if (!TryInitializeGraphics()) return;
        StopVideo();

        LastError =
            null;

        IsAudioOnly =
            false;

        _screenPainter?.SetAudioOnly(
            false);

        _screenPainter?.SetAudioLevel(
            0f);

        _currentImageSelection =
            selection;

        _isPlayingImage =
            true;

        _imageElapsedBeforeClockStart =
    0d;

        _imageClockStartedUtc =
            DateTime.UtcNow;

        _imagePlaybackPaused =
            false;

        _imagePlaybackCancellation =
            new CancellationTokenSource();

        var cancellation =
            _imagePlaybackCancellation;

        var imageGeneration =
            _playbackGeneration;

        AssignScreenForSession(
            _screenTexture);

        _screenPainter?.SetLoading(
            true);

        _screenPainter?.SetTransform(
            ScreenPosition,
            ScreenYaw,
            ScreenWidthScale,
            ScreenHeightScale);

        _isActive =
            true;

        _imagePlaybackTask =
            selection.Mode ==
                ImageMediaMode.Slideshow
                ? RunSlideshowAsync(
                    selection,
                    imageGeneration,
                    cancellation)
                : LoadStillImageAsync(
                    imageUrl,
                    imageGeneration,
                    cancellation);
    }

    private async Task LoadStillImageAsync(
        string imageUrl,
        int imageGeneration,
        CancellationTokenSource cancellation)
    {
        var result =
            await _imageRenderer
                .LoadAsync(
                    imageUrl,
                    _screenTexture,
                    ScreenWidth,
                    ScreenHeight,
                    cancellation.Token)
                .ConfigureAwait(false);

        //
        // Ignore completion from an image replaced by newer media.
        //
        if (cancellation.IsCancellationRequested ||
            !ReferenceEquals(
                _imagePlaybackCancellation,
                cancellation) ||
            imageGeneration !=
                _playbackGeneration)
        {
            return;
        }

        if (!result.Success)
        {
            LastError =
                result.Error ??
                "The image could not be displayed.";

            _isPlayingImage =
                false;

            _currentImageSelection =
                null;

            _isActive =
                false;

            _screenPainter?.SetLoading(
                false);

            _screenPainter?.SetTarget(
                null);

            AepLog.Warning(
                "[Image] Playback failed.");

            return;
        }

        _screenPainter?.SetLoading(
            false);

        _isActive =
            true;

        
    }

    private async Task RunSlideshowAsync(
    ImageMediaSelection selection,
    int imageGeneration,
    CancellationTokenSource cancellation)
    {
        var imageCount =
            selection.ImageUrls.Count;

        if (imageCount == 0)
        {
            FailImagePlayback(
                "The slideshow contains no images.",
                imageGeneration,
                cancellation);

            return;
        }

        var lastDisplayedIndex =
     -1;

        var hasDisplayedImage =
            false;

        //
        // Before the first successful image, try each URL in sequence instead
        // of immediately failing the complete slideshow.
        //
        var initialCandidateIndex =
            0;

        var initialFailureCount =
            0;

        var preloadStarted =
    false;

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (!IsCurrentImageRequest(
                        imageGeneration,
                        cancellation))
                {
                    return;
                }

                var elapsedSeconds =
                    GetImageElapsedSeconds();

                var absoluteIndex =
                    (long)Math.Floor(
                        elapsedSeconds /
                        selection.SecondsPerImage);

                if (!selection.Loop &&
                    absoluteIndex >=
                        imageCount)
                {
                    CompleteNonLoopingSlideshow(
                        imageGeneration,
                        cancellation);

                    return;
                }

                var selectedIndex =
      !hasDisplayedImage
          ? initialCandidateIndex
          : selection.Loop
              ? (int)(
                  absoluteIndex %
                  imageCount)
              : (int)Math.Min(
                  absoluteIndex,
                  imageCount - 1);

                if (selectedIndex !=
                    lastDisplayedIndex)
                {
                    var useFade =
    hasDisplayedImage &&
    selection.Transition ==
        ImageTransition.Fade;

                    var result =
                        await DisplaySlideshowImageAsync(
                                selection.ImageUrls[
                                    selectedIndex],
                                useFade,
                                cancellation.Token)
                            .ConfigureAwait(false);

                    if (!IsCurrentImageRequest(
                            imageGeneration,
                            cancellation))
                    {
                        return;
                    }

                    if (result.Success)
                    {
                        lastDisplayedIndex =
                            selectedIndex;

                        hasDisplayedImage =
                            true;

                        _screenPainter?.SetLoading(
      false);

                        _isActive =
                            true;

                        

                        //
                        // Once something is visible, prepare every other slide in the
                        // background. Preloading only fills ImageRenderer's bounded pixel
                        // cache and never uploads over the image currently on the TV.
                        //
                        if (!preloadStarted)
                        {
                            preloadStarted =
                                true;

                            _ = PreloadRemainingSlideshowImagesAsync(
                                selection,
                                selectedIndex,
                                imageGeneration,
                                cancellation);
                        }
                    }
                    else if (!hasDisplayedImage)
                    {
                        initialFailureCount++;

                        AepLog.Warning(
                            "[Image] Slideshow image [redacted] could not be loaded.");

                        if (initialFailureCount >=
                            imageCount)
                        {
                            FailImagePlayback(
                                "None of the slideshow images could be displayed.",
                                imageGeneration,
                                cancellation);

                            return;
                        }

                        //
                        // Try the next image immediately. Do not wait for the slideshow
                        // timer because there is still nothing visible on the TV.
                        //
                        initialCandidateIndex =
                            (selectedIndex + 1) %
                            imageCount;

                        continue;
                    }
                    else
                    {
                        //
                        // Once at least one image is visible, retain it when a later image
                        // fails. Mark the failed position as handled for this cycle so the
                        // scheduler can continue to the following slide.
                        //
                        lastDisplayedIndex =
                            selectedIndex;

                        AepLog.Warning(
                            "[Image] Slideshow image [redacted] was skipped.");
                    }
                }

                await Task.Delay(
                        100,
                        cancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Replaced media and normal shutdown both arrive here.
        }
    }

    private async Task<ImageRenderResult> DisplaySlideshowImageAsync(
    string imageUrl,
    bool useFade,
    CancellationToken cancellationToken)
    {
        //
        // The first image and Instant transitions write directly to the
        // primary screen texture.
        //
        if (!useFade)
        {
            _screenPainter?.ClearImageTransition();

            return await _imageRenderer
                .LoadAsync(
                    imageUrl,
                    _screenTexture,
                    ScreenWidth,
                    ScreenHeight,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        //
        // Prepare the incoming image on the secondary texture. This does not
        // disturb the image currently visible on the primary screen texture.
        //
        var transitionResult =
            await _imageRenderer
                .LoadAsync(
                    imageUrl,
                    _imageTransitionTexture,
                    ScreenWidth,
                    ScreenHeight,
                    cancellationToken)
                .ConfigureAwait(false);

        if (!transitionResult.Success)
        {
            return transitionResult;
        }

        cancellationToken.ThrowIfCancellationRequested();

        _screenPainter?.SetImageTransitionTarget(
            _imageTransitionTexture);

        var transitionStartedUtc =
            DateTime.UtcNow;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var elapsed =
                DateTime.UtcNow -
                transitionStartedUtc;

            var progress =
                Math.Clamp(
                    elapsed.TotalSeconds /
                    ImageFadeDuration.TotalSeconds,
                    0d,
                    1d);

            //
            // Smoothstep prevents a visibly abrupt start or end.
            //
            var easedProgress =
                progress *
                progress *
                (3d -
                 2d *
                 progress);

            _screenPainter?.SetImageTransitionBlend(
                (float)easedProgress);

            if (progress >=
                1d)
            {
                break;
            }

            await Task.Delay(
                    16,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        //
        // The secondary image is fully visible now. Commit the same cached
        // pixels to the primary texture while the blend remains at 1, then
        // remove the temporary transition target. This avoids a visible flash
        // back to the preceding image.
        //
        var commitResult =
            await _imageRenderer
                .LoadAsync(
                    imageUrl,
                    _screenTexture,
                    ScreenWidth,
                    ScreenHeight,
                    cancellationToken)
                .ConfigureAwait(false);

        if (!commitResult.Success)
        {
            _screenPainter?.ClearImageTransition();
            return commitResult;
        }

        _screenPainter?.ClearImageTransition();

        return ImageRenderResult.Completed();
    }

    private async Task PreloadRemainingSlideshowImagesAsync(
    ImageMediaSelection selection,
    int displayedIndex,
    int imageGeneration,
    CancellationTokenSource cancellation)
    {
        var imageCount =
            selection.ImageUrls.Count;

        for (var offset = 1;
             offset < imageCount;
             offset++)
        {
            if (!IsCurrentImageRequest(
                    imageGeneration,
                    cancellation))
            {
                return;
            }

            var imageIndex =
                (displayedIndex + offset) %
                imageCount;

            var result =
                await _imageRenderer
                    .PreloadAsync(
                        selection.ImageUrls[
                            imageIndex],
                        ScreenWidth,
                        ScreenHeight,
                        cancellation.Token)
                    .ConfigureAwait(false);

            if (!IsCurrentImageRequest(
                    imageGeneration,
                    cancellation))
            {
                return;
            }

            if (!result.Success)
            {
                //
                // A failed preload is not a playback failure. The normal
                // scheduler will retry or skip it when its turn arrives.
                //
                
            }
        }
    }

    private bool IsCurrentImageRequest(
        int imageGeneration,
        CancellationTokenSource cancellation)
    {
        return !cancellation.IsCancellationRequested &&
               ReferenceEquals(
                   _imagePlaybackCancellation,
                   cancellation) &&
               imageGeneration ==
                   _playbackGeneration;
    }

    private void FailImagePlayback(
        string error,
        int imageGeneration,
        CancellationTokenSource cancellation)
    {
        if (!IsCurrentImageRequest(
                imageGeneration,
                cancellation))
        {
            return;
        }

        LastError =
            error;

        _isPlayingImage =
            false;

        _currentImageSelection =
            null;

        _isActive =
            false;

        _screenPainter?.SetLoading(
            false);

        _screenPainter?.SetTarget(
            null);

        AepLog.Warning(
            "[Image] Playback failed.");
    }

    private void CompleteNonLoopingSlideshow(
        int imageGeneration,
        CancellationTokenSource cancellation)
    {
        if (!IsCurrentImageRequest(
                imageGeneration,
                cancellation))
        {
            return;
        }

        _isPlayingImage =
            false;

        _currentImageSelection =
            null;

        _isActive =
            false;

        _screenPainter?.SetLoading(
            false);

        _screenPainter?.SetTarget(
            null);

        
    }

    private double GetImageElapsedSeconds()
    {
        if (!_isPlayingImage)
        {
            return 0d;
        }

        if (_imagePlaybackPaused)
        {
            return Math.Max(
                0d,
                _imageElapsedBeforeClockStart);
        }

        return Math.Max(
            0d,
            _imageElapsedBeforeClockStart +
            (DateTime.UtcNow -
             _imageClockStartedUtc).TotalSeconds);
    }

    private void StopImagePlayback(
        bool waitForCompletion = false)
    {

        _screenPainter?.ClearImageTransition();

        var cancellation =
            _imagePlaybackCancellation;

        var playbackTask =
            _imagePlaybackTask;

        _imagePlaybackCancellation =
            null;

        if (cancellation is not null)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // An earlier stop already completed cancellation.
            }

            if (waitForCompletion &&
                playbackTask is { IsCompleted: false })
            {
                try
                {
                    if (!playbackTask.Wait(TimeSpan.FromSeconds(3)))
                    {
                        AepLog.Warning(
                            "[Image] Playback worker did not stop within 3 seconds.");
                    }
                }
                catch (AggregateException exception)
                    when (exception.InnerExceptions.All(
                        inner => inner is OperationCanceledException))
                {
                    // Normal during shutdown.
                }
            }

            cancellation.Dispose();
        }

        _imagePlaybackTask =
            null;

        _currentImageSelection =
            null;

        _isPlayingImage =
            false;
    }

    internal void StopVideo(bool preserveScreen = false)
    {
        _pendingOutputMuted = false;
        SetAudioScreenText(AudioScreenTextSelection.Empty);

        if (IsPlayingWhiteboard)
        {
            _whiteboardRenderer.CancelPendingFrame();
            IsPlayingWhiteboard = false;
            _isActive = false;
            IsAudioOnly = false;
            _screenPainter?.SetLoading(false);
            _screenPainter?.SetTarget(null);
            return;
        }

        StopImagePlayback();
        StopLocalVideoBroadcast();
        if (_isPlayingSnes)
        {
            

            // Release every SNES button and immediately return
            // keyboard/gamepad control to FFXIV.
            SetSnesControlsEnabled(false);

            _isPlayingSnes = false;
            _isActive = false;
            IsAudioOnly = false;

            try
            {
                _snesRenderer?.Unload();
            }
            catch (Exception)
            {
                AepLog.Warning(
                    "[SNES9X] Failed to unload game.");
            }

            _screenPainter?.SetAudioOnly(false);
            _screenPainter?.SetAudioLevel(0f);
            _screenPainter?.SetLoading(false);
            _screenPainter?.SetTarget(null);

            return;
        }

        if (_isPlayingGameBoy)
        {
            

            // Release Game Boy buttons and immediately
            // return keyboard/controller input to FFXIV.
            SetGameBoyControlsEnabled(
                false);

            _isPlayingGameBoy = false;
            _isActive = false;
            IsAudioOnly = false;

            try
            {
                _gameBoyRenderer?.Unload();
            }
            catch (Exception)
            {
                AepLog.Warning(
                    "[GAMBATTE] Failed to unload game.");
            }

            _screenPainter?.SetAudioOnly(false);
            _screenPainter?.SetAudioLevel(0f);
            _screenPainter?.SetLoading(false);
            _screenPainter?.SetTarget(null);

            return;
        }

        if (_isPlayingMasterSystem)
        {
            
            SetGameBoyControlsEnabled(false);
            _isPlayingMasterSystem = false;
            _isActive = false;
            IsAudioOnly = false;
            try { _masterSystemRenderer?.Unload(); }
            catch (Exception)
            {
                AepLog.Warning("[GEARSYSTEM] Failed to unload game.");
            }
            _screenPainter?.SetAudioOnly(false);
            _screenPainter?.SetAudioLevel(0f);
            _screenPainter?.SetLoading(false);
            _screenPainter?.SetTarget(null);
            return;
        }

        if (_isPlayingGameGear)
        {
            
            SetGameBoyControlsEnabled(false);
            _isPlayingGameGear = false;
            _isActive = false;
            IsAudioOnly = false;
            try { _gameGearRenderer?.Unload(); }
            catch (Exception)
            {
                AepLog.Warning("[GEARSYSTEM] Failed to unload Game Gear game.");
            }
            _screenPainter?.SetAudioOnly(false);
            _screenPainter?.SetAudioLevel(0f);
            _screenPainter?.SetLoading(false);
            _screenPainter?.SetTarget(null);
            return;
        }

        if (_isPlayingBrowser)
        {
            
            SetBrowserControlsEnabled(false);
            _isPlayingBrowser = false;
            _isActive = false;
            IsAudioOnly = false;
            try { _browserRenderer?.Dispose(); }
            catch (Exception) { AepLog.Warning("[BROWSER] Failed to stop."); }
            _browserRenderer = null;
            _screenPainter?.SetAudioOnly(false);
            _screenPainter?.SetAudioLevel(0f);
            _screenPainter?.SetLoading(false);
            _screenPainter?.SetTarget(null);
            return;
        }

        if (_isPlayingNes)
        {
            
            SetGameBoyControlsEnabled(false);
            _isPlayingNes = false;
            _isActive = false;
            IsAudioOnly = false;
            try { _nesRenderer?.Unload(); }
            catch (Exception)
            {
                AepLog.Warning("[NESTOPIA] Failed to unload game.");
            }
            _screenPainter?.SetAudioOnly(false);
            _screenPainter?.SetAudioLevel(0f);
            _screenPainter?.SetLoading(false);
            _screenPainter?.SetTarget(null);
            return;
        }

        if (_isPlayingGameBoyAdvance)
        {
            
            SetGameBoyControlsEnabled(false);
            _isPlayingGameBoyAdvance = false;
            _isActive = false;
            IsAudioOnly = false;
            try { _gameBoyAdvanceRenderer?.Unload(); }
            catch (Exception)
            {
                AepLog.Warning("[MGBA] Failed to unload game.");
            }
            _screenPainter?.SetAudioOnly(false);
            _screenPainter?.SetAudioLevel(0f);
            _screenPainter?.SetLoading(false);
            _screenPainter?.SetTarget(null);
            return;
        }

        // Invalidate any currently-running MPV render task before
        // another playback mode is allowed to take over the screen.
        _playbackGeneration++;

        _stopRequested = true;

        _isActive = preserveScreen;
        _rendererFailed = false;
        _isPlayingLocalVideo = false;
        IsAudioOnly = false;

        _screenPainter?.SetAudioOnly(false);
        _screenPainter?.SetAudioLevel(0f);
        _screenPainter?.SetLoading(preserveScreen);


        //
        // Explicit Stop means the TV must disappear immediately.
        //
        // Do not wait for the MPV render task / delayed renderer cleanup
        // to eventually clear the screen. Local Video uses StopVideo()
        // as its deliberate "exit this exclusive mode" action.
        //

        _screenPainter?.SetTarget(preserveScreen ? _screenTexture : null);


        var renderCancellation = ReplaceRenderCancellation();

        try
        {
            renderCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Its playback task already completed cleanup.
        }

        var renderer = rendererSlot.Detach();
        renderer?.Stop();

        if (renderer is not null)
        {
            try
            {
                Task.Delay(1000).ContinueWith(_ =>
                {
                    try
                    {
                        renderer.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Already cleaned up by the render loop.
                    }
                    catch (Exception)
                    {
                        AepLog.Warning(
                            "[MPV] Failed delayed renderer dispose after video end.");
                    }
                    finally
                    {
                        renderCancellation.Dispose();
                    }
                });
            }
            catch (Exception)
            {
                AepLog.Warning(
                    "[MPV] Failed to schedule renderer cleanup.");
            }
        }
        else
        {
            renderCancellation.Dispose();
        }

    }

    private void StopVideoForExternalPlayback()
    {
        // Let the VideoPlayer/queue capture the current position and release
        // their now-playing state while MPV can still report its progress.
        // The emulator/browser then receives exclusive ownership of the TV.
        try
        {
            ExternalPlaybackTakingOver?.Invoke();
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[Video] Failed to clear playback UI before changing media mode.");
        }

        StopVideo();
    }

    private void ResetFailedRenderer()
    {
        var renderCancellation = ReplaceRenderCancellation();

        try
        {
            renderCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Its playback task already completed cleanup.
        }

        var renderer = rendererSlot.Detach();
        _rendererFailed = false;
        _webResolverFallbackRunning = false;
        _isActive = false;
        IsAudioOnly = false;

        if (_isPlayingLocalVideo)
        {
            _isPlayingLocalVideo = false;
            StopLocalVideoBroadcast();
        }

        _screenPainter?.SetAudioOnly(false);
        _screenPainter?.SetAudioLevel(0f);
        _screenPainter?.SetLoading(false);
        _screenPainter?.SetTarget(null);

        if (renderer is not null)
        {
            try
            {
                renderer.Dispose();
            }
            catch (Exception)
            {
                AepLog.Warning(
                    "[MPV] Failed renderer cleanup.");
            }
        }

        renderCancellation.Dispose();
    }

    private CancellationTokenSource GetRenderCancellation()
    {
        lock (_renderCancellationLock)
        {
            return _renderCancellation;
        }
    }

    private CancellationTokenSource ReplaceRenderCancellation()
    {
        lock (_renderCancellationLock)
        {
            var previous = _renderCancellation;
            _renderCancellation = new CancellationTokenSource();
            return previous;
        }
    }

    private void CompleteRenderCancellation(CancellationTokenSource cancellation)
    {
        lock (_renderCancellationLock)
        {
            if (ReferenceEquals(_renderCancellation, cancellation))
            {
                _renderCancellation = new CancellationTokenSource();
            }
        }

        cancellation.Dispose();
    }

    internal void ShowLiveBroadcastConnectingScreen()
    {
        ShowWaitingScreen();
        if (!_isActive) return;
        _screenPainter?.SetScreensaver(null);
        _screenPainter?.SetLoading(true);
        if (_screenPainter is { } painter) painter.SetLiveBroadcastConnectionWaiting(painter.LoadingConnectionVersion, true);
    }

    internal void ShowWaitingScreen()
    {
        if (_isActive)
        {
            return;
        }

        if (Plugin.ObjectTable.LocalPlayer is null)
        {
            return;
        }

        if (!TryInitializeGraphics()) return;
        AssignScreenForSession(_screenTexture);

        _screenPainter?.SetLoading(true);

        _isActive = true;

        _screenPainter?.SetTransform(
            ScreenPosition,
            ScreenYaw,
            ScreenWidthScale,
            ScreenHeightScale);
    }

    internal bool PlaySnes(
        string romPath)
    {
        if (WhiteboardBlocksMediaStart())
        {
            return false;
        }

        if (_isPlayingLocalVideo)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Stop the local video before starting a SNES game.");

            return false;
        }

        if (_disposing)
        {
            return false;
        }

        LastError = null;

        if (string.IsNullOrWhiteSpace(
                romPath) ||
            !File.Exists(
                romPath))
        {
            LastError =
                "SNES ROM file was not found.";

            AepLog.Warning(
                "[SNES9X] ROM not found.");

            return false;
        }

        var extension =
            Path.GetExtension(
                romPath);

        if (!extension.Equals(
                ".sfc",
                StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(
                ".smc",
                StringComparison.OrdinalIgnoreCase))
        {
            LastError =
                "Please select an .sfc or .smc SNES ROM.";

            return false;
        }

        var corePath =
            Resources.GetLocationSNES9X();

        if (string.IsNullOrWhiteSpace(
                corePath) ||
            !File.Exists(
                corePath))
        {
            LastError =
                "Snes9x is still being installed. Try again in a few seconds.";

            AepLog.Warning(
                "[SNES9X] Play requested but core is not installed.");

            return false;
        }

        // Stop whatever was previously using the TV.
        if (!TryInitializeGraphics()) return false;
        StopVideoForExternalPlayback();

        try
        {
            _snesRenderer ??=
                new Snes9xRenderer(
                    corePath,
                    Resources.RomsDirectory);

            _snesRenderer.SetCrtFilterEnabled(
                SnesCrtFilterEnabled);

            IsAudioOnly = false;

            _screenPainter?.SetAudioOnly(
                false);

            AssignScreenForSession(
                _screenTexture);

            _screenPainter?.SetLoading(
                true);

            

            bool loaded =
                _snesRenderer.Load(
                    _screenTexture,
                    romPath);

            if (!loaded)
            {
                LastError =
                    "Snes9x failed to load the ROM.";

                _screenPainter?.SetLoading(
                    false);

                _screenPainter?.SetTarget(
                    null);

                return false;
            }

            _snesRenderer.SetVolume(
                _pendingVolume);

            _isPlayingSnes = true;
            _isActive = true;

            // A newly-started game begins in SNES-control mode.
            SetSnesControlsEnabled(true);

            _screenPainter?.SetLoading(
                false);

            _screenPainter?.SetTransform(
                ScreenPosition,
                ScreenYaw,
                ScreenWidthScale,
                ScreenHeightScale);

            _screenPainter?.SetTitle(
                Path.GetFileNameWithoutExtension(
                    romPath),
                "Super Nintendo");

            
            ShowRetroControlHintOnce();

            return true;
        }
        catch (Exception exception)
        {
            _isPlayingSnes = false;
            _isActive = false;

            try { _snesRenderer?.Dispose(); }
            catch (Exception)
            {
                AepLog.Warning("[SNES9X] Failed startup cleanup.");
            }
            _snesRenderer = null;

            LastError =
                exception.Message;

            _screenPainter?.SetLoading(
                false);

            _screenPainter?.SetTarget(
                null);

            AepLog.Error(
         "[SNES9X] Failed to start game.");

            return false;
        }
    }

    // =============================================================
    // Local video broadcasting
    // =============================================================

    internal bool StartLocalVideoBroadcast(
        string sourcePath,
        string publishUrl,
        double positionSeconds,
        LocalVideoBroadcastQuality quality,
        string? subtitleFile, int? audioStreamIndex = null)
    {
        LastError =
            null;

        if (!_isPlayingLocalVideo)
        {
            LastError =
                "Start the local video before broadcasting it.";

            return false;
        }

        var ffmpegPath =
            Resources.GetLocationFFmpeg();

        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            LastError = Resources.FFmpegUnavailableMessage;
            return false;
        }

        var started =
            _localVideoBroadcastEncoder.Start(
            ffmpegPath,
            sourcePath,
            publishUrl,
            positionSeconds,
            quality,
            subtitleFile, Plugin.Cfg.LocalVideoBroadcastFramesPerSecond, audioStreamIndex);

        if (!started)
        {
            LastError =
                _localVideoBroadcastEncoder.LastError ??
                "The local video broadcast could not be started.";

            return false;
        }

        return true;
    }

    internal void StopLocalVideoBroadcast()
    {
        _localVideoBroadcastEncoder.Stop();
    }


    // =============================================================
    // SNES broadcasting
    // =============================================================

    internal bool StartSnesBroadcast(
        string publishUrl,
        GameBroadcastQuality quality)
    {
        LastError =
            null;

        if (!_isPlayingSnes ||
            _snesRenderer is null)
        {
            LastError =
                "Start an SNES game before broadcasting.";

            AepLog.Warning(
                "[GAME-BROADCAST] Broadcast requested while no SNES game is running.");

            return false;
        }

        if (_snesRenderer.IsBroadcasting)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(
                publishUrl))
        {
            LastError =
                "The broadcast publish URL was empty.";

            AepLog.Warning(
                "[GAME-BROADCAST] SNES broadcast requested with an empty publish URL.");

            return false;
        }

        var ffmpegPath =
            Resources.GetLocationFFmpeg();

        if (string.IsNullOrWhiteSpace(
                ffmpegPath) ||
            !File.Exists(
                ffmpegPath))
        {
            LastError =
                Resources.FFmpegUnavailableMessage;

            AepLog.Warning(
                "[GAME-BROADCAST] FFmpeg is not installed.");

            return false;
        }

        //
        // IMPORTANT:
        //
        // Do not log publishUrl here.
        //
        // The final RTMP URL contains the account's private
        // stream secret.
        //

        

        var started =
            _snesRenderer.StartBroadcast(
                ffmpegPath,
                publishUrl,
                quality, Plugin.Cfg.GameBroadcastFramesPerSecond);

        if (!started)
        {
            LastError =
                "FFmpeg failed to start the SNES broadcast.";

            AepLog.Error(
                "[GAME-BROADCAST] SNES broadcast failed to start.");

            return false;
        }

        

        return true;
    }


    internal void StopSnesBroadcast()
    {
        if (_snesRenderer is null)
        {
            return;
        }

        if (!_snesRenderer.IsBroadcasting)
        {
            return;
        }

        

        _snesRenderer.StopBroadcast();

        
    }


    // =============================================================
    // Game Boy / Game Boy Color
    // =============================================================

    internal bool StartGameBoyBroadcast(
            string publishUrl,
            GameBroadcastQuality quality)
    {
        LastError =
            null;

        if (!_isPlayingGameBoy ||
            _gameBoyRenderer is null)
        {
            LastError =
                "Start a Game Boy game before broadcasting.";

            AepLog.Warning(
                "[GAME-BROADCAST] Broadcast requested while no Game Boy game is running.");

            return false;
        }

        if (_gameBoyRenderer.IsBroadcasting)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(
                publishUrl))
        {
            LastError =
                "The broadcast publish URL was empty.";

            AepLog.Warning(
                "[GAME-BROADCAST] Broadcast requested with an empty publish URL.");

            return false;
        }

        var ffmpegPath =
            Resources.GetLocationFFmpeg();

        if (string.IsNullOrWhiteSpace(
                ffmpegPath) ||
            !File.Exists(
                ffmpegPath))
        {
            LastError =
                Resources.FFmpegUnavailableMessage;

            AepLog.Warning(
                "[GAME-BROADCAST] FFmpeg is not installed.");

            return false;
        }

        //
        // IMPORTANT:
        //
        // Do not log publishUrl here.
        //
        // The final RTMP URL contains the account's private
        // stream secret.
        //

        

        var started =
            _gameBoyRenderer.StartBroadcast(
                ffmpegPath,
                publishUrl,
                quality, Plugin.Cfg.GameBroadcastFramesPerSecond);

        if (!started)
        {
            LastError =
                "FFmpeg failed to start the Game Boy broadcast.";

            AepLog.Error(
                "[GAME-BROADCAST] Game Boy broadcast failed to start.");

            return false;
        }

        

        return true;
    }


    internal void StopGameBoyBroadcast()
    {
        if (_gameBoyRenderer is null)
        {
            return;
        }

        if (!_gameBoyRenderer.IsBroadcasting)
        {
            return;
        }

        

        _gameBoyRenderer.StopBroadcast();

        
    }

    internal bool StartNesBroadcast(string publishUrl, GameBroadcastQuality quality)
    {
        LastError = null;
        if (!_isPlayingNes || _nesRenderer is null)
        {
            LastError = "Start an NES game before broadcasting.";
            return false;
        }
        if (_nesRenderer.IsBroadcasting) return true;
        if (string.IsNullOrWhiteSpace(publishUrl))
        {
            LastError = "The broadcast publish URL was empty.";
            return false;
        }
        var ffmpegPath = Resources.GetLocationFFmpeg();
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            LastError = Resources.FFmpegUnavailableMessage;
            return false;
        }
        
        if (!_nesRenderer.StartBroadcast(ffmpegPath, publishUrl, quality, Plugin.Cfg.GameBroadcastFramesPerSecond))
        {
            LastError = "FFmpeg failed to start the NES broadcast.";
            return false;
        }
        return true;
    }

    internal void StopNesBroadcast()
    {
        if (_nesRenderer?.IsBroadcasting == true) _nesRenderer.StopBroadcast();
    }

    internal bool PlayNes(string romPath)
    {
        if (WhiteboardBlocksMediaStart())
        {
            return false;
        }

        if (_isPlayingLocalVideo)
        {
            ActivityStartBlocked?.Invoke();
            Plugin.ChatGui.Print("[AlphaChannel] Stop the local video before starting an NES game.");
            return false;
        }
        if (_disposing) return false;
        LastError = null;
        if (string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath))
        {
            LastError = "NES ROM file was not found.";
            return false;
        }
        if (!Path.GetExtension(romPath).Equals(".nes", StringComparison.OrdinalIgnoreCase))
        {
            LastError = "Please select an .nes NES ROM.";
            return false;
        }
        var corePath = Resources.GetLocationNestopia();
        if (string.IsNullOrWhiteSpace(corePath) || !File.Exists(corePath))
        {
            LastError = "Nestopia is still being installed. Try again in a few seconds.";
            return false;
        }

        if (!TryInitializeGraphics()) return false;
        StopVideoForExternalPlayback();
        try
        {
            // GambatteRenderer hosts the shared software-libretro frontend;
            // the Nestopia profile selects NES content and XRGB8888 conversion.
            _gameBoyRenderer?.Dispose();
            _gameBoyRenderer = null;
            _gameBoyAdvanceRenderer?.Dispose();
            _gameBoyAdvanceRenderer = null;
            _masterSystemRenderer?.Dispose();
            _masterSystemRenderer = null;
            _gameGearRenderer?.Dispose();
            _gameGearRenderer = null;
            _nesRenderer ??= new GambatteRenderer(corePath, Resources.RomsDirectory, nestopia: true);
            _nesRenderer.SetCrtFilterEnabled(NesCrtFilterEnabled);
            IsAudioOnly = false;
            _screenPainter?.SetAudioOnly(false);
            AssignScreenForSession(_screenTexture);
            _screenPainter?.SetLoading(true);
            if (!_nesRenderer.Load(_screenTexture, romPath))
            {
                LastError = "Nestopia failed to load the ROM.";
                _screenPainter?.SetLoading(false);
                _screenPainter?.SetTarget(null);
                return false;
            }
            _nesRenderer.SetVolume(_pendingVolume);
            _isPlayingNes = true;
            _isActive = true;
            SetGameBoyControlsEnabled(true);
            _screenPainter?.SetLoading(false);
            _screenPainter?.SetTransform(ScreenPosition, ScreenYaw, ScreenWidthScale, ScreenHeightScale);
            _screenPainter?.SetTitle(Path.GetFileNameWithoutExtension(romPath), "Nintendo Entertainment System");
            
            ShowRetroControlHintOnce();
            return true;
        }
        catch (Exception exception)
        {
            _isPlayingNes = false;
            _isActive = false;
            try { _nesRenderer?.Dispose(); }
            catch (Exception) { AepLog.Warning("[NESTOPIA] Failed startup cleanup."); }
            _nesRenderer = null;
            LastError = exception.Message;
            _screenPainter?.SetLoading(false);
            _screenPainter?.SetTarget(null);
            AepLog.Error("[NESTOPIA] Failed to start game.");
            return false;
        }
    }

    internal bool StartMasterSystemBroadcast(string publishUrl, GameBroadcastQuality quality)
    {
        LastError = null;
        if (!_isPlayingMasterSystem || _masterSystemRenderer is null)
        {
            LastError = "Start a Master System or SG-1000 game before broadcasting.";
            return false;
        }
        if (_masterSystemRenderer.IsBroadcasting) return true;
        if (string.IsNullOrWhiteSpace(publishUrl))
        {
            LastError = "The broadcast publish URL was empty.";
            return false;
        }
        var ffmpegPath = Resources.GetLocationFFmpeg();
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            LastError = Resources.FFmpegUnavailableMessage;
            return false;
        }
        
        if (!_masterSystemRenderer.StartBroadcast(ffmpegPath, publishUrl, quality, Plugin.Cfg.GameBroadcastFramesPerSecond))
        {
            LastError = "FFmpeg failed to start the Master System / SG-1000 broadcast.";
            return false;
        }
        return true;
    }

    internal void StopMasterSystemBroadcast()
    {
        if (_masterSystemRenderer?.IsBroadcasting == true) _masterSystemRenderer.StopBroadcast();
    }

    internal bool PlayMasterSystem(string romPath)
    {
        if (WhiteboardBlocksMediaStart())
        {
            return false;
        }

        if (_isPlayingLocalVideo)
        {
            ActivityStartBlocked?.Invoke();
            Plugin.ChatGui.Print("[AlphaChannel] Stop the local video before starting a Master System or SG-1000 game.");
            return false;
        }
        if (_disposing) return false;
        LastError = null;
        if (string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath))
        {
            LastError = "Master System / SG-1000 ROM file was not found.";
            return false;
        }
        var extension = Path.GetExtension(romPath);
        if (!extension.Equals(".sms", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".sg", StringComparison.OrdinalIgnoreCase))
        {
            LastError = "Please select a .sms Master System or .sg SG-1000 ROM.";
            return false;
        }
        var corePath = Resources.GetLocationGearsystem();
        if (string.IsNullOrWhiteSpace(corePath) || !File.Exists(corePath))
        {
            LastError = "Gearsystem is still being installed. Try again in a few seconds.";
            return false;
        }

        if (!TryInitializeGraphics()) return false;
        StopVideoForExternalPlayback();
        try
        {
            _gameBoyRenderer?.Dispose();
            _gameBoyRenderer = null;
            _nesRenderer?.Dispose();
            _nesRenderer = null;
            _gameBoyAdvanceRenderer?.Dispose();
            _gameBoyAdvanceRenderer = null;
            _gameGearRenderer?.Dispose();
            _gameGearRenderer = null;
            _masterSystemRenderer ??= new GambatteRenderer(corePath, Resources.RomsDirectory, gearsystem: true);
            _masterSystemRenderer.SetCrtFilterEnabled(MasterSystemCrtFilterEnabled);
            IsAudioOnly = false;
            _screenPainter?.SetAudioOnly(false);
            AssignScreenForSession(_screenTexture);
            _screenPainter?.SetLoading(true);
            if (!_masterSystemRenderer.Load(_screenTexture, romPath))
            {
                LastError = "Gearsystem failed to load the ROM.";
                _screenPainter?.SetLoading(false);
                _screenPainter?.SetTarget(null);
                return false;
            }
            _masterSystemRenderer.SetVolume(_pendingVolume);
            _isPlayingMasterSystem = true;
            _isActive = true;
            SetGameBoyControlsEnabled(true);
            _screenPainter?.SetLoading(false);
            _screenPainter?.SetTransform(ScreenPosition, ScreenYaw, ScreenWidthScale, ScreenHeightScale);
            _screenPainter?.SetTitle(Path.GetFileNameWithoutExtension(romPath),
                extension.Equals(".sg", StringComparison.OrdinalIgnoreCase) ? "SG-1000" : "Sega Master System");
            
            ShowRetroControlHintOnce();
            return true;
        }
        catch (Exception exception)
        {
            _isPlayingMasterSystem = false;
            _isActive = false;
            try { _masterSystemRenderer?.Dispose(); }
            catch (Exception) { AepLog.Warning("[GEARSYSTEM] Failed startup cleanup."); }
            _masterSystemRenderer = null;
            LastError = exception.Message;
            _screenPainter?.SetLoading(false);
            _screenPainter?.SetTarget(null);
            AepLog.Error("[GEARSYSTEM] Failed to start game.");
            return false;
        }
    }

    internal bool StartGameGearBroadcast(string publishUrl, GameBroadcastQuality quality)
    {
        LastError = null;
        if (!_isPlayingGameGear || _gameGearRenderer is null)
        {
            LastError = "Start a Game Gear game before broadcasting.";
            return false;
        }
        if (_gameGearRenderer.IsBroadcasting) return true;
        if (string.IsNullOrWhiteSpace(publishUrl))
        {
            LastError = "The broadcast publish URL was empty.";
            return false;
        }
        var ffmpegPath = Resources.GetLocationFFmpeg();
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            LastError = Resources.FFmpegUnavailableMessage;
            return false;
        }
        
        if (!_gameGearRenderer.StartBroadcast(ffmpegPath, publishUrl, quality, Plugin.Cfg.GameBroadcastFramesPerSecond))
        {
            LastError = "FFmpeg failed to start the Game Gear broadcast.";
            return false;
        }
        return true;
    }

    internal void StopGameGearBroadcast()
    {
        if (_gameGearRenderer?.IsBroadcasting == true) _gameGearRenderer.StopBroadcast();
    }

    internal bool PlayGameGear(string romPath)
    {
        if (WhiteboardBlocksMediaStart())
        {
            return false;
        }

        if (_isPlayingLocalVideo)
        {
            ActivityStartBlocked?.Invoke();
            Plugin.ChatGui.Print("[AlphaChannel] Stop the local video before starting a Game Gear game.");
            return false;
        }
        if (_disposing) return false;
        LastError = null;
        if (string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath))
        {
            LastError = "Game Gear ROM file was not found.";
            return false;
        }
        var extension = Path.GetExtension(romPath);
        if (!extension.Equals(".gg", StringComparison.OrdinalIgnoreCase))
        {
            LastError = "Please select a .gg Game Gear ROM.";
            return false;
        }
        var corePath = Resources.GetLocationGearsystem();
        if (string.IsNullOrWhiteSpace(corePath) || !File.Exists(corePath))
        {
            LastError = "Gearsystem is still being installed. Try again in a few seconds.";
            return false;
        }

        if (!TryInitializeGraphics()) return false;
        StopVideoForExternalPlayback();
        try
        {
            _gameBoyRenderer?.Dispose();
            _gameBoyRenderer = null;
            _nesRenderer?.Dispose();
            _nesRenderer = null;
            _gameBoyAdvanceRenderer?.Dispose();
            _gameBoyAdvanceRenderer = null;
            _masterSystemRenderer?.Dispose();
            _masterSystemRenderer = null;
            _gameGearRenderer ??= new GambatteRenderer(corePath, Resources.RomsDirectory, gearsystem: true, gameGear: true);
            _gameGearRenderer.SetCrtFilterEnabled(GameGearCrtFilterEnabled);
            IsAudioOnly = false;
            _screenPainter?.SetAudioOnly(false);
            AssignScreenForSession(_screenTexture);
            _screenPainter?.SetLoading(true);
            if (!_gameGearRenderer.Load(_screenTexture, romPath))
            {
                LastError = "Gearsystem failed to load the Game Gear ROM.";
                _screenPainter?.SetLoading(false);
                _screenPainter?.SetTarget(null);
                return false;
            }
            _gameGearRenderer.SetVolume(_pendingVolume);
            _isPlayingGameGear = true;
            _isActive = true;
            SetGameBoyControlsEnabled(true);
            _screenPainter?.SetLoading(false);
            _screenPainter?.SetTransform(ScreenPosition, ScreenYaw, ScreenWidthScale, ScreenHeightScale);
            _screenPainter?.SetTitle(Path.GetFileNameWithoutExtension(romPath), "Game Gear");
            
            ShowRetroControlHintOnce();
            return true;
        }
        catch (Exception exception)
        {
            _isPlayingGameGear = false;
            _isActive = false;
            try { _gameGearRenderer?.Dispose(); }
            catch (Exception) { AepLog.Warning("[GEARSYSTEM] Failed Game Gear startup cleanup."); }
            _gameGearRenderer = null;
            LastError = exception.Message;
            _screenPainter?.SetLoading(false);
            _screenPainter?.SetTarget(null);
            AepLog.Error("[GEARSYSTEM] Failed to start Game Gear game.");
            return false;
        }
    }

    internal bool StartGameBoyAdvanceBroadcast(string publishUrl, GameBroadcastQuality quality)
    {
        LastError = null;
        if (!_isPlayingGameBoyAdvance || _gameBoyAdvanceRenderer is null)
        {
            LastError = "Start a Game Boy Advance game before broadcasting.";
            return false;
        }
        if (_gameBoyAdvanceRenderer.IsBroadcasting) return true;
        if (string.IsNullOrWhiteSpace(publishUrl))
        {
            LastError = "The broadcast publish URL was empty.";
            return false;
        }
        var ffmpegPath = Resources.GetLocationFFmpeg();
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            LastError = Resources.FFmpegUnavailableMessage;
            return false;
        }
        
        if (!_gameBoyAdvanceRenderer.StartBroadcast(ffmpegPath, publishUrl, quality, Plugin.Cfg.GameBroadcastFramesPerSecond))
        {
            LastError = "FFmpeg failed to start the Game Boy Advance broadcast.";
            return false;
        }
        return true;
    }

    internal void StopGameBoyAdvanceBroadcast()
    {
        if (_gameBoyAdvanceRenderer?.IsBroadcasting == true) _gameBoyAdvanceRenderer.StopBroadcast();
    }

    internal bool PlayGameBoyAdvance(string romPath)
    {
        if (WhiteboardBlocksMediaStart())
        {
            return false;
        }

        if (_isPlayingLocalVideo)
        {
            ActivityStartBlocked?.Invoke();
            Plugin.ChatGui.Print("[AlphaChannel] Stop the local video before starting a Game Boy Advance game.");
            return false;
        }
        if (_disposing) return false;
        LastError = null;
        if (string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath))
        {
            LastError = "Game Boy Advance ROM file was not found.";
            return false;
        }
        if (!Path.GetExtension(romPath).Equals(".gba", StringComparison.OrdinalIgnoreCase))
        {
            LastError = "Please select a .gba Game Boy Advance ROM.";
            return false;
        }
        var corePath = Resources.GetLocationMgba();
        if (string.IsNullOrWhiteSpace(corePath) || !File.Exists(corePath))
        {
            LastError = "mGBA is still being installed. Try again in a few seconds.";
            return false;
        }

        if (!TryInitializeGraphics()) return false;
        StopVideoForExternalPlayback();
        try
        {
            _gameBoyRenderer?.Dispose();
            _gameBoyRenderer = null;
            _nesRenderer?.Dispose();
            _nesRenderer = null;
            _masterSystemRenderer?.Dispose();
            _masterSystemRenderer = null;
            _gameGearRenderer?.Dispose();
            _gameGearRenderer = null;
            _gameBoyAdvanceRenderer ??= new GambatteRenderer(corePath, Resources.RomsDirectory, mgba: true);
            _gameBoyAdvanceRenderer.SetCrtFilterEnabled(GameBoyAdvanceCrtFilterEnabled);
            IsAudioOnly = false;
            _screenPainter?.SetAudioOnly(false);
            AssignScreenForSession(_screenTexture);
            _screenPainter?.SetLoading(true);
            if (!_gameBoyAdvanceRenderer.Load(_screenTexture, romPath))
            {
                LastError = "mGBA failed to load the ROM.";
                _screenPainter?.SetLoading(false);
                _screenPainter?.SetTarget(null);
                return false;
            }
            _gameBoyAdvanceRenderer.SetVolume(_pendingVolume);
            _isPlayingGameBoyAdvance = true;
            _isActive = true;
            SetGameBoyControlsEnabled(true);
            _screenPainter?.SetLoading(false);
            _screenPainter?.SetTransform(ScreenPosition, ScreenYaw, ScreenWidthScale, ScreenHeightScale);
            _screenPainter?.SetTitle(Path.GetFileNameWithoutExtension(romPath), "Game Boy Advance");
            
            ShowRetroControlHintOnce();
            return true;
        }
        catch (Exception exception)
        {
            _isPlayingGameBoyAdvance = false;
            _isActive = false;
            try { _gameBoyAdvanceRenderer?.Dispose(); }
            catch (Exception) { AepLog.Warning("[MGBA] Failed startup cleanup."); }
            _gameBoyAdvanceRenderer = null;
            LastError = exception.Message;
            _screenPainter?.SetLoading(false);
            _screenPainter?.SetTarget(null);
            AepLog.Error("[MGBA] Failed to start game.");
            return false;
        }
    }

    internal bool TryPrepareBrowserPlayback()
    {
        if (_disposing) return false;
        if (!IsPlayingGame) return true;

        LastError = "Stop the current game before opening a browser.";
        Plugin.ChatGui.Print($"[AlphaChannel] {LastError}");
        return false;
    }

    internal bool PlayBrowser(string initialUrl, bool useWebView = false)
    {
        // Also check at actual launch: WebView2 runtime setup can finish after
        // the user has started an emulator. Never replace a running game.
        if (!TryPrepareBrowserPlayback()) return false;
        if (WhiteboardBlocksMediaStart()) return false;
        if (_disposing) return false;
        if (!useWebView && Resources.Chromium.Status != ChromiumPackageStatus.Ready)
        {
            if (Resources.Chromium.Status == ChromiumPackageStatus.Waiting) Resources.PrepareChromium();
            LastError = Resources.Chromium.Error ?? "Downloading Chromium. Please check back soon.";
            return false;
        }
        LastError = null;
        if (!TryInitializeGraphics()) return false;
        StopVideoForExternalPlayback();
        try
        {
            var cache = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "Browser", useWebView ? "WebViewCache" : "Cache");
            _browserRenderer = useWebView ? new WebViewBrowserRenderer(_screenTexture, cache) : CreateBrowserRenderer(cache);
            _browserRenderer.SetVolume(_pendingVolume);
            if (!string.IsNullOrWhiteSpace(initialUrl)) _browserRenderer.Navigate(initialUrl);
            _isPlayingBrowser = true;
            BrowserControlsEnabled = false;
            IsAudioOnly = false;
            _screenPainter?.SetAudioOnly(false);
            AssignScreenForSession(_screenTexture);
            _isActive = true;
            _screenPainter?.SetLoading(false);
            _screenPainter?.SetTransform(ScreenPosition, ScreenYaw, ScreenWidthScale, ScreenHeightScale);
            _screenPainter?.SetTitle("Browser", "Web Browser");
            
            return true;
        }
        catch (Exception exception)
        {
            _browserRenderer?.Dispose();
            _browserRenderer = null;
            _isPlayingBrowser = false;
            _isActive = false;
            LastError = exception.Message;
            AepLog.Error("[BROWSER] Failed to start.");
            return false;
        }
    }

    private BrowserRendererBase CreateBrowserRenderer(string cache) => new BrowserRenderer(_screenTexture, cache,
        Resources.Chromium.RuntimeDirectory ?? throw new InvalidOperationException("Chromium is not installed yet."));

    internal bool StartBrowserBroadcast(
        string publishUrl,
        BrowserBroadcastQuality quality, int frameRate)
    {
        if (!_isPlayingBrowser || _browserRenderer is null)
        {
            LastError = "Open the browser before broadcasting.";
            return false;
        }
        var ffmpeg = Resources.GetLocationFFmpeg();
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
        {
            LastError = Resources.FFmpegUnavailableMessage;
            return false;
        }
        return _browserRenderer.StartBroadcast(
            ffmpeg,
            publishUrl,
            quality, frameRate);
    }

    internal void StopBrowserBroadcast() => _browserRenderer?.StopBroadcast();


    internal bool PlayGameBoy(
        string romPath)
    {
        if (WhiteboardBlocksMediaStart())
        {
            return false;
        }

        if (_isPlayingLocalVideo)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Stop the local video before starting a Game Boy game.");

            return false;
        }

        if (_disposing)
        {
            return false;
        }

        LastError =
            null;

        if (string.IsNullOrWhiteSpace(
                romPath) ||
            !File.Exists(
                romPath))
        {
            LastError =
                "Game Boy ROM file was not found.";

            AepLog.Warning(
                "[GAMBATTE] ROM not found.");

            return false;
        }

        var extension =
            Path.GetExtension(
                romPath);

        if (!extension.Equals(
                ".gb",
                StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(
                ".gbc",
                StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(
                ".dmg",
                StringComparison.OrdinalIgnoreCase))
        {
            LastError =
                "Please select a .gb, .gbc or .dmg Game Boy ROM.";

            return false;
        }

        var corePath =
            Resources.GetLocationGambatte();

        if (string.IsNullOrWhiteSpace(
                corePath) ||
            !File.Exists(
                corePath))
        {
            LastError =
                "Gambatte is still being installed. Try again in a few seconds.";

            AepLog.Warning(
                "[GAMBATTE] Play requested but core is not installed.");

            return false;
        }

        //
        // Stop whatever currently owns the TV before Gambatte takes it.
        //

        if (!TryInitializeGraphics()) return false;
        StopVideoForExternalPlayback();

        try
        {
            _nesRenderer?.Dispose();
            _nesRenderer = null;
            _gameBoyAdvanceRenderer?.Dispose();
            _gameBoyAdvanceRenderer = null;
            _masterSystemRenderer?.Dispose();
            _masterSystemRenderer = null;
            _gameGearRenderer?.Dispose();
            _gameGearRenderer = null;
            _gameBoyRenderer ??=
                new GambatteRenderer(
                    corePath,
                    Resources.RomsDirectory);

            IsAudioOnly =
                false;

            _screenPainter?.SetAudioOnly(
                false);

            AssignScreenForSession(
                _screenTexture);

            _screenPainter?.SetLoading(
                true);

            

            var loaded =
                _gameBoyRenderer.Load(
                    _screenTexture,
                    romPath);

            if (!loaded)
            {
                LastError =
                    "Gambatte failed to load the ROM.";

                _screenPainter?.SetLoading(
                    false);

                _screenPainter?.SetTarget(
                    null);

                return false;
            }

            _gameBoyRenderer.SetVolume(
                _pendingVolume);

            _gameBoyRenderer.SetCrtFilterEnabled(
                GameBoyCrtFilterEnabled);

            _isPlayingGameBoy =
                true;

            _isActive =
                true;

            // A newly-started game begins with input routed
            // to the Game Boy emulator.
            SetGameBoyControlsEnabled(
                true);

            _screenPainter?.SetLoading(
                false);

            _screenPainter?.SetTransform(
                ScreenPosition,
                ScreenYaw,
                ScreenWidthScale,
                ScreenHeightScale);

            _screenPainter?.SetTitle(
                Path.GetFileNameWithoutExtension(
                    romPath),
                extension.Equals(
                    ".gbc",
                    StringComparison.OrdinalIgnoreCase)
                    ? "Game Boy Color"
                    : "Game Boy");

            
            ShowRetroControlHintOnce();

            return true;
        }
        catch (Exception exception)
        {
            _isPlayingGameBoy =
                false;

            _isActive =
                false;

            try { _gameBoyRenderer?.Dispose(); }
            catch (Exception)
            {
                AepLog.Warning("[GAMBATTE] Failed startup cleanup.");
            }
            _gameBoyRenderer = null;

            LastError =
                exception.Message;

            _screenPainter?.SetLoading(
                false);

            _screenPainter?.SetTarget(
                null);

            AepLog.Error(
                "[GAMBATTE] Failed to start game.");

            return false;
        }
    }


    internal void PlayVideo(
    string url,
    int playbackPosition = 0,
    bool isPlaying = true,
    bool allowWebResolverFallback = true,
    bool isLocalVideo = false,
    bool expectedAudioOnly = false,
    bool forceRestart = false,
    bool preserveScreenVisibility = false)
    {
        IReadOnlyDictionary<string, string>? chosenHeaders = null;
        var recentChoice = ResolvedMediaLink.TakeRecent(url);
        var chosenPage = ResolvedMediaLink.TryDecode(url, out var chosenMedia);
        if (chosenPage) url = chosenMedia.Page;
        if (!TryPrepareYouTubePlayback(url)) return;
        if (HalloweenPlayback && HalloweenTvFrame.IsVideo(url)) isPlaying = !HalloweenPreparing;

        if (HalloweenPlayback && HalloweenTvFrame.IsVideo(url)) allowWebResolverFallback = false;
        var preserveRelayScreenOnFailure = Plugin.IsLiveBroadcastUrl(url);
        if (WhiteboardBlocksMediaStart(videoPlayback: true))
        {
            return;
        }

        if (_isPlayingImage)
        {
            Plugin.ChatGui.Print("[AlphaChannel] Stop the images or slideshow before using other media playback.");
            return;
        }
        //
        // Local Video is an exclusive TV mode, just like SNES.
        //
        // Normal URL/queue/watch-party playback must not replace it.
        // The user must explicitly stop Local Video first.
        //

        if (_isPlayingLocalVideo &&
            !isLocalVideo)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Stop the local video before using other media playback.");

            return;
        }


        if (IsPlayingGame || IsPlayingBrowser)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Stop the current game or browser to begin playback.");

            return;
        }


        if (_disposing)
        {
            return;
        }

        if (isLocalVideo && (string.IsNullOrWhiteSpace(url) || !File.Exists(url)))
        {
            LastError = "The selected local video file could not be found.";
            AepLog.Warning("[LocalVideo] File not found.");
            return;
        }
        if (!TryInitializeGraphics()) return;

        // A local-output-only mute is used while the host monitors a DJ stream.
        // Do not carry it into a different piece of media.
        if (_mpvRenderer?.GetCurrentUrl() != url)
        {
            _pendingOutputMuted = false;
            _mpvRenderer?.SetMuted(false);
        }


        //
        // Local files are handed directly to MPV.
        //
        // They never use WebMediaUrlResolver / yt-dlp fallback.
        //

        if (isLocalVideo)
        {
            allowWebResolverFallback =
                false;

            _isPlayingLocalVideo =
                true;

            
        }


        //
        // Never reuse a renderer that genuinely failed.
        //

        if (_rendererFailed)
        {
            {}


            ResetFailedRenderer();
        }


        if (_mpvRenderer is { } currentRenderer &&
            currentRenderer.GetCurrentUrl() == url &&
            !currentRenderer.IsIdle() &&
            !forceRestart)
        {
            return;
        }


        LastError =
            null;


        _stopRequested =
            false;


        IsAudioOnly =
            false;


        var playbackGeneration =
            ++_playbackGeneration;
        _screenPainter?.ResetLoadingConnection();
        Volatile.Write(ref _pendingMediaLoadGeneration, playbackGeneration);

        var useConnectedAgeSession = YouTubeAgeRestrictions.Contains(url) && HasUsableYouTubeConnection() &&
            YouTubePoTokenSupport.IsAvailable(Resources);
        var youtubeRoute =
            IsYTURL(
                url)
                ? useConnectedAgeSession || HalloweenPlayback && HalloweenTvFrame.IsVideo(url) ? YouTubePlaybackRoute.PoToken : Resources.YouTubePolicy.SelectRoute()
                : YouTubePlaybackRoute.Android;

        if (IsYTURL(url))
            YouTubeRequestScheduler.Shared.RecordPlayback(url, useConnectedAgeSession ? YouTubeRequestIdentity.Account : YouTubeRequestIdentity.Guest);
        if (IsYTURL(url))
            AepLog.PlaybackAttempt(new(PlaybackAttemptStage.RouteSelected,
                youtubeRoute == YouTubePlaybackRoute.PoToken ? PlaybackAttemptRoute.PoToken :
                    youtubeRoute == YouTubePlaybackRoute.AndroidProbe ? PlaybackAttemptRoute.AndroidProbe : PlaybackAttemptRoute.Android,
                playbackGeneration, _mpvRenderer?.DiagnosticRendererId ?? 0, _mpvRenderer?.DiagnosticLoad ?? 0,
                useConnectedAgeSession, Resources.IsYouTubeProviderRunning, _mpvRenderer is not null, false));


        //
        // Has this attempt actually begun useful playback?
        //
        // Video:
        //     first rendered frame
        //
        // Audio-only:
        //     FILE_LOADED + audio track and no video track
        //
        // We deliberately do NOT treat FILE_LOADED alone as success.
        //

        var playbackStarted =
            0;


        //
        // Prevent multiple MPV events from launching multiple resolver
        // attempts for the same URL.
        //

        var fallbackStarted =
            0;

        var authenticatedYouTubeRetryStarted = useConnectedAgeSession ? 1 : 0;

        //
        // MPV's final END_FILE error is generic. Preserve a recognisable
        // yt-dlp bot-check warning so the playback policy can make the
        // correct fallback decision.
        //
        string? youtubeVerificationError =
            null;

        string? youtubeAuthenticationError =
            null;
        string? youtubeAgeRestrictionError = null;

        var youtubePolicySuccessReported =
    0;

        void ReportYouTubeRouteSucceeded()
        {
            //
            // Normal Android and PO-token playback don't need a state
            // transition here. Only a deliberate post-timeout Android
            // probe can return the policy to normal mode.
            //
            if (youtubeRoute !=
                    YouTubePlaybackRoute.AndroidProbe ||
                Volatile.Read(
                    ref fallbackStarted) != 0 ||
                Volatile.Read(
                    ref authenticatedYouTubeRetryStarted) != 0)
            {
                return;
            }

            //
            // Frame callbacks run repeatedly. Report success only once
            // for this playback request.
            //
            if (Interlocked.Exchange(
                    ref youtubePolicySuccessReported,
                    1) != 0)
            {
                return;
            }

            Resources
                .YouTubePolicy
                .ReportPlaybackSucceeded(
                    youtubeRoute);
        }


        void StartWebResolverFallback(
      string failureMessage)
        {
            //
            // The resolved retry does not get another resolver attempt.
            //
            // fallbackStarted is scoped to this PlayVideo() call, so this
            // guarantees at most one automatic fallback for the requested
            // URL.
            //

            if (!allowWebResolverFallback)
            {
                AepLog.Warning(
                    "[WebResolver] Resolved retry failed. No further resolver attempt will be made.");

                return;
            }


            //
            // MPV can be reused across several videos.
            //
            // Do NOT use playbackStarted as a resolver gate here. Previous
            // playback/frame events on the reused renderer can leave that
            // state looking "started" while the newly requested URL has
            // actually failed before producing anything.
            //
            // fallbackStarted is the correct per-request guard.
            //

            if (Interlocked.Exchange(
                    ref fallbackStarted,
                    1) != 0)
            {
                return;
            }


            


            


            var failedRenderer = _mpvRenderer;
            var resumePosition = playbackPosition;
            if (IsYTURL(url) && failedRenderer is not null)
            {
                var position = failedRenderer.GetProperties()[0];
                if (double.IsFinite(position) && position >= 0 && position <= 604800)
                    resumePosition = (int)position;
            }
            failedRenderer?.SuspendYouTubeStartup();
            _webResolverFallbackTask =
                Task.Run(
                    async () =>
                    {
                        await TryWebResolverFallbackAsync(
                                url,
                                resumePosition,
                                isPlaying,
                                playbackGeneration,
                                failureMessage,
                                Volatile.Read(
                                    ref authenticatedYouTubeRetryStarted) != 0,
                                expectedAudioOnly,
                                youtubeRoute)
                            .ConfigureAwait(false);
                    },
                    _lifetimeCancellation.Token);
        }


        bool StartAuthenticatedYouTubeRetry(
            MpvRenderer renderer,
            string failureMessage)
        {
            if (!IsYTURL(url) ||
                !YouTubePlaybackPolicy.IsAccountRequiredError(failureMessage) ||
                string.IsNullOrWhiteSpace(CookiesPath) ||
                !YouTubeEmbeddedBrowserSession.LooksUsable(CookiesPath) ||
                !YouTubePoTokenSupport.IsAvailable(Resources) ||
                Interlocked.CompareExchange(
                    ref authenticatedYouTubeRetryStarted,
                    1,
                    0) != 0)
            {
                return false;
            }

            

            YouTubeRequestScheduler.Shared.RecordPlayback(url, YouTubeRequestIdentity.Account);

            renderer.SuspendYouTubeStartup();

            _webResolverFallbackRunning =
                true;

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        var providerReady =
                            await WaitForYouTubeProviderAsync(EnsureYouTubePlaybackProviderAsync, playbackGeneration, _lifetimeCancellation.Token).ConfigureAwait(false);

                        if (!providerReady ||
                            playbackGeneration != _playbackGeneration ||
                            _disposing)
                        {
                            if (!providerReady)
                            {
                                AepLog.Warning(
                                    "[YouTube/Account] The PO-token provider is unavailable; the browser account will not be sent through Android playback.");
                            }

                            StartWebResolverFallback(failureMessage);
                            return;
                        }

                        renderer.SetYouTubeCookiesPath(CookiesPath);
                        renderer.Play(
                            url,
                            playbackPosition,
                            isPlaying,
                            useYouTubePoTokens: true,
                            useYouTubeCookies: true,
                            preparedVisualizerMode:
                                _audioVisualizerMode != AudioVisualizerMode.ClassicBars
                                    ? _audioVisualizerMode
                                    : null,
                            preparedVisualizerTheme: _audioVisualizerTheme,
                            expectedAudioOnly: expectedAudioOnly);

                        
                    }
                    catch (Exception)
                    {
                        AepLog.Warning(
                            "[YouTube/Account] Could not start authenticated retry.");
                        StartWebResolverFallback(failureMessage);
                    }
                },
                _lifetimeCancellation.Token);

            return true;
        }

        AssignScreenForSession(
            _screenTexture, preserveScreenVisibility);


        _screenPainter?.SetLoading(
            true);

        // Make the TV and its loading screen visible before any delayed
        // YouTube work begins. PO-token provider startup can take several
        // seconds on its first use; keeping _isActive false until after that
        // wait made the application appear to have ignored the Play action.
        _isActive =
            true;

        if (preserveRelayScreenOnFailure && _screenPainter is { } painter)
            painter.SetLiveBroadcastConnectionWaiting(painter.LoadingConnectionVersion, true);

        _screenPainter?.SetAudioOnly(
            false);

        _screenPainter?.SetTransform(
            ScreenPosition,
            ScreenYaw,
            ScreenWidthScale,
            ScreenHeightScale);

        // This playback attempt owns this source for its entire native MPV
        // lifetime. A later StopVideo replaces the engine's current source,
        // so delayed cleanup from an earlier video cannot cancel or dispose
        // the next video's renderer.
        var renderCancellation =
            GetRenderCancellation();
        var renderToken = renderCancellation.Token;

        var playbackWorker = Task.Run(
                async () =>
                {
                    try
                    {
                        renderToken.ThrowIfCancellationRequested();
                        await Resources.PrepareMediaAsync(Uri.TryCreate(url,UriKind.Absolute,out var mediaUri) && mediaUri.Scheme is "http" or "https").WaitAsync(renderToken).ConfigureAwait(false);
                        renderToken.ThrowIfCancellationRequested();
                        if (chosenPage && !chosenMedia.Original)
                        {
                            using var extractionDeadline = CancellationTokenSource.CreateLinkedTokenSource(renderToken);
                            extractionDeadline.CancelAfter(TimeSpan.FromSeconds(60));
                            var discovery = recentChoice is not null
                                ? new WebMediaUrlResolver.Discovery([recentChoice], true)
                                : await WebMediaUrlResolver.DiscoverAsync(Resources, url, extractionDeadline.Token,
                                    includeHtmlAlternatives: true).ConfigureAwait(false);
                            var refreshed = ResolvedMediaLink.Match(chosenMedia, discovery.Candidates) ??
                                await VideoPageChoice.ChooseAsync(url, Task.FromResult(discovery), renderToken,
                                    () => playbackGeneration == _playbackGeneration && !_disposing, forceChoice: true).ConfigureAwait(false);
                            renderToken.ThrowIfCancellationRequested();
                            if (playbackGeneration != _playbackGeneration || _disposing) return;
                            if (refreshed.Url is null)
                            {
                                LastError = "Video selection cancelled.";
                                _rendererFailed = true;
                                _isActive = false;
                                _screenPainter?.SetLoading(false);
                                return;
                            }
                            url = refreshed.Url;
                            chosenHeaders = refreshed.Headers;
                        }
                        else if (!chosenPage && !expectedAudioOnly && WebMediaUrlResolver.NeedsPageChoice(url))
                        {
                            var choice = await VideoPageChoice.ResolveAsync(Resources, url, renderToken,
                                () => playbackGeneration == _playbackGeneration && !_disposing).ConfigureAwait(false);
                            renderToken.ThrowIfCancellationRequested();
                            if (playbackGeneration != _playbackGeneration || _disposing) return;
                            if (choice.Url is null)
                            {
                                LastError = "Video selection cancelled.";
                                _rendererFailed = true;
                                _isActive = false;
                                _screenPainter?.SetLoading(false);
                                return;
                            }
                            url = choice.Url;
                            chosenHeaders = choice.Headers;
                        }
                        renderToken.ThrowIfCancellationRequested();
                        if (IsYTURL(
                                url))
                        {
                            TimeSpan elapsed =
                                DateTime.Now -
                                _lastLoadYT;


                            if (elapsed.TotalSeconds <
                                7)
                            {
                                int sleepTime =
                                    Math.Min(
                                        Math.Max(
                                            (int)(
                                                7000 -
                                                elapsed.TotalMilliseconds),
                                            0),
                                        7000);


                                await Task.Delay(sleepTime, renderToken).ConfigureAwait(false);
                            }


                            _lastLoadYT =
                             DateTime.Now;
                        }

                        //
                        // A newer PlayVideo call may have replaced this request
                        // while this YouTube task was waiting for the load
                        // throttle. Never allow the delayed task to load its URL
                        // into the newer playback session.
                        //
                        if (_disposing || renderToken.IsCancellationRequested || playbackGeneration !=
           _playbackGeneration)
                        {
                            

                            return;
                        }

                        var useYouTubePoTokens =
                            youtubeRoute ==
                            YouTubePlaybackRoute.PoToken;

                        //
                        // Start the local provider only when compatibility mode
                        // actually needs it. If the HTTP provider cannot start,
                        // yt-dlp can still use the configured one-shot script
                        // provider.
                        //
                        if (useYouTubePoTokens)
                        {
                            var providerReady =
                                await WaitForYouTubeProviderAsync(EnsureYouTubePlaybackProviderAsync, playbackGeneration, renderToken).ConfigureAwait(false);

                            if (!providerReady)
                            {
                                {}
                            }

                            //
                            // Starting the provider takes time. Ensure this request
                            // was not replaced while we were waiting.
                            //
                            if (_disposing || renderToken.IsCancellationRequested || playbackGeneration !=
                                _playbackGeneration)
                            {
                                

                                return;
                            }
                        }

                        var browserStartupReady = false;
                        if (BrowserHlsStartup.AppliesTo(url))
                        {
                            using var probe = Net.PluginHttpClients.CreateProbeClient();
                            var started = System.Diagnostics.Stopwatch.GetTimestamp();
                            browserStartupReady = await BrowserHlsStartup.WaitAsync(probe, url, renderToken)
                                .ConfigureAwait(false);
                            if (_disposing || renderCancellation.IsCancellationRequested || playbackGeneration != _playbackGeneration) return;
                            
                        }
                        //
                        // =================================================
                        // Renderer callbacks for this playback generation
                        // =================================================
                        //

                        void ConfigureRendererCallbacks(
                            MpvRenderer renderer)
                        {
                            //
                            // =========================================================
                            // MPV DIAGNOSTIC LOGGING
                            // =========================================================
                            //
                            // MPV emits many warnings during otherwise healthy playback:existingRenderer.Play(
                            //
                            // - A/V desynchronisation
                            // - temporary buffering / slow decode
                            // - ytdl informational errors
                            // - driver / timing warnings
                            //
                            // These are NOT terminal playback failures.
                            //
                            // IMPORTANT:
                            //
                            // Do NOT set LastError here.
                            //
                            // LastError is consumed by VideoPlayer as a genuine playback
                            // failure and causes StopVideo() to be called.
                            //
                            // Actual terminal playback failures are handled separately by
                            // OnPlaybackFailed when MPV sends END_FILE with reason=ERROR.
                            //

                            renderer.OnError =
                                message =>
                                {
                                    if (playbackGeneration !=
                                        _playbackGeneration)
                                    {
                                        return;
                                    }


                                    if (string.IsNullOrWhiteSpace(
                                            message))
                                    {
                                        return;
                                    }

                                    if (IsYTURL(url) &&
    YouTubePlaybackPolicy.IsBotCheckError(
        message))
                                    {
                                        youtubeVerificationError =
                                            message;

                                        {}
                                    }

                                    if (IsYTURL(url) && YouTubeAgeRestrictionCache.IsAgeRestrictedError(message))
                                        youtubeAgeRestrictionError = message;

                                    if (IsYTURL(url) &&
                                        YouTubePlaybackPolicy.IsAccountRequiredError(
                                            message))
                                    {
                                        youtubeAuthenticationError =
                                            message;

                                        {}
                                    }


                                    {}


                                };


                            //
                            // Definitive MPV terminal failure.
                            //
                            // MpvRenderer fires this from END_FILE when
                            // reason == MPV_END_FILE_REASON_ERROR.
                            //

                            renderer.OnPlaybackFailed =
    message =>
    {
        if (playbackGeneration !=
            _playbackGeneration)
        {
            return;
        }


        //
        // =========================================================
        // FIRST FAILURE: KEEP THIS MPV RENDERER ALIVE
        // =========================================================
        //
        // The generic web resolver gets one second chance.
        //
        // IMPORTANT:
        //
        // Do NOT Stop(), Dispose(), ResetFailedRenderer(), or otherwise
        // destroy this MpvRenderer here.
        //
        // Its RenderFrame() call may simply remain blocked waiting for
        // another frame. That is intentional.
        //
        // If the resolver succeeds we will issue another loadfile to
        // THIS SAME mpv instance. The existing render loop will then
        // wake naturally when the resolved media produces frames.
        //
        // This avoids destroying/recreating the D3D/mpv render context
        // during the retry.
        //

        // END_FILE from the failed native load may arrive while fresh links
        // are being extracted. It is not a failure of the retry, which has not
        // been loaded yet. Play resets this guard before the replacement load.
        if (renderer.YouTubeRecoveryResolving) return;
        var effectiveFailure = youtubeAgeRestrictionError ?? youtubeAuthenticationError ?? youtubeVerificationError ?? message;
        if (StopForYouTubeAgeRestriction(url, effectiveFailure, playbackGeneration)) return;

        if (allowWebResolverFallback &&
            Volatile.Read(
                ref fallbackStarted) == 0)
        {
            _webResolverFallbackRunning =
                true;


            _rendererFailed =
                false;


            LastError =
                null;


            _isActive =
                true;


            IsAudioOnly =
                false;


            _screenPainter?.SetAudioOnly(
                false);


            _screenPainter?.SetLoading(
                true);


            




            if (IsYTURL(url))
                YouTubeRequestScheduler.Shared.ReportFailure(
                    Volatile.Read(ref authenticatedYouTubeRetryStarted) != 0
                        ? YouTubeRequestIdentity.Account
                        : YouTubeRequestIdentity.Guest,
                    effectiveFailure);

            if (StartAuthenticatedYouTubeRetry(
                    renderer,
                    effectiveFailure))
            {
                return;
            }

            //
            // A probe that failed for an ordinary reason must not be
            // treated as proof that YouTube verification is still active.
            //
            // A recognised bot check is handled by the fallback method,
            // which re-enters compatibility mode and extends the timer.
            //
            if (youtubeRoute ==
                    YouTubePlaybackRoute.AndroidProbe &&
                !YouTubePlaybackPolicy.IsBotCheckError(
                    effectiveFailure))
            {
                Resources
                    .YouTubePolicy
                    .ReportProbeInconclusive();
            }

            StartWebResolverFallback(
                effectiveFailure);


            return;
        }


        //
        // =========================================================
        // SECOND FAILURE: FINAL
        // =========================================================
        //
        // The resolver chance has already been consumed, so this is
        // now a genuine final playback failure.
        //

        _webResolverFallbackRunning =
            false;


        _rendererFailed =
            true;


        LastError =
            string.IsNullOrWhiteSpace(
                LastError)
                ? IsYTURL(url) ? "YouTube playback failed after an automatic retry. Please try again." : message
                : LastError;


        preserveRelayScreenOnFailure = !localScreenHidden && (preserveRelayScreenOnFailure || PreserveScreenOnPlaybackFailure || HalloweenPlayback);
        _isActive = preserveRelayScreenOnFailure;


        IsAudioOnly =
            false;


        _screenPainter?.SetAudioOnly(
            false);


        _screenPainter?.ResetLoadingConnection();
        _screenPainter?.SetLoading(preserveRelayScreenOnFailure);


        _screenPainter?.SetTarget(
            preserveRelayScreenOnFailure ? _screenTexture : null);


        AepLog.Warning(
            "[MPV] Resolved retry also failed; playback is now final.");


        //
        // Final failure only: wake the render loop so its normal
        // cleanup can run.
        //

        try
        {
            renderer.Stop();
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[MPV] Failed to stop renderer after final playback failure.");
        }
    };


                            //
                            // FILE_LOADED tells us enough to detect an
                            // audio-only stream, but video isn't considered
                            // successfully playing until a frame is rendered.
                            //

                            renderer.OnYouTubePlaybackHealthy = () =>
                            {
                                if (playbackGeneration != _playbackGeneration) return;
                                _webResolverFallbackRunning = false;
                                LastError = null;
                                ReportYouTubeRouteSucceeded();
                            };

                            renderer.OnMediaLoaded =
     () =>
     {
         if (playbackGeneration !=
             _playbackGeneration)
         {
             return;
         }

         youtubeAgeRestrictionError = null;
         youtubeAuthenticationError = null;
         youtubeVerificationError = null;
         Interlocked.CompareExchange(ref _pendingMediaLoadGeneration, -1, playbackGeneration);


         var currentRenderer =
             _mpvRenderer;


         if (currentRenderer is null)
         {
             return;
         }


         bool hasAudio =
             currentRenderer
                 .HasAudioTrack();


         bool hasVideo =
             currentRenderer
                 .HasVideoTrack();


         //
         // An FFmpeg visualizer deliberately generates a video track for an
         // otherwise audio-only stream. Treat that generated output as
         // audio-only media rather than mistaking it for ordinary video and
         // clearing the visualizer graph.
         //
         bool audioOnly =
    expectedAudioOnly ||
    hasAudio &&
    (!hasVideo ||
     currentRenderer.AudioSpectrumEnabled);


         IsAudioOnly =
      audioOnly;

         if (audioOnly)
         {
             if (currentRenderer.AudioSpectrumEnabled &&
                 _audioVisualizerMode != AudioVisualizerMode.ClassicBars)
             {
                 //
                 // The FFmpeg graph was installed before loadfile. Its
                 // generated frames should be displayed through the normal
                 // video texture rather than the Classic Bars shader.
                 //
                 _screenPainter?.SetAudioOnly(
                     false);
             }
             else
             {
                 //
                 // Classic Bars does not generate video frames, so enable
                 // ScreenPainter's lightweight audio-only shader. This also
                 // provides the fallback if mpv rejected a prepared graph.
                 //
                 SetAudioVisualizerMode(
                     _audioVisualizerMode,
                     _audioVisualizerTheme);
             }
         }
         else
         {
             currentRenderer.SetAudioSpectrumEnabled(
                 false);

             _screenPainter?.SetAudioOnly(
                 false);
         }


         if (audioOnly)
         {
             Interlocked.Exchange(
                 ref playbackStarted,
                 1);

             _screenPainter?
                 .SetLoading(
                     false);

             if (!IsYTURL(url)) ReportYouTubeRouteSucceeded();
         }


         //
         // =========================================================
         // RESOLVER RETRY SUCCESS
         // =========================================================
         //
         // TryWebResolverFallbackAsync() deliberately leaves
         // _webResolverFallbackRunning=true after sending the
         // resolved URL to the existing MPV renderer.
         //
         // Ordinary sources clear recovery at FILE_LOADED. YouTube waits
         // for OnYouTubePlaybackHealthy so moving video with dead audio
         // cannot be mistaken for successful recovery.
         //

         if (_webResolverFallbackRunning && !IsYTURL(url))
         {
             


             _webResolverFallbackRunning =
                 false;


             LastError =
                 null;
         }
     };

                            //
                            // First actual video frame = successful video
                            // playback.
                            //

                            renderer.OnFrameRendered =
                                () =>
                                {
                                    if (playbackGeneration !=
                                        _playbackGeneration)
                                    {
                                        return;
                                    }


                                    Interlocked.Exchange(
    ref playbackStarted,
    1);

                                    _screenPainter?
                                        .SetLoading(
                                            false);

                                    if (!IsYTURL(url)) ReportYouTubeRouteSucceeded();
                                };
                        }


                        //
                        // =================================================
                        // Existing renderer
                        // =================================================
                        //

                        MpvRenderer? ownedRenderer = null;
                        var rendererPublished = false;
                        using var startupLease = await rendererSlot.EnterAsync(renderToken).ConfigureAwait(false);
                        try
                        {
                            if (_disposing || renderToken.IsCancellationRequested ||
                                playbackGeneration != _playbackGeneration) return;
                            var existingRenderer =
                       _mpvRenderer;

                            if (existingRenderer?.IsClosed == true)
                            {
                                rendererSlot.Clear(existingRenderer);
                                existingRenderer.Dispose();
                                existingRenderer = null;
                            }

                            if (existingRenderer is not null)
                            {
                                if (playbackGeneration !=
                                    _playbackGeneration)
                                {
                                

                                    return;
                                }

                                ConfigureRendererCallbacks(
                                    existingRenderer);

                                existingRenderer.SetMuted(
                                    _pendingOutputMuted);


                                if (useConnectedAgeSession) existingRenderer.SetYouTubeCookiesPath(CookiesPath);
                                existingRenderer.Play(
           url,
           playbackPosition,
           isPlaying,
           useYouTubePoTokens,
           useYouTubeCookies: useConnectedAgeSession,
           preparedVisualizerMode:
               _audioVisualizerMode !=
                   AudioVisualizerMode.ClassicBars
                   ? _audioVisualizerMode
                   : null,
           preparedVisualizerTheme:
               _audioVisualizerTheme,
           expectedAudioOnly:
               expectedAudioOnly,
           bufferBrowserInitially: browserStartupReady,
           resolvedHttpHeaders: chosenHeaders);

                                _isActive =
                                    true;

                                _screenPainter?.SetTransform(
                                    ScreenPosition,
                                    ScreenYaw,
                                    ScreenWidthScale,
                                    ScreenHeightScale);

                                return;
                            }


                            //
                            // =================================================
                            // New renderer
                            // =================================================
                            //

                        


                            ownedRenderer = new MpvRenderer();

                            //
                            // Check again after construction. Another PlayVideo
                            // request may have become current while this task was
                            // being scheduled.
                            //
                            if (playbackGeneration !=
                                _playbackGeneration)
                            {
                                return;
                            }

                            ownedRenderer.SetFrameConsumersVisible(frameConsumersVisible);

                        

                            ConfigureRendererCallbacks(
                                ownedRenderer);

                        

                        

                            ownedRenderer.Initialize(
                                ScreenWidth,
                                ScreenHeight,
                                _screenTexture,
                                renderToken,
                                HardwareDecoding,
                                MaxQualityHeight,
                                AllowInsecureDirectUrls,
                                _pendingVolume,
                                CookiesPath,
                                _visualizerTexture);

                            // Volume/unmute may have changed while Initialize was
                            // running; apply the latest value rather than its snapshot.
                            ownedRenderer.SetVolume(_pendingVolume);
                            ownedRenderer.SetMuted(
                                _pendingOutputMuted);

                            rendererPublished = rendererSlot.TryPublish(ownedRenderer, () =>
                                !_disposing && !renderToken.IsCancellationRequested && playbackGeneration == _playbackGeneration);
                            if (!rendererPublished) return;

                            ownedRenderer.SetFrameConsumersVisible(frameConsumersVisible);
                            ownedRenderer.Play(
         url,
         playbackPosition,
         isPlaying,
         useYouTubePoTokens,
         useYouTubeCookies: useConnectedAgeSession,
         preparedVisualizerMode:
             _audioVisualizerMode !=
                 AudioVisualizerMode.ClassicBars
                 ? _audioVisualizerMode
                 : null,
         preparedVisualizerTheme:
             _audioVisualizerTheme,
         expectedAudioOnly:
             expectedAudioOnly,
         bufferBrowserInitially: browserStartupReady,
           resolvedHttpHeaders: chosenHeaders);

                        

                            _isActive =
                                true;

                            _screenPainter?.SetTransform(
                                ScreenPosition,
                                ScreenYaw,
                                ScreenWidthScale,
                                ScreenHeightScale);

                        

                        }
                        finally
                        {
                            if (!rendererPublished) ownedRenderer?.Dispose();
                            startupLease.Dispose();
                        }

                        //
                        // This task renders only the renderer it created.
                        //
                        // The same renderer is deliberately reused when the
                        // user changes from one video to another. A new URL
                        // changes playbackGeneration and replaces the renderer
                        // callbacks, but this existing render loop must remain
                        // alive to display frames from the new video.
                        //
                        while (!_stopRequested &&
                               ownedRenderer.RenderFrame())
                        {
                        }

                        

                        if (playbackGeneration !=
       _playbackGeneration)
                        {
                            

                            return;
                        }

                        preserveRelayScreenOnFailure = !localScreenHidden && (preserveRelayScreenOnFailure || PreserveScreenOnPlaybackFailure || HalloweenPlayback);
        _isActive = preserveRelayScreenOnFailure;

                        //
                        // A naturally completed local file must release exclusive ownership.
                        //
                        // Previously this flag was only cleared by StopVideo(). That left the
                        // completed local file blocking every later playback attempt until the
                        // plugin was restarted.
                        //

                        if (isLocalVideo)
                        {
                            _isPlayingLocalVideo =
                                false;

                            StopLocalVideoBroadcast();

                            
                        }

                        _screenPainter?.ResetLoadingConnection();
                        _screenPainter?.SetLoading(preserveRelayScreenOnFailure);

                        _screenPainter?.SetTarget(
                            preserveRelayScreenOnFailure ? _screenTexture : null);

                        rendererSlot.Clear(ownedRenderer);

                        try
                        {
                            ownedRenderer.Dispose();
                        }
                        catch (Exception)
                        {
                            AepLog.Warning(
                                "[MPV] Failed to dispose renderer after video end.");
                        }

                        CompleteRenderCancellation(
                            renderCancellation);
                    }
                    catch (OperationCanceledException) when (renderToken.IsCancellationRequested || _disposing)
                    {
                        // Stop also interrupts startup throttling, provider waits and HLS checks.
                    }
                    catch (Exception exception)
                    {
                        //
                        // Ignore exceptions from a playback generation that
                        // has already been replaced.
                        //

                        if (playbackGeneration !=
                            _playbackGeneration)
                        {
                            

                            return;
                        }


                        AepLog.Error(
                            "[MPV] Generic error: [redacted] [redacted]");


                        LastError =
                            exception.Message;


                        _rendererFailed =
                            true;


                        _isActive =
                            false;


                        IsAudioOnly =
                            false;


                        _screenPainter?
                            .SetAudioOnly(
                                false);


                        _screenPainter?
                            .SetLoading(
                                false);


                        _screenPainter?
                            .SetTarget(
                                null);


                        //
                        // Initialization-level exceptions happen outside the
                        // MPV END_FILE event system, so they also get one
                        // resolver attempt.
                        //

                        StartWebResolverFallback(
                            exception.Message);
                    }
                });
        var worker = playbackWorker;
        _renderTask = worker;
        playbackWorkers.TryAdd(worker, 0);
        _ = worker.ContinueWith(completed => playbackWorkers.TryRemove(completed, out _),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }


    private async Task TryWebResolverFallbackAsync(
    string originalUrl,
    int playbackPosition,
    bool isPlaying,
    int failedPlaybackGeneration,
    string originalFailure,
    bool useAuthenticatedYouTubeSession,
    bool expectedAudioOnly,
    YouTubePlaybackRoute failedYouTubeRoute)
    {
        if (_disposing ||
            failedPlaybackGeneration !=
            _playbackGeneration)
        {
            return;
        }


        _webResolverFallbackRunning =
            true;


        //
        // The original MPV failure is not final while the resolver is
        // working.
        //

        LastError =
            null;


        try
        {
            if (expectedAudioOnly && Uri.TryCreate(originalUrl, UriKind.Absolute, out var radioUri) &&
                radioUri.Scheme is "http" or "https")
            {
                // This is already the audio stream, not a web page to resolve.
                // Recreate its relay once, at the live edge, using the existing
                // per-playback fallback guard rather than an unlimited retry loop.
                await Task.Delay(300, _lifetimeCancellation.Token).ConfigureAwait(false);
                using var radioRetryLease = await rendererSlot.EnterAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
                if (_disposing || failedPlaybackGeneration != _playbackGeneration) return;
                var radioRenderer = _mpvRenderer;
                if (radioRenderer is null) throw new InvalidOperationException("Radio renderer is no longer available.");
                
                radioRenderer.Play(originalUrl, 0, isPlaying,
                    preparedVisualizerMode: _audioVisualizerMode != AudioVisualizerMode.ClassicBars ? _audioVisualizerMode : null,
                    preparedVisualizerTheme: _audioVisualizerTheme, expectedAudioOnly: true);
                return; // FILE_LOADED confirms recovery and clears the pending flag.
            }
            

            var usePoTokens =
                false;

            if (IsYTURL(
                    originalUrl) && !Resources.PoTokens.AndroidFallbackRequired)
            {
                if (useAuthenticatedYouTubeSession &&
                    !string.IsNullOrWhiteSpace(CookiesPath) &&
                    YouTubeEmbeddedBrowserSession.LooksUsable(CookiesPath) &&
                    YouTubePoTokenSupport.IsAvailable(Resources))
                {
                    await WaitForYouTubeProviderAsync(EnsureYouTubePlaybackProviderAsync, failedPlaybackGeneration, _lifetimeCancellation.Token).ConfigureAwait(false);

                    usePoTokens =
                        true;
                }
                else
                if (YouTubePlaybackPolicy.IsBotCheckError(
                        originalFailure))
                {
                    //
                    // Android encountered a recognisable verification
                    // restriction. Enable compatibility mode and wait
                    // for the local provider before retrying.
                    //
                    await WaitForYouTubeProviderAsync(async () => { await Resources.YouTubePolicy.EnterCompatibilityModeAsync().ConfigureAwait(false); return await EnsureYouTubePlaybackProviderAsync().ConfigureAwait(false); }, failedPlaybackGeneration, _lifetimeCancellation.Token).ConfigureAwait(false);

                    usePoTokens =
                        true;
                }
                else if (Resources.YouTubePolicy.CompatibilityModeActive ||
                    Resources.YouTubePolicy.ShouldUsePoTokenFallback(failedYouTubeRoute, originalFailure))
                {
                    //
                    // Keep PO-token requests on their chosen route, or use
                    // the installed alternate route for the single fallback
                    // after an otherwise unrecognised Android failure.
                    //
                    await WaitForYouTubeProviderAsync(EnsureYouTubePlaybackProviderAsync, failedPlaybackGeneration, _lifetimeCancellation.Token).ConfigureAwait(false);

                    usePoTokens =
                        true;
                    AepLog.Warning("[YouTube] Retrying failed playback through the PO-token resolver.");
                }
            }

            // Invalidate only for this bounded recovery, never ordinary playback.
            // No provider restart: existing media keeps its already-issued URLs.
            var freshYouTubeTokens = usePoTokens && IsYTURL(originalUrl);
            if (freshYouTubeTokens)
            {
                if (_disposing || failedPlaybackGeneration != _playbackGeneration) return;
                await Resources.RefreshYouTubeTokensAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
                if (_disposing || failedPlaybackGeneration != _playbackGeneration) return;
            }

            // Provider startup above has its own lifetime. Only start the
            // extraction deadline once that wait has finished.
            using var resolverDeadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            if (IsYTURL(originalUrl)) resolverDeadline.CancelAfter(TimeSpan.FromSeconds(30));
            var result =
                await WebMediaUrlResolver
                    .ResolveAsync(
                        Resources,
                        originalUrl,
                        resolverDeadline.Token,
                        usePoTokens,
                        useAuthenticatedYouTubeSession
                            ? CookiesPath
                            : null,
                        () => failedPlaybackGeneration == _playbackGeneration && !_disposing,
                        freshYouTubeTokens: freshYouTubeTokens)
                    .ConfigureAwait(false);


            using var retryLease = await rendererSlot.EnterAsync(_lifetimeCancellation.Token).ConfigureAwait(false);

            if (_disposing ||
                failedPlaybackGeneration !=
                _playbackGeneration)
            {
                

                return;
            }


            if (IsYTURL(originalUrl))
                AepLog.PlaybackAttempt(new(PlaybackAttemptStage.ResolverResult,
                    usePoTokens ? PlaybackAttemptRoute.PoToken : PlaybackAttemptRoute.Android,
                    failedPlaybackGeneration, _mpvRenderer?.DiagnosticRendererId ?? 0, _mpvRenderer?.DiagnosticLoad ?? 0,
                    useAuthenticatedYouTubeSession, Resources.IsYouTubeProviderRunning, true,
                    !string.IsNullOrWhiteSpace(result.Url), PlaybackAccessDiagnostic.HttpStatus(result.Error),
                    PlaybackAccessDiagnostic.Reason(result.Error)));

            var resolvedUrl =
                result.Url;


            if (string.IsNullOrWhiteSpace(
                    resolvedUrl))
            {
                var resolverError =
                    string.IsNullOrWhiteSpace(
                        result.Error)
                        ? "No playable video URL was found."
                        : result.Error;


                _webResolverFallbackRunning =
                    false;


                if (StopForYouTubeAgeRestriction(originalUrl, resolverError, failedPlaybackGeneration)) return;
                LastError =
                    IsYTURL(originalUrl) ? "YouTube playback could not be recovered. Please try again." : resolverError;


                _rendererFailed =
                    true;


                AepLog.Warning(
                    "[WebResolver] Automatic fallback failed.");


                try
                {
                    _mpvRenderer?.Stop();
                }
                catch (Exception)
                {
                    AepLog.Warning(
                        "[WebResolver] Failed to stop renderer after resolver failure.");
                }


                return;
            }


            if (string.Equals(
                    resolvedUrl,
                    originalUrl,
                    StringComparison.OrdinalIgnoreCase))
            {
                _webResolverFallbackRunning =
                    false;


                LastError =
                    originalFailure;


                _rendererFailed =
                    true;


                AepLog.Warning(
                    "[WebResolver] Resolver returned the same URL that already failed. No retry performed.");


                try
                {
                    _mpvRenderer?.Stop();
                }
                catch (Exception)
                {
                    AepLog.Warning(
                        "[WebResolver] Failed to stop renderer after unusable resolver result.");
                }


                return;
            }


            var renderer =
                _mpvRenderer;


            if (renderer is null)
            {
                _webResolverFallbackRunning =
                    false;


                _rendererFailed =
                    true;


                LastError =
                    "The MPV renderer disappeared while the fallback URL was being resolved.";


                AepLog.Warning(
                    "[WebResolver] [redacted]");


                return;
            }


            


            //
            // =========================================================
            // RETRY ON THE EXISTING MPV INSTANCE
            // =========================================================
            //
            // Do NOT:
            //
            //   await _renderTask
            //   ResetFailedRenderer()
            //   Dispose()
            //   PlayVideo(...)
            //
            // The renderer and its D3D render context stay alive.
            //
            // MpvRenderer.Play() performs a loadfile/replace on the same
            // mpv instance. Its existing RenderFrame loop is still waiting
            // and will naturally wake when this media produces a frame.
            //

            _rendererFailed =
                false;


            LastError =
                null;


            _isActive =
                true;


            IsAudioOnly =
                false;


            _screenPainter?.SetAudioOnly(
                false);


            _screenPainter?.SetLoading(
                true);


            _screenPainter?.SetTransform(
                ScreenPosition,
                ScreenYaw,
                ScreenWidthScale,
                ScreenHeightScale);


            


            if (IsYTURL(originalUrl))
                AepLog.PlaybackAttempt(new(PlaybackAttemptStage.LoadRequested, PlaybackAttemptRoute.ResolvedUrl,
                    failedPlaybackGeneration, renderer.DiagnosticRendererId, renderer.DiagnosticLoad + 1,
                    useAuthenticatedYouTubeSession, Resources.IsYouTubeProviderRunning, true, false));
            renderer.Play(
       resolvedUrl,
       playbackPosition,
       isPlaying,
       preparedVisualizerMode:
           _audioVisualizerMode !=
               AudioVisualizerMode.ClassicBars
               ? _audioVisualizerMode
               : null,
       preparedVisualizerTheme:
           _audioVisualizerTheme,
       resolvedHttpHeaders: result.Headers,
       youtubePlayback: IsYTURL(originalUrl));


            


            //
            // IMPORTANT:
            //
            // Do NOT set _webResolverFallbackRunning=false here.
            //
            // Play() only queues MPV's loadfile command. It does not mean
            // FILE_LOADED has happened yet.
            //
            // YouTube clears the flag only after its playback watchdog confirms
            // working output (including audio when selected). Other sources use FILE_LOADED.
            //
        }

        catch (OperationCanceledException)
            when (_lifetimeCancellation.IsCancellationRequested)
        {
            _webResolverFallbackRunning =
                false;
        }
        catch (Exception exception)
        {
            if (_disposing || _lifetimeCancellation.IsCancellationRequested) return;
            using var failureLease = await rendererSlot.EnterAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
            if (failedPlaybackGeneration !=
                _playbackGeneration)
            {
                return;
            }


            _webResolverFallbackRunning =
                false;


            _rendererFailed =
                true;


            if (IsYTURL(originalUrl))
                AepLog.PlaybackAttempt(new(PlaybackAttemptStage.ResolverException, PlaybackAttemptRoute.ResolvedUrl,
                    failedPlaybackGeneration, _mpvRenderer?.DiagnosticRendererId ?? 0, _mpvRenderer?.DiagnosticLoad ?? 0,
                    useAuthenticatedYouTubeSession, Resources.IsYouTubeProviderRunning, true, false,
                    PlaybackAccessDiagnostic.HttpStatus(exception.Message), PlaybackAccessDiagnostic.Reason(exception.Message)));
            LastError =
                exception.Message;


            AepLog.Warning(
                "[WebResolver] Automatic fallback failed.");


            try
            {
                _mpvRenderer?.Stop();
            }
            catch (Exception)
            {
                AepLog.Warning(
                    "[WebResolver] Failed to stop renderer after resolver exception.");
            }
        }
    }

    internal void Pause(bool pause)
    {
        //
        // Still images have no running playback clock to pause.
        //
        if (_isPlayingImage)
        {
            if (pause ==
                _imagePlaybackPaused)
            {
                return;
            }

            if (pause)
            {
                _imageElapsedBeforeClockStart =
                    GetImageElapsedSeconds();

                _imagePlaybackPaused =
                    true;
            }
            else
            {
                _imageClockStartedUtc =
                    DateTime.UtcNow;

                _imagePlaybackPaused =
                    false;
            }

            return;
        }

        if (!_renderCancellation.Token.IsCancellationRequested)
        {
            _mpvRenderer?.Pause(
                pause);
        }
    }

    internal bool GetIdle()
    {
        if (MediaLoadPending) return true;
        //
        // A still image remains active until explicitly replaced or stopped.
        //
        if (_isPlayingImage)
        {
            if (_currentImageSelection is
                {
                    Mode: ImageMediaMode.Slideshow,
                    Loop: false
                } slideshow)
            {
                var duration =
                    slideshow.SecondsPerImage *
                    slideshow.ImageUrls.Count;

                return GetImageElapsedSeconds() >=
                       duration;
            }

            return false;
        }

        if (!_renderCancellation.Token.IsCancellationRequested)
        {
            return _mpvRenderer?.IsEofReached() ??
                   true;
        }

        return true;
    }

    internal bool GetPaused()
    {
        if (_isPlayingImage)
        {
            return _imagePlaybackPaused;
        }

        if (!_renderCancellation.Token.IsCancellationRequested)
        {
            return _mpvRenderer?.GetPaused() ??
                   false;
        }

        return false;
    }

    internal double[] GetInfo()
    {
        // A reused decoder still exposes the previous file's duration/EOF
        // during YouTube's delayed startup. Do not let the queue seek, unpause
        // or auto-advance that old file while the new load is pending.
        if (MediaLoadPending) return [0, 0, 0, 0, 0];
        if (_isPlayingImage)
        {
            var elapsed =
                GetImageElapsedSeconds();

            var duration =
                _currentImageSelection is
                {
                    Mode: ImageMediaMode.Slideshow,
                    Loop: false
                } slideshow
                    ? slideshow.SecondsPerImage *
                      slideshow.ImageUrls.Count
                    : 0d;

            return
            [
                elapsed,
                duration,
                _pendingVolume,
                ScreenWidth,
                ScreenHeight
            ];
        }

        if (!_renderCancellation.Token.IsCancellationRequested)
        {
            return _mpvRenderer?.GetProperties() ??
                   [0, 0, 0, 0, 0];
        }

        return [0, 0, 0, 0, 0];
    }

    internal void Seek(int seconds)
    {
        if (_isPlayingImage)
        {
            //
            // Still images have no timeline.
            //
            if (_currentImageSelection is not
                {
                    Mode: ImageMediaMode.Slideshow
                } slideshow)
            {
                return;
            }

            var requestedSeconds =
                Math.Max(
                    0d,
                    seconds);

            //
            // Do not seek a non-looping slideshow beyond its natural end.
            // Looping slideshows may use an unrestricted elapsed time because
            // RunSlideshowAsync converts it to a slide using modulo arithmetic.
            //
            if (!slideshow.Loop)
            {
                var duration =
                    slideshow.SecondsPerImage *
                    slideshow.ImageUrls.Count;

                requestedSeconds =
                    Math.Min(
                        requestedSeconds,
                        duration);
            }

            _imageElapsedBeforeClockStart =
                requestedSeconds;

            _imageClockStartedUtc =
                DateTime.UtcNow;

            return;
        }

        if (!_renderCancellation.Token.IsCancellationRequested)
        {
            _mpvRenderer?.Seek(
                seconds);
        }
    }

    internal void SetAudioVisualizerMode(
        AudioVisualizerMode mode,
        AudioVisualizerTheme theme)
    {
        _audioVisualizerMode =
            mode;

        _audioVisualizerTheme =
    theme;
        _screenPainter?.SetClassicBarsTheme(theme);

        //
        // Remember the selection even before MPV confirms that the media
        // is audio-only. It will be applied after FILE_LOADED.
        //
        if (!IsAudioOnly ||
            _mpvRenderer is not { } renderer)
        {
            return;
        }

        if (mode == AudioVisualizerMode.ClassicBars)
        {
            renderer.SetAudioSpectrumEnabled(
                false);

            _screenPainter?.SetAudioOnly(
                true);



            return;
        }

        var ffmpegEnabled =
renderer.SetAudioSpectrumEnabled(
    true,
    mode,
    theme);

        //
        // If FFmpeg rejects the graph, leave the original visualizer active
        // as a safe fallback.
        //
        _screenPainter?.SetAudioOnly(
            !ffmpegEnabled);

        
    }

    private float behaviourVolumeScale = 1f;
    private int behaviourBaseVolume = 60;
    internal void SetBehaviourVolumeScale(float scale)
    {
        if (behaviourVolumeScale == scale) return;
        behaviourVolumeScale = scale;
        ApplyVolume(behaviourBaseVolume, preserveOutputMute: true);
    }

    internal void SetVolume(int vol)
        => ApplyVolume(vol, preserveOutputMute: false);

    private void ApplyVolume(int vol, bool preserveOutputMute)
    {
        behaviourBaseVolume = vol;
        // The show owns this flag through countdown, playback and cleanup on every client.
        // Multiply the output only, preserving normal mute/distance handling and the 200% cap.
        vol = (int)(vol * behaviourVolumeScale * (HalloweenPlayback ? 1.2f : 1f));
        vol = Math.Clamp(
            vol,
            0,
            200);

        // DJ host monitoring uses mpv's output mute so its decoded audio still
        // drives the visualizer. Calls which represent the already-muted UI
        // must not replace that signal with zero-volume samples.
        if (_pendingOutputMuted && vol == 0)
        {
            return;
        }

        // A user unmute must reach MPV even if its mute property and the
        // pending flag became out of sync during renderer startup/reuse.
        if (vol > 0 && !preserveOutputMute)
        {
            _pendingOutputMuted = false;
            _mpvRenderer?.SetMuted(false);
        }

        _pendingVolume =
            vol;

        if (_isPlayingSnes)
        {
            _snesRenderer?.SetVolume(
                vol);

            return;
        }

        if (_isPlayingGameBoy)
        {
            _gameBoyRenderer?.SetVolume(
                vol);

            return;
        }

        if (_isPlayingNes)
        {
            _nesRenderer?.SetVolume(vol);
            return;
        }

        if (_isPlayingGameBoyAdvance)
        {
            _gameBoyAdvanceRenderer?.SetVolume(vol);
            return;
        }

        if (_isPlayingMasterSystem)
        {
            _masterSystemRenderer?.SetVolume(vol);
            return;
        }

        if (_isPlayingGameGear)
        {
            _gameGearRenderer?.SetVolume(vol);
            return;
        }

        if (_isPlayingBrowser)
        {
            _browserRenderer?.SetVolume(vol);
            return;
        }

        if (!_renderCancellation.Token
                        .IsCancellationRequested)
        {
            _mpvRenderer?.SetVolume(
                vol);
        }
    }

    internal void SetMusicSceneEffects(MusicSceneEffects effects, bool active = true)
    {
        musicSceneEffects = effects;
        musicSceneActive = active;
        _screenPainter?.SetMusicSceneEffects(effects, active);
    }
    internal TvFrameSelection FrameSelection
    {
        get => frameSelection;
        set
        {
            frameSelection = value.Normalized();
            if (_screenPainter is { } painter) painter.FrameSelection = frameSelection;
        }
    }

    internal void SetAudioScreenText(AudioScreenTextSelection selection)
    {
        audioScreenText = selection;
        _screenPainter?.SetAudioScreenText(selection);
    }

    internal void SetOutputMuted(bool muted)
    {
        _pendingOutputMuted = muted;
        _mpvRenderer?.SetMuted(muted);
    }

    internal byte[]? TryGetFrame(out int width, out int height)
    {
        var renderer = _mpvRenderer;
        if (renderer is null)
        {
            width = ScreenWidth;
            height = ScreenHeight;
            return null;
        }

        return renderer.TryGetFrame(out width, out height);
    }

    internal string GetMediaTitle()
    {
        if (!_renderCancellation.Token.IsCancellationRequested)
        {
            return _mpvRenderer?.GetMediaTitle() ?? string.Empty;
        }

        return string.Empty;
    }

    internal string? GetCurrentUrl() => _mpvRenderer?.GetCurrentUrl();

    internal IReadOnlyList<AudioTrack> GetAudioTracks() => MediaLoadPending ? [] : _mpvRenderer?.GetAudioTracks() ?? [];
    internal int? GetSelectedAudioTrackId() => _mpvRenderer?.GetSelectedAudioTrackId();
    internal bool SelectAudioTrack(int id) => _mpvRenderer?.SelectAudioTrack(id) ?? false;

    internal IReadOnlyList<SubtitleTrack> GetSubtitleTracks() =>
        _mpvRenderer?.GetSubtitleTracks() ?? [];

    internal IReadOnlyList<SubtitleTrack> GetEmbeddedSubtitleTracks() =>
        _mpvRenderer?.GetSubtitleTracks().Where(track => !track.IsExternal).ToArray() ?? [];

    internal bool SelectSubtitleTrack(int id) => _mpvRenderer?.SelectSubtitleTrack(id) ?? false;

    internal bool SelectSubtitle(string? language, string? name)
    {
        if (language is not null && _mpvRenderer is { } renderer &&
            InternetArchiveSubtitles.IsArchiveVideoUrl(renderer.GetCurrentUrl()))
        {
            var tracks = InternetArchiveSubtitles.GetTracks(renderer.GetCurrentUrl()!);
            var track = tracks?.FirstOrDefault(candidate =>
                string.Equals(candidate.Language, language, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            return track is not null && renderer.SelectArchiveSubtitle(track);
        }
        return _mpvRenderer?.SelectSubtitle(language, name) ?? false;
    }

    internal string? GetSelectedSubtitleId() =>
        _mpvRenderer?.GetSelectedSubtitleId();

    internal Task StopYouTubeProviderAsync() => Resources.StopYouTubeProviderAsync();
    internal bool IsYouTubeProviderRunning => Resources.IsYouTubeProviderRunning;

    internal bool ValidateURL(string inputUrl, out Uri? url)
    {
        string formattedUrl = inputUrl;

        if (!formattedUrl.StartsWith("http://", StringComparison.Ordinal) && !formattedUrl.StartsWith("https://", StringComparison.Ordinal))
        {
            formattedUrl = "https://" + formattedUrl;
        }

        return Uri.TryCreate(formattedUrl, UriKind.Absolute, out url)
            && (url?.Scheme == Uri.UriSchemeHttp || url?.Scheme == Uri.UriSchemeHttps)
            && url.Host.Contains('.') && !url.Host.EndsWith('.')
            && Uri.CheckHostName(url.Host) == UriHostNameType.Dns;
    }

    private void RecoverNativePlaybackFailure(string source, string message, Action releaseAndDispose)
    {
        if (_disposing) return;

        // Native failure text may contain a ROM path or browser address.
        const string safeMessage = "Playback stopped unexpectedly.";

        AepLog.Error("[NATIVE-RECOVERY] Playback stopped unexpectedly.");

        try
        {
            releaseAndDispose();
        }
        catch (Exception)
        {
            AepLog.Warning("[NATIVE-RECOVERY] Playback cleanup was incomplete.");
        }

        _playbackGeneration++;
        _isActive = false;
        IsAudioOnly = false;
        _lastIdle = true;
        _screenPainter?.SetAudioOnly(false);
        _screenPainter?.SetAudioLevel(0f);
        _screenPainter?.SetLoading(false);
        _screenPainter?.SetTarget(null);
        LastError = safeMessage;

        Plugin.ChatGui.Print($"[AlphaChannel] {safeMessage} You can start something else without reloading the plugin.");
    }

    private void CheckBroadcastFailures()
    {
        BroadcastDiagnosticsSnapshot? failed = null;
        Action? stop = null;

        void Consider(BroadcastDiagnosticsSnapshot snapshot, Action stopAction)
        {
            if (failed is null && snapshot.Health == BroadcastHealth.Failed &&
                snapshot.UpdatedUtc > _lastHandledBroadcastFailureUtc)
            {
                failed = snapshot;
                stop = stopAction;
            }
        }

        if (_isPlayingBrowser && _browserRenderer is not null)
            Consider(_browserRenderer.BroadcastDiagnostics, _browserRenderer.StopBroadcast);
        if (_isPlayingSnes && _snesRenderer is not null)
            Consider(_snesRenderer.BroadcastDiagnostics, _snesRenderer.StopBroadcast);
        if (_isPlayingGameBoy && _gameBoyRenderer is not null)
            Consider(_gameBoyRenderer.BroadcastDiagnostics, _gameBoyRenderer.StopBroadcast);
        if (_isPlayingNes && _nesRenderer is not null)
            Consider(_nesRenderer.BroadcastDiagnostics, _nesRenderer.StopBroadcast);
        if (_isPlayingGameBoyAdvance && _gameBoyAdvanceRenderer is not null)
            Consider(_gameBoyAdvanceRenderer.BroadcastDiagnostics, _gameBoyAdvanceRenderer.StopBroadcast);
        if (_isPlayingMasterSystem && _masterSystemRenderer is not null)
            Consider(_masterSystemRenderer.BroadcastDiagnostics, _masterSystemRenderer.StopBroadcast);
        if (_isPlayingGameGear && _gameGearRenderer is not null)
            Consider(_gameGearRenderer.BroadcastDiagnostics, _gameGearRenderer.StopBroadcast);
        if (_isPlayingLocalVideo)
            Consider(_localVideoBroadcastEncoder.Diagnostics, _localVideoBroadcastEncoder.Stop);

        if (failed is null) return;

        _lastHandledBroadcastFailureUtc = failed.UpdatedUtc;
        _lastBroadcastDiagnostics = failed;
        try { stop?.Invoke(); }
        catch (Exception)
        {
            AepLog.Warning("[BROADCAST-RECOVERY] Failed cleanup.");
        }

        AepLog.Warning("[BROADCAST-RECOVERY] [redacted] upload stopped; local playback remains active.");
        Plugin.ChatGui.Print($"[AlphaChannel] The {failed.Source} broadcast encoder stopped unexpectedly. Local playback is still running.");
    }

    private bool frameConsumersVisible = true;
    internal void SetFrameConsumersVisible(bool visible)
    { frameConsumersVisible = visible && !AudioOnlyMode; _mpvRenderer?.SetFrameConsumersVisible(frameConsumersVisible); }

    internal void OnFrameworkUpdate()
    {
        if (_disposing)
            return;

        if (_screenPainter is not { } painter) return;
        UpdateControllerMode();
        painter.CaptureUiRectangles();

        if (_rendererFailed && !_webResolverFallbackRunning && _renderTask?.IsCompleted != false)
        {
            // Dispose a renderer that failed during initialization or outside MPV's
            // normal END_FILE cleanup. LastError remains available to VideoPlayer/UI.
            ResetFailedRenderer();
        }

        var musicPlaybackActive = !AudioOnlyMode && IsAudioOnly && _isActive && !MediaLoadPending && _mpvRenderer is not null && !GetPaused();
        _screenPainter?.SetAudioEffectsPlaybackActive(painter.MusicEffects.AnyEnabled && musicPlaybackActive);
        UpdateMusicSpotlight(musicPlaybackActive);

        CheckBroadcastFailures();

        if (_isPlayingSnes)
        {
            var renderer = _snesRenderer;
            if (renderer?.HasFailed == true)
            {
                RecoverNativePlaybackFailure("Super Nintendo emulator", renderer.FailureMessage ?? string.Empty, () =>
                {
                    SetSnesControlsEnabled(false);
                    _isPlayingSnes = false;
                    _snesRenderer = null;
                    renderer.Dispose();
                });
                return;
            }

            try
            {
                UpdateSnesInput();
                renderer?.OnFrameworkUpdate();
            }
            catch (Exception)
            {
                RecoverNativePlaybackFailure("Super Nintendo emulator",
                    "The Super Nintendo emulator stopped unexpectedly.", () =>
                    {
                        SetSnesControlsEnabled(false);
                        _isPlayingSnes = false;
                        _snesRenderer = null;
                        renderer?.Dispose();
                    });
                AepLog.Error("[SNES9X] Framework update failed.");
                return;
            }

            _lastIdle = false;

            return;
        }

        if (_isPlayingGameBoy)
        {
            var renderer = _gameBoyRenderer;
            if (renderer?.HasFailed == true)
            {
                RecoverFailedGambatte(renderer, "Game Boy emulator", renderer.FailureMessage);
                return;
            }

            try { UpdateGameBoyInput(); renderer?.OnFrameworkUpdate(); }
            catch (Exception)
            {
                AepLog.Error("[GAMBATTE] Framework update failed.");
                RecoverFailedGambatte(renderer, "Game Boy emulator", null);
                return;
            }

            _lastIdle = false;

            return;
        }


        if (_isPlayingNes)
        {
            var renderer = _nesRenderer;
            if (renderer?.HasFailed == true)
            {
                RecoverFailedGambatte(renderer, "NES emulator", renderer.FailureMessage);
                return;
            }
            try { UpdateGameBoyInput(); renderer?.OnFrameworkUpdate(); }
            catch (Exception)
            {
                AepLog.Error("[NESTOPIA] Framework update failed.");
                RecoverFailedGambatte(renderer, "NES emulator", null);
                return;
            }
            _lastIdle = false;
            return;
        }

        if (_isPlayingGameBoyAdvance)
        {
            var renderer = _gameBoyAdvanceRenderer;
            if (renderer?.HasFailed == true)
            {
                RecoverFailedGambatte(renderer, "Game Boy Advance emulator", renderer.FailureMessage);
                return;
            }
            try { UpdateGameBoyInput(); renderer?.OnFrameworkUpdate(); }
            catch (Exception)
            {
                AepLog.Error("[MGBA] Framework update failed.");
                RecoverFailedGambatte(renderer, "Game Boy Advance emulator", null);
                return;
            }
            _lastIdle = false;
            return;
        }

        if (_isPlayingMasterSystem)
        {
            var renderer = _masterSystemRenderer;
            if (renderer?.HasFailed == true)
            {
                RecoverFailedGambatte(renderer, "Master System / SG-1000 emulator", renderer.FailureMessage);
                return;
            }
            try { UpdateGameBoyInput(); renderer?.OnFrameworkUpdate(); }
            catch (Exception)
            {
                AepLog.Error("[GEARSYSTEM] Framework update failed.");
                RecoverFailedGambatte(renderer, "Master System / SG-1000 emulator", null);
                return;
            }
            _lastIdle = false;
            return;
        }

        if (_isPlayingGameGear)
        {
            var renderer = _gameGearRenderer;
            if (renderer?.HasFailed == true)
            {
                RecoverFailedGambatte(renderer, "Game Gear emulator", renderer.FailureMessage);
                return;
            }
            try { UpdateGameBoyInput(); renderer?.OnFrameworkUpdate(); }
            catch (Exception)
            {
                AepLog.Error("[GEARSYSTEM] Game Gear framework update failed.");
                RecoverFailedGambatte(renderer, "Game Gear emulator", null);
                return;
            }
            _lastIdle = false;
            return;
        }

        if (_isPlayingBrowser)
        {
            var renderer = _browserRenderer;
            if (renderer?.HasFailed != false)
            {
                RecoverNativePlaybackFailure("browser", renderer?.FailureMessage ?? "The browser process stopped unexpectedly.", () =>
                {
                    SetBrowserControlsEnabled(false);
                    _isPlayingBrowser = false;
                    _browserRenderer = null;
                    renderer?.Dispose();
                });
                return;
            }

            if (BrowserControlsEnabled)
            {
                if (TryForceFfxivControl()) return;
                SuppressAllFfxivKeyboardInput();
            }
            try { renderer.OnFrameworkUpdate(); }
            catch (Exception)
            {
                AepLog.Error("[BROWSER] Framework update failed.");
                RecoverNativePlaybackFailure("browser", "The browser process stopped unexpectedly.", () =>
                {
                    SetBrowserControlsEnabled(false);
                    _isPlayingBrowser = false;
                    _browserRenderer = null;
                    renderer.Dispose();
                });
                return;
            }
            _lastIdle = false;
            return;
        }

        //
        // Update the audio visualizer at roughly 30 Hz.
        //
        // There is no reason to query mpv 60+ times per second;
        // the shader itself still renders every frame using the
        // most recently measured value.
        //
        if (_isActive && _mpvRenderer is { } activeMpv)
        {
            // Both the world TV and mini-player sample this small texture
            // directly. Their GPU samplers perform the final display scaling.
            _screenPainter?.SetTarget(activeMpv.AudioSpectrumEnabled ? _visualizerTexture : _screenTexture);
        }

        if (frameConsumersVisible && IsAudioOnly &&
     _mpvRenderer is { } audioRenderer &&
     (!audioRenderer.AudioSpectrumEnabled || painter.MusicEffects.AnyEnabled) &&
             (DateTime.UtcNow -
             _lastAudioLevelUpdate)
                .TotalMilliseconds >= 33)
        {
            _lastAudioLevelUpdate =
                DateTime.UtcNow;

            _screenPainter?.SetAudioLevel(
                audioRenderer.GetAudioLevel());
        }


        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer is not null && _isActive)
        {
            bool idle = GetIdle();
            _lastIdle = idle;
        }
        else
        {
            _lastIdle = true;
        }
    }

    private void RecoverFailedGambatte(GambatteRenderer? renderer, string source, string? message)
    {
        RecoverNativePlaybackFailure(source,
            message ?? $"The {source} stopped unexpectedly.", () =>
            {
                SetGameBoyControlsEnabled(false);
                if (ReferenceEquals(renderer, _gameBoyRenderer))
                {
                    _isPlayingGameBoy = false;
                    _gameBoyRenderer = null;
                }
                if (ReferenceEquals(renderer, _nesRenderer))
                {
                    _isPlayingNes = false;
                    _nesRenderer = null;
                }
                if (ReferenceEquals(renderer, _gameBoyAdvanceRenderer))
                {
                    _isPlayingGameBoyAdvance = false;
                    _gameBoyAdvanceRenderer = null;
                }
                if (ReferenceEquals(renderer, _masterSystemRenderer))
                {
                    _isPlayingMasterSystem = false;
                    _masterSystemRenderer = null;
                }
                if (ReferenceEquals(renderer, _gameGearRenderer))
                {
                    _isPlayingGameGear = false;
                    _gameGearRenderer = null;
                }
                renderer?.Dispose();
            });
    }

    //Places the screen 2 units in front of (and slightly above) the local player, facing the way
    //they're facing. Called when a genuinely new session starts (see AssignScreenForSession), and
    //re-callable any time via RecenterScreen() as a one-tap "lost track of it" reset.
    private void SpawnScreenInFrontOfLocalPlayer()
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer is null)
        {
            return;
        }

        float yaw = localPlayer.Rotation;
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));

        var position = localPlayer.Position + forward * DefaultScreenSpawnDistance + new Vector3(0, DefaultScreenHeightOffset, 0);
        ScreenSpawnAnchor = position;
        SetScreenTransform(position, yaw + MathF.PI, false, 1f, 1f, localPlacement: false, resetPresetNotice: false);
    }

    //One-tap reset for when the screen has drifted out of view/reach - re-spawns it in front of the
    //player exactly like a fresh session would, without touching playback.
    internal void RecenterScreen(bool manualAdjustment = true)
    {
        if (HalloweenPlayback) return;
        if (Plugin.ObjectTable.LocalPlayer is null) return;
        var wasHidden = DutyScreen.Hidden;
        if (!DutyScreen.Place()) return;
        RefreshLocalScreenPlacementContext(force: true);
        localScreenPlacement.ClearFixed();
        if (manualAdjustment) DisableAppliedPresetAutoApply();
        ResetScreenPresetSpawnNotice();
        SpawnScreenInFrontOfLocalPlayer();
        LocalScreenPlaced?.Invoke();
        if (wasHidden) RespawnScreen(recenter: false);
    }

    internal void ReleaseWatchPartyScreenPlacement()
    {
        if (!screenPlacementFromWatchParty) return;
        screenPlacementFromWatchParty = false;
        localScreenPlacement.ClearFixed();
        // Calculate a fresh position at the next spawn, not at departure:
        // the local player may move before starting their own content.
        hasScreenPlacement = false;
    }

    //Live, unsaved position/yaw/scale edit from the Casting tab - only meaningful while the screen is
    //active. Scale is clamped to [MinScreenScale, MaxScreenScale] here rather than at each call site,
    //so drag/slider widgets in the UI can't push it out of range through fast mouse movement.
    //
    // Compatibility overload for existing callers and old presets.
    //
    internal void SetScreenTransform(
        Vector3 position,
        float yaw,
        float scale,
        bool manualAdjustment = false)
    {
        SetScreenTransform(
            position,
            yaw,
            disableFixedScaleRatio: false,
            widthScale: scale,
            heightScale: scale,
            manualAdjustment: manualAdjustment);
    }

    internal void SetScreenTransform(
        Vector3 position,
        float yaw,
        bool disableFixedScaleRatio,
        float widthScale,
        float heightScale,
        bool localPlacement = true,
        bool resetPresetNotice = true,
        bool manualAdjustment = false)
    {
        var wasHidden = localPlacement && DutyScreen.Hidden;
        if (localPlacement)
        {
            if (!DutyScreen.Place()) return;
            RefreshLocalScreenPlacementContext(force: true);
            LocalScreenPlaced?.Invoke();
        }
        var useIndependentScale =
            IndependentScreenScalingEnabled &&
            disableFixedScaleRatio;

        var transformChanged = ScreenPosition != position || ScreenYaw != yaw ||
            DisableFixedScreenScaleRatio != useIndependentScale ||
            ScreenWidthScale != Math.Clamp(widthScale, MinScreenScale, MaxScreenScale) ||
            ScreenHeightScale != Math.Clamp(useIndependentScale ? heightScale : widthScale, MinScreenScale, MaxScreenScale);
        if (manualAdjustment && localPlacement && transformChanged)
        {
            DisableAppliedPresetAutoApply();
            localScreenPlacement.Pin();
        }
        if (resetPresetNotice && transformChanged)
            ResetScreenPresetSpawnNotice();

        ScreenPosition =
            position;
        if (transformChanged || !localPlacement) AppliedScreenPreset = null;
        hasScreenPlacement = true;
        screenPlacementFromWatchParty = false;

        ScreenYaw =
            yaw;

        DisableFixedScreenScaleRatio =
            useIndependentScale;

        ScreenWidthScale =
            Math.Clamp(
                widthScale,
                MinScreenScale,
                MaxScreenScale);

        ScreenHeightScale =
            useIndependentScale
                ? Math.Clamp(
                    heightScale,
                    MinScreenScale,
                    MaxScreenScale)
                : ScreenWidthScale;

        //
        // Preserve a meaningful legacy scale for older clients.
        //
        ScreenScale =
            ScreenWidthScale;

        if (wasHidden) RespawnScreen(recenter: false);

        if (_isActive)
        {
            _screenPainter?.SetTransform(
                ScreenPosition,
                ScreenYaw,
                ScreenWidthScale,
                ScreenHeightScale);
        }
    }

    internal List<ScreenPositionPreset> GetScreenPresets() => [.. _screenPresets];

    private void DisableAppliedPresetAutoApply()
    {
        if (AppliedScreenPreset is not { AutoApply: true } preset ||
            !Plugin.Cfg.ScreenPresets.Contains(preset)) return;
        preset.AutoApply = false;
        InvalidateScreenPresetSpawnNotice(preset);
        Plugin.Cfg.Save();
    }

    internal void SaveScreenPreset(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _screenPresets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _screenPresets.Add(
            new ScreenPositionPreset
            {
                Name =
                    name,

                X =
                    ScreenPosition.X,

                Y =
                    ScreenPosition.Y,

                Z =
                    ScreenPosition.Z,

                Yaw =
                    ScreenYaw,

                Scale =
                    ScreenScale,

                DisableFixedScaleRatio =
                    DisableFixedScreenScaleRatio,

                WidthScale =
                    ScreenWidthScale,

                HeightScale =
                    ScreenHeightScale,
            });

        Plugin.Cfg.ScreenPresets = _screenPresets;
        Plugin.Cfg.Save();
    }

    internal void RemoveScreenPreset(string name)
    {
        _screenPresets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Plugin.Cfg.ScreenPresets = _screenPresets;
        Plugin.Cfg.Save();
    }

    internal void ApplyScreenPreset(
        ScreenPositionPreset preset, bool spawnScreen = false)
    {
        if (DutyScreen.InDuty) return;
        RefreshLocalScreenPlacementContext(force: true);
        screenPresetSpawnNotice.Observe(preset, CurrentPresetNoticeLocation());
        var wasActive = IsActive;
        var position =
            new Vector3(
                preset.X,
                preset.Y,
                preset.Z);

        ScreenSpawnAnchor =
            position;

        var widthScale =
            preset.WidthScale ??
            preset.Scale;

        var heightScale =
            preset.HeightScale ??
            preset.Scale;

        localScreenPlacement.Pin();
        SetScreenTransform(
            position,
            preset.Yaw,
            preset.DisableFixedScaleRatio,
            widthScale,
            heightScale, resetPresetNotice: false);
        if (spawnScreen && !IsActive)
            RespawnScreen(recenter: false, preserveExactPlacement: true);
        if (!DutyScreen.Hidden) AppliedScreenPreset = preset;
        if (spawnScreen && IsActive)
        {
            // A manual Load takes precedence over automatic selection until
            // the eligible area changes; do not immediately replace this preset.
            if (WatchPartyTvLocation.TryReadCurrent(out var location) && Plugin.ObjectTable.LocalPlayer is { } player)
                screenPresetAutoApply.Observe(Plugin.Cfg.ScreenPresets, location.Encode(), player.Position,
                    WatchPartyTvLocation.IsResidentialInterior());
            presetScreenWasActive = true;
            presetSpawnNoticePending = false;
            if (!wasActive)
                AnnounceScreenPresetSpawn(preset);
        }
    }

    // Applied when watching someone else's AetherStream over StreamClient and their host client
    // publishes a screen transform (see StreamClient.PublishStateAsync/StreamControl's
    // ScreenX/Y/Z/Yaw/Scale). There is no shared/networked 3D object - this just makes the local
    // ScreenPainter draw at the same coordinates the host is using, same as any other placement.
    internal void ApplyRemoteScreenTransform(
        Vector3 position,
        float yaw,
        float legacyScale,
        bool disableFixedScaleRatio,
        float? widthScale,
        float? heightScale)
    {
        localScreenPlacement.ClearFixed();
        ScreenSpawnAnchor =
            position;

        SetScreenTransform(
            position,
            yaw,
            disableFixedScaleRatio,
            widthScale ??
            legacyScale,
            heightScale ??
            legacyScale,
            localPlacement: false);
        screenPlacementFromWatchParty = true;
    }

    //Called whenever the queue advances or a watch-along viewer's remote state changes, so the
    //in-world screen's own "now playing" banner tracks the same title everyone sees.
    internal void SetOverlayTitle(string title, string source) => _screenPainter?.SetTitle(title, source);

    //Called every tick from Plugin.cs with the current active reaction particles - see
    //ScreenPainter.SetReactions for the render side.
    internal void SetReactions(IReadOnlyList<ReactionParticle> reactions) => _screenPainter?.SetReactions(reactions);

    // Convert a nearby character's live world position into a horizontal point on this client's
    // TV. Each viewer performs this calculation against their own local TV transform when the
    // reaction arrives, so no position or placement data needs to cross the relay.
    internal bool TryGetReactionSpawnX(Vector3 characterPosition, out float x)
    {
        x = 0.5f;
        if (!IsActive)
        {
            return false;
        }

        var toCharacter = characterPosition - ScreenPosition;
        toCharacter.Y = 0f;
        if (toCharacter.LengthSquared() < 0.0001f)
        {
            return false;
        }

        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, ScreenYaw);
        var screenForward = Vector3.Transform(Vector3.UnitZ, rotation);
        var screenRight = Vector3.Transform(Vector3.UnitX, rotation);
        var forwardDistance = Vector3.Dot(toCharacter, screenForward);

        // A character behind the display has no meaningful position on its visible face.
        if (forwardDistance <= 0.05f)
        {
            return false;
        }

        var signedAngle = MathF.Atan2(
            Vector3.Dot(toCharacter, screenRight),
            forwardDistance);

        const float maximumMappedAngle = 70f * (MathF.PI / 180f);
        x = Math.Clamp(
            0.5f + (signedAngle / maximumMappedAngle) * 0.45f,
            0.05f,
            0.95f);

        return true;
    }

    // Maps a desktop cursor position onto the curved in-world TV surface. The geometry mirrors
    // ScreenPainter's procedural 24-segment quad so interactive media can use texture-space hit
    // regions without changing how the TV itself is rendered.
    internal bool TryScreenPointToTvUv(Vector2 screenPoint, out Vector2 uv)
    {
        uv = default;
        if (!IsActive)
        {
            return false;
        }

        const int curveSegments = 24;
        const float baseWidth = 1f;
        const float baseHeight = 0.6f;
        const float curvature = 0.12f;
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, ScreenYaw);
        var depthScale = (ScreenWidthScale + ScreenHeightScale) * 0.5f;

        bool Project(float normalizedX, float normalizedY, out Vector2 projected)
        {
            var local = new Vector3(
                normalizedX * baseWidth * ScreenWidthScale,
                normalizedY * baseHeight * ScreenHeightScale,
                curvature * normalizedX * normalizedX * depthScale);
            return Plugin.GameGui.WorldToScreen(
                ScreenPosition + Vector3.Transform(local, rotation), out projected);
        }

        for (var segment = 0; segment < curveSegments; segment++)
        {
            var u0 = segment / (float)curveSegments;
            var u1 = (segment + 1) / (float)curveSegments;
            var x0 = -1f + u0 * 2f;
            var x1 = -1f + u1 * 2f;
            if (!Project(x0, 1f, out var top0) || !Project(x0, -1f, out var bottom0) ||
                !Project(x1, 1f, out var top1) || !Project(x1, -1f, out var bottom1))
            {
                continue;
            }

            if (TryInterpolateTvTriangle(screenPoint, top0, new Vector2(u0, 0f),
                    bottom0, new Vector2(u0, 1f), bottom1, new Vector2(u1, 1f), out uv) ||
                TryInterpolateTvTriangle(screenPoint, top0, new Vector2(u0, 0f),
                    bottom1, new Vector2(u1, 1f), top1, new Vector2(u1, 0f), out uv))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryInterpolateTvTriangle(
        Vector2 point,
        Vector2 a,
        Vector2 uvA,
        Vector2 b,
        Vector2 uvB,
        Vector2 c,
        Vector2 uvC,
        out Vector2 uv)
    {
        uv = default;
        var denominator = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (MathF.Abs(denominator) < 0.001f)
        {
            return false;
        }

        var weightA = ((b.Y - c.Y) * (point.X - c.X) +
                       (c.X - b.X) * (point.Y - c.Y)) / denominator;
        var weightB = ((c.Y - a.Y) * (point.X - c.X) +
                       (a.X - c.X) * (point.Y - c.Y)) / denominator;
        var weightC = 1f - weightA - weightB;
        const float tolerance = -0.002f;
        if (weightA < tolerance || weightB < tolerance || weightC < tolerance)
        {
            return false;
        }

        uv = uvA * weightA + uvB * weightB + uvC * weightC;
        return true;
    }

    // Hands the painter its new texture without changing the TV transform. A media change often
    // makes the renderer briefly inactive, but that is not a request to move or resize the TV.
    // Automatic placement is reserved for the first screen assignment; later placement changes
    // come only from the user or an explicitly inherited Watch Party transform.
    private void AssignScreenForSession(Texture2D screenTexture, bool preserveScreenVisibility = false)
    {
        RefreshLocalScreenPlacementContext(force: true);
        if (!preserveScreenVisibility) localScreenHidden = false;
        _screenPainter?.SetTarget(screenTexture);

        if (!hasScreenPlacement || (!preserveScreenVisibility && !_isActive &&
            localScreenPlacement.ShouldRecenter(screenPlacementFromWatchParty)))
        {
            SpawnScreenInFrontOfLocalPlayer();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        static void Cleanup(
            ShutdownResource name,
            Action action)
        {
            try
            {
                action();
            }
            catch (Exception error) { AepLog.CleanupFailure(ShutdownOwner.VideoEngine,name,error); }
        }

        lock (graphicsStartupLock) _disposing = true;
        DisposeMusicSpotlight();
        Cleanup(ShutdownResource.RetroControllerCapture, DisposeControllerMode);
        _stopRequested = true;
        _isActive = false;

        // Stop invoking the painter before any of its resources can be
        // released. Pending one-shot cleanup work is still processed.
        DxHandler.BeginShutdown();

        _lifetimeCancellation.Cancel();

        Cleanup(ShutdownResource.SNESControls, () => SetSnesControlsEnabled(false));
        Cleanup(ShutdownResource.GameBoyControls, () => SetGameBoyControlsEnabled(false));
        Cleanup(ShutdownResource.BrowserControls, () => SetBrowserControlsEnabled(false));
        SetBlockAllFfxivKeyboardInput(false);

        CancellationTokenSource renderCancellation;
        lock (_renderCancellationLock)
        {
            renderCancellation = _renderCancellation;
        }

        try { renderCancellation.Cancel(); }
        catch (ObjectDisposedException) { }

        var shutdownRenderer = rendererSlot.Detach();
        if (shutdownRenderer is not null)
        {
            shutdownRenderer.RequestShutdown();
            _ = Task.Run(() => Cleanup(ShutdownResource.VideoPlayer, shutdownRenderer.Dispose));
        }

        try
        {
            Task.WhenAll(playbackWorkers.Keys).Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[MPV] Failed waiting for render task during shutdown.");
        }

        try
        {
            if (_webResolverFallbackTask is { IsCompleted: false } &&
                !_webResolverFallbackTask.Wait(TimeSpan.FromSeconds(3)))
            {
                AepLog.Warning(
                    "[WebResolver] Fallback worker did not stop within 3 seconds.");
            }
        }
        catch (Exception)
        {
            
        }

        try
        {
            _snesRenderer?.Dispose();
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[SNES9X] Failed renderer dispose during shutdown.");
        }

        _snesRenderer = null;
        _isPlayingSnes = false;

        try { _gameBoyRenderer?.Dispose(); }
        catch (Exception) { AepLog.Warning("[GAMBATTE] Failed renderer dispose during shutdown."); }
        _gameBoyRenderer = null;
        _isPlayingGameBoy = false;

        try { _nesRenderer?.Dispose(); }
        catch (Exception) { AepLog.Warning("[NESTOPIA] Failed renderer dispose during shutdown."); }
        _nesRenderer = null;
        _isPlayingNes = false;

        try { _gameBoyAdvanceRenderer?.Dispose(); }
        catch (Exception) { AepLog.Warning("[MGBA] Failed renderer dispose during shutdown."); }
        _gameBoyAdvanceRenderer = null;
        _isPlayingGameBoyAdvance = false;

        try { _masterSystemRenderer?.Dispose(); }
        catch (Exception) { AepLog.Warning("[GEARSYSTEM] Failed renderer dispose during shutdown."); }
        _masterSystemRenderer = null;
        _isPlayingMasterSystem = false;

        try { _gameGearRenderer?.Dispose(); }
        catch (Exception) { AepLog.Warning("[GEARSYSTEM] Failed Game Gear renderer dispose during shutdown."); }
        _gameGearRenderer = null;
        _isPlayingGameGear = false;

        try { _browserRenderer?.Dispose(); }
        catch (Exception) { AepLog.Warning("[BROWSER] Failed renderer dispose during shutdown."); }
        _browserRenderer = null;
        _isPlayingBrowser = false;

        Cleanup(ShutdownResource.LocalVideoBroadcaster, _localVideoBroadcastEncoder.Dispose);

        Cleanup(ShutdownResource.ImagePlayback, () => StopImagePlayback(waitForCompletion: true));
        Cleanup(ShutdownResource.ImageRenderer, _imageRenderer.Dispose);

        if (graphics is { } gpuResources)
            DxHandler.QueueShutdownWork(gpuResources.Dispose);

        Cleanup(ShutdownResource.MediaResources, Resources.Dispose);

        lock (_renderCancellationLock)
        {
            try { _renderCancellation.Dispose(); }
            catch (ObjectDisposedException) { }
        }

        Cleanup(ShutdownResource.PlaybackLifetimeToken, _lifetimeCancellation.Dispose);

        _imageElapsedBeforeClockStart =
    0d;

        _imageClockStartedUtc =
            DateTime.MinValue;

        _imagePlaybackPaused =
            false;
    }
}

using System.Collections.Concurrent;
using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Core;
using AlphaChannel.Plugin.BroadcastAllowance;
using AlphaChannel.Plugin.ActivityPoints;
using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.Net;
using AlphaChannel.Plugin.YouTube;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Utility.Raii;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace AlphaChannel.Plugin;

public sealed partial class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!; [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider InteropProvider { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;

    [PluginService] internal static INamePlateGui NamePlateGui { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static INotificationManager NotificationManager { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;

    [PluginService] internal static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IGamepadState GamepadState { get; private set; } = null!;

    internal static Configuration Cfg { get; private set; } = null!;
    private static Lighting.WorldLightingController? worldLighting;
    internal static Lighting.WorldLightingController LightingControls => worldLighting ??= new();

    private readonly WindowSystem windowSystem = new("AlphaChannel");
    // Standard-mode services are absent when only the recovery window is loaded.
    private readonly ScreenController screenController = null!;
    private readonly TvGizmoOverlay tvGizmoOverlay = null!;
    private readonly VideoPlayer video = null!;
    private readonly AetherStreamQueue personalQueue = null!;
    private AetherStreamQueue queue => mainWindow?.PartyPlaybackQueue ?? personalQueue;
    private readonly StreamClient stream = null!;
    private readonly MainWindow mainWindow = null!;
    private readonly MiniPlayerWindow miniPlayerWindow = null!;
    private readonly PlaybackHud playbackHud = null!;
    private readonly PluginUpdateMonitor pluginUpdates = null!;
    private readonly MiniChatWindow miniChatWindow = null!;
    private readonly AuthClient authClient = null!;
    private readonly SignInFlow signInFlow = null!;
    private readonly FriendsClient friendsClient = null!;
    private readonly ActivityClient activityClient = null!;
    private readonly DmClient dmClient = null!;
    private readonly ReportClient reportClient = null!;
    private readonly VenuesClient venuesClient = null!;
    private readonly LiveClient liveClient = null!;
    private readonly RoomsClient roomsClient = null!;
    private readonly RadioClient radioClient = null!;
    private readonly TwitchClient twitchClient = null!;
    private readonly KeysClient keysClient = null!;
    private readonly AlphaChannel.Plugin.Crypto.KeyVault keyVault = null!;
    private readonly BroadcastAllowanceService broadcastAllowance = null!;
    private readonly BroadcastLedgerCoordinator broadcastLedger = new();
    private readonly ActivityPointsService activityPoints = null!;
    private readonly NearbyAutoWatch nearbyAutoWatch = null!;
    private readonly ConcurrentQueue<Action> frameworkActions = new();
    private readonly CancellationTokenSource sessionValidationLifetime = new();
    private Task? sessionValidationTask;
    private string? lastHonorificStatus;
    private int lastHonorificColorPreset = -1;
    private string? lastAppliedHonorificTitleJson;
    private bool honorificOverrideApplied;
    private uint lastHonorificEntityId;
    private DateTime nextHonorificIpcAttemptUtc = DateTime.MinValue;
    private DateTime nextHonorificTitleReadUtc = DateTime.MinValue;
    private readonly HonorificStatusGracePeriod honorificGrace = new(HonorificTitlePayload.Equivalent);
    private ICallGateSubscriber<string, object>? honorificTitleChanged;
    private int honorificObservationGeneration;
    private int disposeStarted;
    private readonly object frameworkLifetimeLock = new();
    private string? pendingUpdateNoticeVersion;
    private ulong observedSessionContentId;
    private ulong validatedSessionContentId;
    private ulong validatingSessionContentId;
    private DateTime nextSessionValidationAttemptUtc = DateTime.MinValue;
    private string? keyRegistrationAccountId;
    private string? keyRegistrationToken;
    private bool keyRegistrationInProgress;
    private bool keyRegistrationVerified;
    private DateTime nextKeyRegistrationAttemptUtc = DateTime.MinValue;
    private bool restrictedContentActive;
    private DateTime nextYouTubeProviderIdleCheckUtc = DateTime.MinValue;
    private Task? youtubeProviderStopTask;
    private DateTime? youtubeProviderIdleSinceUtc;

    // Written from the network thread (OnRemoteState), read/cleared on the main thread
    // (OnFrameworkUpdate). Interlocked.Exchange lets the framework take the
    // latest state without clearing a newer message written during that handoff.
    private AlphaChannel.Contracts.StreamControl? pendingRemoteState;
    private string? lastReceivedRemoteUrl;

    private double lastRecentlyWatchedSave;
    private int lastRecommendedPlaybackAttemptId = -1;

    // How far a viewer's local position can drift from the host's reported position before it's
    // worth a corrective seek. Below this, natural playback + network jitter accounts for the gap.
    private const float SyncToleranceSeconds = 2.5f;

    // The URL last handed to video.Play() as a viewer. VideoEngine.PlayVideo's own guard against
    // redundant reloads only kicks in once its internal mpv instance actually exists - for a fresh
    // video that can take up to several seconds (a YouTube rate-limit cooldown runs before the
    // instance is created), and the host publishes state every tick with no diff-check, so without
    // this a viewer could fire off dozens of concurrent PlayVideo calls for the same URL before the
    // first one ever finishes initializing - exactly what looked like a permanently frozen screen.
    private string? lastAppliedRemoteUrl;
    private readonly AlphaChannel.Plugin.Core.ViewerPlaybackRecovery youtubeViewerRecovery = new();
    private bool waitingForMedia;
    private bool loggedEmptyRemoteState;

    // MediaMTX can briefly return 404 while a new RTMP publisher creates
    // its HLS manifest. Retry live URLs without launching a new renderer
    // every framework update.
    private DateTime nextLiveHlsRetryUtc =
        DateTime.MinValue;
    private string? liveHlsProbeUrl;
    private readonly LiveDjRetry liveDjRetry = new();
    private DateTime nextLiveDjUpdateUtc;
    private Task<bool>? liveHlsProbeTask;
    private bool liveHlsReady;
    private DateTime nextLiveHlsProbeUtc = DateTime.MinValue;
    private string? liveHlsStartupUrl;
    private DateTime liveHlsPlaybackAllowedUtc = DateTime.MinValue;

    private static readonly TimeSpan LiveHlsStartupDelay = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan LiveHlsRetryDelay =
        TimeSpan.FromSeconds(
            2);

    private bool screenRangeDespawned;
    private bool screenRangeWarningShown;
    private bool viewerTvNeedsLocationRecheckAfterZoning;
    private DateTime viewerTvLocationRecheckStartedUtc;

    private const float HostScreenWarnDistance = 35f;
    private const float HostScreenDespawnDistance = VideoEngine.MaximumScreenDistance;

    // Latest state received from the host. Kept even while this viewer has
    // chosen not to spawn their local TV.
    private AlphaChannel.Contracts.StreamControl? latestRemoteState;

    private static readonly TimeSpan ReactionLifetime = TimeSpan.FromSeconds(3);
    private const float CharacterReactionHorizontalJitter = 0.06f;
    private readonly List<InWorldReaction> activeReactions = new();

    public string Name => "AlphaChannel";

    private StartupRecoveryWindow? recoveryWindow;
    private volatile bool startupComplete;
    private bool alphaCommandRegistered, watchPartyCommandRegistered;
    [ThreadStatic] private static bool drawingPluginUi;
    internal static bool IsDrawingPluginUi => drawingPluginUi;

    public Plugin()
    {
        var version = typeof(Plugin).Assembly.GetName().Version ?? PluginInterface.Manifest.AssemblyVersion;
        var startup = new StartupRecoveryRecord(PluginInterface.ConfigDirectory.FullName, version);
        var interrupted = false;
        try
        {
            interrupted = startup.WasInterrupted(PluginInterface.Reason == PluginLoadReason.Update);
            if (!interrupted) startup.Begin();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AepLog.Warning("[Startup] Could not write the recovery record.");
        }
        if (interrupted)
        {
            recoveryWindow = new StartupRecoveryWindow(startup);
            return;
        }

        try
        {
            AlphaChannel.Media.MediaTlsTrust.ConfigureDirectory(PluginInterface.AssemblyLocation.DirectoryName!);
            Cfg = ConfigurationMigration.Load(
                PluginInterface,
                out var configurationWasReset);
            YouTubeRequestScheduler.Shared.Configure(Cfg);

            // Dalamud distinguishes an update from installation, boot and developer reloads.
            // Defer the notice until login so updates applied at the title screen are visible.
            if (PluginInterface.Reason == PluginLoadReason.Update)
                pendingUpdateNoticeVersion = typeof(Plugin).Assembly.GetName().Version?.ToString()
                    ?? PluginInterface.Manifest.AssemblyVersion.ToString();

            if (configurationWasReset)
            {
                ChatGui.Print(
                    "Your Alpha Channel config file has been reset to allow compatibility with a newer version and migration system. This will only happen once");
            }

            // Configuration.Initialize selects the build-specific server and vault.

            // Old builds defaulted AutoWatchNearby to true; that scan wiped YouTube typing by
            // flipping Player tabs / joining nearby names. Force off until the feature is re-enabled.
            if (Cfg.AutoWatchNearby)
            {
                Cfg.AutoWatchNearby = false;
                Cfg.Save();
            }

            // The video engine installs graphics hooks on its first rendering request.
            screenController = new ScreenController(
                () => Cfg.ScreenNameplateHideRadiusYalms);
            screenController.Engine.KeepGameUiVisibleOverTv =
                Cfg.KeepGameUiVisibleOverTv;
            video = new VideoPlayer(screenController.Engine);
            video.SetVolume(Cfg.Muted ? 0 : Cfg.Volume);

            video.CookiesPath =
                YouTubeEmbeddedBrowserSession.IsConnected(Cfg)
                    ? YouTubeEmbeddedBrowserSession.CookieFilePath
                    : null;

            //
            // Repair/migrate saved queue slots before reading ActiveQueueSlot.
            //
            QueueManager.NormalizeConfiguration(
                Cfg);

            var activeProfile =
                Cfg.SavedQueueProfiles[
                    Cfg.ActiveQueueSlot];

            personalQueue =
                new AetherStreamQueue(
                    video,
                    activeProfile?.Entries ??
                    Enumerable.Empty<VideoQueueRecord>());
            stream = new StreamClient(
                Cfg,
                () =>
                {
                    var contentId =
                        ReadLocalContentId();

                    var accountDisplayName =
                        Cfg.CharacterSessions
                            .GetValueOrDefault(
                                contentId)
                            ?.DisplayName;

                    return !string.IsNullOrWhiteSpace(
                        accountDisplayName)
                        ? accountDisplayName
                        : Cfg.CharacterDisplayNames
                            .GetValueOrDefault(
                                contentId);
                },
                () => Cfg.SessionForCharacter(ReadLocalContentId()));
            video.PreserveHostDjAudioSignal = () =>
                stream.Mode == StreamMode.Hosting &&
                queue.Current?.Source == "Music / DJ" &&
                LiveDjStream.AppliesTo(video.CurrentMediaUrl);
            stream.OnState += OnRemoteState;
            screenController.Engine.LocalScreenPlaced += stream.ReleaseDutyScreen;
            stream.OnRenameRequired += OnRenameRequired;

            authClient = new AuthClient(Cfg);
            signInFlow = new SignInFlow(authClient);
            friendsClient = new FriendsClient(Cfg);
            activityClient = new ActivityClient(Cfg);
            dmClient = new DmClient(Cfg);
            reportClient = new ReportClient(Cfg);
            venuesClient = new VenuesClient(Cfg);
            liveClient = new LiveClient(Cfg);
            roomsClient = new RoomsClient(Cfg);
            radioClient = new RadioClient(Cfg);
            twitchClient = new TwitchClient(Cfg);
            keysClient = new KeysClient(Cfg);
            keyVault = new AlphaChannel.Plugin.Crypto.KeyVault(Cfg, keysClient);
            broadcastAllowance = new BroadcastAllowanceService(
                Cfg,
                PluginInterface.ConfigDirectory.FullName);
            activityPoints = new ActivityPointsService(PluginInterface.ConfigDirectory.FullName);
            mainWindow = new MainWindow(screenController, video, queue, stream, RequestRename, SaveLocalDisplayName, authClient, signInFlow,
                friendsClient, activityClient, dmClient, reportClient, venuesClient, liveClient,
                roomsClient, radioClient,
                twitchClient, keyVault, broadcastAllowance, activityPoints, UpdateSessionForCurrentCharacter);
            mainWindow.BroadcastLedger = broadcastLedger;
            tvGizmoOverlay = new TvGizmoOverlay(
                screenController.Engine,
                () => Cfg.TvGizmoKey,
                () => mainWindow.IsCapturingTvGizmoKey ||
                      stream.Mode == StreamMode.Viewing && mainWindow.PartySyncTvPlacement,
                () => mainWindow.QuizTvControlsInteractive,
                mainWindow.IsQuizTvControlAt,
                mainWindow.SetQuizTvHover,
                mainWindow.SubmitQuizTvControlAt,
                () => mainWindow.BlastPartyTvControlsInteractive || mainWindow.PixelsTvControlsInteractive || mainWindow.RouletteTvControlsInteractive || mainWindow.ChocoRaceTvControlsInteractive || mainWindow.BingoTvControlsInteractive || mainWindow.SkyRailTvControlsInteractive || mainWindow.SlotMachineTvControlsInteractive || mainWindow.PokerTvControlsInteractive || mainWindow.LuckyWheelTvControlsInteractive,
                uv => mainWindow.BlastPartyTvControlsInteractive ? mainWindow.IsBlastPartyTvControlAt(uv) : mainWindow.PixelsTvControlsInteractive ? mainWindow.IsPixelsTvControlAt(uv) : mainWindow.ChocoRaceTvControlsInteractive
                    ? mainWindow.IsChocoRaceTvControlAt(uv) : mainWindow.BingoTvControlsInteractive
                        ? mainWindow.IsBingoTvControlAt(uv) : mainWindow.SkyRailTvControlsInteractive
                            ? mainWindow.IsSkyRailTvControlAt(uv) : mainWindow.SlotMachineTvControlsInteractive
                                ? mainWindow.IsSlotMachineTvControlAt(uv) : mainWindow.PokerTvControlsInteractive
                                    ? mainWindow.IsPokerTvControlAt(uv) : mainWindow.LuckyWheelTvControlsInteractive
                                        ? mainWindow.IsLuckyWheelTvControlAt(uv) : mainWindow.IsRouletteTvControlAt(uv),
                uv =>
                {
                    if (mainWindow.BlastPartyTvControlsInteractive) return;
                    if (mainWindow.PixelsTvControlsInteractive) mainWindow.SetPixelsTvHover(uv);
                    else if (mainWindow.ChocoRaceTvControlsInteractive) mainWindow.SetChocoRaceTvHover(uv);
                    else if (mainWindow.BingoTvControlsInteractive) mainWindow.SetBingoTvHover(uv);
                    else if (mainWindow.SkyRailTvControlsInteractive) mainWindow.SetSkyRailTvHover(uv);
                    else if (mainWindow.SlotMachineTvControlsInteractive) mainWindow.SetSlotMachineTvHover(uv);
                    else if (mainWindow.PokerTvControlsInteractive) mainWindow.SetPokerTvHover(uv);
                    else if (!mainWindow.LuckyWheelTvControlsInteractive) mainWindow.SetRouletteTvHover(uv);
                },
                uv =>
                {
                    if (mainWindow.BlastPartyTvControlsInteractive) mainWindow.SubmitBlastPartyTvControlAt(uv);
                    else if (mainWindow.PixelsTvControlsInteractive) mainWindow.SubmitPixelsTvControlAt(uv);
                    else if (mainWindow.ChocoRaceTvControlsInteractive) mainWindow.SubmitChocoRaceTvControlAt(uv);
                    else if (mainWindow.BingoTvControlsInteractive) mainWindow.SubmitBingoTvControlAt(uv);
                    else if (mainWindow.SkyRailTvControlsInteractive) mainWindow.SubmitSkyRailTvControlAt(uv);
                    else if (mainWindow.SlotMachineTvControlsInteractive) mainWindow.SubmitSlotMachineTvControlAt(uv);
                    else if (mainWindow.PokerTvControlsInteractive) mainWindow.SubmitPokerTvControlAt(uv);
                    else if (mainWindow.LuckyWheelTvControlsInteractive) mainWindow.SubmitLuckyWheelTvControlAt(uv);
                    else mainWindow.SubmitRouletteTvControlAt(uv);
                },
                (uv, mouseDown, mousePressed) =>
                {
                    if (mainWindow.BlastPartyTvControlsInteractive || mainWindow.PixelsTvControlsInteractive) return;
                    if (mainWindow.ChocoRaceTvControlsInteractive)
                        mainWindow.UpdateChocoRaceTvVolumePointer(uv, mouseDown, mousePressed);
                    else if (mainWindow.BingoTvControlsInteractive)
                        mainWindow.UpdateBingoTvVolumePointer(uv, mouseDown, mousePressed);
                    else if (mainWindow.SkyRailTvControlsInteractive)
                        mainWindow.UpdateSkyRailTvVolumePointer(uv, mouseDown, mousePressed);
                    else if (!mainWindow.SlotMachineTvControlsInteractive && !mainWindow.PokerTvControlsInteractive && !mainWindow.LuckyWheelTvControlsInteractive)
                        mainWindow.UpdateRouletteTvVolumePointer(uv, mouseDown, mousePressed);
                },
                () => mainWindow.PictionaryTvControlsInteractive,
                mainWindow.IsPictionaryTvControlAt,
                mainWindow.SetPictionaryTvHover,
                mainWindow.SubmitPictionaryTvControlAt,
                mainWindow.UpdatePictionaryTvDrawingPointer,
                () => mainWindow.PictionaryTvDrawingActive,
                () => mainWindow.PictionaryTvGuessInputActive || mainWindow.BingoNumberInputActive,
                () => mainWindow.BingoNumberInputActive
                    ? mainWindow.BingoNumberInput : mainWindow.PictionaryTvGuessInput,
                value =>
                {
                    if (mainWindow.BingoNumberInputActive) mainWindow.SetBingoNumberInput(value);
                    else mainWindow.SetPictionaryTvGuessInput(value);
                },
                submit =>
                {
                    if (mainWindow.BingoNumberInputActive) mainWindow.FinishBingoNumberInput(submit);
                    else mainWindow.FinishPictionaryTvGuess(submit);
                });

            mainWindow.OnViewerTvSpawnRequested = SpawnViewerTv;

            miniPlayerWindow = new MiniPlayerWindow(
                video,
                screenController.Engine,
                stream,
                () => mainWindow.GetMiniModeTitle(),
                () => mainWindow.IsArcadeGameActive,
                OpenMiniChat,
                () => mainWindow.OpenUi());

            miniChatWindow = new MiniChatWindow(
                stream,
                () => mainWindow.GetMiniPartyTitle(),
                mainWindow.DrawMiniPartyChatContents,
                OpenMiniPlayer,
                mainWindow.OpenFullWatchPartyChat);

            mainWindow.OnMiniPlayerRequested = OpenMiniPlayer;
            mainWindow.OnMiniPlayerToggleRequested = ToggleMiniPlayer;
            mainWindow.OnPartyPlaybackResyncRequested = () =>
            {
                if (stream.Mode != StreamMode.Viewing || latestRemoteState is not { } state ||
                    restrictedContentActive ||
                    !(mainWindow.ViewerTvEnabled || MiniPlaybackActive)) return;

                // Use the normal viewer path, including broadcast checks and safe TV placement.
                liveDjRetry.Reset();
                lastAppliedRemoteUrl = null;
                nextLiveHlsRetryUtc = DateTime.MinValue;
                if (state.Url is { } broadcastUrl && IsLiveBroadcastUrl(broadcastUrl))
                {
                    // Re-sync must reopen the live decoder. Clearing only the URL
                    // guard lets VideoPlayer's same-media shortcut keep a stalled
                    // session alive, which previously required respawning the TV.
                    liveHlsReady = false;
                    liveHlsProbeTask = null;
                    nextLiveHlsProbeUtc = DateTime.MinValue;
                    ShowViewerBroadcastStatus(LiveBroadcastStream.ConnectingMessage);
                }
                ApplyRemoteState(state);
            };
            mainWindow.OnMiniChatRequested = OpenMiniChat;
            mainWindow.OnMiniChatToggleRequested = ToggleMiniChat;
            mainWindow.IsMiniPlayerOpen = () => miniPlayerWindow.IsOpen;
            mainWindow.IsMiniChatOpen = () => miniChatWindow.IsOpen;
            video.ShouldSpawnTvOnResume = () => stream.Mode != StreamMode.Viewing && !MiniPlaybackActive;
            video.ShouldPreserveTvOnPlaybackFailure = () => stream.Mode == StreamMode.Viewing &&
                mainWindow.ViewerTvEnabled;

            pluginUpdates = new PluginUpdateMonitor(
                async () => PluginInterface.IsDev ? null : (await PluginInterface.CheckForUpdateAsync().ConfigureAwait(false))?.Version,
                exception => { });

            mainWindow.AvailablePluginUpdate = () => pluginUpdates.AvailableVersion;
            mainWindow.OnPluginUpdateRequested = () => PluginInterface.OpenPluginInstallerTo(
                Dalamud.Interface.PluginInstallerOpenKind.UpdateablePlugins, "AlphaChannel");

            playbackHud = new PlaybackHud(
                video,
                screenController.Engine,
                stream,
                () => mainWindow.ViewerTvEnabled,
                () => miniPlayerWindow.IsOpen,
                () => mainWindow.IsOpen,
                mainWindow.GetPlaybackHudNotificationCount,
                mainWindow.ToggleTvFromPlaybackHud,
                ToggleMiniPlayer,
                ToggleMainWindow,
                mainWindow.AvailablePluginUpdate,
                mainWindow.OnPluginUpdateRequested);

            miniPlayerWindow.EnterAudioOnly = EnterAudioOnlyMode;
            miniPlayerWindow.CanEnterAudioOnly = CanEnterAudioOnlyMode;
            miniPlayerWindow.AudioOnlyUnavailableReason = () => mainWindow.HalloweenActivityActive || screenController.Engine.HalloweenPlayback
                ? "Audio only is unavailable during Halloween 2026" : "Audio only is unavailable for this content";
            mainWindow.OnAudioOnlyRequested = miniPlayerWindow.EnterAudioOnly;
            mainWindow.CanEnterAudioOnly = miniPlayerWindow.CanEnterAudioOnly;
            mainWindow.AudioOnlyUnavailableReason = miniPlayerWindow.AudioOnlyUnavailableReason;
            playbackHud.ApplyListeningVolume = mainWindow.ApplyListeningVolume;
            playbackHud.DrawVolumeContents = mainWindow.DrawQuickVolumeContents;
            playbackHud.VolumePanelSize = ()=>mainWindow.QuickVolumePanelSize;
            mainWindow.DrawHeaderLightingPanel = playbackHud.DrawHeaderLightingPanel;
            video.CanStartPlayback = mainWindow.CanStartPlaybackUrl;
            stream.PrepareViewerJoinAsync = () => Framework.RunOnFrameworkThread(mainWindow.PrepareWatchPartyViewerJoin);
            screenController.Engine.CanStartOtherMedia = mainWindow.CanStartOtherStudioMedia;
            screenController.Engine.YouTubeSetupBlocked += mainWindow.ShowYouTubeSetupNotice;
            mainWindow.InitializeYouTubeAgeRestrictions();
            screenController.Engine.YouTubeAgeRestricted += mainWindow.QueueYouTubeAgeWarning;
            screenController.Engine.ActivityStartBlocked += mainWindow.NotifyActivityStartBlocked;
            nearbyAutoWatch = new NearbyAutoWatch(stream, mainWindow);
            windowSystem.AddWindow(mainWindow);
            windowSystem.AddWindow(miniPlayerWindow);
            windowSystem.AddWindow(miniChatWindow);

            SubscribeHonorificTitleChanges();
            InitializeServerInfoBar();
            Framework.Update += OnFrameworkUpdate;
            PluginInterface.UiBuilder.DisableGposeUiHide = Cfg.ShowControlsDuringGpose;
            PluginInterface.UiBuilder.Draw += DrawPluginUi;
            PluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;
            PluginInterface.UiBuilder.OpenConfigUi += mainWindow.OpenSettingsUi;
            ContextMenu.OnMenuOpened += OnCharacterMenuOpened;
            alphaCommandRegistered = CommandManager.AddHandler("/alpha", new CommandInfo(OnCommand)
            {
                HelpMessage = "Open Alpha Channel.",
            });
            watchPartyCommandRegistered = CommandManager.AddHandler("/wp", new CommandInfo(OnWatchPartyChatCommand)
            {
                HelpMessage = "Send a message to your current Alpha Channel watch party.",
            });
            // Start the account worker only after initialization has succeeded.
            // A constructor failure cannot leave an unowned worker holding its lease.
            stream.Start();
        }
        catch (Exception error)
        {
            AepLog.Warning($"[Startup] Initialization failed ({error.GetType().Name}, 0x{error.HResult:X8}).");
            DisposeStandardMode();
            recoveryWindow = new StartupRecoveryWindow(startup);
            return;
        }

        try { startup.Complete(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AepLog.Warning("[Startup] Could not clear the startup record.");
        }
        startupComplete = true;
    }

    private void OnCommand(string command, string arguments)
    {
        var args = arguments.Trim();
#if DEBUG
        // Recovery remains reachable even if signup cannot finish and Account
        // settings are still hidden by onboarding.
        if (args.Equals("restore-account", StringComparison.OrdinalIgnoreCase))
        {
            if (Cfg.DebugAccountTest is not { } test || (!test.Loaded && !test.Requested))
                ChatGui.Print("[AlphaChannel] Your normal account is already selected.");
            else if (!Cfg.SaveAccountCredentials())
                ChatGui.Print("[AlphaChannel] Could not save the current login. Account selection was not changed.");
            else
            {
                test.Requested = false;
                Cfg.Save();
                ChatGui.Print("[AlphaChannel] Reload Alpha Channel to restore your normal saved account.");
            }
            return;
        }
#endif
        if(args.StartsWith("view ",StringComparison.OrdinalIgnoreCase))
        {mainWindow.OpenQuickView(args[5..]);return;}
        if (args.Length == 0)
        {
            ToggleMainWindow();
            return;
        }

        ToggleMainWindow();
    }

    private void OnWatchPartyChatCommand(
    string command,
    string arguments)
    {
        var message =
            arguments.Trim();

        if (stream.Mode is not (StreamMode.Hosting or StreamMode.Viewing))
        {
            ChatGui.Print(
                "[AlphaChannel] You are not currently in a watch party.");
            return;
        }

        if (message.Length == 0)
        {
            ChatGui.Print(
                "Usage: /wp <message>");
            return;
        }

        _ = stream.SendChatAsync(
            message);
    }

    private static unsafe void SendChatCommand(string command)
    {
        var utf8 = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String.FromString(command);
        try
        {
            FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance()->ProcessChatBoxEntry(utf8);
        }
        finally
        {
            utf8->Dtor(true);
        }
    }

    internal static void ExecuteChatCommand(string command) => SendChatCommand(command);

    // Right-click a player -> Join Stream as AlphaChannel viewer (capsule + ScreenPainter). Works
    // whenever that player kept the name the first-connect prompt suggests by default (their real
    // character name) - same name-matching the manual "Host's name" field in the window already
    // relies on, this is just a shortcut that skips typing it. Both players need AlphaChannel.
    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.Target is not MenuTargetDefault { TargetName.Length: > 0 } target)
        {
            return;
        }

        // "Make it easier for people to find one another": resolves by the target's actual FFXIV
        // character identity (name+world), not a chosen name anyone has to know/type - if you can
        // see them, you can add them. Needs both a signed-in caller and a resolvable world (cross-
        // world/instanced targets don't always carry one, so world resolution remains guarded by
        // the try/catch below.
        if (mainWindow.CurrentSession is { } session)
        {
            var characterName = target.TargetName;
            string? world = null;
            try
            {
                world = target.TargetHomeWorld.IsValid ? target.TargetHomeWorld.Value.Name.ToString() : null;
            }
            catch (Exception)
            {
                AepLog.Warning("[Friends] couldn't resolve target world.");
            }

            if (world is { Length: > 0 })
            {
                args.AddMenuItem(new MenuItem
                {
                    Name = "Join Stream",
                    PrefixChar = 'A',
                    PrefixColor = 588,
                    OnClicked = clickedArgs =>
                    {
                        _ = Task.Run(async () =>
                        {
                            var result = await friendsClient.FindJoinableStreamByCharacterAsync(
                                session.Token, characterName, world);
                            if (result is null)
                            {
                                frameworkActions.Enqueue(() =>
                                    ChatGui.Print($"[AlphaChannel] {characterName} is not hosting a joinable Watch Party."));
                                return;
                            }

                            if (result.Kind == AlphaChannel.Contracts.RoomKind.Locked)
                            {
                                frameworkActions.Enqueue(() =>
                                    ChatGui.Print($"[AlphaChannel] {characterName}'s Watch Party requires a password. Join it from the Party Directory."));
                                return;
                            }

                            frameworkActions.Enqueue(() =>
                            {
                                queue.Clear();
                                mainWindow.OpenViewerAndJoin(
                                    result.AccountId,
                                    visibleHostName: result.DisplayName);
                            });
                        });
                    },
                });

                args.AddMenuItem(new MenuItem
                {
                    Name = "Add Alpha Channel Friend",
                    PrefixChar = 'A',
                    PrefixColor = 588,
                    OnClicked = clickedArgs =>
                    {
                        _ = Task.Run(async () =>
                        {
                            var outcome = await friendsClient.SendRequestByCharacterAsync(session.Token, characterName, world);
                            frameworkActions.Enqueue(() =>
                                mainWindow.HandleAddFriendByCharacterResult(outcome, characterName));
                        });
                    },
                });
            }
        }
    }

    private void ToggleMainWindow()
    {
        if (mainWindow.IsOpen)
        {
            mainWindow.CloseUi();
            return;
        }

        mainWindow.OpenUi();
    }

    private void ApplyDistanceTvBehaviour()
    {
        if (stream.Mode != StreamMode.Hosting && Cfg.DistanceTvBehaviour == TvDutyBehaviour.StopPlayback)
        {
            miniPlayerWindow.IsOpen = false;
            mainWindow.StopMediaForRestrictedContent();
            if (stream.Mode == StreamMode.Viewing)
            {
                dutyViewerPlaybackStopped = true;
                mainWindow.HideViewerTvForDuty();
                Interlocked.Exchange(ref pendingRemoteState, null);
                waitingForMedia = false;
                broadcastLedger.ResetViewer();
            }
        }
        else
        {
            miniPlayerWindow.IsOpen = true;
            if (stream.Mode == StreamMode.Viewing) mainWindow.DespawnViewerTv(stopPlayback: false);
            else screenController.Engine.DespawnScreen();
        }
    }

    private void ApplyScreenRangeDespawn()
    {
        // Finish the Halloween session before evaluating range, even when
        // the mapper UI has not drawn its end-of-show frame yet.
        mainWindow.UpdatePumpkinVideoPreview();

        var engine =
            screenController.Engine;

        var isViewing =
            stream.Mode ==
            StreamMode.Viewing;

        //
        // A viewer must have explicitly spawned their TV. Solo and hosting
        // modes use the engine's own active state.
        //
        if (!engine.IsActive ||
            (
                isViewing &&
                !mainWindow.ViewerTvEnabled
            ))
        {
            screenRangeDespawned =
                false;

            screenRangeWarningShown =
                false;

            return;
        }

        var localPlayer =
            ObjectTable.LocalPlayer;

        //
        // Zoning can temporarily remove LocalPlayer. Treat that as being
        // outside the allowed range.
        //
        if (localPlayer is null)
        {
            // Duty flags may arrive after the player disappears during zoning.
            // Logout has already taken the full-cleanup path.
            if (Condition[ConditionFlag.BetweenAreas] || Condition[ConditionFlag.BetweenAreas51]) return;
            if (!screenRangeDespawned)
            {
                if (isViewing)
                {
                    viewerTvNeedsLocationRecheckAfterZoning = true;
                    viewerTvLocationRecheckStartedUtc = DateTime.UtcNow;

                    

                    ApplyDistanceTvBehaviour();
                }
                else
                {

                    

                    ApplyDistanceTvBehaviour();
                }

                screenRangeDespawned =
                    true;
            }

            return;
        }

        // The local Halloween show spans the plaza. Keep its TV alive while
        // that exact effect session runs, without altering saved range settings.
        if (mainWindow.HalloweenOwnsTv)
        {
            screenRangeWarningShown = false;
            screenRangeDespawned = false;
            return;
        }

        var distance =
            Vector3.Distance(
                localPlayer.Position,
                engine.ScreenPosition);
        // Indoor presets cover the entire estate/apartment, including floors.
        if (WatchPartyTvLocation.IsResidentialInterior())
        {
            screenRangeWarningShown = false;
            screenRangeDespawned = false;
            return;
        }

        //
        // Warn once while approaching the cutoff.
        //
        if (distance >
                HostScreenWarnDistance &&
            distance <=
                HostScreenDespawnDistance &&
            !screenRangeWarningShown)
        {
            ChatGui.Print("[AlphaChannel] You're getting too far from the TV. Move closer or it will despawn.");

            screenRangeWarningShown =
                true;
        }

        //
        // Apply the hard cutoff once.
        //
        if (distance >
            HostScreenDespawnDistance)
        {
            if (!screenRangeDespawned)
            {
                if (isViewing)
                {

                    

                    ApplyDistanceTvBehaviour();
                }
                else
                {

                    

                    ApplyDistanceTvBehaviour();
                }

                screenRangeDespawned =
                    true;
            }

            return;
        }

        //
        // Reset the warning after returning to the safe area. The TV remains
        // despawned until manually restored; playback continues normally.
        //
        if (distance <=
            HostScreenWarnDistance)
        {
            screenRangeWarningShown =
                false;

            screenRangeDespawned =
                false;
        }
    }

    private void DrawPluginUi()
    {
        drawingPluginUi = true;
        try { DrawPluginUiCore(); }
        finally { drawingPluginUi = false; }
    }

    private void DrawPluginUiCore()
    {
        lock (frameworkLifetimeLock)
        {
            if (!startupComplete || Volatile.Read(ref disposeStarted) != 0)
                return;

            var pointerButtonDown = ImGui.IsMouseDown(ImGuiMouseButton.Left) ||
                ImGui.IsMouseDown(ImGuiMouseButton.Right) || ImGui.IsMouseDown(ImGuiMouseButton.Middle);
            if (GameWindowFocus.CanReceiveUiInput(pointerButtonDown))
            {
                mainWindow.ShowPendingVideoChooser();
                windowSystem.Draw();
                playbackHud.Draw();
                tvGizmoOverlay.Draw();
                mainWindow.DrawWorldBeacons();
#if DEBUG
#endif
            }
            else
            {
                DrawUiWithoutMouseInput();
            }
        }
    }

    private void DrawUiWithoutMouseInput()
    {
        // ImGui input is shared with Dalamud. Mask it only during our draw and
        // restore it even if drawing fails, leaving other plugins untouched.
        var io = ImGui.GetIO();
        var position = io.MousePos;
        var delta = io.MouseDelta;
        var wheel = io.MouseWheel;
        var horizontalWheel = io.MouseWheelH;
        Span<bool> down = stackalloc bool[io.MouseDown.Length];
        Span<bool> clicked = stackalloc bool[io.MouseClicked.Length];
        Span<bool> released = stackalloc bool[io.MouseReleased.Length];
        Span<bool> doubleClicked = stackalloc bool[io.MouseDoubleClicked.Length];
        io.MouseDown.CopyTo(down);
        io.MouseClicked.CopyTo(clicked);
        io.MouseReleased.CopyTo(released);
        io.MouseDoubleClicked.CopyTo(doubleClicked);
        try
        {
            io.MousePos = new System.Numerics.Vector2(-float.MaxValue);
            io.MouseDelta = System.Numerics.Vector2.Zero;
            io.MouseWheel = io.MouseWheelH = 0;
            io.MouseDown.Clear();
            io.MouseClicked.Clear();
            io.MouseReleased.Clear();
            io.MouseDoubleClicked.Clear();
            // Block inactive input without fading the whole UI; custom draw lists
            // and textures do not share ImGui's disabled alpha.
            using var inputMask = UiDisabled.InputMask();
            using var disabledAlpha = ImRaii.PushStyle(ImGuiStyleVar.DisabledAlpha, 1f);
            using var disabled = ImRaii.Disabled();
            windowSystem.Draw();
            playbackHud.Draw();
            tvGizmoOverlay.Draw();
            mainWindow.DrawWorldBeacons();
#if DEBUG
#endif
        }
        finally
        {
            io.MousePos = position;
            io.MouseDelta = delta;
            io.MouseWheel = wheel;
            io.MouseWheelH = horizontalWheel;
            down.CopyTo(io.MouseDown);
            clicked.CopyTo(io.MouseClicked);
            released.CopyTo(io.MouseReleased);
            doubleClicked.CopyTo(io.MouseDoubleClicked);
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        lock (frameworkLifetimeLock)
        {
            if (startupComplete && Volatile.Read(ref disposeStarted) == 0)
            {
                screenController.Engine.PreserveScreenOnPlaybackFailure = stream.Mode == StreamMode.Viewing && mainWindow.ViewerTvEnabled;
                screenController.Engine.BeginProgressFrame();
                UpdateFrameworkCore(framework);
                TickDiscordActivity();
                UpdateServerInfoBar();
            }
        }
    }

    private void UpdateFrameworkCore(IFramework framework)
    {
        if (Volatile.Read(ref disposeStarted) != 0)
            return;

        if (pendingUpdateNoticeVersion is { } updatedVersion && ClientState.IsLoggedIn)
        {
            pendingUpdateNoticeVersion = null;
            ChatGui.Print($"[Alpha Channel] Updated to version {updatedVersion}. Type /alpha to open the plugin.");
        }

        pluginUpdates.Tick(DateTime.UtcNow);
        mainWindow.TickPopularVideos();
        mainWindow.TickYouTubeAgeRestrictions();
        mainWindow.TickAutomaticBadges();
        mainWindow.TickAdminUsage();
        mainWindow.TickArcadeScores();

        while (frameworkActions.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception)
            {
                AepLog.Error("[Framework] Queued action failed.");
            }
        }

        // The TV Pictionary answer box reads text through ImGui, but ImGui focus alone does
        // not stop FFXIV from seeing the same physical keys. Give the answer box exclusive
        // keyboard ownership for as long as it is open, while leaving the mouse available
        // for the Submit and Cancel controls on the TV.
        if (ClientState.IsLoggedIn && (mainWindow.PictionaryTvGuessInputActive || mainWindow.BingoNumberInputActive))
            VideoEngine.SuppressAllFfxivKeyboardInput();

        // Logout and non-duty restricted activities still perform full cleanup.
        // Duty behaviour is configured separately and never interrupts a party host.
        screenController.Engine.RefreshLocalScreenPlacementContext();
        mainWindow.TickWorldMode();
        mainWindow.TickEvents();
        mainWindow.TickStatistics();
        StopPlaybackAfterMiniPlayerClosed();
        var restricted = !ClientState.IsLoggedIn ||
            Condition[ConditionFlag.ChocoboRacing] ||
            Condition[ConditionFlag.PlayingMiniGame] ||
            Condition[ConditionFlag.DutyRecorderPlayback];
        if (restricted)
        {
            mainWindow.EndHalloween();
            ClearHonorificStatus();
            broadcastLedger.UpdateHost(null, null, false);
            broadcastLedger.ResetViewer();
            var restrictedEngine = screenController.Engine;
            restrictedEngine.ResetScreenPresetSpawnNotice();
            var mediaStillActive = video.State != VideoPlaybackState.Idle ||
                queue.Current is not null || restrictedEngine.IsActive ||
                restrictedEngine.HasVideoPlaybackSession || restrictedEngine.HasActiveNonVideoPlayback;
            if (!restrictedContentActive || mediaStillActive)
            {
                restrictedContentActive = true;
                restrictedEngine.DutyScreen.Reset();
                stream.ReleaseDutyScreen();
                mainWindow.StopMediaForRestrictedContent();
                if (stream.Mode == StreamMode.Hosting)
                    _ = stream.PublishStateAsync(null, 0d, true, null, null, null, null, null);
                Interlocked.Exchange(ref pendingRemoteState, null);
                latestRemoteState = null;
                waitingForMedia = false;
                miniPlayerWindow.IsOpen = false;
                miniChatWindow.IsOpen = false;
                mainWindow.CloseUi();
                
            }
            StopYouTubeProviderIfIdle();
            return;
        }
        restrictedContentActive = false;

        var dutyEngine = screenController.Engine;
        var inDuty = Condition[ConditionFlag.BoundByDuty] ||
            Condition[ConditionFlag.BoundByDuty56] ||
            Condition[ConditionFlag.BoundByDuty95] ||
            Condition[ConditionFlag.ParticipatingInCustomMatch];
        var hasDutyContent = video.State != VideoPlaybackState.Idle || queue.Current is not null ||
            dutyEngine.HasVideoPlaybackSession || dutyEngine.HasActiveNonVideoPlayback;
        if (stream.Mode != StreamMode.Viewing || mainWindow.ViewerTvEnabled || MiniPlaybackActive)
            dutyViewerPlaybackStopped = false;
        var hostInDuty = inDuty && stream.Mode == StreamMode.Hosting;
        if (hostInDuty && !hostDutyActive) stream.PreserveDutyScreen();
        hostDutyActive = hostInDuty;
        if (stream.Mode == StreamMode.Hosting) dutyEngine.DutyScreen.Reset();
        if (dutyEngine.DutyScreen.Update(TvDutyBehaviourPolicy.Applies(inDuty, stream.Mode == StreamMode.Hosting), hasDutyContent))
        {
            viewerTvNeedsLocationRecheckAfterZoning = false;
            if (Cfg.InstancedContentTvBehaviour == TvDutyBehaviour.StopPlayback)
            {
                miniPlayerWindow.IsOpen = false;
                mainWindow.StopMediaForRestrictedContent();
                if (stream.Mode == StreamMode.Viewing)
                {
                    dutyViewerPlaybackStopped = true;
                    mainWindow.HideViewerTvForDuty();
                    Interlocked.Exchange(ref pendingRemoteState, null);
                    waitingForMedia = false;
                    broadcastLedger.ResetViewer();
                }
                
            }
            else
            {
                // Audio-only listeners already have an active playback consumer.
                if (!dutyEngine.AudioOnlyMode) miniPlayerWindow.IsOpen = true;
                if (stream.Mode == StreamMode.Viewing) mainWindow.HideViewerTvForDuty();
                dutyEngine.DespawnScreen();
                dutyEngine.SetBrowserControlsEnabled(false);
                dutyEngine.SetSnesControlsEnabled(false);
                dutyEngine.SetGameBoyControlsEnabled(false);
                if (!dutyEngine.AudioOnlyMode) miniPlayerWindow.ShowDutyNotice();
                
            }
        }

        ApplyPendingHostTransfers();
        UpdateTransferredHostSubtitles();
        mainWindow.TickHalloween();
        if (!mainWindow.HalloweenActivityActive) mainWindow.TickPartyQueue();
        mainWindow.TickPartyCountdown();

        TickAudioOnlyMode();
        screenController.Engine.SetFrameConsumersVisible(screenController.Engine.IsActive ||
            miniPlayerWindow.IsOpen || mainWindow.HasVisiblePluginWindow || stream.Mode == StreamMode.Hosting);
        screenController.OnFrameworkUpdate();
        queue.ApplyPendingMetadata();
        if (stream.Mode != StreamMode.Viewing && !stream.HostPlaybackAdoptionPending && !mainWindow.HalloweenActivityActive)
            queue.OnFrameworkUpdate();
        video.UpdateIdleScreensaver(miniPlayerWindow.IsOpen,
            miniPlayerWindow.IsOpen && stream.Mode == StreamMode.Viewing &&
            string.IsNullOrWhiteSpace(stream.CurrentRoomState?.Url));
        if (!mainWindow.HalloweenActivityActive) screenController.Engine.UpdateScreenPresetAutoApply(
            stream.Mode == StreamMode.Viewing && mainWindow.PartySyncTvPlacement);
        UpdateHonorificStatus();
        EnsureCharacterHasName();
        mainWindow.TickGuestAccounts();
        var contentId = ReadLocalContentId();
        var currentSession = Cfg.SessionForCharacter(contentId);
        mainWindow.CurrentSession = currentSession;
        mainWindow.TickCharacterDirectory();
        mainWindow.TickProfileThemes();
        TickCharacterMenus();
        mainWindow.TickAlphaChatSession();
        mainWindow.TickUsernameStyles();
        mainWindow.TickGuestPresence();
        mainWindow.TickArcadePresence();
        mainWindow.CurrentDisplayName = !string.IsNullOrWhiteSpace(currentSession?.DisplayName)
            ? currentSession.DisplayName
            : Cfg.CharacterDisplayNames.GetValueOrDefault(contentId);
        UpdateSessionValidation(contentId, mainWindow.CurrentSession);
        mainWindow.UpdateAutomaticStreamKey();
        mainWindow.RefreshPatreonEntitlementIfNeeded();
        mainWindow.ApplyPendingPatreonEntitlementChange();
        if (stream.TakeBroadcastAllowanceRefresh() && stream.Mode == StreamMode.Viewing)
            broadcastLedger.RetryViewer();
        mainWindow.RefreshPatreonBadgesIfNeeded();
        UpdateDmKeyRegistration(contentId, mainWindow.CurrentSession);
        mainWindow.CurrentCharacterName = ObjectTable.LocalPlayer?.Name.TextValue;
        mainWindow.CurrentWorldName = ReadLocalWorldName();
        mainWindow.CurrentCharacterHomeWorldName = ReadLocalHomeWorldName();
        mainWindow.RefreshHostedRoomWorldMetadata();
        mainWindow.UpdateLocalPartyAlerts();
        mainWindow.CurrentIsLalafell = ReadIsLalafell();
        video.SetViewerClassicBarsOverride(
            stream.Mode == StreamMode.Viewing && Cfg.PreferClassicBarsWhenViewing);

        if (mainWindow.ApplyPendingJoinQueueClear())
        {
            //
            // Joining may be confirmed just after the host's first state was
            // received. The local cleanup above stops normal video playback,
            // so forget every previous playback guard and replay only the
            // state that belongs to the newly joined room. This also allows the
            // TV spawn prompt to appear again for the new room.
            //
            lastReceivedRemoteUrl = null;
            lastAppliedRemoteUrl = null;
            waitingForMedia = true;
            loggedEmptyRemoteState = false;
            nextLiveHlsRetryUtc = DateTime.MinValue;
            liveHlsStartupUrl = null;
            liveHlsPlaybackAllowedUtc = DateTime.MinValue;
            latestRemoteState = stream.CurrentRoomState;
            liveDjRetry.Reset();
            Interlocked.Exchange(ref pendingRemoteState, stream.CurrentRoomState);
            
        }
        mainWindow.ApplyPendingRoomEndedReset();
        mainWindow.TickRoomRecovery();
        mainWindow.UpdateTvFrame();
        mainWindow.UpdateWatchPartyChat();

        if (stream.Mode == StreamMode.None && miniChatWindow.IsOpen)
        {
            miniChatWindow.IsOpen = false;
        }

        if (Interlocked.Exchange(ref pendingRemoteState, null) is { } remoteState)
        {
            ApplyRemoteState(remoteState);
        }
        if(stream.Mode!=StreamMode.Viewing)youtubeViewerRecovery.Reset();

        if (stream.Mode != StreamMode.Viewing || dutyEngine.DutyScreen.Hidden)
            viewerTvNeedsLocationRecheckAfterZoning = false;
        else if (viewerTvNeedsLocationRecheckAfterZoning &&
                 WatchPartyTvLocation.TryReadCurrent(out _) &&
                 mainWindow.RequestViewerTvPlacementRecheck(
                     DateTime.UtcNow - viewerTvLocationRecheckStartedUtc >=
                     TimeSpan.FromSeconds(2)))
            viewerTvNeedsLocationRecheckAfterZoning = false;

        if (stream.Mode == StreamMode.Viewing && !dutyViewerPlaybackStopped &&
            latestRemoteState is { Url: { } currentUrl } currentState &&
            IsLiveBroadcastUrl(currentUrl))
        {
            broadcastLedger.CanView(currentSession?.Token, stream.HostId);
            if (broadcastLedger.TakeViewerStateChanged() ||
                liveHlsProbeTask is { IsCompleted: true } ||
                (!liveHlsReady && liveHlsProbeTask is null &&
                 DateTime.UtcNow >= nextLiveHlsProbeUtc))
                ApplyRemoteState(currentState);
        }
        else
        {
            broadcastLedger.ResetViewer();
        }

        if (stream.Mode == StreamMode.Viewing && !dutyViewerPlaybackStopped &&
            latestRemoteState is { Url: { } djUrl } djState && LiveDjStream.AppliesTo(djUrl))
        {
            if (DateTime.UtcNow >= nextLiveDjUpdateUtc)
            {
                nextLiveDjUpdateUtc = DateTime.UtcNow.AddMilliseconds(250);
                ApplyRemoteState(djState);
            }
        }
        else
            liveDjRetry.Reset();

        if (mainWindow.ViewerTvEnabled &&
            waitingForMedia &&
            !screenController.Engine.IsActive)
        {
            video.ShowWaitingScreen();
        }

        ApplyTvInterruptionBehaviours();
        ApplyScreenRangeDespawn();
        miniPlayerWasOpen = miniPlayerWindow.IsOpen;

        UpdateRecentlyWatched();

        UpdateReactions();

        if (stream.Mode == StreamMode.Viewing && dutyViewerPlaybackStopped)
        {
            stream.IncomingWhiteboardCommands.Clear();
            stream.IncomingPictionaryCommands.Clear();
            stream.IncomingPictionaryTeamsCommands.Clear();
            stream.IncomingQuizCommands.Clear();
            stream.IncomingRouletteCommands.Clear();
            stream.IncomingChocoRaceCommands.Clear();
            stream.IncomingBingoCommands.Clear();
            stream.IncomingSkyRailCommands.Clear();
            stream.IncomingSlotMachineCommands.Clear();
            stream.IncomingLuckyWheelCommands.Clear();
            stream.IncomingPokerCommands.Clear();
        }
        else
        {
            mainWindow.UpdateWhiteboard();
            mainWindow.UpdatePictionary();
            mainWindow.UpdatePictionaryTeams();
            mainWindow.UpdateQuiz();
            mainWindow.UpdateRoulettePrototype();
            mainWindow.UpdateBingo();
            mainWindow.UpdateBlastParty();
            mainWindow.UpdateSkyRail();
            mainWindow.UpdateSlotMachine();
            mainWindow.UpdatePixels();
            mainWindow.UpdateLuckyWheel();
            mainWindow.UpdatePoker();
            mainWindow.UpdateViewerActivityPrompt();
            mainWindow.RefreshArcadeCanvasFrame();
            mainWindow.UpdatePokerPreview();
            mainWindow.UpdateRacePreview();
        }

        nearbyAutoWatch.OnFrameworkUpdate();

        //
        // Local playback remains local until the user explicitly creates
        // a Watch Party. Once hosting, publish the current media and current
        // timestamp exactly as before.
        //
        // Retro games use a separate armed broadcast URL. Their encoder is
        // demand-controlled and the room publishes a status URL while an
        // inactive broadcast is paused or its HLS stream is resuming.
        //
        var current =
            queue.Current;

        var engine =
            screenController.Engine;

        mainWindow.UpdateGameBroadcastDemand();
        mainWindow.UpdateLocalVideoBroadcastDemand();
        mainWindow.UpdateObsBitrateMeasurement();
        mainWindow.UpdateBroadcastStudio();
        mainWindow.UpdateSpotifyBroadcast();
        mainWindow.UpdateSpotifyAutomation();
        mainWindow.UpdateTwitchPopular();
        mainWindow.UpdateMusicTextVariables();
        current = queue.Current;
        mainWindow.UpdateBroadcastAllowance();
        broadcastLedger.UpdateHost(
            currentSession?.AccountId,
            currentSession?.Token,
            mainWindow.IsChargeableBroadcastActive());
        if (broadcastLedger.TakeHostAllowanceRefresh() && stream.Mode == StreamMode.Hosting)
            _ = NotifyBroadcastAllowanceRefreshedAsync();
        StopYouTubeProviderIfIdle();

        if (stream.Mode == StreamMode.Hosting)
        {
            if (!CompleteAdoptedHostPlayback()) return;
            if (mainWindow.ActiveWhiteboardUrl is { } whiteboardUrl)
            {
                _ = stream.PublishStateAsync(
                    whiteboardUrl,
                    0d,
                    false,
                    engine.IsActive ? engine.ScreenPosition : null,
                    engine.IsActive ? engine.ScreenYaw : null,
                    engine.IsActive ? engine.ScreenScale : null,
                    mainWindow.ActiveCanvasTitle,
                    null);
                return;
            }

            if (mainWindow.ActiveGameBroadcastStatusUrl is { } gameStatusUrl)
            {
                _ = stream.PublishStateAsync(
                    gameStatusUrl,
                    0d,
                    true,
                    engine.IsActive ? engine.ScreenPosition : null,
                    engine.IsActive ? engine.ScreenYaw : null,
                    engine.IsActive ? engine.ScreenScale : null,
                    engine.IsActive ? engine.DisableFixedScreenScaleRatio : null,
                    engine.IsActive ? engine.ScreenWidthScale : null,
                    engine.IsActive ? engine.ScreenHeightScale : null,
                    mainWindow.TryGetGameBroadcastStatus(gameStatusUrl, out var gameStatus) &&
                    gameStatus == "Broadcast paused"
                        ? "Paused for inactivity"
                        : "Broadcast resuming",
                    null);
                return;
            }

            var localVideoBroadcastUrl =
     mainWindow.ActiveLocalVideoBroadcastHlsUrl;

            var gameBroadcastUrl =
                mainWindow.ActiveGameBroadcastHlsUrl;

            var browserBroadcastUrl =
                mainWindow.ActiveBrowserBroadcastHlsUrl;

            var relayBroadcastUrl =
                !string.IsNullOrWhiteSpace(
                    localVideoBroadcastUrl)
                    ? localVideoBroadcastUrl
                    : !string.IsNullOrWhiteSpace(
                        browserBroadcastUrl)
                        ? browserBroadcastUrl
                        : gameBroadcastUrl;

            if (!string.IsNullOrWhiteSpace(
                    relayBroadcastUrl))
            {
                var isLocalVideoBroadcast =
                    !string.IsNullOrWhiteSpace(
                        localVideoBroadcastUrl);

                var isBrowserBroadcast =
                    !isLocalVideoBroadcast &&
                    !string.IsNullOrWhiteSpace(
                        browserBroadcastUrl);

                _ = stream.PublishStateAsync(
                    relayBroadcastUrl,
                    0d,
                    isLocalVideoBroadcast &&
                    mainWindow.ActiveLocalVideoBroadcastPaused,
                    engine.IsActive
                        ? engine.ScreenPosition
                        : null,
                    engine.IsActive
                        ? engine.ScreenYaw
                        : null,
 engine.IsActive
    ? engine.ScreenScale
    : null,
engine.IsActive
    ? engine.DisableFixedScreenScaleRatio
    : null,
engine.IsActive
    ? engine.ScreenWidthScale
    : null,
engine.IsActive
    ? engine.ScreenHeightScale
    : null,
                  isLocalVideoBroadcast
    ? mainWindow.ActiveLocalVideoBroadcastTitle ??
      "Local Video"
    : isBrowserBroadcast
    ? mainWindow.ActiveBrowserBroadcastTitle ??
      "Web Browsing"
    : mainWindow.ActiveGameBroadcastTitle ??
      "Gameplay",
null);
            }
            else if (mainWindow.IsLocalVideoBroadcastArmed || mainWindow.IsBrowserBroadcastArmed || mainWindow.IsStudioPreviewActive)
            {
                // An armed relay with no running publisher has no playable URL.
                // Keep the room open without leaking a local file or stale queue item.
                _ = stream.PublishStateAsync(null, 0d, true,
                    engine.IsActive ? engine.ScreenPosition : null,
                    engine.IsActive ? engine.ScreenYaw : null,
                    engine.IsActive ? engine.ScreenScale : null,
                    null, null);
            }
            else
            {
                var (position, _, paused) =
                    current is null
                        ? (0d, 0d, true)
                        : video.GetProgress();

                //
                // Direct URL entries use the URL as their temporary
                // title until AetherStreamQueue finishes resolving
                // metadata. Do not expose that placeholder as a title.
                //
                var mediaTitle =
                    mainWindow.IsGameBroadcastArmed
                        ? mainWindow.ActiveGameBroadcastTitle
                        : current is not null &&
                    !string.IsNullOrWhiteSpace(
                        current.Title) &&
                    !string.Equals(
                        current.Title,
                        current.Url,
                        StringComparison.OrdinalIgnoreCase)
                        ? current.Title
                        : null;

                var publishedUrl =
    current?.Url;

                if (!string.IsNullOrWhiteSpace(
                        publishedUrl) &&
                    engine.IsAudioOnly)
                {
                    publishedUrl =
                        mainWindow.BuildPartyAudioPresentationUrl(publishedUrl);
                    mediaTitle = WatchPartyMediaDisplay.Title(publishedUrl, mediaTitle, audioOnly: true);
                    if (current is not null && VideoPlayer.IsNonSeekableLiveSource(current.Source))
                        publishedUrl += publishedUrl.Contains('#') ? "&ac_live=1" : "#ac_live=1";
                }

                _ = stream.PublishStateAsync(
                    publishedUrl,
                    position,
                                    paused,
                    engine.IsActive
                        ? engine.ScreenPosition
                        : null,
                    engine.IsActive
                        ? engine.ScreenYaw
                        : null,
                    engine.IsActive
                        ? engine.ScreenScale
                        : null,
                    engine.IsActive ? engine.DisableFixedScreenScaleRatio : null,
                    engine.IsActive ? engine.ScreenWidthScale : null,
                    engine.IsActive ? engine.ScreenHeightScale : null,
                    mediaTitle,
                    current?.ThumbnailUrl);

                if (current is not null)
                {
                    video.SetOverlayTitle(
                        current.Title,
                        current.Source);
                }
            }
        }
    }

    internal void UpdateRecentlyWatched()
    {
        UpdateRecommendedTopicPlayCount();

        if (queue.Current is not { } current ||
            current.IsTransient)
        {
            return;
        }

        var now = ImGui.GetTime();

        if (now - lastRecentlyWatchedSave < 15)
        {
            return;
        }

        lastRecentlyWatchedSave = now;

        var (position, duration, _) = video.GetProgress();

        mainWindow.UpdateRecentlyWatched(
            current,
            position,
            duration);
    }

    private void UpdateRecommendedTopicPlayCount()
    {
        if (video.State == VideoPlaybackState.Paused)
        {
            lastRecommendedPlaybackAttemptId = -1;
            return;
        }

        var attemptId = video.PlaybackAttemptId;
        if (attemptId == lastRecommendedPlaybackAttemptId ||
            video.State != VideoPlaybackState.Playing ||
            !video.HasVideoPlaybackSession ||
            string.IsNullOrWhiteSpace(video.CurrentMediaUrl) ||
            !VideoUrlResolver.IsYouTubeUrl(video.CurrentMediaUrl))
            return;

        var (position, duration, _) = video.GetProgress();
        if (duration <= 0 || position <= 0 || video.State != VideoPlaybackState.Playing)
            return;

        lastRecommendedPlaybackAttemptId = attemptId;
        mainWindow.RecordRecommendedTopicPlay(video.CurrentMediaUrl);
    }

    private bool interruptionStopActive;

    // Stop once when an interruption begins. Resuming is an explicit user action;
    // viewers stay in their party and adopt the host's current position on resume.
    private void ApplyTvInterruptionBehaviours()
    {
        var cutscene = Condition[ConditionFlag.WatchingCutscene] || Condition[ConditionFlag.WatchingCutscene78] ||
            Condition[ConditionFlag.OccupiedInCutSceneEvent];
        var combat = Condition[ConditionFlag.InCombat];
        var a = cutscene ? Cfg.CutsceneTvBehaviour : TvInterruptionBehaviour.NoEffect;
        var b = combat ? Cfg.CombatTvBehaviour : TvInterruptionBehaviour.NoEffect;
        var stop = a == TvInterruptionBehaviour.StopPlayback || b == TvInterruptionBehaviour.StopPlayback;
        if (stop && !interruptionStopActive)
        {
            miniPlayerWindow.IsOpen = false;
            mainWindow.StopMediaForRestrictedContent("Pictionary ended because playback was stopped.");
            if (stream.Mode == StreamMode.Viewing)
            {
                dutyViewerPlaybackStopped = true;
                mainWindow.HideViewerTvForDuty();
                Interlocked.Exchange(ref pendingRemoteState, null);
                waitingForMedia = false;
                broadcastLedger.ResetViewer();
            }
        }
        interruptionStopActive = stop;
        screenController.Engine.SetBehaviourVolumeScale(
            a == TvInterruptionBehaviour.Mute ? 0f :
            a == TvInterruptionBehaviour.LowerVolume ? 0.3f :
            b == TvInterruptionBehaviour.LowerVolume ? 0.7f : 1f);
    }

    // Drains stream.IncomingReactions (the sole consumer - MainWindow's reaction buttons only
    // send, they don't also drain, since a ConcurrentQueue only lets one consumer actually get
    // each item) and pushes the current animated particle set to the in-world screen every tick.
    // Spawns near the bottom of the screen (uv.y close to 1, just above the title banner's own
    // band) and rises toward the top over ReactionLifetime, matching the GUI's earlier "fly up"
    // behavior but rendered on the actual video screen instead.
    private async Task NotifyBroadcastAllowanceRefreshedAsync()
    {
        try
        {
            await stream.SendReactionAsync(StreamClient.BroadcastAllowanceRefreshCommand,
                null, null, null, false).ConfigureAwait(false);
        }
        catch (Exception)
        {
            AepLog.Warning("[BroadcastAllowance] Could not notify viewers of refreshed allowance.");
        }
    }

    private void UpdateReactions()
    {
        // Whiteboard commands are dispatched into IncomingWhiteboardCommands
        // before ordinary reactions reach this queue. Per-user reaction
        // visibility therefore never suppresses team whiteboard strokes.
        while (stream.IncomingReactions.TryDequeue(
                   out var incomingReaction))
        {
            var visibility =
                Cfg.PartyReactionVisibilityByUserId.TryGetValue(
                    incomingReaction.UserId,
                    out var savedVisibility)
                    ? savedVisibility
                    : PartyReactionVisibility.ShowAll;

            var showOnTv = visibility is
                PartyReactionVisibility.ShowAll or
                PartyReactionVisibility.TvOnly;
            var showInChat = visibility is
                PartyReactionVisibility.ShowAll or
                PartyReactionVisibility.ChatOnly;

            if (showOnTv)
            {
                float spawnX;
                if (TryResolveReactionSpawnX(
                        incomingReaction.CharacterName,
                        incomingReaction.WorldName,
                        out var characterSpawnX))
                {
                    var jitter =
                        (float)(
                            reactionRandom.NextDouble() *
                            CharacterReactionHorizontalJitter *
                            2d -
                            CharacterReactionHorizontalJitter);

                    spawnX = Math.Clamp(
                        characterSpawnX + jitter,
                        0.05f,
                        0.95f);
                }
                else
                {
                    spawnX =
                        0.50f +
                        (float)(
                            reactionRandom.NextDouble() *
                            0.84 -
                            0.42);
                }

                activeReactions.Add(
                    new InWorldReaction(
                        DateTime.UtcNow,
                        spawnX,
                        ReactionGlyphs.FromGlyph(
                            incomingReaction.Glyph),
                        incomingReaction.CharacterName,
                        incomingReaction.WorldName));
            }

// Mirror the same received reaction into the
// chronological Watch Party activity feed.
//
// Preserve the authenticated Alpha Channel account ID
// so the feed can resolve the sender's avatar locally.
var reactionDisplayName =
    stream.Mode == StreamMode.Viewing &&
    string.Equals(
        incomingReaction.UserId,
        stream.HostId,
        StringComparison.Ordinal) &&
    string.Equals(
        incomingReaction.AccountDisplayName,
        "Someone",
        StringComparison.Ordinal) &&
    mainWindow.JoinedHostDisplayName is { Length: > 0 } hostDisplayName
        ? hostDisplayName
        : incomingReaction.AccountDisplayName;

if (showInChat && incomingReaction.ShowInChat)
{
    mainWindow.AddPartyReactionToFeed(
        incomingReaction.UserId,
        reactionDisplayName,
        incomingReaction.Glyph);
}
        }

        activeReactions.RemoveAll(
            reaction =>
                DateTime.UtcNow -
                reaction.SpawnedAt >=
                ReactionLifetime);

        var particles =
            new List<ReactionParticle>(
                activeReactions.Count);

        var now =
            DateTime.UtcNow;

        foreach (var reaction in activeReactions)
        {
            var progress =
                Math.Clamp(
                    (float)(
                        now -
                        reaction.SpawnedAt)
                    .TotalSeconds /
                    (float)
                    ReactionLifetime.TotalSeconds,
                    0f,
                    1f);

            var x =
                Math.Clamp(
                    reaction.SpawnX +
                    MathF.Sin(progress * MathF.PI) *
                    0.018f,
                    0.05f,
                    0.95f);

            var y =
                0.85f -
                progress *
                0.7f;

            // Keep the badge clear for most of its flight, then fade it quickly near the end.
            var alpha =
                progress < 0.80f
                    ? 1f
                    : 1f -
                      ((progress - 0.80f) /
                       0.20f);

            var elapsedSeconds =
                (float)(now - reaction.SpawnedAt).TotalSeconds;

            // Quick overshooting pop on arrival, then settle at the normal badge size.
            var popScale =
                elapsedSeconds < 0.12f
                    ? 0.70f +
                      (elapsedSeconds / 0.12f) *
                      0.45f
                    : elapsedSeconds < 0.24f
                        ? 1.15f -
                          ((elapsedSeconds - 0.12f) /
                           0.12f) *
                          0.15f
                        : 1f;

            var color =
                ReactionBadgeColor(
                    reaction.Glyph);

            particles.Add(
                new ReactionParticle(
                    x,
                    y,
                    alpha,
                    0.047f * popScale,
                    color.R,
                    color.G,
                    color.B,
                    reaction.Glyph));
        }

        video.SetReactions(
            particles);
    }

    private readonly Random reactionRandom = new();

    private bool TryResolveReactionSpawnX(
        string? characterName,
        string? homeWorldName,
        out float spawnX)
    {
        spawnX = 0.5f;
        if (string.IsNullOrWhiteSpace(characterName) ||
            string.IsNullOrWhiteSpace(homeWorldName))
        {
            return false;
        }

        foreach (var gameObject in ObjectTable)
        {
            if (gameObject is not IPlayerCharacter character ||
                !string.Equals(
                    character.Name.TextValue,
                    characterName.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? characterHomeWorld;
            try
            {
                characterHomeWorld =
                    character.HomeWorld.Value.Name.ToString();
            }
            catch (Exception)
            {
                AepLog.Warning(
                    "[Reactions] Could not resolve [redacted]'s home world.");
                continue;
            }

            if (!string.Equals(
                    characterHomeWorld,
                    homeWorldName.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return video.TryGetReactionSpawnX(
                character.Position,
                out spawnX);
        }

        return false;
    }

    private void StopYouTubeProviderIfIdle()
    {
        if (mainWindow.HalloweenActivityActive) { youtubeProviderIdleSinceUtc = null; return; }
        var now = DateTime.UtcNow;
        if (now < nextYouTubeProviderIdleCheckUtc ||
            youtubeProviderStopTask is { IsCompleted: false })
            return;
        nextYouTubeProviderIdleCheckUtc = now.AddSeconds(5);
        var engine = screenController.Engine;
        if (!engine.IsYouTubeProviderRunning)
        {
            youtubeProviderIdleSinceUtc = null;
            return;
        }
        if (!restrictedContentActive &&
            (VideoUrlResolver.IsYouTubeUrl(video.CurrentMediaUrl ?? string.Empty) ||
             VideoUrlResolver.IsYouTubeUrl(queue.Current?.Url ?? string.Empty) ||
             stream.Mode == StreamMode.Viewing &&
             VideoUrlResolver.IsYouTubeUrl(latestRemoteState?.Url ?? string.Empty)))
        {
            youtubeProviderIdleSinceUtc = null;
            return;
        }

        // Give an in-flight video handoff time to claim the provider.
        youtubeProviderIdleSinceUtc ??= now;
        if (!restrictedContentActive &&
            now - youtubeProviderIdleSinceUtc.Value < TimeSpan.FromSeconds(5))
            return;

        youtubeProviderStopTask = StopYouTubeProviderSafelyAsync();
        youtubeProviderIdleSinceUtc = null;
    }

    private async Task StopYouTubeProviderSafelyAsync()
    {
        try
        {
            await screenController.Engine.StopYouTubeProviderAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            {}
        }
    }

    private void SubscribeHonorificTitleChanges()
    {
        try
        {
            honorificTitleChanged = PluginInterface.GetIpcSubscriber<string, object>("Honorific.LocalCharacterTitleChanged");
            honorificTitleChanged.Subscribe(OnHonorificTitleChanged);
        }
        catch (Exception)
        {
            // Reading the effective title once a second remains a fallback.
            
        }
    }

    private void OnHonorificTitleChanged(string titleJson)
    {
        if (Volatile.Read(ref disposeStarted) != 0) return;
        var generation = Volatile.Read(ref honorificObservationGeneration);
        var changedUtc = DateTime.UtcNow;
        frameworkActions.Enqueue(() =>
        {
            if (Volatile.Read(ref disposeStarted) != 0 || generation != honorificObservationGeneration ||
                !honorificOverrideApplied || !Cfg.DisplayAlphaChannelStatusInHonorific ||
                ObjectTable.LocalPlayer?.EntityId != lastHonorificEntityId) return;
            honorificGrace.Notify(titleJson, changedUtc);
        });
    }

    private void ResetHonorificTracking()
    {
        lastHonorificStatus = null;
        lastAppliedHonorificTitleJson = null;
        lastHonorificColorPreset = -1;
        lastHonorificEntityId = 0;
        honorificOverrideApplied = false;
        honorificGrace.Reset();
        nextHonorificTitleReadUtc = DateTime.MinValue;
        nextHonorificIpcAttemptUtc = DateTime.MinValue;
        Interlocked.Increment(ref honorificObservationGeneration);
    }

    private void UpdateHonorificStatus()
    {
        if (!Cfg.DisplayAlphaChannelStatusInHonorific)
        {
            ClearHonorificStatus();
            return;
        }

        var player = ObjectTable.LocalPlayer;
        if (player is null)
        {
            if (lastHonorificEntityId != 0) ResetHonorificTracking();
            return;
        }
        if (lastHonorificEntityId != 0 && lastHonorificEntityId != player.EntityId)
            ResetHonorificTracking();

        var status = GetHonorificStatus();
        if (status is null)
        {
            ClearHonorificStatus();
            return;
        }
        if (status.Length > 32) status = "Live on Alpha Channel";
        var colorPreset = Math.Clamp(Cfg.HonorificStatusColorPreset, 0, 5);
        var statusChanged = !string.Equals(status, lastHonorificStatus, StringComparison.Ordinal) ||
            colorPreset != lastHonorificColorPreset || lastHonorificEntityId != player.EntityId;
        var now = DateTime.UtcNow;
        if (now < nextHonorificIpcAttemptUtc ||
            now < nextHonorificTitleReadUtc && (!statusChanged || honorificGrace.Waiting(now)) &&
            !honorificGrace.RestorationDue(now)) return;

        try
        {
            var received = PluginInterface.GetIpcSubscriber<int, string>("Honorific.GetCharacterTitle")
                .InvokeFunc((int)player.ObjectIndex);
            nextHonorificTitleReadUtc = now.AddSeconds(1);
            honorificGrace.Observe(received, now);
            if (honorificGrace.Waiting(now)) return;

            var (textColor, glowColor) = GetHonorificColors(colorPreset);
            if (HonorificTitlePayload.Matches(received, status, textColor, glowColor))
            {
                RecordHonorificStatus(received, status, colorPreset, player.EntityId);
                return;
            }

            var titleJson = HonorificTitlePayload.Create(status, textColor, glowColor);
            PluginInterface.GetIpcSubscriber<int, string, object>("Honorific.SetCharacterTitle")
                .InvokeAction((int)player.ObjectIndex, titleJson);
            // Track the request, but only retain a cleanup ownership payload
            // after verifying that the receiver actually applied it.
            honorificOverrideApplied = true;
            lastAppliedHonorificTitleJson = null;
            lastHonorificStatus = status;
            lastHonorificColorPreset = colorPreset;
            lastHonorificEntityId = player.EntityId;
            received = PluginInterface.GetIpcSubscriber<int, string>("Honorific.GetCharacterTitle")
                .InvokeFunc((int)player.ObjectIndex);
            if (!HonorificTitlePayload.Matches(received, status, textColor, glowColor))
                throw new InvalidOperationException("Honorific did not retain the requested title colours.");
            RecordHonorificStatus(received, status, colorPreset, player.EntityId);
        }
        catch (Exception)
        {
            // Honorific may be absent or loading; retry quietly without flooding the log.
            nextHonorificIpcAttemptUtc = now.AddSeconds(10);
            
        }
    }

    private void RecordHonorificStatus(string received, string status, int colorPreset, uint entityId)
    {
        lastAppliedHonorificTitleJson = received;
        lastHonorificStatus = status;
        lastHonorificColorPreset = colorPreset;
        lastHonorificEntityId = entityId;
        honorificOverrideApplied = true;
        honorificGrace.Applied(received);
        nextHonorificIpcAttemptUtc = DateTime.MinValue;
    }

    private static (string Text, string Glow) GetHonorificColors(int preset) =>
        preset switch
        {
            1 => ("#CD2F2F", "#4B1616"),
            2 => ("#CD9C2F", "#4B4216"),
            3 => ("#57CD2F", "#264B16"),
            4 => ("#2FCDB6", "#164B42"),
            5 => ("#2F39CD", "#161C4B"),
            _ => ("#A92FCD", "#4B1642"),
        };

    private string? GetHonorificStatus()
    {
        var engine = screenController.Engine;
        if(stream.Mode==StreamMode.Viewing)
        {
            // Viewer games can run in their own page without a spawned TV or
            // local emulator. Resolve participation before the renderer's state.
            var remoteUrl=stream.CurrentRoomState?.Url;
            if(string.IsNullOrWhiteSpace(remoteUrl))return "In a Watch Party";
            if(mainWindow.IsArcadeGameActive)
                return mainWindow.IsStandaloneCanvasActivityActive
                    ? "Drawing on Alpha Channel":"Playing on Alpha Channel";
            if(engine.IsPlayingGame)return "Playing on Alpha Channel";
            if(engine.IsAudioOnly||LiveDjStream.AppliesTo(remoteUrl)||AudioVisualizerSelection.HasAudioVisualizerMetadata(remoteUrl))
                return "Listening on Alpha Channel";
            return "Watching on Alpha Channel";
        }
        if (engine.IsPlayingSnes)
            return "Playing SNES on Alpha Channel";
        if (engine.IsPlayingNes)
            return "Playing NES on Alpha Channel";
        if (engine.IsPlayingGameBoyAdvance)
            return "Playing GBA on Alpha Channel";
        if (engine.IsPlayingGameBoy)
            return "Playing Game Boy on Alpha Channel";
        if (engine.IsPlayingMasterSystem)
            return "Playing Master System on Alpha Channel";
        if (engine.IsPlayingGameGear)
            return "Playing Game Gear on Alpha Channel";
        if (engine.IsPlayingWhiteboard)
            return mainWindow.IsStandaloneCanvasActivityActive
                ? "Drawing on Alpha Channel"
                : "Playing on Alpha Channel";
        if (engine.IsPlayingBrowser)
            return "Browsing on Alpha Channel";
        if (engine.IsPlayingImage)
            return engine.CurrentImageSelection?.Mode == ImageMediaMode.Slideshow
                ? "Watching a slideshow on Alpha Channel"
                : "Viewing an image on Alpha Channel";

        // Pausing keeps the media category, so Honorific receives no title
        // change when the same video/audio is paused and resumed.
        if (video.State is VideoPlaybackState.Playing or VideoPlaybackState.Loading or VideoPlaybackState.Paused)
        {
            if (engine.IsPlayingLocalVideo)
                return "Watching local video on Alpha Channel";
            if (engine.IsAudioOnly)
                return mainWindow.IsDjActivityActive
                    ? "DJing on Alpha Channel"
                    : "Listening on Alpha Channel";
            return "Watching on Alpha Channel";
        }

        return stream.Mode switch
        {
            StreamMode.Hosting => "Hosting on Alpha Channel",
            _ => null,
        };
    }

    private void ClearHonorificStatus()
    {
        if (!honorificOverrideApplied) return;
        try
        {
            var player = ClientState.IsLoggedIn ? ObjectTable.LocalPlayer : null;
            if (player is not null && player.EntityId == lastHonorificEntityId && lastHonorificStatus is { } status)
            {
                var received = PluginInterface.GetIpcSubscriber<int, string>("Honorific.GetCharacterTitle")
                    .InvokeFunc((int)player.ObjectIndex);
                var (textColor, glowColor) = GetHonorificColors(lastHonorificColorPreset);
                // Another plugin may currently own the single IPC title slot.
                if (lastAppliedHonorificTitleJson is { } owned && HonorificTitlePayload.Equivalent(received, owned) &&
                    HonorificTitlePayload.Matches(received, status, textColor, glowColor))
                    PluginInterface.GetIpcSubscriber<int, object>("Honorific.ClearCharacterTitle")
                        .InvokeAction((int)player.ObjectIndex);
            }
        }
        catch (Exception)
        {
            AepLog.Warning("[Honorific] Could not clear the Alpha Channel status.");
        }
        finally { ResetHonorificTracking(); }
    }

    private static (float R, float G, float B) ReactionBadgeColor(
        ReactionGlyph glyph) => glyph switch
        {
            ReactionGlyph.Like => (0.18f, 0.45f, 1.00f),
            ReactionGlyph.Laugh => (1.00f, 0.72f, 0.16f),
            ReactionGlyph.Love => (1.00f, 0.20f, 0.42f),
            ReactionGlyph.Surprised => (0.12f, 0.76f, 0.96f),
            ReactionGlyph.Hype => (0.56f, 0.20f, 0.96f),
            ReactionGlyph.Fire => (1.00f, 0.31f, 0.06f),
            ReactionGlyph.Dislike => (0.80f, 0.18f, 0.27f),
            ReactionGlyph.Angry => (0.96f, 0.10f, 0.08f),
            ReactionGlyph.Crying => (0.16f, 0.55f, 1.00f),
            ReactionGlyph.Dead => (0.42f, 0.48f, 0.58f),
            ReactionGlyph.MindBlown => (0.96f, 0.39f, 0.08f),
            ReactionGlyph.RocketHype => (0.08f, 0.78f, 0.70f),
            ReactionGlyph.Victory => (0.96f, 0.69f, 0.10f),
            ReactionGlyph.Spooky => (0.56f, 0.39f, 0.84f),
            ReactionGlyph.Royal => (0.88f, 0.53f, 0.07f),
            ReactionGlyph.MusicMoment => (0.86f, 0.24f, 0.78f),
            ReactionGlyph.Warning => (1.00f, 0.59f, 0.06f),
            ReactionGlyph.MaximumHype => (0.92f, 0.82f, 0.08f),
            ReactionGlyph.Fabulous => (0.96f, 0.21f, 0.70f),
            ReactionGlyph.Embarrassed => (0.97f, 0.43f, 0.53f),
            ReactionGlyph.Awkward => (0.74f, 0.55f, 0.17f),
            ReactionGlyph.Dizzy => (0.45f, 0.30f, 0.84f),
            ReactionGlyph.Watching => (0.10f, 0.67f, 0.79f),
            ReactionGlyph.Amazing => (0.08f, 0.81f, 0.95f),
            ReactionGlyph.Exhausted => (0.31f, 0.43f, 0.66f),
            ReactionGlyph.Cheeky => (0.55f, 0.76f, 0.12f),
            ReactionGlyph.Celebration => (0.95f, 0.34f, 0.57f),
            ReactionGlyph.Powerful => (0.77f, 0.18f, 0.12f),
            ReactionGlyph.FoodMoment => (0.96f, 0.32f, 0.10f),
            ReactionGlyph.Salute => (0.12f, 0.70f, 0.38f),
            _ => (0.55f, 0.60f, 1.00f),
        };

    private readonly record struct InWorldReaction(
        DateTime SpawnedAt,
        float SpawnX,
        ReactionGlyph Glyph,
        string? CharacterName,
        string? WorldName);

    // Runs every tick (cheap dictionary lookup) rather than once at startup because LocalContentId
    // is 0 until the player is actually logged into a character - a dev plugin can load at the
    // title screen, well before that's known.
    private void EnsureCharacterHasName()
    {
        var contentId =
            ReadLocalContentId();

        // The welcome flow now chooses guest or account identity before asking
        // for a name. Signed-in characters use their account name instead.
        if (            !Cfg.HasCompletedFirstLaunch ||
            Cfg.CharacterSessions.ContainsKey(contentId))
            return;

        if (contentId == 0 ||
            mainWindow.IsNamePromptActive)
        {
            return;
        }

        if (Cfg.CharacterDisplayNames.ContainsKey(
                contentId))
        {
            return;
        }

        //
        // Don't force the whole plugin UI open just because the character
        // still needs a username. Wait until they open Alpha Channel.
        //
        if (!mainWindow.IsOpen)
        {
            return;
        }

        PromptForName(
            contentId,
            ObjectTable.LocalPlayer?
                .Name.TextValue ??
            "Player");
    }

    // Manually triggered from MainWindow's "Rename" button - same flow as the automatic
    // first-connect prompt above, just invocable any time instead of only once per character.
    private void RequestRename()
    {
        var contentId = ReadLocalContentId();
        if (contentId == 0 || mainWindow.IsNamePromptActive)
        {
            return;
        }

        var suggested = Cfg.CharacterDisplayNames.GetValueOrDefault(contentId) ??
            ObjectTable.LocalPlayer?.Name.TextValue ?? "Player";
        PromptForName(contentId, suggested);
    }

    private void PromptForName(ulong contentId, string suggested)
    {
        mainWindow.RequestNamePrompt(suggested, name =>
        {
            Cfg.CharacterDisplayNames[contentId] = name;
            Cfg.Save();
            _ = stream.SendHelloAsync(name);
        });
    }

    private void SaveLocalDisplayName(string name)
    {
        var contentId = ReadLocalContentId();
        if (contentId == 0) return;
        Cfg.CharacterDisplayNames[contentId] = name;
        Cfg.Save();
        _ = stream.SendHelloAsync(name);
    }

    // An admin cleared this player's name server-side (see AlphaChannel.Server's
    // /admin/reset-username) - drop the local record too so EnsureCharacterHasName re-prompts them
    // on the very next tick, same code path as the first-connect flow.
    private void OnRenameRequired()
    {
        var contentId = ReadLocalContentId();
        if (contentId != 0)
        {
            Cfg.CharacterDisplayNames.Remove(contentId);
            Cfg.Save();
        }
    }

    // stream.OnState fires from StreamClient's WebSocket receive loop - a background thread, not
    // the game's main thread. video.Play and the screen transform both touch main-thread-only game
    // state (this is exactly what threw "Not on main thread!" when applied here directly), so this
    // just records the latest message and OnFrameworkUpdate applies it on the next tick instead.
    private void OnRemoteState(AlphaChannel.Contracts.StreamControl message)
    {
        if (message.Url == lastReceivedRemoteUrl)
        {
            Interlocked.Exchange(ref pendingRemoteState, message);
            return;
        }

        lastReceivedRemoteUrl = message.Url;
        Interlocked.Exchange(ref pendingRemoteState, message);
    }

    private void SpawnViewerTv()
    {
        if (!screenController.Engine.DutyScreen.Place()) return;
        if (stream.Mode != StreamMode.Viewing)
        {
            return;
        }

        var state = latestRemoteState;

        if (state is null || string.IsNullOrEmpty(state.Url))
        {
            waitingForMedia = true;
            video.ShowWaitingScreen();
            
            return;
        }

        ApplyRemoteState(state);
        screenController.Engine.RespawnScreen(recenter: false);
        
    }

    private void UpdateDmKeyRegistration(ulong contentId, CharacterSession? session)
    {
        if (session is null || contentId == 0)
        {
            keyRegistrationAccountId = null;
            keyRegistrationToken = null;
            keyRegistrationInProgress = false;
            keyRegistrationVerified = false;
            return;
        }

        if (keyRegistrationAccountId != session.AccountId || keyRegistrationToken != session.Token)
        {
            keyRegistrationAccountId = session.AccountId;
            keyRegistrationToken = session.Token;
            keyRegistrationInProgress = false;
            keyRegistrationVerified = false;
            nextKeyRegistrationAttemptUtc = DateTime.MinValue;
        }

        if (validatedSessionContentId != contentId || keyRegistrationVerified ||
            keyRegistrationInProgress || DateTime.UtcNow < nextKeyRegistrationAttemptUtc)
            return;

        keyRegistrationInProgress = true;
        var accountId = session.AccountId;
        var token = session.Token;
        _ = Task.Run(async () =>
        {
            var verified = false;
            try
            {
                verified = await keyVault.EnsureIdentityRegisteredAsync(accountId, token, forceCheck: true)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                AepLog.Warning("[Messages] Public key registration failed.");
            }

            frameworkActions.Enqueue(() =>
            {
                if (ReadLocalContentId() != contentId ||
                    keyRegistrationAccountId != accountId || keyRegistrationToken != token)
                    return;

                keyRegistrationInProgress = false;
                keyRegistrationVerified = verified;
                if (!verified)
                    nextKeyRegistrationAttemptUtc = DateTime.UtcNow.AddMinutes(5);
            });
        });
    }

    private void UpdateSessionValidation(
        ulong contentId,
        CharacterSession? session)
    {
        if (contentId != observedSessionContentId)
        {
            observedSessionContentId = contentId;
            validatedSessionContentId = 0;
            validatingSessionContentId = 0;
            nextSessionValidationAttemptUtc = DateTime.MinValue;
            mainWindow.SessionValidationInProgress = false;
        }

        if (contentId == 0)
        {
            return;
        }

        if (session is null)
        {
            validatedSessionContentId = contentId;
            mainWindow.SessionValidationInProgress = false;
            return;
        }

        // A structurally incomplete session cannot authenticate and is safe to
        // remove locally without making a request. The encrypted vault itself
        // is left intact if loading it failed; Configuration never supplies a
        // partially loaded vault in that case.
        if (string.IsNullOrWhiteSpace(session.Token) ||
            string.IsNullOrWhiteSpace(session.AccountId))
        {
            Cfg.CharacterSessions.Remove(contentId);
            Cfg.Save();
            validatedSessionContentId = contentId;
            mainWindow.SessionValidationInProgress = false;
            return;
        }

        if (validatedSessionContentId == contentId ||
            validatingSessionContentId == contentId ||
            DateTime.UtcNow < nextSessionValidationAttemptUtc)
        {
            return;
        }

        validatingSessionContentId = contentId;
        mainWindow.SessionValidationInProgress = true;
        var token = session.Token;

        sessionValidationTask = Task.Run(async () =>
        {
            var result = await authClient
                .ValidateSessionAsync(token, sessionValidationLifetime.Token)
                .ConfigureAwait(false);

            frameworkActions.Enqueue(() =>
            {
                if (ReadLocalContentId() != contentId)
                {
                    return;
                }

                validatingSessionContentId = 0;
                mainWindow.SessionValidationInProgress = false;

                if (result.Account is { } account)
                {
                    if (!Cfg.CharacterSessions.TryGetValue(contentId, out var current) ||
                        !string.Equals(current.Token, token, StringComparison.Ordinal))
                    {
                        return;
                    }

                    mainWindow.NotifySessionRefreshed(
                        current.AvatarImageUrl,
                        account.AvatarImageUrl);
                    current.AccountId = account.AccountId;
                    current.Handle = account.Handle;
                    current.DisplayName = account.DisplayName;
                    current.InviteCode = account.InviteCode;
                    current.AvatarIcon = account.AvatarIcon;
                    current.AvatarColorHex = account.AvatarColorHex;
                    current.AvatarImageUrl = account.AvatarImageUrl;
                    current.Bio = account.Bio;
                    current.StatusMessage = account.StatusMessage;
                    current.IsGuest = account.IsGuest;
                    foreach (var alias in Cfg.CharacterSessions.Values.Where(s => s.AccountId == account.AccountId))
                        alias.IsGuest = account.IsGuest;
                    Cfg.Save();
                    if (!string.IsNullOrWhiteSpace(current.DisplayName))
                        _ = stream.SendHelloAsync(current.DisplayName);
                    validatedSessionContentId = contentId;
                    return;
                }

                if (result.Failure?.Failure == NetworkFailure.Unauthorized)
                {
                    // Only a definite authentication rejection removes the
                    // token. Timeouts, outages and malformed responses retain
                    // it so a temporary problem cannot sign the player out.
                    if (Cfg.CharacterSessions.TryGetValue(contentId, out var current) &&
                        string.Equals(current.Token, token, StringComparison.Ordinal))
                    {
                        foreach (var key in Cfg.CharacterSessions.Where(p => p.Value.Token == token).Select(p => p.Key).ToArray())
                            Cfg.CharacterSessions.Remove(key);
                        Cfg.GuestAccessNoticeSeen = true;
                        Cfg.Save();
                    }

                    validatedSessionContentId = contentId;
                    return;
                }

                // Retry temporary failures quietly. The existing session stays
                // usable while the server or network recovers.
                nextSessionValidationAttemptUtc =
                    DateTime.UtcNow.AddMinutes(5);
            });
        });
    }

    private void ToggleMiniPlayer()
    {
        if (miniPlayerWindow.IsOpen)
            miniPlayerWindow.IsOpen = false;
        else
            OpenMiniPlayer();
    }

    private bool miniPlayerWasOpen;

    private void StopPlaybackAfterMiniPlayerClosed()
    {
        var closed = miniPlayerWasOpen && !MiniPlaybackActive;
        miniPlayerWasOpen = miniPlayerWindow.IsOpen;
        // Automatic TV hiding changes the engine's visibility independently
        // of the viewer's spawn preference. Use the actual TV state here.
        if (!closed || screenController.Engine.IsActive || screenController.Engine.AudioOnlyMode) return;

        if (stream.Mode == StreamMode.Viewing)
        {
            mainWindow.DespawnViewerTv(stopPlayback: false);
            video.Stop();
            dutyViewerPlaybackStopped = true;
            Interlocked.Exchange(ref pendingRemoteState, null);
            waitingForMedia = false;
            broadcastLedger.ResetViewer();
        }
        else queue.StopPlayback();
        
    }

    private bool dutyViewerPlaybackStopped;
    private bool hostDutyActive;

    private void OpenMiniPlayer()
    {
        screenController.Engine.SetAudioOnlyMode(false);
        dutyViewerPlaybackStopped = false;
        miniPlayerWindow.IsOpen = true;

        if (stream.Mode != StreamMode.Viewing)
        {
            return;
        }

        var state = latestRemoteState;
        if (state is null || string.IsNullOrWhiteSpace(state.Url))
        {
            waitingForMedia = true;
            return;
        }

        ApplyRemoteState(state);
    }

    private void ToggleMiniChat()
    {
        if (miniChatWindow.IsOpen)
            miniChatWindow.IsOpen = false;
        else
            OpenMiniChat();
    }

    private void OpenMiniChat()
    {
        if (stream.Mode is not (StreamMode.Hosting or StreamMode.Viewing))
        {
            return;
        }

        miniChatWindow.IsOpen = true;
    }

    // Viewer path: apply playback position, pause state and screen transform to this
    // client's local ScreenPainter. Relay /rt only — not Penumbra — so anyone without AlphaChannel
    // (e.g. Lightless-only) cannot see the screen.
    private void ApplyRemoteState(AlphaChannel.Contracts.StreamControl message)
    {
        if (restrictedContentActive)
            return;
        if (stream.Mode != StreamMode.Viewing)
        {
            AepLog.Warning("[WatchParty] Ignoring state because mode is [redacted]");
            return;
        }

        //
        // Always remember the host's latest state, including while the
        // viewer has chosen not to spawn their local TV.
        //
        latestRemoteState =
            message;
        // Placement preferences still need to recover as the viewer moves,
        // even while they have deliberately closed both playback surfaces.
        mainWindow.ObserveYouTubeAgePlayback(message);
        var halloweenHandled = mainWindow.ObserveHalloweenState(message);
        if (mainWindow.HalloweenActivityActive) viewerTvNeedsLocationRecheckAfterZoning = false;
        if (halloweenHandled)
        {
            // Halloween replaces the decoder outside ordinary remote playback.
            // Do not let the pre-show URL suppress the first video afterwards.
            lastAppliedRemoteUrl = null;
            return;
        }
        if (mainWindow.ObservePartyCountdownState(message)) return;
        if (!mainWindow.HalloweenActivityActive) mainWindow.ObserveHostTvPlacement(message);
        if (dutyViewerPlaybackStopped && !mainWindow.ViewerTvEnabled && !MiniPlaybackActive) return;

        // Check the host TV's world/area before any of the media-specific
        // branches can apply its coordinates to this client's screen.
        mainWindow.HandleUnsafeRemoteTvPlacement(message);

        if (string.IsNullOrWhiteSpace(message.Url))
        {
            var enteredWaitingState =
                !loggedEmptyRemoteState;

            if (enteredWaitingState)
            {
                

                //
                // An empty stream state is the host's authoritative stop
                // signal. Clear the viewer's existing player before entering
                // the waiting state; otherwise the old URL keeps playing
                // indefinitely even though the room no longer advertises it.
                // Only do this on the transition because hosts publish their
                // current state every framework update.
                //
                video.Stop();

                mainWindow.ResetViewerPlaybackStart();
                loggedEmptyRemoteState = true;
            }

            waitingForMedia =
                true;

            //
            // A livestream source may briefly disappear while FFmpeg and
            // MediaMTX change publishers. Let the same URL be applied again
            // when it returns.
            //
            lastAppliedRemoteUrl =
                null;

            nextLiveHlsRetryUtc =
                DateTime.MinValue;

            liveHlsStartupUrl = null;
            liveHlsPlaybackAllowedUtc = DateTime.MinValue;

            liveDjRetry.Reset();
            return;
        }

        waitingForMedia =
            false;

        loggedEmptyRemoteState =
            false;

        // Give a newly advertised live broadcast time to publish its first
        // playable segments before starting MPV. Keep this deadline across
        // retries; only a different URL or room starts a new waiting period.
        if (IsLiveBroadcastUrl(message.Url))
        {
            if (!string.Equals(liveHlsStartupUrl, message.Url, StringComparison.Ordinal))
            {
                liveHlsStartupUrl = message.Url;
                liveHlsPlaybackAllowedUtc = DateTime.UtcNow + LiveHlsStartupDelay;
            }
        }
        else
        {
            liveHlsStartupUrl = null;
            liveHlsPlaybackAllowedUtc = DateTime.MinValue;
        }

        //
        // Apply the viewer's preference once when playback starts. The
        // prompt latch also prevents reopening a deliberately closed TV.
        //
        if (!mainWindow.ViewerTvEnabled &&
            !MiniPlaybackActive)
        {
            mainWindow.RequestViewerTvSpawnPrompt(applyPlaybackStartBehaviour: true);
            return;
        }

        var url =
     message.Url;

        if (mainWindow.BlockAgeRestrictedRemotePlayback(url))
        {
            lastAppliedRemoteUrl = null;
            youtubeViewerRecovery.Reset();
            return;
        }

        if (mainWindow.TryGetGameBroadcastStatus(url, out var gameStatus))
        {
            lastAppliedRemoteUrl = url;
            nextLiveHlsRetryUtc = DateTime.MinValue;
            ShowViewerBroadcastStatus(gameStatus);
            video.SetOverlayTitle(gameStatus, "Watch Party");

            if (!mainWindow.HalloweenActivityActive && mainWindow.CanApplyRemoteTvPlacement(message) &&
                message.ScreenX is { } statusX &&
                message.ScreenY is { } statusY &&
                message.ScreenZ is { } statusZ &&
                message.ScreenYaw is { } statusYaw &&
                message.ScreenScale is { } statusScale)
            {
                screenController.Engine.ApplyRemoteScreenTransform(
                    new Vector3(statusX, statusY, statusZ),
                    statusYaw,
                    statusScale,
                    message.ScreenDisableFixedScaleRatio ?? false,
                    message.ScreenWidthScale,
                    message.ScreenHeightScale);
            }

            if (!mainWindow.ViewerTvEnabled && MiniPlaybackActive)
                screenController.Engine.DespawnScreen();
            return;
        }

        if (mainWindow.IsWhiteboardUrl(url))
        {
            video.ClearBroadcastStatusScreen();
            lastAppliedRemoteUrl = url;
            nextLiveHlsRetryUtc = DateTime.MinValue;
            mainWindow.ActivateWhiteboardFromRemoteState(url);
            video.SetOverlayTitle(mainWindow.ActiveCanvasTitle, "Watch Party");

            if (!mainWindow.HalloweenActivityActive && mainWindow.CanApplyRemoteTvPlacement(message) &&
                message.ScreenX is { } whiteboardX &&
                message.ScreenY is { } whiteboardY &&
                message.ScreenZ is { } whiteboardZ &&
                message.ScreenYaw is { } whiteboardYaw &&
                message.ScreenScale is { } whiteboardScale)
            {
                screenController.Engine.ApplyRemoteScreenTransform(
                    new Vector3(whiteboardX, whiteboardY, whiteboardZ),
                    whiteboardYaw,
                    whiteboardScale,
                    message.ScreenDisableFixedScaleRatio ?? false,
                    message.ScreenWidthScale,
                    message.ScreenHeightScale);
            }

            if (!mainWindow.ViewerTvEnabled && MiniPlaybackActive)
            {
                screenController.Engine.DespawnScreen();
            }
            return;
        }

        video.ReleaseReplacedRemoteImage(url);
        var isLiveHls = IsLiveBroadcastUrl(url);
        var isLiveDj = LiveDjStream.AppliesTo(url);
        var liveDjMediaUrl = isLiveDj ? AudioVisualizerSelection.Parse(url).MediaUrl : null;
        liveDjRetry.SetUrl(liveDjMediaUrl);
        // Variable updates only change presentation. Apply them without recording
        // another connection attempt or showing a connecting screen over live audio.
        if (isLiveDj && video.HasVideoPlaybackSession && video.CurrentMediaUrl == liveDjMediaUrl &&
            video.State is (VideoPlaybackState.Playing or VideoPlaybackState.Paused) && url != lastAppliedRemoteUrl)
        {
            video.Play(url);
            lastAppliedRemoteUrl = url;
        }

        if (isLiveDj)
        {
            // GetProgress observes decoder failures, even before audio detection succeeds.
            var progress = video.GetProgress();
            if (video.State is (VideoPlaybackState.Playing or VideoPlaybackState.Paused) && progress.Position > 0f)
                liveDjRetry.Connected();
            if (liveDjRetry.Expired(DateTime.UtcNow))
            {
                ShowViewerBroadcastStatus("Live DJ broadcast unavailable. Re-sync to try again.");
                return;
            }
            var needsAttempt = !string.Equals(url, lastAppliedRemoteUrl, StringComparison.Ordinal) ||
                video.State is VideoPlaybackState.Failed or VideoPlaybackState.Idle;
            if (needsAttempt)
            {
                if (!liveDjRetry.CanAttempt(DateTime.UtcNow))
                {
                    ShowViewerBroadcastStatus("Connecting to live DJ broadcast...");
                    return;
                }
                liveDjRetry.Attempted(DateTime.UtcNow);
                video.ClearBroadcastStatusScreen();
                lastAppliedRemoteUrl = url;
                video.Play(url);
            }
        }

        if (isLiveHls && !broadcastLedger.CanView(
                mainWindow.CurrentSession?.Token, stream.HostId))
        {
            lastAppliedRemoteUrl = null;
            nextLiveHlsRetryUtc = DateTime.MinValue;
            ShowViewerBroadcastStatus(broadcastLedger.ViewerMessage);
            return;
        }

        if (isLiveHls && DateTime.UtcNow < liveHlsPlaybackAllowedUtc)
        {
            ShowViewerBroadcastStatus(LiveBroadcastStream.ConnectingMessage);
            return;
        }

        if (isLiveHls && !LiveHlsPlaylistReady(url))
        {
            ShowViewerBroadcastStatus(LiveBroadcastStream.ConnectingMessage);
            return;
        }

        video.ClearBroadcastStatusScreen();

        var urlChanged =
            !string.Equals(
                url,
                lastAppliedRemoteUrl,
                StringComparison.Ordinal);

        var livePlaybackNeedsRetry =
            isLiveHls &&
            video.State is
                VideoPlaybackState.Failed or
                VideoPlaybackState.Idle;

        var retryIsDue =
            DateTime.UtcNow >=
            nextLiveHlsRetryUtc;

        if (urlChanged)
        {
            lastAppliedRemoteUrl =
                url;

            nextLiveHlsRetryUtc =
                DateTime.UtcNow +
                LiveHlsRetryDelay;

            video.Play(
                url);
        }
        else if (livePlaybackNeedsRetry &&
                 retryIsDue)
        {
            //
            // Keep the viewer's TV present while MediaMTX prepares the new HLS
            // manifest, then make another clean playback attempt. This also
            // recovers after the first attempt entered Failed rather than Idle.
            //
            liveHlsReady = false;
            nextLiveHlsProbeUtc = DateTime.MinValue;
            lastAppliedRemoteUrl = null;
            ShowViewerBroadcastStatus(LiveBroadcastStream.ConnectingMessage);

            nextLiveHlsRetryUtc =
                DateTime.UtcNow +
                LiveHlsRetryDelay;

            return;
        }
        else if (!isLiveHls &&
                 video.State ==
                 VideoPlaybackState.Idle)
        {
            //
            // Preserve the previous same-URL restart behaviour for ordinary
            // media after joining or resetting local playback.
            //
            video.Play(
                url);
        }

        // MPV may add caption tracks after loadfile returns. Reapply
        // the host's choice on subsequent room states until the track exists.
        if (VideoUrlResolver.IsYouTubeUrl(url) ||
            VideoUrlResolver.IsDailymotionUrl(url) ||
            InternetArchiveSubtitles.IsArchiveVideoUrl(url))
            video.SelectSubtitle(message.SubtitleLanguage, message.SubtitleName);

        //
        // Ordinary videos use position correction. Live HLS and audio-only
        // streams are not safely seekable and must remain attached to their
        // live edge.
        //
        if (!isLiveHls && !isLiveDj &&
            !video.IsAudioOnly)
        {
            if (message.PositionSeconds is double remotePosition)
            {
                var progress=video.GetProgress();
                var youtube=VideoUrlResolver.IsYouTubeUrl(url);
                var now=DateTime.UtcNow;
                var loading=screenController.Engine.MediaLoadPending||screenController.Engine.WebResolverFallbackRunning;
                if(youtube&&youtubeViewerRecovery.Observe(stream.HostId+"|"+url,now,remotePosition,message.Paused??false,
                    progress.Position,loading,video.State==VideoPlaybackState.Failed,
                    progress.Duration>0))
                {
                    AepLog.Warning("[WatchParty] Restarting stalled viewer playback to follow the host.");
                    video.Play(url,forceRestart:true);
                }
                else if (!loading&&video.State is (VideoPlaybackState.Playing or VideoPlaybackState.Paused)&&
                    double.IsFinite(remotePosition)&&remotePosition>=0&&
                    Math.Abs(progress.Position-remotePosition)>SyncToleranceSeconds&&
                    (!youtube||youtubeViewerRecovery.TryCorrectPosition(now)))
                {
                    video.SeekForSynchronization(
                        (float)remotePosition);
                }
            }
        }

        // Do not seek/unpause the old decoder while a YouTube reload is pending.
        // The next fresh host heartbeat applies its state after FILE_LOADED.
        if(!VideoUrlResolver.IsYouTubeUrl(url)||
            !screenController.Engine.MediaLoadPending&&!screenController.Engine.WebResolverFallbackRunning)
            video.PauseForSynchronization(message.Paused??false);
        video.SetOverlayTitle(WatchPartyMediaDisplay.Title(message.Url, message.MediaTitle,
            audioOnly: video.IsAudioOnly), string.Empty);

        if (!mainWindow.HalloweenActivityActive && mainWindow.CanApplyRemoteTvPlacement(message) &&
            message.ScreenX is { } x &&
            message.ScreenY is { } y &&
            message.ScreenZ is { } z &&
            message.ScreenYaw is { } yaw &&
            message.ScreenScale is { } scale)
        {
            screenController.Engine.ApplyRemoteScreenTransform(
                new Vector3(
                    x,
                    y,
                    z),
                yaw,
                scale,
                message.ScreenDisableFixedScaleRatio ??
                false,
                message.ScreenWidthScale,
                message.ScreenHeightScale);
        }

        if (!mainWindow.ViewerTvEnabled &&
            MiniPlaybackActive)
        {
            screenController.Engine.DespawnScreen();
        }
    }

    internal static unsafe ulong ReadLocalContentId()
    {
        var state = PlayerState.Instance();
        return state is null ? 0 : state->ContentId;
    }

    // CurrentWorld follows world visits and data-centre travel.
    // HomeWorld would always return the character's original server.
    private static string? ReadLocalWorldName()
    {
        var player =
            ObjectTable.LocalPlayer;

        if (player is null)
        {
            return null;
        }

        try
        {
            var worldName =
                player.CurrentWorld.Value.Name
                    .ToString();

            return string.IsNullOrWhiteSpace(
                    worldName)
                ? null
                : worldName;
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[World] Could not resolve current world.");

            return null;
        }
    }

    // Previous plugin code, likely needs removing with future updates
    private const byte LalafellRaceId = 3;

    private static bool ReadIsLalafell()
    {
        var local = ObjectTable.LocalPlayer;
        if (local is null)
        {
            return false;
        }

        var customize = local.Customize;
        var raceIndex = (int)CustomizeIndex.Race;
        return customize.Length > raceIndex && customize[raceIndex] == LalafellRaceId;
    }

    // Writes (or, for sign-out, removes) the CharacterSession for whichever character is currently
    // being played - the one piece of persistence MainWindow's sign-in UI isn't allowed to do
    // itself, same split as PromptForName above.
    private void UpdateSessionForCurrentCharacter(CharacterSession? session)
    {
        var contentId = ReadLocalContentId();
        if (session?.IsGuest == true) Cfg.CharacterSessions[0] = session;
        // In-place conversion retains the shared identity and its metadata.
        if (session is not null && Cfg.CharacterSessions.GetValueOrDefault(0UL)?.AccountId == session.AccountId)
        {
            var previousReference = Cfg.CharacterSessions[0].GuestReferenceRecorded;
            session.GuestReferenceRecorded |= previousReference;
            foreach (var key in Cfg.CharacterSessions.Where(p => p.Value.AccountId == session.AccountId).Select(p => p.Key).ToArray())
                Cfg.CharacterSessions[key] = session;
        }
        if (contentId == 0)
        {
            Cfg.Save();
            return;
        }

        if (session is null)
        {
            var old = Cfg.CharacterSessions.GetValueOrDefault(contentId);
            if (old is not null)
                foreach (var key in Cfg.CharacterSessions.Where(p => p.Value.Token == old.Token).Select(p => p.Key).ToArray())
                    Cfg.CharacterSessions.Remove(key);
        }
        else
        {
            Cfg.CharacterSessions[contentId] = session;
        }

        Cfg.Save();
        if (!string.IsNullOrWhiteSpace(session?.DisplayName))
            _ = stream.SendHelloAsync(session.DisplayName);
    }

    private void ShowViewerBroadcastStatus(string status)
    {
        video.ShowBroadcastStatusScreen(status);
        if (!mainWindow.ViewerTvEnabled && MiniPlaybackActive)
            screenController.Engine.DespawnScreen();
    }

    private bool LiveHlsPlaylistReady(string url)
    {
        var now = DateTime.UtcNow;
        if (!string.Equals(liveHlsProbeUrl, url, StringComparison.Ordinal))
        {
            liveHlsProbeUrl = url;
            liveHlsProbeTask = null;
            liveHlsReady = false;
            nextLiveHlsProbeUtc = DateTime.MinValue;
        }

        if (liveHlsReady) return true;
        if (liveHlsProbeTask is { IsCompleted: true } completed)
        {
            liveHlsProbeTask = null;
            try { liveHlsReady = completed.GetAwaiter().GetResult(); }
            catch { liveHlsReady = false; }
            if (liveHlsReady) return true;
            nextLiveHlsProbeUtc = now.AddSeconds(3);
        }

        if (liveHlsProbeTask is null && now >= nextLiveHlsProbeUtc)
        {
            liveHlsProbeTask = ProbeLiveHlsPlaylistAsync(url);
            nextLiveHlsProbeUtc = now.AddSeconds(3);
        }
        return false;
    }

    private static async Task<bool> ProbeLiveHlsPlaylistAsync(string url)
    {
        try
        {
            using var client = PluginHttpClients.CreateProbeClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var response = await client.GetAsync(url,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool IsLiveBroadcastUrl(string url) => LiveBroadcastStream.AppliesTo(url);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref recoveryWindow, null) is { } recovery)
        {
            recovery.Dispose();
            return;
        }
        DisposeStandardMode();
    }

    private void DisposeStandardMode()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
        {
            return;
        }

        YouTubeRequestScheduler.Shared.CancelAllPending();

        static void Cleanup(
            ShutdownResource name,
            Action action)
        {
            try
            {
                action();
            }
            catch (Exception error) { AepLog.CleanupFailure(ShutdownOwner.Plugin,name,error); }
        }

        // Drain a framework tick or UI draw already in progress before destroying the
        // objects it uses. Also stop world rendering before any blocking wait.
        lock (frameworkLifetimeLock)
            Cleanup(ShutdownResource.DirectXHook, DxHandler.BeginShutdown);

        Cleanup(ShutdownResource.BroadcastLedger, () => broadcastLedger.UpdateHost(null, null, false));
        Cleanup(ShutdownResource.FrameworkUpdateCallback, () => Framework.Update -= OnFrameworkUpdate);
        Cleanup(ShutdownResource.DiscordActivity, () => discordActivity?.Dispose());
        Cleanup(ShutdownResource.ServerInfoBar, () => serverInfoEntry?.Remove());
        Cleanup(ShutdownResource.PluginUpdateMonitor, () => pluginUpdates?.Dispose());
        Cleanup(ShutdownResource.UIDrawCallback, () => PluginInterface.UiBuilder.Draw -= DrawPluginUi);

        Cleanup(ShutdownResource.SessionValidationCancellation, sessionValidationLifetime.Cancel);
        Cleanup(ShutdownResource.SessionValidationWorker,
            () =>
            {
                if (sessionValidationTask is { IsCompleted: false } &&
                    !sessionValidationTask.Wait(TimeSpan.FromSeconds(3)))
                {
                    AepLog.Warning(
                        "[Shutdown] Session validation worker did not stop within 3 seconds.");
                }
            });
        if (alphaCommandRegistered) Cleanup(ShutdownResource.AlphaCommand, () => CommandManager.RemoveHandler("/alpha"));
        if (watchPartyCommandRegistered) Cleanup(ShutdownResource.WpCommand, () => CommandManager.RemoveHandler("/wp"));
        // Keep this paired with the disabled registration above.
        Cleanup(ShutdownResource.ContextMenu, () => ContextMenu.OnMenuOpened -= OnCharacterMenuOpened);
        Cleanup(ShutdownResource.MainUICallback, () => PluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow);
        if (mainWindow is not null)
            Cleanup(ShutdownResource.SettingsUICallback, () => PluginInterface.UiBuilder.OpenConfigUi -= mainWindow.OpenSettingsUi);
        Cleanup(ShutdownResource.HonorificStatusOverride, ClearHonorificStatus);
        Cleanup(ShutdownResource.HonorificTitleChangeCallback, () => honorificTitleChanged?.Unsubscribe(OnHonorificTitleChanged));

        Cleanup(ShutdownResource.MainWindow, () => mainWindow?.Dispose());
        Cleanup(ShutdownResource.WorldLighting, () => { worldLighting?.Dispose(); worldLighting = null; });
        if (screenController is not null && mainWindow is not null)
        {
            Cleanup(ShutdownResource.YouTubeSetupNotice, () => { screenController.Engine.YouTubeSetupBlocked -= mainWindow.ShowYouTubeSetupNotice; screenController.Engine.YouTubeAgeRestricted -= mainWindow.QueueYouTubeAgeWarning; screenController.Engine.YouTubeConnectionUsable = null; });
            Cleanup(ShutdownResource.ActivityBlockedNotice, () => screenController.Engine.ActivityStartBlocked -= mainWindow.NotifyActivityStartBlocked);
        }
        Cleanup(ShutdownResource.MiniPlayer, () => miniPlayerWindow?.Dispose());
        Cleanup(ShutdownResource.MiniChat, () => miniChatWindow?.Dispose());
        Cleanup(ShutdownResource.NearbyAutoWatch, () => nearbyAutoWatch?.Dispose());
        Cleanup(ShutdownResource.BroadcastAllowance, () => broadcastAllowance?.Dispose());
        Cleanup(ShutdownResource.Queue, () => queue?.Dispose());

        Cleanup(ShutdownResource.StreamEvent, () => { if (stream is not null) stream.OnState -= OnRemoteState; });
        Cleanup(ShutdownResource.LocalScreenPlacementEvent, () => { if (screenController is not null && stream is not null) screenController.Engine.LocalScreenPlaced -= stream.ReleaseDutyScreen; });
        Cleanup(ShutdownResource.RenameEvent, () => { if (stream is not null) stream.OnRenameRequired -= OnRenameRequired; });
        Cleanup(ShutdownResource.RealtimeConnection, () => stream?.Dispose());

        Cleanup(ShutdownResource.VideoPlayer, () => video?.Dispose());
        Cleanup(ShutdownResource.ScreenController, () => screenController?.Dispose());
        Cleanup(ShutdownResource.DirectXHook, DxHandler.Dispose);
        Cleanup(ShutdownResource.SessionValidationToken, sessionValidationLifetime.Dispose);
    }

    private static string? ReadLocalHomeWorldName()
    {
        var player = ObjectTable.LocalPlayer;
        if (player is null)
        {
            return null;
        }

        try
        {
            var worldName = player.HomeWorld.Value.Name.ToString();
            return string.IsNullOrWhiteSpace(worldName)
                ? null
                : worldName;
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[Reactions] Could not resolve local home world.");
            return null;
        }
    }
}

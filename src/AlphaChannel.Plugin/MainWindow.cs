using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.YouTube;
using AlphaChannel.Plugin.Video;
using AlphaChannel.Contracts;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using AlphaChannel.Plugin.BroadcastAllowance;
using AlphaChannel.Plugin.ActivityPoints;
using System.Diagnostics;

namespace AlphaChannel.Plugin;

// Split into partials by concern (MainWindow.Home.cs, .Playback.cs, .Queue.cs, .Search.cs,
// .Screen.cs, .Settings.cs, .Reactions.cs) - this file has the window skeleton: the sidebar nav,
// the theme/palette, the name prompt, and watch-along/roster (shared between the Home dashboard's
// Live Now card and the dedicated Watch-along page). Smart-TV-dashboard look (dark background,
// purple neon glow border, sidebar nav, rounded cards) built with plain ImGui style pushes plus
// hand-drawn ImDrawList primitives (MainWindow.Home.cs) where ImGui has no built-in equivalent.
internal sealed partial class MainWindow : Window, IDisposable
{
    private int disposeStarted;
    // Active palette for this frame - set at the top of Draw() from Cfg.UiTheme so every partial
    // (and ThemeScope) reads the same colors without threading a palette through each helper.
    // Mockup default is Purple (deep navy + violet accent + magenta/cyan glow).
    private static ThemeColors Colors = ThemeCatalog.Get(UiTheme.Purple);
    private static RichPageColors hubColors = new(Colors, UiBackground.Theme, false);
    private static Vector4 HubSurface(int referenceRgb) => hubColors.Surface(referenceRgb);
    private static Vector4 HubTint(int referenceRgb) => hubColors.Tint(referenceRgb);
    private static Vector4 HubSelected(int referenceRgb) => hubColors.Selected(referenceRgb);

    private static Vector4 Accent => Colors.Accent;
    private static Vector4 AccentHover => Colors.AccentHover;
    private static Vector4 AccentActive => Colors.AccentActive;
    private static Vector4 AccentSurface(float strength) => FadeForCustomBg(Vector4.Lerp(Colors.CardBg, Accent, strength), 0.30f);
    private static Vector4 AccentFieldBg => AccentSurface(0.15f);
    private static Vector4 AccentFieldHover => AccentSurface(0.23f);
    private static Vector4 AccentFieldActive => AccentSurface(0.31f);
    private static Vector4 AccentPanelBg => CardBg;
    private static Vector4 AccentPanelHover => CardBgHover;
    private static Vector4 AccentIconBg => AccentSurface(0.23f);
    private static Vector4 AccentInk(float alpha) => new(Accent.X, Accent.Y, Accent.Z, alpha);
    private static Vector4 BlueGlow => Colors.BlueGlow;
    private static Vector4 MagentaGlow => Colors.MagentaGlow;
    private static Vector4 Gold => Colors.Gold;
    private static Vector4 GoldHover => Colors.GoldHover;
    private static Vector4 FrameBg => settingsControlsActive
        ? SettingsControlBg
        : FadeForCustomBg(Colors.FrameBg, 0.30f);
    private static Vector4 FrameBgHover => settingsControlsActive
        ? SettingsControlHover
        : FadeForCustomBg(Colors.FrameBgHover, 0.38f);
    private static Vector4 Danger => Colors.Danger;
    private static Vector4 Good => Colors.Good;
    // Custom wallpaper mode: panels are ~75% see-through so the image reads through.
    private static Vector4 WindowBg => FadeForCustomBg(Colors.WindowBg, 0.22f);
    private static Vector4 SidebarBg => FadeForCustomBg(Colors.SidebarBg, 0.28f);
    private static Vector4 CardBg => FadeForCustomBg(Colors.CardBg, 0.25f);
    private static Vector4 CardBgHover => FadeForCustomBg(Colors.CardBgHover, 0.35f);
    private static Vector4 MutedText => Colors.MutedText;
    private static Vector4 BorderSubtle => new(1f, 1f, 1f, settingsControlsActive ? 0.35f : 0.085f);
    private ISharedImmediateTexture? alphaIconImage;
    private ISharedImmediateTexture? sidebarLogoImage;
    private UiTheme? sidebarLogoTheme;
    private ISharedImmediateTexture? reactPreviewImage;
    private ISharedImmediateTexture? watchPartyHeaderImage;


    // Set each frame in Draw() when a custom background texture is actually showing.
    private static bool customBackgroundActive;


    private static Vector4 FadeForCustomBg(Vector4 color, float alpha) =>
        customBackgroundActive ? new Vector4(color.X, color.Y, color.Z, alpha) : color;

    // First launch experience
    private bool showingFirstLaunch;

    private float firstLaunchFadeAlpha = 1f;
    private bool firstLaunchFadingOut;
    private bool firstLaunchStarted;

    private bool firstLaunchWindowDragging;

    private Vector2 firstLaunchWindowDragOffset;

    //
    // The splash begins immediately, pauses for onboarding after the welcome
    // text, then resumes its fixed loading sequence after confirmation.
    //
    private bool firstLaunchSetupSubmitted;
    private enum FirstLaunchStep { SignInChoice, CreateAccountChoice, Username, Topics }
    private FirstLaunchStep firstLaunchStep = FirstLaunchStep.SignInChoice;
    private bool firstLaunchUsingAccount;
    private bool firstLaunchNameSubmitting;
    private string? firstLaunchNameError;

    private double firstLaunchSetupSubmittedAt;
    private double firstLaunchLoadingStartedAt;

    private const double FirstLaunchWelcomeDuration =
        3.5;

    private const double FirstLaunchSetupFadeDuration =
        0.75;

    private const double FirstLaunchLoadingDuration =
        16.0;

    private bool roomEndedPlaybackResetPending;
    private bool hostLeaveConfirmationRequested;

    private double firstLaunchStartedAt;


    private double firstLaunchLastMessageChange;

    private int firstLaunchMessageIndex;

    private static readonly string[] FirstLaunchMessages =
    [
        "Building Video Cache...",
    "Fetching video data...",
    "Polishing the screen...",
    "Gets distracted and starts doomscrolling...",
    "Watching cat videos..."
    ];

    private static Vector4 Hex(int rgb) => new(
        ((rgb >> 16) & 0xFF) / 255f,
        ((rgb >> 8) & 0xFF) / 255f,
        (rgb & 0xFF) / 255f,
        1f);

    private enum HomePage
    {
        Home,
        Player,
        PlaySnes,
        // Alpha Channel embedded-browser integration.
        Browser,
        InternetArchive,
        UnifiedSearch,
        VideoGrid,
        Screen,
        WatchAlong,
        Moderator,
#if DEBUG
#endif
        Whiteboard,
        Pictionary,
        PictionaryTeams,
        Quiz,
        Roulette,
        Bingo,
        SkyRail,
        SlotMachine,
        Poker,
        RacePreview,
        Friends,
        Messages,
        Activities,
        Arcade,
        PartyDirectory,
        GoLive,
        Settings,
        Help,
        LuckyWheel,
        Music,
        Profile,
        AlphaChat,
        Chats,
        Events,
        Pixels,
        Dailymotion,
        BlastParty,
        FriendsDirectory,
        Twitch,
    }

    private readonly ScreenController screenController;
    private readonly VideoPlayer video;
    private readonly AetherStreamQueue personalQueue;
    private AetherStreamQueue queue => PartyPlaybackQueue ?? personalQueue;
    private readonly QueueManager queueManager;
    private readonly StreamClient stream;
    private readonly ThumbnailCache thumbnails =
        new();

    //
    // User-provided still images and slideshow previews use the strict image
    // validation path. Ordinary YouTube/Twitch thumbnails remain separate.
    //
    private readonly SafeImagePreviewCache imagePreviews =
        new();

    private readonly Action requestRename;
    private readonly Action<string> saveLocalDisplayName;
    private readonly SignInFlow signInFlow;
    private readonly AuthClient authClient;
    private readonly FriendsClient friendsClient;
    private readonly ActivityClient activityClient;
    private readonly DmClient dmClient;
    private readonly ReportClient reportClient;
    private readonly VenuesClient venuesClient;
    private readonly LiveClient liveClient;
    private readonly RoomsClient roomsClient;
    private readonly RadioClient radioClient;
    private readonly TwitchClient twitchClient;
    private readonly Crypto.KeyVault keyVault;

    // Called whenever sign-in/link/sign-out changes what CharacterSession belongs to the currently-
    // played character - the callback (Plugin.cs) is what actually writes Cfg.CharacterSessions and
    // saves, same split as requestRename above (MainWindow owns the UI, Plugin.cs owns persistence).
    private readonly Action<CharacterSession?> onSessionChanged;
    private readonly BroadcastAllowanceService broadcastAllowance;
    internal BroadcastLedgerCoordinator BroadcastLedger { get; set; } = null!;
    private readonly ActivityPointsService activityPoints;
    // The approved full-size layout is the largest the normal window may become.
    // At 85% scale the dashboard remains usable without allowing it to collapse
    // all the way to the separate minimized capsule dimensions.
    //
    // The approved full layout remains the maximum size. Brand-new
    // installations initially open at approximately 80% of it.
    //
    private static readonly Vector2 WindowSize =
    new(1220f, 840f);

    // Retain the old base when deriving the two ceilings: Design now uses the
    // previous 1440p maximum; 4K keeps its previous maximum.
    private static readonly Vector2 BaseMaximumWindowSize =
        WindowSize * 1.05f;

    private static readonly Vector2 MaximumWindowSize =
        BaseMaximumWindowSize * 1.2f;

    private static readonly Vector2 UhdMaximumWindowSize =
        BaseMaximumWindowSize * 1.5f;

    private static readonly Vector2 FirstLaunchWindowSize =
        new(
            WindowSize.X * 0.90f,
            WindowSize.Y * 0.90f);

    private const float SidebarRowHeight = 36f;
    private const float PreviousMinimumWindowHeight = 730f;
    private static readonly Vector2 MinimumWindowSize =
        new(950f, MinimumWindowHeight);

    private const float FixedLayoutScale =
     1f;

    private float layoutScale =
        FixedLayoutScale;

    // Drawing helpers include a small number of static primitives shared by
    // the partial files. They read this frame-local value so their geometry
    // follows the active main-window preset as well.
    private static float activeLayoutScale =
        FixedLayoutScale;

    private static bool activeNativeUiSizing;

    private bool windowSizeSavePending;


    private void RefreshLayoutScale(
        Vector2 windowSize)
    {
        activeNativeUiSizing = Plugin.Cfg.NativeUiSizing;
        layoutScale =
            !windowMinimized &&
            !showingFirstLaunch &&
            Plugin.Cfg.WindowSizePreset == UiWindowSizePreset.Uhd
                ? 1.5f
                : FixedLayoutScale;

        if (activeNativeUiSizing && !windowMinimized)
            layoutScale = NativeUiSizingMetrics.LayoutScale(windowSize.X, WindowSize.X);

        activeLayoutScale =
            layoutScale;
    }

    private static float Ui(
        float px) =>
        px * activeLayoutScale;

    // Legacy literal measurements stay untouched until native sizing is opted into.
    private static float NativeUi(float px) => activeNativeUiSizing ? Ui(px) : px;

    private static Vector2 UiVec(
        float x,
        float y) =>
        new(
            x * activeLayoutScale,
            y * activeLayoutScale);

    // Mini Mode begins as a compact status bar. The player and chat actions
    // currently open their matching full-size pages; the later detachable
    // panels can take over those actions without changing this chrome.
    private static readonly Vector2 MinimizedSize = new(650f, 44f);
    private bool windowMinimized;
    private bool userResized;
    private Vector2 userWindowSize = WindowSize;

    // True after /achannel watch or context-menu Join Stream: stay minimized; screen still
    // draws via ScreenPainter + /rt sync. Requires AlphaChannel on both sides — not Lightless.
    private bool viewerMode;
    // Set when NearbyAutoWatch started the join — range leave only applies to these sessions.
    private bool proximityJoined;
    private Vector2? maximizedPosition;
    private Vector2? minimizedPosition;
    private Vector2? pendingPosition;

    private HomePage selectedPage = HomePage.Home;
    private sealed record PageBackEntry(HomePage Page,string? ProfileId,string? ProfileName,
        string? ProfileViewer,HomePage ProfileReturn);
    private readonly List<PageBackEntry> pageBackHistory = [];
    // All page assignments pass through here, including navigation outside the sidebar.
    // Tabs use their own state and do not add history entries.
    private HomePage currentPage
    {
        get => selectedPage;
        set
        {
            // Keep the page beneath the age warning in place, including during
            // the first frame before its overlay has captured pointer input.
            // The connection link clears the warning before navigating.
            if (youtubeAgeWarningTitle is not null) return;
            if (value == HomePage.Chats) value = chatsHubTab == 0 ? HomePage.AlphaChat : chatsHubTab is 1 or 2 ? HomePage.Messages : HomePage.Chats;
            if (selectedPage == value) return;
            if (!AllowInlineProfileNavigation()) return;
            pageBackHistory.Add(new PageBackEntry(selectedPage,
                selectedPage==HomePage.Profile?profilePageAccountId:null,
                selectedPage==HomePage.Profile?profilePageFallbackName:null,
                selectedPage==HomePage.Profile?CurrentSession?.AccountId:null,profileReturnPage));
            if (pageBackHistory.Count > 100) pageBackHistory.RemoveAt(0);
            selectedPage = value;
        }
    }

    private void GoBackPage()
    {
        if (pageBackHistory.Count == 0) return;
        if (!AllowInlineProfileNavigation()) return;
        while(pageBackHistory.Count>0)
        {
            var previous=pageBackHistory[^1];
            pageBackHistory.RemoveAt(pageBackHistory.Count-1);
            if(previous.Page==HomePage.Profile)
            {
                if(previous.ProfileId is not { } profileId || CurrentSession is not { } session ||
                    previous.ProfileViewer!=session.AccountId || AlphaChatBlocked(profileId)) continue;
                selectedPage=HomePage.Profile;
                profileReturnPage=previous.ProfileReturn;
                if(profilePageAccountId==profileId && profilePageData is not null && !profilePageLoading && !profileDetailsLoading)
                {
                    profilePageOpen=true;
                    profileFriendConfirmation=ProfileFriendConfirmationAction.None;
                    profileFriendConfirmationPendingOpen=false;
                }
                else OpenProfilePage(session,profileId,previous.ProfileName??"Profile");
            }
            else selectedPage=previous.Page;
            return;
        }
        selectedPage=HomePage.Chats;
    }

    private bool DrawPageBackButton()
    {
        bool clicked;
        using (UiDisabled.Scope(pageBackHistory.Count == 0))
        using (ImRaii.PushColor(ImGuiCol.Button, CardBg)
            .Push(ImGuiCol.ButtonHovered, CardBgHover)
            .Push(ImGuiCol.ButtonActive, Accent)
            .Push(ImGuiCol.Border, BorderSubtle)
            .Push(ImGuiCol.Text, MutedText))
        using (UiFonts.PushIcon())
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, Vector2.Zero)
            .Push(ImGuiStyleVar.FrameBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.FrameRounding, Ui(8f))
            .Push(ImGuiStyleVar.ButtonTextAlign, new Vector2(.5f, .5f)))
            clicked = ImGui.Button($"{FontAwesomeIcon.ArrowLeft.ToIconString()}##backPage", UiVec(34f, 30f));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiFonts.SetTooltip("Back");
        if (clicked) GoBackPage();
        return clicked;
    }
    private HomePage helpReturnPage = HomePage.Home;


    // Transition Animation
    private HomePage lastAnimatedPage = HomePage.Home;
    private double pageTransitionStartedAt = -1d;

    private string joinHostNameInput = string.Empty;
    private string joinPasswordInput = string.Empty;
    private string? joinErrorText;
    private DateTime joinErrorExpiresAt = DateTime.MaxValue;
    private string? joinError
    {
        get
        {
            if (DateTime.UtcNow >= joinErrorExpiresAt) joinErrorText = null;
            return joinErrorText;
        }
        set { joinErrorText = value; joinErrorExpiresAt = DateTime.MaxValue; }
    }
    private string createRoomDescription = string.Empty;
    private string createRoomLocation = string.Empty;
    private int createRoomKindIndex;
    private string createRoomPassword = string.Empty;
    private int createRoomCategoryIndex;
    private bool createRoomAdultOnly;
    private bool createRoomPrivate;

    //
    // Locked-room creation is confirmed through a dedicated password
    // popup rather than showing the password inside the main form.
    //
    private bool createLockedRoomPasswordPopupRequested;
    private string? createLockedRoomPasswordError;

    //
    // The locked-room password popup is shared by room creation and
    // editing an existing hosted room.
    //
    private bool createLockedRoomPasswordForRoomEdit;

    //
    // Host-only editing state for the active Watch Party details tab.
    //
    private bool partyRoomEditing;

    private static readonly string[] WatchPartyCategoryOptions =
    [
        "YouTube",
        "Movies",
        "TV",
        "Twitch",
        "Cartoons",
        "Live Stream",
        "Gaming",
        "DJ",
        "Music",
        "Images",
        "Promotional",
        "Anime",
        "Horror",
        "Web Browsing",
        "Marathon",
        "Social",
        "Cinema",
        "Club",
        "Halloween",
    ];
    private string roomBrowsePassword = string.Empty;
    private string? roomBrowseTitle;
    private RoomDirectoryDto[] roomBrowseList = [];
    private bool roomBrowseLoading;
    private string? roomBrowseError;
    private RoomDirectoryDto[] homeWatchPartyRooms = [];
    private double homeWatchPartyFetchedAt = -999;
    private bool homeWatchPartyLoading;
    private bool roomBrowseFriendsOnly;
    private bool joinClearQueuePending;
    private RadioCredentialsDto? radioCredentials;
    private string? radioError;

    private const float DesignSidebarWidth = 210f;
    private const float CollapsedSidebarWidth = 56f;
    private bool sidebarCollapsedForFrame;
    private Vector2 sidebarMainWindowSize;
    private float SidebarWidth => Ui(sidebarCollapsedForFrame ? CollapsedSidebarWidth : DesignSidebarWidth);
    // Collapse reduces the Design width before the selected minimum scale.
    private float CollapsedSidebarSavedWidth => Plugin.Cfg.SidebarCollapsed && !showingFirstLaunch
        ? DesignSidebarWidth-CollapsedSidebarWidth : 0f;
    private const float DesignBottomBarHeight = 96f;
    private static float BottomBarHeight => Ui(DesignBottomBarHeight);

    // Borderless Child windows ignore WindowPadding in this ImGui build unless AlwaysUseWindowPadding
    private const ImGuiWindowFlags PaddedChild =
    ImGuiWindowFlags.AlwaysUseWindowPadding |
    ImGuiWindowFlags.NoScrollbar;

    private const ImGuiWindowFlags NavPaneFlags = PaddedChild | ImGuiWindowFlags.NoScrollWithMouse;

    // Not from StreamClient - see the comment where it's set (DoJoin) for why: HostId gets
    // overwritten with the host's real UserId once StreamJoined arrives, so this is the only
    // place the friendly name a viewer actually typed survives for display.
    private string? joinedHostDisplayName;

    private bool namePromptActive;
    private string namePromptInput = string.Empty;

    private bool createQueuePopupOpen;
    private int creatingQueueIndex = -1;
    private bool editingQueueProfile;
    private string newQueueName = string.Empty;
    private string newQueueIcon = "Tv";
    private string? queueEditorError;

    private bool deleteQueuePopupOpen;
    private int deletingQueueIndex = -1;
    private string deletingQueueName = string.Empty;

    private bool clearQueuePopupOpen;
    private string clearingQueueName = string.Empty;
    private int clearingQueueVideoCount;

    private Action<string>? onNameConfirmed;


    // ---------------------------------------------------------
    // Welcome splash particles
    // ---------------------------------------------------------

    private sealed class LoadingCardParticle
    {
        public Vector2 Position;

        public float Width;
        public float Height;

        public float Speed;

        public float Alpha;

        public float Drift;

        public double SpawnTime;

        public float ShimmerOffset;

        public bool IsLeftSide;
    }

    private static readonly
        (
            FontAwesomeIcon Icon,
            string Title
        )[]
        SplashLoadingFeatures =
        [
            (
            FontAwesomeIcon.Music,
            "Live DJ"
        ),
        (
            FontAwesomeIcon.Users,
            "Watch Parties"
        ),
        (
            FontAwesomeIcon.Gamepad,
            "Retro Games"
        ),
        (
            FontAwesomeIcon.Film,
            "Movies & Shows"
        )
        ];

    private double lastLoadingCardSpawn;

    //
    // Blank cards normally choose a random side. After three consecutive
    // cards on one side, force the next one onto the opposite side.
    //
    private bool? lastBlankCardSpawnedLeft;

    private int consecutiveBlankCardsOnSameSide;

    //
    // Particle positions use screen coordinates. Track the splash area's
    // previous position so existing particles follow the window when it moves.
    //
    private Vector2? previousLoadingCardAreaMinimum;

    private readonly List<LoadingCardParticle>
        loadingCards =
            [];

    internal bool IsNamePromptActive => namePromptActive;

    internal bool ViewerTvEnabled { get; private set; }
    private DateTime nextPlaybackHudNotificationRefreshUtc = DateTime.MinValue;

    internal int GetPlaybackHudNotificationCount()
    {
        if (CurrentSession is { } session)
        {
            if (DateTime.UtcNow >= nextPlaybackHudNotificationRefreshUtc)
            {
                friendsDirty = true;
                conversationsDirty = true;
                nextPlaybackHudNotificationRefreshUtc = DateTime.UtcNow.AddMinutes(1);
            }

            if (friendsDirty && !friendsLoading)
                RefreshFriends(session.Token);
            if (conversationsDirty && !conversationsLoading &&
                DateTime.UtcNow >= conversationsNextRetryUtc)
                RefreshConversations(session.Token);
        }

        var total = (long)Math.Max(0, partyChatUnreadCount) + FriendsSidebarNotificationCount() + ChatsHubNotificationCount();
        return (int)Math.Min(int.MaxValue, total);
    }

    internal void ToggleTvFromPlaybackHud()
    {
        if (stream.Mode == StreamMode.Viewing)
        {
            if (ViewerTvEnabled)
                DespawnViewerTv(stopPlayback: !(IsMiniPlayerOpen?.Invoke() ?? false));
            else
            {
                RequestViewerTvSpawn();
                // Placement decisions are drawn by the main window.
                if (viewerTvPlacementPromptRequested)
                    OpenUi();
            }

            return;
        }

        var engine = screenController.Engine;
        if (engine.IsActive)
        {
            engine.DespawnScreen();
        }
        else
            engine.RespawnScreen();
    }
    internal Action? OnViewerTvSpawnRequested { get; set; }
    internal Action? OnMiniPlayerRequested { get; set; }
    internal Action? OnMiniPlayerToggleRequested { get; set; }
    internal Action? OnAudioOnlyRequested { get; set; }
    internal Func<bool>? CanEnterAudioOnly { get; set; }
    internal Func<string>? AudioOnlyUnavailableReason { get; set; }
    internal Action? OnMiniChatRequested { get; set; }
    internal Action? OnMiniChatToggleRequested { get; set; }
    internal Func<bool>? IsMiniPlayerOpen { get; set; }
    internal Func<bool>? IsMiniChatOpen { get; set; }
    internal Func<Version?>? AvailablePluginUpdate { get; set; }
    internal Action? OnPluginUpdateRequested { get; set; }

    private bool viewerTvSpawnPromptRequested;
    private bool viewerTvSpawnPromptShownForCurrentRoom;
    private bool viewerTvSpawnPromptForQuiz;
    private bool viewerTvSpawnPromptForPictionary;
    private bool viewerTvSpawnPromptForRoulette;

    private void NotifyViewerQuizStartedWithoutTv()
    {
        if (stream.Mode != StreamMode.Viewing || ViewerTvEnabled)
            return;

        viewerTvSpawnPromptShownForCurrentRoom = true;
        if (IsOpen)
        {
            viewerTvSpawnPromptForQuiz = true;
            viewerTvSpawnPromptForPictionary = false;
            viewerTvSpawnPromptForRoulette = false;
            viewerTvSpawnPromptRequested = true;
            return;
        }

        viewerTvSpawnPromptRequested = false;
        viewerTvSpawnPromptForQuiz = false;
        viewerTvSpawnPromptForPictionary = false;
        viewerTvSpawnPromptForRoulette = false;
    }

    private void NotifyViewerPictionaryStartedWithoutTv()
    {
        if (stream.Mode != StreamMode.Viewing || ViewerTvEnabled)
            return;

        viewerTvSpawnPromptShownForCurrentRoom = true;
        if (IsOpen)
        {
            viewerTvSpawnPromptForQuiz = false;
            viewerTvSpawnPromptForPictionary = true;
            viewerTvSpawnPromptForRoulette = false;
            viewerTvSpawnPromptRequested = true;
            return;
        }

        viewerTvSpawnPromptRequested = false;
        viewerTvSpawnPromptForQuiz = false;
        viewerTvSpawnPromptForPictionary = false;
        viewerTvSpawnPromptForRoulette = false;
    }

    private void NotifyViewerRouletteStartedWithoutTv()
    {
        if (stream.Mode != StreamMode.Viewing || ViewerTvEnabled) return;
        viewerTvSpawnPromptShownForCurrentRoom = true;
        if (IsOpen)
        {
            viewerTvSpawnPromptForQuiz = false;
            viewerTvSpawnPromptForPictionary = false;
            viewerTvSpawnPromptForRoulette = true;
            viewerTvSpawnPromptRequested = true;
            return;
        }
        viewerTvSpawnPromptRequested = false;
        viewerTvSpawnPromptForRoulette = false;
    }

    internal void DespawnViewerTv(bool stopPlayback = true)
    {
        if (HalloweenActivityActive) { EndHalloween(); return; }
        if (!ViewerTvEnabled)
        {
            return;
        }

        //
        // Match the Watch Party Spawn/Despawn TV button. Keep
        // viewerTvSpawnPromptShownForCurrentRoom true so incoming host states
        // do not immediately ask to spawn the TV again.
        //
        ViewerTvEnabled =
            false;

        viewerTvSpawnPromptRequested =
            false;

        if (stopPlayback)
        {
            video.Stop();
        }
        else
        {
            screenController.Engine.DespawnScreen();
        }
    }

    private void ResetViewerTvSpawnPrompt()
    {
        if (partySyncTvPlacementBeforeTemporaryOverride is { } previousSync)
            partySyncTvPlacement = previousSync;
        partySyncTvPlacementBeforeTemporaryOverride = null;
        hostScreenSyncPause.Reset();
        placementPauseHostId = null;
        viewerTvPlacementPromptRequested = false;
        viewerTvSpawnPromptRequested =
            false;

        viewerTvSpawnPromptShownForCurrentRoom =
            false;

        viewerTvSpawnPromptForQuiz =
            false;
        viewerTvSpawnPromptForPictionary = false;
        viewerTvSpawnPromptForRoulette = false;

        ViewerTvEnabled =
            false;
    }

    // Updated every tick from Plugin.cs (cheap dictionary lookup there) - shown here instead of the
    // raw UserId so players never need to read each other an opaque GUID to join a stream.
    internal string? CurrentDisplayName { get; set; }

    // Also updated every tick from Plugin.cs, same reasoning as CurrentDisplayName - the signed-in
    // account (if any) for whichever character is currently being played, and the live character
    // name/world to sign in with if there isn't one yet.
    private CharacterSession? currentSession;
    internal CharacterSession? CurrentSession
    {
        get => Volatile.Read(ref currentSession);
        set
        {
            var previous = Volatile.Read(ref currentSession);
            Volatile.Write(ref currentSession, value);
            var contentId = Plugin.ReadLocalContentId();
            if (!ReferenceEquals(previous, value) || accountRequestContentId != contentId)
            {
                accountRequestContentId = contentId;
                ResetAccountSettingsRequests();
                ResetObsRequests();
                ResetStreamKeyRequests();
            }
            if (!string.Equals(previous?.AccountId, value?.AccountId, StringComparison.Ordinal) ||
                !string.Equals(previous?.Token, value?.Token, StringComparison.Ordinal))
            {
                CancelPatreonConnection();
                ResetMessagesForSessionChange();
            }
        }
    }
    internal bool SessionValidationInProgress { get; set; }
    internal string? CurrentCharacterName { get; set; }
    internal string? CurrentWorldName { get; set; }
    internal string? CurrentCharacterHomeWorldName { get; set; }
    internal bool CurrentIsLalafell { get; set; }

    internal MainWindow(ScreenController screenController, VideoPlayer video, AetherStreamQueue queue,
        StreamClient stream, Action requestRename, Action<string> saveLocalDisplayName,
        AuthClient authClient, SignInFlow signInFlow,
        FriendsClient friendsClient, ActivityClient activityClient, DmClient dmClient, ReportClient reportClient,
        VenuesClient venuesClient, LiveClient liveClient,
        RoomsClient roomsClient, RadioClient radioClient,
        TwitchClient twitchClient, Crypto.KeyVault keyVault,
        BroadcastAllowanceService broadcastAllowance,
        ActivityPointsService activityPoints,
        Action<CharacterSession?> onSessionChanged)
        : base("AlphaChannel###AlphaChannelMain")
    {
        this.screenController = screenController;
        this.video = video;
        this.personalQueue = queue;

        queueManager =
            new QueueManager(
                Plugin.Cfg,
                queue);

        this.stream = stream;
        this.requestRename = requestRename;
        this.saveLocalDisplayName = saveLocalDisplayName;
        this.authClient = authClient;
        this.signInFlow = signInFlow;
        this.friendsClient = friendsClient;
        this.activityClient = activityClient;
        this.dmClient = dmClient;
        this.reportClient = reportClient;
        this.venuesClient = venuesClient;
        this.liveClient = liveClient;
        this.roomsClient = roomsClient;
        this.radioClient = radioClient;
        this.twitchClient = twitchClient;
        this.keyVault = keyVault;
        this.broadcastAllowance = broadcastAllowance;
        this.activityPoints = activityPoints;
        this.onSessionChanged = onSessionChanged;
        youtubeFeedToken = youtubeFeedLifetime.Token;
        SharedYouTubeCache.Current = new SharedYouTubeCache(() => CurrentSession?.Token,
            Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "youtube-metadata-cache.json"),
            failure: (error, method) => AepLog.YouTubeFetchFailure(YouTubeFetchDiagnostic.Classify(
                YouTubeFetchSection.Background, YouTubeFetchData.FullMetadata, method, error,
                error is PopularWorkerException worker ? worker.HttpStatus : 0)));
        SeedYouTubeMetadataCache();

        stream.OnFriendRequestReceived += _ => { friendsDirty = true; QueueNotificationSound(); };
        stream.OnFriendAccepted += _ => friendsDirty = true;
        stream.OnFriendRemoved += _ => friendsDirty = true;
        stream.OnPresenceUpdate += ApplyPresenceUpdate;
        stream.OnOnlineCount += count => { signedInUsersOnlineCount = count; hasRealtimeOnlineCount = true; };
        stream.OnActivityNew += _ => activityDirty = true;
        stream.OnDmMessage += ApplyIncomingDm;
        stream.OnDmDeleted += ApplyDeletedDm;

        // Borderless dashboard window with normal ImGui resize handles.
        // The full approved layout is the maximum; PreDraw temporarily swaps
        // constraints only while the custom minimized capsule is active.
        //
        // Alpha Channel persists its normal and minimized layouts itself.
        // Prevent ImGui/Dalamud from saving the temporary 276x40 minimized
        // capsule as the main window's size when the plugin unloads.
        //
        Flags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoSavedSettings;

        SizeCondition = ImGuiCond.FirstUseEver;
        Size = WindowSize;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = CurrentMinimumWindowSize,
            MaximumSize = CurrentMaximumWindowSize,
        };

        LoadWindowSizeFromConfig();
        InitializeLocalLibraryRecovery();

        //
        // The welcome-animation size is applied temporarily by PreDraw() while
        // showingFirstLaunch is true. It must not replace userWindowSize or be
        // written into the user's saved configuration.
        //

        stream.OnCapacityBlocked += () =>
        {
            _=Plugin.Framework.RunOnFrameworkThread(()=>
            {
                if(Volatile.Read(ref disposeStarted)==0){proximityJoined=false;OpenUi();}
            });
        };
        stream.OnJoined += () =>
        {

            HandleWatchPartyInviteJoined();
            joinError = null;
            // Fresh joins adopt the host after normal playback is stopped.
            // Reconnecting an existing viewer must preserve their remote activity.
            joinClearQueuePending = viewerJoinPlaybackPrepared;
            viewerJoinPlaybackPrepared = false;
        };
        stream.OnDeclined += reason =>
        {
            viewerJoinPlaybackPrepared = false;
            HandleWatchPartyInviteDeclined(reason);
            joinError = string.IsNullOrEmpty(reason) ? "Could not find that host." : reason;
            if (viewerMode)
            {
                proximityJoined = false;
                joinedHostDisplayName = null;
                viewerMode = false;
                SetMinimized(false);
            }
        };
        stream.OnEnded += () =>
        {
            joinedHostDisplayName =
                null;

            viewerMode =
                false;

            proximityJoined =
                false;

            //
            // StreamClient events arrive from the socket receive thread.
            // Defer video/screen mutation until the framework thread.
            //
            roomEndedPlaybackResetPending =
                true;
        };
        stream.OnPartyModerated += message =>
        {
            var accountToken = CurrentSession?.Token;
            _ = Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0 || CurrentSession?.Token != accountToken || stream.Mode != StreamMode.None) return;
                EndHalloween(publish: false);
                screenController.Engine.DespawnScreen();
                partyModerationNotice = message;
                OpenUi();
            });
        };
        stream.OnViewerPartyEnded += () => Interlocked.Exchange(ref viewerPartyEndedNoticePending, 1);

        maximizedPosition = Plugin.Cfg.MaximizedPosition;
        minimizedPosition = Plugin.Cfg.MinimizedPosition;

        //
        // Only run the welcome presentation until the user has completed it
        // for the first time.
        //
        showingFirstLaunch = !Plugin.Cfg.HasCompletedFirstLaunch;
    }

    // Explicit open actions restore and raise the main window even when it is
    // already open behind another window. Toggle actions decide closure outside here.
    internal void OpenUi()
    {
        SetMinimized(false);
        RequestPosition(maximizedPosition);
        IsOpen = true;
        BringToFront();
    }

    internal void OpenSettingsUi()
    {
        currentPage = HomePage.Settings;
        OpenUi();
    }

    // Full-window join (Home / Party "Join" field). Prefer OpenViewerAndJoin for quick watch.
    internal void OpenPlayerAndJoin(string hostDisplayName)
    {
        if(!EnsureWatchPartyJoinAllowed()) return;
        proximityJoined = false;
        viewerMode = false;
        currentPage = HomePage.Player;
        playerSourceTab = 0;
        OpenUi();
        DoJoin(hostDisplayName);
    }

    // Viewer-only: AlphaChannel required. Capsule UI + ScreenPainter; sync is still /rt URL/position
    // (ApplyRemoteState) — no Penumbra texture pipe, so Lightless alone cannot show the screen.
    // fromProximity: NearbyAutoWatch owns leave-on-range; manual /watch keeps the session until Leave.
    internal void OpenViewerAndJoin(
        string hostLookup, bool fromProximity = false, string? visibleHostName = null)
    {
        if(!EnsureWatchPartyJoinAllowed(silent:fromProximity)) return;
        proximityJoined = fromProximity;
        viewerMode = true;
        currentPage = HomePage.Player;
        playerSourceTab = 0;
        SetMinimized(true);
        RequestPosition(minimizedPosition);
        IsOpen = true;
        DoJoin(hostLookup, visibleHostName: visibleHostName);
    }

    // Typing animation for loading screen
    private static string GetTypewriterText(
        string text,
        double elapsed,
        double startDelay = 0.5,
        double charactersPerSecond = 12)
    {
        if (elapsed < startDelay)
        {
            return string.Empty;
        }

        var visibleCharacters =
            (int)((elapsed - startDelay) * charactersPerSecond);

        return text[..Math.Clamp(
            visibleCharacters,
            0,
            text.Length)];
    }

    // Silent proximity probe — join without opening chrome until ShowProximityViewer (URL confirmed).
    // Joining preserves saved queue entries; normal playback stops before admission.
    internal void BeginProximityJoin(string hostDisplayName)
    {
        if(!EnsureWatchPartyJoinAllowed(silent:true)) return;
        if (hostDisplayName.Length == 0)
        {
            return;
        }

        proximityJoined = true;
        viewerMode = true;
        // Do not touch playerSourceTab / queue — probes must not yank the YouTube search box.
        joinedHostIdentity = null;
        joinedHostDisplayName = hostDisplayName.Trim();
        _ = stream.JoinAsync(hostDisplayName.Trim());
    }

    // True when this client is driving its own screen/queue (hosting or solo play) — auto-watch
    // must not join/clear over the top of that.
    internal bool HasLocalPlayback =>
        stream.Mode == StreamMode.Hosting
        || queue.Current is not null
        || screenController.Engine.IsActive;

    internal void ShowProximityViewer()
    {
        if (!proximityJoined)
        {
            return;
        }

        SetMinimized(true);
        RequestPosition(minimizedPosition);
        IsOpen = true;
    }

    private void RequestLeaveWatchParty()
    {
        //
        // Viewers can leave immediately. Only hosts need confirmation
        // because their action closes the room for everyone.
        //
        if (stream.Mode != StreamMode.Hosting)
        {
            LeaveStream();
            return;
        }

        hostLeaveConfirmationRequested =
            true;
    }

    internal void LeaveStream()
    {
        ForgetEndedPartyQueue();
        hostLeaveConfirmationRequested = false;
        ResetPartyChatState();

        // LeaveAsync clears the room mode before its first await. Do this
        // before stopping local playback so cleanup cannot publish another
        // host state or prevent the leave request from being sent.
        stream.RecordPartyLeaveIntent();
        _ = stream.LeaveAsync();

        viewerMode =
            false;

        proximityJoined =
            false;

        joinedHostDisplayName =
            null;

        StopGameWatchPartyBroadcast();
        StopBrowserWatchPartyBroadcast();
        StopLocalVideoWatchPartyBroadcast();

        ResetNormalWatchPartyPlayback();
        ResetViewerTvSpawnPrompt();
        ResetRouletteLocal();
        ResetBingoLocal();
        ResetBlastPartyLocal();
        ResetSkyRailLocal();
        ResetSlotMachineLocal();
        ResetPixelsLocal();
        ResetLuckyWheelLocal();
        StopRacePreview();

        gameplayStreamOfferDismissed =
            false;

        browserStreamOfferDismissed =
            false;
    }

    internal bool ApplyPendingJoinQueueClear()
    {
        if (!joinClearQueuePending)
        {
            return false;
        }

        joinClearQueuePending =
            false;

        ResetPlaybackForViewerJoin();
        return true;
    }

    internal void ApplyPendingRoomEndedReset()
    {
        if (Interlocked.Exchange(ref viewerPartyEndedNoticePending, 0) != 0)
        {
            viewerPartyEndedNoticeVisible = true;
            OpenUi();
        }
        if (!roomEndedPlaybackResetPending)
        {
            return;
        }

        roomEndedPlaybackResetPending =
            false;

        ResetPartyChatState();

        ResetNormalWatchPartyPlayback();
        ResetViewerTvSpawnPrompt();

        joinedHostDisplayName =
            null;

        viewerMode =
            false;

        proximityJoined =
            false;

        gameplayStreamOfferDismissed =
            false;

        browserStreamOfferDismissed =
            false;
    }

    private void ResetNormalWatchPartyPlayback()
    {
        //
        // A viewer must explicitly choose whether to spawn a TV in the
        // newly joined room.
        //
        ViewerTvEnabled =
            false;

        var engine =
            screenController.Engine;

        // Host coordinates belong to the room being left. The next solo
        // playback or manual TV spawn must place the screen near this player.
        engine.ReleaseWatchPartyScreenPlacement();

        //
        // Do not stop or despawn emulator or embedded-browser content.
        //
        if (engine.IsPlayingGame || engine.IsPlayingBrowser)
        {
            return;
        }

        video.Stop();
        queue.Clear();
    }

    private void ResetPlaybackForViewerJoin()
    {
        //
        // Do this only after the relay confirms the join. A declined join
        // must leave the user's existing solo playback untouched.
        //
        ResetViewerTvSpawnPrompt();

        // Activities are rejected before joining; never tear one down here.
        // Keep saved queues intact while disarming local auto-advance.
        personalQueue.StopPlayback();
    }

    private void StopPlayback()
    {
        video.Stop();
        queue.Clear();
    }

    internal void StopMediaForRestrictedContent(string pictionaryReason = "Pictionary ended because the host entered instanced content.")
    {
        EndHalloween();
        StopStudioBroadcast();
        if (QuizActive)
            StopQuiz();
        else if (PictionaryActive)
            StopPictionaryGame(pictionaryReason);
        else if (PictionaryTeamsActive)
            StopPictionaryTeams();
        else if (RouletteActive)
            StopRouletteGame();
        else if (BingoActive)
            StopBingoGame();
        else if (BlastPartyActive)
            StopBlastParty();
        else if (SkyRailActive)
            StopSkyRailGame();
        else if (pixelsActive)
            StopPixels();
        else if (LuckyWheelActive)
            StopLuckyWheel();
        else if (SlotMachineActive)
            StopSlotMachineGame();
        else if (PokerActive)
            StopPokerTable();
        else if (racePreviewActive)
            StopRacePreview();
        else if (whiteboardActive && whiteboardSessionId is not null && stream.Mode == StreamMode.Hosting)
            SendAndApplyWhiteboardCommand($"X|{whiteboardSessionId}");
        if (whiteboardActive)
            HandleWhiteboardClosed();
        whiteboardSessionId = null;

        StopGameWatchPartyBroadcast();
        StopBrowserWatchPartyBroadcast();
        StopLocalVideoWatchPartyBroadcast();
        djBroadcastingToWatchParty = false;
        queue.StopPlayback();
        screenController.Engine.DespawnScreen();
        ViewerTvEnabled = false;
        ResetViewerTvSpawnPrompt();
    }

    internal string? JoinedHostDisplayName => joinedHostDisplayName;
    internal bool ProximityJoined => proximityJoined;

    internal void ClearProximityJoin() => proximityJoined = false;

    internal void CloseUi()
    {
        PersistPositions();

        //
        // Preserve windowMinimized while closed. OpenUi() calls
        // SetMinimized(false), which then restores userWindowSize on the
        // first expanded frame.
        //
        IsOpen =
            false;
    }

    public override void OnClose()
    {
        youtubeVisibleMetadata.Reset();
        PersistPositions();
    }

    private void RequestPosition(Vector2? target)
    {
        if (target is not { } position)
        {
            return;
        }

        pendingPosition = position;
    }

    private void CaptureCurrentPosition()
    {
        var pos = ImGui.GetWindowPos();
        if (windowMinimized)
        {
            minimizedPosition = pos;
        }
        else
        {
            maximizedPosition = pos;
        }
    }

    // Called from Plugin.cs once per character that hasn't picked a name yet, or after an admin
    // reset - suggested is pre-filled (their real character name) so confirming needs no typing.
    internal void RequestNamePrompt(
      string suggested,
      Action<string> onConfirmed)
    {
        if (namePromptActive)
        {
            return;
        }

        namePromptInput =
            suggested;

        onNameConfirmed =
            onConfirmed;

        namePromptActive =
            true;

        IsOpen =
            true;
    }

    // First launch process
    private void BeginFirstLaunch()
    {
        showingFirstLaunch =
            true;

        firstLaunchStartedAt =
            ImGui.GetTime();

        firstLaunchLastMessageChange =
            ImGui.GetTime();

        firstLaunchMessageIndex =
            0;

        firstLaunchSetupSubmitted =
            false;

        firstLaunchStep = Plugin.Cfg.HasCompletedFirstLaunch ? FirstLaunchStep.Username : FirstLaunchStep.SignInChoice;
        firstLaunchUsingAccount = Plugin.Cfg.HasCompletedFirstLaunch && CurrentSession is not null;
        if (Plugin.Cfg.HasCompletedFirstLaunch)
            namePromptInput = CurrentSession?.DisplayName ?? CurrentDisplayName ?? namePromptInput;
        firstLaunchNameSubmitting = false;
        firstLaunchNameError = null;

        ResumeIncompleteGuestSignup();


        firstLaunchSetupSubmittedAt =
            0d;

        firstLaunchLoadingStartedAt =
            0d;

        firstLaunchFadingOut =
            false;

        firstLaunchFadeAlpha =
            1f;

        previousLoadingCardAreaMinimum =
            null;

        loadingCards.Clear();

        lastBlankCardSpawnedLeft =
            null;

        consecutiveBlankCardsOnSameSide =
            0;

        lastLoadingCardSpawn =
            ImGui.GetTime() -
            1.2d;
    }

    private async Task CompleteFirstLaunchAsync()
    {
        try
        {
            await Task.Delay(
                    TimeSpan.FromSeconds(
                        FirstLaunchLoadingDuration), youtubeFeedToken)
                .ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
            if (Volatile.Read(ref disposeStarted) != 0) return;
            Plugin.Cfg.HasCompletedFirstLaunch =
                true;

            Plugin.Cfg.Save();

            firstLaunchFadingOut =
                true;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (youtubeFeedToken.IsCancellationRequested) { }
        catch (Exception) { AepLog.Warning("[Setup] Could not complete onboarding."); }
    }

    private void UpdateLoadingCards(
    Vector2 areaMin,
    Vector2 areaMax)
    {
        var now =
            ImGui.GetTime();

        //
        // Keep existing particles attached to the splash when the window moves.
        //
        if (previousLoadingCardAreaMinimum is
            { } previousAreaMin)
        {
            var windowMovement =
                areaMin -
                previousAreaMin;

            if (MathF.Abs(
                    windowMovement.X) >
                0.01f ||
                MathF.Abs(
                    windowMovement.Y) >
                0.01f)
            {
                foreach (var existingCard in
                         loadingCards)
                {
                    existingCard.Position +=
                        windowMovement;
                }
            }
        }

        previousLoadingCardAreaMinimum =
            areaMin;

        if (now -
            lastLoadingCardSpawn >
            1.1d)
        {
            var sideLeft =
                Random.Shared.Next(
                    0,
                    100) <
                50;

            //
            // After three consecutive cards on one side, force the next card
            // onto the opposite side before returning to random selection.
            //
            if (lastBlankCardSpawnedLeft is
                { } previousSide &&
                consecutiveBlankCardsOnSameSide >=
                3)
            {
                sideLeft =
                    !previousSide;
            }

            if (lastBlankCardSpawnedLeft ==
                sideLeft)
            {
                consecutiveBlankCardsOnSameSide++;
            }
            else
            {
                lastBlankCardSpawnedLeft =
                    sideLeft;

                consecutiveBlankCardsOnSameSide =
                    1;
            }

            var width =
                Random.Shared.Next(
                    80,
                    150);

            var height =
                Random.Shared.Next(
                    50,
                    90);

            var horizontalInset =
                Random.Shared.Next(
                    20,
                    180);

            var spawnX =
                sideLeft
                    ? areaMin.X +
                      horizontalInset
                    : areaMax.X -
                      width -
                      horizontalInset;

            loadingCards.Add(
                new LoadingCardParticle
                {
                    Position =
                        new Vector2(
                            spawnX,
                            areaMax.Y +
                            height +
                            Ui(20f)),

                    Width =
                        width,

                    Height =
                        height,

                    Speed =
                        Random.Shared.Next(
                            35,
                            80),

                    Drift =
                        Random.Shared.NextSingle() *
                        12f -
                        6f,

                    Alpha =
                        0f,

                    SpawnTime =
                        now,

                    ShimmerOffset =
                        Random.Shared.NextSingle(),

                    IsLeftSide =
                        sideLeft
                });

            lastLoadingCardSpawn =
                now;
        }

        var deltaTime =
            ImGui.GetIO().DeltaTime;

        foreach (var card in
                 loadingCards)
        {
            card.Position.Y -=
                card.Speed *
                deltaTime;

            card.Position.X +=
                MathF.Sin(
                    (float)(
                        now +
                        card.SpawnTime) *
                    0.65f) *
                8f *
                deltaTime;

            var age =
                now -
                card.SpawnTime;

            var fadeIn =
                (float)Math.Clamp(
                    age /
                    1.5d,
                    0d,
                    1d);

            var topFade =
                Math.Clamp(
                    (
                        card.Position.Y -
                        areaMin.Y
                    ) /
                    110f,
                    0f,
                    1f);

            card.Alpha =
                fadeIn *
                topFade;
        }

        loadingCards.RemoveAll(
            card =>
                card.Position.Y +
                card.Height <
                areaMin.Y -
                30f);
    }

    private float GetFirstLaunchLoadingAlpha()
    {
        if (!firstLaunchSetupSubmitted)
        {
            return 0f;
        }

        return (float)Math.Clamp(
            (
                ImGui.GetTime() -
                firstLaunchSetupSubmittedAt
            ) /
            FirstLaunchSetupFadeDuration,
            0d,
            1d);
    }

    private void SubmitFirstLaunchSetup()
    {
        if (firstLaunchSetupSubmitted || guestRegistrationBusy)
        {
            return;
        }

        var selectedTopicCount =
            GetSubscribedTopicCount();

        if (firstLaunchStep != FirstLaunchStep.Topics ||
            selectedTopicCount is < 3 or > 15)
        {
            return;
        }

        // Direct signup creates nothing at the username step. Only the final
        // confirmation may reserve a name and activate its durable credential.
        if (!firstLaunchUsingAccount && CurrentSession is null)
        {
            BeginGuestRegistration(namePromptInput.Trim(), true);
            return;
        }

        Plugin.Cfg.Save();

        firstLaunchSetupSubmitted =
            true;


        firstLaunchSetupSubmittedAt =
            ImGui.GetTime();

        firstLaunchLoadingStartedAt =
            ImGui.GetTime();

        firstLaunchLastMessageChange =
            ImGui.GetTime();

        firstLaunchMessageIndex =
            0;

        //
        // Start the shared cache preparation as soon as the form is submitted.
        // Normal freshness and topic-signature rules still determine whether a
        // YouTube request is actually required.
        //
        browseVideoRequested =
            true;

        topicVideoStartupRequested =
            true;

        homeYouTubeResults =
            null;

        homeYouTubeSelectedTopics.Clear();

        isLoadingHomeYouTube =
            true;

        _ =
            PrepareTopicVideosForLaunchAsync(
                forceRefresh: false);

        _ =
            CompleteFirstLaunchAsync();
    }

    private void SubmitFirstLaunchUsername()
    {
        if (firstLaunchNameSubmitting || !DisplayNameRules.IsValid(namePromptInput)) return;
        var name = namePromptInput.Trim();
        firstLaunchNameError = null;
        if (!firstLaunchUsingAccount)
        {
            // This is a local draft, not a username reservation. Availability
            // is decided atomically by the server when topics are confirmed.
            firstLaunchStep = FirstLaunchStep.Topics;
            return;
        }

        var session = pendingOnboardingSession ?? CurrentSession;
        if (session is null)
        {
            firstLaunchNameError = "Sign-in is not complete. Please try again.";
            return;
        }

        // Replaying setup does not create a pending sign-in session. Keeping
        // the signed-in account's existing name needs no new reservation.
        if (ReferenceEquals(CurrentSession, session) &&
            string.Equals(name, session.DisplayName, StringComparison.Ordinal))
        {
            saveLocalDisplayName(session.DisplayName);
            pendingOnboardingSession = null;
            firstLaunchStep = FirstLaunchStep.Topics;
            return;
        }

        firstLaunchNameSubmitting = true;
        var generation = Volatile.Read(ref signInGeneration);
        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await authClient.UpdateDisplayNameAsync(session.Token, name);
                if (generation != Volatile.Read(ref signInGeneration) ||
                    !ReferenceEquals(pendingOnboardingSession ?? CurrentSession, session)) return;
                if (outcome.Account is not { } account)
                {
                    firstLaunchNameError = outcome.NameTaken
                        ? "That username is already taken. Please choose another."
                        : "Could not save that username. Please try again.";
                    return;
                }

                session.DisplayName = account.DisplayName;
                onSessionChanged(session);
                saveLocalDisplayName(session.DisplayName);
                pendingOnboardingSession = null;
                firstLaunchStep = FirstLaunchStep.Topics;
            }
            catch (Exception)
            {
                AepLog.Warning("[Auth] First-launch username reservation failed.");
                if (generation == Volatile.Read(ref signInGeneration) &&
                    ReferenceEquals(pendingOnboardingSession ?? CurrentSession, session))
                    firstLaunchNameError = "Could not save that username. Please try again.";
            }
            finally
            {
                if (generation == Volatile.Read(ref signInGeneration))
                    firstLaunchNameSubmitting = false;
            }
        });
    }

    private void DrawFirstLaunchDragBlocker(
    string id,
    Vector2 minimum,
    Vector2 maximum,
    Vector2 windowPosition)
    {
        var size =
            maximum -
            minimum;

        if (size.X <= 0f ||
            size.Y <= 0f)
        {
            return;
        }

        ImGui.SetCursorScreenPos(
            minimum);

        ImGui.InvisibleButton(
            id,
            size);

        var mouse =
            ImGui.GetMousePos();

        if (ImGui.IsItemClicked(
                ImGuiMouseButton.Left))
        {
            firstLaunchWindowDragging =
                true;

            firstLaunchWindowDragOffset =
                mouse -
                windowPosition;
        }

        if (firstLaunchWindowDragging &&
            ImGui.IsItemActive() &&
            ImGui.IsMouseDown(
                ImGuiMouseButton.Left))
        {
            var newPosition =
                mouse -
                firstLaunchWindowDragOffset;

            //
            // Move the underlying Alpha Channel window.
            //
            ImGui.SetWindowPos(
                "AlphaChannel###AlphaChannelMain",
                newPosition,
                ImGuiCond.Always);

            //
            // Move the splash overlay with it.
            //
            ImGui.SetWindowPos(
                newPosition,
                ImGuiCond.Always);

            maximizedPosition =
                newPosition;
        }

        if (!ImGui.IsMouseDown(
                ImGuiMouseButton.Left))
        {
            firstLaunchWindowDragging =
                false;
        }
    }

    private void DrawFirstLaunchInteractionShield(
        Vector2 windowPosition, Vector2 windowSize)
    {
        // Signup pages handle their own controls. Only the loading phase reaches
        // this shield, where the complete splash remains a draggable surface.
        DrawFirstLaunchDragBlocker("##firstLaunchFullShield", windowPosition,
            windowPosition + windowSize, windowPosition);
    }
    private void DrawFirstLaunchSpinnerFeatures(
     ImDrawListPtr drawList,
     Vector2 spinnerCenter,
     float loadingAlpha)
    {
        if (loadingAlpha <= 0f)
        {
            return;
        }

        var featureAlpha =
            Math.Clamp(
                loadingAlpha *
                firstLaunchFadeAlpha,
                0f,
                1f);

        var positions =
            new[]
            {
            spinnerCenter +
            UiVec(-220f, -52f),

            spinnerCenter +
            UiVec(220f, -52f),

            spinnerCenter +
            UiVec(-220f, 76f),

            spinnerCenter +
            UiVec(220f, 76f)
            };

        for (var featureIndex = 0;
             featureIndex <
             SplashLoadingFeatures.Length;
             featureIndex++)
        {
            var feature =
                SplashLoadingFeatures[
                    featureIndex];

            var featureCenter =
                positions[
                    featureIndex];

            var iconGlyph =
                feature.Icon.ToIconString();

            Vector2 iconSize;

            using var uiFontScale82549=UiFonts.PushRelative(1.65f);

            using (UiFonts.PushIcon())
            {
                iconSize =
                    ImGui.CalcTextSize(
                        iconGlyph);

                UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                        featureCenter.X -
                        iconSize.X *
                        0.5f,
                        featureCenter.Y), ImGui.GetColorU32(
                        new Vector4(
                            AccentHover.X,
                            AccentHover.Y,
                            AccentHover.Z,
                            featureAlpha)), iconGlyph);
            }

            uiFontScale82549.Dispose();

            var titleSize =
                ImGui.CalcTextSize(
                    feature.Title);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                    featureCenter.X -
                    titleSize.X *
                    0.5f,
                    featureCenter.Y +
                    Ui(34f)), ImGui.GetColorU32(
                    new Vector4(
                        1f,
                        1f,
                        1f,
                        featureAlpha)), feature.Title);
        }
    }

    // First time launch welcome splash screen
    private void DrawFirstLaunchOverlay()
    {
        if (firstLaunchFadingOut)
        {
            firstLaunchFadeAlpha -=
                ImGui.GetIO().DeltaTime /
                1.5f;

            if (firstLaunchFadeAlpha <= 0f)
            {
                firstLaunchFadeAlpha =
                    0f;

                showingFirstLaunch =
                    false;
                if (!Plugin.Cfg.HasCompletedInterfaceSizingSetup)
                    OpenInterfaceSizingComparison();
                return;

                //
                // Keep the window at its current splash dimensions. Setting
                // userResized here would immediately replace them with the saved
                // normal size and make the window visibly shrink as the username
                // prompt appears.
                //
            }
        }

        var windowPos =
            ImGui.GetWindowPos();

        var windowSize =
            ImGui.GetWindowSize();

        var min =
            windowPos;

        var max =
            new Vector2(
                windowPos.X + windowSize.X,
                windowPos.Y + windowSize.Y);

        // -------------------------------------------------
        // Input blocker
        // -------------------------------------------------
        // A real top-level ImGui window is required here.
        // An InvisibleButton inside the main window does not reliably block
        // clicks against the sidebar/content child windows underneath.

        ImGui.SetNextWindowPos(
            windowPos,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            windowSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(0f);

        const ImGuiWindowFlags blockerFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking;

        if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.Begin(
         "##firstLaunchInputBlockerWindow",
         blockerFlags)))
        {
            ImGui.End();
            return;
        }

       

        var drawList =
            ImGui.GetWindowDrawList();

        // Full window themed overlay


        // Full window themed overlay
        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(
                new Vector4(
                    0.03f,
                    0.02f,
                    0.08f,
                    0.96f *
                    firstLaunchFadeAlpha)));

        var cardAreaMin = new Vector2(
            windowPos.X + Ui(20),
            windowPos.Y + Ui(40));

        var cardAreaMax = new Vector2(
            windowPos.X + windowSize.X - Ui(20),
            windowPos.Y + windowSize.Y - Ui(40));

        UpdateLoadingCards(cardAreaMin, cardAreaMax);

        drawList.PushClipRect(
            cardAreaMin,
            cardAreaMax,
            true);

        foreach (var card in
          loadingCards)
        {
            var cardMin =
                card.Position;

            var cardMax =
                card.Position +
                new Vector2(
                    card.Width,
                    card.Height);

            var effectiveAlpha =
                Math.Clamp(
                    card.Alpha *
                    firstLaunchFadeAlpha,
                    0f,
                    1f);

            //
            // Rising fade trail.
            //
            for (var trailIndex = 1;
                 trailIndex <= 3;
                 trailIndex++)
            {
                var trailOffset =
                    trailIndex *
                    24f;

                drawList.AddRectFilled(
                    cardMin +
                    new Vector2(
                        0f,
                        trailOffset),
                    cardMax +
                    new Vector2(
                        0f,
                        trailOffset +
                        Ui(20f)),
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            effectiveAlpha *
                            (
                                0.04f /
                                trailIndex
                            ))),
                    8f);
            }

            //
            // Translucent blank placeholder.
            //
            drawList.AddRectFilled(
                cardMin,
                cardMax,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        effectiveAlpha *
                        0.10f)),
                8f);

            drawList.AddRectFilled(
                cardMin +
                UiVec(10f, 10f),
                new Vector2(
                    cardMax.X -
                    Ui(10f),
                    cardMin.Y +
                    card.Height *
                    0.55f),
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X *
                        0.65f,
                        Accent.Y *
                        0.65f,
                        Accent.Z *
                        0.65f,
                        effectiveAlpha *
                        0.20f)),
                6f);

            var shimmer =
                (float)(
                    (
                        Math.Sin(
                            ImGui.GetTime() *
                            3d +
                            card.ShimmerOffset *
                            10f) *
                        0.5d
                    ) +
                    0.5d);

            var shimmerX =
                cardMin.X +
                card.Width *
                shimmer;

            drawList.AddRectFilled(
                new Vector2(
                    shimmerX -
                    Ui(18f),
                    cardMin.Y),
                new Vector2(
                    shimmerX +
                    Ui(18f),
                    cardMax.Y),
                ImGui.GetColorU32(
                    new Vector4(
                        1f,
                        1f,
                        1f,
                        effectiveAlpha *
                        0.06f)),
                8f);

            drawList.AddRectFilled(
                cardMin +
                new Vector2(
                    Ui(10f),
                    card.Height -
                    Ui(28f)),
                new Vector2(
                    cardMin.X +
                    card.Width *
                    0.75f,
                    cardMin.Y +
                    card.Height -
                    Ui(20f)),
                ImGui.GetColorU32(
                    new Vector4(
                        0.7f,
                        0.7f,
                        0.8f,
                        effectiveAlpha *
                        0.35f)),
                4f);

            drawList.AddRectFilled(
                cardMin +
                new Vector2(
                    Ui(10f),
                    card.Height -
                    Ui(14f)),
                new Vector2(
                    cardMin.X +
                    card.Width *
                    0.5f,
                    cardMin.Y +
                    card.Height -
                    Ui(7f)),
                ImGui.GetColorU32(
                    new Vector4(
                        0.55f,
                        0.55f,
                        0.65f,
                        effectiveAlpha *
                        0.20f)),
                4f);
        }


        drawList.PopClipRect();

        // Keep the original animated panels behind every onboarding page.
        if (!firstLaunchSetupSubmitted && firstLaunchStep is
            FirstLaunchStep.SignInChoice or FirstLaunchStep.CreateAccountChoice or FirstLaunchStep.Username or FirstLaunchStep.Topics)
        {
            DrawAccountWelcomeSplash(windowPos, windowSize);
            ImGui.End();
            return;
        }

        var center =
            new Vector2(
                windowPos.X + windowSize.X / 2,
                windowPos.Y + windowSize.Y / 2);


        // ---------------------------------------------
        // AlphaChannel branding
        // ---------------------------------------------

        var logoTop =
            windowPos.Y + 70;

        if (alphaIconImage is not null)
        {
            var logoSize = Ui(110f);

            drawList.AddImage(
                alphaIconImage.GetWrapOrEmpty()
                    .Handle,
                new Vector2(
                    center.X - logoSize / 2,
                    logoTop),
                new Vector2(
                    center.X + logoSize / 2,
                    logoTop + logoSize));
        }

        var brandText =
            "ALPHA CHANNEL";

        using var uiFontScale93323=UiFonts.PushRelative(1.8f);

        var brandSize =
            ImGui.CalcTextSize(brandText);

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                center.X - brandSize.X / 2,
                logoTop + Ui(130)), ImGui.GetColorU32(
                Vector4.One), brandText);

        uiFontScale93323.Dispose();

        var tagline =
    "Your gateway to shared media in Eorzea.";

        var taglineSize =
            ImGui.CalcTextSize(tagline);

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                center.X - taglineSize.X / 2,
                logoTop + Ui(190)), ImGui.GetColorU32(
                new Vector4(
                    0.65f,
                    0.62f,
                    0.75f,
                    0.85f *
firstLaunchFadeAlpha)), tagline);

        // Branding glow divider
        var lineY =
            logoTop + 220;

        drawList.AddLine(
            new Vector2(
                center.X - Ui(55),
                lineY),
            new Vector2(
                center.X + Ui(55),
                lineY),
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.8f)),
            3f);

        // ---------------------------------------------
        // Phase timing
        // ---------------------------------------------

        var elapsed =
            ImGui.GetTime() -
            firstLaunchStartedAt;

        var loadingElapsed =
            firstLaunchSetupSubmitted
                ? ImGui.GetTime() -
                  firstLaunchLoadingStartedAt
                : 0d;

        string introText;

        if (!firstLaunchSetupSubmitted)
        {
            //
            // Keep the welcome text visible throughout the sign-in, username,
            // and topic steps.
            //
            introText =
                GetTypewriterText(
                    "Welcome to Alpha Channel",
                    Math.Min(
                        elapsed,
                        FirstLaunchWelcomeDuration),
                    1.5,
                    12);
        }
        else
        {
            introText =
                GetTypewriterText(
                    "We're just getting things ready for you...",
                    loadingElapsed,
                    0.5,
                    10);
        }

        var statusText =
            FirstLaunchMessages[
                firstLaunchMessageIndex];

        if (firstLaunchSetupSubmitted &&
            loadingElapsed > 6d &&
            ImGui.GetTime() -
            firstLaunchLastMessageChange >
            3d)
        {
            firstLaunchMessageIndex++;

            if (firstLaunchMessageIndex >=
                FirstLaunchMessages.Length)
            {
                firstLaunchMessageIndex =
                    0;
            }

            firstLaunchLastMessageChange =
                ImGui.GetTime();
        }
        // ---------------------------------------------
        // Draw intro text
        // ---------------------------------------------

        if (!string.IsNullOrEmpty(
                introText))
        {
            using var uiFontScale96759=UiFonts.PushRelative(1.35f);

            var introSize =
                ImGui.CalcTextSize(
                    introText);

            var introY =
                center.Y -
                70f;

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                    center.X -
                    introSize.X * 0.5f,
                    introY), ImGui.GetColorU32(
                    new Vector4(
                        1f,
                        1f,
                        1f,
                        firstLaunchFadeAlpha)), introText);

            uiFontScale96759.Dispose();
        }

        // ---------------------------------------------
        // Draw loading status
        // ---------------------------------------------

        if (firstLaunchSetupSubmitted &&
            loadingElapsed >= 6d)
        {
            var statusSize =
                ImGui.CalcTextSize(
                    statusText);

            var statusAlpha =
                Math.Clamp(
                    (float)(
                        (
                            loadingElapsed -
                            6d
                        ) /
                        0.75d
                    ),
                    0f,
                    1f);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
         center.X -
         statusSize.X *
         0.5f,
         center.Y +
         Ui(250f)), ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        firstLaunchFadeAlpha *
                        statusAlpha)), statusText);
        }

        // ---------------------------------------------
        // Embedded onboarding form
        // ---------------------------------------------


        // ---------------------------------------------
        // TV-style spinner
        // ---------------------------------------------

        var loadingAlpha =
            GetFirstLaunchLoadingAlpha();

        if (loadingAlpha > 0f)
        {
            var spinnerCenter =
                new Vector2(
                    center.X,
                    center.Y + Ui(135f));

            DrawFirstLaunchSpinnerFeatures(
                drawList,
                spinnerCenter,
                loadingAlpha);

            const float radius =
                90f;

            const float thickness =
                8f;

            var time =
                (float)ImGui.GetTime();

            var startAngle =
                time * 3f;

            //
            // Segmented arc with a fading tail.
            //
            const int segments =
                80;

            for (var index = 0;
                 index < segments;
                 index++)
            {
                var progress =
                    index /
                    (float)segments;

                var angle =
                    startAngle +
                    progress *
                    MathF.PI *
                    2f;

                var alpha =
                    MathF.Pow(
                        progress,
                        2.4f) *
                    firstLaunchFadeAlpha *
                    loadingAlpha;

                var point =
                    spinnerCenter +
                    new Vector2(
                        MathF.Cos(
                            angle),
                        MathF.Sin(
                            angle)) *
                    radius;

                drawList.AddCircleFilled(
                    point,
                    thickness,
                    ImGui.GetColorU32(
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            alpha)));
            }

            //
            // Bright moving head.
            //
            var headAngle =
                startAngle;

            var head =
                spinnerCenter +
                new Vector2(
                    MathF.Cos(
                        headAngle),
                    MathF.Sin(
                        headAngle)) *
                radius;

            drawList.AddCircleFilled(
                head,
                14f,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        firstLaunchFadeAlpha *
                        loadingAlpha)));
        }

        //
        // Block the complete splash surface while leaving only the embedded
        // form available for interaction. Every blocked area also acts as a
        // window-dragging surface.
        //
        DrawFirstLaunchInteractionShield(
            windowPos,
            windowSize);

        //
        // Close ##firstLaunchInputBlockerWindow, which was opened at the
        // beginning of this method.
        //
        ImGui.End();
    }


    // PreDraw runs before the main window's Begin().
    // Keep Size populated only while a programmatic size must be applied.
    // Setting Size to null is what stops Dalamud from submitting
    // SetNextWindowSize for the normal user-resizable window.
    // PreDraw runs before the main window's Begin().
    //
    // MinimizedSize and FirstLaunchWindowSize are temporary presentation
    // sizes. userWindowSize is the user's remembered normal size and is
    // the only one that should be persisted.
    private HomePage activityDragBoundsPage;
    private Vector2 activityDragBoundsMin;
    private Vector2 activityDragBoundsMax;
    private bool activityDragBoundsValid;
    private bool activityPanelDragBlocked;

    private void MarkActivityDragBounds()
    {
        activityDragBoundsPage = currentPage;
        activityDragBoundsMin = ImGui.GetCursorScreenPos();
        activityDragBoundsMax = activityDragBoundsMin + ImGui.GetContentRegionAvail();
        activityDragBoundsValid = true;
    }

    public override void PreDraw()
    {
        PrepareInterfaceSizingSetup();
        // Clicking the main window must raise it like other plugin windows.
        // Dialogs maintain their own order through BeginWindowOverlay.
        Flags = MainWindowFocusFlags(Flags);

        // Activity previews and controls may handle dragging without activating an
        // ImGui widget. Keep drags that start in their content from moving the window.
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            activityPanelDragBlocked = false;
        else if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            var mouse = ImGui.GetIO().MousePos;
            activityPanelDragBlocked = activityDragBoundsValid &&
                activityDragBoundsPage == currentPage &&
                mouse.X >= activityDragBoundsMin.X && mouse.X < activityDragBoundsMax.X &&
                mouse.Y >= activityDragBoundsMin.Y && mouse.Y < activityDragBoundsMax.Y;
        }
        if (!windowMinimized && !showingFirstLaunch && activityPanelDragBlocked)
            Flags |= ImGuiWindowFlags.NoMove;
        else
            Flags &= ~ImGuiWindowFlags.NoMove;

        if (windowMinimized)
        {
            Flags |=
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoBackground;

            //
            // Mini Mode uses its own fixed status-bar dimensions.
            //
            SizeConstraints =
                new WindowSizeConstraints
                {
                    MinimumSize = MinimizedSize,
                    MaximumSize = MinimizedSize,
                };

            SizeCondition =
                ImGuiCond.Always;

            Size =
                MinimizedSize;

            return;
        }

        if (showingFirstLaunch)
        {
            Flags &=
                ~ImGuiWindowFlags.NoBackground;

            //
            // The splash uses fixed dimensions. Remove the resize grips entirely
            // so attempting to drag an edge cannot disturb or dismiss onboarding.
            //
            Flags |=
                ImGuiWindowFlags.NoResize;

            var splashSize = InterfaceSizingSplashSize();

            SizeConstraints =
                new WindowSizeConstraints
                {
                    MinimumSize = splashSize,
                    MaximumSize = splashSize,
                };

            SizeCondition =
                ImGuiCond.Always;

            Size =
                splashSize;

            //
            // LoadWindowSizeFromConfig() sets userResized so the saved size is
            // normally applied on the first frame. The splash has already supplied
            // the desired startup dimensions, so consume that pending request here.
            // Otherwise it fires immediately after the splash and shrinks the window.
            //
            userResized =
                false;

            return;
        }

        //
        // The splash has finished, so restore ordinary window resizing.
        //
        Flags &=
            ~(ImGuiWindowFlags.NoResize |
              ImGuiWindowFlags.NoBackground);

        //
        // Normal expanded-window constraints.
        //
        SizeConstraints =
            new WindowSizeConstraints
            {
                MinimumSize = CurrentMinimumWindowSize,
                MaximumSize = CurrentMaximumWindowSize,
            };

        if (userResized)
        {
            userWindowSize =
                ClampWindowSize(
                    userWindowSize);

            SizeCondition =
                ImGuiCond.Always;

            Size =
                userWindowSize;

            userResized =
                false;

            return;
        }

        //
        // Allow normal user resizing when no programmatic size needs to
        // be applied.
        //
        Size =
            null;

        SizeCondition =
            ImGuiCond.None;
    }

    private void OpenPlayerSearch(
        int tab,
        string value)
    {
        if (tab == 2)
        {
            currentPage = HomePage.Twitch;
            twitchHubTab = TwitchHubTab.Discover;
            twitchChannelInput = value;
            if (!isCheckingTwitch && !string.IsNullOrWhiteSpace(value))
            {
                isCheckingTwitch = true; twitchResult = null; twitchError = null;
                twitchResultIsOffline = false; twitchCheckedChannelName = null;
                _ = RunTwitchCheckAsync(value.Trim());
            }
            return;
        }
        currentPage =
            HomePage.Player;

        activePlayerDrawer =
            PlayerDrawer.PlayVideo;

        playerSourceTab =
            tab;

        pendingPlayerSearch =
            value;

        //
        // Searches opened from elsewhere in the plugin should go directly
        // to their requested source instead of showing the landing page.
        //
        showingAddMediaSources =
            false;
    }

    private void EnsureAlphaIconLoaded()
    {
        if (alphaIconImage is not null)
        {
            return;
        }

        var path = Path.Combine(
            Plugin.PluginInterface.AssemblyLocation.DirectoryName!,
            "Assets",
            "alphaicon.png");

        if (File.Exists(path))
        {
            alphaIconImage = Plugin.TextureProvider.GetFromFile(path);
        }
    }

    private ISharedImmediateTexture? GetSidebarLogo()
    {
        EnsureAlphaIconLoaded();
        var theme = Plugin.Cfg.UiTheme;
        if (sidebarLogoTheme == theme) return sidebarLogoImage;
        var filename = theme switch
        {
            UiTheme.Blue => "alphaicon-blue.png",
            UiTheme.Orange => "alphaicon-orange.png",
            UiTheme.Pink => "alphaicon-pink.png",
            UiTheme.Red => "alphaicon-red.png",
            UiTheme.Green => "alphaicon-green.png",
            UiTheme.Gold => "alphaicon-yellow.png",
            _ => "alphaicon.png",
        };
        var path = Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName!, "Assets", filename);
        sidebarLogoImage = File.Exists(path) ? Plugin.TextureProvider.GetFromFile(path) : alphaIconImage;
        sidebarLogoTheme = theme;
        return sidebarLogoImage;
    }

    private void EnsureReactPreviewLoaded()
    {
        if (reactPreviewImage is not null)
        {
            return;
        }

        var path = Path.Combine(
            Plugin.PluginInterface.AssemblyLocation.DirectoryName!,
            "Assets",
            "reactpreview.png");


        if (File.Exists(path))
        {
            reactPreviewImage =
                Plugin.TextureProvider.GetFromFile(path);
        }
    }

    private void EnsureWatchPartyHeaderLoaded()
    {
        if (watchPartyHeaderImage is not null)
        {
            return;
        }

        var path = Path.Combine(
            Plugin.PluginInterface.AssemblyLocation.DirectoryName!,
            "Assets",
            "watchpartyheader.png");

        if (File.Exists(path))
        {
            watchPartyHeaderImage =
                Plugin.TextureProvider.GetFromFile(path);
        }
    }

    private void HandleHomeDragScroll()
    {
        // Mouse wheel scrolling is handled natively by ImGui.
        // This adds click-and-drag scrolling when dragging empty Home background.
        if (!ImGui.IsWindowHovered())
        {
            return;
        }

        // Don't steal drags from buttons, media cards, search inputs, etc.
        if (ImGui.IsAnyItemHovered() ||
            ImGui.IsAnyItemActive())
        {
            return;
        }

        if (!ImGui.IsMouseDragging(
                ImGuiMouseButton.Left,
                4f))
        {
            return;
        }

        var delta = ImGui.GetIO().MouseDelta.Y;

        if (MathF.Abs(delta) < 0.01f)
        {
            return;
        }

        ImGui.SetScrollY(
            Math.Clamp(
                ImGui.GetScrollY() - delta,
                0f,
                ImGui.GetScrollMaxY()));

    }

    public override void Draw()
    {
        ApplyHomeSearchQueueAdditions();
        Colors =
            ThemeCatalog.Get(
                Plugin.Cfg.UiTheme,
                Plugin.Cfg.UiBackground);

        //
        // The splash may return before DrawSidebar(), so its branding image
        // must be loaded independently of the sidebar.
        //
        EnsureAlphaIconLoaded();

        EnsureCustomBackgroundLoaded();
        EnsureReactPreviewLoaded();
        EnsureWatchPartyHeaderLoaded();
        customBackgroundActive = Plugin.Cfg.UiBackground == UiBackground.Custom && customBackground is not null;
        hubColors = new RichPageColors(Colors, Plugin.Cfg.UiBackground, customBackgroundActive);
        // Prime the homepage during onboarding, before subscription/background requests.
        TryRefreshFeaturedSlides();

        UpdateAlphaChat();
        UpdateCloudGroups();

        // Keep the chat feed and unread badge current while another page is
        // open, without changing the user's place in the chat scroll view.
        if (stream.Mode is StreamMode.Hosting or StreamMode.Viewing)
        {
            if (currentPage == HomePage.WatchAlong &&
                partyPanelTab == PartyPanelTab.Chat &&
                DateTime.UtcNow - partyChatLastDrawUtc > TimeSpan.FromSeconds(1))
                partyChatWasAtBottom = false;
            DrainPartyChat();
        }

        using var nativeLayoutScope = new NativeLayoutScope(Plugin.Cfg.NativeUiSizing);
        RefreshLayoutScale(ImGui.GetWindowSize());
        using var nativeFontScale = new NativeUiFontScaleScope(activeNativeUiSizing, layoutScale);
        using var fontScale = new GlobalFontScaleScope(activeNativeUiSizing ? 1f : layoutScale);
        using var managedFonts = UiFonts.Begin(preloadHeadings: true);
        using var theme = new ThemeScope(layoutScale);
        CaptureCurrentPosition();
#if DEBUG
#endif
        if (!windowMinimized)
        {
            var currentSize = ClampWindowSize(ImGui.GetWindowSize());
            var sizeDelta = currentSize - userWindowSize;
            var sizeChanged =
                MathF.Abs(sizeDelta.X) > 1f ||
                MathF.Abs(sizeDelta.Y) > 1f;

            var userDraggingResize =
                ImGui.IsMouseDragging(
                    ImGuiMouseButton.Left,
                    1f) &&
                !ImGui.IsAnyItemActive();

            if (userDraggingResize && sizeChanged)
            {
                // Record the size without setting userResized. Setting that
                // flag here would make PreDraw force the size on the following
                // frame and fight ImGui while the drag is still in progress.
                RememberNormalWindowSize(currentSize);
            }

            if (windowSizeSavePending &&
                !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                Plugin.Cfg.Save();
                windowSizeSavePending = false;
            }
        }

        if (windowMinimized)
        {
            customBackgroundActive = false;
            using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
            {
                DrawMinimizedBar();
            }

            return;
        }

        DrawCustomBackgroundLayer();

        //
        // While onboarding or the splash is visible, do not draw the
        // underlying page at all. Several media cards use direct mouse
        // rectangle checks, so drawing them before an overlay would allow
        // their actions to run even when visually covered.
        //
        if (showingFirstLaunch)
        {
            if (!firstLaunchStarted)
            {
                firstLaunchStarted =
                    true;

                BeginFirstLaunch();
            }

            DrawFirstLaunchOverlay();

            // The same XIVAuth device-flow overlay is used by the first-launch
            // account option; it must be drawn above the welcome overlay.
            DrawSignInOverlay(showCloseButton: false);
            DrawSplashCloseButton();

            return;
        }

        // DrawGlowBorder();

        using var sizingPageInput = new InterfaceSizingPageInputScope(showInterfaceSizingComparison);

        var avail =
            ImGui.GetContentRegionAvail();

        var playbackActive =
    queue.Current is not null ||
    video.IsPlayingLocalVideo;

        if (playbackActive && !playbackWasActive)
        {
            playbackStartedAt = ImGui.GetTime();
        }

        if (!playbackActive && playbackWasActive)
        {
            playbackStoppedAt = ImGui.GetTime();
        }

        playbackWasActive = playbackActive;

        if (playbackActive && !lastPlaybackState)
        {
            playbackStartedAt = ImGui.GetTime();
        }

        lastPlaybackState = playbackActive;

        // Decide before drawing the page so the page and transport use the same
        // layout for this frame, including playback started/stopped by a click.
        var showingPlaybackBar = ShouldShowBottomPlaybackBar &&
            (playbackActive || (ImGui.GetTime() - playbackStoppedAt) < 0.35f);

        var topHeight = MathF.Max(
            avail.Y,
            120f);

        // Keep the sidebar and content on the same layout when the toggle is clicked.
        sidebarCollapsedForFrame = Plugin.Cfg.SidebarCollapsed;
        var centerWidth = MathF.Max(avail.X - SidebarWidth, 0f);


        {


            ImGui.SameLine(0, 0);

            // Native scrollbars reserve their own content gutter and support dragging.
            var contentFlags = PaddedChild & ~ImGuiWindowFlags.NoScrollbar;
            if (currentPage is HomePage.InternetArchive or HomePage.Chats or HomePage.Messages or HomePage.AlphaChat)
                contentFlags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
            // Use the full page width until its content actually needs a scrollbar.
            // Nested scroll regions retain their own scrolling behavior.
            contentFlags = AutomaticPageScrollbars(contentFlags);

            var contentOrigin = ImGui.GetCursorScreenPos();
            sidebarMainWindowSize = ImGui.GetWindowSize();

            using (ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                UiVec(24, 18)))

            using (ImRaii.PushColor(ImGuiCol.ChildBg, SidebarBg))
            using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,
                       UiVec(sidebarCollapsedForFrame ? 8f : 16f, 18f)))
            using (var sidebar = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                "##sidebar",
                new Vector2(SidebarWidth, topHeight),
                false,
                NavPaneFlags)))
            {
                if (sidebar)
                {
                    DrawSidebar();
                }
            }



            // Sidebar right border - subtle theme accent divider
            var sidebarEdge = ImGui.GetItemRectMax().X;
            var dividerList =
    ImGui.GetWindowDrawList();

            var windowPos = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();

            dividerList.AddLine(
                new Vector2(sidebarEdge - 1f, windowPos.Y + Ui(6f)),
                new Vector2(sidebarEdge - 1f, windowPos.Y + windowSize.Y - Ui(6f)),
                ImGui.GetColorU32(new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.14f)),
                1f);

            ImGui.SameLine(0, 0);

            // The transport has its own input window. Keep page widgets and their
            // clipping rectangle above it rather than covering clickable content.
            var pageHeight = showingPlaybackBar
                ? MathF.Min(topHeight, MathF.Max(1f,
                    ImGui.GetWindowPos().Y + ImGui.GetWindowSize().Y - BottomBarHeight - Ui(6f) -
                    ImGui.GetCursorScreenPos().Y))
                : topHeight;

            using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,
                       Vector2.Zero))
            using (ImRaii.PushStyle(ImGuiStyleVar.ScrollbarSize, Ui(14f)))
            using (ImRaii.PushStyle(ImGuiStyleVar.ScrollbarRounding, Ui(7f)))
            using (ImRaii.PushColor(ImGuiCol.ScrollbarBg, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.10f)))
            using (ImRaii.PushColor(ImGuiCol.ScrollbarGrab, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.55f)))
            using (ImRaii.PushColor(ImGuiCol.ScrollbarGrabHovered, AccentHover))
            using (ImRaii.PushColor(ImGuiCol.ScrollbarGrabActive, Accent))
            using (var content = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                "##pageFrame",
                new Vector2(centerWidth, pageHeight),
                false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
            {
                if (content)
                {
                    ImGui.SetScrollX(0f);
                    ImGui.SetScrollY(0f);
                    UiFonts.ResetRelativeScale();
                    var pageOrigin = ImGui.GetWindowPos();
                    sharedHeaderOrigin = pageOrigin;
                    sharedHeaderLayout = Core.SharedHeaderLayout.Calculate(centerWidth / Ui(1f), currentPage == HomePage.Home, DesiredHeaderProfileWidth());
                    var pagePaddingX = Ui(currentPage == HomePage.Browser && screenController.Engine.Browser is not null ? BrowserPageSpacing : 12f);
                    var headerHeight = PageHeaderHeight + Ui(18f);
                    var hasHeader = true;
                    if (hasHeader)
                    {
                        using var headerPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(pagePaddingX, Ui(18f)));
                        using var header = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##pageHeader", new Vector2(centerWidth, headerHeight), false,
                            ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse));
                        if (header)
                        {
                            ImGui.SetScrollY(0f);
                            DrawFixedPageHeader();
                        }
                    }
                    var bodyOffset = hasHeader ? headerHeight + ImGui.GetStyle().ItemSpacing.Y : 0f;
                    ImGui.SetCursorScreenPos(pageOrigin + new Vector2(0f, bodyOffset));
                    using var bodyPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,
                        new Vector2(pagePaddingX, hasHeader ? 0f : Ui(18f)));
                    using var body = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##content", new Vector2(centerWidth, MathF.Max(1f, pageHeight - bodyOffset)),
                        false, contentFlags));
                    if (body)
                    {
                    ResetPageScrollOnNavigation();
                    if (currentPage == HomePage.InternetArchive) ImGui.SetScrollY(0f);
                    UiFonts.ResetRelativeScale();

                    // ---------------------------------------------------------
                    // Page entrance transition
                    // ---------------------------------------------------------

                    if (currentPage != lastAnimatedPage)
                    {
                        lastAnimatedPage = currentPage;
                        pageTransitionStartedAt = ImGui.GetTime();
                    }

                    const float pageTransitionDuration = 0.30f;
                    const float pageTransitionDistance = 18f;

                    float pageTransitionProgress;

                    if (pageTransitionStartedAt < 0d)
                    {
                        pageTransitionProgress = 1f;
                    }
                    else
                    {
                        pageTransitionProgress =
                            Math.Clamp(
                                (float)((ImGui.GetTime() - pageTransitionStartedAt) /
                                        pageTransitionDuration),
                                0f,
                                1f);
                    }

                    // Smooth ease-out rather than a linear movement.
                    var pageTransitionEased =
                        1f -
                        MathF.Pow(
                            1f - pageTransitionProgress,
                            3f);

                    var pageTransitionOffset =
                        pageTransitionDistance *
                        (1f - pageTransitionEased);

                    var pageTransitionAlpha =
                        0.15f +
                        (0.85f * pageTransitionEased);

                    // Start the new page very slightly lower and settle it into place.
                    if (pageTransitionOffset > 0.01f)
                    {
                        ImGui.SetCursorPosY(
                            ImGui.GetCursorPosY() +
                            pageTransitionOffset);
                    }

                    // Fade only the page content.
                    // The scrollbar and player bar remain unaffected.
                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.Alpha,
                        pageTransitionAlpha))
                    {
                        DrawContent();
                        // Tabs can change during DrawContent; queue their reset
                        // before this frame ends rather than retaining old scroll.
                        ResetPageScrollOnNavigation();
                    }

                    if (currentPage == HomePage.Home && !showInterfaceSizingComparison)
                    {
                        HandleHomeDragScroll();
                    }
                    }
                }
            }




            if (showingPlaybackBar)
            {
                //
                // sidebarEdge comes directly from the sidebar child's item rectangle,
                // so it includes the main window's padding and is the authoritative
                // boundary between the sidebar and page content.
                //
                DrawBottomBar(
                    playbackActive,
                    sidebarEdge);
            }

            // Include the header controls in the live sizing preview. The
            // input scope keeps them inactive until the chooser is dismissed.
            DrawWindowControlsStrip();

            if (showInterfaceSizingComparison)
            {
                sizingPageInput.Dispose();
                DrawInterfaceSizingLiveOverlay();
                EnsureTopicVideoStartup();
                return;
            }

            DrawPlaybackErrorToast();
            DrawYouTubeCardMenu();
            DrawSearchQueueMenu();

            //
            // Friend profiles and the DJ connection guide must be drawn after
            // the sidebar, content, scrollbar and divider.
            //

            if (currentPage == HomePage.Profile)
                DrawProfileFriendConfirmation(ImGui.GetWindowPos(), ImGui.GetWindowWidth(), ImGui.GetWindowHeight());
            else if (profilePageOpen)
                CloseProfilePage();
            DrawPatreonPopup();
            DrawArcadeScoreboardPopup();
            DrawDjConnectionGuideOverlay();
            DrawLiveStreamGuideOverlay();
            DrawDjStationEditorOverlay();
            DrawSpotifyLibraryOverlay();
            DrawStudioStopConfirmation();
            DrawMusicDialogs();
            // Image dialogs belong to the whole plugin, not the source page's
            // clipped child window. Their backdrops must also cover the sidebar.
            if (currentPage == HomePage.Player && playerSourceTab == 6)
            {
                DrawImageGalleryOverlay();
                DrawImageHostingGuideOverlay();
            }
            else
            {
                CloseImageGallery();
                imageHostingGuideOpen = false;
            }
            DrawPendingWatchPartyCreationOverlay();
            DrawProfileSocialEditor();
            DrawGuestUpgradePopup();
            DrawAccountAccessPopup();
            DrawWatchPartyLandingOverlay();
            UpdateAlphaChat();
            DrawAlphaChatRosterOverlay();
            DrawAlphaChatRosterOverlay(party:true);
            DrawAlphaChatRosterOverlay(dm:true);
            DrawAlphaChatRulesOverlay();
            DrawAlphaChatSettingsOverlay();
            DrawAlphaChatShareOverlay();
            DrawAlphaChatModerationOverlay();
            DrawAlphaChatReportOverlay();
#if DEBUG
#endif
            DrawPartyAudioSettingsOverlay();
            DrawPartyCountdownOverlay();
            DrawChatsCreationOverlay();
            DrawFriendsAddOverlay();
            DrawEventOverlays();
            DrawFriendsDirectoryOverlays();
            DrawRoomCapacityOverlay();
            DrawPartyModerationOverlays();
            DrawPixelsRankings();
            DrawGamePageDialogs();
            DrawInternetArchiveEpisodeDialog();
            DrawYouTubeSubscriptionsOverlay();
            DrawYouTubeTopicsOverlay();

            //
            // Queue creation/editing uses the same global overlay style
            // as username, controller, and Watch Party password prompts.
            //
            DrawCreateQueuePopup();
            DrawDeleteQueuePopup();
            DrawClearQueuePopup();
            DrawPersonalQueueDialog();

            // Watch-party viewer media decisions must be drawn globally.
            // A request can originate from Home, Add Media, Browse Videos, etc.
            DrawViewerMediaActionPopup();
            DrawVideoChooser();

            //
            // The viewer TV decision must be global because the shared
            // state can arrive while any Watch Party sub-tab is selected.
            //
            DrawViewerTvSpawnPrompt();
            DrawViewerTvPlacementPrompt();
            DrawViewerActivityPrompt();
            //
            // Draw password prompts globally so they appear above the
            // content panel and block interaction underneath them.
            //
            DrawCreateLockedRoomPasswordPopup();

            DrawPartyDirectoryPasswordPopup();
            DrawMessageReportConfirmationOverlay();

            //
            // The splash returns from Draw() before reaching this point.
            // Normal interface startup continues here after it has finished.
            //
            EnsureTopicVideoStartup();

            //
            // Keep the standalone popup for later manual username changes
            // and administrator-triggered resets.
            //
            DrawNamePrompt();
            DrawHostLeaveConfirmationPopup();
            DrawRoomClosingNotice();
            DrawInlineProfileNotices();
            DrawContinueWatchingOverlay();
            DrawHomeRadioSettingsOverlay();
            DrawFriendActionConfirmationOverlay();
            DrawYouTubeSetupNotice();
            DrawYouTubeAgeRestrictionWarning();
            DrawHostTransferNotice();
            DrawViewerPartyEndedNotice();
            DrawRoomRecoveryPrompt();
            DrawSignInOverlay();
            DrawGroupImageViewer();
            DrawChatLinkedImageViewer();
            DrawPluginFilePickers();
        }
    }

    // No title bar means no native window chrome - these controls replace it. Minimize collapses
    // the window down to MinimizedSize (see PreDraw) rather than just hiding content at full size,
    // so it actually reads as "tucked out of the way" instead of an empty box; close just does what
    // /achannel already does (IsOpen = false).
    //
    // Floated in a tiny sibling window just above the neon glow so (1) it sits outside the main
    // chrome and (2) hit-testing works — parent InvisibleButtons under child panes never receive
    // clicks even when painted with the foreground draw list.
    // Chrome is outline-only (no solid fill) so it doesn't read as a double-stacked pill.
    private bool quickVolumeToggleRequested,quickVolumeCloseRequested;

    private void DrawWindowControlsStrip() => DrawSharedHeaderChrome();

    private void DrawQuickVolumeOverlay(Vector2 position)
    {
        const string popupId="##quickVolumeOverlay";
        var close=quickVolumeCloseRequested;
        quickVolumeCloseRequested=false;
        // Open and render in the same parent ID scope, after the header child
        // ends. Native popups handle focus and outside clicks even when the
        // main window is partially outside the viewport.
        if(quickVolumeToggleRequested)
        {
            quickVolumeToggleRequested=false;
            if(ImGui.IsPopupOpen(popupId))close=true;
            else if(!close)ImGui.OpenPopup(popupId);
        }
        if(!ImGui.IsPopupOpen(popupId))return;
        var monitor=DesktopMonitors.ForWindow(ImGui.GetWindowPos(),ImGui.GetWindowSize());
        var panelSize=QuickVolumePanelSize;
        position=monitor.Clamp(position,panelSize);
        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(panelSize,ImGuiCond.Always);
        using (ImRaii.PushColor(ImGuiCol.PopupBg, SidebarBg)
                   .Push(ImGuiCol.Border, Accent))
        using (ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, Ui(10f))
                   .Push(ImGuiStyleVar.WindowPadding, UiVec(14f, 12f))
                   .Push(ImGuiStyleVar.WindowBorderSize, Ui(1f)))
        {
            const ImGuiWindowFlags volumeFlags = ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoDocking;
            if(!UiFonts.Window(ImGui.GetFontSize(),ImGui.BeginPopup(popupId,volumeFlags)))return;
            try
            {
                if(close){ImGui.CloseCurrentPopup();return;}
                DrawQuickVolumeContents();
            }
            finally{ImGui.EndPopup();}
        }
    }

    // Invisible hit target + theme-glow outline (Accent / MagentaGlow from the picked theme).
    private bool DrawWindowControlButton(string id, FontAwesomeIcon icon, float size)
    {
        var origin = ImGui.GetCursorScreenPos();
        ImGui.PushID(id);
        var clicked = ImGui.InvisibleButton("##hit", new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        ImGui.PopID();

        var drawList = ImGui.GetWindowDrawList();
        // Idle: soft MagentaGlow (same family as the outer halo). Hover: Accent rim strength.
        var background = hovered
            ? new Vector4(Accent.X, Accent.Y, Accent.Z, 0.20f)
            : new Vector4(CardBg.X, CardBg.Y, CardBg.Z, 0.95f);

        drawList.AddRectFilled(
            origin,
            origin + new Vector2(size, size),
            ImGui.GetColorU32(background),
            Ui(8f));

        drawList.AddRect(origin, origin + new Vector2(size, size),
            ImGui.GetColorU32(hovered ? Accent : BorderSubtle), Ui(8f));

        using (UiFonts.PushIcon())
        {
            var glyph = icon.ToIconString();
            const float glyphScale = 1.1f;
            var glyphColor = hovered
                ? AccentHover
                : MutedText;
            UiFonts.DrawCenteredIcon(drawList,ImGui.GetFontSize()*glyphScale,origin+new Vector2(size,size)*.5f,ImGui.GetColorU32(glyphColor),glyph);
        }

        return clicked;
    }

    // Compact Mini Mode status bar. It remains draggable and keeps the old
    // capsule's persisted position while exposing useful playback/party shortcuts.
    private void DrawMinimizedBar()
    {
        var origin = ImGui.GetWindowPos();
        var size = MinimizedSize;
        var drawList = ImGui.GetWindowDrawList();
        var rounding = size.Y * 0.5f;

        // Keep all capsule chrome in this window's stacking order. Widen only
        // its drawing clip so the soft rim survives the window's content padding.
        drawList.PushClipRect(origin - UiVec(3f, 3f), origin + size + UiVec(3f, 3f), false);
        try
        {
            drawList.AddRectFilled(
                origin,
                origin + size,
                ImGui.GetColorU32(new Vector4(SidebarBg.X, SidebarBg.Y, SidebarBg.Z, 0.96f)),
                rounding);

            DrawGlowBorder(rounding);
        }
        finally
        {
            drawList.PopClipRect();
        }

        var chipSize = Ui(26f);
        var chipGap = Ui(4f);
        // Snap the controls to the capsule's visual centre. Half-pixel positions
        // make these small circular buttons look uneven after rasterisation.
        var controlsY = MathF.Round(origin.Y + (size.Y - chipSize) * 0.5f);
        var closeOrigin = new Vector2(origin.X + size.X - Ui(8f) - chipSize, controlsY);
        var restoreOrigin = closeOrigin - new Vector2(chipSize + chipGap, 0f);
        var chatOrigin = restoreOrigin - new Vector2(chipSize + chipGap, 0f);
        var audioOnlyOrigin = chatOrigin - new Vector2(chipSize + chipGap, 0f);
        var playerOrigin = audioOnlyOrigin - new Vector2(chipSize + chipGap, 0f);
        var tvOrigin = playerOrigin - new Vector2(chipSize + chipGap, 0f);
        var lightingOrigin = tvOrigin - new Vector2(chipSize + chipGap, 0f);
        var volumeOrigin = lightingOrigin - new Vector2(chipSize + chipGap, 0f);

        var inParty = stream.Mode is StreamMode.Hosting or StreamMode.Viewing;
        var canControlPlayback = !video.ManualTransportLocked && stream.Mode != StreamMode.Viewing &&
                                 video.State is VideoPlaybackState.Playing or VideoPlaybackState.Paused;
        var (position, duration, paused) = video.GetProgress();
        _ = position;
        _ = duration;

        var transportOrigin = volumeOrigin - new Vector2(chipSize + chipGap, 0f);
        var rightContentEdge = canControlPlayback ? transportOrigin.X : volumeOrigin.X;

        // Brand mark.
        using (UiFonts.PushIcon())
        {
            var logo = FontAwesomeIcon.PlayCircle.ToIconString();
            var logoSize = ImGui.CalcTextSize(logo);
            UiFonts.DrawText(drawList, ImGui.GetFontSize(), origin + new Vector2(Ui(14f), (size.Y - logoSize.Y) * 0.5f), ImGui.GetColorU32(Accent), logo);
        }

        var title = GetMiniModeTitle();
        var titleStart = origin.X + 38f;
        var statusReserve = inParty ? 126f : 16f;
        var titleWidth = MathF.Max(80f, rightContentEdge - titleStart - statusReserve);
        title = TruncateToWidth(title, titleWidth);
        var titleSize = ImGui.CalcTextSize(title);
        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(titleStart, origin.Y + (size.Y - titleSize.Y) * 0.5f), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.94f)), title);

        if (inParty)
        {
            var viewerText = $"{stream.Roster.Length + 1} watching";
            using var viewerFont=UiFonts.PushRelative(.90f);
            var viewerSize = ImGui.CalcTextSize(viewerText);
            var viewerX = rightContentEdge - viewerSize.X - 18f;
            drawList.AddCircleFilled(
                new Vector2(viewerX - Ui(8f), origin.Y + size.Y * 0.5f),
                3.5f,
                ImGui.GetColorU32(Good));
            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(viewerX, origin.Y + (size.Y - viewerSize.Y) * 0.5f), ImGui.GetColorU32(MutedText), viewerText);
        }

        if (canControlPlayback &&
            DrawMinimizedRoundButton(
                "##miniTransport",
                transportOrigin,
                chipSize,
                paused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause,
                Accent,
                paused ? "Play" : "Pause"))
        {
            video.Pause(!paused);
        }

        if (DrawMinimizedRoundButton(
                "##miniVolume", volumeOrigin, chipSize,
                Plugin.Cfg.Muted || Plugin.Cfg.Volume == 0 ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp,
                Accent, "Volume"))
            quickVolumeToggleRequested = true;
        var lightingClicked = DrawMinimizedRoundButton(
            "##miniLighting", lightingOrigin, chipSize, FontAwesomeIcon.Lightbulb, Accent, "Lighting",
            active: Plugin.LightingControls.HasCustomAdjustments, highlightOnHover:false);
        if(lightingClicked) quickVolumeCloseRequested=true;

        var tvActive = stream.Mode == StreamMode.Viewing ? ViewerTvEnabled : screenController.Engine.IsActive;
        var tvClicked = DrawMinimizedRoundButton(
            "##miniTv", tvOrigin, chipSize, FontAwesomeIcon.Tv, Accent,
            tvActive ? "Despawn TV" : "Spawn TV", active: tvActive);
        var playerClicked = DrawMinimizedRoundButton(
            "##miniPlayer",
            playerOrigin,
            chipSize,
            FontAwesomeIcon.PlayCircle,
            Accent,
            "Mini Player",
            active: IsMiniPlayerOpen?.Invoke() == true,
            miniPlayer: true);
        var canEnterAudioOnly = CanEnterAudioOnly?.Invoke() == true;
        bool audioOnlyClicked;
        using (UiDisabled.Scope(!canEnterAudioOnly))
            audioOnlyClicked = DrawMinimizedRoundButton(
                "##miniAudioOnly", audioOnlyOrigin, chipSize, FontAwesomeIcon.Headphones, Accent,
                canEnterAudioOnly ? "Continue with audio only" : AudioOnlyUnavailableReason?.Invoke() ?? "Audio only unavailable",
                active: screenController.Engine.AudioOnlyMode);
        var chatClicked = DrawMinimizedRoundButton(
            "##miniChat",
            chatOrigin,
            chipSize,
            FontAwesomeIcon.Comments,
            Accent,
            "Chat",
            active: IsMiniChatOpen?.Invoke() == true);
        var restoreClicked = DrawMinimizedRoundButton(
            "##windowRestore", restoreOrigin, chipSize, FontAwesomeIcon.Expand, Accent,
            "Return to full size window");
        var closeClicked = DrawMinimizedRoundButton(
            "##windowCloseMini", closeOrigin, chipSize, FontAwesomeIcon.Times, Danger,
            "Close");

        var notificationCount = GetPlaybackHudNotificationCount();
        if (notificationCount > 0)
        {
            var badge = notificationCount > 99 ? "99+" : notificationCount.ToString();
            var badgeCenter = restoreOrigin + new Vector2(chipSize - Ui(2f), Ui(3f));
            drawList.AddCircleFilled(badgeCenter, Ui(badge.Length > 2 ? 11f : 8f),
                ImGui.GetColorU32(new Vector4(0.91f, 0.17f, 0.23f, 1f)));
            var badgeFontSize = ImGui.GetFontSize() * 0.78f;
            var badgeSize = ImGui.CalcTextSize(badge) * 0.78f;
            UiFonts.DrawText(drawList, badgeFontSize, badgeCenter - badgeSize * 0.5f, ImGui.GetColorU32(Vector4.One), badge);
        }

        // Drag region covers the title/status area but never any action chip.
        var dragWidth = MathF.Max(rightContentEdge - origin.X - 8f, 0f);
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##minimizedDrag", new Vector2(dragWidth, size.Y));
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);
        }

        if (closeClicked)
        {
            CloseUi();
        }
        else if (tvClicked)
        {
            ToggleTvFromPlaybackHud();
        }
        else if (playerClicked)
        {
            OnMiniPlayerToggleRequested?.Invoke();
        }
        else if (audioOnlyClicked)
        {
            OnAudioOnlyRequested?.Invoke();
        }
        else if (chatClicked)
        {
            OnMiniChatToggleRequested?.Invoke();
        }
        else if (restoreClicked ||
                 (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)))
        {
            SetMinimized(false);
        }

        var popupSize = QuickVolumePanelSize;
        var monitor=DesktopMonitors.ForWindow(origin,size);
        var popupY = origin.Y + size.Y + Ui(7f);
        if (popupY + popupSize.Y > monitor.Origin.Y + monitor.Size.Y)
            popupY = origin.Y - popupSize.Y - Ui(7f);
        var popupPosition = monitor.Clamp(
            new Vector2(volumeOrigin.X - popupSize.X * 0.5f + chipSize * 0.5f, popupY),popupSize);
        if (IsOpen && windowMinimized)
        {
            DrawHeaderLightingPanel?.Invoke(lightingOrigin,lightingClicked);
            DrawQuickVolumeOverlay(popupPosition);
        }
    }

    internal string GetMiniModeTitle()
    {
        if (stream.Mode == StreamMode.Viewing &&
            Plugin.IsLiveBroadcastUrl(stream.CurrentRoomState?.Url ?? video.CurrentMediaUrl ?? string.Empty))
            return "Live Broadcast";

        var title = queue.Current?.Title;

        if (string.IsNullOrWhiteSpace(title))
        {
            title = stream.CurrentRoomState?.MediaTitle;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = screenController.Engine.GetMediaTitle();
        }

        if (video.IsAudioOnly) title = GetRadioDisplayTitle(title);

        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        if (stream.Mode == StreamMode.Viewing &&
            joinedHostDisplayName is { Length: > 0 } host)
        {
            return $"Watching {host}";
        }

        return inPartyLabel();

        string inPartyLabel() =>
            stream.Mode == StreamMode.Hosting
                ? "Your Watch Party"
                : "Alpha Channel Mini";
    }

    internal string GetMiniPartyTitle()
    {
        if (stream.Mode == StreamMode.Viewing &&
            joinedHostDisplayName is { Length: > 0 } host)
        {
            return $"{host}'s Watch Party";
        }

        return stream.Mode == StreamMode.Hosting
            ? "Your Watch Party"
            : "Watch Party Chat";
    }

    internal void OpenFullWatchPartyChat()
    {
        currentPage = HomePage.WatchAlong;
        partyPanelTab = PartyPanelTab.Chat;
        OpenUi();
    }

    private static string TruncateToWidth(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width)
        {
            return text;
        }

        const string ellipsis = "…";
        while (text.Length > 1 &&
               ImGui.CalcTextSize(text + ellipsis).X > width)
        {
            text = text[..^1];
        }

        return text + ellipsis;
    }

    private void DrawGuestHeaderSignInButton()
    {
        ImGui.SameLine(0f, Ui(6f));
        using (UiFonts.PushIcon())
        using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero)
            .Push(ImGuiCol.ButtonHovered, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.25f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(3f, 0f)))
        {
            if (ImGui.SmallButton(FontAwesomeIcon.SignInAlt.ToIconString() + "##guestHeaderSignIn"))
            {
                currentPage = HomePage.Settings;
                settingsTab = SettingsTab.Account;
            }
        }
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Sign in");
    }

    private bool DrawMinimizedRoundButton(
        string id, Vector2 origin, float size, FontAwesomeIcon icon, Vector4 hoverColor,
        string tooltip, bool active = false, bool highlightOnHover = true, bool miniPlayer = false)
    {
        ImGui.SetCursorScreenPos(origin);
        ImGui.PushID(id);
        var clicked = ImGui.InvisibleButton("##ctl", new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();

        if (hovered)
        {
            UiFonts.SetTooltip(tooltip);
        }

        if (miniPlayer)
        {
            var color = ImGui.GetColorU32(active || (hovered && highlightOnHover)
                ? hoverColor : new Vector4(1f, 1f, 1f, 0.78f));
            MiniPlayerIcon.Draw(drawList, origin, size, color);
        }
        else using (UiFonts.PushIcon())
        {
            var text = icon.ToIconString();
            // Font Awesome glyphs have different internal bearings, and drawing
            // them at the full UI font size makes the wider symbols appear off
            // centre. Keep a consistent visual footprint inside every chip and
            // compensate for the icon font's high baseline.
            const float glyphScale = 0.82f;
            var glyphSize = ImGui.CalcTextSize(text) * glyphScale;
            var opticalOffset = icon switch
            {
                FontAwesomeIcon.Tv => UiVec(2f, 0.5f),
                FontAwesomeIcon.Comments => UiVec(2f, 0.5f),
                FontAwesomeIcon.Expand => UiVec(2.5f, 1f),
                FontAwesomeIcon.Times => UiVec(1.5f, 0.5f),
                FontAwesomeIcon.Play => new Vector2(0.5f, 1f),
                FontAwesomeIcon.Pause => new Vector2(0f, 1f),
                _ => new Vector2(0f, 1f),
            };
            UiFonts.DrawIcon(drawList, ImGui.GetFontSize() * glyphScale, origin +
                new Vector2(size, size) * 0.5f -
                glyphSize * 0.5f +
                opticalOffset, ImGui.GetColorU32(active || (hovered && highlightOnHover) ? hoverColor : new Vector4(1f, 1f, 1f, 0.78f)), text);
        }

        ImGui.PopID();
        return clicked;
    }

    private void DrawFixedPageHeader()
    {
        // Still parked from launch cut — bounce home if somehow selected.
        if (currentPage is HomePage.GoLive)
        {
            currentPage = HomePage.Home;
        }

        switch (currentPage)
        {
            case HomePage.Home:
                DrawHomePageHeader();
                break;
            case HomePage.Music:
                PageTitleBack("Music / DJ", "Listen, broadcast and build your music scene.", HomePage.Home);
                break;
            case HomePage.Player:
                PageTitle(
                    "Media Player",
                    "Watch on your TV or mini player.");
                break;
            case HomePage.VideoGrid:
                PageTitle(
                    "YouTube",
                    "Watch YouTube solo or with friends");
                break;
            case HomePage.Twitch:
                PageTitle("Twitch", "Find a live channel or share your own.");
                break;
            case HomePage.PlaySnes:
                PageTitle(
                    "Retro Games",
                    "Play classic games solo or with friends.");
                break;
            // Alpha Channel embedded-browser integration.
            case HomePage.Browser:
                PageTitle("Browser", "Browse the web and broadcast to your party");
                break;
            case HomePage.Dailymotion:
                PageTitle("Dailymotion", "Watch videos solo or with friends.");
                break;
            case HomePage.InternetArchive:
                PageTitle("Internet Archive", "Discover films, television and archive footage.");
                break;
            case HomePage.UnifiedSearch:
                PageTitle("Search Videos", "Search YouTube, Dailymotion and the Internet Archive.");
                break;
#if DEBUG
#endif
            case HomePage.Moderator:
                PageTitle("Moderator Panel", "Help keep Alpha Channel welcoming.");
                break;
            case HomePage.WatchAlong:
                PageTitle(
                    "Watch Party",
                    "Watch, listen and play together.");
                break;
            case HomePage.Activities:
                currentPage = HomePage.Arcade;
                goto case HomePage.Arcade;
            case HomePage.Arcade:
                PageTitleBack("The Arcade", "Play, compete and create together.", HomePage.Home);
                break;
            case HomePage.Whiteboard:
                PageTitleBack(
                    "Canvas",
                    "Draw together on the Watch Party TV.",
                    activityLibraryReturnPage);
                break;
            case HomePage.Pictionary:
                PageTitleBack(
                    "Pictionary",
                    "Draw, guess, and race to the top.",
                    activityLibraryReturnPage);
                break;
            case HomePage.PictionaryTeams:
                PageTitleBack("Pictionary Teams", "Two teams draw and guess side by side.", activityLibraryReturnPage);
                break;
            case HomePage.Quiz:
                PageTitleBack(
                    "Quiz",
                    QuizActive ? "Quiz controls and live TV scores." : "Choose your rules and play together.",
                    activityLibraryReturnPage);
                break;
            case HomePage.Roulette:
                PageTitleBack(
                    "Roulette",
                    RouletteActive ? "Place bets on the page or TV." : "Choose standard rules or create a custom table.",
                    activityLibraryReturnPage);
                break;
            case HomePage.RacePreview:
                PageTitleBack("Choco RaceBets", "Place bets and cheer your choco to the finish.", activityLibraryReturnPage);
                break;
            case HomePage.Bingo:
                PageTitleBack("Bingo", "Play on the TV or in this window.", activityLibraryReturnPage);
                break;
            case HomePage.BlastParty:
                PageTitleBack("Blast Party", "Click, blast and battle together.", activityLibraryReturnPage);
                break;
            case HomePage.SkyRail:
                PageTitleBack("Air Force EX", "Aim and fire on the TV or in this window.", activityLibraryReturnPage);
                break;
            case HomePage.Pixels:
                PageTitleBack("Pixels", "Leave your mark on our shared canvas.", activityLibraryReturnPage);
                break;
            case HomePage.LuckyWheel:
                PageTitleBack("Lucky Wheel", "Create a wheel and spin together.", activityLibraryReturnPage);
                break;
            case HomePage.SlotMachine:
                PageTitleBack("Slot Machine", "Insert a coin and give it a spin.", activityLibraryReturnPage);
                break;
            case HomePage.Poker:
                PageTitleBack("Poker", "Play Texas Hold'em on the TV or in this window.", activityLibraryReturnPage);
                break;
            case HomePage.PartyDirectory:
                PageTitleBack(
                    "Party Directory",
                    "Find Watch Parties and people to watch with.",
                    HomePage.WatchAlong);
                break;
            case HomePage.AlphaChat:
                PageTitle("Chats", "Conversations and community.");
                break;
            case HomePage.Screen:
                PageTitle("Screen", "Place, customise and style your TV.");
                break;
            case HomePage.Profile:
                PageTitleBack("Profile", "Get to know your friends.", profileReturnPage);
                break;
            case HomePage.Friends:
                PageTitle("Friends", "People you can invite and join.");
                break;
            case HomePage.FriendsDirectory:
                PageTitleBack("Find Friends", "Meet people to watch, play and listen with.", HomePage.Friends);
                break;
            case HomePage.Chats:
                PageTitle("Chats", "Conversations and community.");
                break;
            case HomePage.Events:
                PageTitle("Events", "Make plans. Find your people. Watch together.");
                break;
            case HomePage.Messages:
                PageTitle("Chats", "Conversations and community.");
                break;
            case HomePage.Settings:
                PageTitle("Settings", "Account, appearance, and plugin preferences.");
                break;
            case HomePage.Help:
                PageTitleBack(
                    "Help & FAQ",
                    "Answers, guides and troubleshooting.",
                    helpReturnPage);
                break;
        }
    }

    private void DrawContent()
    {
        BeginYouTubeCardMetadataView();
        // Still parked from launch cut — bounce home if somehow selected.
        if (currentPage is HomePage.GoLive)
        {
            currentPage = HomePage.Home;
        }

        switch (currentPage)
        {
            case HomePage.Home:
                DrawHome();
                break;
            case HomePage.Music:
                DrawMusicHubPage();
                break;
            case HomePage.Player:
                DrawPlayerPage();
                break;
            case HomePage.VideoGrid:
                DrawYouTubeBrowsePage();
                break;
            case HomePage.Twitch:
                DrawTwitchHubPage();
                break;
            case HomePage.PlaySnes:
                DrawRetroHubPage();
                break;
            // Alpha Channel embedded-browser integration.
            case HomePage.Browser:
                DrawBrowserPage();
                break;
            case HomePage.Dailymotion:
                DrawDailymotionPage();
                break;
            case HomePage.InternetArchive:
                DrawInternetArchivePage();
                break;
            case HomePage.UnifiedSearch:
                DrawUnifiedSearchPage();
                break;
#if DEBUG
#endif
            case HomePage.Moderator:
                DrawModeratorPanel();
                break;
            case HomePage.WatchAlong:
                DrawWatchPartyPage();
                break;
            case HomePage.Activities:
                currentPage = HomePage.Arcade;
                goto case HomePage.Arcade;
            case HomePage.Arcade:
                DrawArcadePage();
                break;
            case HomePage.Whiteboard:
                MarkActivityDragBounds();
                DrawWhiteboardPage();
                break;
            case HomePage.Pictionary:
                MarkActivityDragBounds();
                DrawPictionaryPage();
                break;
            case HomePage.PictionaryTeams:
                MarkActivityDragBounds();
                DrawPictionaryTeamsPage();
                break;
            case HomePage.Quiz:
                MarkActivityDragBounds();
                DrawQuizPage();
                break;
            case HomePage.Roulette:
                MarkActivityDragBounds();
                DrawRoulettePrototypePage();
                break;
            case HomePage.RacePreview:
                MarkActivityDragBounds();
                DrawRacePreviewPage();
                break;
            case HomePage.Bingo:
                MarkActivityDragBounds();
                DrawBingoPage();
                break;
            case HomePage.BlastParty:
                MarkActivityDragBounds();
                DrawBlastPartyPage();
                break;
            case HomePage.SkyRail:
                MarkActivityDragBounds();
                DrawSkyRailPage();
                break;
            case HomePage.Pixels:
                DrawPixelsPage();
                break;
            case HomePage.LuckyWheel:
                DrawLuckyWheelPage();
                break;
            case HomePage.SlotMachine:
                MarkActivityDragBounds();
                DrawSlotMachinePage();
                break;
            case HomePage.Poker:
                MarkActivityDragBounds();
                DrawPokerPage();
                break;
            case HomePage.PartyDirectory:
                DrawPartyDirectoryPage();
                break;
            case HomePage.AlphaChat:
                DrawChatsHub();
                break;
            case HomePage.Screen:
                DrawScreenControls();
                break;
            case HomePage.Profile:
                if (currentPage == HomePage.Profile) DrawProfilePage();
                break;
            case HomePage.Friends:
                DrawFriends();
                break;
            case HomePage.FriendsDirectory:
                DrawFriendsDirectoryPage();
                break;
            case HomePage.Chats:
                DrawChatsHub();
                break;
            case HomePage.Events:
                DrawEventsPage();
                break;
            case HomePage.Messages:
                DrawChatsHub();
                break;
            case HomePage.Settings:
                DrawSettings();
                break;
            case HomePage.Help:
                DrawHelpPage();
                break;
        }
    }

    // Keep the halo in the window draw list so overlapping windows cover it.
    // The capsule caller supplies a drawing clip with room for the soft rim.
    // roundingOverride: capsule uses half-height; full window uses the default 16.
    private void DrawGlowBorder(float rounding = 16f)
    {
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();

        // Very restrained violet halo — enough to separate the window from the game,
        // but no longer reads as an RGB/neon frame.
        for (var layer = 3; layer >= 1; layer--)
        {
            var outset = 0f;
            var alpha = 0.018f + (4 - layer) * 0.012f;

            drawList.AddRect(
                min - new Vector2(outset, outset),
                max + new Vector2(outset, outset),
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        alpha)),
                rounding + outset * 0.45f,
                ImDrawFlags.None,
                1.5f + layer * 0.25f);
        }

        // Thin muted violet perimeter.
        drawList.AddRect(
            min + new Vector2(0.5f, 0.5f),
            max - new Vector2(0.5f, 0.5f),
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.48f)),
            rounding,
            ImDrawFlags.None,
            1.1f);

        // Barely-visible inner edge gives the frame a little depth without introducing cyan.
        if (rounding < max.Y * 0.45f)
        {
            drawList.AddRect(
                min + UiVec(2f, 2f),
                max - UiVec(2f, 2f),
                ImGui.GetColorU32(
                    new Vector4(
                        1f,
                        1f,
                        1f,
                        0.035f)),
                MathF.Max(4f, rounding - 2f),
                ImDrawFlags.None,
                1f);
        }
    }

    private void DrawSidebar()
    {
        // The sidebar is the first area converted to the experimental UHD
        // scale. Setting its window scale here also lets ordinary ImGui text
        // and the nested navigation child inherit the larger font metrics.
        UiFonts.ResetRelativeScale();

        //
        // =========================================================
        // FIXED SIDEBAR BRANDING
        // =========================================================
        //
        // The Alpha Channel logo and wordmark always remain visible
        // at the top of the sidebar.
        //

        var brandOrigin =
          ImGui.GetCursorScreenPos();

        // Local X position where the sidebar's usable content begins.
        // This includes the child window's left padding.
        var sidebarContentStartX =
            ImGui.GetCursorPosX();

        var sidebarWidth =
            ImGui.GetContentRegionAvail().X;

        var drawList =
            ImGui.GetWindowDrawList();

        var mark = Ui(sidebarCollapsedForFrame ? 32f : 42f);

        UiDecorationRenderer.Draw(drawList,ImGui.GetWindowPos(),new Vector2(ImGui.GetWindowSize().X,Ui(96f)),
            Plugin.Cfg.UiDecorationTheme,Colors,Ui(1f),UiDecorationRenderer.Region.Brand,sidebarCollapsedForFrame);


        EnsureAlphaIconLoaded();


        var centeredLogo =
            brandOrigin +
            new Vector2(
                (sidebarWidth - mark) * 0.5f,
                0f);


        var alphaWrap =
            GetSidebarLogo()?
                .GetWrapOrDefault();


        if (alphaWrap is not null)
        {
            drawList.AddImage(
                alphaWrap.Handle,
                centeredLogo,
                centeredLogo +
                new Vector2(
                    mark,
                    mark),
                Vector2.Zero,
                Vector2.One,
                ImGui.GetColorU32(
                    Vector4.One));
        }


        ImGui.Dummy(
            new Vector2(
                0f,
                mark));


        var toggleOrigin = sidebarCollapsedForFrame
            ? brandOrigin + new Vector2((sidebarWidth - Ui(28f)) * 0.5f, mark + Ui(8f))
            : brandOrigin + new Vector2(sidebarWidth - Ui(28f), 0f);
        ImGui.SetCursorScreenPos(toggleOrigin);
        DrawSidebarCollapseToggle();
        ImGui.SetCursorScreenPos(brandOrigin + new Vector2(0f,
            sidebarCollapsedForFrame ? mark + Ui(44f) : mark + ImGui.GetStyle().ItemSpacing.Y));

        if (!sidebarCollapsedForFrame)
        {
            const string brandText = "ALPHA CHANNEL";
            var brandScale = 1.25f;
            using var uiFontScale169921=UiFonts.PushRelative(brandScale);
            var textWidth = ImGui.CalcTextSize(brandText).X;
            if (textWidth > sidebarWidth && textWidth > 1f)
            {
                brandScale *= sidebarWidth / textWidth;
                using var uiFontScale170161=UiFonts.PushRelative(brandScale);
                textWidth = ImGui.CalcTextSize(brandText).X;
            }
            ImGui.SetCursorPosX(sidebarContentStartX + MathF.Max(0f, (sidebarWidth - textWidth) * 0.5f));
            ImGui.TextColored(Hex(0xD5DCE8), brandText);
            uiFontScale169921.Dispose();
        }
        ImGui.SetCursorPosX(sidebarContentStartX);


        //
        // Refresh sidebar data before entering the scrolling child.
        //

        if (CurrentSession is { } sidebarSession &&
            friendsDirty &&
            !friendsLoading)
        {
            RefreshFriends(
                sidebarSession.Token);
        }


        //
        // =========================================================
        // SCROLLABLE NAVIGATION AREA
        // =========================================================
        //
        // Everything between the fixed logo and fixed support row
        // lives inside this child.
        //
        // NoScrollbar hides the scrollbar chrome.
        //
        // We intentionally DO NOT use NoScrollWithMouse here, so
        // the mouse wheel can scroll this region whenever its
        // contents are taller than the available height.
        //

        var availableUpdate = AvailablePluginUpdate?.Invoke();
        // Reserve the fixed utility buttons above the existing support buttons.
        var utilityFooterHeight = sidebarCollapsedForFrame
            ? Ui(64f) + ImGui.GetStyle().ItemSpacing.Y * 2f
            : Ui(32f) + ImGui.GetStyle().ItemSpacing.Y;
        var supportFooterHeight = utilityFooterHeight + (sidebarCollapsedForFrame
            ? Ui(62f) + ImGui.GetStyle().ItemSpacing.Y : Ui(34f)) + (availableUpdate is null
            ? 0f : Ui(30f) + ImGui.GetStyle().ItemSpacing.Y);


        var availableMiddleHeight =
            ImGui.GetContentRegionAvail().Y;


        var navigationHeight =
            MathF.Max(
                1f,
                availableMiddleHeight -
                supportFooterHeight);

        var hideSidebarHeadings = sidebarCollapsedForFrame;


        const ImGuiWindowFlags navigationFlags =
            ImGuiWindowFlags.AlwaysUseWindowPadding |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoSavedSettings;


        using (ImRaii.PushStyle(
            ImGuiStyleVar.WindowPadding,
            Vector2.Zero))
        using (var navigation = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
            "##sidebarNavigation",
            new Vector2(
                -1f,
                navigationHeight),
            false,
            navigationFlags)))
        {
            if (navigation)
            {
                UiDecorationRenderer.Draw(ImGui.GetWindowDrawList(),ImGui.GetWindowPos(),ImGui.GetWindowSize(),
                    Plugin.Cfg.UiDecorationTheme,Colors,Ui(1f),UiDecorationRenderer.Region.Navigation,sidebarCollapsedForFrame);
                // Keep compact spacing consistent at every window height.
                using var navigationSpacing = ImRaii.PushStyle(
                    ImGuiStyleVar.ItemSpacing,
                    UiVec(12f, sidebarCollapsedForFrame ? 1f : 3f));

                DrawNavItem(
                    HomePage.Home,
                    FontAwesomeIcon.Home,
                    "Home");



                DrawSidebarDivider();
                DrawNavGroup("TOGETHER", hideSidebarHeadings);

                DrawNavItem(
                    HomePage.WatchAlong,
                    FontAwesomeIcon.Users,
                    "Watch Party",
                    partyChatUnreadCount,
                    notificationBadge: true);

                DrawNavItem(
                     HomePage.PartyDirectory,
                     FontAwesomeIcon.Search,
                     "Find Parties");

                DrawNavItem(
                    HomePage.Friends,
                    FontAwesomeIcon.UserFriends,
                    "Friends",
                    FriendsSidebarNotificationCount(),
                    notificationBadge: true,
                    forceActive: currentPage is HomePage.Profile or HomePage.FriendsDirectory);

                DrawNavItem(
                    HomePage.Chats,
                    FontAwesomeIcon.Comments,
                    "Chats",
                    badgeCount: ChatsHubNotificationCount(), notificationBadge: true,
                    forceActive: currentPage is HomePage.AlphaChat or HomePage.Messages or HomePage.Chats,
                    showUnreadDot: (alphaChatUnread.Count > 0 || alphaChat.BackgroundUnread) && alphaChatUnreadMentions.Count == 0,
                    populationCount: null);

                DrawNavItem(HomePage.Events, FontAwesomeIcon.CalendarAlt, "Events",
                    badgeCount:events.Where(e=>!e.Cancelled&&e.HostAccountId==CurrentSession?.AccountId&&e.HostState=="pending"&&e.EndAt>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).GroupBy(e=>e.SeriesId??e.Id).Count(),notificationBadge:true);

                DrawSidebarDivider();
                DrawNavGroup("DISCOVER", hideSidebarHeadings);

                DrawNavItem(
                    HomePage.VideoGrid,
                    FontAwesomeIcon.PlayCircle,
                    "YouTube");

                DrawNavItem(HomePage.Twitch, FontAwesomeIcon.BroadcastTower, "Twitch");

                DrawNavItem(
                    HomePage.InternetArchive,
                    FontAwesomeIcon.Film,
                    "Internet Archive");

                DrawNavItem(HomePage.Dailymotion, FontAwesomeIcon.Video, "Dailymotion");

                DrawNavItem(
                    HomePage.Music,
                    FontAwesomeIcon.Music,
                    "Music / DJ",
                    showRunningDot: StudioOwnsPlaybackControls);

                DrawNavItem(
                    HomePage.Arcade,
                    FontAwesomeIcon.Dice,
                    "The Arcade",
                    forceActive: activityLibraryReturnPage == HomePage.Arcade &&
                        currentPage is (HomePage.Whiteboard or HomePage.Pictionary or HomePage.PictionaryTeams or HomePage.Quiz or HomePage.Roulette or HomePage.Bingo or HomePage.BlastParty or HomePage.SkyRail or HomePage.SlotMachine or HomePage.LuckyWheel or HomePage.Pixels or HomePage.Poker or HomePage.RacePreview),
                    showRunningDot: ActiveActivity != ActiveActivityKind.None);

                DrawNavItem(
                    HomePage.PlaySnes,
                    FontAwesomeIcon.Gamepad,
                    "Retro Games",
                    showRunningDot: screenController.Engine.IsPlayingGame);

                DrawNavItem(
                    HomePage.Browser,
                    FontAwesomeIcon.Globe,
                    "Browser",
                    showRunningDot: screenController.Engine.IsPlayingBrowser);

                DrawSidebarDivider();

                var playerLabel = queue.Entries.Count > 0
                    ? $"Media Player ({queue.Entries.Count})" : "Media Player";
                DrawNavItem(HomePage.Player, FontAwesomeIcon.Play, playerLabel);
                DrawSidebarDivider();

                var decorationSpace=ImGui.GetContentRegionAvail().Y;
                if(decorationSpace>=Ui(24f))
                {
                    var decorationHeight=MathF.Min(decorationSpace,Ui(85f));
                    var decorationOrigin=new Vector2(ImGui.GetWindowPos().X,
                        ImGui.GetCursorScreenPos().Y+decorationSpace-decorationHeight);
                    UiDecorationRenderer.Draw(ImGui.GetWindowDrawList(),decorationOrigin,
                        new Vector2(ImGui.GetWindowSize().X,decorationHeight),Plugin.Cfg.UiDecorationTheme,
                        Colors,Ui(1f),UiDecorationRenderer.Region.Footer,sidebarCollapsedForFrame);
                }

                if (CurrentSession is { } dmSidebarSession &&
                    conversationsDirty &&
                    !conversationsLoading &&
                    DateTime.UtcNow >= conversationsNextRetryUtc)
                {
                    RefreshConversations(
                        dmSidebarSession.Token);
                }
            }
        }


        //
        // =========================================================
        // FIXED SUPPORT FOOTER
        // =========================================================
        //
        // Patreon and Discord are outside the scrolling child.
        //
        // They therefore remain anchored at the bottom regardless
        // of where the navigation list has been scrolled.
        //

        //
        // Small breathing room above the fixed support row.
        //

        ImGui.Dummy(UiVec(0f, 2f));

        if (availableUpdate is not null)
            DrawSidebarUpdate(availableUpdate);

        var supportRowWidth =
            ImGui.GetContentRegionAvail().X;


        var supportGap = Ui(5f);


        var supportButtonWidth =
            sidebarCollapsedForFrame ? supportRowWidth : (supportRowWidth - supportGap) * 0.5f;


        DrawSidebarUtilityButton(HomePage.Screen, FontAwesomeIcon.Desktop, "Screen", supportButtonWidth);
        if (!sidebarCollapsedForFrame)
            ImGui.SameLine(0f, supportGap);
        DrawSidebarUtilityButton(HomePage.Settings, FontAwesomeIcon.Cog, "Settings", supportButtonWidth);

        DrawCompactSupportLink(
            "Patreon",
            supportButtonWidth,
            Ui(28f),
            PatreonOrange,
            PatreonOrangeHover,
            null,
            FontAwesomeIcon.Heart);


        if (!sidebarCollapsedForFrame)
            ImGui.SameLine(0f, supportGap);


        DrawCompactSupportLink(
            "Discord",
            supportButtonWidth,
            Ui(28f),
            new Vector4(
                0.42f,
                0.52f,
                1.00f,
                1f),
            new Vector4(
                0.58f,
                0.66f,
                1.00f,
                1f),
            DiscordInviteUrl,
            FontAwesomeIcon.CommentDots,
            discordIcon: true);

        //
        // Keep the support buttons just off the bottom edge.
        //

        ImGui.Dummy(
            UiVec(0f, 3f));
    }

    private void DrawSidebarUtilityButton(HomePage page, FontAwesomeIcon icon, string tooltip, float width)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, Ui(32f));
        if (ImGui.InvisibleButton($"##sidebarUtility{page}", size))
            currentPage = page;

        var hovered = ImGui.IsItemHovered();
        var active = currentPage == page;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(active ? Accent : hovered ? CardBgHover : CardBg), Ui(8f));
        draw.AddRect(origin, origin + size,
            ImGui.GetColorU32(hovered || active ? Accent : BorderSubtle), Ui(8f));
        var iconText = icon.ToIconString();
        var iconSize = MeasureSidebarIcon(iconText);
        UiFonts.DrawIcon(draw, ImGui.GetFontSize(), origin + (size - iconSize) * .5f, ImGui.GetColorU32(active ? Vector4.One : hovered ? AccentHover : MutedText), iconText);
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            UiFonts.SetTooltip(tooltip);
        }
    }

    private void DrawSidebarUpdate(Version version)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, Ui(30f));
        if (ImGui.InvisibleButton("##sidebarPluginUpdate", size))
            OnPluginUpdateRequested?.Invoke();

        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        var ink = new Vector4(0.78f, 0.67f, 0.96f, 1f);
        draw.AddRectFilled(origin, origin + size,
            ImGui.GetColorU32(new Vector4(0.54f, 0.33f, 0.88f, hovered ? 0.19f : 0.08f)), Ui(7f));
        draw.AddRect(origin, origin + size,
            ImGui.GetColorU32(new Vector4(ink.X, ink.Y, ink.Z, hovered ? 0.65f : 0.25f)), Ui(7f));

        var arrow = origin + new Vector2(Ui(16f), size.Y * 0.5f);
        var arrowColor = ImGui.GetColorU32(ink);
        draw.AddLine(arrow + UiVec(0f, 5f), arrow - UiVec(0f, 5f), arrowColor, Ui(1.5f));
        draw.AddLine(arrow - UiVec(0f, 5f), arrow + UiVec(-4f, -1f), arrowColor, Ui(1.5f));
        draw.AddLine(arrow - UiVec(0f, 5f), arrow + UiVec(4f, -1f), arrowColor, Ui(1.5f));

        var label = $"Update to {version}";
        if (!sidebarCollapsedForFrame)
        {
            var textSize = ImGui.CalcTextSize(label);
            var textScale = MathF.Min(1f, MathF.Max(1f, size.X - Ui(40f)) / MathF.Max(1f, textSize.X));
            UiFonts.DrawFittedText(draw, ImGui.GetFontSize() * textScale, origin + new Vector2(Ui(30f), (size.Y - textSize.Y * textScale) * 0.5f), ImGui.GetColorU32(hovered ? Vector4.One : ink), label);
        }
        if (hovered)
            UiFonts.SetTooltip($"Alpha Channel {version} is available.\nClick to open Dalamud's Can be updated page.");
    }

    private void DrawNavGroup(
     string label,
     bool hideHeading)
    {
        //
        // ---------------------------------------------------------
        // Sidebar group headings
        // ---------------------------------------------------------
        //
        // Collapsed navigation retains separation without showing headings.
        //

        if (hideHeading)
        {
            // Retain a small separation between groups without a heading.
            ImGui.Dummy(
                new Vector2(
                    0f,
                    Ui(4.6f)));

            return;
        }


        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(4f)));


        using var uiFontScale184351=UiFonts.PushRelative(0.92f);


        ImGui.TextColored(
            new Vector4(0.64f, 0.70f, 0.82f, 1f),
            label);


        uiFontScale184351.Dispose();


        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(1f)));
    }

    private void DrawNavItem(HomePage page, FontAwesomeIcon icon, string label, int badgeCount = 0,
        bool forceActive = false, bool showRunningDot = false, bool notificationBadge = false, bool showUnreadDot = false,
        int? populationCount = null)
    {
        var active = forceActive || currentPage == page;
        ImGui.PushID((int)page);

        var rowStart = ImGui.GetCursorScreenPos();
        var rowSize = new Vector2(ImGui.GetContentRegionAvail().X, Ui(SidebarRowHeight));
        var drawList = ImGui.GetWindowDrawList();

        var clicked = ImGui.InvisibleButton("##navrow", rowSize);
        var hovered = ImGui.IsItemHovered();

        if (active)
        {
            // The navigation child starts at the content edge, but the pill
            // uses the sidebar's empty left gutter. Widen only the horizontal
            // draw clip; preserve vertical clipping while navigation scrolls.
            var selectionClipMin = drawList.GetClipRectMin();
            selectionClipMin.X -= Ui(10f);
            drawList.PushClipRect(selectionClipMin, drawList.GetClipRectMax(), false);
            var selectionStart = rowStart + UiVec(-2f, 0f);
            drawList.AddRectFilled(selectionStart, rowStart + rowSize,
                ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, hovered ? .25f : .17f)), Ui(8f));
            drawList.AddRect(selectionStart, rowStart + rowSize,
                ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, .30f)), Ui(8f));
            const float indicatorX = -8f;
            drawList.AddRectFilled(rowStart + UiVec(indicatorX, 3f),
                rowStart + new Vector2(Ui(indicatorX + 3f), rowSize.Y - Ui(3f)),
                ImGui.GetColorU32(Accent), Ui(2f));
            UiDecorationRenderer.Selection(drawList,rowStart,rowSize,Plugin.Cfg.UiDecorationTheme,Colors,Ui(1f),sidebarCollapsedForFrame);
            drawList.PopClipRect();
        }
        else if (hovered)
        {
            drawList.AddRectFilled(rowStart, rowStart + rowSize, ImGui.GetColorU32(CardBgHover), Ui(8f));
        }

        var textColor = active || hovered ? Vector4.One : new Vector4(.68f, .73f, .83f, 1f);
        var iconText = icon.ToIconString();
        var iconScale = 1.08f;
        var iconSize = MeasureSidebarIcon(iconText) * iconScale;
        var iconPosition = sidebarCollapsedForFrame
            ? rowStart + (rowSize - iconSize) * 0.5f
            : rowStart + new Vector2(Ui(20f) - iconSize.X * .5f, (rowSize.Y - iconSize.Y) * .5f);
        UiFonts.DrawIcon(drawList, ImGui.GetFontSize() * iconScale, iconPosition, ImGui.GetColorU32(active ? Accent : textColor), iconText);

        // Reserve badge space before laying out labels, including when a mention
        // notification and the Alpha Chat population are both present.
        var badgeRight = rowStart.X + rowSize.X - Ui(6f);
        void DrawCountBadge(int count, bool notification, bool population)
        {
            var capped = sidebarCollapsedForFrame ? 9 : population ? 999 : 99;
            var badgeText = count > capped ? $"{capped}+" : count.ToString();
            const float badgeScale = .9f;
            var textSize = ImGui.CalcTextSize(badgeText) * badgeScale;
            var radius = MathF.Max(Ui(sidebarCollapsedForFrame ? 7f : 9f), textSize.X * .5f + Ui(4f));
            var badgeCenter = sidebarCollapsedForFrame
                ? rowStart + new Vector2(rowSize.X - radius,
                    population && badgeCount > 0 ? rowSize.Y - radius : radius)
                : new Vector2(badgeRight - radius, rowStart.Y + rowSize.Y * .5f);
            drawList.AddCircleFilled(badgeCenter, radius, ImGui.GetColorU32(notification ? Danger : Accent));
            UiFonts.DrawText(drawList, ImGui.GetFontSize() * badgeScale, badgeCenter - textSize * .5f, ImGui.GetColorU32(Vector4.One), badgeText);
            badgeRight -= radius * 2f + Ui(4f);
        }
        if (populationCount is { } population)
            DrawCountBadge(population, false, true);
        if (badgeCount > 0)
            DrawCountBadge(badgeCount, notificationBadge, false);

        var labelRight = badgeRight - Ui(4f);

        if (showRunningDot)
        {
            var dotCenter = sidebarCollapsedForFrame
                ? rowStart + new Vector2(rowSize.X - Ui(5f), rowSize.Y - Ui(6f))
                : new Vector2(labelRight - Ui(6f), rowStart.Y + rowSize.Y * .5f);
            drawList.AddCircleFilled(dotCenter, Ui(6f), ImGui.GetColorU32(new Vector4(0.10f, 0.16f, 0.13f, 1f)));
            drawList.AddCircleFilled(dotCenter, Ui(4.5f), ImGui.GetColorU32(new Vector4(0.22f, 0.88f, 0.48f, 1f)));
            labelRight -= Ui(18f);
        }

        if (showUnreadDot)
        {
            var dotCenter = sidebarCollapsedForFrame
                ? rowStart + new Vector2(populationCount is not null ? Ui(8f) : rowSize.X - Ui(6f), Ui(7f))
                : new Vector2(labelRight - Ui(4f), rowStart.Y + rowSize.Y * .5f);
            drawList.AddCircleFilled(dotCenter, Ui(4f), ImGui.GetColorU32(active ? Vector4.One : Accent));
            labelRight -= Ui(14f);
        }

        if (!sidebarCollapsedForFrame)
        {
            const float labelScale = 1.03f;
            var labelStart = rowStart.X + Ui(38f);
            var visibleLabel = FitPartyTextToWidth(label, MathF.Max(0f, labelRight - labelStart) / labelScale);
            var labelSize = ImGui.CalcTextSize(visibleLabel) * labelScale;
            UiFonts.DrawText(drawList, ImGui.GetFontSize() * labelScale, new Vector2(labelStart, rowStart.Y + (rowSize.Y - labelSize.Y) * .5f), ImGui.GetColorU32(textColor), visibleLabel);
        }

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (sidebarCollapsedForFrame || populationCount is not null || badgeCount > 0)
                UiFonts.SetTooltip(label + (badgeCount > 0 ? $"\n{badgeCount} unread notifications" : string.Empty) +
                    (populationCount is { } members ? $"\n{members} people in Alpha Chat" : string.Empty) +
                    (showRunningDot ? "\nActive" : string.Empty) + (showUnreadDot ? "\nUnread messages" : string.Empty));
        }

        ImGui.PopID();

        if (clicked)
        {
            currentPage = page;
        }
    }

    private void DrawHeaderAccountStatus(bool signedIn)
    {
        var colour = !signedIn ? new Vector4(0.22f, 0.62f, 1f, 1f) : stream.ConnectionState switch
        {
            RealtimeConnectionState.Connected => Good,
            RealtimeConnectionState.Connecting or RealtimeConnectionState.Reconnecting => ConnectionPendingOrange,
            _ => Danger,
        };
        // Status indicators are geometry, so their appearance is independent
        // of the active text font and its available Unicode glyphs.
        var diameter = MathF.Max(2f, MathF.Round(ImGui.GetFontSize() * .72f));
        var size = new Vector2(diameter, ImGui.GetTextLineHeight());
        var center = ImGui.GetCursorScreenPos() + size * .5f;
        ImGui.GetWindowDrawList().AddCircleFilled(Vector2.Round(center), diameter * .5f, ImGui.GetColorU32(colour));
        ImGui.Dummy(size);
        if (ImGui.IsItemHovered())
            UiFonts.SetTooltip(signedIn ? $"Alpha Channel: {stream.ConnectionStatusText}" : "Not signed in to Alpha Channel");
    }

    private void DrawHeaderProfileLink(string id)
    {
        if (CurrentSession is not { } session) return;
        var origin = ImGui.GetItemRectMin();
        var size = ImGui.GetItemRectSize();
        var cursor = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(origin);
        if (ImGui.InvisibleButton($"##headerOwnProfile{id}", size))
            OpenProfilePage(session, session.AccountId, session.DisplayName);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            UiFonts.SetTooltip("View your profile");
        }
        ImGui.SetCursorScreenPos(cursor);
    }

    // Ko-fi brand pink — left-nav footer above the version. Alternates ask ↔ CTA every 30s.

    private static readonly Vector4 PatreonOrange = new(1f, 0.55f, 0.15f, 1f);
    private static readonly Vector4 PatreonOrangeHover = new(1f, 0.68f, 0.30f, 1f);
    private static readonly Vector4 ConnectionPendingOrange = new(1f, 0.55f, 0.15f, 1f);
    private static readonly string[] DonateLabels =
    [
        "Hey, like what you see?\nConsider supporting us",
        "Donate on Ko-fi",
    ];
    private const double DonateRotateSeconds = 30;

    private static void DrawSupportLink(
    string label,
    float height,
    Vector4 color,
    Vector4 hoverColor,
    string url)
    {
        var width = ImGui.GetContentRegionAvail().X - 24f;
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height);
        var buttonOrigin = origin + UiVec(12f, 0);

        ImGui.SetCursorScreenPos(buttonOrigin);

        if (ImGui.InvisibleButton($"##{label}", size))
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception)
            {
                AepLog.Warning("[Support] Failed to open browser.");
            }
        }

        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRect(
            buttonOrigin,
            buttonOrigin + size,
            ImGui.GetColorU32(hovered ? hoverColor : color),
            8f,
            ImDrawFlags.None,
            1f);

        var textSize = ImGui.CalcTextSize(label);
        var textPos = buttonOrigin + new Vector2(
            (width - textSize.X) * 0.5f,
            (height - textSize.Y) * 0.5f);

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), textPos, ImGui.GetColorU32(hovered ? hoverColor : color), label);
    }


    // ---------------------------------------------------------
    // Compact support link
    // ---------------------------------------------------------
    //
    // Unlike DrawSupportLink(), this one accepts an explicit
    // width so several support links can share one row.
    //

    private void DrawCompactSupportLink(
        string label,
        float width,
        float height,
        Vector4 color,
        Vector4 hoverColor,
        string? url,
        FontAwesomeIcon? compactIcon = null,
        bool discordIcon = false)
    {
        var buttonOrigin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(
                width,
                height);


        ImGui.SetCursorScreenPos(
            buttonOrigin);


        if (ImGui.InvisibleButton(
                $"##support_{label}",
                size))
        {
            if (url is null)
            {
                patreonPopupOpen = true;
            }
            else
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception)
                {
                    AepLog.Warning("[Support] Failed to open browser.");
                }
            }
        }


        var hovered =
            ImGui.IsItemHovered();


        var drawList =
            ImGui.GetWindowDrawList();


        drawList.AddRectFilled(buttonOrigin, buttonOrigin + size,
            ImGui.GetColorU32(hovered ? CardBgHover : CardBg), Ui(8f));
        drawList.AddRect(
            buttonOrigin,
            buttonOrigin + size,
            ImGui.GetColorU32(
                hovered
                    ? hoverColor
                    : new Vector4(color.X, color.Y, color.Z, .55f)),
            Ui(8f),
            ImDrawFlags.None,
            Ui(1f));


        if (compactIcon is { } supportIcon)
        {
            var iconText = supportIcon.ToIconString();
            var iconSize = discordIcon ? UiVec(16f, 13f) : MeasureSidebarIcon(iconText);
            var supportLabelSize = sidebarCollapsedForFrame ? Vector2.Zero : ImGui.CalcTextSize(label);
            var gap = sidebarCollapsedForFrame ? 0f : Ui(5f);
            var contentWidth = iconSize.X + gap + supportLabelSize.X;
            var contentScale = MathF.Min(1f, MathF.Max(1f, size.X - Ui(10f)) / contentWidth);
            var contentX = buttonOrigin.X + (size.X - contentWidth * contentScale) * .5f;
            var iconOrigin = new Vector2(contentX, buttonOrigin.Y + (size.Y - iconSize.Y * contentScale) * .5f);
            if (discordIcon)
                DrawSidebarDiscordIcon(iconOrigin, iconSize * contentScale, hovered ? hoverColor : color,
                    hovered ? CardBgHover : CardBg);
            else
                UiFonts.DrawIcon(drawList, ImGui.GetFontSize() * contentScale, iconOrigin, ImGui.GetColorU32(hovered ? hoverColor : color), iconText);
            if (!sidebarCollapsedForFrame)
                UiFonts.DrawFittedText(drawList, ImGui.GetFontSize() * contentScale, new Vector2(contentX + (iconSize.X + gap) * contentScale,
                        buttonOrigin.Y + (size.Y - supportLabelSize.Y * contentScale) * .5f), ImGui.GetColorU32(hovered ? hoverColor : color), label);
            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            }
            return;
        }

        var textSize =
            ImGui.CalcTextSize(
                label);


        var textPos =
            buttonOrigin +
            new Vector2(
                (width - textSize.X) * 0.5f,
                (height - textSize.Y) * 0.5f);


        UiFonts.DrawText(drawList, ImGui.GetFontSize(), textPos, ImGui.GetColorU32(
                hovered
                    ? hoverColor
                    : color), label);
    }

       

  

    // Every non-Home page starts with navigation + title + a one-line purpose so each Channel reads as
    // its own place, not a clone of every other tab with a different header string.
    private void PageTitle(string text, string purpose) =>
        PageTitleBack(text, purpose, HomePage.Home);

    private void PageTitleBack(string text, string purpose, HomePage backPage)
    {
        DrawPolishedPageHeader(text, purpose);
    }

    // Consistent accent-colored sub-headers within a page — same weight on every Channel.
    private static void SectionHeader(string text)
    {
        ImGui.TextColored(Accent, text);
        ImGui.Dummy(new Vector2(0, 0));
    }

    // Soft panel for content that needs grouping. Height must be >0 — Child size.y=0 means
    // "fill remaining host height" in ImGui, which swallowed the Player search section below.
    private static void DrawCard(string id, Action draw)
    {
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(16, 14)))
        using (var card = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(id, new Vector2(-1, 1), false,
                   PaddedChild | ImGuiWindowFlags.AlwaysAutoResize)))
        {
            if (card)
            {
                draw();
            }
        }

        ImGui.Spacing();
    }

    // Tall accent-edged panel for the "main thing" on media/live pages (now playing, room status).
    private static void DrawStage(string id, Action draw)
    {
        var origin = ImGui.GetCursorScreenPos();
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBgHover))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(20, 18)))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14f)))
        using (var stage = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(id, new Vector2(-1, 1), false,
                   PaddedChild | ImGuiWindowFlags.AlwaysAutoResize)))
        {
            if (stage)
            {
                draw();
            }
        }

        var end = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList().AddRectFilled(origin, new Vector2(origin.X + Ui(3f), end.Y),
            ImGui.GetColorU32(Accent), 2f);
        ImGui.Spacing();
        ImGui.Spacing();
    }

    // Activity feed row: left rail + text, no card chrome.
    private void DrawTimelineRow(string id, string text, bool unread = false,string? usernameId=null,string? username=null)
    {
        ImGui.PushID(id);
        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var wrapWidth = MathF.Max(ImGui.GetContentRegionAvail().X - 28f, 40f);
        var textHeight = ImGui.CalcTextSize(text, false, wrapWidth).Y;
        var height = MathF.Max(textHeight + 12f, 28f);

        drawList.AddLine(origin + UiVec(7, 0), origin + new Vector2(Ui(7), height),
            ImGui.GetColorU32(BorderSubtle), 1.5f);
        drawList.AddCircleFilled(origin + UiVec(7, 12), unread ? 4.5f : 3.5f,
            ImGui.GetColorU32(unread ? Accent : MutedText));

        ImGui.SetCursorScreenPos(origin + UiVec(22, 4));
        ImGui.PushTextWrapPos(origin.X + 22f + wrapWidth);
        if(!string.IsNullOrEmpty(username) && text.StartsWith(username,StringComparison.Ordinal))
        {
            DrawUsername(usernameId,username);
            ImGui.SameLine(0f,0f);
            ImGui.TextWrapped(text[username.Length..]);
        }
        else ImGui.TextWrapped(text);
        ImGui.PopTextWrapPos();

        var afterY = ImGui.GetCursorScreenPos().Y;
        ImGui.SetCursorScreenPos(new Vector2(origin.X, MathF.Max(afterY, origin.Y + height) + Ui(2f)));
        ImGui.PopID();
    }

    private static void DrawPlainEmpty(string message, string? buttonLabel = null, Action? onClick = null)
    {
        ImGui.Dummy(UiVec(0, 8));
        ImGui.TextColored(MutedText, message);
        if (buttonLabel is not null && onClick is not null)
        {
            ImGui.Spacing();
            if (ImGui.Button(buttonLabel, UiVec(160, 30)))
            {
                onClick();
            }
        }

        ImGui.Dummy(UiVec(0, 8));
    }

    private void DrawNamePrompt()
    {
        if (!namePromptActive)
        {
            return;
        }

        var promptWidth = Ui(620f);
        var promptHeight = Ui(455f);

        //
        // Cover only the Alpha Channel window.
        //
        var parentPos =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var promptPos =
            new Vector2(
                parentPos.X +
                (parentSize.X - promptWidth) * 0.5f,
                parentPos.Y +
                (parentSize.Y - promptHeight) * 0.5f);

        ImGui.SetNextWindowPos(
            parentPos,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(0f);

        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;

        if (!BeginWindowOverlay(
                "##usernameOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

        //
        // Darken Alpha Channel behind the prompt.
        //
        drawList.AddRectFilled(
            parentPos,
            parentPos + parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.48f)));

        //
        // Draw the username card itself.
        //
        var promptMax =
            promptPos +
            new Vector2(
                promptWidth,
                promptHeight);

        drawList.AddRectFilled(
            promptPos,
            promptMax,
            ImGui.GetColorU32(
                CardBg),
            10f);

        drawList.AddRect(
            promptPos,
            promptMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.45f)),
            10f,
            ImDrawFlags.RoundCornersAll,
            1f);

        //
        // Everything below is inside the SAME overlay window,
        // so clicking the dark area can no longer cover the prompt.
        //
        var padding = NativeUi(20f);

        ImGui.SetCursorScreenPos(
            promptPos +
            new Vector2(
                padding,
                Ui(17f)));

        using var uiFontScale208840=UiFonts.PushRelative(1.3f);

        ImGui.TextColored(
            AccentHover,
            "Welcome to Alpha Channel!");

        uiFontScale208840.Dispose();

        ImGui.SetCursorScreenPos(
            promptPos +
            new Vector2(
                padding,
                Ui(53f)));

        using var uiFontScale209173=UiFonts.PushRelative(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Choose your username");

        uiFontScale209173.Dispose();

        ImGui.SetCursorScreenPos(
            promptPos +
            new Vector2(
                padding,
                Ui(79f)));

        ImGui.TextColored(
            MutedText,
            "Choose the username other Alpha Channel users will see.");

        ImGui.SameLine(
            0f,
            7f);

        using (UiFonts.PushIcon())
        {
            ImGui.TextDisabled(
                FontAwesomeIcon.InfoCircle.ToIconString());
        }

        if (ImGui.IsItemHovered())
        {
            UiFonts.SetTooltip(
                "This is the name used to add you to friends lists and join your watch party.");
        }

        //
        // Username input.
        //
        ImGui.SetCursorScreenPos(
            promptPos +
            new Vector2(
                padding,
                Ui(111f)));

        ImGui.SetNextItemWidth(
            promptWidth -
            (padding * 2f));

        using (ImRaii.PushColor(
            ImGuiCol.FrameBg,
            FrameBg))
        using (ImRaii.PushColor(
            ImGuiCol.Border,
            new Vector4(
                Accent.X,
                Accent.Y,
                Accent.Z,
                0.75f)))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameBorderSize,
            1f))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            5f))
        {
            ImGui.InputText(
                "##usernameInput",
                ref namePromptInput,
                32);
        }

        //
        // Topic subscriptions.
        //
        //
        // Divider between username and topic selection.
        //
        var dividerY =
            promptPos.Y +
            158f;

        drawList.AddLine(
            new Vector2(
                promptPos.X +
                padding,
                dividerY),
            new Vector2(
                promptMax.X -
                padding,
                dividerY),
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.28f)),
            1f);

        ImGui.SetCursorScreenPos(
            promptPos +
            new Vector2(
                padding,
                Ui(172f)));

        ImGui.TextColored(
            Vector4.One,
            "Subscribe to your favourite topics");

        ImGui.SetCursorScreenPos(
            promptPos +
            new Vector2(
                padding,
                Ui(198f)));

        ImGui.TextColored(
            MutedText,
            "Choose at least 3 topics to receive relevant video recommendations.");

        ImGui.SameLine(
            0f,
            7f);

        using (UiFonts.PushIcon())
        {
            ImGui.TextDisabled(
                FontAwesomeIcon.InfoCircle.ToIconString());
        }

        if (ImGui.IsItemHovered())
        {
            UiFonts.SetTooltip(
                "You can change these later in Settings.");
        }

        ImGui.SetCursorScreenPos(
            promptPos +
            new Vector2(
                padding,
                Ui(230f)));

        ImGui.PushID(
            "welcomeTopicSelection");

        DrawTrendingTopicTags(
    columnHeight: 145f,
    availableWidthOverride:
        promptWidth -
        (padding * 2f));

        ImGui.PopID();

        var selectedTopicCount =
            GetSubscribedTopicCount();

        ImGui.SetCursorScreenPos(
            promptPos +
            new Vector2(
                padding,
                Ui(384f)));

        var topicCountValid =
            selectedTopicCount is >= 3 and <= 15;

        ImGui.TextColored(
            topicCountValid
                ? Accent
                : new Vector4(
                    1f,
                    0.55f,
                    0.35f,
                    1f),
             $"{selectedTopicCount} topics selected (minimum 3)");

        var valid =
            !string.IsNullOrWhiteSpace(
                namePromptInput) &&
            topicCountValid;

        //
        // Confirm button.
        //
        ImGui.SetCursorScreenPos(
            promptPos +
            new Vector2(
                padding,
                Ui(414f)));

        if (!valid)
        {
            UiDisabled.Begin();
        }

        if (ImGui.Button(
          "Confirm",
          UiVec(120f, 32f)))
        {
            Plugin.Cfg.Save();

            onNameConfirmed?.Invoke(
                namePromptInput.Trim());

            onNameConfirmed =
                null;

            namePromptActive =
                false;

            //
            // First-launch topics have now been submitted. Start the same
            // shared Topics-cache pipeline used by Browse Videos while the
            // splash animation runs independently.
            //
            browseVideoRequested =
                true;

            topicVideoStartupRequested =
                true;

            homeYouTubeResults =
                null;

            homeYouTubeSelectedTopics.Clear();

            isLoadingHomeYouTube =
                true;

            _ =
                PrepareTopicVideosForLaunchAsync(
                    forceRefresh: false);
        }

        if (!valid)
        {
            UiDisabled.End();
        }

        if (topicSelectionLimitWarning)
        {
            ImGui.SameLine(
                0f,
                10f);

            ImGui.TextColored(
                new Vector4(
                    1f,
                    0.55f,
                    0.35f,
                    1f),
                "You may only choose up to 15 topics, " +
                "please remove one to add another");
        }

        ImGui.End();
    }

    private void DrawRoster(
     string label,
     bool allowPromote)
    {
        var realCount =
            stream.Roster.Length;

        using var uiFontScale215602=UiFonts.PushRelative(1.08f);

        ImGui.TextColored(
            Vector4.One,
            label);

        uiFontScale215602.Dispose();

        ImGui.Dummy(
            UiVec(0f, 8f));

        if (realCount == 0)
        {
            ImGui.TextColored(
                MutedText,
                "Waiting for people to join...");

            return;
        }

        DrawAvatarStack(
            stream.Roster,
            maxShown: 12);

        ImGui.Dummy(
            UiVec(0f, 10f));

        for (var index = 0;
             index < realCount;
             index++)
        {
            var participant =
                stream.Roster[index];

            ImGui.PushID(
                participant.UserId);

            DrawPartyRosterRow(
                participant.DisplayName,
                participant.UserId,
                allowPromote,
                canUseActions: true,
                onPromote:
                    () =>
                    {
                        _ =
                            TransferWatchPartyHostAsync(
                                participant.UserId);
                    });

            ImGui.PopID();

            ImGui.Dummy(
                UiVec(0f, 6f));
        }
    }

    private void DrawPartyRosterRow(
    string displayName,
    string accountId,
    bool allowPromote,
    bool canUseActions,
    Action? onPromote)
    {
        var rowHeight = Ui(52f);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            8f))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            CardBg))
        using (var row = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
            "##partyParticipantRow",
            new Vector2(-1f, rowHeight),
            false,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (!row)
            {
                return;
            }

            var origin =
                ImGui.GetCursorScreenPos();

            var rowWidth =
                ImGui.GetWindowWidth();

            // Online/live indicator.
            ImGui.GetWindowDrawList()
                .AddCircleFilled(
                    origin +
                    UiVec(18f, 26f),
                    4f,
                    ImGui.GetColorU32(Good));

            // Leave room for the tier badge and host controls on narrow layouts.
            var badgeTier = PatreonBadgeTier(accountId);
            var badgeWidth = badgeTier > 0 ? PatreonBadgeSize(badgeTier, compact: false).X + Ui(6f) : 0f;
            var nameWidth = MathF.Max(Ui(24f), rowWidth - Ui(44f) - badgeWidth -
                (allowPromote ? Ui(236f) : Ui(12f)));
            var visibleName = displayName;
            while (visibleName.Length > 1 && ImGui.CalcTextSize(visibleName).X > nameWidth)
                visibleName = visibleName[..^1];
            if (visibleName.Length < displayName.Length)
                visibleName = visibleName.TrimEnd() + "…";

            // Name.
            DrawUsernameAt(ImGui.GetWindowDrawList(),origin+UiVec(32f,18f),visibleName,Vector4.One,accountId);

            DrawPatreonBadgeAt(
                origin + UiVec(38f, 14f) + new Vector2(ImGui.CalcTextSize(visibleName).X, 0f),
                accountId, $"roster_{accountId}", compact: false);

            if (!allowPromote)
            {
                return;
            }

            var rightPadding = Ui(12f);
            var gap = NativeUi(8f);

            var kickSize =
                UiVec(112f, 30f);

            var hostSize =
                UiVec(104f, 30f);

            // -----------------------------------------------------
            // Kick from room
            // -----------------------------------------------------

            var kickPos =
                new Vector2(
                    origin.X +
                    rowWidth -
                    rightPadding -
                    kickSize.X,
                    origin.Y + Ui(11f));

            ImGui.SetCursorScreenPos(
                kickPos);

            using (UiDisabled.Scope(
                !canUseActions))
            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                7f))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                new Vector4(
                    0.16f,
                    0.055f,
                    0.07f,
                    1f))
                .Push(
                    ImGuiCol.ButtonHovered,
                    new Vector4(
                        0.22f,
                        0.07f,
                        0.09f,
                        1f))
                .Push(
                    ImGuiCol.ButtonActive,
                    new Vector4(
                        0.25f,
                        0.08f,
                        0.10f,
                        1f)))
            {
                if (ImGui.Button("Kick from room", kickSize))
                    ModeratePartyMember(accountId, displayName, "kick");
            }

            // -----------------------------------------------------
            // Make host
            // -----------------------------------------------------

            var hostPos =
                new Vector2(
                    kickPos.X -
                    gap -
                    hostSize.X,
                    kickPos.Y);

            ImGui.SetCursorScreenPos(
                hostPos);

            var transferBlocked = CurrentContentBlocksHostTransfer();
            using (UiDisabled.Scope(
                !canUseActions || transferBlocked))
            using (ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                7f))
            using (ImRaii.PushColor(
                ImGuiCol.Button,
                FrameBg)
                .Push(
                    ImGuiCol.ButtonHovered,
                    FrameBgHover)
                .Push(
                    ImGuiCol.ButtonActive,
                    AccentFieldActive))
            {
                if (ImGui.Button(
                    "Make host",
                    hostSize) &&
                    canUseActions && !transferBlocked)
                {
                    onPromote?.Invoke();
                }
            }
            DrawHostTransferDisabledTooltip(transferBlocked);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0) return;
        SharedYouTubeCache.Current?.Dispose();
        CloseStatisticsConnection();
        Interlocked.Increment(ref signInGeneration);
        Interlocked.Increment(ref patreonConnectionGeneration);
        Interlocked.Increment(ref unifiedSearchGeneration);
        Interlocked.Increment(ref subscriptionVideoLoadGeneration);
        Interlocked.Increment(ref topicSettingsRefreshGeneration);

        static void Cleanup(ShutdownResource name, Action action)
        {
            try { action(); }
            catch (Exception error) { AepLog.CleanupFailure(ShutdownOwner.MainWindow,name,error); }
        }

        Cleanup(ShutdownResource.BadgeProgress, SaveBadgeProgress);
        Cleanup(ShutdownResource.SpotifyClient, DisposeSpotifyPage);
        Cleanup(ShutdownResource.PublicConfiguration,Plugin.Cfg.FlushPublicState);
        Cleanup(ShutdownResource.ChatImageCache, () => ReconcileGroupImageCache(new HashSet<string>(), clearAll: true));
        Cleanup(ShutdownResource.FinalAdminStatistics, SaveFinalAdminUsage);
        Cleanup(ShutdownResource.UIFontCache, UiFonts.Dispose);
        Cleanup(ShutdownResource.PrivateGroupChats, dmClient.Groups.Dispose);
        Cleanup(ShutdownResource.WorldBeacon, WithdrawWorldBeaconOnShutdown);
        EndHalloween(publish: false);
#if DEBUG
#endif
        Cleanup(ShutdownResource.FinalArcadeScores, ObserveArcadeScores);
        Cleanup(ShutdownResource.BroadcastStudio, () => { StopStudioBroadcast(); studioClient?.Dispose(); studioClient = null; });
        Cleanup(ShutdownResource.ArcadeScores, () => arcadeScores?.Dispose());
        Cleanup(ShutdownResource.GuestPresence, () => guestPresence?.Dispose());
        Cleanup(ShutdownResource.ArcadePresence, () => arcadePresence?.Dispose());
        Cleanup(ShutdownResource.SignIn, () => signInCts?.Cancel());
        signInCts = null;
        Cleanup(ShutdownResource.Patreon, CancelPatreonConnection);
        Cleanup(ShutdownResource.BroadcastViewerVerification, ResetBroadcastPatreonViewers);
        Cleanup(ShutdownResource.HostBroadcastRepair, CancelHostBroadcastRepair);
        Cleanup(ShutdownResource.UnifiedSearch, () => { unifiedSearchCts?.Cancel(); dailymotionSearchCancellation?.Cancel(); dailymotionSearchCancellation?.Dispose(); });
        Cleanup(ShutdownResource.YouTubeSearch, () => youtubeSearchCts?.Cancel());
        Cleanup(ShutdownResource.VideoBrowsing, () => browseVideosCts?.Cancel());
        Cleanup(ShutdownResource.Subscriptions, () => subscriptionVideosCts?.Cancel());
        Cleanup(ShutdownResource.YouTubeAccountFeeds, youtubeFeedLifetime.Cancel);
        Cleanup(ShutdownResource.YouTubeVisibleMetadata, youtubeVisibleMetadata.Reset);

        Cleanup(ShutdownResource.LuckyWheelAudio, () => { luckyWheelRoll.Dispose(); luckyWheelFinish.Dispose(); luckyWheelPreviewRoll.Dispose(); luckyWheelPreviewFinish.Dispose(); });
        Cleanup(ShutdownResource.PatreonBadgePersistence, () => patreonBadgeCache?.Dispose());
        Cleanup(ShutdownResource.PixelsConnection, ResetPixelsLocal);
        Cleanup(ShutdownResource.LuckyWheelCloudflareConnection, DisposeLuckyWheelRelay);
        Cleanup(ShutdownResource.QuizCloudflareConnection, DisposeQuizRelay);
        Cleanup(ShutdownResource.ArcadeCloudflareConnections, () => { ResetBlastPartyLocal(); DisposeActivityRelays(); });
        Cleanup(ShutdownResource.BingoCloudflareConnection, DisposeBingoRelay);
        if (luckyWheelPageRenderer is { } wheelRenderer)
            DxHandler.QueueShutdownWork(wheelRenderer.Dispose);
        if (bingoPageRenderer is { } bingoRenderer)
            DxHandler.QueueShutdownWork(bingoRenderer.Dispose);
        if (skyRailPageRenderer is { } skyRailRenderer)
            DxHandler.QueueShutdownWork(skyRailRenderer.Dispose);
        if (DxHandler.Device is not null)
            DxHandler.QueueShutdownWork(DrainRetiredArcadePageRenderers);
        if (slotMachinePageRenderer is { } slotRenderer)
            DxHandler.QueueShutdownWork(slotRenderer.Dispose);
        if (pokerPageRenderer is { } pokerRenderer)
            DxHandler.QueueShutdownWork(pokerRenderer.Dispose);
        Cleanup(ShutdownResource.AirForceMusic, skyRailThemePlayer.Dispose);
        Cleanup(ShutdownResource.AirForceBossAlert, skyRailBossAlertPlayer.Dispose);
        Cleanup(ShutdownResource.ChocoboTheme, chocoRaceThemePlayer.Dispose);
        Cleanup(ShutdownResource.ChocoboStartAudio, chocoRaceStartPlayer.Dispose);
        Cleanup(ShutdownResource.ChocoboMeteorAudio, chocoRaceMeteorPlayer.Dispose);
        Cleanup(ShutdownResource.ChocoboFanfareAudio, chocoRaceFanfarePlayer.Dispose);
        Cleanup(ShutdownResource.ChocoboPage, () => DisposeChocoRacePageRenderer(flush: true));
        Cleanup(ShutdownResource.RouletteMusic, rouletteMusicPlayer.Dispose);
        Cleanup(ShutdownResource.PokerMusic, pokerMusicPlayer.Dispose);
        Cleanup(ShutdownResource.SlotCoinAudio, slotCoinSoundPlayer.Dispose);
        Cleanup(ShutdownResource.RouletteChipAudio, rouletteChipSoundPlayer.Dispose);
        Cleanup(ShutdownResource.RouletteRollAudio, rouletteRollSoundPlayer.Dispose);
        Cleanup(ShutdownResource.NotificationAudio, notificationSoundPlayer.Dispose);
        Cleanup(ShutdownResource.BrowserBroadcast, StopBrowserWatchPartyBroadcast);
        Cleanup(ShutdownResource.LocalVideoBroadcast, StopLocalVideoWatchPartyBroadcast);
        Cleanup(ShutdownResource.LocalVideoSubtitles, localVideoSubtitleCache.Dispose);
        // Unified search disposes its source after all three providers finish.
        unifiedSearchCts = null;
        // Each search disposes its own source after its continuation has finished.
        youtubeSearchCts = null;
        Cleanup(ShutdownResource.InternetArchive, DisposeInternetArchive);
        Cleanup(ShutdownResource.WindowPositions, PersistPositions);

        // Discovery tasks own their cancellation source until their final continuation.
        browseVideosCts = null;

        subscriptionVideosCts = null;
        Cleanup(ShutdownResource.YouTubeAccountFeedToken, youtubeFeedLifetime.Dispose);

        Cleanup(ShutdownResource.ImagePreviews, imagePreviews.Dispose);
        Cleanup(ShutdownResource.Thumbnails, thumbnails.Dispose);
        Cleanup(ShutdownResource.PendingWindowImages, () =>
        {
            lock (windowTexturePublicationGate) ApplyPendingWindowTextures();
        });

        Cleanup(ShutdownResource.AlphaChatLinkPreviews, alphaChatLinkPreviews.Dispose);
        Cleanup(ShutdownResource.AlphaChatLinkImages, alphaChatLinkImages.Dispose);
        Cleanup(ShutdownResource.ChatLinkedImages, chatLinkedImages.Dispose);
        Cleanup(ShutdownResource.AlphaChat, alphaChat.Dispose);
        Cleanup(ShutdownResource.HomeHero, () => homeHero?.Dispose());
        Cleanup(ShutdownResource.PartyQueuePlayback, DisposePartyPlaybackQueue);
        Cleanup(ShutdownResource.PartyQueueConnection, DisposePartyQueueConnection);
        Cleanup(ShutdownResource.PartyCountdownConnection, DisposePartyCountdown);
        homeHero = null;
        Cleanup(ShutdownResource.TVFramePreviews, () =>
        {
            foreach (var texture in framePreviewTextures) texture?.Dispose();
            Array.Clear(framePreviewTextures);
        });
        Cleanup(ShutdownResource.CustomBackground, () => customBackground?.Dispose());
        customBackground = null;
    }

    // Shared by every partial that wants a play/pause/skip/volume-style glyph button instead of a
    // text label - Dalamud bundles FontAwesome already, no extra font asset needed.
    private static bool IconButton(FontAwesomeIcon icon)
    {
        using var iconFont = UiFonts.PushIcon();
        return ImGui.Button(icon.ToIconString());
    }

    private readonly struct ThemeScope : IDisposable
    {
        private const int ColorCount = 18;
        private const int StyleCount = 7;

        public ThemeScope(float scale)
        {
            ImGui.PushStyleColor(ImGuiCol.WindowBg, WindowBg);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, WindowBg);
            ImGui.PushStyleColor(ImGuiCol.PopupBg, CardBg);
            ImGui.PushStyleColor(ImGuiCol.Button, Accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentActive);
            ImGui.PushStyleColor(ImGuiCol.FrameBg, FrameBg);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, FrameBgHover);
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, AccentFieldActive);
            ImGui.PushStyleColor(ImGuiCol.CheckMark, AccentHover);
            ImGui.PushStyleColor(ImGuiCol.Header, AccentFieldBg);
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, AccentFieldHover);
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, AccentFieldActive);
            ImGui.PushStyleColor(ImGuiCol.TextSelectedBg, AccentInk(0.35f));
            ImGui.PushStyleColor(ImGuiCol.SeparatorHovered, AccentHover);
            ImGui.PushStyleColor(ImGuiCol.SeparatorActive, AccentActive);
            ImGui.PushStyleColor(ImGuiCol.SliderGrab, Accent);
            ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, AccentActive);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 12f * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 12f * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(12f, 10f) * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12f, 8f) * scale);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(8f, 6f) * scale);
        }

        public void Dispose()
        {
            ImGui.PopStyleVar(StyleCount);
            ImGui.PopStyleColor(ColorCount);
        }
    }

    private readonly struct NativeLayoutScope(bool enabled) : IDisposable
    {
        public void Dispose()
        {
            if (!enabled) return;
            // Detached mini windows use their own fixed sizing, not the last main-window scale.
            activeLayoutScale = FixedLayoutScale;
            activeNativeUiSizing = false;
        }
    }

    private readonly struct GlobalFontScaleScope : IDisposable
    {
        private readonly float previousScale;

        public GlobalFontScaleScope(float scale)
        {
            var io = ImGui.GetIO();
            previousScale = io.FontGlobalScale;
            io.FontGlobalScale = previousScale * scale;
            UiFonts.RefreshMetrics();
        }

        public void Dispose()
        {
            ImGui.GetIO().FontGlobalScale = previousScale;
            UiFonts.RefreshMetrics();
        }
    }
}

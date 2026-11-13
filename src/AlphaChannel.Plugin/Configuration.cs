using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.Crypto;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin;
namespace AlphaChannel.Plugin;
using AlphaChannel.Plugin.Video;

[Serializable]
internal sealed class ArchivedGroupChatState
{
    public long LastMessageAtUnix { get; set; }
    public int UnreadCount { get; set; }
}

[Serializable]
internal sealed class GameLibraryEntry
{
    public string Icon { get; set; } = "Gamepad";
    public string Hash { get; set; } = "";
    public string Name { get; set; } = "";
    public string Extension { get; set; } = "";
}

// Persisted queue data, kept separate from VideoQueueEntry so saved configuration
// does not depend on the queue's runtime implementation.
[Serializable]
internal sealed class VideoQueueRecord
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public double? DurationSeconds { get; set; }
    public string? ThumbnailUrl { get; set; }

    // Position to resume from when playback was paused or stopped.
    public double ResumePositionSeconds { get; set; }
}

[Serializable]
internal sealed class RecentlyWatchedVideoRecord
{
    public string Url { get; set; } = "";

    public string Title { get; set; } = "";

    public string? ThumbnailUrl { get; set; }

    public string ChannelName { get; set; } = "";

    public double WatchedSeconds { get; set; }

    public double DurationSeconds { get; set; }

    public DateTime LastWatchedUtc { get; set; }
}

[Serializable]
internal sealed class SavedRadioStationRecord
{
    public string Name { get; set; } = "";

    public string Url { get; set; } = "";

    public List<string> Genres { get; set; } = [];
}

[Serializable]
internal sealed class SavedImageRecord
{
    public Guid Id { get; set; } =
        Guid.NewGuid();

    public string Name { get; set; } =
        string.Empty;

    public string Url { get; set; } =
        string.Empty;

    public DateTime SavedAtUtc { get; set; } =
        DateTime.UtcNow;
}

[Serializable]
internal sealed class CachedBrowseVideoRecord
{
    public string Title { get; set; } =
        string.Empty;

    public string Url { get; set; } =
        string.Empty;

    public string ChannelName { get; set; } =
        string.Empty;

    public double? DurationSeconds { get; set; }

    public string? ThumbnailUrl { get; set; }

    public long? ViewCount { get; set; }
    public long? ViewCountObservedAtUnix { get; set; }

    public DateTimeOffset? UploadDate { get; set; }

    public string? ChannelId { get; set; }
}

internal enum PartyReactionVisibility
{
    ShowAll,
    TvOnly,
    ChatOnly,
    HideAll,
}

[Serializable]
internal sealed class Configuration : IPluginConfiguration
{
    public bool DiscordActivityEnabled { get; set; } = true;
    public bool DiscordActivityShowTitles { get; set; } = true;
    public HashSet<string> InitialBadgeChecksCompleted { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Halloween2026BadgeCompleted { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> YouTubeAgeRestrictedVideos { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> AlphaChatProtectedDrafts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Halloween2026BadgePending { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CommunityBadgeCompleted { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> MovieBuffBadgeCompleted { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Per-account progress protected with Windows DPAPI; no plaintext playback counter.
    public Dictionary<string, string> ActivityBadgeProtectedProgress { get; set; } = new();
    public Dictionary<string, HashSet<string>> ActivityBadgesCompleted { get; set; } = new();
    public Dictionary<string, string> MovieBuffProtectedProgress { get; set; } = new();
    public List<LuckyWheelDesign> SavedLuckyWheels { get; set; } = [];

    public Dictionary<string, string> HiddenYouTubeChannels { get; set; } = new();

    internal const string ProductionRelayServerUrl = "https://" + RelayServerOrigins.OriginalHost;

    // Keep the legacy default at 1 so configurations written before the
    // version field existed are treated as pre-baseline configurations.
    // New configurations receive the current version in ConfigurationMigration.
    public int Version { get; set; } = 1;
    public List<GameLibraryEntry> GameLibrary { get; set; } = new();
    public string? SelectedSnesLibraryHash { get; set; }
    public string? SelectedGameBoyLibraryHash { get; set; }
    public string? SelectedNesLibraryHash { get; set; }
    public string? SelectedGameBoyAdvanceLibraryHash { get; set; }
    public string? SelectedMasterSystemLibraryHash { get; set; }
    public string? SelectedGameGearLibraryHash { get; set; }

    public GameBroadcastQuality GameBroadcastQuality { get; set; } =
        GameBroadcastQuality.DataSaver;
    public int GameBroadcastFramesPerSecond { get; set; } = 24;

    public BrowserBroadcastQuality BrowserBroadcastQuality { get; set; } =
        BrowserBroadcastQuality.DataSaver;
    public int BrowserBroadcastFramesPerSecond { get; set; } = 20;
    public bool BrowserAdBlockEnabled { get; set; } = true;
    public string BrowserHomepageUrl { get; set; } = string.Empty;
    // Separate from the former GPU toggle so existing installations start with
    // the DRM-compatible JPEG option when the launch-page choice is introduced.
    public List<string> BrowserRedirectWhitelist { get; set; } = [];
    public bool UseAlphaChannelRedirectWhitelist { get; set; } = true;

    public LocalVideoBroadcastQuality LocalVideoBroadcastQuality { get; set; } =
        LocalVideoBroadcastQuality.DataSaver;
    public int LocalVideoBroadcastFramesPerSecond { get; set; } = 30;

    // First-run experience has been completed.
    public bool HasCompletedFirstLaunch { get; set; }
    // Saved separately so existing users can choose a size without repeating onboarding.
    public bool HasCompletedInterfaceSizingSetup { get; set; }

    public bool DisplayAlphaChannelStatusInHonorific { get; set; }

    public int HonorificStatusColorPreset { get; set; }

    public string RelayServerUrl { get; set; } = RelayServerProfile.ServerUrl;

    // A non-secret sentinel only. Activated after the first chargeable broadcast;
    // an unused installation can be removed without triggering a reset lock.
    public bool BroadcastAllowanceInitialized { get; set; }
    public Dictionary<string, BroadcastAllowance.AllowanceRepairStamp> BroadcastAllowanceRepairs { get; set; } = new(StringComparer.Ordinal);

    // Off by default — prod/dev stack switcher stays out of player Settings. Flip true in the
    // plugin config JSON only when you need to point this install at the isolated dev relay.

    // Verified per-character sessions retain their existing keys. Slot zero holds
    // the shared direct account or its durable, incomplete registration state.
    [Newtonsoft.Json.JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<ulong, CharacterSession> CharacterSessions { get; set; } = new();
    public bool GuestAccessNoticeSeen { get; set; }

    // No credentials live here. Kept readable in Release solely so returning
    // from a Debug test restores normal onboarding before opening the normal vault.
    public DebugAccountTestState? DebugAccountTest { get; set; }
    [Newtonsoft.Json.JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    internal bool DebugAccountTestActive { get; private set; }

    private string AccountVaultDirectory(string configDirectory)
    {
        var state = DebugAccountTest;
        if (state is null) return configDirectory;
        // Signup preview is temporarily disabled after acceptance testing.
        // Restore the preserved normal vault on the next plugin load; retain
        // the separate test vault so no test credentials are discarded.
        state.Apply(this, allowTest: false);
        DebugAccountTestActive = state.Loaded;
        return state.Loaded ? Path.Combine(configDirectory, "DebugAccountTest") : configDirectory;
    }

    // Slot zero is the shared local guest identity (also protected by the
    // existing session vault). An incomplete slot is registration retry state.
    internal CharacterSession? SessionForCharacter(ulong id) =>
        (CharacterSessions.GetValueOrDefault(id) is { AccountId.Length: > 0 } character ? character : null) ??
        (CharacterSessions.GetValueOrDefault(0UL) is { AccountId.Length: > 0 } guest ? guest : null);

    // Newtonsoft deserialize-only bridge for configurations written before
    // secrets moved into the encrypted vault. ShouldSerialize keeps this
    // plaintext property out of every newly saved configuration.
    [Newtonsoft.Json.JsonProperty("CharacterSessions")]
    private Dictionary<ulong, CharacterSession>? LegacyCharacterSessions
    {
        get => null;
        set
        {
            if (value is not null)
                CharacterSessions = value;
        }
    }

    private bool ShouldSerializeLegacyCharacterSessions() => false;

    // Keyed by AccountId (not LocalContentId) - DM identity belongs to the account, not whichever
    // character happens to be signed in as it. Base64 PKCS8 private key, DPAPI-protected on Windows
    // where available (see Encryption/KeyVault.cs) - the key never leaves this machine either way, this
    // is defense-in-depth against other local processes, not network protection.
    public Dictionary<string, string> DmPrivateKeys { get; set; } = new();

    // Retain protected keys when recovering an older identity; never discard a different key.
    public Dictionary<string, List<string>> DmPrivateKeyHistory { get; set; } = new();

    // Keyed by AccountId, same reasoning as DmPrivateKeys - a locally-cached copy of the raw stream
    // key purely for convenience redisplay on the Go Live page (the server only ever stores a hash,
    // see Server/Data/Entities.cs's StreamKey). If this local cache is ever lost, the only recovery
    // is hitting Regenerate - that's acceptable one-time friction, not a bug, since regenerating
    // also instantly invalidates anyone else's copy of the old key.
    [Newtonsoft.Json.JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, string> StreamKeys { get; set; } = new();

    // Durable deliberate-departure intent, scoped to server/account. Not a credential.
    public System.Collections.Concurrent.ConcurrentDictionary<string, bool> PendingPartyLeaves { get; set; } = new();

    [Newtonsoft.Json.JsonProperty("StreamKeys")]
    private Dictionary<string, string>? LegacyStreamKeys
    {
        get => null;
        set
        {
            if (value is not null)
                StreamKeys = value;
        }
    }

    private bool ShouldSerializeLegacyStreamKeys() => false;

    private int volume = 100;
    public int Volume
    {
        get => volume;
        set => volume = Math.Clamp(value, 0, 130);
    }
    public bool Muted { get; set; }
    public Dictionary<string, int> SkyRailPersonalBestScores { get; set; } = new();
    public bool SkyRailMusicEnabled { get; set; } = true;
    public bool SkyRailSoundEffectsEnabled { get; set; } = true;
    public int SkyRailMusicVolume { get; set; } = 100;
    public int SkyRailSoundEffectsVolume { get; set; } = 100;
    public bool ChocoRaceMusicEnabled { get; set; } = true;
    public bool ChocoRaceSoundEffectsEnabled { get; set; } = true;
    public bool ChocoRaceEmotesEnabled { get; set; } = true;
    public bool RouletteMusicEnabled { get; set; } = true;
    public bool PokerMusicEnabled { get; set; } = true;
    public bool RouletteSoundEffectsEnabled { get; set; } = true;
    public bool RouletteEmotesEnabled { get; set; } = true;
    public bool BingoMusicEnabled { get; set; } = true;
    public bool BingoSoundEffectsEnabled { get; set; } = true;
    public bool BingoEmotesEnabled { get; set; } = true;

    //
    // Use the FFmpeg frequency spectrum by default.
    // ClassicBars preserves the existing 13-bar shader.
    //
    public AudioVisualizerMode AudioVisualizerMode { get; set; } =
        AudioVisualizerMode.FfmpegSpectrum;

    public string DjBroadcastName { get; set; } = "Live DJ broadcast";
    public List<SpotifySavedLink> SpotifySavedLinks { get; set; } = [];
    public bool SpotifyCoverOnTv { get; set; }
    public AlphaChannel.BroadcastStudio.StudioSettings StudioSettings { get; set; } = new();
    public List<AlphaChannel.BroadcastStudio.StudioTrack> StudioQueue { get; set; } = [];
    public List<AlphaChannel.BroadcastStudio.StudioPlaylist> StudioPlaylists { get; set; } = [];
    public string MusicLibraryFolder { get; set; } = "";
    public List<AlphaChannel.BroadcastStudio.StudioTrack> MusicLibrary { get; set; } = [];
    public int StudioTalkKey { get; set; } = 119; // F8
    public List<Video.SavedMusicScene> SavedMusicScenes { get; set; } = [];
    public Video.SavedMusicScene? CurrentMusicScene { get; set; }

    public bool TvAudioEffectsEnabled { get; set; } = false;
    public bool TvAudioHaloEnabled { get; set; } = false;
    public bool TvAudioLedStripsEnabled { get; set; } = false;
    public bool TvAudioLightRingsEnabled { get; set; } = false;
    public bool TvAudioSparklesEnabled { get; set; } = false;
    internal bool AnyTvAudioEffectsEnabled => TvAudioEffectsEnabled || TvAudioHaloEnabled ||
        TvAudioLedStripsEnabled || TvAudioLightRingsEnabled || TvAudioSparklesEnabled;

    public bool PreferClassicBarsWhenViewing { get; set; } = false;

    //
    // SNES Player 1 keyboard controls.
    //
    // Stored as VirtualKey integer values so existing config files
    // continue to deserialize cleanly and bindings can be changed
    // without touching the emulator core.
    //

    public int SnesKeyUp { get; set; } =
        (int)VirtualKey.UP;

    public int SnesKeyDown { get; set; } =
        (int)VirtualKey.DOWN;

    public int SnesKeyLeft { get; set; } =
        (int)VirtualKey.LEFT;

    public int SnesKeyRight { get; set; } =
        (int)VirtualKey.RIGHT;

    public int SnesKeyA { get; set; } =
        (int)VirtualKey.X;

    public int SnesKeyB { get; set; } =
        (int)VirtualKey.Z;

    public int SnesKeyX { get; set; } =
        (int)VirtualKey.S;

    public int SnesKeyY { get; set; } =
        (int)VirtualKey.A;

    public int SnesKeyL { get; set; } =
        (int)VirtualKey.Q;

    public int SnesKeyR { get; set; } =
        (int)VirtualKey.W;

    public int SnesKeyStart { get; set; } =
        (int)VirtualKey.T;

    public int SnesKeySelect { get; set; } =
        (int)VirtualKey.RSHIFT;

    // Shared Player 1 controller layout. Each emulator uses the controls its
    // original console provides; values map directly to Dalamud GamepadButtons.
    public int GamepadUp { get; set; } = (int)GamepadButtons.DpadUp;
    public int GamepadDown { get; set; } = (int)GamepadButtons.DpadDown;
    public int GamepadLeft { get; set; } = (int)GamepadButtons.DpadLeft;
    public int GamepadRight { get; set; } = (int)GamepadButtons.DpadRight;
    public int GamepadA { get; set; } = (int)GamepadButtons.East;
    public int GamepadB { get; set; } = (int)GamepadButtons.South;
    public int GamepadX { get; set; } = (int)GamepadButtons.North;
    public int GamepadY { get; set; } = (int)GamepadButtons.West;
    public int GamepadL { get; set; } = (int)GamepadButtons.L1;
    public int GamepadR { get; set; } = (int)GamepadButtons.R1;
    public int GamepadStart { get; set; } = (int)GamepadButtons.Start;
    public int GamepadSelect { get; set; } = (int)GamepadButtons.Select;

    /// <summary>
    /// UTC Unix timestamp until which YouTube PO-token compatibility
    /// mode should remain active.
    ///
    /// Zero means normal Android playback.
    /// </summary>
    public long YouTubePoCompatibilityUntilUnixSeconds
    {
        get;
        set;
    }

    /// <summary>
    /// When enabled, all YouTube videos use the higher-quality mweb
    /// PO-token route instead of beginning with the faster Android route.
    ///
    /// Disabled preserves the automatic Android → PO-token fallback policy.
    /// </summary>
    public bool PreferHighQualityYouTubeVideos
    {
        get;
        set;
    }


    /// <summary>
    /// Explicit consent to use the YouTube account signed into Alpha
    /// Channel's embedded browser. Detecting a browser login never enables
    /// this automatically.
    /// </summary>
    public bool YouTubeEmbeddedBrowserSessionEnabled { get; set; }
    public bool YouTubeSessionNeedsReconnect { get; set; }

    public string? YouTubeConnectedDisplayName { get; set; }
    public string? YouTubeConnectedChannelName { get; set; }
    public string? YouTubeConnectedHandle { get; set; }
    public string? YouTubeConnectedAvatarUrl { get; set; }

    // Account likes are read-only: liking a video in Alpha Channel never
    // changes the user's YouTube account.
    public bool SyncYouTubeLikedVideos { get; set; } = true;

    public List<CachedBrowseVideoRecord> YouTubeLikedVideoCache { get; set; } = new();

    public DateTime YouTubeLikedVideoCacheUpdatedUtc { get; set; }

    public DateTime YouTubeLikedVideosLastManualFetchUtc { get; set; }

    public DateTime YouTubeLikedVideosLastAttemptUtc { get; set; }

    // Account subscription-feed videos are separate from locally managed channel uploads.
    public bool SyncYouTubeSubscriptionVideos { get; set; } = true;

    public List<CachedBrowseVideoRecord> YouTubeSubscriptionVideoCache { get; set; } = new();

    public DateTime YouTubeSubscriptionVideoCacheUpdatedUtc { get; set; }

    public DateTime YouTubeSubscriptionVideosLastManualFetchUtc { get; set; }

    public DateTime YouTubeSubscriptionVideosLastAttemptUtc { get; set; }

    public bool SyncYouTubeRecommendedVideos { get; set; } = true;
    public List<AlphaChannel.Plugin.Video.InternetArchiveItem> ArchivePopularItems { get; set; } = new();
    public List<AlphaChannel.Plugin.Video.DailymotionVideo> DailymotionPopularVideos { get; set; } = new();

    public List<AlphaChannel.Plugin.YouTube.PopularVideoRecord> YouTubePopularVideos { get; set; } = new();
    // Shared Worker ranking only. The old directory cache is never uploaded.
    public List<AlphaChannel.Plugin.YouTube.PopularVideoRecord> YouTubeSharedPopularVideos { get; set; } = new();
    public List<CachedBrowseVideoRecord> YouTubeRecommendedVideoCache { get; set; } = new();
    public DateTime YouTubeRecommendedVideoCacheUpdatedUtc { get; set; }
    public DateTime YouTubeRecommendedVideosLastManualFetchUtc { get; set; }
    public DateTime YouTubeRecommendedVideosLastAttemptUtc { get; set; }

    // Cooldowns survive a plugin restart so reconnecting cannot immediately
    // repeat requests after YouTube has rate-limited the current session.
    public DateTime YouTubeAccountRequestsPausedUntilUtc { get; set; }

    public DateTime YouTubeGuestRequestsPausedUntilUtc { get; set; }

    // Anonymous installation identity used only for guest activity counting.
    public string GuestPresenceWorkerUrl { get; set; } = GuestPresenceClient.DefaultUrl;
    public string GuestPresenceInstallationId { get; set; } = string.Empty;
    public DateTime GuestPresenceNextUpdateUtc { get; set; }
    public DateTime GuestPresenceSessionExpiresUtc { get; set; }

    // Existing configurations default to Chromium, which was the original
    // browser used by the YouTube account connection flow.
    public bool YouTubeAccountUseWebView2 { get; set; }

    // Keyed by IClientState.LocalContentId - the display name a player picked is tied to the FFXIV
    // character they were playing when they picked it, not to the Windows/plugin install, so an alt
    // gets its own prompt instead of inheriting the main character's name.
    public Dictionary<ulong, string> CharacterDisplayNames { get; set; } = new();
    public List<VideoQueueRecord> VideoQueue { get; set; } = new();
    public AlphaChannel.Plugin.Core.PartyQueueRecovery? PartyQueueRecovery { get; set; }

    //
    // Start empty so Newtonsoft does not append deserialized profiles
    // after three pre-created null entries. QueueManager fills the list
    // to the currently allowed slot count after configuration is loaded.
    //
    public List<SavedQueueProfile?> SavedQueueProfiles { get; set; } =
        new();

    public int ActiveQueueSlot { get; set; } = 0;

    // Shared across every saved queue. Existing configurations that do
    // not contain this property receive the enabled default.
    public bool AutoPlayNextQueueVideo { get; set; } = true;

    // YouTube videos favourited by the player.
    // Store stable YouTube video IDs rather than full URLs.
    public List<string> FavouriteYouTubeVideoIds { get; set; } =
        new();

    // Stable activity IDs selected from The Arcade.
    public List<string> FavouriteArcadeActivityIds { get; set; } =
        new();

    //
    // Persisted metadata for favourited YouTube videos.
    //
    // The list follows FavouriteYouTubeVideoIds ordering, with the most
    // recently favourited video first.
    //
    public List<CachedBrowseVideoRecord> FavouriteVideoCache
    {
        get;
        set;
    } = new();

    public DateTime FavouriteVideoCacheUpdatedUtc
    {
        get;
        set;
    }

    public string? FavouriteVideoCacheSignature
    {
        get;
        set;
    }

    //
    // Persisted Browse Videos discovery cache.
    //
    // Videos are grouped by the topic that fetched them because the same
    // YouTube video may legitimately appear under multiple topics.
    //
    public Dictionary<string, List<CachedBrowseVideoRecord>>
        BrowseVideoTopicCache
    {
        get;
        set;
    } = new(StringComparer.OrdinalIgnoreCase);

    // Number of successful playback starts for videos found in each topic cache.
    public Dictionary<string, int> BrowseVideoTopicPlayCounts { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public DateTime BrowseVideoCacheUpdatedUtc
    {
        get;
        set;
    }

    public string? BrowseVideoCacheTopicSignature
    {
        get;
        set;
    }

    //
    // Persisted FFXIV homepage discovery results, independent of selected topics.
    //
    public List<CachedBrowseVideoRecord> FfxivVideoCache { get; set; } = new();

    public DateTime FfxivVideoCacheUpdatedUtc { get; set; }

    // The homepage carousel has its own recent-upload and metadata cache.
    public List<CachedBrowseVideoRecord> FeaturedSlideCache { get; set; } = new();

    public DateTime FeaturedSlideCacheUpdatedUtc { get; set; }

    //
    // Persisted subscription feed cache.
    //
    // Entries are grouped by stable YouTube channel ID so the UI can filter
    // subscriptions and apply the 5/10/15 limit independently per channel.
    //
    public Dictionary<string, List<CachedBrowseVideoRecord>>
        SubscriptionVideoChannelCache
    {
        get;
        set;
    } = new(StringComparer.OrdinalIgnoreCase);

    public DateTime SubscriptionVideoCacheUpdatedUtc
    {
        get;
        set;
    }

    public string? SubscriptionVideoCacheSignature
    {
        get;
        set;
    }

    //
    // Twitch channels favourited by the player.
    //
    // Store normalized lower-case channel usernames rather than full URLs.
    // Live status and stream metadata are temporary and are not written to
    // configuration.
    //

    public List<string> FavouriteTwitchChannels { get; set; } =
        new();

    public string OwnTwitchChannel { get; set; } = string.Empty;

    public List<RecentlyWatchedVideoRecord> RecentlyWatchedVideos { get; set; } =
        new();

    //
    // User-added direct internet-radio streams.
    //

    public List<SavedRadioStationRecord> SavedRadioStations { get; set; } =
        new();

    public string HomeRadioStation1Url { get; set; } =
        "https://radio.truckers.fm/";

    public string HomeRadioStation2Url { get; set; } =
        "https://listen.181fm.com/181-power_128k.mp3";

    //
    // Personal image gallery. Only the public image URL and local
    // presentation details are persisted; downloaded preview textures
    // remain part of the temporary image-preview cache.
    //

    public List<SavedImageRecord> SavedImages { get; set; } =
        new();

    // YouTube channels subscribed to inside AlphaChannel.
    // Store stable channel IDs rather than display names.
    public List<string> SubscribedYouTubeChannelIds { get; set; } = new();

    // Display-name cache for locally-managed YouTube subscriptions.
    // The channel ID remains the real identity.
    public Dictionary<string, string> SubscribedYouTubeChannelNames { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<ScreenPositionPreset> ScreenPresets { get; set; } = new();

    // Plugin window chrome palette - see UiTheme.cs / ThemeCatalog. Defaults to Purple (mockup).
    public UiTheme UiTheme { get; set; } = UiTheme.Purple;

    // Window/sidebar/card surfaces — independent of accent color. Theme = use the accent pack's defaults.
    public UiBackground UiBackground { get; set; } = UiBackground.Theme;

    // Decorative window chrome, independent of accent and surface colours.
    public UiDecorationTheme UiDecorationTheme { get; set; } = UiDecorationTheme.Classic;

    public bool SidebarCollapsed { get; set; } = false;

    // Selected size level persists independently of the drag-resized dimensions.
    public UiWindowSizePreset WindowSizePreset { get; set; } = UiWindowSizePreset.Design;

    // Opt-in native sizing experiment. Existing installations retain their original layout.
    public bool NativeUiSizing { get; set; } = false;

    public float WindowWidth { get; set; } = 1220f;

    public float WindowHeight { get; set; } = 840f;


    // Copied under the plugin config folder when the player applies a custom image (png/jpg/webp).
    public string? CustomBackgroundPath { get; set; }

    // Dark overlay strength over the custom image (0 = full photo, 1 = fully dimmed).
    public float CustomBackgroundDim { get; set; } = 0.30f;

    // Home welcome illustration (couch / castle art). Off = text + CTA only.
    public bool ShowHomeHeroImage { get; set; } = true;

    // Optional FFXIV discovery shelf on the media-hub Home page.
    public bool ShowFfxivYouTubeSection { get; set; } = true;

    // Subscribed YouTube topic preferences.
    //
    // New installations begin with no subscribed topics. Existing values
    // already written to configuration JSON remain unchanged.
    public bool TrendingGaming { get; set; }
    public bool TrendingMMORPG { get; set; }
    public bool TrendingFinalFantasy { get; set; }
    public bool TrendingFfxiv { get; set; }
    public bool TrendingAnime { get; set; }
    public bool TrendingMovies { get; set; }
    public bool TrendingTvShows { get; set; }
    public bool TrendingMusic { get; set; }
    public bool TrendingMemes { get; set; }

    public bool TrendingCartoons { get; set; }
    public bool TrendingHorror { get; set; }
    public bool TrendingSciFi { get; set; }
    public bool TrendingComedy { get; set; }
    public bool TrendingMinecraft { get; set; }
    public bool TrendingDisney { get; set; }
    public bool TrendingFantasy { get; set; }

    public bool TrendingWildlife { get; set; }
    public bool TrendingArchitecture { get; set; }
    public bool TrendingScience { get; set; }
    public bool TrendingSpace { get; set; }
    public bool TrendingHistory { get; set; }
    public bool TrendingTechnology { get; set; }
    public bool TrendingUrbanExploration { get; set; }

    public bool TrendingPets { get; set; }
    public bool TrendingFood { get; set; }
    public bool TrendingTravel { get; set; }
    public bool TrendingCars { get; set; }
    public bool TrendingSports { get; set; }
    public bool TrendingArtsAndCrafts { get; set; }
    public bool TrendingCosplaying { get; set; }
    public bool TrendingDiy { get; set; }
    public bool TrendingFashion { get; set; }

    // Optional replacement for the bundled Home hero art (copied under Backgrounds/).
    public string? CustomHomeHeroPath { get; set; }

    // Include this account's TV reactions in the Watch Party activity feed.
    // The in-world TV reaction is always sent regardless of this preference.
    public bool ShowReactionsInPartyChat { get; set; } = true;

    // Hide every reaction activity row locally. This does not affect the
    // in-world TV or what other Watch Party members see in their chat.
    public bool HideAllReactionsInPartyChat { get; set; }

    // Local, per-account reaction visibility. Missing entries intentionally
    // mean ShowAll so new party members use the least surprising default.
    public Dictionary<string, PartyReactionVisibility> PartyReactionVisibilityByUserId { get; set; } =
        new(StringComparer.Ordinal);

    // Remember the full window and minimized capsule positions across minimize, restore, and reopen.
    public Vector2? MaximizedPosition { get; set; }
    public Vector2? MinimizedPosition { get; set; }

    public TvDutyBehaviour DistanceTvBehaviour { get; set; } = TvDutyBehaviour.MiniPlayer;
    public ViewerPlaybackStartBehaviour ViewerPlaybackStartBehaviour { get; set; } = ViewerPlaybackStartBehaviour.SpawnTv;
    public int LiveBroadcastExtraBufferSeconds { get; set; }
    public TvInterruptionBehaviour CutsceneTvBehaviour { get; set; } = TvInterruptionBehaviour.NoEffect;
    public bool ShowControlsDuringGpose { get; set; }
    public TvInterruptionBehaviour CombatTvBehaviour { get; set; } = TvInterruptionBehaviour.NoEffect;
    public TvDutyBehaviour InstancedContentTvBehaviour { get; set; } = TvDutyBehaviour.MiniPlayer;
    public Vector2? MiniPlayerPosition { get; set; }
    public Vector2? PlaybackHudPosition { get; set; }
    public AlphaChannel.Plugin.Video.TvFrameSelection TvFrame { get; set; } = new();
    public bool UseHostTvFrame { get; set; } = true;
    // Legacy value retained only to migrate saved show/hide choices to MiniDockDisplay.
    public bool HideMiniDock { get; set; } = true;
    public MiniDockVisibility MiniDockDisplay { get; set; } = MiniDockVisibility.AlwaysDisplay;
    public bool PartyDirectoryFeaturedFirst { get; set; } = true;
    public bool AutoJoinAlphaChat { get; set; } = true;
    public Dictionary<string, string> AlphaChatUsernameColours { get; set; } = new();
    public Dictionary<string, CachedUsernameStyle> UsernameStyleCache { get; set; } = new();
    public Dictionary<string, string> AlphaChatUsernameEffects { get; set; } = new();
    public HashSet<string> UsernameStylePreferencesSaved { get; set; } = new();
    public bool NotificationSoundsEnabled { get; set; } = false;
    public bool LimitNotificationSoundsToTwoMinutes { get; set; } = true;
    public bool PartyPresenceNotificationsEnabled { get; set; } = true;
    public bool ReactLiveNotificationsEnabled { get; set; } = true;
    public int NotificationSoundIndex { get; set; } = 0;
    public List<string> ExpiredWatchPartyInviteIds { get; set; } = [];
    // Keep the stored name for compatibility; this now holds direct and group chat archives.
    public Dictionary<string, ArchivedGroupChatState> ArchivedGroupChats { get; set; } = [];
    public Dictionary<string,long> EventNotificationHistory { get; set; } = [];
    public Dictionary<string,int> EventKnownVersions { get; set; } = [];
    public float MiniPlayerWidth { get; set; } = 560f;
    public float MiniPlayerHeight { get; set; } = 380f;
    public Vector2? MiniChatPosition { get; set; }
    public float MiniChatWidth { get; set; } = 390f;
    public float MiniChatHeight { get; set; } = 510f;

    // Walk-up auto-view for public DJ sets and streams. Off by default — probing nearby players
    // can feel like the UI is reloading until a real public host is found.
    public bool AutoWatchNearby { get; set; } = false;
    public bool WorldMode { get; set; } = true;

    // How close (yalms) another player must be before AutoWatchNearby tries to join them.
    public float AutoWatchRadiusYalms { get; set; } = 18f;

    public Vector3 ScreenPosition { get; set; }

    public float ScreenYaw { get; set; }

    //
    // Retained for compatibility with older configuration files.
    //
    public float ScreenScale { get; set; } = 1f;

    public int TvGizmoKey { get; set; } = (int)VirtualKey.TAB;

    public bool DisableFixedScreenScaleRatio { get; set; }

    public float ScreenWidthScale { get; set; } = 1f;

    public float ScreenHeightScale { get; set; } = 1f;

    // Local presentation preferences. These are deliberately excluded from
    // Watch Party state, placement exports and screen presets so every viewer
    // controls how their own client treats content around the TV.
    public float ScreenNameplateHideRadiusYalms { get; set; } = 3f;

    public bool KeepGameUiVisibleOverTv { get; set; } = true;

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    [NonSerialized]
    private LocalSecretVault? localSecretVault;

    [NonSerialized]
    private bool persistenceEnabled = true;

    [NonSerialized]
    private readonly object persistenceGate = new();
    private long persistenceRevision;

    internal void StoreDmPrivateKey(string accountId, string protectedKey)
    {
        // Key generation runs on message workers; serialize mutation with saves that
        // enumerate this dictionary. Keep the existing on-disk dictionary format.
        lock (persistenceGate)
        {
            foreach (var old in DmPrivateKeys.Where(pair =>
                         string.Equals(pair.Key, accountId, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                if (old.Value != protectedKey)
                {
                    if (!DmPrivateKeyHistory.TryGetValue(accountId, out var history))
                        DmPrivateKeyHistory[accountId] = history = [];
                    if (!history.Contains(old.Value)) history.Add(old.Value);
                }
                DmPrivateKeys.Remove(old.Key);
            }
            DmPrivateKeys[accountId] = protectedKey;
            Save();
        }
    }

    internal string? GetDmPrivateKey(string accountId)
    {
        lock (persistenceGate)
            return DmPrivateKeys.FirstOrDefault(pair =>
                string.Equals(pair.Key, accountId, StringComparison.OrdinalIgnoreCase)).Value;
    }

    internal string[] GetDmPrivateKeyRecoveryCandidates(string accountId)
    {
        string[] history;
        lock (persistenceGate)
            history = DmPrivateKeyHistory.Where(pair => string.Equals(pair.Key, accountId, StringComparison.OrdinalIgnoreCase))
                .SelectMany(pair => pair.Value).ToArray();
        if (pluginInterface is null) return history;
        return [.. history, .. DmPrivateKeyBackupReader.Read(pluginInterface.ConfigDirectory.FullName,
            Path.GetFileNameWithoutExtension(pluginInterface.ConfigFile.Name), accountId)];
    }

    public void Initialize(
        IDalamudPluginInterface pi,
        bool enablePersistence = true)
    {
        pluginInterface = pi;
        persistenceEnabled = enablePersistence;
        var containedLegacySecrets = CharacterSessions.Count > 0 || StreamKeys.Count > 0;
        // Select the endpoint and matching vault before any client is created.
        RelayServerUrl = RelayServerProfile.ServerUrl;
        var previousTestBank = DebugAccountTest?.Loaded ?? false;
        var vaultDirectory = AccountVaultDirectory(pi.ConfigDirectory.FullName);
        localSecretVault = RelayServerProfile.OpenVault(this, vaultDirectory);
        if (localSecretVault.Restore(this) && (containedLegacySecrets || previousTestBank != DebugAccountTestActive))
        {
            // Re-save immediately so JsonIgnore removes the old plaintext copies
            // after the encrypted vault has safely received them.
            Save();
        }
    }

    // Capture public configuration on its owning thread; write the detached
    // snapshot on a worker. Secret changes still use Save() and its vault.
    internal void SavePublicStateDeferred()
    {
        if (!persistenceEnabled) return;
        string json; long revision;
        lock (persistenceGate)
        {
            revision = ++persistenceRevision;
            json = Newtonsoft.Json.JsonConvert.SerializeObject(this);
        }
        _ = Task.Run(async () =>
        {
            for(var attempt=0;attempt<3;attempt++) {
                try {
                    var snapshot=Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(json);
                    lock(persistenceGate) {
                        if(revision!=persistenceRevision || !persistenceEnabled)return;
                        if(snapshot is not null)pluginInterface?.SavePluginConfig(snapshot);
                    }
                    return;
                } catch {
                    if(attempt==2)AepLog.Warning("[Configuration] Deferred public-state save failed; final shutdown save will retry.");
                    else await Task.Delay(250*(attempt+1)).ConfigureAwait(false);
                }
            }
        });
    }

    // Invalidate/drain older worker writes under the same gate and persist the latest
    // public state at unload, including changes still inside a debounce interval.
    internal void FlushPublicState()
    {
        lock(persistenceGate) {
            if(!persistenceEnabled)return;
            persistenceRevision++;
            var json=Newtonsoft.Json.JsonConvert.SerializeObject(this);
            var snapshot=Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(json);
            if(snapshot is not null)pluginInterface?.SavePluginConfig(snapshot);
        }
    }

    public void Save()
    {
        // A configuration created as a safe fallback for a newer, unsupported
        // schema must never overwrite that newer file.
        if (!persistenceEnabled)
            return;

        lock (persistenceGate)
        {
            persistenceRevision++;
            localSecretVault?.Save(this);
            pluginInterface?.SavePluginConfig(this);
        }
    }

    internal bool SaveAccountCredentials()
    {
        lock (persistenceGate)
            return persistenceEnabled && localSecretVault is not null && localSecretVault.Save(this);
    }
}

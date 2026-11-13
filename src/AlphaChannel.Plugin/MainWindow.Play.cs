using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.BroadcastAllowance;
namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
 


    //
    // =============================================================
    // Play Games state
    // =============================================================
    //

    private enum GameSystem
    {
        Snes,
        GameBoy,
        Nes,
        GameBoyAdvance,
        MasterSystem,
        GameGear
    }


    private GameSystem selectedGameSystem =
          GameSystem.Snes;

    //
    // "Armed" is intentionally separate from the encoder's IsBroadcasting
    // state. The host sees the broadcast as enabled while the encoder itself
    // runs only when at least one viewer is present.
    //
    private bool gameBroadcastArmed;
    private bool gameBroadcastForSnes;
    private GameSystem gameBroadcastSystem = GameSystem.Snes;
    private bool gameBroadcastStartFailed;
    private bool gameBroadcastExpectedRunning;
    private DateTime nextGameBroadcastRetryUtc = DateTime.MinValue;
    private string? gameBroadcastPublishUrl;
    private string? gameBroadcastHlsUrl;
    private DateTime gameBroadcastViewerReadyUtc = DateTime.MaxValue;
    private DateTime gameBroadcastLastInputUtc = DateTime.MinValue;
    private bool gameBroadcastPausedForInactivity;
    private Guid gameBroadcastStatusId;

    private static readonly TimeSpan GameBroadcastInactivityTimeout =
        TimeSpan.FromMinutes(10);
    private const string GameBroadcastStatusUrlPrefix =
        "alphachannel://game-broadcast/";

    // MediaMTX does not expose the HLS playlist until FFmpeg has supplied the
    // first keyframe and segment. Keep viewers on the waiting screen during
    // that short startup window instead of handing mpv a URL that still 404s.
    private static readonly TimeSpan GameBroadcastViewerWarmup =
        TimeSpan.FromSeconds(4);

    internal bool IsGameBroadcastArmed => gameBroadcastArmed;

    internal string? ActiveGameBroadcastHlsUrl =>
        gameBroadcastArmed &&
        !gameBroadcastPausedForInactivity &&
        GameBroadcastEncoderRunning &&
        DateTime.UtcNow >= gameBroadcastViewerReadyUtc
            ? gameBroadcastHlsUrl
            : null;

    private bool GameBroadcastEncoderRunning => gameBroadcastSystem switch
    {
        GameSystem.Snes => screenController.Engine.IsSnesBroadcasting,
        GameSystem.Nes => screenController.Engine.IsNesBroadcasting,
        GameSystem.GameBoyAdvance => screenController.Engine.IsGameBoyAdvanceBroadcasting,
        GameSystem.MasterSystem => screenController.Engine.IsMasterSystemBroadcasting,
        GameSystem.GameGear => screenController.Engine.IsGameGearBroadcasting,
        _ => screenController.Engine.IsGameBoyBroadcasting,
    };

    internal string? ActiveGameBroadcastStatusUrl =>
        !gameBroadcastArmed
            ? null
            : gameBroadcastPausedForInactivity
                ? GameBroadcastStatusUrlPrefix + "paused/" + gameBroadcastStatusId.ToString("N")
                : !GameBroadcastEncoderRunning ||
                  DateTime.UtcNow < gameBroadcastViewerReadyUtc
                    ? GameBroadcastStatusUrlPrefix + "resuming/" + gameBroadcastStatusId.ToString("N")
                    : null;

    internal bool TryGetGameBroadcastStatus(string? url, out string status)
    {
        status = string.Empty;
        if (url?.StartsWith(GameBroadcastStatusUrlPrefix, StringComparison.OrdinalIgnoreCase) != true)
            return false;

        var state = url[GameBroadcastStatusUrlPrefix.Length..];
        if (state.StartsWith("paused/", StringComparison.OrdinalIgnoreCase))
            status = "Broadcast paused";
        else if (state.StartsWith("resuming/", StringComparison.OrdinalIgnoreCase))
            status = "Broadcast resuming";
        return status.Length > 0;
    }

    internal string? ActiveGameBroadcastTitle
    {
        get
        {
            if (!gameBroadcastArmed)
            {
                return null;
            }

            var romPath = gameBroadcastSystem switch
            {
                GameSystem.Snes => snesSelectedRomPath,
                GameSystem.Nes => nesSelectedRomPath,
                GameSystem.GameBoyAdvance => gameBoyAdvanceSelectedRomPath,
                GameSystem.MasterSystem => masterSystemSelectedRomPath,
                GameSystem.GameGear => gameGearSelectedRomPath,
                _ => gameBoySelectedRomPath
            };

            var gameName =
                string.IsNullOrWhiteSpace(
                    romPath)
                    ? string.Empty
                    : LibraryGameName(
                        romPath);

            if (!string.IsNullOrWhiteSpace(
                    gameName))
            {
                return $"Playing: {gameName}";
            }

            return gameBroadcastSystem switch
            {
                GameSystem.Snes => "SNES Gameplay",
                GameSystem.Nes => "NES Gameplay",
                GameSystem.GameBoyAdvance => "Game Boy Advance Gameplay",
                GameSystem.MasterSystem => "Master System / SG-1000 Gameplay",
                GameSystem.GameGear => "Game Gear Gameplay",
                _ => "Game Boy Gameplay"
            };
        }
    }


    private string snesSelectedRomPath =
        string.Empty;

    private string? snesLaunchError;

    private readonly PluginFilePicker snesFileDialog =
        new();


    private string gameBoySelectedRomPath =
        string.Empty;

    private string? gameBoyLaunchError;

    private readonly PluginFilePicker gameBoyFileDialog =
        new();

    private string nesSelectedRomPath = string.Empty;
    private string? nesLaunchError;
    private readonly PluginFilePicker nesFileDialog = new();

    private string gameBoyAdvanceSelectedRomPath = string.Empty;
    private string? gameBoyAdvanceLaunchError;
    private readonly PluginFilePicker gameBoyAdvanceFileDialog = new();

    private string masterSystemSelectedRomPath = string.Empty;
    private string? masterSystemLaunchError;
    private readonly PluginFilePicker masterSystemFileDialog = new();

    private string gameGearSelectedRomPath = string.Empty;
    private string? gameGearLaunchError;
    private readonly PluginFilePicker gameGearFileDialog = new();



    private bool snesRomSourcesPopupRequested;

    private bool StartGameWatchPartyBroadcast()
    {
        if (!CanStartOtherStudioMedia()) return false;
        if (stream.Mode !=
                   StreamMode.Hosting)
        {

            return false;
        }

        if (!CanStartBroadcastWithAllowance(BroadcastAllowanceMethod.RetroGame))
        {
            return false;
        }

        var engine =
            screenController.Engine;

        var isSnes =
            engine.IsPlayingSnes;

        var isGameBoy =
            engine.IsPlayingGameBoy;

        var isNes =
            engine.IsPlayingNes;

        var isGameBoyAdvance =
            engine.IsPlayingGameBoyAdvance;

        var isMasterSystem =
            engine.IsPlayingMasterSystem;

        var isGameGear =
            engine.IsPlayingGameGear;

        if (!isSnes &&
            !isGameBoy &&
            !isNes &&
            !isGameBoyAdvance &&
            !isMasterSystem &&
            !isGameGear)
        {
            return false;
        }

        if (CurrentSession is not { } session)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Sign in before broadcasting gameplay.");

            return false;
        }

        var streamKey =
     Plugin.Cfg.StreamKeys
         .GetValueOrDefault(
             session.AccountId);

        if (string.IsNullOrWhiteSpace(
                streamKey))
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] No stream key is available. Generate one from Settings > Account first.");

            return false;
        }

        //
        // Stop any previous encoder instance before arming a new source.
        //
        StopGameBroadcastEncoder();

        gameBroadcastForSnes =
            isSnes;

        gameBroadcastSystem = isSnes ? GameSystem.Snes : isNes ? GameSystem.Nes : isGameBoyAdvance ? GameSystem.GameBoyAdvance : isMasterSystem ? GameSystem.MasterSystem : isGameGear ? GameSystem.GameGear : GameSystem.GameBoy;

        gameBroadcastPublishUrl =
            $"{BuildRtmpServer()}/{streamKey}";

        gameBroadcastHlsUrl =
            $"{BuildMyHlsUrl(session)}" +
            $"?broadcast={Guid.NewGuid():N}&ac_content=retro-games";

        gameBroadcastStartFailed =
            false;
        gameBroadcastExpectedRunning = false;
        nextGameBroadcastRetryUtc = DateTime.MinValue;

        gameBroadcastViewerReadyUtc =
            DateTime.MaxValue;

        gameBroadcastLastInputUtc = DateTime.UtcNow;
        gameBroadcastPausedForInactivity = false;
        gameBroadcastStatusId = Guid.NewGuid();

        gameBroadcastArmed =
            true;

        if (isSnes)
        {
            snesLaunchError =
                null;
        }
        else
        {
            if (isNes) nesLaunchError = null;
            else if (isGameBoyAdvance) gameBoyAdvanceLaunchError = null;
            else if (isMasterSystem) masterSystemLaunchError = null;
            else if (isGameGear) gameGearLaunchError = null;
            else gameBoyLaunchError = null;
        }

        //
        // Publish the game status immediately, but do not advertise the HLS
        // URL until FFmpeg has started and MediaMTX has had time to create its
        // first playlist segment.
        //
        _ = PublishGameplayWatchPartyAsync(
            null);


        return true;
    }

    internal void UpdateGameBroadcastDemand()
    {
        UpdateBrowserBroadcastDemand();
        if (!gameBroadcastArmed)
        {
            return;
        }

        var engine =
            screenController.Engine;

        var sourceStillRunning = gameBroadcastSystem switch
        {
            GameSystem.Snes => engine.IsPlayingSnes,
            GameSystem.Nes => engine.IsPlayingNes,
            GameSystem.GameBoyAdvance => engine.IsPlayingGameBoyAdvance,
            GameSystem.MasterSystem => engine.IsPlayingMasterSystem,
            GameSystem.GameGear => engine.IsPlayingGameGear,
            _ => engine.IsPlayingGameBoy
        };

        //
        // Disarm if the room ended, hosting transferred away, or the
        // associated emulator stopped.
        //
        if (stream.Mode != StreamMode.Hosting ||
     !sourceStillRunning ||
     string.IsNullOrWhiteSpace(
         gameBroadcastPublishUrl))
        {
            StopGameWatchPartyBroadcast();
            return;
        }

        var latestInputUtc = engine.LastGameInputUtc;
        if (latestInputUtc > gameBroadcastLastInputUtc)
        {
            gameBroadcastLastInputUtc = latestInputUtc;
            if (gameBroadcastPausedForInactivity)
            {
                gameBroadcastPausedForInactivity = false;
                gameBroadcastStatusId = Guid.NewGuid();
                if (gameBroadcastHlsUrl is { } previousHlsUrl)
                    gameBroadcastHlsUrl = previousHlsUrl.Split('?')[0] +
                        $"?broadcast={Guid.NewGuid():N}";
                gameBroadcastStartFailed = false;
                
            }
        }

        if (!gameBroadcastPausedForInactivity &&
            DateTime.UtcNow - gameBroadcastLastInputUtc >= GameBroadcastInactivityTimeout)
        {
            gameBroadcastPausedForInactivity = true;
            gameBroadcastStatusId = Guid.NewGuid();
            gameBroadcastViewerReadyUtc = DateTime.MaxValue;
            gameBroadcastStartFailed = false;
            StopGameBroadcastEncoder();
            
        }

        if (gameBroadcastPausedForInactivity)
            return;

        //
        // StreamClient.Roster contains viewers, not the local host.
        //
        // Encode only while a viewer can receive the stream.
        var hasViewer = stream.Roster.Length > 0;

        var encoderRunning = gameBroadcastSystem switch
        {
            GameSystem.Snes => engine.IsSnesBroadcasting,
            GameSystem.Nes => engine.IsNesBroadcasting,
            GameSystem.GameBoyAdvance => engine.IsGameBoyAdvanceBroadcasting,
            GameSystem.MasterSystem => engine.IsMasterSystemBroadcasting,
            GameSystem.GameGear => engine.IsGameGearBroadcasting,
            _ => engine.IsGameBoyBroadcasting
        };

        if (!hasViewer)
        {
            if (encoderRunning)
            {
                StopGameBroadcastEncoder();
            }

            gameBroadcastViewerReadyUtc =
                DateTime.MaxValue;

            //
            // Permit one new start attempt when another viewer arrives.
            //
            gameBroadcastStartFailed =
                false;
            gameBroadcastExpectedRunning = false;
            nextGameBroadcastRetryUtc = DateTime.MinValue;

            return;
        }

        if (gameBroadcastExpectedRunning && !encoderRunning)
        {
            gameBroadcastExpectedRunning = false;
            gameBroadcastStartFailed = true;
            nextGameBroadcastRetryUtc = DateTime.UtcNow.AddSeconds(10);
            gameBroadcastViewerReadyUtc = DateTime.MaxValue;
        }

        if (gameBroadcastStartFailed && DateTime.UtcNow >= nextGameBroadcastRetryUtc)
            gameBroadcastStartFailed = false;

        if (encoderRunning || gameBroadcastStartFailed)
        {
            return;
        }

        var quality = Plugin.Cfg.GameBroadcastQuality == GameBroadcastQuality.StandardNative &&
                      HasConfiguredPatreonTier(2)
            ? GameBroadcastQuality.StandardNative
            : GameBroadcastQuality.DataSaver;

        var started = gameBroadcastSystem switch
        {
            GameSystem.Snes => engine.StartSnesBroadcast(gameBroadcastPublishUrl, quality),
            GameSystem.Nes => engine.StartNesBroadcast(gameBroadcastPublishUrl, quality),
            GameSystem.GameBoyAdvance => engine.StartGameBoyAdvanceBroadcast(gameBroadcastPublishUrl, quality),
            GameSystem.MasterSystem => engine.StartMasterSystemBroadcast(gameBroadcastPublishUrl, quality),
            GameSystem.GameGear => engine.StartGameGearBroadcast(gameBroadcastPublishUrl, quality),
            _ => engine.StartGameBoyBroadcast(gameBroadcastPublishUrl, quality)
        };

        if (started)
        {
            gameBroadcastExpectedRunning = true;
            gameBroadcastViewerReadyUtc =
                DateTime.UtcNow +
                GameBroadcastViewerWarmup;

            if (gameBroadcastSystem == GameSystem.Snes) snesLaunchError = null;
            else if (gameBroadcastSystem == GameSystem.Nes) nesLaunchError = null;
            else if (gameBroadcastSystem == GameSystem.GameBoyAdvance) gameBoyAdvanceLaunchError = null;
            else if (gameBroadcastSystem == GameSystem.MasterSystem) masterSystemLaunchError = null;
            else if (gameBroadcastSystem == GameSystem.GameGear) gameGearLaunchError = null;
            else gameBoyLaunchError = null;


            return;
        }

        gameBroadcastStartFailed =
            true;
        nextGameBroadcastRetryUtc = DateTime.UtcNow.AddSeconds(10);

        var error =
            engine.LastError ??
            "Gameplay broadcast failed to start.";

        if (gameBroadcastSystem == GameSystem.Snes) snesLaunchError = error;
        else if (gameBroadcastSystem == GameSystem.Nes) nesLaunchError = error;
        else if (gameBroadcastSystem == GameSystem.GameBoyAdvance) gameBoyAdvanceLaunchError = error;
        else if (gameBroadcastSystem == GameSystem.MasterSystem) masterSystemLaunchError = error;
        else if (gameBroadcastSystem == GameSystem.GameGear) gameGearLaunchError = error;
        else gameBoyLaunchError = error;
    }

    private void StopGameBroadcastEncoder()
    {
        gameBroadcastExpectedRunning = false;
        var engine =
            screenController.Engine;

        if (engine.IsSnesBroadcasting)
        {
            engine.StopSnesBroadcast();
        }

        if (engine.IsGameBoyBroadcasting)
        {
            engine.StopGameBoyBroadcast();
        }

        if (engine.IsNesBroadcasting)
        {
            engine.StopNesBroadcast();
        }

        if (engine.IsGameBoyAdvanceBroadcasting)
        {
            engine.StopGameBoyAdvanceBroadcast();
        }

        if (engine.IsMasterSystemBroadcasting)
        {
            engine.StopMasterSystemBroadcast();
        }

        if (engine.IsGameGearBroadcasting)
        {
            engine.StopGameGearBroadcast();
        }
    }

    private void StopGameWatchPartyBroadcast()
    {
        StopGameBroadcastEncoder();

        gameBroadcastArmed =
            false;

        gameBroadcastStartFailed =
            false;
        gameBroadcastExpectedRunning = false;
        nextGameBroadcastRetryUtc = DateTime.MinValue;

        gameBroadcastPublishUrl =
            null;

        gameBroadcastHlsUrl =
            null;

        gameBroadcastViewerReadyUtc =
            DateTime.MaxValue;
        gameBroadcastPausedForInactivity = false;
        gameBroadcastLastInputUtc = DateTime.MinValue;
    }

    private bool HasDuplicateGameKeyBindings()
    {
        int[] bindings;

        if (selectedGameSystem ==
            GameSystem.Snes)
        {
            bindings =
            [
                Plugin.Cfg.SnesKeyUp,
            Plugin.Cfg.SnesKeyDown,
            Plugin.Cfg.SnesKeyLeft,
            Plugin.Cfg.SnesKeyRight,

            Plugin.Cfg.SnesKeyA,
            Plugin.Cfg.SnesKeyB,
            Plugin.Cfg.SnesKeyX,
            Plugin.Cfg.SnesKeyY,

            Plugin.Cfg.SnesKeyL,
            Plugin.Cfg.SnesKeyR,

            Plugin.Cfg.SnesKeyStart,
            Plugin.Cfg.SnesKeySelect
            ];
        }
        else
        {
            //
            // Game Boy only uses these eight controls.
            //

            bindings =
            [
                Plugin.Cfg.SnesKeyUp,
            Plugin.Cfg.SnesKeyDown,
            Plugin.Cfg.SnesKeyLeft,
            Plugin.Cfg.SnesKeyRight,

            Plugin.Cfg.SnesKeyA,
            Plugin.Cfg.SnesKeyB,

            Plugin.Cfg.SnesKeyStart,
            Plugin.Cfg.SnesKeySelect
            ];
        }

        return bindings
            .GroupBy(
                key => key)
            .Any(
                group =>
                    group.Count() > 1);
    }


    private void DrawSnesConfigRow(
        string label,
        int currentValue,
        Action<int> setValue)
    {
        ImGui.TableNextRow();

        ImGui.TableSetColumnIndex(
            0);

        ImGui.AlignTextToFramePadding();

        ImGui.TextUnformatted(
            label);


        ImGui.TableSetColumnIndex(
            1);

        var current =
            (VirtualKey)currentValue;

        var preview =
            GetSnesKeyDisplayName(
                current);

        ImGui.SetNextItemWidth(
            -1f);

        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo(
                $"##snesKey_{label}",
                preview)))
        {
            foreach (var key in GetSnesConfigurableKeys())
            {
                var selected =
                    key == current;

                if (ImGui.Selectable(
                        GetSnesKeyDisplayName(
                            key),
                        selected))
                {
                    setValue(
                        (int)key);
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }
    }


    private static VirtualKey[] GetSnesConfigurableKeys()
    {
        return
        [
            VirtualKey.UP,
        VirtualKey.DOWN,
        VirtualKey.LEFT,
        VirtualKey.RIGHT,

        VirtualKey.A,
        VirtualKey.B,
        VirtualKey.C,
        VirtualKey.D,
        VirtualKey.E,
        VirtualKey.F,
        VirtualKey.G,
        VirtualKey.H,
        VirtualKey.I,
        VirtualKey.J,
        VirtualKey.K,
        VirtualKey.L,
        VirtualKey.M,
        VirtualKey.N,
        VirtualKey.O,
        VirtualKey.P,
        VirtualKey.Q,
        VirtualKey.R,
        VirtualKey.S,
        VirtualKey.T,
        VirtualKey.U,
        VirtualKey.V,
        VirtualKey.W,
        VirtualKey.X,
        VirtualKey.Y,
        VirtualKey.Z,

        VirtualKey.RETURN,
        VirtualKey.SPACE,
        VirtualKey.LSHIFT,
        VirtualKey.RSHIFT
        ];
    }


    private static string GetSnesKeyDisplayName(
        VirtualKey key)
    {
        return key switch
        {
            VirtualKey.UP =>
                "Up Arrow",

            VirtualKey.DOWN =>
                "Down Arrow",

            VirtualKey.LEFT =>
                "Left Arrow",

            VirtualKey.RIGHT =>
                "Right Arrow",

            VirtualKey.RETURN =>
                "Enter",

            VirtualKey.SPACE =>
                "Space",

            VirtualKey.LSHIFT =>
                "Left Shift",

            VirtualKey.RSHIFT =>
                "Right Shift",

            _ =>
                key.ToString()
        };
    }


    private static void ResetSnesKeyboardControls()
    {
        Plugin.Cfg.SnesKeyUp =
            (int)VirtualKey.UP;

        Plugin.Cfg.SnesKeyDown =
            (int)VirtualKey.DOWN;

        Plugin.Cfg.SnesKeyLeft =
            (int)VirtualKey.LEFT;

        Plugin.Cfg.SnesKeyRight =
            (int)VirtualKey.RIGHT;

        Plugin.Cfg.SnesKeyA =
            (int)VirtualKey.X;

        Plugin.Cfg.SnesKeyB =
            (int)VirtualKey.Z;

        Plugin.Cfg.SnesKeyX =
            (int)VirtualKey.S;

        Plugin.Cfg.SnesKeyY =
            (int)VirtualKey.A;

        Plugin.Cfg.SnesKeyL =
            (int)VirtualKey.Q;

        Plugin.Cfg.SnesKeyR =
            (int)VirtualKey.W;

        Plugin.Cfg.SnesKeyStart =
            (int)VirtualKey.T;

        Plugin.Cfg.SnesKeySelect =
            (int)VirtualKey.RSHIFT;
    }
}

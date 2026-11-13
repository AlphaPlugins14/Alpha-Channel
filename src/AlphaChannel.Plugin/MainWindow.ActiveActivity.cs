using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private enum ActiveActivityKind
    {
        None,
        Whiteboard,
        Pictionary,
        PictionaryTeams,
        Quiz,
        Roulette,
        Bingo,
        SkyRail,
        SlotMachine,
        Poker,
        ChocoRaceBets,
        LuckyWheel,
        Pixels,
        BlastParty,
    }

    // The games share a whiteboard canvas. Resolve its owner before treating it
    // as a standalone whiteboard, so every activity surface uses one identity.
    private ActiveActivityKind ActiveActivity =>
        BlastPartyActive ? ActiveActivityKind.BlastParty :
        pixelsActive ? ActiveActivityKind.Pixels :
        LuckyWheelActive ? ActiveActivityKind.LuckyWheel :
        SkyRailActive ? ActiveActivityKind.SkyRail :
        SlotMachineActive ? ActiveActivityKind.SlotMachine :
        PokerActive ? ActiveActivityKind.Poker :
        ChocoRaceActive ? ActiveActivityKind.ChocoRaceBets :
        RouletteActive ? ActiveActivityKind.Roulette :
        BingoActive ? ActiveActivityKind.Bingo :
        QuizActive ? ActiveActivityKind.Quiz :
        PictionaryActive ? ActiveActivityKind.Pictionary :
        PictionaryTeamsActive ? ActiveActivityKind.PictionaryTeams :
        whiteboardActive ? ViewerCanvasActivity :
        ActiveActivityKind.None;

    internal bool IsStandaloneCanvasActivityActive => ActiveActivity == ActiveActivityKind.Whiteboard;

    internal bool IsArcadeGameActive => ActiveActivity != ActiveActivityKind.None;

    // The canvas command can arrive before the game's own state command.
    // The host's published canvas title keeps viewer cards accurate then too.
    private ActiveActivityKind ViewerCanvasActivity =>
        stream.Mode == StreamMode.Viewing && IsWhiteboardUrl(stream.CurrentRoomState?.Url)
            ? ActivityFromCanvasUrl(stream.CurrentRoomState?.Url) switch
            {
                ActiveActivityKind.Whiteboard => stream.CurrentRoomState?.MediaTitle switch
                {
                    "Pictionary" => ActiveActivityKind.Pictionary,
                    "Pictionary Teams" => ActiveActivityKind.PictionaryTeams,
                    "Quiz" => ActiveActivityKind.Quiz,
                    "Roulette" => ActiveActivityKind.Roulette,
                    "Bingo" => ActiveActivityKind.Bingo,
                    "Sky Rail" => ActiveActivityKind.SkyRail,
                    "Air Force EX" => ActiveActivityKind.SkyRail,
                    "Pixels" => ActiveActivityKind.Pixels,
                    "Lucky Wheel" => ActiveActivityKind.LuckyWheel,
                    "Slot Machine" => ActiveActivityKind.SlotMachine,
                    "Poker" => ActiveActivityKind.Poker,
                    "Choco RaceBets" => ActiveActivityKind.ChocoRaceBets,
                    _ => ActiveActivityKind.Whiteboard,
                },
                var activity => activity,
            }
            : ActiveActivityKind.Whiteboard;

    private static ActiveActivityKind ActivityFromCanvasUrl(string? url)
    {
        if (url?.StartsWith(WhiteboardUrlPrefix, StringComparison.OrdinalIgnoreCase) != true)
            return ActiveActivityKind.None;

        if (url.Contains("?activity=blast-party&key=", StringComparison.Ordinal)) return ActiveActivityKind.BlastParty;
        var marker = url.IndexOf("?activity=", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            return ActiveActivityKind.Whiteboard;

        return url[(marker + "?activity=".Length)..] switch
            {
                "pictionary" => ActiveActivityKind.Pictionary,
                "pictionary-teams" => ActiveActivityKind.PictionaryTeams,
                "quiz" => ActiveActivityKind.Quiz,
                "roulette" => ActiveActivityKind.Roulette,
                "bingo" => ActiveActivityKind.Bingo,
                "blast-party" => ActiveActivityKind.BlastParty,
                "sky-rail" => ActiveActivityKind.SkyRail,
                "pixels" => ActiveActivityKind.Pixels,
                "lucky-wheel" => ActiveActivityKind.LuckyWheel,
                "slot-machine" => ActiveActivityKind.SlotMachine,
                "poker" => ActiveActivityKind.Poker,
                "choco-racebets" => ActiveActivityKind.ChocoRaceBets,
                _ => ActiveActivityKind.Whiteboard,
            };
    }

    private static string ActivityCanvasSuffix(ActiveActivityKind activity) => activity switch
    {
        ActiveActivityKind.Pictionary => "?activity=pictionary",
        ActiveActivityKind.PictionaryTeams => "?activity=pictionary-teams",
        ActiveActivityKind.Quiz => "?activity=quiz",
        ActiveActivityKind.Roulette => "?activity=roulette",
        ActiveActivityKind.Bingo => "?activity=bingo",
        ActiveActivityKind.BlastParty => "?activity=blast-party",
        ActiveActivityKind.SkyRail => "?activity=sky-rail",
        ActiveActivityKind.Pixels => "?activity=pixels",
        ActiveActivityKind.LuckyWheel => "?activity=lucky-wheel",
        ActiveActivityKind.SlotMachine => "?activity=slot-machine",
        ActiveActivityKind.Poker => "?activity=poker",
        ActiveActivityKind.ChocoRaceBets => "?activity=choco-racebets",
        _ => string.Empty,
    };

    private bool CanLaunchActivity(ActiveActivityKind activity) =>
        !HalloweenActivityActive && !hostTransferPreparing && !stream.IsJoiningWatchParty && !StudioOwnsPlaybackControls && (ActiveActivity is ActiveActivityKind.None || ActiveActivity == activity);

    // The local player runs activity logic both alone and as the party host.
    private bool IsLocalActivityHost => stream.Mode != StreamMode.Viewing;

    private void DrawActivityStartButtons(string id, Action startSolo, Action startInParty,
        bool enabled, float height = 42f, float width = -1f)
    {
        using var disabled = UiDisabled.Scope(!enabled);
        var available = width > 0f ? width : ImGui.GetContentRegionAvail().X;
        if (stream.Mode == StreamMode.Hosting)
        {
            if (ImGui.Button($"Start in Watch Party##{id}Party", new Vector2(available, Ui(height))))
                startInParty();
            return;
        }

        var gap = Ui(8f);
        var buttonWidth = (available - gap) * 0.5f;
        if (ImGui.Button($"Start##{id}Solo", new Vector2(buttonWidth, Ui(height))))
            startSolo();
        ImGui.SameLine(0f, gap);
        if (ImGui.Button($"Start in party##{id}Party", new Vector2(buttonWidth, Ui(height))))
            startInParty();
    }

    private static string ActivityTitle(ActiveActivityKind activity) => activity switch
    {
        ActiveActivityKind.Whiteboard => "Canvas",
        ActiveActivityKind.Pictionary => "Pictionary",
        ActiveActivityKind.PictionaryTeams => "Pictionary Teams",
        ActiveActivityKind.Quiz => "Quiz",
        ActiveActivityKind.Roulette => "Roulette",
        ActiveActivityKind.Bingo => "Bingo",
        ActiveActivityKind.BlastParty => "Blast Party",
        ActiveActivityKind.SkyRail => "Air Force EX",
        ActiveActivityKind.Pixels => "Pixels",
        ActiveActivityKind.LuckyWheel => "Lucky Wheel",
        ActiveActivityKind.SlotMachine => "Slot Machine",
        ActiveActivityKind.Poker => "Poker",
        ActiveActivityKind.ChocoRaceBets => "Choco RaceBets",
        _ => string.Empty,
    };
}

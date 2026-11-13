namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private enum ActivityPreviewKind
    {
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
        ComingSoon,
        LuckyWheel,
        Pixels,
        BlastParty,
    }

    private sealed record ActivityLibraryEntry(
        string Id,
        string[] Categories,
        string Title,
        string Description,
        string PlayerLabel,
        bool SupportsSolo,
        bool SupportsTeam,
        bool RequiresWatchParty,
        bool PlayableOnTv,
        bool Enabled,
        ActivityPreviewKind Preview,
        HomePage? Destination,
        bool SupportsWatchParty = true);

    private static readonly ActivityLibraryEntry[] ActivityLibrary =
    [
        new("pixels", ["Creative", "Arcade"], "Pixels", "One global canvas. Place a pixel every three minutes and create together.",
            "Everyone", true, true, false, true, true, ActivityPreviewKind.Pixels, HomePage.Pixels),
        new("whiteboard", ["Creative"], "Canvas", "Draw, sketch, and create together on every TV.",
            "1+ players", true, true, false, true, true, ActivityPreviewKind.Whiteboard, HomePage.Whiteboard),
        new("pictionary", ["Party Game", "Trivia"], "Pictionary", "Take turns drawing while everyone races to guess.",
            "2+ players", false, true, true, true, true, ActivityPreviewKind.Pictionary, HomePage.Pictionary),
        new("pictionaryTeams", ["Party Game", "Trivia"], "Pictionary Teams", "Pick red or blue, then draw and guess against the other team.",
            $"{PictionaryTeamsPreviewRules.MinimumPlayers}+ players", false, true, true, true, true, ActivityPreviewKind.PictionaryTeams, HomePage.PictionaryTeams),
        new("quiz", ["Trivia", "Party Game"], "Quiz", "Answer questions together and race to the top.",
            "1+ players", true, true, false, true, true, ActivityPreviewKind.Quiz, HomePage.Quiz),
        new("roulettePrototype", ["Casino"], "Roulette", "Place your chips and watch the wheel spin together.",
            "1+ players", true, true, false, true, true, ActivityPreviewKind.Roulette, HomePage.Roulette),
        new("luckyWheel", ["Creative", "Party Game"], "Lucky Wheel", "Design a custom wheel and spin together for your own games.",
            "1+ players", true, true, false, true, true, ActivityPreviewKind.LuckyWheel, HomePage.LuckyWheel),
        new("bingo", ["Party Game", "Casino"], "Bingo", "Get the next number and race to complete your card.",
            "1+ players", true, true, false, true, true, ActivityPreviewKind.Bingo, HomePage.Bingo),
        new("racePreview", ["Arcade", "Casino"], "Choco RaceBets", "Bet on six chocos and watch them race together.",
            "1+ players", true, true, false, true, true, ActivityPreviewKind.RacePreview, HomePage.RacePreview),
        new("blastParty", ["Arcade", "Party Game"], "Blast Party", "Click to move, place bombs and battle your friends in a changing maze.",
            "2-8 players", false, true, true, true, true, ActivityPreviewKind.BlastParty, HomePage.BlastParty),
        new("skyRail", ["Arcade"], "Air Force EX", "Aim, fire, and dodge bomb targets in an arcade airship run.",
            "1+ players", true, true, false, true, true, ActivityPreviewKind.SkyRail, HomePage.SkyRail),
        new("slotMachine", ["Casino", "Arcade"], "Slot Machine", "Insert a coin, pull the handle, and spin three Eorzean reels.",
            "1+ players", true, true, false, true, true, ActivityPreviewKind.SlotMachine, HomePage.SlotMachine),
        new("poker", ["Casino", "Party Game"], "Poker", "Take a seat at a 16-player Gold Saucer-style table.",
            "1–16 players", true, true, false, true, true, ActivityPreviewKind.Poker, HomePage.Poker),
    ];

    private static readonly string[] ActivityCategories =
        ["All categories", "Creative", "Party Game", "Trivia", "Casino", "Arcade"];
}

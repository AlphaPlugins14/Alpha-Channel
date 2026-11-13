using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private const int PictionaryStandardRoundCount = 10;
    private const int PictionaryStandardDrawingSeconds = 60;
    private const int PictionaryStandardFirstGuessPoints = 3;
    private const int PictionaryStandardOtherGuessPoints = 1;
    private const int PictionaryStandardDrawerPoints = 0;
    private const int PictionaryTvGuessStartTarget = 0;
    private const int PictionaryTvGuessSubmitTarget = 1;
    private const int PictionaryTvGuessCancelTarget = 2;
    private const int PictionaryTvPlayAgainTarget = 3;
    private const int PictionaryTvWordChoiceTargetStart = 10;
    private static readonly TimeSpan PictionaryDrawerSelectionTime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PictionaryWordChoiceTime = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PictionarySummaryTime = TimeSpan.FromSeconds(7);

    private enum PictionaryPhase
    {
        Inactive,
        ChoosingDrawer,
        ChoosingWord,
        Drawing,
        RoundSummary,
        GameOver,
        Countdown,
    }

    private sealed record PictionaryPlayerState(string Id, string Name, int Score, bool Active);

    private sealed record PictionarySnapshot(
        string SessionId,
        PictionaryPhase Phase,
        int Round,
        int RoundCount,
        long PhaseEndsUnixMs,
        string DrawerId,
        string DrawerName,
        int WordLength,
        string MaskedWord,
        string RevealedWord,
        bool RandomDrawerSelection,
        int DrawingSeconds,
        int FirstGuessPoints,
        int OtherGuessPoints,
        int DrawerPoints,
        bool FirstGuesserDrawsNext,
        string FirstGuesserId,
        string FirstGuesserName,
        int FirstGuesserPoints,
        string[] GuessedUserIds,
        PictionaryPlayerState[] Players);

    private sealed record PictionaryFeedItem(
        string Text,
        string Name,
        string UserId,
        bool Success,
        bool System,
        long CreatedUnixMs,
        Guid? ReplyToId = null,
        Guid ChatItemId = default,
        string SessionId = "",
        int Round = 0,
        bool GuessResult = false,
        string GuessText = "");

    private static readonly string[] PictionaryWords =
    [
        "airplane", "apple", "balloon", "banana", "beach", "bicycle", "birthday", "book",
        "bridge", "butterfly", "camera", "campfire", "castle", "cat", "chair", "cloud",
        "coffee", "computer", "crown", "dinosaur", "dragon", "drum", "elephant", "feather",
        "fireworks", "flower", "football", "forest", "ghost", "guitar", "hamburger", "heart",
        "helicopter", "ice cream", "island", "jellyfish", "kangaroo", "key", "kite", "ladder",
        "lighthouse", "lion", "mermaid", "microphone", "moon", "mountain", "mushroom", "octopus",
        "panda", "penguin", "piano", "pirate", "pizza", "rainbow", "robot", "rocket",
        "sandcastle", "shark", "ship", "snowman", "spaceship", "spider", "star", "strawberry",
        "sun", "sword", "telephone", "television", "train", "tree", "turtle", "umbrella",
        "unicorn", "volcano", "waterfall", "whale", "wizard", "zebra",
        "alarm clock", "backpack", "bathtub", "birthday cake", "bowling ball", "bus stop",
        "candle", "compass", "doorbell", "elevator", "ferris wheel", "fishing rod",
        "hot-air balloon", "hourglass", "mailbox", "mirror", "parachute", "passport",
        "picnic", "roller coaster", "shopping cart", "skateboard", "snow globe", "tent",
        "toothbrush", "traffic light",
        "chocobo", "moogle", "cactuar", "tonberry", "carbuncle", "aetheryte", "Fat Cat",
        "spriggan", "Namazu", "airship", "magitek armour", "Crystal Tower", "Gold Saucer",
        "Triple Triad card", "chocobo race", "Black Mage", "White Mage", "Dragoon",
        "Dark Knight", "Lalafell", "Miqo'te", "Viera", "Ifrit", "Garuda", "Titan",
        "Bahamut", "limit break",
        "axolotl", "bat", "beaver", "camel", "chameleon", "crab", "crocodile", "deer",
        "dolphin", "duck", "flamingo", "fox", "frog", "giraffe", "hedgehog", "koala",
        "llama", "owl", "peacock", "platypus", "porcupine", "raccoon", "seahorse",
        "sloth", "snail", "walrus",
        "enchanted mirror", "fairy", "flying carpet", "genie", "giant", "goblin",
        "griffin", "knight", "magic portal", "magic potion", "magic wand", "phoenix",
        "spellbook", "troll", "vampire", "werewolf", "witch", "crystal ball",
        "haunted house", "invisibility cloak", "treasure chest", "unicorn horn",
        "axe", "battle hammer", "bow and arrow", "cannon", "catapult", "crossbow",
        "dagger", "flamethrower", "gauntlet", "lance", "laser gun", "mace",
        "nunchucks", "pistol", "scythe", "shield", "slingshot", "spear", "staff",
        "trident",
        "arcade cabinet", "boss fight", "controller", "dice", "dungeon", "game cartridge",
        "health bar", "joystick", "loot chest", "map marker", "save point", "spawn point",
        "treasure hunt", "vr headset", "zombie",
        "film reel", "movie camera", "cinema", "popcorn bucket", "red carpet", "3D glasses",
        "car chase", "alien", "masked villain", "secret agent", "superhero", "time machine",
        "lightsaber", "sinking ship", "shark fin", "glass slipper", "ruby slippers",
        "golden idol", "dinosaur park", "haunted doll",
        "Pikachu", "Eevee", "Charmander", "Squirtle", "Bulbasaur", "Charizard", "Psyduck",
        "Snorlax", "Jigglypuff", "Meowth", "Gengar", "Mewtwo", "Magikarp", "Ditto",
        "Wicked", "Hamilton", "Les Miserables", "The Phantom of the Opera", "Grease",
        "The Lion King", "Hairspray", "West Side Story", "The Sound of Music", "Matilda",
        "Mary Poppins", "Beetlejuice", "School of Rock", "Little Shop of Horrors",
        "Aladdin", "Hadestown",
        "Titanic", "Jaws", "Back to the Future", "Jurassic Park", "Ghostbusters", "Barbie",
        "Frozen", "Home Alone", "The Matrix", "Shrek", "Star Wars", "E.T.", "Finding Nemo",
        "Toy Story", "Avatar", "The Mummy",
        "Friends", "Stranger Things", "The Simpsons", "Doctor Who", "SpongeBob SquarePants",
        "The Office", "Game of Thrones", "Breaking Bad", "The Mandalorian", "The Walking Dead",
        "Scooby-Doo", "The X-Files", "Wednesday", "Bluey", "Squid Game", "The Big Bang Theory",
        "Naruto", "One Piece", "Dragon Ball Z", "Sailor Moon", "Demon Slayer", "Attack on Titan",
        "My Hero Academia", "Death Note", "Fullmetal Alchemist", "Spy x Family",
        "Neon Genesis Evangelion", "Jujutsu Kaisen", "Bleach", "Chainsaw Man", "Cowboy Bebop",
        "One Punch Man", "Ouran High School Host Club", "Sword Art Online",
        "badger", "otter", "meerkat", "capybara", "red panda", "polar bear", "tiger", "leopard",
        "rhinoceros", "hippopotamus", "ostrich", "puffin", "narwhal", "orca", "manta ray",
        "praying mantis", "ladybird", "hummingbird",
        "dog", "golden retriever", "corgi", "dachshund", "rabbit", "hamster", "guinea pig",
        "goldfish", "budgie", "parrot", "cockatiel", "ferret", "chinchilla", "gecko", "tortoise",
        "Earth", "solar system", "asteroid", "comet", "astronaut", "space station",
        "lunar rover", "black hole", "galaxy", "constellation", "meteor shower", "telescope",
        "space shuttle",
        "cauldron", "dragon egg", "elf", "dwarf", "orc", "centaur", "minotaur", "hydra",
        "basilisk", "kraken", "golem", "wizard tower", "spell scroll", "rune stone",
        "magic broom", "fairy ring", "enchanted sword", "magic library",
        "sushi", "spaghetti", "taco", "burrito", "ramen", "dumplings", "pancakes", "waffles",
        "doughnut", "pretzel", "chocolate bar", "watermelon", "avocado", "pineapple",
        "croissant", "hot dog", "sandwich", "cupcake",
        "McDonald's", "KFC", "Burger King", "Subway", "Taco Bell", "Wendy's", "Domino's",
        "Pizza Hut", "Five Guys", "Popeyes", "Chick-fil-A", "Chipotle", "Shake Shack",
        "In-N-Out Burger",
        "Dungeons & Dragons", "comic book", "cosplay", "comic convention", "tabletop RPG",
        "trading card", "action figure", "gaming keyboard", "3D printer", "manga", "pixel art",
        "20-sided die", "miniatures", "LAN party", "gaming PC", "speedrun",
        "basketball", "baseball", "tennis", "golf", "cricket", "rugby", "ice hockey",
        "volleyball", "badminton", "swimming", "gymnastics", "archery", "fencing",
        "boxing", "karate", "surfing", "skiing", "darts",
    ];

    private readonly Dictionary<string, string> pictionaryPlayerNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> pictionaryScores = new(StringComparer.Ordinal);
    private readonly HashSet<string> pictionaryActivePlayers = new(StringComparer.Ordinal);
    private readonly HashSet<string> pictionaryGuessedPlayers = new(StringComparer.Ordinal);
    private readonly List<PictionaryFeedItem> pictionaryFeed = [];
    private readonly Dictionary<string, DateTime> pictionaryGuessRate = new(StringComparer.Ordinal);
    private string? pictionarySessionId;
    private PictionaryPhase pictionaryPhase;
    private int pictionaryRound;
    private DateTime pictionaryPhaseEndsUtc;
    private string pictionaryDrawerId = string.Empty;
    private string pictionaryDrawerName = string.Empty;
    private string pictionaryNextDrawerId = string.Empty;
    private string pictionaryRoundFirstGuesserId = string.Empty;
    private string pictionaryRoundFirstGuesserName = string.Empty;
    private int pictionaryRoundFirstGuesserPoints;
    private string pictionarySelectedWord = string.Empty;
    private string pictionaryMaskedWord = string.Empty;
    private int pictionaryWordLength;
    private string pictionaryRevealedWord = string.Empty;
    private string[] pictionaryWordChoices = [];
    private string pictionaryGuessInput = string.Empty;
    private bool pictionaryRandomDrawerSelection;
    private bool pictionaryThirtySecondNoticeSent;
    private bool pictionaryTenSecondNoticeSent;
    private string pictionaryRosterSignature = string.Empty;
    private int pictionaryLastTvSecond = -1;
    private long pictionaryLastTvAnimationTick = -1;
    private bool pictionaryHostAuthority;
    private bool pictionaryShowSetup = true;
    private int pictionaryRoundCount = PictionaryStandardRoundCount;
    private int pictionaryDrawingSeconds = PictionaryStandardDrawingSeconds;
    private int pictionaryFirstGuessPoints = PictionaryStandardFirstGuessPoints;
    private int pictionaryOtherGuessPoints = PictionaryStandardOtherGuessPoints;
    private int pictionaryDrawerPoints = PictionaryStandardDrawerPoints;
    private bool pictionaryFirstGuesserDrawsNext = true;
    private int pictionaryCustomRoundCount = PictionaryStandardRoundCount;
    private int pictionaryCustomDrawingSeconds = PictionaryStandardDrawingSeconds;
    private int pictionaryCustomFirstGuessPoints = PictionaryStandardFirstGuessPoints;
    private int pictionaryCustomOtherGuessPoints = PictionaryStandardOtherGuessPoints;
    private int pictionaryCustomDrawerPoints = PictionaryStandardDrawerPoints;
    private int pictionaryCustomNextDrawerIndex;
    private string? lastPictionaryDrawingTurnAlert;
    private string? pictionaryAnnouncedNameSessionId;
    private string? pictionaryAnnouncedName;
    private bool pictionaryTvGuessInputActive;
    private string pictionaryTvGuessInput = string.Empty;
    private int pictionaryTvHoveredTarget = -1;
    private int pictionaryLocalAnswerFeedbackRound;
    private bool? pictionaryLocalAnswerCorrect;

    private bool PictionaryActive => pictionaryPhase != PictionaryPhase.Inactive;
    internal string ActiveCanvasTitle => ActivityTitle(ActiveActivity);
    private string LocalPictionaryUserId => CurrentSession?.AccountId ?? string.Empty;
    private bool LocalIsPictionaryDrawer =>
        PictionaryActive && string.Equals(LocalPictionaryUserId, pictionaryDrawerId, StringComparison.Ordinal);

    private bool LocalCanSubmitPictionaryGuess =>
        pictionaryPhase == PictionaryPhase.Drawing &&
        pictionaryActivePlayers.Contains(LocalPictionaryUserId) &&
        !LocalIsPictionaryDrawer &&
        !pictionaryGuessedPlayers.Contains(LocalPictionaryUserId) &&
        LocalPictionaryAnswerResult is not true;

    private bool? LocalPictionaryAnswerResult =>
        pictionaryLocalAnswerFeedbackRound == pictionaryRound
            ? pictionaryLocalAnswerCorrect
            : null;

    internal void UpdatePictionary()
    {
        if (stream.Mode == StreamMode.None)
        {
            if (!stream.IncomingPictionaryCommands.IsEmpty)
                stream.IncomingPictionaryCommands.Clear();
            if (PictionaryActive || pictionarySessionId is not null)
                ResetPictionaryLocal();
            return;
        }

        while (stream.IncomingPictionaryCommands.TryDequeue(out var incoming))
        {
            if (stream.Mode == StreamMode.Hosting)
            {
                ReceiveArcadeServerCommand("pictionary", incoming.UserId, incoming.Command);
            }
            else
            {
                ReceiveArcadeServerCommand("pictionary", incoming.UserId, incoming.Command);
            }
        }

        UpdateActivityRelay("pictionary");
        UpdatePictionaryDrawerNotifications();
        if (pictionaryTvGuessInputActive && !LocalCanSubmitPictionaryGuess && !TeamsCanGuess)
            CancelPictionaryTvGuess();
        if (pictionaryTvDrawingActive && !LocalCanDrawPictionaryOnTv)
            CancelPictionaryTvDrawing();

        // Timers and the next-drawer name animation are rendered into the TV texture, so
        // refresh them locally on viewers as well as on the host between state messages.
        var tvSecond = PictionarySecondsRemaining;
        var tvAnimationTick = pictionaryPhase == PictionaryPhase.ChoosingDrawer
            ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 100
            : -1;
        if (tvSecond != pictionaryLastTvSecond || tvAnimationTick != pictionaryLastTvAnimationTick)
        {
            pictionaryLastTvSecond = tvSecond;
            pictionaryLastTvAnimationTick = tvAnimationTick;
            whiteboardTvDirty = true;
        }

        if (stream.Mode != StreamMode.Hosting || !PictionaryActive)
        {
            return;
        }
        if (!pictionaryHostAuthority)
        {
            StopPictionaryGame("Pictionary ended after the Watch Party host changed.");
            return;
        }

        if (!screenController.Engine.IsPlayingWhiteboard)
        {
            StopPictionaryGame("Pictionary ended because another activity took over the TV.");
            return;
        }

        var rosterSignature = string.Join(';', stream.Roster.Select(player => player.UserId).Order(StringComparer.Ordinal));
        if (!string.Equals(rosterSignature, pictionaryRosterSignature, StringComparison.Ordinal))
        {
            pictionaryRosterSignature = rosterSignature;
            var connected = stream.Roster.Select(player => player.UserId).ToHashSet(StringComparer.Ordinal);
            if (CurrentSession is { AccountId.Length: > 0 } hostSession)
            {
                connected.Add(hostSession.AccountId);
            }
            pictionaryActivePlayers.RemoveWhere(id => !connected.Contains(id));
            PictionaryRosterScores.RetainConnected(pictionaryScores, pictionaryPlayerNames, connected);
            if (pictionaryActivePlayers.Count < 2)
            {
                StopPictionaryGame("Pictionary ended because not enough players remain.");
                return;
            }
            if (!pictionaryActivePlayers.Contains(pictionaryDrawerId) &&
                pictionaryPhase is PictionaryPhase.ChoosingDrawer or PictionaryPhase.ChoosingWord or PictionaryPhase.Drawing)
            {
                if (pictionaryPhase == PictionaryPhase.Drawing)
                {
                    EndPictionaryRound();
                }
                else
                {
                    BeginPictionaryDrawerSelection(string.Empty, true);
                }
                return;
            }
            SendPictionaryState();
            SendDrawerSecretIfNeeded();
        }

        var now = DateTime.UtcNow;
        if (pictionaryPhase == PictionaryPhase.Drawing)
        {
            var remaining = pictionaryPhaseEndsUtc - now;
            if (!pictionaryThirtySecondNoticeSent && remaining <= TimeSpan.FromSeconds(30))
            {
                pictionaryThirtySecondNoticeSent = true;
                BroadcastPictionaryFeed("30 seconds remaining", string.Empty, string.Empty, false, true);
            }
            if (!pictionaryTenSecondNoticeSent && remaining <= TimeSpan.FromSeconds(10))
            {
                pictionaryTenSecondNoticeSent = true;
                BroadcastPictionaryFeed("10 seconds remaining", string.Empty, string.Empty, false, true);
            }
        }

        if (now < pictionaryPhaseEndsUtc)
        {
            return;
        }

        switch (pictionaryPhase)
        {
            case PictionaryPhase.Countdown:
                BeginPictionaryDrawerSelection(string.Empty, true);
                break;
            case PictionaryPhase.ChoosingDrawer:
                BeginPictionaryWordChoice();
                break;
            case PictionaryPhase.ChoosingWord:
                SelectPictionaryWord(Random.Shared.Next(0, Math.Max(1, pictionaryWordChoices.Length)));
                break;
            case PictionaryPhase.Drawing:
                EndPictionaryRound();
                break;
            case PictionaryPhase.RoundSummary:
                if (pictionaryRound >= pictionaryRoundCount)
                {
                    pictionaryPhase = PictionaryPhase.GameOver;
                    // Final results remain on both screens until the host explicitly
                    // starts another game or closes Pictionary.
                    pictionaryPhaseEndsUtc = DateTime.MaxValue;
                    BroadcastPictionaryFeed("Game over! Final scores are ready.", string.Empty, string.Empty, false, true);
                    AnnouncePictionaryCycleWinner();
                    AwardPictionaryActivityPoints();
                    SendPictionaryState();
                }
                else
                {
                    ReconcilePictionaryPlayers();
                    pictionaryRound++;
                    BeginPictionaryDrawerSelection(pictionaryNextDrawerId, string.IsNullOrEmpty(pictionaryNextDrawerId));
                }
                break;
        }
    }

    private void StartPictionaryGame()
    {
        if (stream.Mode != StreamMode.Hosting || CurrentSession is null || stream.Roster.Length == 0)
        {
            return;
        }
        if (!CanLaunchActivity(ActiveActivityKind.Pictionary))
        {
            NotifyActivityStartBlocked();
            Plugin.ChatGui.Print("[AlphaChannel] End the active activity before starting Pictionary.");
            return;
        }

        pictionarySessionId = Guid.NewGuid().ToString("N");
        pictionaryShowSetup = false;
        pictionaryHostAuthority = true;
        pictionaryRound = 1;
        pictionaryScores.Clear();
        pictionaryPlayerNames.Clear();
        pictionaryActivePlayers.Clear();
        pictionaryFeed.Clear();
        ReconcilePictionaryPlayers();
        if (pictionaryActivePlayers.Count < 2)
        {
            AddPictionaryFeed("At least two Watch Party members are needed to start.", string.Empty, string.Empty, false, true);
            pictionaryPhase = PictionaryPhase.Inactive;
            return;
        }

        whiteboardBackgroundWhite = false;
        StartHostWhiteboard(WhiteboardMode.Team, ActiveActivityKind.Pictionary);
        pictionaryRosterSignature = string.Empty;
        BeginPictionaryCountdown();
    }

    private void BeginPictionaryCountdown()
    {
        pictionaryPhase = PictionaryPhase.Countdown;
        pictionaryPhaseEndsUtc = DateTime.UtcNow.AddSeconds(15);
        pictionaryDrawerId = string.Empty;
        pictionaryDrawerName = string.Empty;
        pictionarySelectedWord = string.Empty;
        pictionaryRevealedWord = string.Empty;
        pictionaryMaskedWord = string.Empty;
        pictionaryWordLength = 0;
        pictionaryWordChoices = [];
        pictionaryGuessedPlayers.Clear();
        ResetLocalPictionaryAnswerFeedback();
        pictionaryRoundFirstGuesserId = string.Empty;
        pictionaryRoundFirstGuesserName = string.Empty;
        pictionaryRoundFirstGuesserPoints = 0;
        SendPictionaryState();
    }

    private void ReconcilePictionaryPlayers()
    {
        pictionaryActivePlayers.Clear();
        if (CurrentSession is { AccountId.Length: > 0 } session)
        {
            pictionaryActivePlayers.Add(session.AccountId);
            pictionaryPlayerNames[session.AccountId] = string.IsNullOrWhiteSpace(session.DisplayName) ? "Host" : session.DisplayName;
            pictionaryScores.TryAdd(session.AccountId, 0);
        }

        foreach (var player in stream.Roster)
        {
            if (string.IsNullOrWhiteSpace(player.UserId))
            {
                continue;
            }
            pictionaryActivePlayers.Add(player.UserId);
            pictionaryPlayerNames[player.UserId] = string.IsNullOrWhiteSpace(player.DisplayName) ? "Player" : player.DisplayName;
            pictionaryScores.TryAdd(player.UserId, 0);
        }
        PictionaryRosterScores.RetainConnected(pictionaryScores, pictionaryPlayerNames, pictionaryActivePlayers);
    }

    private void PruneDisconnectedPictionaryScores()
    {
        var connected = stream.Roster.Select(player => player.UserId).ToHashSet(StringComparer.Ordinal);
        if (CurrentSession is { AccountId.Length: > 0 } hostSession) connected.Add(hostSession.AccountId);
        pictionaryActivePlayers.RemoveWhere(id => !connected.Contains(id));
        PictionaryRosterScores.RetainConnected(pictionaryScores, pictionaryPlayerNames, connected);
    }

    private void BeginPictionaryDrawerSelection(string requestedDrawerId, bool random)
    {
        var candidates = pictionaryActivePlayers
            .Where(id => !string.Equals(id, pictionaryDrawerId, StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length == 0)
        {
            StopPictionaryGame("Not enough players remain to continue.");
            return;
        }

        var usedRequestedDrawer = !string.IsNullOrEmpty(requestedDrawerId) &&
            candidates.Contains(requestedDrawerId, StringComparer.Ordinal);
        var chosen = usedRequestedDrawer
            ? requestedDrawerId
            : candidates[Random.Shared.Next(candidates.Length)];
        pictionaryDrawerId = chosen;
        pictionaryDrawerName = pictionaryPlayerNames.GetValueOrDefault(chosen, "Player");
        pictionaryRandomDrawerSelection = random || !usedRequestedDrawer;
        pictionaryPhase = PictionaryPhase.ChoosingDrawer;
        pictionaryPhaseEndsUtc = DateTime.UtcNow + (pictionaryRandomDrawerSelection
            ? PictionaryDrawerSelectionTime
            : TimeSpan.FromSeconds(2));
        pictionarySelectedWord = string.Empty;
        pictionaryRevealedWord = string.Empty;
        pictionaryMaskedWord = string.Empty;
        pictionaryWordLength = 0;
        pictionaryWordChoices = [];
        pictionaryGuessedPlayers.Clear();
        ResetLocalPictionaryAnswerFeedback();
        SendPictionaryState();
    }

    private void BeginPictionaryWordChoice()
    {
        if (!pictionaryActivePlayers.Contains(pictionaryDrawerId))
        {
            BeginPictionaryDrawerSelection(string.Empty, true);
            return;
        }

        pictionaryPhase = PictionaryPhase.ChoosingWord;
        pictionaryPhaseEndsUtc = DateTime.UtcNow + PictionaryWordChoiceTime;
        pictionaryWordChoices = PictionaryWords.OrderBy(_ => Random.Shared.Next()).Take(3).ToArray();
        pictionarySelectedWord = string.Empty;
        SendPictionaryState();
        SendPictionaryWordChoices();
    }

    private void SendPictionaryWordChoices()
    {
        if (pictionarySessionId is null || pictionaryWordChoices.Length == 0)
        {
            return;
        }
        if (string.Equals(pictionaryDrawerId, LocalPictionaryUserId, StringComparison.Ordinal))
        {
            return;
        }

        if (GameRelay("pictionary") is not null) { PublishRemainingState("pictionary"); return; }
        var encoded = EncodePictionaryJson(pictionaryWordChoices);
        _ = stream.SendPictionaryCommandAsync($"O|{pictionarySessionId}|{pictionaryRound}|{pictionaryDrawerId}|{encoded}");
    }

    private void SubmitPictionaryWordChoice(int index)
    {
        if (!LocalIsPictionaryDrawer || pictionaryPhase != PictionaryPhase.ChoosingWord ||
            index < 0 || index >= pictionaryWordChoices.Length || pictionarySessionId is null)
        {
            return;
        }

        if (stream.Mode == StreamMode.Hosting)
        {
            SelectPictionaryWord(index);
        }
        else
        {
            pictionarySelectedWord = pictionaryWordChoices[index];
            SendArcadePlayerAction("pictionary", $"W|{pictionarySessionId}|{pictionaryRound}|{index}");
        }
    }

    private void SelectPictionaryWord(int index)
    {
        if (pictionaryWordChoices.Length == 0)
        {
            pictionaryWordChoices = PictionaryWords.OrderBy(_ => Random.Shared.Next()).Take(3).ToArray();
        }
        index = Math.Clamp(index, 0, pictionaryWordChoices.Length - 1);
        pictionarySelectedWord = pictionaryWordChoices[index];
        pictionaryMaskedWord = BuildPictionaryMask(pictionarySelectedWord);
        pictionaryWordLength = pictionarySelectedWord.Count(char.IsLetterOrDigit);
        pictionaryRevealedWord = string.Empty;
        pictionaryPhase = PictionaryPhase.Drawing;
        pictionaryPhaseEndsUtc = DateTime.UtcNow + TimeSpan.FromSeconds(pictionaryDrawingSeconds);
        pictionaryGuessedPlayers.Clear();
        ResetLocalPictionaryAnswerFeedback();
        pictionaryRoundFirstGuesserId = string.Empty;
        pictionaryRoundFirstGuesserName = string.Empty;
        pictionaryRoundFirstGuesserPoints = 0;
        pictionaryNextDrawerId = string.Empty;
        pictionaryThirtySecondNoticeSent = false;
        pictionaryTenSecondNoticeSent = false;
        if (whiteboardSessionId is not null)
        {
            SendAndApplyWhiteboardCommand($"C|{whiteboardSessionId}");
        }
        BroadcastPictionaryFeed($"{pictionaryDrawerName} is drawing!", pictionaryDrawerName, pictionaryDrawerId, false, false);
        SendPictionaryState();
        SendDrawerSecretIfNeeded();
    }

    private void SendDrawerSecretIfNeeded()
    {
        if (stream.Mode != StreamMode.Hosting || pictionarySessionId is null ||
            pictionaryPhase != PictionaryPhase.Drawing || string.IsNullOrEmpty(pictionarySelectedWord) ||
            string.Equals(pictionaryDrawerId, LocalPictionaryUserId, StringComparison.Ordinal))
        {
            return;
        }
        if (GameRelay("pictionary") is not null) { PublishRemainingState("pictionary"); return; }
        _ = stream.SendPictionaryCommandAsync(
            $"D|{pictionarySessionId}|{pictionaryRound}|{pictionaryDrawerId}|{EncodePictionaryText(pictionarySelectedWord)}");
    }

    private void SubmitPictionaryGuess()
    {
        var guess = pictionaryGuessInput.Trim();
        pictionaryGuessInput = string.Empty;
        SubmitPictionaryGuess(guess);
    }

    private void SubmitPictionaryGuess(string guess, Guid? replyToId = null)
    {
        guess = guess.Trim();
        if (guess.Length == 0 || pictionaryPhase != PictionaryPhase.Drawing ||
            LocalIsPictionaryDrawer ||
            pictionaryGuessedPlayers.Contains(LocalPictionaryUserId) ||
            pictionarySessionId is null)
        {
            return;
        }

        if (stream.Mode == StreamMode.Hosting)
        {
            HostHandlePictionaryGuess(LocalPictionaryUserId, guess, replyToId);
        }
        else
        {
            SendArcadePlayerAction("pictionary",
                $"G|{pictionarySessionId}|{pictionaryRound}|{replyToId?.ToString("N") ?? string.Empty}|{EncodePictionaryText(guess)}");
        }
    }

    internal bool PictionaryTvGuessInputActive =>
        pictionaryTvGuessInputActive;

    internal string PictionaryTvGuessInput =>
        pictionaryTvGuessInput;

    internal void SetPictionaryTvGuessInput(string value)
    {
        if (PictionaryTeamsActive)
        {
            pictionaryTvGuessInput = value.Length <= 80 ? value : value[..80];
            whiteboardTvDirty = true;
            return;
        }
        var letterLimit = pictionaryMaskedWord.Count(character => character == '_');
        if (letterLimit <= 0)
            letterLimit = pictionaryWordLength;
        var separatorLimit = CountPictionaryMaskWordSpaces(pictionaryMaskedWord) +
            pictionaryMaskedWord.Count(character => character == '-');
        var letters = 0;
        var separators = 0;
        var cleanedBuilder = new StringBuilder(Math.Min(value.Length, letterLimit + separatorLimit));
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (letters >= letterLimit)
                    continue;
                cleanedBuilder.Append(character);
                letters++;
            }
            else if ((char.IsWhiteSpace(character) || character == '-') &&
                     separators < separatorLimit && cleanedBuilder.Length > 0 &&
                     cleanedBuilder[^1] != ' ' && cleanedBuilder[^1] != '-')
            {
                cleanedBuilder.Append(character == '-' ? '-' : ' ');
                separators++;
            }
        }
        var cleaned = cleanedBuilder.ToString();
        if (string.Equals(cleaned, pictionaryTvGuessInput, StringComparison.Ordinal))
            return;
        pictionaryTvGuessInput = cleaned;
        whiteboardTvDirty = true;
    }

    private static int CountPictionaryMaskWordSpaces(string mask)
    {
        var count = 0;
        for (var index = 0; index < mask.Length;)
        {
            if (mask[index] != ' ')
            {
                index++;
                continue;
            }
            var start = index;
            while (index < mask.Length && mask[index] == ' ')
                index++;
            count += Math.Max(0, (index - start - 1) / 2);
        }
        return count;
    }

    internal bool IsPictionaryTvControlAt(Vector2 uv) =>
        TryGetPictionaryTvTarget(uv, out _);

    internal void SetPictionaryTvHover(Vector2? uv)
    {
        var target = uv is { } position && TryGetPictionaryTvTarget(position, out var hitTarget)
            ? hitTarget
            : -1;
        if (target == pictionaryTvHoveredTarget)
            return;
        pictionaryTvHoveredTarget = target;
        whiteboardTvDirty = true;
    }

    internal void SubmitPictionaryTvControlAt(Vector2 uv)
    {
        if (!TryGetPictionaryTvTarget(uv, out var target))
            return;
        if (target == TeamsTvJoinRedTarget) { SubmitTeamsJoin(0); return; }
        if (target == TeamsTvJoinBlueTarget) { SubmitTeamsJoin(1); return; }
        if (target == TeamsTvConfirmTarget) { ConfirmTeams(); return; }
        if (target >= TeamsTvWordChoiceTargetStart && target < TeamsTvWordChoiceTargetStart + 3)
        { SubmitTeamsWord(TeamsLocalDrawerColour, target - TeamsTvWordChoiceTargetStart); return; }
        if (target == TeamsTvPlayAgainTarget) { StartTeamsNewCycle(); return; }
        if (target == PictionaryTvGuessStartTarget)
        {
            pictionaryTvGuessInput = string.Empty;
            pictionaryTvGuessInputActive = true;
            pictionaryTvHoveredTarget = -1;
            whiteboardTvDirty = true;
        }
        else if (target == PictionaryTvGuessSubmitTarget)
        {
            FinishPictionaryTvGuess(submit: true);
        }
        else if (target == PictionaryTvGuessCancelTarget)
        {
            FinishPictionaryTvGuess(submit: false);
        }
        else if (target == PictionaryTvPlayAgainTarget &&
                 pictionaryPhase == PictionaryPhase.GameOver &&
                 stream.Mode == StreamMode.Hosting)
        {
            StartPictionaryGame();
        }
        else if (target >= PictionaryTvWordChoiceTargetStart)
        {
            if (!SubmitPictionaryTvDrawingTarget(target))
                SubmitPictionaryWordChoice(target - PictionaryTvWordChoiceTargetStart);
        }
    }

    internal void FinishPictionaryTvGuess(bool submit)
    {
        var guess = pictionaryTvGuessInput.Trim();
        pictionaryTvGuessInput = string.Empty;
        pictionaryTvGuessInputActive = false;
        pictionaryTvHoveredTarget = -1;
        whiteboardTvDirty = true;
        if (submit && guess.Length > 0)
        {
            if (PictionaryTeamsActive) SubmitTeamsGuess(guess);
            else SubmitPictionaryGuess(guess);
        }
    }

    private void CancelPictionaryTvGuess() =>
        FinishPictionaryTvGuess(submit: false);

    private bool TryGetPictionaryTvTarget(Vector2 uv, out int target)
    {
        target = -1;

        if (TryGetPictionaryTvDrawingTarget(uv, out target))
            return true;

        if (PictionaryTeamsActive)
        {
            if (teamsPhase == TeamsPhase.Lobby)
            {
                if (stream.Mode == StreamMode.Hosting && TeamsReady && uv.X >= 470f / 1280f &&
                    uv.X <= 810f / 1280f && uv.Y >= 500f / 720f && uv.Y <= 552f / 720f)
                    target = TeamsTvConfirmTarget;
                else if (uv.Y >= 614f / 720f && uv.Y <= 685f / 720f)
                    target = uv.X < 0.5f ? TeamsTvJoinRedTarget : TeamsTvJoinBlueTarget;
                return target >= 0;
            }
            var x = uv.X * WhiteboardRenderer.Width;
            var y = uv.Y * WhiteboardRenderer.Height;
            if (TeamsCanPlayAgainOnTv &&
                x >= 480 && x <= 800 && y >= 540 && y <= 596)
            { target = TeamsTvPlayAgainTarget; return true; }
            var drawer = TeamsLocalDrawerColour;
            if (TeamsCanChooseTvWord)
            {
                var left = drawer == 0 ? 120 : 760;
                for (var i = 0; i < teamsChoices[drawer].Length; i++)
                    if (x >= left && x <= left + 400 && y >= 240 + i * 80 && y <= 300 + i * 80)
                    { target = TeamsTvWordChoiceTargetStart + i; return true; }
            }
            if (!TeamsCanGuess || y < 540 || y > 596) return false;
            var centerX = TeamsLocalColour == 0 ? 320 : 960;
            if (!pictionaryTvGuessInputActive && x >= centerX - 130 && x <= centerX + 130)
                target = PictionaryTvGuessStartTarget;
            else if (pictionaryTvGuessInputActive && x >= centerX - 160 && x <= centerX - 15)
                target = PictionaryTvGuessSubmitTarget;
            else if (pictionaryTvGuessInputActive && x >= centerX + 15 && x <= centerX + 160)
                target = PictionaryTvGuessCancelTarget;
            return target >= 0;
        }

        var pixelX = uv.X * WhiteboardRenderer.Width;
        var pixelY = uv.Y * WhiteboardRenderer.Height;
        if (pictionaryPhase == PictionaryPhase.GameOver && stream.Mode == StreamMode.Hosting &&
            pixelX >= 480f && pixelX <= 800f && pixelY >= 540f && pixelY <= 612f)
        {
            target = PictionaryTvPlayAgainTarget;
            return true;
        }
        if (pictionaryPhase == PictionaryPhase.ChoosingWord && LocalIsPictionaryDrawer &&
            pictionaryWordChoices.Length == 3 && pixelY >= 292f && pixelY <= 412f)
        {
            for (var index = 0; index < 3; index++)
            {
                var left = 115f + index * 350f;
                if (pixelX >= left && pixelX <= left + 330f)
                {
                    target = PictionaryTvWordChoiceTargetStart + index;
                    return true;
                }
            }
            return false;
        }

        if (!LocalCanSubmitPictionaryGuess)
            return false;
        if (pixelY < 610f || pixelY > 666f)
            return false;

        if (!pictionaryTvGuessInputActive && pixelX >= 510f && pixelX <= 770f)
            target = PictionaryTvGuessStartTarget;
        else if (pictionaryTvGuessInputActive && pixelX >= 480f && pixelX <= 625f)
            target = PictionaryTvGuessSubmitTarget;
        else if (pictionaryTvGuessInputActive && pixelX >= 655f && pixelX <= 800f)
            target = PictionaryTvGuessCancelTarget;
        return target >= 0;
    }

    private void SubmitPartyChatMessage(string text)
    {
        var message = text.Trim();
        if (message.Length == 0)
        {
            return;
        }

        // During an active round, every composer behaves as a guessing input for eligible
        // players. The host evaluates the guess through the existing private game channel so a
        // correct answer is not broadcast to the room before it can be replaced by the success
        // announcement. Drawers, spectators, and normal chat continue through StreamChat.
        if (LocalCanSubmitPictionaryGuess)
        {
            var replyToId = partyChatReplyTarget?.Id;
            partyChatReplyTarget = null;
            SubmitPictionaryGuess(message, replyToId);
            return;
        }

        _ = stream.SendRichChatAsync(message, partyChatReplyTarget?.Id);
        partyChatReplyTarget = null;
    }

    private void HostHandlePictionaryGuess(string senderId, string guess, Guid? replyToId = null)
    {
        if (pictionaryPhase != PictionaryPhase.Drawing || !pictionaryActivePlayers.Contains(senderId) ||
            string.Equals(senderId, pictionaryDrawerId, StringComparison.Ordinal) ||
            pictionaryGuessedPlayers.Contains(senderId) || string.IsNullOrWhiteSpace(guess) || guess.Length > 80)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (pictionaryGuessRate.TryGetValue(senderId, out var last) && now - last < TimeSpan.FromMilliseconds(450))
        {
            return;
        }
        pictionaryGuessRate[senderId] = now;

        var name = pictionaryPlayerNames.GetValueOrDefault(senderId, "Player");
        if (NormalizePictionaryAnswer(guess) == NormalizePictionaryAnswer(pictionarySelectedWord))
        {
            var first = pictionaryGuessedPlayers.Count == 0;
            pictionaryGuessedPlayers.Add(senderId);
            var awardedPoints = first ? pictionaryFirstGuessPoints : pictionaryOtherGuessPoints;
            pictionaryScores[senderId] = pictionaryScores.GetValueOrDefault(senderId) + awardedPoints;
            if (first)
            {
                pictionaryRoundFirstGuesserId = senderId;
                pictionaryRoundFirstGuesserName = name;
                pictionaryRoundFirstGuesserPoints = awardedPoints;
            }
            if (first && pictionaryFirstGuesserDrawsNext)
            {
                pictionaryNextDrawerId = senderId;
            }
            BroadcastPictionaryFeed($"{name} guessed correctly! +{awardedPoints} {(awardedPoints == 1 ? "point" : "points")}", name, senderId, true, false, replyToId, guessResult: true);
            SendPictionaryState();

            var eligible = pictionaryActivePlayers.Count(id => !string.Equals(id, pictionaryDrawerId, StringComparison.Ordinal));
            if (eligible > 0 && pictionaryGuessedPlayers.Count >= eligible)
            {
                EndPictionaryRound();
            }
        }
        else
        {
            BroadcastPictionaryFeed($"{name}: {guess}", name, senderId, false, false, replyToId, guessResult: true, guessText: guess);
        }
    }

    private void EndPictionaryRound()
    {
        if (pictionaryPhase != PictionaryPhase.Drawing)
        {
            return;
        }

        // Clear the completed drawing before the next drawer is selected so the following
        // word-choice phase starts with a clean canvas on the host, viewers, and TV.
        if (whiteboardSessionId is not null)
        {
            SendAndApplyWhiteboardCommand($"C|{whiteboardSessionId}");
        }

        pictionaryRevealedWord = pictionarySelectedWord;
        pictionaryPhase = PictionaryPhase.RoundSummary;
        pictionaryDrawerNotifications.Clear();
        pictionaryPhaseEndsUtc = DateTime.UtcNow + PictionarySummaryTime;
        if (pictionaryGuessedPlayers.Count > 0 && pictionaryDrawerPoints > 0 &&
            pictionaryScores.ContainsKey(pictionaryDrawerId))
        {
            pictionaryScores[pictionaryDrawerId] += pictionaryDrawerPoints;
        }
        if (string.IsNullOrEmpty(pictionaryNextDrawerId))
        {
            var candidates = pictionaryActivePlayers
                .Where(id => !string.Equals(id, pictionaryDrawerId, StringComparison.Ordinal))
                .ToArray();
            pictionaryNextDrawerId = candidates.Length == 0 ? string.Empty : candidates[Random.Shared.Next(candidates.Length)];
        }
        BroadcastPictionaryFeed($"Round {pictionaryRound}: The answer was {pictionarySelectedWord}.",
            string.Empty, string.Empty, false, true);
        SendPictionaryState();
    }

    private void AnnouncePictionaryCycleWinner()
    {
        var mostPoints = pictionaryScores.Values.DefaultIfEmpty(0).Max();
        if (mostPoints <= 0)
        {
            BroadcastPictionaryFeed("Cycle complete: no points were earned.",
                string.Empty, string.Empty, false, true);
            return;
        }

        var winners = pictionaryScores.Where(player => player.Value == mostPoints)
            .Select(player => pictionaryPlayerNames.GetValueOrDefault(player.Key, "Player"))
            .ToArray();
        var tied = winners.Length > 1;
        BroadcastPictionaryFeed(
            $"Cycle {(tied ? "tie" : "winner")}: {string.Join(" and ", winners)} — {mostPoints} " +
            $"{(mostPoints == 1 ? "point" : "points")}{(tied ? " each" : " total")}",
            string.Empty, string.Empty, false, true);
    }

    private void StopPictionaryGame(string message = "Pictionary ended.")
    {
        if (stream.Mode == StreamMode.Hosting && pictionarySessionId is not null)
        {
            BroadcastPictionaryFeed(message, string.Empty, string.Empty, false, true);
            _ = stream.SendPictionaryCommandAsync($"X|{pictionarySessionId}|{EncodePictionaryText(message)}");
        }
        if (whiteboardSessionId is not null && stream.Mode == StreamMode.Hosting)
        {
            SendAndApplyWhiteboardCommand($"X|{whiteboardSessionId}");
        }
        ResetPictionaryLocal();
        pictionaryShowSetup = true;
    }

    private void ResetPictionaryLocal()
    {
        CaptureArcadeScoresBeforeReset();
        ResetRemainingRelay("pictionary");
        pictionarySessionId = null;
        pictionaryPhase = PictionaryPhase.Inactive;
        pictionaryRound = 0;
        pictionaryDrawerId = string.Empty;
        pictionaryDrawerName = string.Empty;
        pictionarySelectedWord = string.Empty;
        pictionaryRevealedWord = string.Empty;
        pictionaryMaskedWord = string.Empty;
        pictionaryWordLength = 0;
        pictionaryWordChoices = [];
        pictionaryGuessedPlayers.Clear();
        pictionaryActivePlayers.Clear();
        pictionaryRosterSignature = string.Empty;
        pictionaryHostAuthority = false;
        pictionaryShowSetup = true;
        pictionaryAnnouncedNameSessionId = null;
        pictionaryAnnouncedName = null;
        pictionaryTvGuessInputActive = false;
        pictionaryTvGuessInput = string.Empty;
        pictionaryTvHoveredTarget = -1;
        ResetLocalPictionaryAnswerFeedback();
        CancelPictionaryTvDrawing();
        pictionaryRoundFirstGuesserId = string.Empty;
        pictionaryRoundFirstGuesserName = string.Empty;
        pictionaryRoundFirstGuesserPoints = 0;
        pictionaryLastTvSecond = -1;
        pictionaryLastTvAnimationTick = -1;
    }

    private void ApplyPictionaryContribution(string senderId, string command)
    {
        var parts = command.Split('|', 5);
        if (parts[0] == "N" && parts.Length == 3 && pictionarySessionId is not null &&
            string.Equals(parts[1], pictionarySessionId, StringComparison.Ordinal) &&
            pictionaryActivePlayers.Contains(senderId))
        {
            var displayName = DecodePictionaryText(parts[2]).Trim();
            if (displayName.Length is > 0 and <= 50 &&
                !string.Equals(pictionaryPlayerNames.GetValueOrDefault(senderId), displayName,
                    StringComparison.Ordinal))
            {
                pictionaryPlayerNames[senderId] = displayName;
                if (string.Equals(senderId, pictionaryDrawerId, StringComparison.Ordinal))
                    pictionaryDrawerName = displayName;
                SendPictionaryState();
            }
            return;
        }

        if (parts.Length < 3 || pictionarySessionId is null ||
            !string.Equals(parts[1], pictionarySessionId, StringComparison.Ordinal) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var round) ||
            round != pictionaryRound)
        {
            return;
        }

        if (parts[0] == "G" && parts.Length is 4 or 5)
        {
            var replyToId = parts.Length == 5 && Guid.TryParseExact(parts[3], "N", out var parsedReplyId)
                ? parsedReplyId : (Guid?)null;
            HostHandlePictionaryGuess(senderId,
                DecodePictionaryText(parts.Length == 5 ? parts[4] : parts[3]), replyToId);
        }
        else if (parts[0] == "W" && parts.Length == 4 &&
                 string.Equals(senderId, pictionaryDrawerId, StringComparison.Ordinal) &&
                 pictionaryPhase == PictionaryPhase.ChoosingWord && int.TryParse(parts[3], out var index))
        {
            SelectPictionaryWord(index);
        }
    }

    private void SendPictionaryState()
    {
        if (stream.Mode != StreamMode.Hosting || pictionarySessionId is null)
        {
            return;
        }
        // A guess or phase change can publish before the next roster tick.
        // Prune here as well so a departed score cannot enter a pending snapshot.
        PruneDisconnectedPictionaryScores();
        var snapshot = new PictionarySnapshot(
            pictionarySessionId,
            pictionaryPhase,
            pictionaryRound,
            pictionaryRoundCount,
            pictionaryPhaseEndsUtc == DateTime.MaxValue
                ? 0
                : new DateTimeOffset(pictionaryPhaseEndsUtc).ToUnixTimeMilliseconds(),
            pictionaryDrawerId,
            pictionaryDrawerName,
            pictionaryWordLength,
            BuildPictionaryMask(pictionarySelectedWord),
            pictionaryRevealedWord,
            pictionaryRandomDrawerSelection,
            pictionaryDrawingSeconds,
            pictionaryFirstGuessPoints,
            pictionaryOtherGuessPoints,
            pictionaryDrawerPoints,
            pictionaryFirstGuesserDrawsNext,
            pictionaryRoundFirstGuesserId,
            pictionaryRoundFirstGuesserName,
            pictionaryRoundFirstGuesserPoints,
            pictionaryGuessedPlayers.ToArray(),
            pictionaryScores.Select(pair => new PictionaryPlayerState(
                pair.Key,
                pictionaryPlayerNames.GetValueOrDefault(pair.Key, "Player"),
                pair.Value,
                pictionaryActivePlayers.Contains(pair.Key))).ToArray());
        var command = "S|" + EncodePictionaryJson(snapshot);
        whiteboardTvDirty = true;
        if (!TrySendRemainingState("pictionary", command)) _ = stream.SendPictionaryCommandAsync(command);
    }

    private void ApplyPictionaryCommand(string command)
    {
        var parts = command.Split('|', 5);
        if (parts[0] == "S" && parts.Length == 2)
        {
            var snapshot = DecodePictionaryJson<PictionarySnapshot>(parts[1]);
            if (snapshot is null)
            {
                return;
            }
            var previousSessionId = pictionarySessionId;
            var previousRound = pictionaryRound;
            var wasActive = PictionaryActive;
            pictionarySessionId = snapshot.SessionId;
            if (stream.Mode == StreamMode.Viewing)
            {
                pictionaryHostAuthority = false;
            }
            pictionaryPhase = snapshot.Phase;
            pictionaryRound = snapshot.Round;
            pictionaryPhaseEndsUtc = snapshot.PhaseEndsUnixMs == 0
                ? DateTime.MaxValue
                : DateTimeOffset.FromUnixTimeMilliseconds(snapshot.PhaseEndsUnixMs).UtcDateTime;
            pictionaryDrawerId = snapshot.DrawerId;
            pictionaryDrawerName = snapshot.DrawerName;
            pictionaryMaskedWord = snapshot.MaskedWord;
            pictionaryWordLength = snapshot.WordLength;
            pictionaryRevealedWord = snapshot.RevealedWord;
            pictionaryRandomDrawerSelection = snapshot.RandomDrawerSelection;
            pictionaryRoundCount = Math.Clamp(snapshot.RoundCount, 1, 50);
            pictionaryDrawingSeconds = Math.Clamp(snapshot.DrawingSeconds, 15, 300);
            pictionaryFirstGuessPoints = Math.Clamp(snapshot.FirstGuessPoints, 0, 20);
            pictionaryOtherGuessPoints = Math.Clamp(snapshot.OtherGuessPoints, 0, 20);
            pictionaryDrawerPoints = Math.Clamp(snapshot.DrawerPoints, 0, 20);
            pictionaryFirstGuesserDrawsNext = snapshot.FirstGuesserDrawsNext;
            pictionaryRoundFirstGuesserId = snapshot.FirstGuesserId ?? string.Empty;
            pictionaryRoundFirstGuesserName = snapshot.FirstGuesserName ?? string.Empty;
            pictionaryRoundFirstGuesserPoints = snapshot.FirstGuesserPoints;
            pictionaryShowSetup = false;
            pictionaryGuessedPlayers.Clear();
            pictionaryGuessedPlayers.UnionWith(snapshot.GuessedUserIds);
            if (!string.Equals(previousSessionId, snapshot.SessionId, StringComparison.Ordinal) ||
                previousRound != snapshot.Round)
            {
                ResetLocalPictionaryAnswerFeedback();
            }
            if (pictionaryGuessedPlayers.Contains(LocalPictionaryUserId))
            {
                pictionaryLocalAnswerFeedbackRound = snapshot.Round;
                pictionaryLocalAnswerCorrect = true;
            }
            pictionaryPlayerNames.Clear();
            pictionaryScores.Clear();
            pictionaryActivePlayers.Clear();
            foreach (var player in snapshot.Players)
            {
                var playerName = string.Equals(player.Id, LocalPictionaryUserId, StringComparison.Ordinal)
                    ? LocalPictionaryDisplayName()
                    : player.Name;
                pictionaryPlayerNames[player.Id] = playerName;
                pictionaryScores[player.Id] = player.Score;
                if (player.Active)
                {
                    pictionaryActivePlayers.Add(player.Id);
                }
            }
            // A relay snapshot can have been queued before a departure. The
            // host's current roster wins over that historical score list.
            if (stream.Mode == StreamMode.Hosting) PruneDisconnectedPictionaryScores();
            AwardPictionaryActivityPoints();
            if (string.Equals(pictionaryDrawerId, LocalPictionaryUserId, StringComparison.Ordinal))
                pictionaryDrawerName = LocalPictionaryDisplayName();
            whiteboardTvDirty = true;
            if (!LocalIsPictionaryDrawer || snapshot.Phase is not (PictionaryPhase.ChoosingWord or PictionaryPhase.Drawing))
            {
                pictionarySelectedWord = snapshot.RevealedWord;
                pictionaryWordChoices = [];
            }
            if (!wasActive || !string.Equals(previousSessionId, snapshot.SessionId, StringComparison.Ordinal))
            {
                RequestPictionaryPrompt(snapshot.SessionId);
                if (stream.Mode == StreamMode.Viewing && !ViewerTvEnabled)
                    NotifyViewerPictionaryStartedWithoutTv();
            }
            AnnounceLocalPictionaryDisplayName(snapshot.SessionId);
            if (LocalIsPictionaryDrawer &&
                snapshot.Phase is PictionaryPhase.ChoosingDrawer or PictionaryPhase.ChoosingWord or PictionaryPhase.Drawing)
                OpenPictionaryForDrawingTurn(snapshot.SessionId, snapshot.Round);
            return;
        }

        if (parts[0] == "O" && parts.Length == 5 && string.Equals(parts[3], LocalPictionaryUserId, StringComparison.Ordinal))
        {
            pictionaryWordChoices = DecodePictionaryJson<string[]>(parts[4]) ?? [];
            whiteboardTvDirty = true;
            return;
        }
        if (parts[0] == "D" && parts.Length == 5 && string.Equals(parts[3], LocalPictionaryUserId, StringComparison.Ordinal))
        {
            pictionarySelectedWord = DecodePictionaryText(parts[4]);
            return;
        }
        if (parts[0] == "E" && parts.Length == 2)
        {
            var item = DecodePictionaryJson<PictionaryFeedItem>(parts[1]);
            if (item is not null)
            {
                AddPictionaryFeed(item);
            }
            return;
        }
        if (parts[0] == "X" && parts.Length >= 3 && string.Equals(parts[1], pictionarySessionId, StringComparison.Ordinal))
        {
            AddPictionaryFeed(DecodePictionaryText(parts[2]), string.Empty, string.Empty, false, true);
            ResetPictionaryLocal();
        }
    }

    private string LocalPictionaryDisplayName()
    {
        var name = CurrentSession?.DisplayName;
        if (string.IsNullOrWhiteSpace(name))
            name = CurrentDisplayName;
        return string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();
    }

    private void AnnounceLocalPictionaryDisplayName(string sessionId)
    {
        if (stream.Mode != StreamMode.Viewing || string.IsNullOrWhiteSpace(LocalPictionaryUserId))
            return;

        var name = LocalPictionaryDisplayName();
        if (string.Equals(pictionaryAnnouncedNameSessionId, sessionId, StringComparison.Ordinal) &&
            string.Equals(pictionaryAnnouncedName, name, StringComparison.Ordinal))
            return;

        pictionaryAnnouncedNameSessionId = sessionId;
        pictionaryAnnouncedName = name;
        SendArcadePlayerAction("pictionary",
            $"N|{sessionId}|{EncodePictionaryText(name)}");
    }

    private void BroadcastPictionaryFeed(string text, string name, string userId, bool success, bool system,
        Guid? replyToId = null, bool guessResult = false, string guessText = "")
    {
        var item = new PictionaryFeedItem(text, name, userId, success, system,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), replyToId, Guid.NewGuid(),
            pictionarySessionId ?? "", pictionaryRound, guessResult, guessText);
        AddPictionaryFeed(item);
        if (stream.Mode == StreamMode.Hosting)
        {
            if (!SendRemainingEvent("pictionary", "E|" + EncodePictionaryJson(item))) _ = stream.SendPictionaryCommandAsync("E|" + EncodePictionaryJson(item));
        }
    }

    private void AddPictionaryFeed(string text, string name, string userId, bool success, bool system) =>
        AddPictionaryFeed(new PictionaryFeedItem(text, name, userId, success, system,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ChatItemId: Guid.NewGuid()));

    private void AddPictionaryFeed(PictionaryFeedItem item)
    {
        QueuePictionaryDrawerGuess(item);
        // Pictionary uses its own transport, page and TV presentation. Keep its feed local
        // to those surfaces so guesses and game notices never become Watch Party messages.
        pictionaryFeed.Add(item);
        whiteboardTvDirty = true;
        if (pictionaryFeed.Count > 100)
        {
            pictionaryFeed.RemoveRange(0, pictionaryFeed.Count - 100);
        }

        var localGuessResult = !string.IsNullOrWhiteSpace(item.UserId) &&
            string.Equals(item.UserId, LocalPictionaryUserId, StringComparison.Ordinal) &&
            (item.Success ||
             !item.System && !string.IsNullOrWhiteSpace(item.Name) &&
             item.Text.StartsWith(item.Name + ": ", StringComparison.OrdinalIgnoreCase));
        if (localGuessResult)
        {
            pictionaryLocalAnswerFeedbackRound = pictionaryRound;
            pictionaryLocalAnswerCorrect = item.Success;
        }
    }

    private void ResetLocalPictionaryAnswerFeedback()
    {
        pictionaryDrawerNotifications.Clear();
        pictionaryLocalAnswerFeedbackRound = 0;
        pictionaryLocalAnswerCorrect = null;
    }

    private static string NormalizePictionaryAnswer(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
        }
        return builder.ToString();
    }

    private static string BuildPictionaryMask(string word) =>
        string.Join(' ', word.Select(character =>
            char.IsLetterOrDigit(character) ? "_" :
            char.IsWhiteSpace(character) ? " " :
            character.ToString()));

    private static string EncodePictionaryText(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string DecodePictionaryText(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    private static string EncodePictionaryJson<T>(T value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    private static T? DecodePictionaryJson<T>(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(Convert.FromBase64String(value)));
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return default;
        }
    }

    private int PictionarySecondsRemaining => pictionaryPhaseEndsUtc == DateTime.MaxValue
        ? 0
        : Math.Max(0, (int)Math.Ceiling((pictionaryPhaseEndsUtc - DateTime.UtcNow).TotalSeconds));

    private static string FormatPictionaryTimer(int totalSeconds)
    {
        totalSeconds = Math.Max(0, totalSeconds);
        return $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";
    }

    private string PictionaryVisibleWord
    {
        get
        {
            if (pictionaryPhase is PictionaryPhase.RoundSummary or PictionaryPhase.GameOver)
            {
                return pictionaryRevealedWord;
            }
            if (LocalIsPictionaryDrawer && !string.IsNullOrEmpty(pictionarySelectedWord))
            {
                return pictionarySelectedWord;
            }
            return pictionaryMaskedWord;
        }
    }

    private void ApplyPictionaryTvOverlay(byte[] pixels, int width, int height)
    {
        FillPictionaryPixels(pixels, width, height, 0, 0, width, 48, 0x181123);
        var word = PictionaryVisibleWord.ToUpperInvariant();
        var centreText = pictionaryPhase switch
        {
            PictionaryPhase.Countdown => "GET READY",
            PictionaryPhase.ChoosingDrawer => GetPictionarySelectionDisplayName().ToUpperInvariant(),
            PictionaryPhase.ChoosingWord => "CHOOSING WORD",
            PictionaryPhase.GameOver => "FINAL RESULTS",
            _ => string.IsNullOrWhiteSpace(word) ? "PICTIONARY" : word,
        };
        DrawPictionaryPixelText(pixels, width, height, 18, 14,
            pictionaryPhase == PictionaryPhase.GameOver
                ? "GAME OVER"
                : $"ROUND {Math.Max(1, pictionaryRound)}/{pictionaryRoundCount}",
            3, 0xF5F7FC);
        var centreTextScale = PictionaryTvTextScaleToFit(centreText, width - 430, 3);
        DrawPictionaryPixelTextCentered(pixels, width, height, width / 2,
            14 + (3 - centreTextScale) * 3, centreText, centreTextScale, 0xF5F7FC);
        if (pictionaryPhase != PictionaryPhase.GameOver)
            DrawPictionaryPixelText(pixels, width, height, width - 112, 14, $"{PictionarySecondsRemaining:00}s", 3, 0xB77DFF);

        if (pictionaryPhase == PictionaryPhase.Countdown)
        {
            FillPictionaryPixels(pixels, width, height, 0, 48, width, height - 48, 0x070707);
            DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 194,
                "PICTIONARY STARTS IN", 4, 0xAAB2CA);
            DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 284,
                PictionarySecondsRemaining.ToString("00", CultureInfo.InvariantCulture), 10, 0xB77DFF);
            DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 420,
                "GET READY TO DRAW AND GUESS", 3, 0xF5F7FC);
        }
        else if (pictionaryPhase == PictionaryPhase.ChoosingDrawer)
        {
            FillPictionaryPixels(pixels, width, height, 0, 48, width, height - 48, 0x070707);
            DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 248,
                pictionaryRandomDrawerSelection ? "CHOOSING THE NEXT DRAWER" : "FIRST GUESSER DRAWS NEXT",
                4, 0xAAB2CA);
            DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 334,
                GetPictionarySelectionDisplayName().ToUpperInvariant(), 6, 0xB77DFF);
        }
        else if (pictionaryPhase == PictionaryPhase.ChoosingWord)
        {
            FillPictionaryPixels(pixels, width, height, 0, 48, width, height - 48, 0x070707);
            if (LocalIsPictionaryDrawer && pictionaryWordChoices.Length == 3)
            {
                DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 188,
                    "CHOOSE A WORD", 4, 0xF5F7FC);
                DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 238,
                    "CLICK ONE OF THE THREE OPTIONS", 2, 0xAAB2CA);
                for (var index = 0; index < 3; index++)
                {
                    var x = 115 + index * 350;
                    var target = PictionaryTvWordChoiceTargetStart + index;
                    FillQuizTvPanel(pixels, width, height, x, 292, 330, 120,
                        0x252D48u, pictionaryTvHoveredTarget == target ? 0x512E8Fu : 0x38235Fu);
                    var choice = pictionaryWordChoices[index].ToUpperInvariant();
                    var choiceScale = PictionaryTvTextScaleToFit(choice, 292, 3);
                    var choiceY = 292 + (120 - 7 * choiceScale) / 2;
                    DrawPictionaryPixelTextCentered(pixels, width, height, x + 165, choiceY,
                        choice, choiceScale, 0xF5F7FC);
                }
            }
            else
            {
                DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 264,
                    $"{TruncateQuizTvText(pictionaryDrawerName.ToUpperInvariant(), 24)} IS CHOOSING",
                    4, 0xF5F7FC);
                DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 338,
                    "THREE WORDS TO CHOOSE FROM", 3, 0xAAB2CA);
            }
        }
        else if (pictionaryPhase == PictionaryPhase.GameOver)
        {
            DrawPictionaryGameOverTv(pixels, width, height);
        }
        else if (pictionaryPhase == PictionaryPhase.Drawing && LocalPictionaryAnswerResult is true)
        {
            FillQuizTvPanel(pixels, width, height, 510, 610, 260, 56, 0x174D32u, 0x2A8F60u);
            DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 628,
                "CORRECT - LOCKED IN", 2, 0x62E6A7);
        }
        else if (LocalCanSubmitPictionaryGuess)
        {
            if (pictionaryTvGuessInputActive)
            {
                FillPictionaryPixels(pixels, width, height, 200, 56, 880, 112, 0x11172A);
                var entryScale = pictionaryMaskedWord.Length > 34 ? 2 : 3;
                var guessLetters = BuildPictionaryTvGuessLetters(pictionaryMaskedWord, pictionaryTvGuessInput);
                DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 70,
                    guessLetters, entryScale, 0xF5F7FC);
                DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 98,
                    pictionaryMaskedWord, entryScale, 0xB77DFF);
                if (pictionaryTvGuessInput.Length == 0)
                    DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 136,
                        "TYPE YOUR GUESS", 2, 0xAAB2CA);

                FillQuizTvPanel(pixels, width, height, 480, 610, 145, 56,
                    0x38235Fu, pictionaryTvHoveredTarget == PictionaryTvGuessSubmitTarget ? 0x512E8Fu : 0x8C55F6u);
                DrawPictionaryPixelTextCentered(pixels, width, height, 552, 628,
                    "SUBMIT", 2, 0xF5F7FC);
                FillQuizTvPanel(pixels, width, height, 655, 610, 145, 56,
                    0x252D48u, pictionaryTvHoveredTarget == PictionaryTvGuessCancelTarget ? 0x20263Bu : 0x343D5Du);
                DrawPictionaryPixelTextCentered(pixels, width, height, 727, 628,
                    "CANCEL", 2, 0xF5F7FC);
            }
            else
            {
                FillQuizTvPanel(pixels, width, height, 510, 610, 260, 56,
                    0x38235Fu, pictionaryTvHoveredTarget == PictionaryTvGuessStartTarget ? 0x512E8Fu : 0x8C55F6u);
                DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 628,
                    "CLICK TO GUESS", 2, 0xF5F7FC);
            }
        }

        if (pictionaryPhase == PictionaryPhase.Drawing &&
            !LocalIsPictionaryDrawer &&
            LocalPictionaryAnswerResult is false)
        {
            DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 574,
                "INCORRECT - TRY AGAIN", 3, 0xFF5964);
        }

        if (pictionaryPhase == PictionaryPhase.RoundSummary)
        {
            FillPictionaryPixels(pixels, width, height, 0, 48, width, height - 48, 0x070707);
            var hasWinner = !string.IsNullOrWhiteSpace(pictionaryRoundFirstGuesserName);
            DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 74,
                hasWinner ? "FIRST CORRECT GUESS" : "NO CORRECT GUESSES", 4,
                hasWinner ? 0xB77DFFu : 0xAAB2CAu);
            if (hasWinner)
            {
                DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 124,
                    TruncateQuizTvText(pictionaryRoundFirstGuesserName.ToUpperInvariant(), 24), 5, 0xF5F7FC);
                DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 180,
                    $"+{pictionaryRoundFirstGuesserPoints} {(pictionaryRoundFirstGuesserPoints == 1 ? "POINT" : "POINTS")}",
                    3, 0x62E6A7);
            }
            var answerText = $"THE ANSWER WAS {pictionaryRevealedWord.ToUpperInvariant()}";
            var answerScale = PictionaryTvTextScaleToFit(answerText, width - 120, 3);
            DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, hasWinner ? 232 : 158,
                answerText, answerScale, 0xAAB2CA);

            var scoreboard = pictionaryScores
                .OrderByDescending(player => player.Value)
                .ThenBy(player => pictionaryPlayerNames.GetValueOrDefault(player.Key, "Player"), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var columns = scoreboard.Length > 8 ? 2 : 1;
            var rowsPerColumn = Math.Max(1, (int)Math.Ceiling(scoreboard.Length / (double)columns));
            var panelWidth = columns == 1 ? 620 : 1020;
            var panelX = (width - panelWidth) / 2;
            var panelY = hasWinner ? 274 : 210;
            var rowHeight = Math.Max(24, Math.Min(34, 270 / rowsPerColumn));
            FillPictionaryPixels(pixels, width, height, panelX, panelY, panelWidth, rowHeight * rowsPerColumn + 42, 0x11172A);
            DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, panelY + 13,
                "SCOREBOARD", 3, 0xF5F7FC);
            for (var index = 0; index < scoreboard.Length; index++)
            {
                var column = index / rowsPerColumn;
                var row = index % rowsPerColumn;
                var columnWidth = panelWidth / columns;
                var x = panelX + column * columnWidth + 22;
                var y = panelY + 49 + row * rowHeight;
                var playerName = pictionaryPlayerNames.GetValueOrDefault(scoreboard[index].Key, "Player");
                DrawPictionaryPixelText(pixels, width, height, x, y,
                    $"{index + 1}. {TruncateQuizTvText(playerName.ToUpperInvariant(), columns == 1 ? 28 : 17)}", 2, 0xF5F7FC);
                DrawPictionaryPixelText(pixels, width, height, panelX + (column + 1) * columnWidth - 92, y,
                    $"{scoreboard[index].Value} PT", 2, 0xB77DFF);
            }
            var nextLabel = pictionaryRound >= pictionaryRoundCount
                ? $"FINAL RESULTS IN {PictionarySecondsRemaining} SECONDS"
                : $"{PictionarySecondsRemaining} SECONDS UNTIL NEXT ROUND BEGINS";
            DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, height - 54,
                nextLabel, 2, 0xAAB2CA);
        }
        else if (pictionaryPhase == PictionaryPhase.Drawing && LocalIsPictionaryDrawer)
        {
            DrawPictionaryTvDrawingToolbar(pixels, width, height);
        }
        else if (pictionaryPhase == PictionaryPhase.Drawing)
        {
            DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 686,
                "ANSWER HERE ON THE TV OR IN THE ALPHA CHANNEL ACTIVITIES PAGE", 2, 0xAAB2CA);
        }

        var notice = pictionaryFeed.LastOrDefault(item =>
            item.System || item.Success || item.Text.EndsWith(" is drawing!", StringComparison.Ordinal));
        if (CurrentPictionaryDrawerNotification is { } drawerNotice)
        {
            FillPictionaryPixels(pixels, width, height, 0, height - 38, width, 38, 0x11172A);
            DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, height - 27,
                TruncateQuizTvText(drawerNotice.Text.ToUpperInvariant(), Math.Max(1, (width - 32) / 12)),
                2, drawerNotice.Correct ? 0x5CE695u : 0xFF5764u);
        }
        else if (!LocalIsPictionaryDrawer && pictionaryPhase is not (PictionaryPhase.RoundSummary or PictionaryPhase.GameOver) && notice is not null &&
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - notice.CreatedUnixMs <= 5500)
        {
            FillPictionaryPixels(pixels, width, height, 0, height - 38, width, 38, notice.Success ? 0x174D32u : 0x201638u);
            DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, height - 27, notice.Text.ToUpperInvariant(), 2, 0xF5F7FC);
        }
    }

    private void DrawPictionaryGameOverTv(byte[] pixels, int width, int height)
    {
        FillPictionaryPixels(pixels, width, height, 0, 48, width, height - 48, 0x070707);
        var standings = pictionaryScores
            .OrderByDescending(player => player.Value)
            .ThenBy(player => pictionaryPlayerNames.GetValueOrDefault(player.Key, "Player"), StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();

        DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 76,
            standings.Length > 0 ? "PICTIONARY CHAMPION" : "GAME COMPLETE", 4, 0xFFD166);

        if (standings.Length > 0)
        {
            DrawPictionaryStandingCard(pixels, width, height, standings[0], 1,
                260, 132, 760, 164, 0x201638u, 0x8C55F6u, 5);
        }

        if (standings.Length == 2)
        {
            DrawPictionaryStandingCard(pixels, width, height, standings[1], 2,
                445, 326, 390, 132, 0x11172Au, 0x343D5Du, 4);
        }
        else if (standings.Length >= 3)
        {
            DrawPictionaryStandingCard(pixels, width, height, standings[1], 2,
                235, 326, 390, 132, 0x11172Au, 0x343D5Du, 4);
            DrawPictionaryStandingCard(pixels, width, height, standings[2], 3,
                655, 326, 390, 132, 0x11172Au, 0x343D5Du, 4);
        }

        var hostCanRestart = stream.Mode == StreamMode.Hosting;
        FillQuizTvPanel(pixels, width, height, 480, 540, 320, 72,
            hostCanRestart ? 0x38235Fu : 0x151827u,
            hostCanRestart
                ? pictionaryTvHoveredTarget == PictionaryTvPlayAgainTarget ? 0x512E8Fu : 0x8C55F6u
                : 0x343D5Du);
        DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 563,
            hostCanRestart ? "PLAY AGAIN" : "WAITING FOR HOST", 3,
            hostCanRestart ? 0xF5F7FCu : 0xAAB2CAu);
        DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 646,
            hostCanRestart
                ? "CLICK PLAY AGAIN TO START A NEW GAME"
                : "FINAL RESULTS WILL REMAIN UNTIL THE HOST CONTINUES",
            2, 0xAAB2CA);
    }

    private void DrawPictionaryStandingCard(
        byte[] pixels,
        int width,
        int height,
        KeyValuePair<string, int> standing,
        int place,
        int x,
        int y,
        int cardWidth,
        int cardHeight,
        uint background,
        uint border,
        int preferredNameScale)
    {
        FillQuizTvPanel(pixels, width, height, x, y, cardWidth, cardHeight, background, border);
        var placeLabel = place switch
        {
            1 => "1ST PLACE",
            2 => "2ND PLACE",
            _ => "3RD PLACE",
        };
        DrawPictionaryPixelTextCentered(pixels, width, height, x + cardWidth / 2, y + 17,
            placeLabel, 3, place == 1 ? 0xFFD166u : 0xB77DFFu);
        var playerName = pictionaryPlayerNames.GetValueOrDefault(standing.Key, "Player").ToUpperInvariant();
        var nameScale = PictionaryTvTextScaleToFit(playerName, cardWidth - 44, preferredNameScale);
        DrawPictionaryPixelTextCentered(pixels, width, height, x + cardWidth / 2, y + 55,
            playerName, nameScale, 0xF5F7FC);
        var points = $"{standing.Value} {(standing.Value == 1 ? "POINT" : "POINTS")}";
        DrawPictionaryPixelTextCentered(pixels, width, height, x + cardWidth / 2, y + cardHeight - 35,
            points, 3, place == 1 ? 0x62E6A7u : 0xAAB2CAu);
    }

    private static string BuildPictionaryTvGuessLetters(string mask, string guess)
    {
        var letters = guess.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray();
        var result = Enumerable.Repeat(' ', mask.Length).ToArray();
        var letterIndex = 0;
        for (var index = 0; index < mask.Length && letterIndex < letters.Length; index++)
        {
            if (mask[index] == '_')
                result[index] = letters[letterIndex++];
        }
        return new string(result);
    }

    private static int PictionaryTvTextScaleToFit(string text, int availableWidth, int preferredScale)
    {
        for (var scale = Math.Max(1, preferredScale); scale > 1; scale--)
        {
            if (Math.Max(0, text.Length * 6 * scale - scale) <= availableWidth)
                return scale;
        }
        return 1;
    }

    private static void FillPictionaryPixels(byte[] pixels, int width, int height, int x, int y, int fillWidth, int fillHeight, uint colour)
    {
        AlphaChannel.Plugin.Video.ArcadeRaster.FillRectangle(
            pixels, width, height, x, y, fillWidth, fillHeight, colour);
    }

    private static void DrawPictionaryPixelTextCentered(byte[] pixels, int width, int height, int centerX, int y, string text, int scale, uint colour)
    {
        var textWidth = Math.Max(0, text.Length * 6 * scale - scale);
        DrawPictionaryPixelText(pixels, width, height, centerX - textWidth / 2, y, text, scale, colour);
    }

    private static void DrawPictionaryPixelText(byte[] pixels, int width, int height, int x, int y, string text, int scale, uint colour)
    {
        var cursor = x;
        foreach (var character in text)
        {
            var rows = PictionaryGlyph(character);
            for (var row = 0; row < rows.Length; row++)
            for (var column = 0; column < 5; column++)
            {
                if ((rows[row] & (1 << (4 - column))) == 0)
                {
                    continue;
                }
                FillPictionaryPixels(pixels, width, height, cursor + column * scale, y + row * scale, scale, scale, colour);
            }
            cursor += 6 * scale;
            if (cursor >= width - 5 * scale)
            {
                break;
            }
        }
    }

    private static ReadOnlySpan<byte> PictionaryGlyph(char value) => char.ToUpperInvariant(value) switch
    {
        'A' => [14, 17, 17, 31, 17, 17, 17], 'B' => [30, 17, 17, 30, 17, 17, 30],
        'C' => [14, 17, 16, 16, 16, 17, 14], 'D' => [30, 17, 17, 17, 17, 17, 30],
        'E' => [31, 16, 16, 30, 16, 16, 31], 'F' => [31, 16, 16, 30, 16, 16, 16],
        'G' => [14, 17, 16, 23, 17, 17, 15], 'H' => [17, 17, 17, 31, 17, 17, 17],
        'I' => [14, 4, 4, 4, 4, 4, 14], 'J' => [7, 2, 2, 2, 18, 18, 12],
        'K' => [17, 18, 20, 24, 20, 18, 17], 'L' => [16, 16, 16, 16, 16, 16, 31],
        'M' => [17, 27, 21, 21, 17, 17, 17], 'N' => [17, 25, 21, 19, 17, 17, 17],
        'O' => [14, 17, 17, 17, 17, 17, 14], 'P' => [30, 17, 17, 30, 16, 16, 16],
        'Q' => [14, 17, 17, 17, 21, 18, 13], 'R' => [30, 17, 17, 30, 20, 18, 17],
        'S' => [15, 16, 16, 14, 1, 1, 30], 'T' => [31, 4, 4, 4, 4, 4, 4],
        'U' => [17, 17, 17, 17, 17, 17, 14], 'V' => [17, 17, 17, 17, 17, 10, 4],
        'W' => [17, 17, 17, 21, 21, 21, 10], 'X' => [17, 17, 10, 4, 10, 17, 17],
        'Y' => [17, 17, 10, 4, 4, 4, 4], 'Z' => [31, 1, 2, 4, 8, 16, 31],
        '0' => [14, 17, 19, 21, 25, 17, 14], '1' => [4, 12, 4, 4, 4, 4, 14],
        '2' => [14, 17, 1, 2, 4, 8, 31], '3' => [30, 1, 1, 14, 1, 1, 30],
        '4' => [2, 6, 10, 18, 31, 2, 2], '5' => [31, 16, 16, 30, 1, 1, 30],
        '6' => [14, 16, 16, 30, 17, 17, 14], '7' => [31, 1, 2, 4, 8, 8, 8],
        '8' => [14, 17, 17, 14, 17, 17, 14], '9' => [14, 17, 17, 15, 1, 1, 14],
        '_' => [0, 0, 0, 0, 0, 0, 31], '-' => [0, 0, 0, 31, 0, 0, 0],
        '/' => [1, 2, 2, 4, 8, 8, 16], ':' => [0, 4, 4, 0, 4, 4, 0],
        '!' => [4, 4, 4, 4, 4, 0, 4], '?' => [14, 17, 1, 2, 4, 0, 4],
        '.' => [0, 0, 0, 0, 0, 0, 4], ',' => [0, 0, 0, 0, 0, 4, 8],
        '\'' => [4, 4, 8, 0, 0, 0, 0], '"' => [10, 10, 0, 0, 0, 0, 0],
        '(' => [2, 4, 8, 8, 8, 4, 2], ')' => [8, 4, 2, 2, 2, 4, 8],
        '&' => [12, 18, 20, 8, 21, 18, 13], '+' => [0, 4, 4, 31, 4, 4, 0],
        '↑' => [4, 14, 21, 4, 4, 4, 4], '↓' => [4, 4, 4, 4, 21, 14, 4],
        _ => [0, 0, 0, 0, 0, 0, 0],
    };
}

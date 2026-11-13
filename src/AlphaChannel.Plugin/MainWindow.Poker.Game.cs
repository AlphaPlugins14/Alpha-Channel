using System.Text;
using System.Text.Json;
using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private enum PokerPhase { Waiting, Countdown, Playing, Revealing, Showdown }
    private enum PokerStreet { Preflop, Flop, Turn, River }
    private sealed class PokerPlayer
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int Seat { get; set; }
        public int Stack { get; set; } = 1000;
        public int Bet { get; set; }
        public int Invested { get; set; }
        public int? HandProfit { get; set; }
        public bool Folded { get; set; }
        public bool Acted { get; set; }
        public bool InHand { get; set; }
        public string State { get; set; } = "READY";
        public int First { get; set; } = -1;
        public int Second { get; set; } = -1;
    }

    private sealed record PokerPublicPlayer(string Id, string Name, int Seat, int Stack,
        int Bet, bool Folded, bool InHand, string State, int First, int Second, bool Acted = false, int Invested = 0, int? HandProfit = null);
    private sealed record PokerSnapshot(string SessionId, int Revision, int Hand, PokerPhase Phase,
        PokerStreet Street, long EndsUnixMs, int Button, int ActingSeat, int CurrentBet,
        int Pot, int[] Board, PokerPublicPlayer[] Players, string Message,
        string MusicTitle = "", string MusicUrl = "", long MusicStartedUnixMs = 0,
        int StartingChips = 1000, int SmallBlind = 10, int BigBlind = 20, int TurnSeconds = 25,
        bool BotEnabled = true, bool Paused = false, int ActionSequence = 0, PokerStanding[]? Ledger = null, int LastRaise = 20);
    private sealed record PokerStanding(string Id, string Name, int Stack, int Hands, int Wins, int Net, int Refills);

    private const string PokerBotId = "#poker-house-opponent";
    private int PokerSmallBlind = 10;
    private int PokerBigBlind = 20;
    private int PokerTurnSeconds = 25;
    private int pokerStartingChips = 1000;
    private bool pokerBotEnabled = true;
    private bool pokerPaused;
    private int pokerActionSequence;
    private bool pokerCustomRules;
    private int pokerCustomChips = 1000;
    private int pokerCustomBlind = 20;
    private int pokerCustomTurnSeconds = 25;
    private bool pokerCustomBot = true;
    private readonly Dictionary<string, PokerStanding> pokerLedger = new(StringComparer.Ordinal);
    private readonly List<PokerPlayer> pokerPlayers = [];
    private readonly List<int> pokerDeck = [];
    private readonly List<int> pokerBoard = [];
    private string? pokerSessionId;
    private PokerPhase pokerPhase = PokerPhase.Waiting;
    private PokerStreet pokerStreet;
    private int pokerRevision;
    private int pokerHand;
    private int pokerButton = -1;
    private int pokerActingSeat = -1;
    private int pokerCurrentBet;
    private int pokerLastRaise = 20;
    private int pokerPot;
    private int pokerDeckPosition;
    private DateTime pokerPhaseEndsUtc = DateTime.MaxValue;
    private DateTime pokerLastSnapshotUtc;
    private DateTime pokerLastFrameUtc;
    private DateTime pokerRevealUtc;
    private string pokerMessage = "SIT TO PLAY";
    private int pokerLocalFirst = -1;
    private int pokerLocalSecond = -1;
    private int pokerLocalCardsHand = -1;
    private (string Session, int Hand, int First, int Second)? pokerPendingPrivateCards;
    private DateTime pokerNextCardRequestUtc = DateTime.MinValue;
    private string LocalPokerUserId => CurrentSession?.AccountId ?? stream.LocalUserId ?? "local";
    private PokerPlayer? LocalPokerPlayer => pokerPlayers.FirstOrDefault(p => p.Id == LocalPokerUserId);
    private int PokerSecondsLeft => pokerPhaseEndsUtc == DateTime.MaxValue ? 0 :
        Math.Max(0, (int)Math.Ceiling((pokerPhaseEndsUtc - DateTime.UtcNow).TotalSeconds));

    private void ResetPokerLocal()
    {
        CaptureArcadeScoresBeforeReset("poker");
        DisposeActivityRelay("poker");
        ResetPokerMusic();
        pokerSessionId = pokerWhiteboardSessionId = null;
        pokerPlayers.Clear();
        pokerLedger.Clear();
        pokerPaused = false;
        pokerActionSequence = 0;
        pokerDeck.Clear();
        pokerBoard.Clear();
        pokerPhase = PokerPhase.Waiting;
        pokerHand = pokerRevision = pokerPot = pokerCurrentBet = 0;
        pokerButton = pokerActingSeat = -1;
        pokerLocalFirst = pokerLocalSecond = pokerLocalCardsHand = -1;
        pokerPendingPrivateCards = null;
        pokerNextCardRequestUtc = DateTime.MinValue;
        pokerPhaseEndsUtc = DateTime.MaxValue;
        pokerMessage = "SIT TO PLAY";
        pokerPageRendered = false;
        whiteboardTvDirty = true;
    }

    internal void UpdatePoker()
    {
        if (stream.Mode == StreamMode.None)
        {
            stream.IncomingPokerCommands.Clear();
        }
        while (stream.IncomingPokerCommands.TryDequeue(out var incoming))
        {
            ReceiveArcadeServerCommand("poker", incoming.UserId, incoming.Command);
        }
        UpdateActivityRelay("poker");
        if (pokerSessionId is null) return;
        if (PokerActive) UpdatePokerMusic();
        else pokerMusicPlayer.Stop();
        var now = DateTime.UtcNow;
        if (IsLocalActivityHost)
        {
            if (!whiteboardActive || whiteboardSessionId != pokerWhiteboardSessionId)
            {
                ResetPokerLocal();
                return;
            }
            // A departed player folds immediately. They can take a seat again next hand.
            foreach (var player in pokerPlayers.Where(p => p.Id != LocalPokerUserId && p.Id != PokerBotId &&
                         p.State != "LEFT" &&
                         !stream.Roster.Any(member => member.UserId == p.Id)).ToArray())
                LeavePokerSeat(player);
            if (pokerPhase == PokerPhase.Countdown && now >= pokerPhaseEndsUtc) BeginPokerHand();
            else if (pokerPhase == PokerPhase.Playing && now >= pokerPhaseEndsUtc)
            {
                var actor = pokerPlayers.FirstOrDefault(p => p.Seat == pokerActingSeat);
                if (actor is not null)
                    ActPoker(actor, pokerCurrentBet == actor.Bet ? "check" : "fold");
            }
            else if (pokerPhase == PokerPhase.Revealing && now >= pokerPhaseEndsUtc)
                StartPokerStreetAction();
            else if (pokerPhase == PokerPhase.Showdown && now >= pokerPhaseEndsUtc)
                QueueNextPokerHand();
            if (pokerPhase == PokerPhase.Playing && pokerPlayers.FirstOrDefault(p => p.Seat == pokerActingSeat)?.Id == PokerBotId &&
                now >= pokerPhaseEndsUtc.AddSeconds(-PokerTurnSeconds + 1))
                ActPoker(pokerPlayers.First(p => p.Id == PokerBotId), "call");
            if (now - pokerLastSnapshotUtc >= TimeSpan.FromSeconds(3)) SendPokerState();
        }
        else if (stream.Mode == StreamMode.Viewing && GameRelay("poker") is null && LocalPokerPlayer?.InHand == true &&
                 pokerPhase is PokerPhase.Playing or PokerPhase.Revealing or PokerPhase.Showdown &&
                 (pokerLocalCardsHand != pokerHand || pokerLocalFirst < 0 || pokerLocalSecond < 0) &&
                 now >= pokerNextCardRequestUtc)
        {
            pokerNextCardRequestUtc = now.AddSeconds(3);
            SendArcadePlayerAction("poker", $"R|{pokerSessionId}|{pokerHand}");
        }
        if (now - pokerLastFrameUtc >= TimeSpan.FromMilliseconds(80))
        {
            pokerLastFrameUtc = now;
            pokerPageRendered = false;
            whiteboardTvDirty = true;
        }
    }

    private void SitPokerSeat(int seat)
    {
        if (pokerSessionId is null || seat is < 0 or >= 16 || LocalPokerPlayer is not null) return;
        if (IsLocalActivityHost) AcceptPokerSeat(LocalPokerUserId, LocalPictionaryDisplayName(), seat);
        else if (stream.Mode == StreamMode.Viewing)
            SendArcadePlayerAction("poker", $"J|{pokerSessionId}|{seat}");
    }

    private void RequestLeavePokerSeat()
    {
        if (pokerSessionId is null || LocalPokerPlayer is null) return;
        if (IsLocalActivityHost) LeavePokerSeat(LocalPokerPlayer);
        else SendArcadePlayerAction("poker", $"L|{pokerSessionId}");
    }

    private void RequestPokerAction(string action)
    {
        if (pokerSessionId is null || LocalPokerPlayer?.Seat != pokerActingSeat ||
            pokerPhase != PokerPhase.Playing || pokerLocalCardsHand != pokerHand ||
            pokerLocalFirst < 0 || pokerLocalSecond < 0) return;
        if (IsLocalActivityHost) ActPoker(LocalPokerPlayer, action);
        else SendArcadePlayerAction("poker", $"A|{pokerSessionId}|{pokerHand}|{pokerActionSequence}|{action}");
    }

    private void AcceptPokerSeat(string id, string name, int seat)
    {
        if (pokerPlayers.Any(p => p.Id == id || p.Seat == seat)) return;
        var stored = pokerLedger.GetValueOrDefault(id);
        var player = new PokerPlayer { Id = id, Name = name, Seat = seat, Stack = stored?.Stack ?? pokerStartingChips };
        pokerPlayers.Add(player);
        if (stored is null) pokerLedger[id] = new PokerStanding(id, name, player.Stack, 0, 0, 0, 0);
        else pokerLedger[id] = stored with { Name = name };
        if (pokerPhase == PokerPhase.Waiting) pokerMessage = "HOST: DEAL WHEN READY";
        pokerRevision++;
        SendPokerState();
    }

    private void LeavePokerSeat(PokerPlayer player)
    {
        if (player.Id == CurrentSession?.AccountId) CaptureArcadeScoresBeforeReset("poker");
        var wasActing = pokerActingSeat == player.Seat;
        if ((pokerPhase is PokerPhase.Playing or PokerPhase.Revealing) && player.InHand)
        {
            player.Folded = true;
            player.State = "LEFT";
            if (wasActing) AdvancePokerAction();
            else if (pokerPlayers.Count(p => p.InHand && !p.Folded) <= 1) FinishPokerHand();
        }
        else
        {
            RememberPokerStack(player);
            pokerPlayers.Remove(player);
        }
        if (pokerPlayers.All(p => p.Id == PokerBotId || p.State == "LEFT"))
        {
            pokerPlayers.Clear();
            pokerPhase = PokerPhase.Waiting;
            pokerPhaseEndsUtc = DateTime.MaxValue;
            pokerMessage = "SIT TO PLAY";
        }
        pokerRevision++;
        SendPokerState();
    }

    private void RememberPokerStack(PokerPlayer player)
    {
        if (pokerLedger.TryGetValue(player.Id, out var standing))
            pokerLedger[player.Id] = standing with { Stack = player.Stack };
    }

    private void DealPokerFromLobby()
    {
        if (!IsLocalActivityHost || pokerPhase != PokerPhase.Waiting || pokerPlayers.Count == 0 ||
            !pokerBotEnabled && pokerPlayers.Count < 2) return;
        pokerPaused = false;
        pokerPhase = PokerPhase.Countdown;
        pokerPhaseEndsUtc = DateTime.UtcNow.AddSeconds(5);
        pokerMessage = "GET READY TO PLAY";
        pokerRevision++;
        SendPokerState();
    }

    private void TogglePokerPause()
    {
        if (!IsLocalActivityHost) return;
        pokerPaused = !pokerPaused;
        if (pokerPaused && pokerPhase == PokerPhase.Countdown)
        {
            pokerPhase = PokerPhase.Waiting;
            pokerPhaseEndsUtc = DateTime.MaxValue;
            pokerMessage = "TABLE PAUSED";
        }
        else if (!pokerPaused && pokerPhase == PokerPhase.Waiting && pokerHand > 0) QueueNextPokerHand();
        pokerRevision++;
        SendPokerState();
    }

    private void QueueNextPokerHand()
    {
        foreach (var player in pokerPlayers) RememberPokerStack(player);
        pokerPlayers.RemoveAll(p => p.Id == PokerBotId || p.State == "LEFT");
        if (!pokerPlayers.Any() || pokerPaused || pokerHand == 0 || !pokerBotEnabled && pokerPlayers.Count < 2)
        {
            pokerPhase = PokerPhase.Waiting;
            pokerPhaseEndsUtc = DateTime.MaxValue;
            pokerMessage = !pokerPlayers.Any() ? "SIT TO PLAY" : pokerPaused ? "TABLE PAUSED" : "HOST: DEAL WHEN READY";
            pokerRevision++;
            SendPokerState();
            return;
        }
        pokerPhase = PokerPhase.Countdown;
        pokerPhaseEndsUtc = DateTime.UtcNow.AddSeconds(5);
        pokerMessage = "NEXT HAND";
        pokerRevision++;
        SendPokerState();
    }

    private void BeginPokerHand()
    {
        pokerPlayers.RemoveAll(p => p.Id == PokerBotId);
        if (pokerPlayers.Count == 0) { QueueNextPokerHand(); return; }
        if (pokerPlayers.Count == 1 && !pokerBotEnabled) { QueueNextPokerHand(); return; }
        if (pokerPlayers.Count == 1)
        {
            var seat = (pokerPlayers[0].Seat + 1) % 16;
            pokerPlayers.Add(new PokerPlayer { Id = PokerBotId, Name = "HOUSE BOT", Seat = seat });
        }
        foreach (var player in pokerPlayers)
        {
            if (player.Id != PokerBotId)
            {
                var standing = pokerLedger[player.Id];
                if (player.Stack < PokerBigBlind)
                {
                    player.Stack = pokerStartingChips;
                    standing = standing with { Refills = standing.Refills + 1 };
                }
                pokerLedger[player.Id] = standing with { Stack = player.Stack, Hands = standing.Hands + 1 };
            }
            else player.Stack = pokerStartingChips;
            player.Bet = player.Invested = 0;
            player.Folded = player.Acted = false;
            player.InHand = true;
            player.HandProfit = null;
            player.State = "PLAYING";
            player.First = player.Second = -1;
        }
        pokerHand++;
        pokerLocalCardsHand = pokerLocalFirst = pokerLocalSecond = -1;
        pokerDeck.Clear();
        pokerDeck.AddRange(Enumerable.Range(0, 52));
        for (var i = 51; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (pokerDeck[i], pokerDeck[j]) = (pokerDeck[j], pokerDeck[i]);
        }
        pokerDeckPosition = 0;
        pokerBoard.Clear();
        foreach (var player in pokerPlayers.OrderBy(p => p.Seat)) player.First = pokerDeck[pokerDeckPosition++];
        foreach (var player in pokerPlayers.OrderBy(p => p.Seat)) player.Second = pokerDeck[pokerDeckPosition++];
        pokerButton = NextPokerSeat(pokerButton);
        var small = NextPokerSeat(pokerButton);
        var big = NextPokerSeat(small);
        if (pokerPlayers.Count == 2) { small = pokerButton; big = NextPokerSeat(small); }
        PostPokerBlind(small, PokerSmallBlind);
        PostPokerBlind(big, PokerBigBlind);
        pokerCurrentBet = pokerPlayers.First(p => p.Seat == big).Bet;
        pokerLastRaise = PokerBigBlind;
        pokerPot = pokerPlayers.Sum(p => p.Invested);
        pokerStreet = PokerStreet.Preflop;
        pokerPhase = PokerPhase.Playing;
        pokerActingSeat = NextPokerActor(big);
        pokerActionSequence++;
        pokerPhaseEndsUtc = DateTime.UtcNow.AddSeconds(PokerTurnSeconds);
        pokerMessage = "PREFLOP";
        pokerRevision++;
        SendPokerState();
        SendPokerHoleCards();
    }

    private void PostPokerBlind(int seat, int amount)
    {
        var player = pokerPlayers.First(p => p.Seat == seat);
        var paid = Math.Min(player.Stack, amount);
        player.Stack -= paid;
        player.Bet += paid;
        player.Invested += paid;
        player.State = amount == PokerSmallBlind ? "SMALL BLIND" : "BIG BLIND";
    }

    private int NextPokerSeat(int after) => pokerPlayers.Where(p => p.Seat > after).OrderBy(p => p.Seat)
        .Concat(pokerPlayers.Where(p => p.Seat <= after).OrderBy(p => p.Seat)).First().Seat;

    private int NextPokerActor(int after) => pokerPlayers.Where(p => p.Seat > after).OrderBy(p => p.Seat)
        .Concat(pokerPlayers.Where(p => p.Seat <= after).OrderBy(p => p.Seat))
        .FirstOrDefault(p => p.InHand && !p.Folded && p.Stack > 0)?.Seat ?? -1;

    private void ActPoker(PokerPlayer player, string action)
    {
        if (pokerPhase != PokerPhase.Playing || pokerActingSeat != player.Seat || player.Folded) return;
        var owed = Math.Max(0, pokerCurrentBet - player.Bet);
        switch (action)
        {
            case "fold": player.Folded = true; player.State = "FOLD"; break;
            case "check" when owed == 0: player.State = "CHECK"; break;
            case "call":
                PayPokerBet(player, Math.Min(owed, player.Stack));
                player.State = player.Stack == 0 ? "ALL IN" : "CALL";
                break;
            case "raise" when !player.Acted && player.Stack >= owed + pokerLastRaise:
                PayPokerBet(player, owed + pokerLastRaise);
                pokerCurrentBet = player.Bet;
                foreach (var other in pokerPlayers.Where(p => p != player && p.InHand && !p.Folded)) other.Acted = false;
                player.State = "RAISE";
                break;
            case "allin" when !player.Acted || player.Stack <= owed:
                var oldBet = pokerCurrentBet;
                PayPokerBet(player, player.Stack);
                if (player.Bet > oldBet)
                {
                    var increase = player.Bet - oldBet;
                    pokerCurrentBet = player.Bet;
                    if (increase >= pokerLastRaise)
                    {
                        pokerLastRaise = increase;
                        foreach (var other in pokerPlayers.Where(p => p != player && p.InHand && !p.Folded)) other.Acted = false;
                    }
                }
                player.State = "ALL IN";
                break;
            default: return;
        }
        player.Acted = true;
        pokerPot = pokerPlayers.Sum(p => p.Invested);
        pokerRevision++;
        AdvancePokerAction();
        SendPokerState();
    }

    private static void PayPokerBet(PokerPlayer player, int amount)
    {
        player.Stack -= amount;
        player.Bet += amount;
        player.Invested += amount;
    }

    private void AdvancePokerAction()
    {
        var live = pokerPlayers.Where(p => p.InHand && !p.Folded).ToArray();
        if (live.Length <= 1) { FinishPokerHand(); return; }
        var pending = live.Where(p => p.Stack > 0 && (!p.Acted || p.Bet < pokerCurrentBet)).ToArray();
        if (pending.Length == 0) { AdvancePokerStreet(); return; }
        var after = pokerActingSeat;
        pokerActionSequence++;
        pokerActingSeat = pending.Where(p => p.Seat > after).OrderBy(p => p.Seat)
            .Concat(pending.Where(p => p.Seat <= after).OrderBy(p => p.Seat)).First().Seat;
        pokerPhaseEndsUtc = DateTime.UtcNow.AddSeconds(PokerTurnSeconds);
    }

    private void AdvancePokerStreet()
    {
        if (pokerStreet == PokerStreet.River) { FinishPokerHand(); return; }
        pokerDeckPosition++; // Burn one card before each board reveal.
        var count = pokerStreet == PokerStreet.Preflop ? 3 : 1;
        for (var i = 0; i < count; i++) pokerBoard.Add(pokerDeck[pokerDeckPosition++]);
        pokerRevealUtc = DateTime.UtcNow;
        pokerStreet++;
        foreach (var player in pokerPlayers) { player.Bet = 0; player.Acted = false; }
        pokerCurrentBet = 0;
        pokerLastRaise = PokerBigBlind;
        pokerMessage = pokerStreet.ToString().ToUpperInvariant();
        pokerActingSeat = -1;
        pokerPhase = PokerPhase.Revealing;
        pokerPhaseEndsUtc = DateTime.UtcNow.AddMilliseconds(600);
    }

    private void StartPokerStreetAction()
    {
        if (pokerPlayers.Count(p => p.InHand && !p.Folded && p.Stack > 0) <= 1)
        {
            AdvancePokerStreet();
            pokerRevision++;
            SendPokerState();
            return;
        }
        pokerActingSeat = NextPokerActor(pokerButton);
        pokerActionSequence++;
        pokerPhase = PokerPhase.Playing;
        pokerPhaseEndsUtc = DateTime.UtcNow.AddSeconds(PokerTurnSeconds);
        pokerRevision++;
        SendPokerState();
    }

    private void FinishPokerHand()
    {
        if (pokerPhase == PokerPhase.Showdown) return;
        var before = pokerPlayers.ToDictionary(p => p.Id, p => p.Stack);
        var contenders = pokerPlayers.Where(p => p.InHand && !p.Folded).ToArray();
        var winners = new HashSet<string>();
        var levels = pokerPlayers.Where(p => p.Invested > 0).Select(p => p.Invested).Distinct().Order().ToArray();
        var previous = 0;
        foreach (var level in levels)
        {
            var eligible = contenders.Where(p => p.Invested >= level).ToArray();
            var amount = (level - previous) * pokerPlayers.Count(p => p.Invested >= level);
            previous = level;
            // Return an unmatched contribution; folded excess must not silently destroy chips.
            var contributors = pokerPlayers.Where(p => p.Invested >= level).ToArray();
            if (contributors.Length == 1 || eligible.Length == 0)
            {
                foreach (var contributor in contributors) contributor.Stack += amount / contributors.Length;
                continue;
            }
            var best = eligible.Max(p => PokerScore(p.First, p.Second, pokerBoard));
            var tied = eligible.Where(p => PokerScore(p.First, p.Second, pokerBoard) == best).OrderBy(p => p.Seat).ToArray();
            foreach (var winner in tied) { winner.Stack += amount / tied.Length; winners.Add(winner.Id); }
            for (var i = 0; i < amount % tied.Length; i++) tied[i].Stack++;
        }
        foreach (var player in pokerPlayers.Where(p => p.Id != PokerBotId && p.InHand))
        {
            var standing = pokerLedger[player.Id];
            player.HandProfit = player.Stack - before[player.Id] - player.Invested;
            pokerLedger[player.Id] = standing with
            {
                Stack = player.Stack,
                Wins = standing.Wins + (winners.Contains(player.Id) ? 1 : 0),
                Net = standing.Net + player.Stack - before[player.Id] - player.Invested,
            };
        }
        foreach (var player in contenders)
            player.State = pokerBoard.Count >= 3
                ? PokerHandLabel(PokerScore(player.First, player.Second, pokerBoard))
                : "WINNER";
        pokerMessage = winners.Count == 0 ? "HAND OVER" :
            string.Join(" + ", pokerPlayers.Where(p => winners.Contains(p.Id)).Select(p => p.Name)) + " WINS";
        pokerPhase = PokerPhase.Showdown;
        pokerActingSeat = -1;
        pokerPhaseEndsUtc = DateTime.UtcNow.AddSeconds(7);
        pokerRevealUtc = DateTime.UtcNow;
        pokerRevision++;
    }

    private static string PokerHandLabel(long score)
    {
        var category = score / 759375;
        if (category == 8 && score / 50625 % 15 == 14) return "ROYAL FLUSH";
        return category switch
        {
            8 => "STRAIGHT FLUSH",
            7 => "FOUR OF A KIND",
            6 => "FULL HOUSE",
            5 => "FLUSH",
            4 => "STRAIGHT",
            3 => "THREE OF A KIND",
            2 => "TWO PAIR",
            1 => "ONE PAIR",
            _ => "HIGH CARD",
        };
    }

    private static long PokerScore(int first, int second, List<int> board)
    {
        if (first < 0 || second < 0) return -1;
        var cards = new[] { first, second }.Concat(board).ToArray();
        if (cards.Length < 5) return 0;
        long best = 0;
        for (var a = 0; a < cards.Length - 4; a++)
        for (var b = a + 1; b < cards.Length - 3; b++)
        for (var c = b + 1; c < cards.Length - 2; c++)
        for (var d = c + 1; d < cards.Length - 1; d++)
        for (var e = d + 1; e < cards.Length; e++)
            best = Math.Max(best, PokerFiveScore([cards[a], cards[b], cards[c], cards[d], cards[e]]));
        return best;
    }

    private static long PokerFiveScore(int[] cards)
    {
        var ranks = cards.Select(c => c % 13 + 2).OrderDescending().ToArray();
        var groups = ranks.GroupBy(r => r).Select(g => (Rank: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count).ThenByDescending(g => g.Rank).ToArray();
        var flush = cards.All(c => c / 13 == cards[0] / 13);
        var unique = ranks.Distinct().ToList();
        if (unique.Contains(14)) unique.Add(1);
        var straight = 0;
        for (var i = 0; i <= unique.Count - 5; i++)
            if (unique[i] - unique[i + 4] == 4) { straight = unique[i]; break; }
        static long Pack(int category, IEnumerable<int> kickers)
        {
            long score = category;
            foreach (var rank in kickers) score = score * 15 + rank;
            return score * (long)Math.Pow(15, Math.Max(0, 5 - kickers.Count()));
        }
        if (flush && straight > 0) return Pack(8, [straight]);
        if (groups[0].Count == 4) return Pack(7, [groups[0].Rank, groups[1].Rank]);
        if (groups[0].Count == 3 && groups[1].Count == 2) return Pack(6, [groups[0].Rank, groups[1].Rank]);
        if (flush) return Pack(5, ranks);
        if (straight > 0) return Pack(4, [straight]);
        if (groups[0].Count == 3) return Pack(3, groups.Select(g => g.Rank));
        if (groups[0].Count == 2 && groups[1].Count == 2)
            return Pack(2, [Math.Max(groups[0].Rank, groups[1].Rank), Math.Min(groups[0].Rank, groups[1].Rank), groups[2].Rank]);
        if (groups[0].Count == 2) return Pack(1, groups.Select(g => g.Rank));
        return Pack(0, ranks);
    }

    private void HandlePokerContribution(string sender, string command)
    {
        var member = stream.Roster.FirstOrDefault(p => p.UserId == sender);
        if (pokerSessionId is null || member is null) return;
        var parts = command.Split('|');
        if (parts.Length < 2 || parts[1] != pokerSessionId) return;
        if (parts[0] == "J" && parts.Length == 3 && int.TryParse(parts[2], out var seat))
        {
            if (seat is >= 0 and < 16)
                AcceptPokerSeat(sender, member.DisplayName, seat);
        }
        else if (parts[0] == "L" && parts.Length == 2 && pokerPlayers.FirstOrDefault(p => p.Id == sender) is { } leaving)
            LeavePokerSeat(leaving);
        else if (parts[0] == "A" && parts.Length == 5 && int.TryParse(parts[2], out var hand) &&
                 int.TryParse(parts[3], out var sequence) && sequence == pokerActionSequence && hand == pokerHand && pokerPlayers.FirstOrDefault(p => p.Id == sender) is { } actor)
            ActPoker(actor, parts[4]);
        else if (parts[0] == "R" && parts.Length == 3 && int.TryParse(parts[2], out var requestedHand) &&
                 requestedHand == pokerHand && pokerPlayers.FirstOrDefault(p => p.Id == sender) is { InHand: true } player)
            SendPokerHoleCards(player);
    }

    private void SendPokerState()
    {
        if (!IsLocalActivityHost || pokerSessionId is null) return;
        UpdateAdditionalArcadePoints();
        foreach (var player in pokerPlayers) RememberPokerStack(player);
        var players = pokerPlayers.Select(p => new PokerPublicPlayer(p.Id, p.Name, p.Seat, p.Stack,
            p.Bet, p.Folded, p.InHand, p.State,
            pokerPhase == PokerPhase.Showdown && p.InHand && !p.Folded ? p.First : -1,
            pokerPhase == PokerPhase.Showdown && p.InHand && !p.Folded ? p.Second : -1, p.Acted, p.Invested, p.HandProfit)).ToArray();
        var snapshot = new PokerSnapshot(pokerSessionId, pokerRevision, pokerHand, pokerPhase,
            pokerStreet, pokerPhaseEndsUtc == DateTime.MaxValue ? 0 :
                new DateTimeOffset(pokerPhaseEndsUtc).ToUnixTimeMilliseconds(),
            pokerButton, pokerActingSeat, pokerCurrentBet, pokerPot, pokerBoard.ToArray(), players, pokerMessage,
            pokerMusicTitle, pokerMusicUrl,
            pokerMusicStartedUtc == DateTime.MinValue ? 0 : new DateTimeOffset(pokerMusicStartedUtc).ToUnixTimeMilliseconds(), pokerStartingChips,
            PokerSmallBlind, PokerBigBlind, PokerTurnSeconds, pokerBotEnabled, pokerPaused, pokerActionSequence, pokerLedger.Values.ToArray(), pokerLastRaise);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot)));
        pokerLastSnapshotUtc = DateTime.UtcNow;
        if (TrySendActivityThroughCloudflare("poker", "S|" + encoded)) return;
        _ = stream.SendArcadeSnapshotAsync(StreamClient.PokerCommandPrefix, "S|" + encoded,
            $"{pokerSessionId}|{pokerHand}|{pokerPhase}|{pokerStreet}|{pokerActingSeat}|{pokerActionSequence}|{pokerPaused}");
    }

    private void SendPokerHoleCards()
    {
        foreach (var player in pokerPlayers.Where(p => p.Id != PokerBotId))
            SendPokerHoleCards(player);
    }

    private void SendPokerHoleCards(PokerPlayer player)
    {
        if (pokerSessionId is null || player.First is < 0 or >= 52 || player.Second is < 0 or >= 52)
            return;
        if (player.Id == LocalPokerUserId)
        {
            pokerLocalFirst = player.First;
            pokerLocalSecond = player.Second;
            pokerLocalCardsHand = pokerHand;
        }
        else if (GameRelay("poker") is null)
            _ = stream.SendPokerCommandAsync($"H|{pokerSessionId}|{pokerHand}|{player.First}|{player.Second}", player.Id);
    }

    private void ApplyPokerCommand(string command)
    {
        if (command.StartsWith("X|", StringComparison.Ordinal))
        {
            if (pokerSessionId is null || command[2..] == pokerSessionId) ResetPokerLocal();
            return;
        }
        if (command.StartsWith("H|", StringComparison.Ordinal))
        {
            var parts = command.Split('|');
            if (parts.Length == 5 && (pokerSessionId is null || parts[1] == pokerSessionId) &&
                int.TryParse(parts[2], out var hand) && hand > 0 && hand >= pokerHand &&
                int.TryParse(parts[3], out var first) && first is >= 0 and < 52 &&
                int.TryParse(parts[4], out var second) && second is >= 0 and < 52 && first != second &&
                (pokerPendingPrivateCards is not { } pending || hand >= pending.Hand))
            {
                pokerPendingPrivateCards = (parts[1], hand, first, second);
                ApplyPendingPokerCards();
            }
            return;
        }
        if (!command.StartsWith("S|", StringComparison.Ordinal)) return;
        try
        {
            var snapshot = JsonSerializer.Deserialize<PokerSnapshot>(
                Encoding.UTF8.GetString(Convert.FromBase64String(command[2..])));
            if (snapshot is null || snapshot.SessionId == pokerSessionId && snapshot.Revision < pokerRevision) return;
            if (snapshot.SessionId != pokerSessionId || snapshot.Hand != pokerHand)
            {
                pokerRevealUtc = DateTime.UtcNow;
                pokerLocalCardsHand = pokerLocalFirst = pokerLocalSecond = -1;
                pokerNextCardRequestUtc = DateTime.MinValue;
            }
            if (snapshot.Board.Length > pokerBoard.Count) pokerRevealUtc = DateTime.UtcNow;
            pokerSessionId = snapshot.SessionId;
            pokerRevision = snapshot.Revision;
            pokerHand = snapshot.Hand;
            pokerPhase = snapshot.Phase;
            pokerStartingChips = snapshot.StartingChips;
            PokerSmallBlind = snapshot.SmallBlind;
            PokerBigBlind = snapshot.BigBlind;
            PokerTurnSeconds = snapshot.TurnSeconds;
            pokerBotEnabled = snapshot.BotEnabled;
            pokerPaused = snapshot.Paused;
            pokerActionSequence = snapshot.ActionSequence;
            pokerLedger.Clear();
            foreach (var standing in snapshot.Ledger ?? []) pokerLedger[standing.Id] = standing;
            pokerStreet = snapshot.Street;
            pokerPhaseEndsUtc = snapshot.EndsUnixMs == 0 ? DateTime.MaxValue :
                DateTimeOffset.FromUnixTimeMilliseconds(snapshot.EndsUnixMs).UtcDateTime;
            pokerButton = snapshot.Button;
            pokerActingSeat = snapshot.ActingSeat;
            pokerCurrentBet = snapshot.CurrentBet;
            pokerLastRaise = snapshot.LastRaise;
            pokerPot = snapshot.Pot;
            pokerMessage = snapshot.Message;
            pokerMusicTitle = snapshot.MusicTitle;
            pokerMusicUrl = snapshot.MusicUrl;
            pokerMusicStartedUtc = snapshot.MusicStartedUnixMs == 0 ? DateTime.MinValue :
                DateTimeOffset.FromUnixTimeMilliseconds(snapshot.MusicStartedUnixMs).UtcDateTime;
            pokerBoard.Clear();
            pokerBoard.AddRange(snapshot.Board);
            pokerPlayers.Clear();
            pokerPlayers.AddRange(snapshot.Players.Select(p => new PokerPlayer { Id = p.Id, Name = p.Name,
                Seat = p.Seat, Stack = p.Stack, Bet = p.Bet, Folded = p.Folded,
                InHand = p.InHand, State = p.State, First = p.First, Second = p.Second, Acted = p.Acted, Invested = p.Invested, HandProfit = p.HandProfit }));
            if (pokerLocalCardsHand != pokerHand) pokerLocalFirst = pokerLocalSecond = -1;
            ApplyPendingPokerCards();
            UpdateAdditionalArcadePoints();
            whiteboardTvDirty = true;
            pokerPageRendered = false;
        }
        catch (Exception) { }
    }

    private void ApplyPendingPokerCards()
    {
        if (pokerPendingPrivateCards is not { } cards || pokerSessionId is null)
            return;
        if (cards.Session != pokerSessionId || cards.Hand < pokerHand)
        {
            pokerPendingPrivateCards = null;
            return;
        }
        if (cards.Hand != pokerHand)
            return;

        pokerLocalCardsHand = cards.Hand;
        pokerLocalFirst = cards.First;
        pokerLocalSecond = cards.Second;
        pokerPendingPrivateCards = null;
        whiteboardTvDirty = true;
        pokerPageRendered = false;
    }
}

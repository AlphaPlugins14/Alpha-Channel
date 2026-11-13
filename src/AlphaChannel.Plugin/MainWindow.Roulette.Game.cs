using System.Text;
using System.Text.Json;
using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private enum RoulettePhase { Inactive, WaitingForBet, Betting, Spinning, Results, BetsClosed }
    private enum RouletteLedgerChangeKind { None, Bet, Win, Loss, BreakEven, Reset, TransferIn, TransferOut, Refund }

    private sealed record RoulettePlayerState(string Id, string Name, int Chips);
    private sealed record RouletteBetState(string PlayerId, string PlayerName, string Target, int Amount);
    private sealed record RouletteWinnerState(string PlayerId, string PlayerName, int Winnings);
    private sealed record RouletteRoundLedgerLine(string Target, int Amount, bool Won, int Payout);
    private sealed record RouletteLedgerPlayerState(
        string PlayerId,
        bool HasJoinedTable,
        string LastBet,
        int LastChange,
        RouletteLedgerChangeKind ChangeKind,
        string ChangeText,
        int DetailRound,
        RouletteRoundLedgerLine[] RoundDetails);
    private sealed record IncompetechTrack(string Title, string Filename, string Genre);
    private sealed record RouletteStateSnapshot(
        string SessionId,
        RoulettePhase Phase,
        long PhaseEndsUnixMs,
        long SpinStartedUnixMs,
        int ResultIndex,
        double WheelStartAngle,
        double BallStartAngle,
        string MusicTitle,
        string MusicUrl,
        long MusicStartedUnixMs,
        int BettingSeconds,
        int StartingChips,
        bool RefillEnabled,
        int Revision,
        RoulettePlayerState[] Players,
        RouletteBetState[] Bets,
        RouletteWinnerState[] Winners,
        string[] RefilledPlayerIds,
        int RoundNumber = 1,
        RouletteLedgerPlayerState[]? Ledger = null,
        bool CroupierMode = false,
        long ResultsStartedUnixMs = 0);

    private const int RouletteDefaultStartingChips = 200;
    private const int RouletteDefaultBettingSeconds = 15;
    private const int RouletteResultsSeconds = 8;
    private static readonly int[] RouletteChipValues = [1, 5, 25, 100, 500];
    private static readonly string[] RouletteWinEmotes = ["/joy", "/cheer", "/thumbsup", "/clap", "/yes"];
    private static readonly string[] RouletteLoseEmotes = ["/panic", "/no", "/angry", "/sulk", "/clutchhead", "/furious", "/fume", "/disappointed"];
    private static readonly string[] RouletteBreakEvenEmotes = ["/huh", "/think", "/consider"];

    private string? rouletteSessionId;
    private string? rouletteWhiteboardSessionId;
    private RoulettePhase roulettePhase;
    private DateTime roulettePhaseEndsUtc = DateTime.MaxValue;
    private DateTime rouletteSpinStartedUtc = DateTime.MinValue;
    private int rouletteRevision;
    private int rouletteBettingSeconds = RouletteDefaultBettingSeconds;
    private int rouletteStartingChips = RouletteDefaultStartingChips;
    private bool rouletteRefillEnabled = true;
    private bool rouletteCroupierMode;
    private bool rouletteCustomCroupierMode;
    private bool pendingRouletteCroupierMode;
    private int rouletteCustomBettingSeconds = RouletteDefaultBettingSeconds;
    private int rouletteCustomStartingChips = RouletteDefaultStartingChips;
    private bool rouletteCustomRefillEnabled = true;
    private int pendingRouletteBettingSeconds = RouletteDefaultBettingSeconds;
    private int pendingRouletteStartingChips = RouletteDefaultStartingChips;
    private bool pendingRouletteRefillEnabled = true;
    private int rouletteSelectedChip = 25;
    private DateTime rouletteNotEnoughChipsUntilUtc = DateTime.MinValue;
    private int rouletteHoveredTarget = -1;
    private string? rouletteHoveredBetTarget;
    private int roulettePageHoveredTarget = -1;
    private string? roulettePageHoveredBetTarget;
    private string? rouletteLastEmoteSessionId;
    private DateTime rouletteLastEmoteSpinStartedUtc = DateTime.MinValue;
    private readonly Dictionary<string, RoulettePlayerState> roulettePlayers = new(StringComparer.Ordinal);
    private readonly List<RouletteBetState> rouletteBets = [];
    private readonly List<RouletteWinnerState> rouletteWinners = [];
    private readonly HashSet<string> rouletteRefilledPlayerIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RouletteLedgerPlayerState> rouletteLedger = new(StringComparer.Ordinal);
    private int rouletteRoundNumber = 1;
    private bool rouletteTransferPopupOpen;
    private string? rouletteTransferRecipientId;
    private int rouletteTransferAmount = 25;
    private string? rouletteTransferError;
    private bool rouletteHelpOpen;
    private readonly RouletteMusicPlayer rouletteMusicPlayer = new();
    private readonly RouletteChipSoundPlayer rouletteChipSoundPlayer = new();
    private readonly RouletteRollSoundPlayer rouletteRollSoundPlayer = new();
    private Task<(int Generation, List<IncompetechTrack> Tracks)>? rouletteMusicLoadTask;
    private readonly List<IncompetechTrack> rouletteMusicPlaylist = [];
    private int rouletteMusicPlaylistIndex;
    private int rouletteMusicGeneration;
    private string rouletteMusicTitle = string.Empty;
    private string rouletteMusicUrl = string.Empty;
    private DateTime rouletteMusicStartedUtc = DateTime.MinValue;
    private string? rouletteMusicFallbackAttemptedUrl;

    internal bool RouletteActive => roulettePhase != RoulettePhase.Inactive && rouletteSessionId is not null;
    internal bool RouletteTvControlsInteractive => RouletteActive;

    private string LocalRouletteUserId => CurrentSession?.AccountId ?? stream.LocalUserId ?? "local";
    private string LocalRouletteDisplayName() => LocalPictionaryDisplayName();

    private void StartRouletteGame()
    {
        if (!IsLocalActivityHost || CurrentSession is null)
            return;
        if (ActiveActivity != ActiveActivityKind.None)
        {
            NotifyActivityStartBlocked();
            Plugin.ChatGui.Print("[AlphaChannel] End the active TV activity before starting roulette.");
            return;
        }

        rouletteSessionId = Guid.NewGuid().ToString("N");
        roulettePhase = RoulettePhase.WaitingForBet;
        roulettePhaseEndsUtc = DateTime.MaxValue;
        rouletteSpinStartedUtc = DateTime.MinValue;
        rouletteResultsStartedUtc = DateTime.MinValue;
        rouletteRevision = 1;
        rouletteBettingSeconds = Math.Clamp(pendingRouletteBettingSeconds, 5, 60);
        rouletteStartingChips = Math.Clamp(pendingRouletteStartingChips, 25, 5000);
        rouletteRefillEnabled = pendingRouletteRefillEnabled;
        rouletteCroupierMode = pendingRouletteCroupierMode;
        rouletteSelectedChip = 25;
        rouletteNotEnoughChipsUntilUtc = DateTime.MinValue;
        roulettePlayers.Clear();
        rouletteBets.Clear();
        rouletteWinners.Clear();
        rouletteRefilledPlayerIds.Clear();
        rouletteLedger.Clear();
        roulettePreviousBets.Clear();
        roulettePreviousBetRound = 0;
        rouletteRoundNumber = 1;
        rouletteHelpOpen = false;
        ResetRouletteSettingsUi();
        ResetRouletteLedgerOverlay();
        CloseRouletteTransferPopup();
        ResetRouletteMusic();
        roulettePlayers[LocalRouletteUserId] =
            new RoulettePlayerState(LocalRouletteUserId, LocalRouletteDisplayName(), rouletteStartingChips);
        EnsureRouletteLedgerPlayer(LocalRouletteUserId);
        AddRouletteRosterPlayers();

        StartHostWhiteboard(WhiteboardMode.Solo, ActiveActivityKind.Roulette);
        rouletteWhiteboardSessionId = whiteboardSessionId;
        roulettePrototypeActive = true;
        roulettePrototypeSettled = true;
        roulettePrototypeLastFrameUtc = DateTime.MinValue;
        whiteboardTvDirty = true;
        SendRouletteState();
        BeginRouletteMusicLoad();
    }

    private void StopRouletteGame()
    {
        if (IsLocalActivityHost && rouletteSessionId is not null)
            _ = stream.SendRouletteCommandAsync("X|" + rouletteSessionId);
        if (IsLocalActivityHost && whiteboardSessionId is not null &&
            string.Equals(whiteboardSessionId, rouletteWhiteboardSessionId, StringComparison.Ordinal))
            SendAndApplyWhiteboardCommand($"X|{whiteboardSessionId}");
        ResetRouletteLocal();
    }

    private void ResetRouletteLocal()
    {
        CaptureArcadeScoresBeforeReset("roulette");
        DisposeActivityRelay("roulette");
        rouletteSessionId = null;
        rouletteWhiteboardSessionId = null;
        roulettePhase = RoulettePhase.Inactive;
        rouletteCroupierMode = false;
        roulettePhaseEndsUtc = DateTime.MaxValue;
        rouletteNotEnoughChipsUntilUtc = DateTime.MinValue;
        rouletteSpinStartedUtc = DateTime.MinValue;
        rouletteResultsStartedUtc = DateTime.MinValue;
        roulettePlayers.Clear();
        rouletteBets.Clear();
        rouletteWinners.Clear();
        rouletteRefilledPlayerIds.Clear();
        rouletteLedger.Clear();
        roulettePreviousBets.Clear();
        roulettePreviousBetRound = 0;
        rouletteRoundNumber = 1;
        rouletteHelpOpen = false;
        ResetRouletteSettingsUi();
        ResetRouletteLedgerOverlay();
        CloseRouletteTransferPopup();
        roulettePrototypeActive = false;
        rouletteHoveredTarget = -1;
        rouletteHoveredBetTarget = null;
        roulettePageHoveredTarget = -1;
        roulettePageHoveredBetTarget = null;
        rouletteLastEmoteSessionId = null;
        rouletteLastEmoteSpinStartedUtc = DateTime.MinValue;
        rouletteRollSoundPlayer.Dispose();
        rouletteChipSoundPlayer.Dispose();
        ResetRouletteMusic();
    }

    private void AddRouletteRosterPlayers()
    {
        foreach (var participant in stream.Roster)
        {
            if (!roulettePlayers.ContainsKey(participant.UserId))
                roulettePlayers[participant.UserId] = new RoulettePlayerState(
                    participant.UserId,
                    string.IsNullOrWhiteSpace(participant.DisplayName) ? "Player" : participant.DisplayName,
                    rouletteStartingChips);
            EnsureRouletteLedgerPlayer(participant.UserId);
        }
    }

    private RouletteLedgerPlayerState EnsureRouletteLedgerPlayer(string playerId)
    {
        if (rouletteLedger.TryGetValue(playerId, out var ledger))
            return ledger;
        ledger = new RouletteLedgerPlayerState(
            playerId, false, string.Empty, 0, RouletteLedgerChangeKind.None,
            string.Empty, 0, []);
        rouletteLedger[playerId] = ledger;
        return ledger;
    }

    private void SendRouletteState()
    {
        if (!IsLocalActivityHost || rouletteSessionId is null)
            return;
        var snapshot = new RouletteStateSnapshot(
            rouletteSessionId, roulettePhase,
            roulettePhaseEndsUtc == DateTime.MaxValue ? 0 : new DateTimeOffset(roulettePhaseEndsUtc).ToUnixTimeMilliseconds(),
            rouletteSpinStartedUtc == DateTime.MinValue ? 0 : new DateTimeOffset(rouletteSpinStartedUtc).ToUnixTimeMilliseconds(),
            roulettePrototypeResultIndex, roulettePrototypeWheelStartAngle, roulettePrototypeBallStartAngle,
            rouletteMusicTitle, rouletteMusicUrl,
            rouletteMusicStartedUtc == DateTime.MinValue ? 0 : new DateTimeOffset(rouletteMusicStartedUtc).ToUnixTimeMilliseconds(),
            rouletteBettingSeconds, rouletteStartingChips, rouletteRefillEnabled,
            rouletteRevision, roulettePlayers.Values.ToArray(), rouletteBets.ToArray(), rouletteWinners.ToArray(),
            rouletteRefilledPlayerIds.ToArray(), rouletteRoundNumber, rouletteLedger.Values.ToArray(), rouletteCroupierMode,
            rouletteResultsStartedUtc == DateTime.MinValue ? 0 : new DateTimeOffset(rouletteResultsStartedUtc).ToUnixTimeMilliseconds());
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot)));
        if (TrySendActivityThroughCloudflare("roulette", "S|" + encoded)) return;
        _ = stream.SendArcadeSnapshotAsync(StreamClient.RouletteCommandPrefix, "S|" + encoded,
            $"{rouletteSessionId}|{rouletteRoundNumber}|{roulettePhase}|{rouletteSpinStartedUtc.Ticks}");
    }

    private void ApplyRouletteCommand(string command)
    {
        if (command.StartsWith("X|", StringComparison.Ordinal))
        {
            if (rouletteSessionId is null || command[2..] == rouletteSessionId)
                ResetRouletteLocal();
            return;
        }
        if (!command.StartsWith("S|", StringComparison.Ordinal))
            return;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(command[2..]));
            var snapshot = JsonSerializer.Deserialize<RouletteStateSnapshot>(json);
            if (snapshot is null || snapshot.Revision < rouletteRevision && snapshot.SessionId == rouletteSessionId)
                return;
            var newSession = !string.Equals(rouletteSessionId, snapshot.SessionId, StringComparison.Ordinal);
            if (newSession) { roulettePreviousBets.Clear(); roulettePreviousBetRound = 0; }
            var chipsClearedForNextRound = !newSession &&
                roulettePhase == RoulettePhase.Results &&
                (snapshot.Phase == RoulettePhase.WaitingForBet || snapshot.CroupierMode && snapshot.Phase == RoulettePhase.Betting) &&
                rouletteBets.Count > 0;
            rouletteSessionId = snapshot.SessionId;
            roulettePhase = snapshot.Phase;
            roulettePhaseEndsUtc = snapshot.PhaseEndsUnixMs == 0
                ? DateTime.MaxValue : DateTimeOffset.FromUnixTimeMilliseconds(snapshot.PhaseEndsUnixMs).UtcDateTime;
            rouletteSpinStartedUtc = snapshot.SpinStartedUnixMs == 0
                ? DateTime.MinValue : DateTimeOffset.FromUnixTimeMilliseconds(snapshot.SpinStartedUnixMs).UtcDateTime;
            roulettePrototypeResultIndex = snapshot.ResultIndex;
            roulettePrototypeWheelStartAngle = snapshot.WheelStartAngle;
            roulettePrototypeBallStartAngle = snapshot.BallStartAngle;
            rouletteMusicTitle = snapshot.MusicTitle;
            rouletteMusicUrl = snapshot.MusicUrl;
            rouletteMusicStartedUtc = snapshot.MusicStartedUnixMs == 0
                ? DateTime.MinValue : DateTimeOffset.FromUnixTimeMilliseconds(snapshot.MusicStartedUnixMs).UtcDateTime;
            rouletteBettingSeconds = snapshot.BettingSeconds > 0
                ? Math.Clamp(snapshot.BettingSeconds, 5, 60)
                : RouletteDefaultBettingSeconds;
            rouletteStartingChips = snapshot.StartingChips > 0
                ? Math.Clamp(snapshot.StartingChips, 25, 5000)
                : RouletteDefaultStartingChips;
            rouletteRefillEnabled = snapshot.RefillEnabled;
            rouletteCroupierMode = snapshot.CroupierMode;
            rouletteResultsStartedUtc = snapshot.ResultsStartedUnixMs == 0 ? DateTime.MinValue
                : DateTimeOffset.FromUnixTimeMilliseconds(snapshot.ResultsStartedUnixMs).UtcDateTime;
            rouletteRevision = snapshot.Revision;
            roulettePlayers.Clear();
            foreach (var player in snapshot.Players) roulettePlayers[player.Id] = player;
            rouletteBets.Clear(); rouletteBets.AddRange(snapshot.Bets);
            rouletteWinners.Clear(); rouletteWinners.AddRange(snapshot.Winners);
            rouletteRefilledPlayerIds.Clear();
            foreach (var playerId in snapshot.RefilledPlayerIds ?? []) rouletteRefilledPlayerIds.Add(playerId);
            rouletteRoundNumber = Math.Max(1, snapshot.RoundNumber);
            if (roulettePhase == RoulettePhase.Results) RememberRouletteRoundBets();
            rouletteLedger.Clear();
            foreach (var ledger in snapshot.Ledger ?? []) rouletteLedger[ledger.PlayerId] = ledger;
            foreach (var player in snapshot.Players) EnsureRouletteLedgerPlayer(player.Id);
            AwardRouletteActivityPoints();
            if (rouletteTransferPopupOpen &&
                (rouletteTransferRecipientId is null || !roulettePlayers.ContainsKey(rouletteTransferRecipientId)))
                CloseRouletteTransferPopup();
            TryPlayRouletteResultEmote();
            roulettePrototypeActive = true;
            roulettePrototypeSettled = roulettePhase != RoulettePhase.Spinning;
            roulettePrototypeLastFrameUtc = DateTime.MinValue;
            whiteboardTvDirty = true;
            SynchronizeRouletteMusic();
            if (Plugin.Cfg.RouletteSoundEffectsEnabled && chipsClearedForNextRound &&
                (stream.Mode != StreamMode.Viewing || ViewerTvEnabled))
                rouletteChipSoundPlayer.Play(Plugin.Cfg.Muted ? 0 : Plugin.Cfg.Volume);
            if (newSession && stream.Mode == StreamMode.Viewing && !ViewerTvEnabled)
                NotifyViewerRouletteStartedWithoutTv();
        }
        catch { }
    }

    private void SubmitRouletteBet(string target)
    {
        if (!LocalRouletteCanBet || rouletteSessionId is null || string.IsNullOrWhiteSpace(target))
            return;
        if (!roulettePlayers.TryGetValue(LocalRouletteUserId, out var localPlayer)) return;
        if (localPlayer.Chips < rouletteSelectedChip)
        {
            rouletteNotEnoughChipsUntilUtc = DateTime.UtcNow.AddSeconds(2.5);
            whiteboardTvDirty = true;
            return;
        }
        rouletteNotEnoughChipsUntilUtc = DateTime.MinValue;
        if (Plugin.Cfg.RouletteSoundEffectsEnabled)
            rouletteChipSoundPlayer.Play(Plugin.Cfg.Muted ? 0 : Plugin.Cfg.Volume);
        var command = $"B|{rouletteSessionId}|{rouletteSelectedChip}|{target}";
        if (IsLocalActivityHost)
            HostAcceptRouletteBet(LocalRouletteUserId, rouletteSelectedChip, target);
        else
            SendArcadePlayerAction("roulette", command);
    }

    private void ClearRouletteBets()
    {
        if (!LocalRouletteCanBet || rouletteSessionId is null)
            return;
        if (IsLocalActivityHost)
            HostClearRouletteBets(LocalRouletteUserId);
        else
            SendArcadePlayerAction("roulette", $"C|{rouletteSessionId}");
    }

    private void HandleRouletteContribution(string senderId, string command)
    {
        var parts = command.Split('|');
        if (parts.Length < 2 || parts[1] != rouletteSessionId ||
            !stream.Roster.Any(player => player.UserId == senderId)) return;
        if (parts[0] == "B" && parts.Length == 4 && int.TryParse(parts[2], out var amount))
            HostAcceptRouletteBet(senderId, amount, parts[3]);
        else if (parts[0] == "C") HostClearRouletteBets(senderId);
        else if (parts[0] == "R" && parts.Length == 3 && int.TryParse(parts[2], out var repeatRound))
            HostRepeatRouletteBet(senderId, repeatRound);
        else if (parts[0] == "T" && parts.Length == 4 && int.TryParse(parts[3], out var transferAmount))
            HostTransferRouletteChips(senderId, parts[2], transferAmount);
    }

    private void HostAcceptRouletteBet(string playerId, int amount, string target)
    {
        if (!CanAcceptRouletteBet(playerId) ||
            !RouletteChipValues.Contains(amount) || !IsValidRouletteTarget(target)) return;
        if (!roulettePlayers.TryGetValue(playerId, out var player))
        {
            var participant = stream.Roster.FirstOrDefault(item => item.UserId == playerId);
            var name = participant is null || string.IsNullOrWhiteSpace(participant.DisplayName) ? "Player" : participant.DisplayName;
            player = new RoulettePlayerState(playerId, name, rouletteStartingChips);
        }
        if (player.Chips < amount) return;
        roulettePlayers[playerId] = player with { Chips = player.Chips - amount };
        var existingIndex = rouletteBets.FindIndex(bet => bet.PlayerId == playerId && bet.Target == target);
        if (existingIndex >= 0)
            rouletteBets[existingIndex] = rouletteBets[existingIndex] with
            {
                Amount = rouletteBets[existingIndex].Amount + amount,
            };
        else
            rouletteBets.Add(new RouletteBetState(playerId, player.Name, target, amount));
        var playerBets = rouletteBets.Where(bet => bet.PlayerId == playerId).ToArray();
        var totalBet = playerBets.Sum(bet => bet.Amount);
        var lastBet = playerBets.Length > 1
            ? $"{totalBet} on multiple"
            : $"{totalBet} on {FormatRouletteTarget(playerBets[0].Target)}";
        var ledger = EnsureRouletteLedgerPlayer(playerId);
        rouletteLedger[playerId] = ledger with
        {
            HasJoinedTable = true,
            LastBet = lastBet,
            LastChange = -amount,
            ChangeKind = RouletteLedgerChangeKind.Bet,
            ChangeText = $"-{amount} bet placed",
        };
        if (roulettePhase == RoulettePhase.WaitingForBet)
        {
            roulettePhase = RoulettePhase.Betting;
            roulettePhaseEndsUtc = DateTime.UtcNow.AddSeconds(rouletteBettingSeconds);
        }
        rouletteRevision++; whiteboardTvDirty = true; SendRouletteState();
    }

    private void HostClearRouletteBets(string playerId)
    {
        if (!CanAcceptRouletteBet(playerId) ||
            !roulettePlayers.TryGetValue(playerId, out var player)) return;
        var refund = rouletteBets.Where(bet => bet.PlayerId == playerId).Sum(bet => bet.Amount);
        if (refund == 0) return;
        rouletteBets.RemoveAll(bet => bet.PlayerId == playerId);
        roulettePlayers[playerId] = player with { Chips = player.Chips + refund };
        var ledger = EnsureRouletteLedgerPlayer(playerId);
        rouletteLedger[playerId] = ledger with
        {
            LastBet = string.Empty,
            LastChange = refund,
            ChangeKind = RouletteLedgerChangeKind.Refund,
            ChangeText = $"+{refund} bets cleared",
        };
        if (rouletteBets.Count == 0 && !rouletteCroupierMode)
        {
            roulettePhase = RoulettePhase.WaitingForBet;
            roulettePhaseEndsUtc = DateTime.MaxValue;
        }
        rouletteRevision++; whiteboardTvDirty = true; SendRouletteState();
    }

    private void HostTransferRouletteChips(string senderId, string recipientId, int amount)
    {
        if (!IsLocalActivityHost || rouletteSessionId is null || amount <= 0 || senderId == recipientId ||
            !roulettePlayers.TryGetValue(senderId, out var sender) ||
            !roulettePlayers.TryGetValue(recipientId, out var recipient) || sender.Chips < amount)
            return;
        var senderLedger = EnsureRouletteLedgerPlayer(senderId);
        var recipientLedger = EnsureRouletteLedgerPlayer(recipientId);
        if (!senderLedger.HasJoinedTable || !recipientLedger.HasJoinedTable)
            return;

        roulettePlayers[senderId] = sender with { Chips = sender.Chips - amount };
        roulettePlayers[recipientId] = recipient with { Chips = recipient.Chips + amount };
        rouletteLedger[senderId] = senderLedger with
        {
            LastChange = -amount,
            ChangeKind = RouletteLedgerChangeKind.TransferOut,
            ChangeText = $"-{amount} sent to {recipient.Name}",
        };
        rouletteLedger[recipientId] = recipientLedger with
        {
            LastChange = amount,
            ChangeKind = RouletteLedgerChangeKind.TransferIn,
            ChangeText = $"+{amount} from {sender.Name}",
        };
        rouletteRevision++;
        SendRouletteState();
    }

    private static string FormatRouletteTarget(string target) => target switch
    {
        "LOW" => "1 - 18",
        "HIGH" => "19 - 36",
        "D1" => "1st 12",
        "D2" => "2nd 12",
        "D3" => "3rd 12",
        "COL1" => "1 - 34",
        "COL2" => "2 - 35",
        "COL3" => "3 - 36",
        _ when target.StartsWith("N:", StringComparison.Ordinal) => target[2..],
        _ => target,
    };

    private void BeginRouletteSpin()
    {
        if (!CanBeginRouletteSpin) return;
        roulettePhase = RoulettePhase.Spinning;
        rouletteSpinStartedUtc = DateTime.UtcNow;
        roulettePhaseEndsUtc = rouletteSpinStartedUtc.AddSeconds(RoulettePrototypeDurationSeconds);
        roulettePrototypeStartedUtc = rouletteSpinStartedUtc;
        roulettePrototypeResultIndex = Random.Shared.Next(RoulettePrototypeNumbers.Length);
        roulettePrototypeWheelStartAngle = Random.Shared.NextDouble() * RoulettePrototypeFullTurn;
        roulettePrototypeBallStartAngle = Random.Shared.NextDouble() * RoulettePrototypeFullTurn;
        roulettePrototypeSettled = false;
        rouletteWinners.Clear();
        rouletteRevision++; whiteboardTvDirty = true; SendRouletteState();
    }

    private void SettleRouletteSpin()
    {
        RememberRouletteRoundBets();
        var number = RoulettePrototypeNumbers[roulettePrototypeResultIndex];
        rouletteRefilledPlayerIds.Clear();
        foreach (var group in rouletteBets.Where(bet => RouletteBetWins(bet.Target, number)).GroupBy(bet => bet.PlayerId))
        {
            var winnings = group.Sum(bet => bet.Amount * RoulettePayoutMultiplier(bet.Target));
            if (!roulettePlayers.TryGetValue(group.Key, out var player)) continue;
            roulettePlayers[group.Key] = player with { Chips = player.Chips + winnings };
            rouletteWinners.Add(new RouletteWinnerState(group.Key, player.Name, winnings));
        }
        if (rouletteRefillEnabled)
        {
            foreach (var playerId in rouletteBets.Select(bet => bet.PlayerId).Distinct(StringComparer.Ordinal))
            {
                if (!roulettePlayers.TryGetValue(playerId, out var player) || player.Chips != 0) continue;
                roulettePlayers[playerId] = player with { Chips = rouletteStartingChips };
                rouletteRefilledPlayerIds.Add(playerId);
            }
        }
        foreach (var playerId in rouletteBets.Select(bet => bet.PlayerId).Distinct(StringComparer.Ordinal))
        {
            var playerBets = rouletteBets.Where(bet => bet.PlayerId == playerId).ToArray();
            var totalBet = playerBets.Sum(bet => bet.Amount);
            var payout = playerBets.Where(bet => RouletteBetWins(bet.Target, number))
                .Sum(bet => bet.Amount * RoulettePayoutMultiplier(bet.Target));
            var details = playerBets.Select(bet => new RouletteRoundLedgerLine(
                FormatRouletteTarget(bet.Target), bet.Amount, RouletteBetWins(bet.Target, number),
                RouletteBetWins(bet.Target, number) ? bet.Amount * RoulettePayoutMultiplier(bet.Target) : 0)).ToArray();
            var ledger = EnsureRouletteLedgerPlayer(playerId);
            if (rouletteRefilledPlayerIds.Contains(playerId))
            {
                rouletteLedger[playerId] = ledger with
                {
                    LastChange = rouletteStartingChips,
                    ChangeKind = RouletteLedgerChangeKind.Reset,
                    ChangeText = $"Chips reset to {rouletteStartingChips}",
                    DetailRound = rouletteRoundNumber,
                    RoundDetails = details,
                };
                continue;
            }

            var net = payout - totalBet;
            rouletteLedger[playerId] = ledger with
            {
                LastChange = net,
                ChangeKind = net > 0 ? RouletteLedgerChangeKind.Win :
                    net < 0 ? RouletteLedgerChangeKind.Loss : RouletteLedgerChangeKind.BreakEven,
                ChangeText = net > 0 ? $"+{net} won" : net < 0 ? $"{net} lost" : "Break even",
                DetailRound = rouletteRoundNumber,
                RoundDetails = details,
            };
        }
        roulettePhase = RoulettePhase.Results;
        rouletteResultsStartedUtc = DateTime.UtcNow;
        roulettePhaseEndsUtc = rouletteCroupierMode ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(RouletteResultsSeconds);
        roulettePrototypeSettled = true;
        AwardRouletteActivityPoints();
        rouletteRevision++;
        TryPlayRouletteResultEmote();
        whiteboardTvDirty = true;
        SendRouletteState();
    }

    private void TryPlayRouletteResultEmote()
    {
        if (roulettePhase != RoulettePhase.Results || rouletteSessionId is null ||
            rouletteSpinStartedUtc == DateTime.MinValue ||
            string.Equals(rouletteLastEmoteSessionId, rouletteSessionId, StringComparison.Ordinal) &&
            rouletteLastEmoteSpinStartedUtc == rouletteSpinStartedUtc)
            return;

        rouletteLastEmoteSessionId = rouletteSessionId;
        rouletteLastEmoteSpinStartedUtc = rouletteSpinStartedUtc;
        if (!Plugin.Cfg.RouletteEmotesEnabled) return;

        var localUserId = LocalRouletteUserId;
        var totalBet = rouletteBets
            .Where(bet => bet.PlayerId == localUserId)
            .Sum(bet => bet.Amount);
        if (totalBet <= 0)
            return;

        var totalPayout = rouletteWinners
            .Where(winner => winner.PlayerId == localUserId)
            .Sum(winner => winner.Winnings);
        var emotes = totalPayout > totalBet
            ? RouletteWinEmotes
            : totalPayout < totalBet
                ? RouletteLoseEmotes
                : RouletteBreakEvenEmotes;

        try
        {
            Plugin.ExecuteChatCommand(emotes[Random.Shared.Next(emotes.Length)] + " motion");
        }
        catch (Exception)
        {
            AepLog.Warning("[Roulette] Could not play the result emote.");
        }
    }

    private void BeginRouletteWaitingRound(bool announce = true)
    {
        var hadBets = rouletteBets.Count > 0;
        rouletteBets.Clear(); rouletteWinners.Clear(); rouletteRefilledPlayerIds.Clear();
        if (hadBets || roulettePhase == RoulettePhase.Results)
            rouletteRoundNumber++;
        roulettePhase = RoulettePhase.WaitingForBet;
        roulettePhaseEndsUtc = DateTime.MaxValue;
        if (hadBets && Plugin.Cfg.RouletteSoundEffectsEnabled)
            rouletteChipSoundPlayer.Play(Plugin.Cfg.Muted ? 0 : Plugin.Cfg.Volume);
        rouletteRevision++; whiteboardTvDirty = true;
        if (announce) SendRouletteState();
    }

    private static bool IsValidRouletteTarget(string target) =>
        target is "RED" or "BLACK" or "ODD" or "EVEN" or "LOW" or "HIGH" or
            "D1" or "D2" or "D3" or "COL1" or "COL2" or "COL3" ||
        target.StartsWith("N:", StringComparison.Ordinal) && int.TryParse(target[2..], out var number) && number is >= 0 and <= 36;

    private static bool RouletteBetWins(string target, int number)
    {
        if (target.StartsWith("N:", StringComparison.Ordinal))
            return int.TryParse(target[2..], out var selected) && selected == number;
        if (number == 0) return false;
        return target switch
        {
            "RED" => RoulettePrototypeRedNumbers.Contains(number), "BLACK" => !RoulettePrototypeRedNumbers.Contains(number),
            "ODD" => number % 2 == 1, "EVEN" => number % 2 == 0, "LOW" => number <= 18, "HIGH" => number >= 19,
            "D1" => number <= 12, "D2" => number is >= 13 and <= 24, "D3" => number >= 25,
            "COL1" => number % 3 == 1, "COL2" => number % 3 == 2, "COL3" => number % 3 == 0, _ => false,
        };
    }

    private static int RoulettePayoutMultiplier(string target) => target.StartsWith("N:", StringComparison.Ordinal) ? 36 :
        target.StartsWith("D", StringComparison.Ordinal) || target.StartsWith("COL", StringComparison.Ordinal) ? 3 : 2;

    internal bool IsRouletteTvControlAt(Vector2 uv) => TryGetRouletteTvTarget(uv, out _, out _);
    internal void SetRouletteTvHover(Vector2? uv)
    {
        SetRouletteHover(uv, ref rouletteHoveredTarget, ref rouletteHoveredBetTarget);
    }

    private void SetRoulettePageHover(Vector2? uv)
    {
        SetRouletteHover(uv, ref roulettePageHoveredTarget, ref roulettePageHoveredBetTarget);
    }

    private void SetRouletteHover(Vector2? uv, ref int hoveredTarget, ref string? hoveredBetTarget)
    {
        var hover = -1;
        string? betTarget = null;
        if (uv is { } point && TryGetRouletteTvTarget(point, out var hitTarget, out var hitBetTarget))
        {
            hover = hitTarget;
            betTarget = hitBetTarget;
        }

        if (hover != hoveredTarget || !string.Equals(betTarget, hoveredBetTarget, StringComparison.Ordinal))
        {
            hoveredTarget = hover;
            hoveredBetTarget = betTarget;
            whiteboardTvDirty = true;
        }
    }
    internal void SubmitRouletteTvControlAt(Vector2 uv)
    {
        if (!TryGetRouletteTvTarget(uv, out var target, out var betTarget)) return;
        if (target == 106)
        {
            rouletteHelpOpen = !rouletteHelpOpen;
            if (rouletteHelpOpen) { rouletteSettingsOpen = false; rouletteLedgerOverlayOpen = false; }
        }
        else if (target == 131) ToggleRouletteCroupierBets();
        else if (target == 132) BeginRouletteSpin();
        else if (target is >= 114 and <= 130) HandleRouletteLedgerOverlayTarget(target);
        else if (target is >= 108 and <= 113) HandleRouletteSettingsTarget(target, uv);
        else if (target is >= 100 and < 105) rouletteSelectedChip = RouletteChipValues[target - 100];
        else if (target == 105) ClearRouletteBets();
        else if (target == 107) RepeatRouletteBet();
        else if (betTarget is not null) SubmitRouletteBet(betTarget);
        whiteboardTvDirty = true;
    }

    private bool TryGetRouletteTvTarget(Vector2 uv, out int target, out string? betTarget)
    {
        target = -1; betTarget = null;
        var x = uv.X * WhiteboardRenderer.Width; var y = uv.Y * WhiteboardRenderer.Height;
        if (x is >= 1218 and <= 1266 && y is >= 10 and <= 60) { target = 106; return true; }
        if (x is >= 1162 and < 1210 && y is >= 10 and < 58) { target = 108; return true; }
        if (x is >= 606 and < 774 && y is >= 14 and < 56) { target = 114; return true; }
        if (rouletteLedgerOverlayOpen)
            return TryGetRouletteLedgerOverlayTarget(x, y, out target);
        if (rouletteSettingsOpen)
            return TryGetRouletteSettingsTarget(x, y, out target);
        if (rouletteHelpOpen || RouletteResultsScreenVisible(DateTime.UtcNow)) return false;
        if (rouletteCroupierMode && IsLocalActivityHost)
        {
            if (roulettePhase is RoulettePhase.WaitingForBet or RoulettePhase.Betting or RoulettePhase.Results &&
                x is >= 622 and <= 1238 && y is >= 92 and <= 156)
            { target = 131; return true; }
            if (roulettePhase == RoulettePhase.BetsClosed &&
                Vector2.Distance(new Vector2(x, y), new Vector2(300, 350)) <= 270)
            { target = 132; return true; }
            return false;
        }
        if (!LocalRouletteCanBet) return false;
        if (CanRepeatRouletteBet(LocalRouletteUserId) && x is >= 618 and < 730 && y is >= 580 and < 604)
        { target = 107; return true; }
        if (y is >= 536 and <= 590)
        {
            for (var i = 0; i < 5; i++) if (Vector2.Distance(new Vector2(x, y), new Vector2(780 + i * 68, 565)) <= 34) { target = 100 + i; return true; }
            if (x is >= 1132 and <= 1242) { target = 105; return true; }
        }
        if (y is >= 182 and < 356)
        {
            var row = (int)((y - 182) / 58); target = row * 20;
            if (x is >= 610 and < 662) { betTarget = "N:0"; return true; }
            if (x is >= 662 and < 1190)
            {
                var column = (int)((x - 662) / 44); var value = (column + 1) * 3 - row;
                target += column + 1; betTarget = "N:" + value; return true;
            }
            if (x is >= 1190 and <= 1250) { betTarget = "COL" + (row == 0 ? 3 : row == 1 ? 2 : 1); target += 18; return true; }
        }
        if (x >= 662 && x < 1190 && y >= 356 && y < 404)
        { var i = Math.Min(2, (int)((x - 662) / 176)); target = 60 + i; betTarget = "D" + (i + 1); return true; }
        if (x >= 662 && x < 1190 && y >= 404 && y < 448)
        { var i = Math.Min(5, (int)((x - 662) / 88)); string[] values = ["LOW", "EVEN", "RED", "BLACK", "ODD", "HIGH"]; target = 70 + i; betTarget = values[i]; return true; }
        return false;
    }

    private void ApplyRouletteTvOverlay(byte[] pixels, int width, int height)
    {
        var source = BuildRoulettePrototypePixels(DateTime.UtcNow);
        Buffer.BlockCopy(source, 0, pixels, 0, Math.Min(source.Length, pixels.Length));
    }

    private void BeginRouletteMusicLoad()
    {
        var generation = ++rouletteMusicGeneration;
        rouletteMusicLoadTask = Task.Run(async () =>
        {
            try
            {
                using var http = Net.PluginHttpClients.CreateMetadataClient();
                var json = await http.GetStringAsync(
                    "https://incompetech.com/music/royalty-free/pieces.json").ConfigureAwait(false);
                var tracks = JsonSerializer.Deserialize<List<IncompetechTrack>>(
                    json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                return (generation, tracks
                    .Where(track => !string.IsNullOrWhiteSpace(track.Filename) &&
                                    track.Genre.Split(',', StringSplitOptions.TrimEntries).Contains("11"))
                    .Select(track => track with { Title = track.Title.Trim(), Filename = track.Filename.Trim() })
                    .ToList());
            }
            catch (Exception)
            {
                AepLog.Warning("[Roulette Music] Could not load the Incompetech catalogue.");
                return (generation, new List<IncompetechTrack>());
            }
        });
    }

    private void UpdateRouletteMusic()
    {
        rouletteMusicPlayer.SetVolume(RouletteMusicVolume);
        if (stream.Mode == StreamMode.Viewing)
        {
            SynchronizeRouletteMusic();
            return;
        }
        if (!IsLocalActivityHost) return;
        if (rouletteMusicLoadTask is { IsCompletedSuccessfully: true } completed)
        {
            rouletteMusicLoadTask = null;
            var result = completed.Result;
            if (result.Generation == rouletteMusicGeneration && result.Tracks.Count > 0)
            {
                rouletteMusicPlaylist.Clear();
                rouletteMusicPlaylist.AddRange(result.Tracks.OrderBy(_ => Random.Shared.Next()));
                rouletteMusicPlaylistIndex = 0;
                PlayNextRouletteMusicTrack();
            }
        }
        else if (rouletteMusicLoadTask is { IsFaulted: true })
        {
            rouletteMusicLoadTask = null;
        }

        if (rouletteMusicPlaylist.Count > 0 && rouletteMusicPlayer.Finished)
            PlayNextRouletteMusicTrack();
    }

    private void PlayNextRouletteMusicTrack()
    {
        if (rouletteMusicPlaylist.Count == 0 || !RouletteActive) return;
        if (rouletteMusicPlaylistIndex >= rouletteMusicPlaylist.Count)
        {
            var shuffled = rouletteMusicPlaylist.OrderBy(_ => Random.Shared.Next()).ToArray();
            rouletteMusicPlaylist.Clear();
            rouletteMusicPlaylist.AddRange(shuffled);
            rouletteMusicPlaylistIndex = 0;
        }
        var track = rouletteMusicPlaylist[rouletteMusicPlaylistIndex++];
        rouletteMusicTitle = track.Title;
        rouletteMusicUrl = "https://incompetech.com/music/royalty-free/mp3-royaltyfree/" + Uri.EscapeDataString(track.Filename);
        rouletteMusicStartedUtc = DateTime.UtcNow;
        rouletteMusicPlayer.Play(rouletteMusicUrl, 0d, RouletteMusicVolume);
        rouletteRevision++;
        SendRouletteState();
    }

    private void SynchronizeRouletteMusic()
    {
        if (string.IsNullOrWhiteSpace(rouletteMusicUrl))
        {
            rouletteMusicPlayer.Stop();
            return;
        }
        if (string.Equals(rouletteMusicPlayer.CurrentUrl, rouletteMusicUrl, StringComparison.Ordinal))
        {
            if (!rouletteMusicPlayer.Failed ||
                string.Equals(rouletteMusicFallbackAttemptedUrl, rouletteMusicUrl, StringComparison.Ordinal))
                return;

            // Some remote MP3 servers reject or mishandle a mid-file start. Retry this
            // track once from its beginning so a viewer still gets music.
            rouletteMusicFallbackAttemptedUrl = rouletteMusicUrl;
            rouletteMusicPlayer.Stop();
            rouletteMusicPlayer.Play(rouletteMusicUrl, 0d, RouletteMusicVolume);
            return;
        }

        rouletteMusicFallbackAttemptedUrl = null;
        var offset = rouletteMusicStartedUtc == DateTime.MinValue
            ? 0d : Math.Max(0d, (DateTime.UtcNow - rouletteMusicStartedUtc).TotalSeconds);
        rouletteMusicPlayer.Play(rouletteMusicUrl, offset, RouletteMusicVolume);
    }

    private void ResetRouletteMusic()
    {
        rouletteMusicGeneration++;
        rouletteMusicLoadTask = null;
        rouletteMusicPlaylist.Clear();
        rouletteMusicPlaylistIndex = 0;
        rouletteMusicTitle = string.Empty;
        rouletteMusicUrl = string.Empty;
        rouletteMusicStartedUtc = DateTime.MinValue;
        rouletteMusicFallbackAttemptedUrl = null;
        rouletteMusicPlayer.Stop();
    }
}

using System.Text;
using System.Text.Json;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

// Bingo's rules, local card, party snapshots, TV input and TV artwork live here.
internal sealed partial class MainWindow
{
    private bool bingoManual, bingoDraftManual;
    private IReadOnlyCollection<int> BingoLocalMarks => bingoManual ? LocalBingoCard?.Marks ?? [] : bingoCalled;
    private bool CanClaimBingo => bingoManual && bingoPhase == BingoPhase.Drawing && LocalBingoCard is { } card && BingoRules.CanClaim(card.Numbers, bingoCalled, card.Marks, CurrentBingoPattern);
    private const int BingoCountdownSeconds = 30;
    private const int BingoResultsSeconds = 9;
    private readonly List<int> bingoCalled = [];
    private readonly Dictionary<string, BingoCard> bingoCards = new(StringComparer.Ordinal);
    private readonly List<string> bingoWinnerIds = [];
    private readonly Dictionary<string, BingoStanding> bingoStandings = new(StringComparer.Ordinal);
    private BingoParticipant[] bingoParticipants = [];
    private bool bingoHostAuthority;
    private int BingoLockedCardCount => IsLocalActivityHost ? bingoCards.Count : bingoParticipants.Length;
    private string BingoPlayerName(string id) => bingoCards.GetValueOrDefault(id)?.Name ??
        bingoParticipants.FirstOrDefault(p => p.PlayerId == id)?.Name ?? bingoStandings.GetValueOrDefault(id)?.Name ?? "Player";
    private BingoPattern[] bingoPatterns = (BingoPattern[])BingoRules.Rotation.Clone();
    private int bingoRoundCount = 7;
    private int bingoDrawSeconds = 5;
    private bool bingoCustomRules;
    private int bingoCustomRounds = 7;
    private int bingoCustomDrawSeconds = 5;
    private int bingoCustomPattern;
    private BingoPattern CurrentBingoPattern => bingoPatterns[(Math.Max(1, bingoRound) - 1) % bingoPatterns.Length];
    private string BingoRoundLabel => bingoRoundCount > 0 ? $"{bingoRound}/{bingoRoundCount}" : bingoRound.ToString();
    private int[] bingoLocalNumbers = [];
    private string? bingoSessionId;
    private string? bingoWhiteboardSessionId;
    private BingoPhase bingoPhase;
    private int bingoRevision;
    private int bingoRound = 1;
    private DateTime bingoPhaseEndsUtc = DateTime.MaxValue;
    private DateTime bingoLastDrawUtc = DateTime.MinValue;
    private DateTime bingoLastFrameUtc = DateTime.MinValue;
    private DateTime bingoLastSnapshotUtc = DateTime.MinValue;
    private int bingoTvHoveredTarget = -1;
    private int bingoPageHoveredTarget = -1;
    private bool bingoEditingCard;
    private int bingoEditingIndex = -1;
    private string bingoNumberInput = string.Empty;
    private string bingoInputError = string.Empty;
    private DateTime bingoInputErrorUntilUtc = DateTime.MinValue;
    private bool bingoHelpOpen;
    private bool bingoSettingsOpen;
    private bool bingoPageVolumeDragging;
    private bool bingoTvVolumeDragging;
    private string? bingoLastEmoteSessionId;
    private int bingoLastEmoteRound;
    private WhiteboardRenderer? bingoPageRenderer;
    private readonly byte[] bingoFramePixels = new byte[WhiteboardRenderer.Width * WhiteboardRenderer.Height * 4];

    internal bool BingoActive => bingoSessionId is not null && bingoPhase != BingoPhase.Inactive;
    internal bool BingoTvControlsInteractive => BingoActive;
    private string LocalBingoUserId => CurrentSession?.AccountId ?? stream.LocalUserId ?? "local";
    private BingoCard? LocalBingoCard => bingoCards.GetValueOrDefault(LocalBingoUserId);
    private int[] BingoVisibleCard => LocalBingoCard?.Numbers ?? bingoLocalNumbers;
    internal bool BingoNumberInputActive => BingoActive && bingoEditingIndex >= 0 &&
        bingoEditingCard && LocalBingoCard is null &&
        bingoPhase is BingoPhase.Waiting or BingoPhase.Countdown;
    internal string BingoNumberInput => bingoNumberInput;
    private int BingoSecondsLeft => bingoPhaseEndsUtc == DateTime.MaxValue
        ? 0 : Math.Max(0, (int)Math.Ceiling((bingoPhaseEndsUtc - DateTime.UtcNow).TotalSeconds));

    private void OpenBingoPage()
    {
        if (!CanLaunchActivity(ActiveActivityKind.Bingo)) return;
        currentPage = HomePage.Bingo;
        lastActivitiesPage = HomePage.Bingo;
    }

    private void StartBingoGame()
    {
        if (!IsLocalActivityHost || CurrentSession is null ||
            ActiveActivity != ActiveActivityKind.None) return;
        bingoManual = bingoCustomRules && bingoDraftManual;
        bingoPatterns = bingoCustomRules && bingoCustomPattern > 0
            ? [BingoRules.Rotation[Math.Clamp(bingoCustomPattern - 1, 0, BingoRules.Rotation.Length - 1)]]
            : (BingoPattern[])BingoRules.Rotation.Clone();
        bingoRoundCount = bingoCustomRules ? Math.Clamp(bingoCustomRounds, 1, 14) : 7;
        bingoDrawSeconds = bingoCustomRules ? Math.Clamp(bingoCustomDrawSeconds, 2, 10) : 5;
        bingoStandings.Clear();
        bingoNumberInput = bingoInputError = string.Empty;
        bingoSessionId = Guid.NewGuid().ToString("N");
        bingoHostAuthority = true;
        bingoPhase = BingoPhase.Waiting;
        bingoRound = 1;
        bingoRevision = 1;
        bingoPhaseEndsUtc = DateTime.MaxValue;
        bingoLastDrawUtc = DateTime.MinValue;
        bingoCalled.Clear();
        bingoCards.Clear();
        bingoWinnerIds.Clear();
        bingoLocalNumbers = GenerateBingoCard();
        bingoEditingCard = false;
        bingoEditingIndex = -1;
        bingoHelpOpen = bingoSettingsOpen = false;
        StartHostWhiteboard(WhiteboardMode.Solo, ActiveActivityKind.Bingo);
        bingoWhiteboardSessionId = whiteboardSessionId;
        whiteboardTvDirty = true;
        SendBingoState();
    }

    private void StopBingoGame()
    {
        if (IsLocalActivityHost && bingoSessionId is not null)
            _ = stream.SendBingoCommandAsync("X|" + bingoSessionId);
        if (IsLocalActivityHost && whiteboardSessionId is not null &&
            string.Equals(whiteboardSessionId, bingoWhiteboardSessionId, StringComparison.Ordinal))
            SendAndApplyWhiteboardCommand($"X|{whiteboardSessionId}");
        ResetBingoLocal();
    }

    private void ResetBingoLocal()
    {
        RetireArcadePageRenderer(ref bingoPageRenderer);
        DisposeBingoRelay();
        bingoHostAuthority = false;
        bingoParticipants = [];
        bingoSessionId = null;
        bingoWhiteboardSessionId = null;
        bingoPhase = BingoPhase.Inactive;
        bingoStandings.Clear();
        bingoPhaseEndsUtc = DateTime.MaxValue;
        bingoLastDrawUtc = DateTime.MinValue;
        bingoCalled.Clear();
        bingoCards.Clear();
        bingoWinnerIds.Clear();
        bingoLocalNumbers = [];
        bingoEditingCard = false;
        bingoEditingIndex = -1;
        bingoNumberInput = string.Empty;
        bingoInputError = string.Empty;
        bingoInputErrorUntilUtc = DateTime.MinValue;
        bingoHelpOpen = bingoSettingsOpen = false;
        bingoPageVolumeDragging = bingoTvVolumeDragging = false;
        bingoLastEmoteSessionId = null;
        bingoLastEmoteRound = 0;
        bingoTvHoveredTarget = -1;
        bingoPageHoveredTarget = -1;
        whiteboardTvDirty = true;
    }

    internal void UpdateBingo()
    {
        if (stream.Mode == StreamMode.None)
        {
            stream.IncomingBingoCommands.Clear();
        }
        while (stream.IncomingBingoCommands.TryDequeue(out var incoming))
        {
            if (IsLocalActivityHost)
            {
                // New Cloudflare sessions accept card locks exclusively via
                // the account-bound action path. Legacy hosts use the server.
                if (bingoRelay is null) HandleBingoContribution(incoming.UserId, incoming.Command);
            }
            else if (incoming.Command.StartsWith("R|", StringComparison.Ordinal)) ReceiveBingoRelayInvitation(incoming.Command);
            else
            {
                if (incoming.Command.StartsWith("S|", StringComparison.Ordinal)) DisposeBingoRelay();
                ApplyBingoCommand(incoming.Command);
            }
        }
        UpdateBingoRelay();
        if (!BingoActive) return;
        if (IsLocalActivityHost)
        {
            if (!bingoHostAuthority) { StopBingoGame(); return; }
            if (!whiteboardActive || !string.Equals(whiteboardSessionId, bingoWhiteboardSessionId, StringComparison.Ordinal))
            {
                StopBingoGame();
                return;
            }
            var now = DateTime.UtcNow;
            ShortenBingoCountdown(now);
            if (bingoPhase == BingoPhase.Countdown && now >= bingoPhaseEndsUtc)
            {
                bingoPhase = BingoPhase.Drawing;
                bingoEditingIndex = -1;
                bingoNumberInput = string.Empty;
                bingoPhaseEndsUtc = now.AddSeconds(1);
                bingoRevision++;
                SendBingoState();
            }
            else if (bingoPhase == BingoPhase.Drawing && now >= bingoPhaseEndsUtc)
                DrawNextBingoBall(now);
            else if (bingoPhase == BingoPhase.Results && now >= bingoPhaseEndsUtc)
                BeginNextBingoRound();
            if (now - bingoLastSnapshotUtc >= TimeSpan.FromSeconds(3))
                SendBingoState();
        }
        // The ball drop, pulsing highlights and countdown render locally on both surfaces.
        var frameNow = DateTime.UtcNow;
        if (frameNow - bingoLastFrameUtc >= TimeSpan.FromMilliseconds(55))
        {
            bingoLastFrameUtc = frameNow;
            var showPage = IsOpen && currentPage == HomePage.Bingo;
            var showTv = ShouldRenderArcadeTv;
            if (!showPage && !showTv) return;
            var frameStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var pixels = BuildBingoPixels();
            PlaybackRenderDiagnostics.RecordArcadeRender("Bingo",
                System.Diagnostics.Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds);
            if (showPage)
            {
                if (bingoPageRenderer is null && !screenController.Engine.TryInitializeGraphics()) return;
                bingoPageRenderer ??= DxHandler.RunOnRenderThreadAndWait(
                    "AlphaChannel.BingoPage.Create",
                    () => new WhiteboardRenderer("AlphaChannel.BingoPage"),
                    TimeSpan.FromSeconds(30));
                bingoPageRenderer.Upload(pixels);
            }
            if (showTv) screenController.Engine.UpdateWhiteboard(pixels);
            whiteboardTvDirty = false;
        }
    }

    private byte[] BuildBingoPixels()
    {
        var width = WhiteboardRenderer.Width;
        var height = WhiteboardRenderer.Height;
        var pixels = bingoFramePixels;
        ApplyBingoTvOverlay(pixels, width, height);
        return pixels;
    }

    private void DrawNextBingoBall(DateTime now)
    {
        var remaining = Enumerable.Range(1, 75).Except(bingoCalled).ToArray();
        if (remaining.Length == 0 && bingoManual) return;
        if (remaining.Length == 0)
        {
            bingoPhase = BingoPhase.Results;
            bingoPhaseEndsUtc = now.AddSeconds(BingoResultsSeconds);
        }
        else
        {
            bingoCalled.Add(remaining[Random.Shared.Next(remaining.Length)]);
            bingoLastDrawUtc = now;
            bingoWinnerIds.Clear();
            foreach (var card in bingoCards.Values)
                if (!bingoManual && BingoRules.Wins(card.Numbers, bingoCalled, CurrentBingoPattern))
                    bingoWinnerIds.Add(card.PlayerId);
            if (bingoWinnerIds.Count > 0)
            {
                foreach (var id in bingoWinnerIds)
                {
                    var standing = bingoStandings[id];
                    bingoStandings[id] = standing with { Wins = standing.Wins + 1 };
                }
                bingoPhase = BingoPhase.Results;
                bingoPhaseEndsUtc = now.AddSeconds(BingoResultsSeconds);
            }
            else
                bingoPhaseEndsUtc = bingoManual && bingoCalled.Count == 75 ? DateTime.MaxValue : now.AddSeconds(bingoDrawSeconds);
        }
        bingoRevision++;
        whiteboardTvDirty = true;
        TryPlayBingoResultEmote();
        SendBingoState();
    }

    private void TryPlayBingoResultEmote()
    {
        if (bingoPhase != BingoPhase.Results || bingoSessionId is null ||
            bingoLastEmoteSessionId == bingoSessionId && bingoLastEmoteRound == bingoRound)
            return;
        bingoLastEmoteSessionId = bingoSessionId;
        bingoLastEmoteRound = bingoRound;
        if (!Plugin.Cfg.BingoEmotesEnabled || LocalBingoCard is null) return;
        var emotes = bingoWinnerIds.Contains(LocalBingoUserId)
            ? RouletteWinEmotes : RouletteLoseEmotes;
        try
        {
            Plugin.ExecuteChatCommand(emotes[Random.Shared.Next(emotes.Length)] + " motion");
        }
        catch (Exception)
        {
            AepLog.Warning("[Bingo] Could not play the result emote.");
        }
    }

    private void BeginNextBingoRound()
    {
        if (bingoRoundCount > 0 && bingoRound >= bingoRoundCount)
        {
            bingoPhase = BingoPhase.Finished;
            bingoPhaseEndsUtc = DateTime.MaxValue;
            bingoRevision++;
            whiteboardTvDirty = true;
            SendBingoState();
            return;
        }
        bingoInputError = string.Empty;
        bingoRound++;
        bingoPhase = BingoPhase.Waiting;
        bingoPhaseEndsUtc = DateTime.MaxValue;
        bingoLastDrawUtc = DateTime.MinValue;
        bingoCalled.Clear();
        bingoCards.Clear();
        bingoWinnerIds.Clear();
        bingoLocalNumbers = GenerateBingoCard();
        bingoEditingCard = false;
        bingoEditingIndex = -1;
        bingoNumberInput = string.Empty;
        bingoRevision++;
        whiteboardTvDirty = true;
        SendBingoState();
    }

    private static int[] GenerateBingoCard()
    {
        var card = new int[25];
        for (var column = 0; column < 5; column++)
        {
            var pool = Enumerable.Range(column * 15 + 1, 15)
                .OrderBy(_ => Random.Shared.Next()).Take(5).ToArray();
            for (var row = 0; row < 5; row++)
                card[row * 5 + column] = row == 2 && column == 2 ? 0 : pool[row];
        }
        return card;
    }

    private static bool ValidBingoCard(int[]? card) => BingoRules.ValidCard(card);
    private HashSet<int> WinningBingoCells(int[] card, IReadOnlyCollection<int> called) =>
        BingoRules.WinningCells(card, called, CurrentBingoPattern);

    private string BuildBingoAnnouncement()
    {
        if (bingoPhase == BingoPhase.Finished) return "GAME COMPLETE - WELL PLAYED!";
        if (bingoPhase == BingoPhase.Results)
        {
            if (bingoWinnerIds.Count == 0) return "ROUND FINISHED";
            var first = BingoPlayerName(bingoWinnerIds[0]);
            return bingoWinnerIds.Count == 1
                ? $"{BingoShortName(first)} GOT BINGO!"
                : $"{bingoWinnerIds.Count} PLAYERS GOT BINGO!";
        }
        if (bingoPhase == BingoPhase.Waiting) return "FIRST LOCK STARTS THE CLOCK";
        if (bingoPhase == BingoPhase.Countdown)
            return $"{BingoLockedCardCount} CARDS LOCKED - GET READY!";
        if (BingoLockedCardCount == 0) return "NEXT ROUND STARTS SOON";

        var progress = BingoRules.Progress(BingoVisibleCard, BingoLocalMarks, CurrentBingoPattern);
        return CurrentBingoPattern is BingoPattern.SingleLine or BingoPattern.TwoLines or BingoPattern.ThreeLines
            ? $"{progress.Complete}/{progress.Required} LINES COMPLETED"
            : $"{progress.Complete}/{progress.Required} PATTERN CELLS MARKED";
    }

    private static string BingoShortName(string name)
    {
        var value = (string.IsNullOrWhiteSpace(name) ? "PLAYER" : name.Trim()).ToUpperInvariant();
        return value.Length > 12 ? value[..12] : value;
    }

    private void BeginBingoCellEdit(int index)
    {
        if (!BingoActive || bingoPhase is not (BingoPhase.Waiting or BingoPhase.Countdown) ||
            LocalBingoCard is not null || !bingoEditingCard || index is < 0 or >= 25 || index == 12)
            return;
        if (BingoNumberInputActive && index != bingoEditingIndex)
        {
            FinishBingoNumberInput(true);
            if (BingoNumberInputActive) return;
        }
        bingoEditingIndex = index;
        bingoNumberInput = string.Empty;
        bingoInputError = string.Empty;
        whiteboardTvDirty = true;
    }

    internal void SetBingoNumberInput(string value)
    {
        if (!BingoNumberInputActive) return;
        var cleaned = new string(value.Where(char.IsDigit).Take(2).ToArray());
        if (cleaned == bingoNumberInput) return;
        bingoNumberInput = cleaned;
        bingoInputError = string.Empty;
        whiteboardTvDirty = true;
    }

    internal void FinishBingoNumberInput(bool submit)
    {
        if (!BingoNumberInputActive) return;
        if (!submit || bingoNumberInput.Length == 0)
        {
            bingoEditingIndex = -1;
            bingoNumberInput = string.Empty;
            whiteboardTvDirty = true;
            return;
        }
        var index = bingoEditingIndex;
        var column = index % 5;
        if (!int.TryParse(bingoNumberInput, out var number) ||
            number < column * 15 + 1 || number > column * 15 + 15)
        {
            bingoInputError = $"USE {column * 15 + 1}-{column * 15 + 15}";
            bingoInputErrorUntilUtc = DateTime.UtcNow.AddSeconds(2.5);
            whiteboardTvDirty = true;
            return;
        }
        if (Enumerable.Range(0, 5).Any(row =>
                row * 5 + column != index && bingoLocalNumbers[row * 5 + column] == number))
        {
            bingoInputError = "NUMBER ALREADY ON CARD";
            bingoInputErrorUntilUtc = DateTime.UtcNow.AddSeconds(2.5);
            whiteboardTvDirty = true;
            return;
        }
        bingoLocalNumbers[index] = number;
        bingoEditingIndex = -1;
        bingoNumberInput = string.Empty;
        bingoInputError = string.Empty;
        whiteboardTvDirty = true;
    }

    private void LockBingoCard()
    {
        if (BingoNumberInputActive)
        {
            FinishBingoNumberInput(true);
            if (BingoNumberInputActive) return;
        }
        if (!BingoActive || bingoPhase is not (BingoPhase.Waiting or BingoPhase.Countdown) ||
            LocalBingoCard is not null || !ValidBingoCard(bingoLocalNumbers)) return;
        var numbers = (int[])bingoLocalNumbers.Clone();
        if (IsLocalActivityHost)
            AcceptBingoCard(LocalBingoUserId, numbers);
        else
        {
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(numbers)));
            var command = $"L|{bingoSessionId}|{bingoRound}|{payload}";
            if (bingoRelay is { IsHost: false } relay) relay.SubmitAction(command);
            else _ = stream.SendBingoContributionAsync(command);
        }
        bingoEditingCard = false;
        bingoEditingIndex = -1;
    }

    private void AcceptBingoCard(string playerId, int[] numbers)
    {
        if (!IsLocalActivityHost || bingoSessionId is null ||
            bingoPhase is not (BingoPhase.Waiting or BingoPhase.Countdown) ||
            bingoCards.ContainsKey(playerId) || !ValidBingoCard(numbers)) return;
        if (playerId != LocalBingoUserId &&
            !stream.Roster.Any(player => player.UserId == playerId)) return;
        var name = playerId == LocalBingoUserId ? LocalPictionaryDisplayName() :
            stream.Roster.FirstOrDefault(player => player.UserId == playerId)?.DisplayName ?? "Player";
        bingoCards[playerId] = new BingoCard(playerId, name, (int[])numbers.Clone());
        var wins = bingoStandings.GetValueOrDefault(playerId)?.Wins ?? 0;
        bingoStandings[playerId] = new BingoStanding(playerId, name, wins);
        if (bingoPhase == BingoPhase.Waiting)
        {
            bingoPhase = BingoPhase.Countdown;
            bingoPhaseEndsUtc = DateTime.UtcNow.AddSeconds(BingoCountdownSeconds);
        }
        bingoRevision++;
        whiteboardTvDirty = true;
        SendBingoState();
    }

    private void ShortenBingoCountdown(DateTime now)
    {
        if (bingoPhase != BingoPhase.Countdown) return;
        var everyone = bingoCards.ContainsKey(LocalBingoUserId) &&
            stream.Roster.All(p => bingoCards.ContainsKey(p.UserId));
        var deadline = BingoRules.ReadyDeadline(bingoPhaseEndsUtc, now, everyone);
        if (deadline == bingoPhaseEndsUtc) return;
        bingoPhaseEndsUtc = deadline;
        bingoRevision++;
        SendBingoState();
    }

    private void MarkLocalBingoNumber(int index)
    {
        if (LocalBingoCard is not { } card) return;
        var number = card.Numbers[index];
        if (!bingoCalled.Contains(number))
        {
            bingoInputError = "NUMBER NOT CALLED";
            bingoInputErrorUntilUtc = DateTime.UtcNow.AddSeconds(2.5);
            return;
        }
        if (!card.Marks.Contains(number)) SubmitBingoPlayAction("M", number.ToString());
    }

    private void SubmitBingoPlayAction(string kind, string value)
    {
        var command = $"{kind}|{bingoSessionId}|{bingoRound}|{value}";
        if (IsLocalActivityHost) HandleBingoContribution(LocalBingoUserId, command);
        else if (bingoRelay is { IsHost: false } relay)
        {
            if (!relay.SubmitAction(command))
            {
                bingoInputError = "PLEASE TRY AGAIN IN A MOMENT";
                bingoInputErrorUntilUtc = DateTime.UtcNow.AddSeconds(2.5);
            }
        }
        else _ = stream.SendBingoContributionAsync(command);
    }

    private bool HandleBingoContribution(string senderId, string command)
    {
        if (!IsLocalActivityHost || bingoSessionId is null ||
            senderId != LocalBingoUserId && !stream.Roster.Any(p => p.UserId == senderId) ||
            !BingoRelayRules.ValidAction(command, bingoSessionId)) return false;
        var parts = command.Split('|', 4);
        if (int.Parse(parts[2]) != bingoRound) return false;
        if (parts[0] == "L")
        {
            var numbers = JsonSerializer.Deserialize<int[]>(Convert.FromBase64String(parts[3]))!;
            AcceptBingoCard(senderId, numbers);
            return bingoCards.TryGetValue(senderId, out var locked) && locked.Numbers.SequenceEqual(numbers);
        }
        if (!bingoManual || bingoPhase != BingoPhase.Drawing || !bingoCards.TryGetValue(senderId, out var card)) return false;
        if (parts[0] == "M")
        {
            var number = int.Parse(parts[3]);
            if (!BingoRules.CanMark(card.Numbers, bingoCalled, number)) return false;
            if (!card.Marks.Contains(number)) bingoCards[senderId] = card with { Marks = [..card.Marks, number] };
        }
        else
        {
            if (!BingoRules.CanClaim(card.Numbers, bingoCalled, card.Marks, CurrentBingoPattern)) return false;
            bingoWinnerIds.Clear();
            bingoWinnerIds.Add(senderId);
            var standing = bingoStandings[senderId];
            bingoStandings[senderId] = standing with { Wins = standing.Wins + 1 };
            bingoPhase = BingoPhase.Results;
            bingoPhaseEndsUtc = DateTime.UtcNow.AddSeconds(BingoResultsSeconds);
            TryPlayBingoResultEmote();
        }
        bingoRevision++;
        whiteboardTvDirty = true;
        SendBingoState();
        return true;
    }

    private void SendBingoState()
    {
        if (!IsLocalActivityHost || bingoSessionId is null) return;
        UpdateAdditionalArcadePoints();
        var snapshot = new BingoSnapshot(
            bingoSessionId, bingoRevision, bingoRound, bingoPhase,
            bingoPhaseEndsUtc == DateTime.MaxValue ? 0 :
                new DateTimeOffset(bingoPhaseEndsUtc).ToUnixTimeMilliseconds(),
            bingoLastDrawUtc == DateTime.MinValue ? 0 :
                new DateTimeOffset(bingoLastDrawUtc).ToUnixTimeMilliseconds(),
            bingoCalled.ToArray(), bingoCards.Values.ToArray(), bingoWinnerIds.ToArray(),
            bingoPatterns, bingoRoundCount, bingoDrawSeconds, bingoStandings.Values.ToArray(), Manual: bingoManual);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot)));
        bingoLastSnapshotUtc = DateTime.UtcNow;
        if (TrySendBingoThroughCloudflare("S|" + encoded)) return;
        _ = stream.SendArcadeSnapshotAsync(StreamClient.BingoCommandPrefix, "S|" + encoded,
            $"{bingoSessionId}|{bingoRound}|{bingoPhase}|{bingoLastDrawUtc.Ticks}");
    }

    private void ApplyBingoCommand(string command)
    {
        if (command.StartsWith("X|", StringComparison.Ordinal))
        {
            if (bingoSessionId is null || command[2..] == bingoSessionId) ResetBingoLocal();
            return;
        }
        if (!command.StartsWith("S|", StringComparison.Ordinal)) return;
        try
        {
            var snapshot = JsonSerializer.Deserialize<BingoSnapshot>(
                Encoding.UTF8.GetString(Convert.FromBase64String(command[2..])));
            if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.SessionId) || snapshot.Round < 1 ||
                !Enum.IsDefined(snapshot.Phase) || snapshot.RoundCount is < 0 or > 14 ||
                snapshot.DrawSeconds is < 2 or > 10 ||
                snapshot.Patterns is { } patterns && (patterns.Length is < 1 or > 7 || patterns.Any(p => !Enum.IsDefined(p))) ||
                snapshot.Called is { } calls && (calls.Length > 75 || calls.Any(n => n is < 1 or > 75) || calls.Distinct().Count() != calls.Length) ||
                snapshot.SessionId == bingoSessionId && snapshot.Revision < bingoRevision) return;
            var phaseEnds = snapshot.PhaseEndsUnixMs == 0 ? DateTime.MaxValue :
                DateTimeOffset.FromUnixTimeMilliseconds(snapshot.PhaseEndsUnixMs).UtcDateTime;
            var lastDraw = snapshot.LastDrawUnixMs == 0 ? DateTime.MinValue :
                DateTimeOffset.FromUnixTimeMilliseconds(snapshot.LastDrawUnixMs).UtcDateTime;
            var newSession = snapshot.SessionId != bingoSessionId;
            var newRound = newSession || snapshot.Round != bingoRound;
            bingoSessionId = snapshot.SessionId;
            bingoPhase = snapshot.Phase;
            bingoPatterns = snapshot.Patterns ?? [BingoPattern.SingleLine];
            bingoRoundCount = snapshot.RoundCount;
            bingoDrawSeconds = snapshot.DrawSeconds;
            bingoManual = snapshot.Manual;
            bingoStandings.Clear();
            foreach (var standing in snapshot.Standings ?? [])
                if (standing is not null && !string.IsNullOrWhiteSpace(standing.PlayerId) && standing.Wins >= 0)
                    bingoStandings[standing.PlayerId] = standing;
            if (bingoPhase is not (BingoPhase.Waiting or BingoPhase.Countdown))
            {
                bingoEditingIndex = -1;
                bingoNumberInput = string.Empty;
            }
            bingoRevision = snapshot.Revision;
            bingoRound = snapshot.Round;
            bingoPhaseEndsUtc = phaseEnds;
            bingoLastDrawUtc = lastDraw;
            bingoCalled.Clear();
            bingoCalled.AddRange(snapshot.Called ?? []);
            bingoCards.Clear();
            foreach (var card in snapshot.Cards ?? [])
                if (card is not null && !string.IsNullOrWhiteSpace(card.PlayerId) && ValidBingoCard(card.Numbers) && BingoRules.ValidMarks(card.Numbers, bingoCalled, card.Marks)) bingoCards[card.PlayerId] = card;
            bingoParticipants = snapshot.Participants ?? (snapshot.Cards ?? []).Select(c => new BingoParticipant(c.PlayerId, c.Name)).ToArray();
            bingoWinnerIds.Clear();
            bingoWinnerIds.AddRange(snapshot.WinnerIds ?? []);
            UpdateAdditionalArcadePoints();
            if (newRound)
            {
                bingoLocalNumbers = GenerateBingoCard();
                bingoEditingCard = false;
                bingoEditingIndex = -1;
                bingoNumberInput = string.Empty;
            }
            TryPlayBingoResultEmote();
            whiteboardTvDirty = true;
        }
        catch (Exception) { }
    }

    private void DrawBingoPage()
    {
        lastActivitiesPage = HomePage.Bingo;
        if (!BingoActive)
        {
            DrawBingoSetupPage();
            return;
        }
        ImGui.TextColored(AccentHover, $"Round {BingoRoundLabel}  •  {BingoRules.Name(CurrentBingoPattern)}");
        ImGui.SameLine();
        ImGui.TextColored(MutedText, $"  Your score: {bingoStandings.GetValueOrDefault(LocalBingoUserId)?.Wins ?? 0}");
        var available = ImGui.GetContentRegionAvail();
        var width = MathF.Min(available.X, MathF.Max(Ui(260f), (available.Y - Ui(90f)) * 16f / 9f));
        var size = new Vector2(width, width * 9f / 16f);
        var origin = ImGui.GetCursorScreenPos() + new Vector2(MathF.Max(0, (available.X - width) / 2f), 0);
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##bingoPreview", size);
        var hovered = ImGui.IsItemHovered();
        var mouseDown = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        var mousePressed = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        if (hovered || bingoPageVolumeDragging)
        {
            var uv = (ImGui.GetIO().MousePos - origin) / size;
            SetBingoPageHover(hovered ? uv : null);
            UpdateBingoPageVolumePointer(uv, mouseDown, mousePressed && hovered);
            if (mousePressed && hovered) SubmitBingoTvControlAt(uv);
            if (hovered && IsBingoTvControlAt(uv)) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        else
        {
            SetBingoPageHover(null);
            UpdateBingoPageVolumePointer(null, mouseDown, false);
        }
        var draw = ImGui.GetWindowDrawList();
        if (bingoPageRenderer?.PreviewHandle is { } handle && handle != nint.Zero)
            draw.AddImage(new ImTextureID(handle),
                origin, origin + size);
        else
            draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(CardBg));
        ImGui.SetCursorScreenPos(origin + new Vector2(0, size.Y + Ui(12f)));
        ImGui.PushTextWrapPos();
        ImGui.TextColored(MutedText, bingoPhase switch
        {
            BingoPhase.Waiting => "Lock a card to start the 30-second countdown.",
            BingoPhase.Countdown => $"First ball in {BingoSecondsLeft} seconds. Cards can still be locked.",
            BingoPhase.Drawing => BingoRules.Description(CurrentBingoPattern),
            BingoPhase.Finished => "Game complete. Each round win earns one point; tied winners share the win.",
            BingoPhase.Results => bingoWinnerIds.Count == 0 ? "Round finished." :
                "Bingo! " + string.Join(", ", bingoWinnerIds.Select(BingoPlayerName)),
            _ => string.Empty,
        });
        ImGui.PopTextWrapPos();
        if (IsLocalActivityHost)
        {
            if (bingoPhase == BingoPhase.Finished)
            {
                if (ImGui.Button("Play again##bingoReplay")) RestartBingoGame();
                ImGui.SameLine();
            }
            if (ImGui.Button("End Bingo##bingoEnd")) StopBingoGame();
        }
    }

    internal bool IsBingoTvControlAt(Vector2 uv) => BingoTargetAt(uv) >= 0;
    internal void SetBingoTvHover(Vector2? uv) => SetBingoHover(uv, ref bingoTvHoveredTarget);
    private void SetBingoPageHover(Vector2? uv) => SetBingoHover(uv, ref bingoPageHoveredTarget);
    private void SetBingoHover(Vector2? uv, ref int target)
    {
        var next = uv is { } point ? BingoTargetAt(point) : -1;
        if (next == target) return;
        target = next;
        whiteboardTvDirty = true;
    }
    private int BingoTargetAt(Vector2 uv)
    {
        if (!BingoActive || uv.X is < 0 or > 1 || uv.Y is < 0 or > 1) return -1;
        var x = uv.X * WhiteboardRenderer.Width;
        var y = uv.Y * WhiteboardRenderer.Height;
        if (x is >= 1070 and <= 1112 && y is >= 29 and <= 73) return 200;
        if (x is >= 1122 and <= 1244 && y is >= 29 and <= 73) return 201;
        if (bingoHelpOpen)
            return x is >= 973 and <= 1017 && y is >= 177 and <= 215 ? 202 : -1;
        if (bingoSettingsOpen)
        {
            if (x is >= 1171 and <= 1214 && y is >= 160 and <= 199) return 203;
            if (x is >= 761 and <= 1201)
            {
                if (y is >= 216 and <= 268) return 210;
                if (y is >= 278 and <= 330) return 211;
                if (y is >= 340 and <= 392) return 212;
                if (y is >= 449 and <= 488) return 220;
            }
            return -1;
        }
        if (bingoPhase == BingoPhase.Finished)
            return IsLocalActivityHost && x is >= 492 and <= 788 && y is >= 507 and <= 559 ? 300 : -1;
        if (y is >= 628 and <= 692)
        {
            if (x is >= 1090 and <= 1245 && CanClaimBingo) return 103;
            if (x is >= 710 and <= 824 && LocalBingoCard is null &&
                bingoPhase is BingoPhase.Waiting or BingoPhase.Countdown) return 100;
            if (x is >= 834 and <= 948 && LocalBingoCard is null &&
                bingoPhase is BingoPhase.Waiting or BingoPhase.Countdown) return 101;
            if (x is >= 958 and <= 1080 && LocalBingoCard is null &&
                bingoPhase is BingoPhase.Waiting or BingoPhase.Countdown) return 102;
        }
        if ((bingoManual && bingoPhase == BingoPhase.Drawing && LocalBingoCard is not null ||
            bingoEditingCard && LocalBingoCard is null && bingoPhase is BingoPhase.Waiting or BingoPhase.Countdown) &&
            x >= 52 && x < 664 && y >= 180 && y < 555)
        {
            var column = (int)((x - 52) / 123);
            var row = (int)((y - 180) / 75);
            if (column < 5 && row < 5 && x - (52 + column * 123) < 115 &&
                y - (180 + row * 75) < 67 && row * 5 + column != 12)
                return row * 5 + column;
        }
        return -1;
    }
    internal void SubmitBingoTvControlAt(Vector2 uv)
    {
        var target = BingoTargetAt(uv);
        switch (target)
        {
            case 300:
                RestartBingoGame();
                break;
            case 200:
                if (BingoNumberInputActive) FinishBingoNumberInput(false);
                bingoHelpOpen = !bingoHelpOpen;
                if (bingoHelpOpen) bingoSettingsOpen = false;
                whiteboardTvDirty = true;
                break;
            case 201:
                if (BingoNumberInputActive) FinishBingoNumberInput(false);
                bingoSettingsOpen = !bingoSettingsOpen;
                if (bingoSettingsOpen) bingoHelpOpen = false;
                whiteboardTvDirty = true;
                break;
            case 202:
                bingoHelpOpen = false;
                whiteboardTvDirty = true;
                break;
            case 203:
                bingoSettingsOpen = false;
                whiteboardTvDirty = true;
                break;
            case 210:
                Plugin.Cfg.BingoMusicEnabled = !Plugin.Cfg.BingoMusicEnabled;
                Plugin.Cfg.Save();
                whiteboardTvDirty = true;
                break;
            case 211:
                Plugin.Cfg.BingoSoundEffectsEnabled = !Plugin.Cfg.BingoSoundEffectsEnabled;
                Plugin.Cfg.Save();
                whiteboardTvDirty = true;
                break;
            case 212:
                Plugin.Cfg.BingoEmotesEnabled = !Plugin.Cfg.BingoEmotesEnabled;
                Plugin.Cfg.Save();
                whiteboardTvDirty = true;
                break;
            case 220:
                SetBingoVolumeFromX(uv.X * WhiteboardRenderer.Width, true);
                break;
            case 100:
                bingoLocalNumbers = GenerateBingoCard();
                bingoEditingIndex = -1;
                bingoNumberInput = string.Empty;
                bingoInputError = string.Empty;
                whiteboardTvDirty = true;
                break;
            case 101:
                if (BingoNumberInputActive) FinishBingoNumberInput(true);
                if (BingoNumberInputActive) break;
                bingoEditingCard = !bingoEditingCard;
                bingoEditingIndex = -1;
                whiteboardTvDirty = true;
                break;
            case 102:
                LockBingoCard();
                break;
            case 103:
                SubmitBingoPlayAction("B", "claim");
                break;
            case >= 0 and < 25:
                if (bingoManual && bingoPhase == BingoPhase.Drawing) MarkLocalBingoNumber(target);
                else BeginBingoCellEdit(target);
                break;
        }
    }

    private void SetBingoVolumeFromX(float x, bool persist)
    {
        var volume = Math.Clamp((int)Math.Round((x - 778f) / 403f * 130f), 0, 130);
        if (Plugin.Cfg.Volume != volume || Plugin.Cfg.Muted && volume > 0)
        {
            Plugin.Cfg.Volume = volume;
            if (volume > 0) Plugin.Cfg.Muted = false;
            ApplyListeningVolume();
            whiteboardTvDirty = true;
        }
        if (persist) Plugin.Cfg.Save();
    }

    private void UpdateBingoVolumePointer(Vector2? uv, bool mouseDown, bool mousePressed,
        ref bool dragging)
    {
        if (mousePressed && uv is { } start && BingoTargetAt(start) == 220)
            dragging = true;
        if (!mouseDown)
        {
            if (dragging) Plugin.Cfg.Save();
            dragging = false;
            return;
        }
        if (dragging && uv is { } point)
            SetBingoVolumeFromX(point.X * WhiteboardRenderer.Width, false);
    }

    private void UpdateBingoPageVolumePointer(Vector2? uv, bool mouseDown, bool mousePressed) =>
        UpdateBingoVolumePointer(uv, mouseDown, mousePressed, ref bingoPageVolumeDragging);

    internal void UpdateBingoTvVolumePointer(Vector2? uv, bool mouseDown, bool mousePressed) =>
        UpdateBingoVolumePointer(uv, mouseDown, mousePressed, ref bingoTvVolumeDragging);

    private void ApplyBingoTvOverlay(byte[] pixels, int width, int height)
    {
        const uint bg = 0x0B0E1A, panel = 0x17182F, panel2 = 0x1B2039;
        const uint purple = 0x9A64F8, border = 0x574A86, white = 0xF5F5FC;
        const uint muted = 0xAEB6CF, gold = 0xFFCB54, dark = 0x090C16;
        uint[] colours = [0x24A8EC, 0xEF58B2, 0xFFD139, 0x2CD08A, 0xA163F1];
        var hover = bingoPageHoveredTarget >= 0 ? bingoPageHoveredTarget : bingoTvHoveredTarget;
        var now = DateTime.UtcNow;
        var shimmer = (Math.Sin(now.TimeOfDay.TotalSeconds * 4) + 1d) * 0.5d;
        FillPictionaryPixels(pixels, width, height, 0, 0, width, height, bg);
        // Cabinet bezel and light strip. Keep all important elements inside the safe area
        // so the same pixels remain readable on a TV seen at a distance.
        FillQuizTvPanel(pixels, width, height, 8, 8, 1264, 704, 0x0E1020, purple);
        DrawRoulettePrototypeRectOutline(pixels, width, height, 13, 13, 1254, 694, 0x372454, 3);
        FillPictionaryPixels(pixels, width, height, 19, 18, 1242, 68, 0x21172F);
        FillPictionaryPixels(pixels, width, height, 19, 84, 1242, 4, gold);
        FillPictionaryPixels(pixels, width, height, 20, 90, 1240, 2, purple);
        for (var light = 0; light < 25; light++)
        {
            var x = 35 + light * 49;
            FillRoulettePrototypeCircle(pixels, width, height, x, 88, 3,
                (light + (int)(now.TimeOfDay.TotalSeconds * 5)) % 5 == 0 ? white : 0x744B85);
        }
        for (var corner = 0; corner < 2; corner++)
        {
            var x = corner == 0 ? 25 : 1225;
            FillPictionaryPixels(pixels, width, height, x, 24, 12, 12, purple);
            FillPictionaryPixels(pixels, width, height, x + 13, 24, 9, 9, 0x654190);
            FillPictionaryPixels(pixels, width, height, x, 37, 9, 9, 0x654190);
        }
        DrawPictionaryPixelText(pixels, width, height, 58, 31, "ALPHA CHANNEL", 4, white);
        DrawPictionaryPixelText(pixels, width, height, 425, 21, "BINGO", 7, gold);
        DrawPictionaryPixelText(pixels, width, height, 735, 39, "PLAY TOGETHER", 2, muted);
        FillQuizTvPanel(pixels, width, height, 955, 30, 105, 42, 0x171128, purple);
        DrawPictionaryPixelTextCentered(pixels, width, height, 1007, 43,
            $"R {BingoRoundLabel}", 2, gold);
        DrawBingoMenuButtons(pixels, width, height, hover);

        FillQuizTvPanel(pixels, width, height, 25, 101, 660, 480, panel, purple);
        FillPictionaryPixels(pixels, width, height, 30, 106, 650, 36, 0x24203F);
        DrawPictionaryPixelText(pixels, width, height, 47, 113, "YOUR BINGO CARD", 3, 0xD6A7FF);
        FillQuizTvPanel(pixels, width, height, 500, 110, 163, 26, 0x10152A, border);
        DrawPictionaryPixelTextCentered(pixels, width, height, 581, 115,
            $"{bingoCalled.Count}/75 CALLED", 2, muted);
        for (var column = 0; column < 5; column++)
        {
            var x = 52 + column * 123;
            FillPictionaryPixels(pixels, width, height, x + 4, 147, 115, 31, 0x080910);
            FillQuizTvPanel(pixels, width, height, x, 143, 115, 31, colours[column], white);
            DrawPictionaryPixelTextCentered(pixels, width, height, x + 57, 147,
                "BINGO"[column].ToString(), 4, dark);
        }
        var card = BingoVisibleCard;
        var winningCells = bingoPhase == BingoPhase.Results && LocalBingoCard is not null && card.Length == 25
            ? WinningBingoCells(card, BingoLocalMarks) : [];
        for (var row = 0; row < 5; row++)
        for (var column = 0; column < 5; column++)
        {
            var index = row * 5 + column;
            var x = 52 + column * 123;
            var y = 180 + row * 75;
            var free = index == 12;
            var number = card.Length == 25 ? card[index] : 0;
            var marked = free || number != 0 && BingoLocalMarks.Contains(number);
            var highlight = index == hover;
            var typingHere = BingoNumberInputActive && index == bingoEditingIndex;
            var winner = winningCells.Contains(index);
            var newlyCalled = number != 0 && bingoCalled.Count > 0 &&
                number == bingoCalled[^1] && bingoLastDrawUtc != DateTime.MinValue &&
                (now - bingoLastDrawUtc).TotalSeconds < 2.2;
            FillPictionaryPixels(pixels, width, height, x + 3, y + 4, 115, 67, 0x080A16);
            FillQuizTvPanel(pixels, width, height, x, y, 115, 67,
                typingHere ? 0x3D2C58 : winner ? 0x57411D : marked ? 0x212544 : highlight ? 0x2C3150 : panel2,
                typingHere || winner || newlyCalled ? gold : highlight ? white : marked ? colours[column] : border);
            if (marked)
            {
                var daubRadius = newlyCalled ? 25 + (int)(shimmer * 3) : 24;
                FillRoulettePrototypeCircle(pixels, width, height, x + 57, y + 34,
                    daubRadius + 3, winner ? gold : white);
                FillRoulettePrototypeCircle(pixels, width, height, x + 57, y + 33,
                    daubRadius, free || winner ? gold : colours[column]);
                FillRoulettePrototypeCircle(pixels, width, height, x + 57, y + 33,
                    free ? 16 : 17, free ? 0x62450Fu : 0x1A1930u);
            }
            DrawPictionaryPixelTextCentered(pixels, width, height, x + 57, y + (free ? 27 : 19),
                typingHere ? bingoNumberInput.Length == 0 ? "__" : bingoNumberInput :
                free ? "FREE" : number == 0 ? "--" : number.ToString(),
                free ? 2 : 5,
                marked ? white : 0xF5F5FC);
            if (typingHere && now.Millisecond < 500)
                FillPictionaryPixels(pixels, width, height, x + 78, y + 19, 3, 35, gold);
            if (newlyCalled)
            {
                DrawRoulettePrototypeLine(pixels, width, height, x + 11, y + 9,
                    x + 20, y + 9, gold, 2);
                DrawRoulettePrototypeLine(pixels, width, height, x + 96, y + 55,
                    x + 105, y + 55, gold, 2);
            }
            if (highlight && bingoEditingCard)
                DrawRoulettePrototypeRectOutline(pixels, width, height,
                    x - 2, y - 2, 119, 71, gold, 3);
        }

        FillQuizTvPanel(pixels, width, height, 703, 101, 552, 480, panel, purple);
        FillPictionaryPixels(pixels, width, height, 708, 106, 542, 36, 0x24203F);
        DrawPictionaryPixelText(pixels, width, height, 727, 113, "CURRENT BALL", 3, 0xD6A7FF);
        FillQuizTvPanel(pixels, width, height, 724, 145, 507, 293, dark, border);
        DrawRoulettePrototypeRectOutline(pixels, width, height, 731, 152, 493, 279, 0x31214D, 4);
        for (var i = 0; i < 9; i++)
        {
            var lampX = 750 + i * 27;
            FillRoulettePrototypeCircle(pixels, width, height, lampX, 168, 3,
                (i + (int)(now.TimeOfDay.TotalSeconds * 6)) % 4 == 0 ? gold : 0x70501F);
            FillRoulettePrototypeCircle(pixels, width, height, lampX, 414, 3,
                (i + (int)(now.TimeOfDay.TotalSeconds * 6)) % 4 == 0 ? gold : 0x70501F);
        }
        // Backlit cabinet chamber, plinth and a glass-looking vertical ball tube.
        FillPictionaryPixels(pixels, width, height, 753, 362, 221, 12, 0x35244F);
        FillPictionaryPixels(pixels, width, height, 780, 374, 168, 20, 0x2A204B);
        FillPictionaryPixels(pixels, width, height, 810, 394, 108, 9, 0x8051C0);
        FillPictionaryPixels(pixels, width, height, 1006, 163, 71, 252, 0x1A213A);
        FillPictionaryPixels(pixels, width, height, 1010, 169, 63, 241, 0x121629);
        FillPictionaryPixels(pixels, width, height, 1008, 169, 5, 241, 0x7656B2);
        FillPictionaryPixels(pixels, width, height, 1070, 169, 5, 241, 0x7656B2);
        FillPictionaryPixels(pixels, width, height, 1015, 171, 3, 235, 0xBED6FF);
        FillPictionaryPixels(pixels, width, height, 1000, 157, 83, 11, purple);
        FillPictionaryPixels(pixels, width, height, 1000, 407, 83, 11, purple);
        for (var i = 0; i < 5; i++)
        {
            var wobble = (int)(Math.Sin(now.TimeOfDay.TotalSeconds * 2.3 + i) * 3);
            FillRoulettePrototypeCircle(pixels, width, height, 1047 + wobble, 193 + i * 44, 19, 0x090C16);
            FillRoulettePrototypeCircle(pixels, width, height, 1046 + wobble, 191 + i * 44, 17, colours[i]);
            FillRoulettePrototypeCircle(pixels, width, height, 1040 + wobble, 185 + i * 44, 5, white);
        }
        var current = bingoCalled.Count > 0 ? bingoCalled[^1] : 0;
        var age = bingoLastDrawUtc == DateTime.MinValue ? 10 :
            Math.Max(0, (DateTime.UtcNow - bingoLastDrawUtc).TotalSeconds);
        var showingBall = current > 0 && age < 3.6;
        var ballX = 861;
        var ballY = 305;
        var radius = 72;
        if (age < 1.2)
        {
            var progress = Math.Clamp(age / 1.2, 0d, 1d);
            ballX = (int)(1046 - progress * 185);
            ballY = (int)(183 + progress * 122 - Math.Sin(progress * Math.PI) * 18);
            radius = (int)(24 + progress * 48);
        }
        else if (age >= 2.8 && age < 3.6)
        {
            var progress = (age - 2.8) / 0.8;
            var lastRackX = 756 + (Math.Min(8, bingoCalled.Count) - 1) * 61;
            ballX = (int)(861 + (lastRackX - 861) * progress);
            ballY = (int)(305 + 215 * progress - Math.Sin(progress * Math.PI) * 38);
            radius = (int)(72 - 46 * progress);
        }
        if (showingBall)
        {
            var ballColour = colours[(current - 1) / 15];
            var shadowColour = ((ballColour & 0xFEFEFEu) >> 1) + ((ballColour & 0xFCFCFCu) >> 2);
            FillRoulettePrototypeCircle(pixels, width, height, ballX, ballY + 9, radius + 15, 0x241338);
            DrawRoulettePrototypeRing(pixels, width, height, ballX, ballY,
                radius + 5, radius + 9, 0x7C4DA8);
            FillRoulettePrototypeCircle(pixels, width, height, ballX, ballY, radius + 2, white);
            FillRoulettePrototypeCircle(pixels, width, height, ballX, ballY, radius - 2, ballColour);
            FillRoulettePrototypeCircle(pixels, width, height, ballX + radius / 3,
                ballY + radius / 3, Math.Max(4, radius / 3), shadowColour);
            FillRoulettePrototypeCircle(pixels, width, height, ballX - radius / 3, ballY - radius / 3,
                Math.Max(4, radius / 6), white);
            var letter = "BINGO"[(current - 1) / 15].ToString();
            DrawPictionaryPixelTextCentered(pixels, width, height, ballX, ballY - radius / 2,
                letter, age < 0.6 || age > 3.15 ? 2 : 3, dark);
            DrawPictionaryPixelTextCentered(pixels, width, height, ballX, ballY,
                current.ToString(), age < 0.6 || age > 3.15 ? 2 : 5, dark);
        }
        else
            DrawPictionaryPixelTextCentered(pixels, width, height, 861, 294,
                current == 0 ? "?" : "NEXT", current == 0 ? 5 : 3, muted);
        if (bingoPhase == BingoPhase.Results && !showingBall && bingoWinnerIds.Count > 0)
        {
            FillQuizTvPanel(pixels, width, height, 754, 226, 215, 128,
                shimmer > 0.5 ? 0x51341Bu : 0x281D2Eu, gold);
            DrawPictionaryPixelTextCentered(pixels, width, height, 861, 245,
                "BINGO!", 5, gold);
            var winnerName = BingoPlayerName(bingoWinnerIds[0]);
            DrawPictionaryPixelTextCentered(pixels, width, height, 861, 314,
                winnerName.Length > 13 ? winnerName[..13].ToUpperInvariant() : winnerName.ToUpperInvariant(),
                2, white);
        }
        else if (showingBall && age is > 0.8 and < 2.8)
        {
            for (var ray = 0; ray < 8; ray++)
            {
                var angle = ray * Math.PI / 4;
                var inner = radius + 11 + (int)(shimmer * 2);
                var outer = inner + 10;
                DrawRoulettePrototypeLine(pixels, width, height,
                    ballX + (int)(Math.Cos(angle) * inner),
                    ballY + (int)(Math.Sin(angle) * inner),
                    ballX + (int)(Math.Cos(angle) * outer),
                    ballY + (int)(Math.Sin(angle) * outer),
                    gold, 2);
            }
        }
        FillQuizTvPanel(pixels, width, height, 1090, 206, 130, 127, 0x1E1830, 0x704D9A);
        DrawPictionaryPixelTextCentered(pixels, width, height, 1155, 220,
            bingoPhase switch
            {
                BingoPhase.Waiting => "FIRST CARD",
                BingoPhase.Countdown => "STARTS IN",
                BingoPhase.Drawing => "NEXT BALL",
                BingoPhase.Results => "NEXT ROUND",
                _ => "BINGO!",
            }, 2, 0xD9C2EE);
        DrawPictionaryPixelTextCentered(pixels, width, height, 1155, 267,
            bingoPhase == BingoPhase.Waiting ? "--:--" : $"{BingoSecondsLeft / 60:00}:{BingoSecondsLeft % 60:00}",
            4, gold);
        FillQuizTvPanel(pixels, width, height, 1090, 343, 130, 68,
            bingoPhase == BingoPhase.Results ? 0x493010u : 0x22172Fu,
            bingoPhase == BingoPhase.Results ? gold : purple);
        DrawPictionaryPixelTextCentered(pixels, width, height, 1155, 368,
            bingoPhase == BingoPhase.Results ? bingoWinnerIds.Contains(LocalBingoUserId) ? "WINNER!" : "ROUND OVER" : "GOOD LUCK!", 2,
            bingoPhase == BingoPhase.Results ? gold : 0xD6A7FF);
        DrawPictionaryPixelText(pixels, width, height, 727, 452, "RECENTLY CALLED", 3, 0xD6A7FF);
        FillQuizTvPanel(pixels, width, height, 724, 486, 507, 79, 0x0C1222, border);
        var recent = (showingBall ? bingoCalled.Take(bingoCalled.Count - 1) : bingoCalled)
            .TakeLast(8).ToArray();
        for (var i = 0; i < recent.Length; i++)
        {
            var number = recent[i];
            var x = 756 + i * 61;
            var colour = colours[(number - 1) / 15];
            FillRoulettePrototypeCircle(pixels, width, height, x + 2, 524, 27, 0x070911);
            FillRoulettePrototypeCircle(pixels, width, height, x, 520, 26, white);
            FillRoulettePrototypeCircle(pixels, width, height, x, 520, 23, colour);
            FillRoulettePrototypeCircle(pixels, width, height, x - 8, 511, 4, white);
            DrawPictionaryPixelTextCentered(pixels, width, height, x, 504,
                "BINGO"[(number - 1) / 15].ToString(), 2, dark);
            DrawPictionaryPixelTextCentered(pixels, width, height, x, 522, number.ToString(), 2, dark);
        }

        FillPictionaryPixels(pixels, width, height, 28, 596, 1230, 110, 0x090A12);
        FillQuizTvPanel(pixels, width, height, 25, 590, 1230, 110, 0x1D182D, purple);
        FillPictionaryPixels(pixels, width, height, 30, 595, 1220, 4, gold);
        // Mode tile is deliberately separate from recent balls: it can describe
        // future patterns without changing the card or ball layout.
        FillQuizTvPanel(pixels, width, height, 42, 608, 259, 79, 0x171426, 0x64518C);
        var previewCells = BingoRules.PreviewCells(CurrentBingoPattern,
            new DateTimeOffset(now).ToUnixTimeMilliseconds() / 1200);
        for (var row = 0; row < 5; row++)
        for (var column = 0; column < 5; column++)
            FillPictionaryPixels(pixels, width, height, 54 + column * 11, 620 + row * 11,
                9, 9, previewCells.Contains(row * 5 + column) ? gold : 0x4A4164);
        DrawPictionaryPixelText(pixels, width, height, 121, 621, "GAME MODE", 2, muted);
        DrawPictionaryPixelText(pixels, width, height, 121, 649, BingoRules.Name(CurrentBingoPattern).ToUpperInvariant(), 2, gold);
        FillQuizTvPanel(pixels, width, height, 311, 608, 380, 79, 0x171426, 0x64518C);
        DrawPictionaryPixelText(pixels, width, height, 327, 618, "BINGO BUZZ", 2, gold);
        var announcement = bingoInputErrorUntilUtc > now && bingoInputError.Length > 0
            ? bingoInputError
            : BingoNumberInputActive
            ? $"TYPE {bingoEditingIndex % 5 * 15 + 1}-{bingoEditingIndex % 5 * 15 + 15} THEN ENTER"
            : bingoEditingCard && LocalBingoCard is null
            ? "CLICK A CELL, THEN TYPE"
            : LocalBingoCard is null && bingoPhase == BingoPhase.Drawing
                ? "JOIN THE NEXT ROUND"
            : bingoManual && bingoPhase == BingoPhase.Drawing ? CanClaimBingo ? "PRESS BINGO TO CLAIM!" : "CLICK CALLED NUMBERS"
            : BuildBingoAnnouncement();
        if (announcement.Length > 29) announcement = announcement[..29];
        DrawPictionaryPixelText(pixels, width, height, 327, 649, announcement, 2,
            bingoInputErrorUntilUtc > now ? 0xFF7474 : white);
        BingoButton(pixels, width, height, 710, 628, 114, 64, "RANDOM", hover == 100, false,
            LocalBingoCard is null && bingoPhase is BingoPhase.Waiting or BingoPhase.Countdown);
        BingoButton(pixels, width, height, 834, 628, 114, 64,
            bingoEditingCard ? "DONE" : "EDIT CARD", hover == 101, bingoEditingCard,
            LocalBingoCard is null && bingoPhase is BingoPhase.Waiting or BingoPhase.Countdown);
        BingoButton(pixels, width, height, 958, 628, 122, 64,
            LocalBingoCard is not null ? "LOCKED" : "LOCK CARD", hover == 102, false,
            LocalBingoCard is null && bingoPhase is BingoPhase.Waiting or BingoPhase.Countdown);
        var bingoFlash = bingoPhase == BingoPhase.Results && shimmer > 0.5;
        FillPictionaryPixels(pixels, width, height, 1094, 633, 155, 64, 0x080911);
        FillQuizTvPanel(pixels, width, height, 1090, 628, 155, 64,
            bingoFlash ? 0xFFE29Au : bingoPhase == BingoPhase.Results ? gold : CanClaimBingo ? 0x805827u : 0x58401Bu,
            bingoPhase == BingoPhase.Results ? white : gold);
        DrawPictionaryPixelTextCentered(pixels, width, height, 1167, 650, "BINGO!", 3,
            bingoPhase == BingoPhase.Results ? dark : gold);
        for (var column = 0; column < 96; column++)
            FillPictionaryPixels(pixels, width, height, 18 + column * 13, 702, 13, 7,
                column % 2 == 0 ? gold : 0x26183B);
        if (bingoPhase == BingoPhase.Finished) DrawBingoFinalResults(pixels, width, height, hover);
        if (bingoHelpOpen) DrawBingoHelpOverlay(pixels, width, height, hover);
        if (bingoSettingsOpen) DrawBingoSettingsOverlay(pixels, width, height, hover);
    }

    private static void BingoButton(byte[] pixels, int width, int height,
        int x, int y, int w, int h, string label, bool hover, bool selected, bool enabled)
    {
        FillPictionaryPixels(pixels, width, height, x + 4, y + 5, w, h, 0x090A13);
        FillQuizTvPanel(pixels, width, height, x, y, w, h,
            !enabled ? 0x26283Au : hover ? 0x543280u : selected ? 0x432767u : 0x241B3Du,
            !enabled ? 0x51556Au : hover ? 0xFFCB54u : 0xB07AFFu);
        if (enabled)
            FillPictionaryPixels(pixels, width, height, x + 4, y + 4, w - 8, 3,
                hover ? 0xFFE4A1u : 0x8054BFu);
        DrawPictionaryPixelTextCentered(pixels, width, height, x + w / 2, y + 23,
            label, 2, enabled ? 0xF5F5FCu : 0x9EA5B8u);
        if (hover)
            DrawRoulettePrototypeRectOutline(pixels, width, height, x - 2, y - 2,
                w + 4, h + 4, 0xFFCB54, 2);
    }

    private void DrawBingoMenuButtons(byte[] pixels, int width, int height, int hover)
    {
        const uint gold = 0xFFCB54, white = 0xF5F5FC, purple = 0x9A64F8;
        FillQuizTvPanel(pixels, width, height, 1070, 29, 42, 44,
            hover == 200 || bingoHelpOpen ? 0x503272u : 0x231934u,
            hover == 200 ? gold : purple);
        DrawPictionaryPixelTextCentered(pixels, width, height, 1091, 38, "?", 4, white);
        FillQuizTvPanel(pixels, width, height, 1122, 29, 122, 44,
            hover == 201 || bingoSettingsOpen ? 0x503272u : 0x231934u,
            hover == 201 ? gold : purple);
        var cogX = 1145;
        var cogY = 51;
        for (var tooth = 0; tooth < 8; tooth++)
        {
            var angle = tooth * Math.PI / 4;
            FillPictionaryPixels(pixels, width, height,
                cogX + (int)(Math.Cos(angle) * 13) - 2,
                cogY + (int)(Math.Sin(angle) * 13) - 2, 5, 5, white);
        }
        DrawRoulettePrototypeRing(pixels, width, height, cogX, cogY, 5, 10, white);
        DrawPictionaryPixelText(pixels, width, height, 1166, 45, "SET", 2, white);
    }

    private void DrawBingoHelpOverlay(byte[] pixels, int width, int height, int hover)
    {
        const uint gold = 0xFFCB54, white = 0xF5F5FC, muted = 0xBAC1D5;
        FillQuizTvPanel(pixels, width, height, 239, 156, 793, 401, 0x171325, 0x9A64F8);
        FillPictionaryPixels(pixels, width, height, 247, 164, 777, 51, 0x28203D);
        DrawPictionaryPixelText(pixels, width, height, 267, 176, "HOW TO PLAY BINGO", 4, gold);
        FillQuizTvPanel(pixels, width, height, 973, 177, 44, 38,
            hover == 202 ? 0x5B387Cu : 0x261B38u, hover == 202 ? gold : 0x9A64F8u);
        DrawPictionaryPixelTextCentered(pixels, width, height, 995, 187, "X", 2, white);
        DrawPictionaryPixelText(pixels, width, height, 267, 242,
            "MAKE A RANDOM CARD OR EDIT EACH NUMBER.", 2, white);
        DrawPictionaryPixelText(pixels, width, height, 267, 274,
            "CLICK A CELL, TYPE A NUMBER, THEN PRESS ENTER.", 2, muted);
        DrawPictionaryPixelText(pixels, width, height, 267, 316,
            "LOCK YOUR CARD TO JOIN THE ROUND.", 2, white);
        DrawPictionaryPixelText(pixels, width, height, 267, 348,
            "FIRST LOCK STARTS A 30 SECOND COUNTDOWN.", 2, muted);
        DrawPictionaryPixelText(pixels, width, height, 267, 390,
            bingoManual ? "CLICK CALLED NUMBERS TO MARK YOUR CARD." : "MATCHING NUMBERS MARK THEMSELVES.", 2, white);
        DrawPictionaryPixelText(pixels, width, height, 267, 422,
            BingoRules.Description(CurrentBingoPattern).ToUpperInvariant(), 2, muted);
        FillPictionaryPixels(pixels, width, height, 257, 465, 755, 3, gold);
        DrawPictionaryPixelText(pixels, width, height, 267, 486,
            bingoManual ? "COMPLETE THE PATTERN, THEN PRESS BINGO!" : "EVERYONE WHO FINISHES ON THE SAME BALL WINS.", 2, gold);
        DrawPictionaryPixelText(pixels, width, height, 267, 519,
            "ONE POINT PER WIN. LOCK A FRESH CARD EACH ROUND.", 2, muted);
    }

    private void DrawBingoSettingsOverlay(byte[] pixels, int width, int height, int hover)
    {
        const uint gold = 0xFFCB54, white = 0xF5F5FC, muted = 0xBAC1D5;
        FillQuizTvPanel(pixels, width, height, 740, 145, 485, 402, 0x171325, 0x9A64F8);
        FillPictionaryPixels(pixels, width, height, 748, 153, 469, 52, 0x28203D);
        DrawPictionaryPixelText(pixels, width, height, 764, 166, "BINGO SETTINGS", 3, gold);
        FillQuizTvPanel(pixels, width, height, 1171, 160, 43, 39,
            hover == 203 ? 0x5B387Cu : 0x261B38u, hover == 203 ? gold : 0x9A64F8u);
        DrawPictionaryPixelTextCentered(pixels, width, height, 1192, 171, "X", 2, white);
        DrawBingoSettingRow(pixels, width, height, 761, 216, "MUSIC",
            "BACKGROUND THEME (READY)", Plugin.Cfg.BingoMusicEnabled, hover == 210);
        DrawBingoSettingRow(pixels, width, height, 761, 278, "SOUND FX",
            "BALL / STAMP (READY)", Plugin.Cfg.BingoSoundEffectsEnabled, hover == 211);
        DrawBingoSettingRow(pixels, width, height, 761, 340, "EMOTES",
            "WIN / LOSS MOTION", Plugin.Cfg.BingoEmotesEnabled, hover == 212);
        DrawPictionaryPixelText(pixels, width, height, 774, 413, "VOLUME", 2, muted);
        DrawPictionaryPixelText(pixels, width, height, 1134, 413,
            $"{Plugin.Cfg.Volume}%", 2, gold);
        FillQuizTvPanel(pixels, width, height, 770, 449, 423, 39,
            0x231B35, hover == 220 ? gold : 0x735296u);
        FillPictionaryPixels(pixels, width, height, 778, 465, 403, 8, 0x554266);
        var fill = (int)Math.Round(403d * Plugin.Cfg.Volume / 130d);
        if (fill > 0)
            FillPictionaryPixels(pixels, width, height, 778, 465, fill, 8, 0x9A64F8);
        FillRoulettePrototypeCircle(pixels, width, height, 778 + fill, 469, 11,
            hover == 220 ? white : gold);
        DrawPictionaryPixelText(pixels, width, height, 766, 514,
            "YOUR SETTINGS - SHARED PLUGIN VOLUME", 2, muted);
    }

    private static void DrawBingoSettingRow(byte[] pixels, int width, int height,
        int x, int y, string title, string detail, bool enabled, bool hovered)
    {
        FillQuizTvPanel(pixels, width, height, x, y, 440, 52,
            hovered ? 0x38274Eu : 0x211B32u, hovered ? 0xFFCB54u : 0x6E568Fu);
        DrawPictionaryPixelText(pixels, width, height, x + 13, y + 7,
            title, 2, 0xF5F5FC);
        DrawPictionaryPixelText(pixels, width, height, x + 13, y + 30,
            detail, 1, 0xBAC1D5);
        FillQuizTvPanel(pixels, width, height, x + 348, y + 9, 76, 34,
            enabled ? 0x15543Cu : 0x3D3C4Eu,
            enabled ? 0x5BDB9Bu : 0x8D879Bu);
        DrawPictionaryPixelTextCentered(pixels, width, height, x + 386, y + 19,
            enabled ? "ON" : "OFF", 2, enabled ? 0xABF6CFu : 0xBAC1D5u);
    }
}

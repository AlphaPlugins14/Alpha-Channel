using System.Text;
using System.Text.Json;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private enum SlotMachinePhase { Idle, CoinInserting, Ready, HandlePull, Spinning, Results }
    private sealed record SlotMachineSnapshot(
        string SessionId, int Revision, SlotMachinePhase Phase,
        long PhaseStartedUnixMs, int SpinSeed, int[] Symbols, bool Shared = false,
        string? ActivePlayerId = null, int Turn = 0, SlotPlayer[]? Players = null,
        string[]? Queue = null, SlotWin[]? Wins = null, int Payout = 0, string Result = "");
    private sealed record SlotPlayer(string Id, string Name, int Coins, int Spins, int Wins, int Spent, int Won, int Best, int Refills);
    private sealed record SlotWin(string PlayerId, string Name, int Payout, string Label, int Turn);

    private static readonly int[] SlotMachineSamples = SlotMachineRules.Symbols;
    private static readonly TimeSpan SlotCoinDuration = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan SlotHandleDuration = TimeSpan.FromMilliseconds(550);
    private static readonly TimeSpan SlotSpinDuration = TimeSpan.FromMilliseconds(3400);

    private string? slotMachineSessionId;
    private string? slotMachineWhiteboardSessionId;
    private SlotMachinePhase slotMachinePhase;
    private DateTime slotMachinePhaseStartedUtc;
    private DateTime slotMachineLastSnapshotUtc;
    private DateTime slotMachineLastFrameUtc;
    private int slotMachineRevision;
    private int slotMachineSpinSeed;
    private int[] slotMachineSymbols = new int[9];
    private bool slotMachineShared = true;
    private bool slotMachineSetupShared = true;
    private string? slotMachineActivePlayerId;
    private int slotMachineTurn;
    private int slotMachinePayout;
    private string slotMachineResult = string.Empty;
    private readonly Dictionary<string, SlotPlayer> slotMachinePlayers = new(StringComparer.Ordinal);
    private readonly List<string> slotMachineQueue = [];
    private readonly List<SlotWin> slotMachineWins = [];
    private bool slotMachineLedgerOpen;
    private int slotMachineLedgerPage;
    private string LocalSlotPlayerId => CurrentSession?.AccountId ?? stream.LocalUserId ?? "local";
    private bool LocalSlotTurn => slotMachineActivePlayerId == LocalSlotPlayerId;
    private string SlotActivePlayerName => slotMachineActivePlayerId is { } id
        ? slotMachinePlayers.GetValueOrDefault(id)?.Name ?? "Player" : "Player";
    private Vector2? slotMachineTvHover;
    private Vector2? slotMachinePageHover;
    private WhiteboardRenderer? slotMachinePageRenderer;

    internal bool SlotMachineActive => slotMachineSessionId is not null;
    internal bool SlotMachineTvControlsInteractive => SlotMachineActive;
    private bool CanOperateSlotMachine => SlotMachineActive && (IsLocalActivityHost || slotMachineShared);

    private void OpenSlotMachinePage()
    {
        if (!CanLaunchActivity(ActiveActivityKind.SlotMachine)) return;
        currentPage = HomePage.SlotMachine;
        lastActivitiesPage = HomePage.SlotMachine;
    }

    private void StartSlotMachineGame()
    {
        if (!IsLocalActivityHost || CurrentSession is null ||
            ActiveActivity != ActiveActivityKind.None) return;
        slotMachineShared = slotMachineSetupShared;
        slotMachineRequests.Clear();
        slotMachinePlayers.Clear();
        slotMachineQueue.Clear();
        slotMachineWins.Clear();
        slotMachineActivePlayerId = null;
        slotMachineTurn = slotMachinePayout = 0;
        slotMachineResult = string.Empty;
        slotMachineHelpOpen = false;
        slotMachineLedgerOpen = false;
        EnsureSlotPlayer(LocalSlotPlayerId);
        slotMachineSessionId = Guid.NewGuid().ToString("N");
        slotMachinePhase = SlotMachinePhase.Idle;
        slotMachinePhaseStartedUtc = DateTime.UtcNow;
        slotMachineRevision = 1;
        slotMachineSpinSeed = Random.Shared.Next();
        slotMachineSymbols = NewSlotSymbols();
        StartHostWhiteboard(WhiteboardMode.Solo, ActiveActivityKind.SlotMachine);
        slotMachineWhiteboardSessionId = whiteboardSessionId;
        whiteboardTvDirty = true;
        SendSlotMachineState();
    }

    private static int[] NewSlotSymbols()
    {
        var symbols = new int[9];
        for (var index = 0; index < symbols.Length; index++)
            symbols[index] = SlotMachineRules.SymbolForRoll(Random.Shared.Next(SlotMachineRules.TotalSymbolWeight));
        return symbols;
    }

    private void StopSlotMachineGame()
    {
        if (IsLocalActivityHost && slotMachineSessionId is { } session)
            _ = stream.SendSlotMachineCommandAsync("X|" + session);
        if (IsLocalActivityHost && whiteboardSessionId is { } canvas &&
            string.Equals(canvas, slotMachineWhiteboardSessionId, StringComparison.Ordinal))
            SendAndApplyWhiteboardCommand("X|" + canvas);
        ResetSlotMachineLocal();
    }

    private void ResetSlotMachineLocal()
    {
        CaptureArcadeScoresBeforeReset("slot-machine");
        DisposeActivityRelay("slot-machine");
        ResetSlotMachineAudio();
        slotMachineSessionId = null;
        slotMachineWhiteboardSessionId = null;
        slotMachinePhase = SlotMachinePhase.Idle;
        slotMachinePhaseStartedUtc = DateTime.MinValue;
        slotMachineTvHover = slotMachinePageHover = null;
        slotMachineRequests.Clear();
        slotMachinePlayers.Clear();
        slotMachineQueue.Clear();
        slotMachineWins.Clear();
        slotMachineActivePlayerId = null;
        slotMachineHelpOpen = false;
        slotMachineLedgerOpen = false;
        slotMachineTurn = slotMachinePayout = 0;
        slotMachineResult = string.Empty;
        whiteboardTvDirty = true;
    }

    internal void UpdateSlotMachine()
    {
        if (stream.Mode == StreamMode.None)
        {
            stream.IncomingSlotMachineCommands.Clear();
        }
        while (stream.IncomingSlotMachineCommands.TryDequeue(out var incoming))
            ReceiveArcadeServerCommand("slot-machine", incoming.UserId, incoming.Command);

        UpdateActivityRelay("slot-machine");

        if (!SlotMachineActive) return;
        var now = DateTime.UtcNow;
        if (IsLocalActivityHost)
        {
            if (!whiteboardActive ||
                !string.Equals(whiteboardSessionId, slotMachineWhiteboardSessionId, StringComparison.Ordinal))
            {
                StopSlotMachineGame();
                return;
            }
            UpdateSlotParticipants(now);
            var elapsed = now - slotMachinePhaseStartedUtc;
            if (slotMachinePhase == SlotMachinePhase.CoinInserting && elapsed >= SlotCoinDuration)
                ChangeSlotMachinePhase(SlotMachinePhase.Ready, now);
            else if (slotMachinePhase == SlotMachinePhase.HandlePull && elapsed >= SlotHandleDuration)
            {
                slotMachineSymbols = NewSlotSymbols();
                ChangeSlotMachinePhase(SlotMachinePhase.Spinning, now);
            }
            else if (slotMachinePhase == SlotMachinePhase.Spinning && elapsed >= SlotSpinDuration)
                SettleSlotSpin(now);
            else if (slotMachinePhase == SlotMachinePhase.Results && elapsed >= TimeSpan.FromSeconds(2))
            {
                slotMachineActivePlayerId = null;
                ChangeSlotMachinePhase(SlotMachinePhase.Idle, now);
                StartNextSlotTurn(now);
            }
            else if (slotMachinePhase == SlotMachinePhase.Ready && elapsed >= TimeSpan.FromSeconds(20))
                CancelSlotTurn(now);
            if (now - slotMachineLastSnapshotUtc >= TimeSpan.FromSeconds(4))
                SendSlotMachineState();
        }
        UpdateSlotMachineAudio(now);
        if (now - slotMachineLastFrameUtc < TimeSpan.FromMilliseconds(65)) return;
        slotMachineLastFrameUtc = now;
        if (slotMachinePhase is SlotMachinePhase.CoinInserting or SlotMachinePhase.HandlePull or SlotMachinePhase.Spinning or SlotMachinePhase.Results)
            whiteboardTvDirty = true;
        if (IsOpen && currentPage == HomePage.SlotMachine)
        {
            if (slotMachinePageRenderer is null && !screenController.Engine.TryInitializeGraphics()) return;
            slotMachinePageRenderer ??= DxHandler.RunOnRenderThreadAndWait(
                "AlphaChannel.SlotMachinePage.Create",
                () => new WhiteboardRenderer("AlphaChannel.SlotMachinePage"),
                TimeSpan.FromSeconds(30));
            var pixels = new byte[WhiteboardRenderer.Width * WhiteboardRenderer.Height * 4];
            ApplySlotMachineTvOverlay(pixels, WhiteboardRenderer.Width, WhiteboardRenderer.Height);
            slotMachinePageRenderer.Upload(pixels);
        }
    }

    private void ChangeSlotMachinePhase(SlotMachinePhase phase, DateTime now)
    {
        slotMachinePhase = phase;
        slotMachinePhaseStartedUtc = now;
        slotMachineRevision++;
        whiteboardTvDirty = true;
        SendSlotMachineState();
    }

    private void InsertSlotMachineCoin()
    {
        if (!CanOperateSlotMachine) return;
        SendSlotAction("Q", slotMachineTurn);
    }

    private void PullSlotMachineHandle()
    {
        if (!CanOperateSlotMachine || !LocalSlotTurn || slotMachinePhase != SlotMachinePhase.Ready) return;
        SendSlotAction("P", slotMachineTurn);
    }

    private void SendSlotMachineState()
    {
        if (!IsLocalActivityHost || slotMachineSessionId is null) return;
        UpdateAdditionalArcadePoints();
        whiteboardTvDirty = true;
        var snapshot = new SlotMachineSnapshot(slotMachineSessionId, slotMachineRevision,
            slotMachinePhase, new DateTimeOffset(slotMachinePhaseStartedUtc).ToUnixTimeMilliseconds(),
            slotMachineSpinSeed, slotMachineSymbols, slotMachineShared, slotMachineActivePlayerId,
            slotMachineTurn, slotMachinePlayers.Values.ToArray(), slotMachineQueue.ToArray(), slotMachineWins.ToArray(),
            slotMachinePayout, slotMachineResult);
        slotMachineLastSnapshotUtc = DateTime.UtcNow;
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot)));
        if (TrySendActivityThroughCloudflare("slot-machine", "S|" + encoded)) return;
        _ = stream.SendArcadeSnapshotAsync(StreamClient.SlotMachineCommandPrefix, "S|" + encoded,
            $"{slotMachineSessionId}|{snapshot.Turn}|{slotMachinePhase}|{slotMachinePhaseStartedUtc.Ticks}");
    }

    private void ApplySlotMachineCommand(string command)
    {
        if (command.StartsWith("X|", StringComparison.Ordinal))
        {
            if (slotMachineSessionId is null || command[2..] == slotMachineSessionId)
                ResetSlotMachineLocal();
            return;
        }
        if (!command.StartsWith("S|", StringComparison.Ordinal)) return;
        try
        {
            var snapshot = JsonSerializer.Deserialize<SlotMachineSnapshot>(
                Encoding.UTF8.GetString(Convert.FromBase64String(command[2..])));
            if (snapshot is null || snapshot.Symbols is not { Length: 9 } ||
                snapshot.Symbols.Any(number => !SlotMachineSamples.Contains(number)) ||
                !Guid.TryParseExact(snapshot.SessionId, "N", out _) ||
                snapshot.Phase is < SlotMachinePhase.Idle or > SlotMachinePhase.Results ||
                (slotMachineSessionId == snapshot.SessionId && snapshot.Revision < slotMachineRevision))
                return;
            var phaseStarted = DateTimeOffset.FromUnixTimeMilliseconds(snapshot.PhaseStartedUnixMs).UtcDateTime;
            if (slotMachineSessionId != snapshot.SessionId) { slotMachineHelpOpen = false; slotMachineLedgerOpen = false; slotMachineLedgerPage = 0; }
            slotMachineShared = snapshot.Shared;
            slotMachineActivePlayerId = snapshot.ActivePlayerId;
            slotMachineTurn = snapshot.Turn;
            slotMachinePayout = snapshot.Payout;
            slotMachineResult = snapshot.Result;
            slotMachinePlayers.Clear();
            foreach (var player in snapshot.Players ?? [])
                if (player is not null && !string.IsNullOrWhiteSpace(player.Id)) slotMachinePlayers[player.Id] = player;
            slotMachineQueue.Clear(); slotMachineQueue.AddRange(snapshot.Queue ?? []);
            slotMachineWins.Clear(); slotMachineWins.AddRange(snapshot.Wins ?? []);
            slotMachineSessionId = snapshot.SessionId;
            slotMachineRevision = snapshot.Revision;
            slotMachinePhase = snapshot.Phase;
            slotMachinePhaseStartedUtc = phaseStarted;
            slotMachineSpinSeed = snapshot.SpinSeed;
            slotMachineSymbols = snapshot.Symbols;
            UpdateAdditionalArcadePoints();
            whiteboardTvDirty = true;
        }
        catch (Exception) { }
    }

    private void DrawSlotMachinePage()
    {
        lastActivitiesPage = HomePage.SlotMachine;
        if (!SlotMachineActive)
        {
            DrawSlotSetupPage();
            return;
        }
        var available = ImGui.GetContentRegionAvail();
        var width = MathF.Min(available.X, MathF.Max(Ui(260f), (available.Y - Ui(95f)) * 16f / 9f));
        var size = new Vector2(width, width * 9f / 16f);
        var origin = ImGui.GetCursorScreenPos() + new Vector2(MathF.Max(0, (available.X - width) * .5f), 0);
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##slotMachinePreview", size);
        if (ImGui.IsItemHovered())
        {
            var uv = (ImGui.GetIO().MousePos - origin) / size;
            slotMachinePageHover = uv;
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                SubmitSlotMachineTvControlAt(uv);
            if (IsSlotMachineTvControlAt(uv))
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        else slotMachinePageHover = null;
        var draw = ImGui.GetWindowDrawList();
        if (slotMachinePageRenderer?.PreviewHandle is { } handle && handle != nint.Zero)
            draw.AddImage(new ImTextureID(handle), origin, origin + size);
        else
            draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(CardBg));
        ImGui.SetCursorScreenPos(origin + new Vector2(0, size.Y + Ui(10f)));
        ImGui.PushTextWrapPos();
        ImGui.TextColored(MutedText, SlotStatusText());
        ImGui.PopTextWrapPos();
        if (ImGui.Button("Ledger##slotLedger")) ToggleSlotLedger();
        ImGui.SameLine();
        if (ImGui.Button("Help##slotHelp")) ToggleSlotMachineHelp();
        if (slotMachineQueue.Contains(LocalSlotPlayerId))
        {
            ImGui.SameLine();
            if (ImGui.Button("Leave queue##slotLeaveQueue")) SendSlotAction("C", slotMachineTurn);
        }
        if (IsLocalActivityHost)
        {
            ImGui.SameLine();
            if (ImGui.Button("End Slot Machine##slotEnd")) StopSlotMachineGame();
        }
        if (ImGui.CollapsingHeader("Recent wins##slotRecentWins"))
        {
            if (slotMachineWins.Count == 0) ImGui.TextColored(MutedText, "The first win will appear here.");
            foreach (var win in slotMachineWins)
                ImGui.TextWrapped($"{win.Name}: {win.Label} +{win.Payout} coins (spin {win.Turn})");
        }
    }

    internal bool IsSlotMachineTvControlAt(Vector2 uv)
    {
        if (!SlotMachineActive || uv.X is < 0 or > 1 || uv.Y is < 0 or > 1) return false;
        var x = uv.X * WhiteboardRenderer.Width;
        var y = uv.Y * WhiteboardRenderer.Height;
        if (slotMachineHelpOpen) return x is >= 960 and <= 1060 && y is >= 110 and <= 155;
        if (slotMachineLedgerOpen)
            return x is >= 1090 and <= 1180 && y is >= 125 and <= 170 ||
                x is >= 850 and <= 1180 && y is >= 580 and <= 630;
        if (x is >= 1120 and <= 1250 && (y is >= 35 and <= 90 || y is >= 105 and <= 160)) return true;
        if (!CanOperateSlotMachine) return false;
        if (x is >= 238 and <= 345 && y is >= 325 and <= 548)
            return !LocalSlotTurn && !slotMachineQueue.Contains(LocalSlotPlayerId);
        return LocalSlotTurn && slotMachinePhase == SlotMachinePhase.Ready &&
            x is >= 968 and <= 1055 && y is >= 265 and <= 570;
    }

    internal void SetSlotMachineTvHover(Vector2? uv) => slotMachineTvHover = uv;

    internal void SubmitSlotMachineTvControlAt(Vector2 uv)
    {
        if (!IsSlotMachineTvControlAt(uv)) return;
        var x = uv.X * WhiteboardRenderer.Width;
        var y = uv.Y * WhiteboardRenderer.Height;
        if (slotMachineHelpOpen) { ToggleSlotMachineHelp(); return; }
        if (slotMachineLedgerOpen)
        {
            if (y <= 170) ToggleSlotLedger();
            else slotMachineLedgerPage = Math.Clamp(slotMachineLedgerPage + (x < 1015 ? -1 : 1),
                0, Math.Max(0, (slotMachinePlayers.Count - 1) / 7));
            whiteboardTvDirty = true;
            return;
        }
        if (x >= 1120)
        {
            if (y >= 105) ToggleSlotMachineHelp();
            else ToggleSlotLedger();
            return;
        }
        if (x < 500) InsertSlotMachineCoin();
        else PullSlotMachineHandle();
    }
}

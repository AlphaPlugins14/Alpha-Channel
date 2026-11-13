using System.Globalization;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly HashSet<string> slotMachineRequests = new(StringComparer.Ordinal);

    private bool SlotPlayerConnected(string id) => id == LocalSlotPlayerId || stream.Mode == StreamMode.Hosting &&
        stream.Roster.Any(p => p.UserId == id);

    private void EnsureSlotPlayer(string id)
    {
        var name = id == LocalSlotPlayerId ? LocalPictionaryDisplayName() :
            stream.Roster.FirstOrDefault(p => p.UserId == id)?.DisplayName ?? "Player";
        if (slotMachinePlayers.TryGetValue(id, out var player))
            slotMachinePlayers[id] = player with { Name = name };
        else slotMachinePlayers[id] = new SlotPlayer(id, name, SlotMachineRules.StartingCoins, 0, 0, 0, 0, 0, 0);
    }

    private void SendSlotAction(string action, int turn)
    {
        var command = $"{action}|{slotMachineSessionId}|{Guid.NewGuid():N}|{turn.ToString(CultureInfo.InvariantCulture)}";
        if (IsLocalActivityHost) HandleSlotContribution(LocalSlotPlayerId, command);
        else SendArcadePlayerAction("slot-machine", command);
    }

    private void HandleSlotContribution(string id, string command)
    {
        if (!IsLocalActivityHost || !SlotMachineActive || !SlotPlayerConnected(id) ||
            !slotMachineShared && id != LocalSlotPlayerId) return;
        var parts = command.Split('|');
        if (parts.Length != 4 || parts[1] != slotMachineSessionId || !Guid.TryParseExact(parts[2], "N", out _) ||
            !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var turn) ||
            parts[0] is not ("Q" or "P" or "C") || !slotMachineRequests.Add(parts[2])) return;
        var now = DateTime.UtcNow;
        if (parts[0] == "Q")
        {
            if (slotMachineActivePlayerId == id || slotMachineQueue.Contains(id)) return;
            EnsureSlotPlayer(id);
            slotMachineQueue.Add(id);
            slotMachineRevision++;
            if (slotMachinePhase == SlotMachinePhase.Idle) StartNextSlotTurn(now);
            else SendSlotMachineState();
        }
        else if (parts[0] == "C")
        {
            if (!slotMachineQueue.Remove(id)) return;
            slotMachineRevision++;
            SendSlotMachineState();
        }
        else if (id == slotMachineActivePlayerId && turn == slotMachineTurn && slotMachinePhase == SlotMachinePhase.Ready)
        {
            var player = slotMachinePlayers[id];
            slotMachinePlayers[id] = player with { Spent = player.Spent + SlotMachineRules.SpinCost };
            slotMachineSpinSeed = Random.Shared.Next();
            ChangeSlotMachinePhase(SlotMachinePhase.HandlePull, now);
        }
    }

    private void StartNextSlotTurn(DateTime now)
    {
        if (!IsLocalActivityHost || slotMachinePhase != SlotMachinePhase.Idle) return;
        slotMachineQueue.RemoveAll(id => !SlotPlayerConnected(id));
        if (slotMachineQueue.Count == 0) return;
        var id = slotMachineQueue[0];
        slotMachineQueue.RemoveAt(0);
        var player = slotMachinePlayers[id];
        if (player.Coins < SlotMachineRules.SpinCost)
            player = player with { Coins = SlotMachineRules.StartingCoins, Refills = player.Refills + 1 };
        slotMachinePlayers[id] = player with { Coins = player.Coins - SlotMachineRules.SpinCost };
        slotMachineActivePlayerId = id;
        slotMachineTurn++;
        slotMachinePayout = 0;
        slotMachineResult = string.Empty;
        ChangeSlotMachinePhase(SlotMachinePhase.CoinInserting, now);
    }

    private void CancelSlotTurn(DateTime now)
    {
        if (slotMachineActivePlayerId is { } id && slotMachinePlayers.TryGetValue(id, out var player))
            slotMachinePlayers[id] = player with { Coins = player.Coins + SlotMachineRules.SpinCost };
        slotMachineActivePlayerId = null;
        ChangeSlotMachinePhase(SlotMachinePhase.Idle, now);
        StartNextSlotTurn(now);
    }

    private void UpdateSlotParticipants(DateTime now)
    {
        if (slotMachineQueue.RemoveAll(id => !SlotPlayerConnected(id)) > 0)
        {
            slotMachineRevision++;
            SendSlotMachineState();
        }
        if (slotMachineActivePlayerId is { } id && !SlotPlayerConnected(id) &&
            slotMachinePhase is SlotMachinePhase.CoinInserting or SlotMachinePhase.Ready)
            CancelSlotTurn(now);
    }

    private void SettleSlotSpin(DateTime now)
    {
        if (slotMachinePhase != SlotMachinePhase.Spinning || slotMachineActivePlayerId is not { } id ||
            !slotMachinePlayers.TryGetValue(id, out var player)) return;
        (slotMachinePayout, slotMachineResult) = SlotMachineRules.Evaluate(slotMachineSymbols);
        slotMachinePlayers[id] = player with
        {
            Coins = player.Coins + slotMachinePayout,
            Spins = player.Spins + 1,
            Wins = player.Wins + (slotMachinePayout > 0 ? 1 : 0),
            Won = player.Won + slotMachinePayout,
            Best = Math.Max(player.Best, slotMachinePayout),
        };
        if (slotMachinePayout > 0)
        {
            slotMachineWins.Insert(0, new SlotWin(id, player.Name, slotMachinePayout, slotMachineResult, slotMachineTurn));
            if (slotMachineWins.Count > 10) slotMachineWins.RemoveRange(10, slotMachineWins.Count - 10);
        }
        ChangeSlotMachinePhase(SlotMachinePhase.Results, now);
    }

    private string SlotStatusText()
    {
        if (!CanOperateSlotMachine) return "Host-only play — watch the host spin. Open the ledger to see their results.";
        if (slotMachineQueue.IndexOf(LocalSlotPlayerId) is var position && position >= 0)
            return $"You are #{position + 1} in the queue. Everyone watches the current player's spin.";
        if (LocalSlotTurn && slotMachinePhase == SlotMachinePhase.Ready)
            return "Your turn! Pull the handle within 20 seconds, or your coin will be refunded.";
        return $"Click the coin slot to play or join the queue. Your balance: {slotMachinePlayers.GetValueOrDefault(LocalSlotPlayerId)?.Coins ?? SlotMachineRules.StartingCoins} play coins.";
    }

    private void ToggleSlotLedger()
    {
        slotMachineHelpOpen = false;
        slotMachineLedgerOpen = !slotMachineLedgerOpen;
        slotMachineLedgerPage = 0;
        whiteboardTvDirty = true;
    }
}

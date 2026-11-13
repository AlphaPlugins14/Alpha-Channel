namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private int ChocoRaceDraftTotal => chocoRaceDraftByLane.Sum();
    private int ChocoRaceDraftLaneCount => chocoRaceDraftByLane.Count(amount => amount > 0);
    private bool ChocoRaceLocalBetLocked =>
        chocoRaceBets.Any(bet => bet.PlayerId == LocalChocoRaceUserId);
    private bool ChocoRaceCanEditDraft => ChocoRaceBetsOpen && !ChocoRaceLocalBetLocked &&
        chocoRacePendingBetId is null;
    private bool ChocoRaceCanPlaceDraft => ChocoRaceCanEditDraft && ChocoRaceDraftTotal > 0;

    private void ResetChocoRaceDraft()
    {
        Array.Clear(chocoRaceDraftByLane);
        chocoRaceSelectedChip = 0;
        chocoRaceNotEnoughChipsUntilUtc = DateTime.MinValue;
        chocoRacePendingBetId = null;
        chocoRacePendingBetSentUtc = DateTime.MinValue;
    }

    private void SelectChocoRaceChip(int amount)
    {
        if (!ChocoRaceCanEditDraft || !ChocoRaceChipValues.Contains(amount)) return;
        var remaining = chocoRacePlayers.TryGetValue(LocalChocoRaceUserId, out var player)
            ? player.Chips - ChocoRaceDraftTotal : 0;
        if (amount > remaining)
        {
            chocoRaceNotEnoughChipsUntilUtc = ChocoRaceNowUtc.AddSeconds(2.5d);
            return;
        }
        chocoRaceSelectedChip = chocoRaceSelectedChip == amount ? 0 : amount;
        chocoRaceNotEnoughChipsUntilUtc = DateTime.MinValue;
    }

    private void AddChocoRaceDraftChip(int lane)
    {
        if (!ChocoRaceCanEditDraft || lane is < 0 or >= 6 ||
            !ChocoRaceChipValues.Contains(chocoRaceSelectedChip) ||
            !chocoRacePlayers.TryGetValue(LocalChocoRaceUserId, out var player)) return;
        if ((long)ChocoRaceDraftTotal + chocoRaceSelectedChip > player.Chips)
        {
            chocoRaceSelectedChip = 0;
            chocoRaceNotEnoughChipsUntilUtc = ChocoRaceNowUtc.AddSeconds(2.5d);
            return;
        }
        var placedChip = chocoRaceSelectedChip;
        chocoRaceDraftByLane[lane] += placedChip;
        chocoRaceNotEnoughChipsUntilUtc = DateTime.MinValue;
        if (player.Chips - ChocoRaceDraftTotal < placedChip)
            chocoRaceSelectedChip = 0;
    }

    private void ClearChocoRaceDraft()
    {
        if (!ChocoRaceCanEditDraft) return;
        ResetChocoRaceDraft();
    }

    private string ChocoRaceDraftPayload(string betId) =>
        $"B|{chocoRaceSessionId}|{chocoRaceRound}|{betId}|" +
        string.Join(',', Enumerable.Range(0, 6)
            .Where(lane => chocoRaceDraftByLane[lane] > 0)
            .Select(lane => $"{lane}:{chocoRaceDraftByLane[lane]}"));

    private void RetryChocoRacePendingBet(DateTime now)
    {
        if (GameRelay("choco-race") is not null) return;
        if (stream.Mode != StreamMode.Viewing || chocoRacePendingBetId is null ||
            !ChocoRaceBetsOpen || (now - chocoRacePendingBetSentUtc).TotalSeconds < 2) return;
        chocoRacePendingBetSentUtc = now;
        SendArcadePlayerAction("choco-race", ChocoRaceDraftPayload(chocoRacePendingBetId));
    }
}

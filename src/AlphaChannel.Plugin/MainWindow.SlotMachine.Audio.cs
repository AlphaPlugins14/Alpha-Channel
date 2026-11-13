using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly RouletteChipSoundPlayer slotCoinSoundPlayer = new("slot-coin.wav");
    private bool slotCoinSoundPrepared;
    private (string? Session, int Turn)? slotCoinSoundPlayed;

    private void UpdateSlotMachineAudio(DateTime now)
    {
        if (!slotCoinSoundPrepared)
        {
            slotCoinSoundPlayer.Prepare();
            slotCoinSoundPrepared = true;
        }
        var volume = Plugin.Cfg.Muted ? 0 : Plugin.Cfg.Volume;
        slotCoinSoundPlayer.SetVolume(volume);
        if (slotMachinePhase != SlotMachinePhase.CoinInserting) return;
        var insertion = (slotMachineSessionId, slotMachineTurn);
        if (slotCoinSoundPlayed == insertion) return;
        slotCoinSoundPlayed = insertion;
        // Play only for an accepted insertion, never when merely joining the queue.
        // Ignore stale insertion snapshots received after the animation has ended.
        if (now - slotMachinePhaseStartedUtc < SlotCoinDuration)
            slotCoinSoundPlayer.Play(volume);
    }

    private void ResetSlotMachineAudio()
    {
        slotCoinSoundPlayer.Dispose();
        slotCoinSoundPrepared = false;
        slotCoinSoundPlayed = null;
    }
}

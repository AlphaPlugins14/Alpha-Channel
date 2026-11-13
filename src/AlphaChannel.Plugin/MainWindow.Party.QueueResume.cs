using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.BroadcastAllowance;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool CanResumePartyQueue => stream.Mode == StreamMode.Hosting && PartyPlaybackQueue is not null &&
        partyQueueState.Upcoming.Count > 0 &&
        !PlaybackActivityOwnsTv;

    // Requests and played history are not playable queue entries. Never replace
    // a current item or resume an existing paused queue just because another item is added.
    private bool PartyQueueEmptyForAutoStart => partyQueueState.AutoPlay &&
        partyQueueState.Current is null && partyQueueState.Upcoming.Count == 0 &&
        PartyPlaybackQueue?.Current is null && (PartyPlaybackQueue?.Entries.Count ?? 0) == 0;

    private void ResumePartyQueueAfterAddition(bool wasEmpty)
    {
        if (wasEmpty && partyQueueState.AutoPlay && VisiblePartyCountdown is null) ResumePartyQueue();
    }

    private void ResumePartyQueue()
    {
        // Standard video playback can be replaced directly, including while
        // paused or loading. Activities and broadcasts require their end controls.
        if (!CanResumePartyQueue || PartyPlaybackQueue is not { } playback) return;
        playback.Advance();
        partyQueueNextReconcile = DateTime.MinValue;
        partyQueueNextSync = DateTime.MinValue;
    }
}

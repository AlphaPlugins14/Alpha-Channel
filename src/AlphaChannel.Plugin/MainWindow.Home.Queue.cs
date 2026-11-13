using System.Collections.Concurrent;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly ConcurrentQueue<Action> homeSearchQueueAdditions = new();

    private void BeginHomeSearchQueueAddition(string url, bool party, VideoQueueEntry? resolvedEntry = null, int? destinationSlot = null)
    {
        if (ChoosePageVideo(new(url, url, string.Empty, null, null), chosen => BeginHomeSearchQueueAddition(chosen.Url, party, chosen, destinationSlot))) return;
        var account = CurrentSession?.AccountId;
        var token = CurrentSession?.Token;
        var generation = partyQueueGeneration;
        var host = stream.HostId;
        var mode = stream.Mode;
        var slot = destinationSlot ?? queueManager.ActiveSlotIndex;
        var profile = queueManager.Slots[slot];
        _ = Task.Run(async () =>
        {
            var entry = resolvedEntry is { } selected
                ? new VideoSearchEntry(selected.Title, url, selected.Source, selected.Duration, selected.ThumbnailUrl)
                : new VideoSearchEntry(url, url, string.Empty, null, null);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(youtubeFeedToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                var metadata = resolvedEntry is null
                    ? await searchResolver.ResolveMetadataAsync(url, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false)
                    : null;
                if (metadata is not null)
                    entry = new VideoSearchEntry(
                        string.IsNullOrWhiteSpace(metadata.Title) ? url : metadata.Title,
                        url, metadata.Source, metadata.Duration, metadata.ThumbnailUrl);
            }
            catch (OperationCanceledException) when (youtubeFeedToken.IsCancellationRequested) { return; }
            catch (Exception)
            {
                // Optional metadata must not prevent adding an otherwise playable link.
            }

            if (Volatile.Read(ref disposeStarted) != 0) return;
            homeSearchQueueAdditions.Enqueue(() =>
            {
                // A lookup must not send to a different account, party or saved queue.
                if (CurrentSession?.AccountId != account || CurrentSession?.Token != token) return;
                if (party)
                {
                    if (!HomeSearchHasParty || stream.Mode != mode || stream.HostId != host ||
                        partyQueueGeneration != generation) return;
                    AddPartySearchResult(entry);
                }
                else
                {
                    if (!ReferenceEquals(queueManager.Slots[slot], profile)) return;
                    AddVideoToQueueSlot(entry, slot);
                }
                queueAddedFeedbackUntil = ImGui.GetTime() + 2;
            });
        });
    }

    private void ApplyHomeSearchQueueAdditions()
    {
        while (homeSearchQueueAdditions.TryDequeue(out var apply))
            if (Volatile.Read(ref disposeStarted) == 0) apply();
    }
}

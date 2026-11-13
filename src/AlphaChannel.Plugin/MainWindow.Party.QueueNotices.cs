using AlphaChannel.Plugin.Core;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void AppendPartyQueueChatNotice(PartyQueueNotice notice, DateTime receivedAt)
    {
        if (partyChatItems.Any(item => item.Id == notice.Id)) return;
        var requested = notice.Kind == "requested";
        var previous = partyChatItems.LastOrDefault();
        // Bulk imports can span consecutive worker replies. Only combine an
        // uninterrupted burst by the same person, within four seconds of its
        // first notice; never merge across a message or another person's action.
        var merge = previous is { Kind: PartyChatItemKind.QueueNotice, ReceivedAt: { } first } &&
            receivedAt >= first && receivedAt - first <= TimeSpan.FromSeconds(4) &&
            string.Equals(previous.UserId, notice.ActorId, StringComparison.OrdinalIgnoreCase) &&
            previous.Text.StartsWith(requested ? "requested " : "added ", StringComparison.Ordinal);
        var count = merge ? previous!.QueueNoticeCount + notice.Count : notice.Count;
        var text = requested
            ? count == 1 ? "requested a video" : $"requested videos ({count})"
            : count == 1 ? "added a video to the queue" : $"added videos to the queue ({count})";
        var id = notice.Id;
        if (merge)
        {
            id = previous!.Id;
            partyChatItems[^1] = previous with { Name = notice.ActorName, Text = text, QueueNoticeCount = count };
        }
        else partyChatItems.Add(new PartyChatItem(id, PartyChatItemKind.QueueNotice, notice.ActorName, text,
            ReceivedAt: receivedAt, UserId: notice.ActorId, QueueNoticeCount: count));
        RegisterPartyChatUnread(id, playNotificationSound: false, kind: PartyChatItemKind.QueueNotice);
    }
}

using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrainPartyChat()
    {
        DrainPartyRosterNotices();
        while (stream.IncomingNextQueueSnapshots.TryDequeue(out var nextSnapshot))
        {
            viewerPartyQueueCount = Math.Max(0, nextSnapshot.Count);
            viewerPartyNextQueueEntry = nextSnapshot.Count > 0 &&
                !string.IsNullOrWhiteSpace(nextSnapshot.Title)
                ? new VideoQueueEntry(
                    string.Empty,
                    nextSnapshot.Title,
                    nextSnapshot.Source ?? string.Empty,
                    nextSnapshot.DurationSeconds is { } seconds
                        ? TimeSpan.FromSeconds(Math.Max(0d, seconds))
                        : null,
                    nextSnapshot.ThumbnailUrl)
                : null;
        }

        // Consume ordinary messages before their reactions/deletions, including
        // the server's echo of our own sends. This must run on framework ticks,
        // even when neither the main chat nor mini chat is being drawn.
        while (stream.IncomingChat.TryDequeue(out var message))
        {
            if (string.IsNullOrWhiteSpace(message.Text) ||
                partyChatItems.Any(item => item.Kind == PartyChatItemKind.Message && item.Id == message.MessageId))
                continue;
            partyChatItems.Add(new PartyChatItem(message.MessageId, PartyChatItemKind.Message,
                string.IsNullOrWhiteSpace(message.DisplayName) ? "Someone" : message.DisplayName,
                message.Text, ReceivedAt: DateTime.Now, UserId: message.UserId,
                ReplyToId: message.ReplyToId, HasSharedMessageId: message.HasSharedMessageId));
            if (!string.Equals(message.UserId, stream.LocalUserId, StringComparison.OrdinalIgnoreCase))
                RegisterPartyChatUnread(message.MessageId, mentionsMe: PartyChatMentionsLocalUser(message.Text));
        }

        while (stream.IncomingChatMessageDeletes.TryDequeue(out var deletion))
        {
            var index = partyChatItems.FindIndex(item => item.Id == deletion.MessageId &&
                item.Kind == PartyChatItemKind.Message && item.HasSharedMessageId);
            // A late joiner has no copy of an earlier message. Discard the request.
            if (index < 0)
                continue;

            var target = partyChatItems[index];
            var senderIsAuthor = string.Equals(deletion.UserId, target.UserId,
                StringComparison.OrdinalIgnoreCase);
            var senderIsHost = stream.Mode == StreamMode.Hosting
                ? string.Equals(deletion.UserId, stream.LocalUserId,
                    StringComparison.OrdinalIgnoreCase)
                : string.Equals(deletion.UserId, stream.HostId,
                    StringComparison.OrdinalIgnoreCase);
            if (!senderIsAuthor && !senderIsHost)
                continue;

            var unreadStartIndex = partyChatFirstUnreadId is { } firstUnreadId
                ? partyChatItems.FindIndex(item => item.Id == firstUnreadId)
                : -1;
            var wasUnread = unreadStartIndex >= 0 && index >= unreadStartIndex;
            partyChatItems.RemoveAt(index);
            partyChatUnseenMentions.Remove(deletion.MessageId);
            partyChatMessageReactions.Remove(deletion.MessageId);
            if (partyChatReplyTarget?.Id == deletion.MessageId)
                partyChatReplyTarget = null;
            if (partyChatJumpToMessageId == deletion.MessageId)
                partyChatJumpToMessageId = null;
            if (partyChatHighlightedMessageId == deletion.MessageId)
                partyChatHighlightedMessageId = null;
            if (partyChatFirstUnreadId == deletion.MessageId)
            {
                partyChatFirstUnreadId = partyChatItems.Skip(index).FirstOrDefault(item => partyChatUnreadIds.Contains(item.Id))?.Id;
                partyChatUnreadDividerShownUtc = null;
            }
            if (wasUnread)
            {
                partyChatUnreadIds.Remove(deletion.MessageId);
            }
        }

        DrainChatMessageReactions();

        if (partyChatItems.Count > 200)
        {
            partyChatItems.RemoveRange(
                0,
                partyChatItems.Count - 200);
            var retainedIds = partyChatItems.Select(item => item.Id).ToHashSet();
            partyChatUnreadIds.IntersectWith(retainedIds);
            partyChatUnseenMentions.IntersectWith(retainedIds);
            foreach (var oldId in partyChatMessageReactions.Keys.Where(id => !retainedIds.Contains(id)).ToArray())
                partyChatMessageReactions.Remove(oldId);
        }

        if (stream.Mode == StreamMode.None)
        {
            ResetPartyChatState();
        }

        if (stream.Mode == StreamMode.None)
        {
            viewerPartyNextQueueEntry = null;
            viewerPartyQueueCount = 0;
        }
    }

    private void DrainChatMessageReactions()
    {
        while (stream.IncomingChatMessageReactions.TryDequeue(out var reaction))
        {
            if ((uint)reaction.Emoji >= ChatMessageReactionCount ||
                !partyChatItems.Any(item => item.Id == reaction.MessageId))
                continue;

            var reactorName = ChatReactionDisplayName(reaction.UserId, party: true);
            if (reactorName != "Someone") partyChatReactionNames[reaction.UserId] = reactorName;
            if (!partyChatMessageReactions.TryGetValue(reaction.MessageId, out var byEmoji))
                partyChatMessageReactions[reaction.MessageId] = byEmoji = [];
            if (!byEmoji.TryGetValue(reaction.Emoji, out var users))
                byEmoji[reaction.Emoji] = users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!users.Add(reaction.UserId))
                users.Remove(reaction.UserId);
            if (users.Count == 0)
                byEmoji.Remove(reaction.Emoji);
            if (byEmoji.Count == 0)
                partyChatMessageReactions.Remove(reaction.MessageId);
        }
    }
}

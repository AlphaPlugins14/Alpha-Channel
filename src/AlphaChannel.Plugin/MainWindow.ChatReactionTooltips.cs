using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly Dictionary<string, string> partyChatReactionNames = new(StringComparer.OrdinalIgnoreCase);

    private string ChatReactionDisplayName(string accountId, bool party = false)
    {
        if (CurrentSession is { } session && accountId.Equals(session.AccountId, StringComparison.OrdinalIgnoreCase))
            return CurrentDisplayName ?? session.DisplayName;
        if (party)
        {
            var member = stream.Roster.FirstOrDefault(m => m.UserId.Equals(accountId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(member?.DisplayName)) return member.DisplayName;
            if (partyChatReactionNames.TryGetValue(accountId, out var cached)) return cached;
            var post = partyChatItems.LastOrDefault(m => m.UserId.Equals(accountId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(post?.Name)) return post.Name;
        }
        if (memberDisplayNameCache.TryGetValue(accountId, out var name)) return name;
        return friends.FirstOrDefault(f => f.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? "Someone";
    }

    private void DrawChatReactionTooltip(int emoji, int count, bool reactedByMe, IEnumerable<string>? names)
    {
        if (!ImGui.IsItemHovered()) return;
        var label = ChatReactionLabels[emoji] + " · " + count;
        if (names is not null)
        {
            var entries = names.Select(name => string.IsNullOrWhiteSpace(name) ? "Someone" : name.Replace('\r', ' ').Replace('\n', ' '))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (entries.Length > 0) label += "\n" + string.Join("\n", entries);
        }
        if (reactedByMe) label += "\n\nClick to remove your reaction";
        UiFonts.SetTooltip(label);
    }
}

using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private int FriendUnreadMessageCount(string accountId) => conversations
        .Where(chat => !chat.IsGroup && chat.Members.Any(member =>
            string.Equals(member.AccountId, accountId, StringComparison.Ordinal)))
        .Sum(chat => Math.Max(0, chat.UnreadCount));

    private int UnreadChatNotificationCount()
    {
        if (CurrentSession is null) return 0;
        var count = conversations.Where(chat => !IsArchivedChat(chat))
            .Sum(chat => (long)Math.Max(0, chat.UnreadCount));
        return (int)Math.Min(int.MaxValue, count);
    }

    private static string UnreadMessageBadgeText(int count) => count > 99 ? "99+" : count.ToString();

    private static Vector2 UnreadMessageBadgeSize(int count) => new(
        MathF.Max(Ui(20f), ImGui.CalcTextSize(UnreadMessageBadgeText(count)).X + Ui(8f)), Ui(20f));

    private static void DrawUnreadMessageBadge(Vector2 center, int count)
    {
        if (count <= 0) return;
        var text = UnreadMessageBadgeText(count);
        var size = UnreadMessageBadgeSize(count);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(center - size * 0.5f, center + size * 0.5f,
            ImGui.GetColorU32(Danger), size.Y * 0.5f);
        draw.AddText(center - ImGui.CalcTextSize(text) * 0.5f,
            ImGui.GetColorU32(Vector4.One), text);
    }
}

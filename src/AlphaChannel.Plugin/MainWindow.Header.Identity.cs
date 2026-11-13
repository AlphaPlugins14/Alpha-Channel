using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private float DesiredHeaderProfileWidth()
    {
        using var font = UiFonts.PushRelative(1f, emphasis: true);
        var nameWidth = ImGui.CalcTextSize(CurrentSession?.DisplayName ?? "Guest").X;
        var verifiedWidth = CurrentSession is { IsGuest: false } ? ImGui.GetFontSize() + Ui(5) : 0;
        // Include avatar, name gap, verification mark and trailing breathing room.
        return MathF.Max(96, (nameWidth + verifiedWidth) / Ui(1) + (CurrentSession is null ? 16 : 56));
    }

    private void DrawSharedHeaderIdentity(Vector2 origin)
    {
        var session = CurrentSession;
        var profileWidth = sharedHeaderLayout.ProfileWidth;
        var avatarSlot = Ui(32);
        var avatar = Ui(36);
        // Grow around the existing centre so the name, counters and action bar
        // keep their alignment and retain a small gap around the avatar.
        ImGui.SetCursorScreenPos(origin + new Vector2((avatarSlot - avatar) * .5f));
        if (session is not null)
        {
            DrawAvatarChip(session.AvatarIcon, session.AvatarColorHex, avatar, session.AvatarImageUrl);
            DrawHeaderProfileLink("avatar");
        }
        var text = origin + new Vector2(session is null ? 0 : avatarSlot + Ui(8), Ui(2));
        ImGui.SetCursorScreenPos(text);
        var name = session?.DisplayName ?? "Guest";
        using (UiFonts.PushRelative(1f, emphasis: true))
            DrawUsername(session?.AccountId, FitPartyTextToWidth(name, Ui(profileWidth) - (text.X - origin.X) - Ui(8)));
        DrawHeaderProfileLink("name");
        ImGui.SetCursorScreenPos(text + UiVec(0, 18));
        using (UiFonts.PushRelative(.83f))
        {
            DrawHeaderAccountStatus(session is not null);
            ImGui.SameLine(0, Ui(5));
            var status = session is null ? "Guest" : stream.ConnectionState == RealtimeConnectionState.Connected
                ? "Online" : stream.ConnectionStatusText;
            ImGui.TextColored(MutedText, FitPartyTextToWidth(status,
                MathF.Max(Ui(20), origin.X + Ui(profileWidth - 8) - ImGui.GetCursorScreenPos().X)));
        }
        if (session is null) DrawGuestHeaderSignInButton();
    }

    private void DrawSharedHeaderOnlineCount()
    {
        var counts = sharedHeaderOrigin + UiVec(sharedHeaderLayout.CountsLeft, sharedHeaderLayout.CountsY);
        ImGui.SetCursorScreenPos(counts);
        var users = usersOnlineCount;
        DrawHeaderOnlineCount("users", FontAwesomeIcon.Users,
            $"{users} {(users == 1 ? "user" : "users")} online", sharedHeaderLayout.UsersWidth);
    }

    private void DrawSharedHeaderSocialCounts()
    {
        ImGui.SetCursorScreenPos(sharedHeaderOrigin + UiVec(sharedHeaderLayout.FriendsLeft, sharedHeaderLayout.SocialY));
        var online = friends.Count(f => f.Online);
        if (DrawHeaderSocialCount("friends", FontAwesomeIcon.UserFriends, online,
                sharedHeaderLayout.FriendsWidth, "Friends online"))
            currentPage = HomePage.Friends;
        ImGui.SetCursorScreenPos(sharedHeaderOrigin + UiVec(sharedHeaderLayout.ChatsLeft, sharedHeaderLayout.SocialY));
        if (DrawHeaderSocialCount("chats", FontAwesomeIcon.CommentDots, UnreadChatNotificationCount(),
                sharedHeaderLayout.ChatsWidth, "Unread messages"))
        {
            conversationsDirty = true;
            OpenUnreadChatsHub();
        }
    }

    private bool DrawHeaderSocialCount(string id, FontAwesomeIcon icon, int count, float width, string tooltip)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = UiVec(width, 28);
        var clicked = ImGui.InvisibleButton("##headerCount" + id, size);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(hovered ? CardBgHover : FrameBg), Ui(6));
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(Hex(0x263D61)), Ui(6));
        var glyph = icon.ToIconString();
        float iconWidth;
        using (UiFonts.PushIcon()) iconWidth = ImGui.CalcTextSize(glyph).X;
        var gap = Ui(5);
        var label = count > 99 ? "99+" : count.ToString();
        var measure = ImGui.CalcTextSize(label);
        var fit = MathF.Min(1f, MathF.Max(1f, size.X - Ui(12) - iconWidth - gap) / MathF.Max(1f, measure.X));
        var groupWidth = iconWidth + gap + measure.X * fit;
        var groupLeft = (size.X - groupWidth) * .5f;
        UiFonts.DrawCenteredIcon(draw, ImGui.GetFontSize(),
            origin + new Vector2(groupLeft + iconWidth * .5f, size.Y * .5f),
            ImGui.GetColorU32(Hex(0xBDC9E5)), glyph);
        UiFonts.DrawFittedText(draw, ImGui.GetFontSize() * fit,
            origin + new Vector2(groupLeft + iconWidth + gap, (size.Y - measure.Y * fit) * .5f),
            ImGui.GetColorU32(Hex(0xD5DCE8)), label);
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            UiFonts.SetTooltip($"{tooltip}: {count}");
        }
        return clicked;
    }

    private bool DrawHeaderOnlineCount(string id, FontAwesomeIcon icon, string label, float width)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = UiVec(width, 28);
        var clicked = ImGui.InvisibleButton("##headerCount" + id, size);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        UiFonts.DrawCenteredIcon(draw, ImGui.GetFontSize(), origin + new Vector2(Ui(13), size.Y * .5f),
            ImGui.GetColorU32(id == "users" ? AccentHover : Hex(0xD5DCE8)), icon.ToIconString());
        var textWidth = MathF.Max(1f, size.X - Ui(36));
        var measure = ImGui.CalcTextSize(label);
        var fit = MathF.Min(1f, textWidth / MathF.Max(1f, measure.X));
        // This is an exact fit, not a grouped relative font request: rounding
        // to the next font group can otherwise turn "online" into an ellipsis.
        UiFonts.DrawFittedText(draw, ImGui.GetFontSize() * fit,
            origin + new Vector2(Ui(32), (size.Y - measure.Y * fit) * .5f),
            ImGui.GetColorU32(Hex(0xBDC9E5)), label);
        if (hovered && id == "friends") ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return clicked;
    }
}

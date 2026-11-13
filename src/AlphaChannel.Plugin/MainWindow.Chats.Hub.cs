using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // Keep routing to the existing page IDs so presence, read receipts, overlays and
    // notification suppression continue to describe the conversation actually visible.
    private int chatsHubTab;
    private bool chatsHubDrawing, chatsHubListOpen, chatsHubUnreadOnly;
    private string? chatsHubDirectId, chatsHubGroupId;
    private string chatsHubSearch = "";
    private readonly Dictionary<string, (string Text, MessageDto? Reply)> chatsHubDrafts = [];
    private PublicGroupListing[] chatsDirectory = [];
    private bool chatsDirectoryLoading;
    private DateTime chatsDirectoryFetched, chatsDirectoryRetry;
    private string? chatsDirectoryAccount, chatsDirectoryError;
    private string chatsDirectorySearch = "";
    private int chatsDirectoryCategory, chatsDirectorySort;
    private PublicGroupListing? chatsDirectoryDetail;
    private bool chatsDirectoryDetailOpen, chatsHubInvitationsOpen;
    private int chatsHubLastDrawFrame = -2;
    private bool chatsHubCompact;
    private Vector2 chatsHubBoundsMin, chatsHubBoundsSize;
    private readonly Dictionary<string, Vector2> chatsPopupAnchors = [];
    private static readonly string[] ChatCategories = ["Social", "Gaming", "Music", "Creative"];
    private int ChatsHubNotificationCount() => (int)Math.Min(int.MaxValue, (long)UnreadChatNotificationCount() + alphaChatUnreadMentions.Count + dmClient.Groups.State.Invitations.Length);

    private void SelectChatsHubTab(int tab)
    {
        if (openConversationId is { } old) {
            chatsHubDrafts[old] = (messageComposerInput, messageReplyTarget);
            if (openConversationIsGroup) chatsHubGroupId = old; else chatsHubDirectId = old;
        }
        chatsHubTab = tab; chatsHubSearch = ""; chatsHubListOpen = false;
        if (tab == 3 && CurrentSession is { } directorySession) RefreshChatsDirectory(directorySession);
        // Tab changes do not add extra entries to the page back stack.
        selectedPage = tab == 0 ? HomePage.AlphaChat : tab == 3 ? HomePage.Chats : HomePage.Messages;
        if (tab is 1 or 2 && CurrentSession is { } session) {
            var id = tab == 2 ? chatsHubGroupId : chatsHubDirectId;
            var chat = conversations.FirstOrDefault(c => c.ConversationId == id && c.IsGroup == (tab == 2));
            if (chat is null) chat = conversations.Where(c => c.IsGroup == (tab == 2) && !IsArchivedChat(c)).OrderByDescending(c => c.LastMessageAtUnix).FirstOrDefault();
            if (chat is not null) OpenConversation(session, chat.ConversationId, chat.Members, chat.IsGroup, ConversationTitle(chat));
        }
    }

    private void OpenUnreadChatsHub()
    {
        var chat = conversations.Where(c => c.UnreadCount > 0 && !IsArchivedChat(c)).OrderByDescending(c => c.LastMessageAtUnix).FirstOrDefault();
        chatsHubTab = chat?.IsGroup == true || chat is null && dmClient.Groups.State.Invitations.Length > 0 ? 2 : 1;
        currentPage = HomePage.Messages;
        if (chat is not null && CurrentSession is { } session) OpenConversation(session, chat.ConversationId, chat.Members, chat.IsGroup, ConversationTitle(chat));
        else SelectChatsHubTab(chatsHubTab);
    }

    private void DrawChatsHub()
    {
        if (currentPage == HomePage.AlphaChat) chatsHubTab = 0;
        else if (currentPage == HomePage.Messages && chatsHubTab is not (1 or 2)) chatsHubTab = openConversationIsGroup ? 2 : 1;
        var frame = ImGui.GetFrameCount();
        if (chatsHubTab == 3 && frame > chatsHubLastDrawFrame + 1 && CurrentSession is { } directorySession) RefreshChatsDirectory(directorySession);
        chatsHubLastDrawFrame = frame;
        if (conversationsDirty && !conversationsLoading && CurrentSession is { } signed && DateTime.UtcNow >= conversationsNextRetryUtc) RefreshConversations(signed.Token);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10)).Push(ImGuiStyleVar.FrameRounding, Ui(7))
            .Push(ImGuiStyleVar.ItemSpacing, UiVec(8, 8)).Push(ImGuiStyleVar.FrameBorderSize, Ui(1))
            .Push(ImGuiStyleVar.PopupRounding, Ui(10)).Push(ImGuiStyleVar.PopupBorderSize, Ui(1)).Push(ImGuiStyleVar.WindowPadding, UiVec(14,14));
        using var colors = ImRaii.PushColor(ImGuiCol.PopupBg, CardBg).Push(ImGuiCol.Border, AccentInk(.3f)).Push(ImGuiCol.FrameBg, CardBg);
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X; var part = width / 4;
        chatsHubBoundsMin = origin; chatsHubBoundsSize = ImGui.GetContentRegionAvail();
        string[] labels = ["Alpha Chat", "Messages", "Groups", "Discover"];
        FontAwesomeIcon[] icons = [FontAwesomeIcon.Comments, FontAwesomeIcon.Envelope, FontAwesomeIcon.Users, FontAwesomeIcon.Compass];
        for (var i = 0; i < 4; i++) {
            ImGui.SetCursorScreenPos(origin + new Vector2(part * i, 0));
            var count = i == 0 ? alphaChatUnreadMentions.Count : i == 3 ? 0 : conversations.Where(c => c.IsGroup == (i == 2) && !IsArchivedChat(c)).Sum(c => (long)Math.Max(0, c.UnreadCount)) + (i == 2 ? dmClient.Groups.State.Invitations.Length : 0);
            var labelAlphaUnread = i == 0 && count == 0 && (alphaChatUnread.Count > 0 || alphaChat.BackgroundUnread);
            var label = labels[i] + (count > 0 ? $" {(count > 99 ? "99+" : count)}" : "");
            if (i == 0 && labelAlphaUnread) label += " •";
            if (DrawChatsHubTab("hubTab" + i, label, icons[i], part, chatsHubTab == i) && chatsHubTab != i) SelectChatsHubTab(i);
        }
        ImGui.GetWindowDrawList().AddLine(origin + UiVec(0,39), origin + new Vector2(width,Ui(39)), ImGui.GetColorU32(AccentInk(.2f)));
        ImGui.SetCursorScreenPos(origin + UiVec(0, 48));
        if (chatsHubTab == 0) { DrawAlphaChatPage(); return; }
        if (CurrentSession is not { } session) { DrawPlainEmpty("Sign in to view your chats.", "Open Settings", () => currentPage = HomePage.Settings); return; }
        if (chatsHubTab == 3) { DrawChatsDirectory(session); return; }
        var groups = chatsHubTab == 2;
        var compact = width < Ui(900); var railWidth = compact ? Ui(66) : Ui(260);
        chatsHubCompact = compact;
        var available = ImGui.GetContentRegionAvail();
        using (ImRaii.PushColor(ImGuiCol.ChildBg, FrameBg))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(8, 8)))
        using (var rail = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##hubConversationRail", new Vector2(railWidth, available.Y), false, ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        if (rail) DrawChatsHubList(session, groups, compact);
        ImGui.SetCursorScreenPos(origin + new Vector2(railWidth + Ui(10), Ui(48)));
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
        using (var thread = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##hubConversationThread", new Vector2(width - railWidth - Ui(10), available.Y), false)))
        if (thread) {
            if (groups && dmClient.Groups.State.Invitations.Length > 0) {
                if (AlphaChatButton("hubInvitations", $"{dmClient.Groups.State.Invitations.Length} group invitations", FontAwesomeIcon.Envelope, ImGui.GetContentRegionAvail().X, Ui(34))) chatsHubInvitationsOpen = !chatsHubInvitationsOpen;
                if (chatsHubInvitationsOpen) DrawCloudGroupInvitations(session);
            }
            if (openConversationId is { } id && openConversationIsGroup == groups && conversations.Any(c => c.ConversationId == id)) {
                chatsHubDrawing = true;
                try { DrawModernDmThread(session, id); } finally { chatsHubDrawing = false; }
            }
            else {
                DrawSocialHeading(groups ? "Your groups" : "Your messages");
                DrawSocialHelperWrapped(groups ? "Choose a group, create one, or find a public group in Discover." : "Choose a conversation or start a new chat with a friend.");
                if (AlphaChatButton("hubEmptyCreate", groups ? "Create group" : "New direct chat", FontAwesomeIcon.Plus, Ui(180), primary: true)) OpenHubCreation(session, groups);
                if (!dmClient.Groups.Ready && groups) DrawSocialHelperWrapped(dmClient.Groups.Status);
            }
        }
    }

    private void DrawChatsHubList(CharacterSession session, bool groups, bool compact)
    {
        var width = ImGui.GetContentRegionAvail().X;
        if (compact) {
            if (AlphaChatButton("hubRailList", "", FontAwesomeIcon.Search, width, Ui(36))) { chatsPopupAnchors["##hubNamedList"] = ImGui.GetItemRectMax(); chatsHubListOpen = true; }
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Search and browse " + (groups ? "groups" : "messages"));
        }
        else {
            ImGui.SetNextItemWidth(width - Ui(42));
            using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(Ui(10),(Ui(36)-ImGui.GetFontSize())*.5f))) ImGui.InputTextWithHint("##hubSearch", groups ? "Search groups…" : "Search messages…", ref chatsHubSearch, 100);
            ImGui.SameLine(0,Ui(6));
            if (AlphaChatButton("hubRailCreate", "", FontAwesomeIcon.Plus, Ui(36), Ui(36))) OpenHubCreation(session, groups);
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip(groups ? "Create group" : "New direct chat");
            DrawChatsUnreadFilter(width);
        }
        var matches = HubChatMatches(groups);
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
        using (var list = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##hubScrollableConversations", new Vector2(width, -Ui(compact ? 86 : 42)), false))) if (list) {
            foreach (var chat in matches) DrawHubConversationRow(session, chat, ImGui.GetContentRegionAvail().X, compact);
            if (!compact && matches.Length == 0) DrawSocialHelperWrapped(conversationsLoading ? "Loading…" : "No matching chats.");
        }
        if (compact) {
            if (AlphaChatButton("hubRailCreate", "", FontAwesomeIcon.Plus, width, Ui(36))) OpenHubCreation(session, groups);
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip(groups ? "Create a private or public group" : "New direct chat");
        }
        if (AlphaChatButton("hubRailArchive", compact ? "" : chatsPageArchived ? "Back to active" : "Archived chats", chatsPageArchived ? FontAwesomeIcon.ArrowLeft : FontAwesomeIcon.Archive, width, Ui(30))) chatsPageArchived = !chatsPageArchived;
        if (compact && ImGui.IsItemHovered()) UiFonts.SetTooltip(chatsPageArchived ? "Back to active chats" : "Archived chats");
        if (chatsHubListOpen) {
            ImGui.OpenPopup("##hubNamedList"); chatsHubListOpen = false;
        }
        PrepareChatsPopup("##hubNamedList", UiVec(320, 420));
        using (var popup = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Popup("##hubNamedList"))) if (popup) {
            DrawSocialHeading(groups ? "Your groups" : "Your messages");
            ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##hubPopupSearch", groups ? "Search groups…" : "Search messages…", ref chatsHubSearch, 100);
            DrawChatsUnreadFilter(ImGui.GetContentRegionAvail().X);
            using (var list = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##hubPopupRows", Vector2.Zero, false))) if (list)
                foreach (var chat in HubChatMatches(groups)) DrawHubConversationRow(session, chat, ImGui.GetContentRegionAvail().X, false, true);
        }
    }

    private bool DrawChatsHubTab(string id, string label, FontAwesomeIcon icon, float width, bool selected)
    {
        var origin = ImGui.GetCursorScreenPos(); var size = new Vector2(width,Ui(38)); var draw = ImGui.GetWindowDrawList();
        var clicked = ImGui.InvisibleButton("##" + id, size); var hovered = ImGui.IsItemHovered();
        if (hovered || selected) draw.AddRectFilled(origin + UiVec(2,0), origin + size - UiVec(2,0), ImGui.GetColorU32(AccentInk(selected ? .12f : .07f)), Ui(6));
        var textSize = ImGui.CalcTextSize(label); var iconSize = Ui(17); var total = textSize.X + iconSize + Ui(8);
        var x = origin.X + (width-total)*.5f;
        UiFonts.DrawCenteredIcon(draw,iconSize,new Vector2(x+iconSize*.5f,origin.Y+Ui(19)),ImGui.GetColorU32(selected ? AccentHover : MutedText),icon.ToIconString());
        draw.AddText(new Vector2(x+iconSize+Ui(8),origin.Y+(Ui(38)-textSize.Y)*.5f),ImGui.GetColorU32(selected ? Vector4.One : MutedText),label);
        if (selected) draw.AddRectFilled(origin+UiVec(12,36),origin+new Vector2(width-Ui(12),Ui(39)),ImGui.GetColorU32(AccentHover),Ui(1));
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return clicked;
    }

    private void DrawChatsUnreadFilter(float width)
    {
        var part = (width-Ui(6))*.5f;
        if (AlphaChatButton("hubFilterAll","All",FontAwesomeIcon.Comments,part,Ui(28),primary:!chatsHubUnreadOnly)) chatsHubUnreadOnly=false;
        ImGui.SameLine(0,Ui(6));
        if (AlphaChatButton("hubFilterUnread","Unread",FontAwesomeIcon.Envelope,part,Ui(28),primary:chatsHubUnreadOnly)) chatsHubUnreadOnly=true;
    }

    private void OpenChatsPopup(string id)
    {
        chatsPopupAnchors[id] = new Vector2(ImGui.GetItemRectMin().X,ImGui.GetItemRectMax().Y+Ui(6));
        ImGui.OpenPopup(id);
    }
    private void PrepareChatsPopup(string id, Vector2 desired)
    {
        if (!ImGui.IsPopupOpen(id)) return;
        var root = ImGuiP.GetCurrentWindow().RootWindow;
        var minimum = chatsHubBoundsSize.X > 0 ? chatsHubBoundsMin : root.Pos;
        var available = chatsHubBoundsSize.X > 0 ? chatsHubBoundsSize : root.Size;
        var size = Vector2.Min(desired, available - UiVec(16,16));
        var min = minimum + UiVec(8,8); var max = Vector2.Max(min,minimum+available-size-UiVec(8,8));
        var anchor = chatsPopupAnchors.GetValueOrDefault(id,min);
        ImGui.SetNextWindowPos(Vector2.Clamp(anchor,min,max),ImGuiCond.Always);
        ImGui.SetNextWindowSize(size,ImGuiCond.Always);
    }

    private ConversationSummaryDto[] HubChatMatches(bool groups) => conversations.Where(c => c.IsGroup == groups && IsArchivedChat(c) == chatsPageArchived)
        .Where(c => !chatsHubUnreadOnly || c.UnreadCount > 0)
        .Where(c => string.IsNullOrWhiteSpace(chatsHubSearch) || ConversationTitle(c).Contains(chatsHubSearch.Trim(), StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(c => c.LastMessageAtUnix ?? 0).ToArray();

    private void DrawHubConversationRow(CharacterSession session, ConversationSummaryDto chat, float width, bool compact, bool popup = false)
    {
        using var id = ImRaii.PushId(chat.ConversationId);
        var origin = ImGui.GetCursorScreenPos(); var height = Ui(compact ? 54 : 64); var draw = ImGui.GetWindowDrawList();
        var clicked = ImGui.InvisibleButton("##hubOpenConversation", new Vector2(width, height)); var hovered = ImGui.IsItemHovered();
        if (chat.ConversationId == openConversationId || hovered) draw.AddRectFilled(origin, origin + new Vector2(width, height), ImGui.GetColorU32(AccentInk(chat.ConversationId == openConversationId ? .25f : .12f)), Ui(8));
        var avatarOrigin = origin + new Vector2(compact ? (width - Ui(40)) / 2 : Ui(6), Ui(compact ? 8 : 12));
        var member = chat.Members.FirstOrDefault();
        if (!chat.IsGroup && member is not null) {
            EnsurePartyAvatarLoaded(member.AccountId, member.DisplayName); partyAvatarCache.TryGetValue(member.AccountId, out var avatar);
            DrawAvatarAt(avatarOrigin, avatar?.AvatarIcon, avatar?.AvatarColorHex, Ui(40), avatar?.AvatarImageUrl);
        }
        else {
            DrawHubGroupAvatar(chat.ConversationId, ConversationTitle(chat), avatarOrigin);
            UiFonts.DrawCenteredIcon(draw, Ui(11), avatarOrigin + UiVec(35, 36), ImGui.GetColorU32(AccentHover), (dmClient.Groups.IsPublic(chat.ConversationId) ? FontAwesomeIcon.Globe : FontAwesomeIcon.Lock).ToIconString());
        }
        if (chat.UnreadCount > 0) DrawUnreadMessageBadge(origin + new Vector2(compact ? width - Ui(6) : width - Ui(14), Ui(12)), chat.UnreadCount);
        if (!compact) {
            draw.AddText(origin + UiVec(56, 12), ImGui.GetColorU32(chat.UnreadCount > 0 ? Vector4.One : AccentHover), FitPartyTextToWidth(ConversationTitle(chat), width - Ui(82)));
            var preview = chatsPagePreviews.TryGetValue(chat.ConversationId, out var cached) ? cached.Text.Replace('\n', ' ').Replace('\r', ' ') : chat.LastMessageAtUnix is null ? "No messages yet" : "Open to view messages";
            using (UiFonts.PushRelative(.85f)) draw.AddText(origin + UiVec(56, 36), ImGui.GetColorU32(MutedText), FitPartyTextToWidth(preview, width - Ui(66)));
        }
        if (hovered && compact) UiFonts.SetTooltip(ConversationTitle(chat) + (chat.UnreadCount > 0 ? $" · {chat.UnreadCount} unread" : ""));
        if (clicked) { if (openConversationId != chat.ConversationId) OpenConversation(session, chat.ConversationId, chat.Members, chat.IsGroup, ConversationTitle(chat)); if (popup) ImGui.CloseCurrentPopup(); }
        ImGui.SetCursorScreenPos(origin + new Vector2(0, height + Ui(4)));
    }

    private void OpenHubCreation(CharacterSession session, bool group)
    {
        OpenChatsCreation(session, group);
        hubGroupCreationType = -1; hubGroupDescription = ""; hubGroupCategory = 0;
    }

    private void DrawHubGroupAvatar(string id, string title, Vector2 origin)
    {
        var group=dmClient.Groups.State.Groups.FirstOrDefault(g=>g.Id==GroupChatClient.Raw(id));
        if(group?.AvatarIcon is { } icon) { DrawAvatarAt(origin,icon,group.AvatarColorHex,Ui(40)); return; }
        uint hash = 2166136261; foreach (var c in id) hash = unchecked((hash ^ c) * 16777619);
        Vector4[] colors = [new(.2f,.35f,.65f,1),new(.4f,.25f,.6f,1),new(.08f,.45f,.45f,1),new(.55f,.3f,.42f,1),new(.3f,.4f,.25f,1)];
        var draw = ImGui.GetWindowDrawList(); draw.AddCircleFilled(origin + UiVec(20,20), Ui(20), ImGui.GetColorU32(colors[hash % colors.Length]));
        var initials = string.Concat(title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(x => char.ToUpperInvariant(x[0])));
        if (initials.Length == 0) initials = "G";
        draw.AddText(origin + (UiVec(40,40) - ImGui.CalcTextSize(initials)) * .5f, ImGui.GetColorU32(Vector4.One), initials);
    }

    private void DrawHubConversationToolbar(CharacterSession session, string conversation)
    {
        var width = ImGui.GetContentRegionAvail().X; var origin = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList(); var publicGroup = dmClient.Groups.IsPublic(conversation);
        draw.AddRectFilled(origin, origin + new Vector2(width, Ui(62)), ImGui.GetColorU32(FrameBg), Ui(9));
        if (!openConversationIsGroup && openConversationMembers.FirstOrDefault() is { } person) {
            EnsurePartyAvatarLoaded(person.AccountId, person.DisplayName); partyAvatarCache.TryGetValue(person.AccountId, out var avatar);
            DrawAvatarAt(origin + UiVec(12, 11), avatar?.AvatarIcon, avatar?.AvatarColorHex, Ui(40), avatar?.AvatarImageUrl);
        }
        else {
            DrawHubGroupAvatar(conversation, openConversationTitle, origin + UiVec(12,11));
        }
        ImGui.SetCursorScreenPos(origin + UiVec(64, 9));
        var titleWidth = MathF.Max(Ui(60), width - Ui(196));
        using (UiFonts.PushRelative(1.05f, emphasis: true)) ImGui.TextUnformatted(FitPartyTextToWidth(openConversationTitle, titleWidth));
        ImGui.SetCursorScreenPos(origin + UiVec(64, 36));
        var people = openConversationMembers.Length + 1;
        var subtitle = messageEncryptionReady == true ? openConversationIsGroup ? $"{(publicGroup ? "Public" : "Private")} group · {people} {(people == 1 ? "member" : "members")}" : "Private conversation" : "Unlocking chat…";
        var groupSettings = publicGroup ? dmClient.Groups.State.Groups.FirstOrDefault(g => GroupChatClient.Prefix + g.Id == conversation) : null;
        if (groupSettings is { SlowSeconds: > 0 }) subtitle += $" · Slow mode {groupSettings.SlowSeconds}s";
        using (UiFonts.PushRelative(.85f)) ImGui.TextColored(MutedText, FitPartyTextToWidth(subtitle, titleWidth));
        if (ImGui.IsItemHovered() && groupSettings is { SlowSeconds: > 0 }) UiFonts.SetTooltip($"Slow mode: one message every {groupSettings.SlowSeconds} seconds.");
        if (ImGui.IsItemHovered() && messageEncryptionReady != true) UiFonts.SetTooltip(messageEncryptionError ?? "Open the chat menu for encryption status and recovery.");
        ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(chatsHubCompact ? 114 : 80), Ui(13)));
        if (AlphaChatButton("hubToolbarPeople", "", openConversationIsGroup ? FontAwesomeIcon.Users : FontAwesomeIcon.User, Ui(30), Ui(34))) {
            if (openConversationIsGroup) dmRosterOpen = true;
            else if (openConversationMembers.FirstOrDefault() is { } member) OpenProfilePage(session, member.AccountId, member.DisplayName);
        }
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip(openConversationIsGroup ? "View group members" : "View profile");
        ImGui.SameLine(0, Ui(4));
        if (chatsHubCompact) {
            if (AlphaChatButton("hubToolbarList", "", FontAwesomeIcon.List, Ui(30), Ui(34))) { chatsPopupAnchors["##hubNamedList"] = new Vector2(origin.X+width-Ui(320),origin.Y+Ui(64)); chatsHubListOpen = true; }
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Browse conversations");
            ImGui.SameLine(0, Ui(4));
        }
        if (AlphaChatButton("hubToolbarMore", "", FontAwesomeIcon.EllipsisV, Ui(30), Ui(34))) OpenChatsPopup("##hubThreadActions");
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Chat options");
        var owner = dmClient.Groups.State.Groups.Any(g => g.Id == GroupChatClient.Raw(conversation) && g.OwnerId == session.AccountId);
        PrepareChatsPopup("##hubThreadActions", UiVec(350,GroupChatClient.IsGroup(conversation)?owner ? publicGroup ? 340 : 400 : 280:220));
        using (var popup = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Popup("##hubThreadActions"))) if (popup) {
            DrawSocialHeading("Chat options");
            DrawSocialHelperWrapped(publicGroup ? "Public group · Server-managed encryption" : "Private chat · End-to-end encrypted");
            if (groupSettings?.JoinLocked == true) DrawSocialHelperWrapped("Closed to new members.");
            if (groupSettings is { SlowSeconds: > 0 }) DrawSocialHelperWrapped($"Slow mode: one message every {groupSettings.SlowSeconds} seconds.");
            ImGui.Separator();
            if (!publicGroup && ImGui.CollapsingHeader("Encryption details")) {
                DrawModernDmSecurityStatus(session, conversation);
                if (GroupChatClient.IsGroup(conversation) && dmClient.Groups.VerificationCode is { } code) ImGui.TextUnformatted("Verification code: " + code);
            }
            using (UiDisabled.Scope(!CanInviteToCurrentWatchParty() || messageEncryptionReady != true || IsOpenDmBlocked(session)))
                if (AlphaChatButton("hubMenuShare","Share Watch Party",FontAwesomeIcon.BroadcastTower,ImGui.GetContentRegionAvail().X,Ui(32))) { SendEncryptedDm(session, conversation, CreateWatchPartyInviteMessage(), showInFeed: true); ImGui.CloseCurrentPopup(); }
            if (AlphaChatButton("hubMenuArchive",conversations.Any(c => c.ConversationId == conversation && IsArchivedChat(c)) ? "Restore chat" : "Archive chat",FontAwesomeIcon.Archive,ImGui.GetContentRegionAvail().X,Ui(32))) {
                var summary = conversations.FirstOrDefault(c => c.ConversationId == conversation);
                if (summary is not null) { if (IsArchivedChat(summary)) RestoreArchivedChat(session.AccountId, conversation); else ArchiveChat(summary); }
                ImGui.CloseCurrentPopup();
            }
            if (GroupChatClient.IsGroup(conversation)) { ImGui.Separator(); DrawCloudGroupControls(session, conversation); }
        }
        ImGui.SetCursorScreenPos(origin + UiVec(0, 70));
    }
}

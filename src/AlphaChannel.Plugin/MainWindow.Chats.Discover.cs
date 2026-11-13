using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string? chatsDirectoryJoinHintGroup;
    private double chatsDirectoryJoinHintUntil;

    private void RefreshChatsDirectory(CharacterSession session, bool force = false)
    {
        if (chatsDirectoryLoading || !dmClient.Groups.Ready || DateTime.UtcNow < chatsDirectoryRetry ||
            !force && chatsDirectoryAccount == session.AccountId && DateTime.UtcNow - chatsDirectoryFetched < TimeSpan.FromMinutes(2)) return;
        chatsDirectoryLoading = true; chatsDirectoryError = null;
        chatsDirectoryRetry = DateTime.UtcNow.AddSeconds(5);
        _ = Task.Run(async () => {
            PublicGroupListing[]? result = null; string? error = null;
            try { result = await dmClient.Groups.DirectoryAsync(session.Token); }
            catch (Exception e) { error = e is InvalidOperationException ? e.Message : "Couldn't load public groups. Please try again."; }
            await Plugin.Framework.RunOnFrameworkThread(() => {
                if (!IsCurrentMessageSession(session)) return;
                chatsDirectoryLoading = false; chatsDirectoryError = error;
                if (result is not null) { chatsDirectory = result; chatsDirectoryFetched = DateTime.UtcNow; chatsDirectoryAccount = session.AccountId; }
                else chatsDirectoryRetry = DateTime.UtcNow.AddSeconds(30);
            });
        });
    }

    private void DrawChatsDirectory(CharacterSession session)
    {
        // Fetch on entry or explicit refresh, not on an interval while the page is open.
        if (chatsDirectoryAccount != session.AccountId) RefreshChatsDirectory(session);
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        DrawSocialHeading("Find your people", 1.3f);
        DrawSocialHelperWrapped("Public groups, shared interests, new friends.");
        ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(196), 0));
        if (AlphaChatButton("discoverCreate", "Create group", FontAwesomeIcon.Plus, Ui(152), primary: true)) OpenHubCreation(session, true);
        ImGui.SameLine(0, Ui(6));
        using (UiDisabled.Scope(chatsDirectoryLoading || DateTime.UtcNow < chatsDirectoryRetry))
            if (AlphaChatButton("discoverRefresh", "", FontAwesomeIcon.SyncAlt, Ui(36))) RefreshChatsDirectory(session, true);
        ImGui.SetCursorScreenPos(origin + UiVec(0, 60));
        ImGui.SetNextItemWidth(-1);
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(Ui(12),(Ui(40)-ImGui.GetFontSize())*.5f))) ImGui.InputTextWithHint("##discoverSearch", "Search public groups…", ref chatsDirectorySearch, 120);
        string[] filters = ["All categories", "Social", "Gaming", "Music", "Creative"];
        var filterOrigin=ImGui.GetCursorScreenPos();
        if (width >= Ui(630)) {
            for (var i=0;i<filters.Length;i++) {
                if (i>0) ImGui.SameLine(0,Ui(6));
                var label=i==0?"All":filters[i]; var chipWidth=ImGui.CalcTextSize(label).X+Ui(24);
                using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding,UiVec(12,6)))
                using (ImRaii.PushColor(ImGuiCol.Button,chatsDirectoryCategory==i?AccentInk(.4f):FrameBg).Push(ImGuiCol.ButtonHovered,CardBgHover))
                    if (ImGui.Button(label+"##directoryCategory"+i,new Vector2(chipWidth,Ui(32)))) chatsDirectoryCategory=i;
            }
            ImGui.SetCursorScreenPos(filterOrigin+new Vector2(width-Ui(160),0));
        }
        else { ImGui.SetNextItemWidth(Ui(160)); ImGui.Combo("##discoverCategory", ref chatsDirectoryCategory, filters, filters.Length); ImGui.SameLine(); }
        ImGui.SetNextItemWidth(Ui(160));
        string[] sorting = ["Newest groups", "Most members", "Name"];
        ImGui.Combo("##discoverSort", ref chatsDirectorySort, sorting, sorting.Length);
        if (!dmClient.Groups.Ready) DrawSocialHelperWrapped(dmClient.Groups.Status);
        if (chatsDirectoryError is { } error) ImGui.TextWrapped(error);
        if (chatsDirectoryLoading) ImGui.TextDisabled("Loading public groups…");
        using (var list = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##publicGroupCards", new Vector2(0, -Ui(28)), false))) if (list) {
            var matches = chatsDirectory.Where(g => !g.Closed && (chatsDirectoryCategory == 0 || g.Category == filters[chatsDirectoryCategory]))
                .Where(g => string.IsNullOrWhiteSpace(chatsDirectorySearch) || (g.Name + " " + g.Description).Contains(chatsDirectorySearch.Trim(), StringComparison.OrdinalIgnoreCase));
            matches = chatsDirectorySort switch { 1 => matches.OrderByDescending(g => g.MemberCount), 2 => matches.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase), _ => matches.OrderByDescending(g => g.CreatedAt) };
            var items = matches.ToArray(); var columns = width >= Ui(1050) ? 2 : 1; var cardWidth = (ImGui.GetContentRegionAvail().X - Ui(12) * (columns - 1)) / columns;
            for (var i = 0; i < items.Length; i++) {
                if (columns == 2 && i % 2 == 1) ImGui.SameLine(0, Ui(12));
                DrawPublicGroupCard(session, items[i], cardWidth);
            }
            if (!chatsDirectoryLoading && items.Length == 0) DrawSocialHelperWrapped("No public groups found. Create one and start the conversation.");
        }
        using (UiFonts.PushRelative(.85f)) ImGui.TextColored(MutedText, "Public groups are open to signed-in Alpha Channel users.");
        DrawPublicGroupDetails(session);
    }

    private void DrawPublicGroupCard(CharacterSession session, PublicGroupListing group, float width)
    {
        using var id = ImRaii.PushId(group.Id);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(14, 12));
        using var color = ImRaii.PushColor(ImGuiCol.ChildBg, FrameBg).Push(ImGuiCol.Border, AccentInk(.3f));
        using var child = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##directoryCard", new Vector2(width, Ui(132)), true, ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar));
        if (!child) return;
        var origin = ImGui.GetCursorScreenPos(); var available = ImGui.GetContentRegionAvail().X;
        var icon = group.Category switch { "Gaming" => FontAwesomeIcon.Gamepad, "Music" => FontAwesomeIcon.Music, "Creative" => FontAwesomeIcon.Camera, _ => FontAwesomeIcon.Comments };
        var draw = ImGui.GetWindowDrawList();
        var ownSummary=dmClient.Groups.State.Groups.FirstOrDefault(g=>g.Id==group.Id);
        var avatarIcon=ownSummary?.AvatarIcon??group.AvatarIcon;
        if(avatarIcon is not null) DrawAvatarAt(origin,avatarIcon,ownSummary?.AvatarColorHex??group.AvatarColorHex,Ui(48));
        else {
            draw.AddRectFilled(origin, origin + UiVec(48, 48), ImGui.GetColorU32(AccentInk(.22f)), Ui(10));
            UiFonts.DrawCenteredIcon(draw, Ui(25), origin + UiVec(24, 24), ImGui.GetColorU32(AccentHover), icon.ToIconString());
        }
        ImGui.SetCursorScreenPos(origin + UiVec(62, 0));
        using (UiFonts.PushRelative(1f, emphasis: true)) ImGui.TextUnformatted(FitPartyTextToWidth(group.Name, available - Ui(64)));
        ImGui.SetCursorScreenPos(origin + UiVec(62, 24));
        using (UiFonts.PushRelative(.85f)) using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + available - Ui(64))) {
            var description = string.IsNullOrWhiteSpace(group.Description) ? "Join the conversation." : group.Description.Replace('\r', ' ').Replace('\n', ' ');
            ImGui.TextColored(MutedText, FitPartyTextToWidth(description, (available - Ui(64)) * 1.8f));
        }
        ImGui.SetCursorScreenPos(origin + UiVec(62, 60));
        var admin = PublicGroupAdminName(group);
        using (UiFonts.PushRelative(.85f)) ImGui.TextColored(MutedText, FitPartyTextToWidth($"Admin: {admin}", available - Ui(64)));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Group admin: {admin}");
        ImGui.SetCursorScreenPos(origin + UiVec(62, 86));
        using (UiFonts.PushRelative(.85f)) ImGui.TextColored(MutedText, FitPartyTextToWidth($"{group.Category} · {group.MemberCount} {(group.MemberCount==1?"member":"members")}" + (group.JoinLocked ? " · Joins locked" : ""), available - Ui(188)));
        var joined = dmClient.Groups.State.Groups.Any(g => g.Id == group.Id);
        ImGui.SetCursorScreenPos(origin + new Vector2(available - Ui(112), Ui(76)));
        var open = AlphaChatButton("directoryView", joined ? "Open chat" : "View group", joined ? FontAwesomeIcon.Comments : FontAwesomeIcon.ArrowRight, Ui(112), Ui(32), primary: !joined);
        var overButton = ImGui.IsItemHovered();
        var overCard = ImGui.IsWindowHovered() && !overButton;
        if (overCard) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (open || overCard && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
            if (joined) OpenPublicGroupConversation(session, group.Id);
            else {
                chatsPopupAnchors["##publicGroupDetails"]=new Vector2(chatsHubBoundsMin.X+(chatsHubBoundsSize.X-Ui(440))*.5f,chatsHubBoundsMin.Y+Ui(70));
                chatsDirectoryDetail = group; chatsDirectoryDetailOpen = true; cloudGroupError = null;
                chatsDirectoryJoinHintGroup = group.Id;
                chatsDirectoryJoinHintUntil = ImGui.GetTime() + 1.2;
            }
        }
    }

    private string PublicGroupAdminName(PublicGroupListing group)
    {
        var own = dmClient.Groups.State.Groups.FirstOrDefault(g => g.Id == group.Id);
        return own?.Members.FirstOrDefault(m => m.AccountId == own.OwnerId)?.DisplayName
            ?? group.OwnerDisplayName ?? "Unknown";
    }

    private void OpenPublicGroupConversation(CharacterSession session, string group)
    {
        MergeCloudGroupConversations();
        var chat = conversations.FirstOrDefault(c => c.ConversationId == GroupChatClient.Prefix + group);
        if (chat is null) return;
        OpenConversation(session, chat.ConversationId, chat.Members, true, ConversationTitle(chat)); currentPage = HomePage.Messages;
    }

    private void DrawPublicGroupDetails(CharacterSession session)
    {
        if (chatsDirectoryDetailOpen) { ImGui.OpenPopup("##publicGroupDetails"); chatsDirectoryDetailOpen = false; }
        PrepareChatsPopup("##publicGroupDetails",UiVec(440,320));
        using var popup = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Popup("##publicGroupDetails"));
        if (!popup || chatsDirectoryDetail is not { } group) return;
        DrawSocialHeading(group.Name); ImGui.TextColored(MutedText, $"{group.Category} · {group.MemberCount} {(group.MemberCount==1?"member":"members")}");
        ImGui.TextWrapped($"Admin: {PublicGroupAdminName(group)}");
        ImGui.TextWrapped(group.Description);
        if (group.JoinLocked) ImGui.TextWrapped("This group is closed to new members. Existing members can still chat.");
        if (group.SlowSeconds > 0) ImGui.TextWrapped($"Slow mode: one message every {group.SlowSeconds} seconds.");
        ImGui.Separator();
        ImGui.TextWrapped("Anyone signed in can join. Messages use server-managed encryption. You'll see new messages sent after you join.");
        if (cloudGroupError is { } error) ImGui.TextWrapped(error);
        using (UiDisabled.Scope(cloudGroupBusy || !dmClient.Groups.Ready || group.JoinLocked)) if (ImGui.Button(cloudGroupBusy ? "Joining…" : group.JoinLocked ? "Closed to new members" : "Join group", UiVec(-1, 36))) {
            var target = group.Id;
            RunCloudGroupAction(session, async () => {
                await dmClient.Groups.MembershipAsync(session.Token, target, "join");
                await Plugin.Framework.RunOnFrameworkThread(() => { if (IsCurrentMessageSession(session)) { chatsDirectoryDetail = null; OpenPublicGroupConversation(session, target); } });
            });
        }
        var hintRemaining = chatsDirectoryJoinHintUntil - ImGui.GetTime();
        if (chatsDirectoryJoinHintGroup == group.Id && hintRemaining > 0 && !group.JoinLocked && !cloudGroupBusy && dmClient.Groups.Ready)
        {
            // Draw over the existing control without changing its size or accepting a join on a card click.
            var pulse = (float)(.5 - .5 * Math.Cos((1.2 - hintRemaining) * Math.PI * 4));
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), ImGui.GetColorU32(AccentInk(.22f * pulse)), Ui(5));
            draw.AddRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), ImGui.GetColorU32(AccentInk(.9f * pulse)), Ui(5), ImDrawFlags.None, Ui(2));
        }
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
    }
}

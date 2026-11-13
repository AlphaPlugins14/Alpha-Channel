using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private int hubGroupCreationType = -1, hubGroupCategory;
    private string hubGroupDescription = "";
    private void DrawHubGroupCreationOverlay()
    {
        if (CurrentSession is not { } session || !ReferenceEquals(session, chatsCreationSession)) { CloseChatsCreation(); return; }
        CompleteFriendGroupCreation(session);
        if (!friendGroupSelectionMode) { chatsCreationOpen = false; return; }
        var busy = friendGroupCreationTask is not null;
        var parent = ImGui.GetWindowPos(); var size = ImGui.GetWindowSize();
        var desiredHeight = hubGroupCreationType < 0 ? 680 : hubGroupCreationType == 1 ? 600 : 660;
        var panelSize = new Vector2(MathF.Min(Ui(560), size.X - Ui(36)), MathF.Min(Ui(desiredHeight), size.Y - Ui(36)));
        var position = parent + (size - panelSize) * .5f;
        ImGui.SetNextWindowPos(parent, ImGuiCond.Always); ImGui.SetNextWindowSize(size, ImGuiCond.Always); ImGui.SetNextWindowBgAlpha(0);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##hubGroupCreateOverlay", flags)) { ImGui.End(); return; }
        ImGui.GetWindowDrawList().AddRectFilled(parent, parent + size, ImGui.GetColorU32(new Vector4(0, 0, 0, .6f)));
        if (!busy && ImGui.IsKeyPressed(ImGuiKey.Escape)) CloseChatsCreation();
        ImGui.SetCursorScreenPos(position);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14)).Push(ImGuiStyleVar.WindowPadding, UiVec(24, 20)).Push(ImGuiStyleVar.FrameRounding, Ui(8)).Push(ImGuiStyleVar.ItemSpacing, UiVec(8, 10)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, AccentInk(.55f)).Push(ImGuiCol.FrameBg, FrameBg))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##hubGroupCreatePanel", panelSize, true, ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar)))
        if (panel) {
            var origin = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
            DrawSocialHeading("Create a group", 1.25f);
            ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(30), 0));
            using (UiDisabled.Scope(busy)) if (AlphaChatTextAction("hubCreateClose", "", FontAwesomeIcon.Times, Vector4.One, Ui(30), Ui(30))) CloseChatsCreation();
            ImGui.SetCursorScreenPos(origin + UiVec(0, 40));
            if (hubGroupCreationType < 0) {
                DrawSocialHelperWrapped("Choose who can join your group.");
                ImGui.SetCursorScreenPos(origin + UiVec(0,76));
                // Keep the title and footer fixed; allow only the choices to scroll
                // if the game viewport is too short at a larger interface scale.
                using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
                using (var choices = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##hubGroupTypeChoices", new Vector2(width,
                    MathF.Max(Ui(150), position.Y + panelSize.Y - Ui(76) - ImGui.GetCursorScreenPos().Y)), false)))
                if (choices) {
                    var choicesOrigin = ImGui.GetCursorScreenPos(); var cardWidth = ImGui.GetContentRegionAvail().X;
                    if (DrawHubGroupTypeCard("private", "Private group", FontAwesomeIcon.UserFriends,
                        "Join by invitation from a friend.", "Only group members can read messages.", cardWidth, false)) hubGroupCreationType = 0;
                    ImGui.SetCursorScreenPos(choicesOrigin + UiVec(0,210));
                    if (DrawHubGroupTypeCard("public", "Public group", FontAwesomeIcon.Globe,
                        "Listed in Discover for anyone to join.", "Messages are encrypted, but managed by Alpha Channel's servers.", cardWidth, true)) hubGroupCreationType = 1;
                }
            }
            else {
                using (UiDisabled.Scope(busy)) if (AlphaChatButton("hubGroupChangeType", "Change type", FontAwesomeIcon.ArrowLeft, Ui(130), Ui(28))) hubGroupCreationType = -1;
                ImGui.SameLine(0,Ui(12)); ImGui.TextColored(AccentHover, hubGroupCreationType == 1 ? "Public group" : "Private group");
                ImGui.TextColored(MutedText,"Group name");
                ImGui.SetNextItemWidth(width); using (UiDisabled.Scope(busy)) ImGui.InputTextWithHint("##hubGroupName", "Give your group a name…", ref friendGroupNameInput, 48);
                using (UiDisabled.Scope(busy)) DrawCreateGroupAppearance(width);
                if (hubGroupCreationType == 1) {
                    DrawSocialHelperWrapped("Anyone can find and join this group. Messages use server-managed encryption. Direct image uploading is not allowed.");
                    ImGui.TextUnformatted("Description"); ImGui.SetNextItemWidth(width);
                    using (UiDisabled.Scope(busy)) ImGui.InputTextMultiline("##hubGroupDescription", ref hubGroupDescription, 240, new Vector2(width, Ui(84)));
                    ImGui.TextColored(MutedText,"Category");
                    ImGui.SetNextItemWidth(width); using (UiDisabled.Scope(busy)) ImGui.Combo("##hubGroupCategory", ref hubGroupCategory, ChatCategories, ChatCategories.Length);
                    DrawSocialHelperWrapped("New members see messages sent after they join. You can remove or ban members from the group menu.");
                }
                else {
                    DrawSocialHelperWrapped("Invite friends now or add them later. Fully end-to-end encrypted for extra privacy. Allows direct image uploading.");
                    if (!friendsLoading) RefreshFriendGroupKeys(session);
                    ImGui.SetNextItemWidth(width); ImGui.InputTextWithHint("##hubGroupFriendsSearch", "Search friends to invite…", ref chatsCreationSearch, 100);
                    ImGui.TextColored(MutedText, $"{friendGroupSelectedIds.Count} friends selected");
                    using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
                    using (var list = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##hubGroupFriends", new Vector2(width, MathF.Max(Ui(80), position.Y + panelSize.Y - Ui(96) - ImGui.GetCursorScreenPos().Y)), false))) if (list) {
                        if (friendsDirty && !friendsLoading) RefreshFriends(session.Token);
                        if (friendsLoading) ImGui.TextDisabled("Loading friends…");
                        using (UiDisabled.Scope(busy)) foreach (var friend in friends.Where(f => !AlphaChatBlocked(f.AccountId) && (string.IsNullOrWhiteSpace(chatsCreationSearch) || f.DisplayName.Contains(chatsCreationSearch.Trim(), StringComparison.OrdinalIgnoreCase))).OrderByDescending(f => f.Online).ThenBy(f => f.DisplayName,StringComparer.OrdinalIgnoreCase)) {
                            using var id = ImRaii.PushId(friend.AccountId); DrawFriendGroupSelectionCard(friend, ImGui.GetContentRegionAvail().X);
                        }
                    }
                }
                if (friendGroupError is { } error) ImGui.TextColored(Danger, error);
            }
            ImGui.SetCursorScreenPos(position + new Vector2(Ui(24), panelSize.Y - Ui(56)));
            using (UiDisabled.Scope(busy)) if (AlphaChatButton("hubCreateCancel", "Cancel", FontAwesomeIcon.Times, Ui(110))) CloseChatsCreation();
            if (hubGroupCreationType >= 0) {
                ImGui.SetCursorScreenPos(position + new Vector2(panelSize.X - Ui(192), panelSize.Y - Ui(56)));
                var keysReady = hubGroupCreationType == 1 || friendGroupSelectedIds.All(id => friendGroupKeyStates.TryGetValue(id, out var state) && state == FriendGroupKeyState.Ready);
                using (UiDisabled.Scope(busy || !dmClient.Groups.Ready || !keysReady || string.IsNullOrWhiteSpace(friendGroupNameInput)))
                    if (AlphaChatButton("hubCreateSubmit", busy ? "Creating…" : "Create group", FontAwesomeIcon.Users, Ui(168), primary: true)) {
                        friendGroupSubmittedName = friendGroupNameInput.Trim(); friendGroupError = null;
                        friendGroupSubmittedFriends = hubGroupCreationType == 1 ? [] : friends.Where(f => friendGroupSelectedIds.Contains(f.AccountId)).ToArray();
                        friendGroupCreationTask = hubGroupCreationType == 1
                            ? CreatePublicGroupForUi(session, friendGroupSubmittedName, hubGroupDescription.Trim(), ChatCategories[hubGroupCategory], hubGroupIcon, hubGroupColor)
                            : dmClient.Groups.CreateAsync(session.Token, friendGroupSubmittedFriends.Select(f => f.AccountId).ToArray(), friendGroupSubmittedName, hubGroupIcon, hubGroupColor);
                    }
            }
        }
        ImGui.End();
    }
    private async Task<string?> CreatePublicGroupForUi(CharacterSession session, string name, string description, string category, string icon, string color)
    {
        var id = await dmClient.Groups.CreatePublicAsync(session.Token, name, description, category, icon, color);
        await Plugin.Framework.RunOnFrameworkThread(() => { if (IsCurrentMessageSession(session)) chatsDirectoryAccount = null; });
        return id;
    }
    private bool DrawHubGroupTypeCard(string id, string title, FontAwesomeIcon icon, string access, string privacy, float width, bool isPublic)
    {
        var origin = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList(); var height = Ui(isPublic ? 288 : 196);
        var clicked = ImGui.InvisibleButton("##groupType" + id, new Vector2(width, height)); var hovered = ImGui.IsItemHovered();
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        draw.AddRectFilled(origin, origin + new Vector2(width, height), ImGui.GetColorU32(hovered ? CardBgHover : FrameBg), Ui(10));
        draw.AddRect(origin, origin + new Vector2(width, height), ImGui.GetColorU32(AccentInk(hovered ? .8f : .35f)), Ui(10));
        UiFonts.DrawCenteredIcon(draw, Ui(30), origin + UiVec(36, 36), ImGui.GetColorU32(AccentHover), icon.ToIconString());
        UiFonts.DrawCenteredIcon(draw, Ui(16), origin + new Vector2(width-Ui(28),Ui(30)), ImGui.GetColorU32(MutedText), FontAwesomeIcon.ChevronRight.ToIconString());
        ImGui.SetCursorScreenPos(origin + UiVec(74, 18)); DrawSocialHeading(title, 1.08f);
        ImGui.SetCursorScreenPos(origin + UiVec(74, 48));
        using (UiFonts.PushRelative(.9f)) using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + width - Ui(98))) ImGui.TextUnformatted(access);

        var tint = isPublic ? new Vector4(1f,.79f,.25f,1) : new Vector4(.24f,.84f,.55f,1);
        var barOrigin = origin + UiVec(18,82); var barSize = new Vector2(width-Ui(36),Ui(36));
        draw.AddRectFilled(barOrigin,barOrigin+barSize,ImGui.GetColorU32(new Vector4(tint.X,tint.Y,tint.Z,.09f)),Ui(7));
        draw.AddRect(barOrigin,barOrigin+barSize,ImGui.GetColorU32(new Vector4(tint.X,tint.Y,tint.Z,.75f)),Ui(7));
        using (UiFonts.PushRelative(.95f,emphasis:true)) {
            var label = isPublic ? "Server managed encryption" : "End-to-end encrypted";
            var textSize = ImGui.CalcTextSize(label); var glyph = Ui(16); var gap = Ui(9);
            var left = barOrigin.X + (barSize.X-textSize.X-glyph-gap)*.5f;
            UiFonts.DrawCenteredIcon(draw,glyph,new Vector2(left+glyph*.5f,barOrigin.Y+barSize.Y*.5f),ImGui.GetColorU32(tint),FontAwesomeIcon.Lock.ToIconString());
            draw.AddText(new Vector2(left+glyph+gap,barOrigin.Y+(barSize.Y-textSize.Y)*.5f),ImGui.GetColorU32(tint),label);
        }
        ImGui.SetCursorScreenPos(origin + UiVec(18,130));
        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + width - Ui(36))) using (UiFonts.PushRelative(.9f)) ImGui.TextColored(MutedText, privacy);
        ImGui.SetCursorScreenPos(origin + UiVec(18,166));
        using (UiFonts.PushRegularRelative(.85f)) using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + width - Ui(36)))
            ImGui.TextColored(isPublic ? MutedText : tint,isPublic ? "Direct image uploading is not allowed" : "Allows direct image uploading");
        if (isPublic) {
            draw.AddLine(origin+UiVec(18,194),origin+new Vector2(width-Ui(18),Ui(194)),ImGui.GetColorU32(AccentInk(.3f)));
            UiFonts.DrawCenteredIcon(draw,Ui(22),origin+UiVec(32,220),ImGui.GetColorU32(AccentHover),FontAwesomeIcon.ShieldAlt.ToIconString());
            ImGui.SetCursorScreenPos(origin+UiVec(58,208));
            using (UiFonts.PushRelative(.9f,emphasis:true))
            using (ImRaii.TextWrapPos(ImGui.GetCursorPosX()+width-Ui(76))) ImGui.TextUnformatted("You're responsible for moderating this group.");
            ImGui.SetCursorScreenPos(origin+UiVec(58,242));
            using (UiFonts.PushRelative(.85f))
            using (ImRaii.TextWrapPos(ImGui.GetCursorPosX()+width-Ui(76))) ImGui.TextColored(MutedText,"Manage members, remove inappropriate messages and handle disruptive behaviour.");
        }
        ImGui.SetCursorScreenPos(origin + new Vector2(0, height));
        return clicked;
    }
}

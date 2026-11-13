using AlphaChannel.Plugin.Auth;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool alphaChatRosterOpen;
    private string alphaChatRosterSearch = string.Empty;
    private bool partyChatRosterOpen;
    private string partyChatRosterSearch = string.Empty;
    private bool dmRosterOpen;
    private string dmRosterSearch="";
    private void DrawAlphaChatRosterOverlay(bool party = false,bool dm=false)
    {
        if (!(dm ? dmRosterOpen : party ? partyChatRosterOpen : alphaChatRosterOpen)) return;
        if (dm ? currentPage != HomePage.Messages || openConversationId is null || !openConversationIsGroup : party ? currentPage != HomePage.WatchAlong || stream.Mode == StreamMode.None : currentPage != HomePage.AlphaChat || !alphaChat.Connected)
        {
            if (dm) dmRosterOpen=false; else if (party) partyChatRosterOpen = false; else alphaChatRosterOpen = false;
            return;
        }
        var parentPosition = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var alphaRosterFooter=0f;
#if DEBUG
        if(alphaChatCanModerate)alphaRosterFooter=42f;
#endif
        var panelSize = new Vector2(MathF.Min(Ui(520f), parentSize.X - Ui(32f)),
            MathF.Min(Ui(party || dm ? Math.Clamp(124f + (dm ? (openConversationMembers.Length + 1) * 82f : (stream.Roster.Length + 1) * 72f), 220f, 520f) : Math.Clamp(124f+alphaChatUsers.Length*82f+alphaRosterFooter,220f,520f)), parentSize.Y - Ui(32f)));
        var panelPosition = parentPosition + (parentSize - panelSize) * .5f;
        ImGui.SetNextWindowPos(parentPosition, ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##alphaChatRosterOverlay", flags))
        {
            ImGui.End();
            return;
        }
        ImGui.GetWindowDrawList().AddRectFilled(parentPosition, parentPosition + parentSize,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, .54f)));
        ImGui.SetCursorScreenPos(panelPosition);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14f))
                   .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
                   .Push(ImGuiStyleVar.WindowPadding, UiVec(20f, 18f))
                   .Push(ImGuiStyleVar.FrameRounding, Ui(8f))
                   .Push(ImGuiStyleVar.ItemSpacing, UiVec(8f, 8f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
                   .Push(ImGuiCol.FrameBg, FrameBg)
                   .Push(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, .82f)))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##alphaChatRosterPanel", panelSize, true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (panel) DrawAlphaChatRosterContents(panelPosition, panelSize,party,dm);
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) { if (dm) dmRosterOpen=false; else if (party) partyChatRosterOpen=false; else alphaChatRosterOpen=false; }
        ImGui.End();
    }

    private void DrawAlphaChatRosterContents(Vector2 position, Vector2 size,bool party = false,bool dm=false)
    {
        var draw = ImGui.GetWindowDrawList();
        var title = position + UiVec(20f, 22f);
        UiFonts.DrawIcon(draw, ImGui.GetFontSize(), title, ImGui.GetColorU32(AccentHover), FontAwesomeIcon.Users.ToIconString());
        using (UiFonts.PushRelative(1f, emphasis: true))
        UiFonts.DrawText(draw, ImGui.GetFontSize(), title + UiVec(28f, 0f), ImGui.GetColorU32(Hex(0xE2E7EF)),
            dm ? $"Group members · {openConversationMembers.Length+1}" : party ? $"People in Watch Party · {stream.Roster.Length+1}" : $"People in Alpha Chat · {alphaChat.MemberCount}");
        ImGui.SetCursorScreenPos(position + new Vector2(size.X - Ui(48f), Ui(16f)));
        if (AlphaChatButton("closeAlphaChatRoster", "", FontAwesomeIcon.Times, Ui(28f), Ui(28f)))
            if (dm) dmRosterOpen=false; else if (party) partyChatRosterOpen = false; else alphaChatRosterOpen = false;
        ImGui.SetCursorScreenPos(position + UiVec(20f, 58f));
        ImGui.SetNextItemWidth(size.X - Ui(40f));
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(12f, 10f)))
        {
            if (dm) ImGui.InputTextWithHint("##dmRosterSearch","Search people…",ref dmRosterSearch,80);
            else if (party) ImGui.InputTextWithHint("##partyRosterSearch","Search people…",ref partyChatRosterSearch,80);
            else ImGui.InputTextWithHint("##alphaChatRosterSearch", "Search people…", ref alphaChatRosterSearch, 80);
        }
        ImGui.SetCursorScreenPos(position + UiVec(20f, 104f));
        var listHeight = size.Y - Ui(124f);
#if DEBUG
        if (!dm && !party && alphaChatCanModerate) listHeight -= Ui(42f);
#endif
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero))
        using (var list = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##alphaChatRosterList", new Vector2(size.X-Ui(40f),listHeight),false)))
        {
            if (list)
            {
                var visible=0;
                foreach(var user in (dm ? GetDmRosterUsers() : party ? GetPartyChatRosterUsers() : alphaChatUsers).OrderByDescending(u=>friends.Any(f=>f.AccountId==u.AccountId)).ThenBy(u=>u.DisplayName,StringComparer.OrdinalIgnoreCase))
                {
                    if(!user.DisplayName.Contains(dm ? dmRosterSearch : party ? partyChatRosterSearch : alphaChatRosterSearch,StringComparison.OrdinalIgnoreCase)) continue;
                    DrawAlphaChatRosterRow(user,ImGui.GetContentRegionAvail().X,party,dm); visible++;
                }
                if(visible==0) ImGui.TextColored(MutedText,"No people match your search.");
            }
        }
#if DEBUG
        if(!dm && !party && alphaChatCanModerate)
        {
            ImGui.SetCursorScreenPos(position+new Vector2(Ui(20f),size.Y-Ui(54f)));
            if(ActivePartyButton("Manage chat restrictions",new Vector2(size.X-Ui(40f),Ui(34f))))
            {
                alphaChatRestrictionsOpen=true;
                _=alphaChat.CommandAsync(new {type="restrictions"});
            }
        }
#endif
    }
    private void DrawAlphaChatRosterRow(AlphaChatUser user,float width,bool party=false,bool dm=false)
    {
        if(CurrentSession is not { } session) return;
        using var id=ImRaii.PushId(user.AccountId);
        var origin=ImGui.GetCursorScreenPos(); var height=Ui(64f); var draw=ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin,origin+new Vector2(width,height),ImGui.GetColorU32(FrameBg),Ui(10f));
        draw.AddRect(origin,origin+new Vector2(width,height),ImGui.GetColorU32(BorderSubtle),Ui(10f));
        var own=user.AccountId==session.AccountId; var blocked=AlphaChatBlocked(user.AccountId);
        var friend=friendsAccountId==session.AccountId && friends.Any(f=>f.AccountId==user.AccountId);
        var pending=friendRequests.Incoming.Concat(friendRequests.Outgoing).Any(r=>r.OtherAccountId==user.AccountId) || partyFriendRequestsSending.ContainsKey((session.Token,user.AccountId));
        DrawAvatarAt(origin+UiVec(10f,12f),user.AvatarIcon,user.AvatarColorHex,Ui(40f),user.AvatarImageUrl);
        var label=own?"You":blocked?"Blocked":friend?"Friend":pending?"Request sent":"";
        var host=party && IsPartyChatHost(user.AccountId);
        var textWidth=MathF.Max(Ui(30f),width-Ui(278f)-(host?Ui(45):0));
        var text=origin+UiVec(62f,label.Length==0?24f:14f);
        var verified = AccountVerified(user.AccountId);
        var verificationWidth = verified ? ImGui.GetFontSize() + Ui(8) : 0;
        var visibleName = FitPartyTextToWidth(user.DisplayName, textWidth - Ui(18f) - verificationWidth - (PatreonBadgeTier(user.AccountId) > 0 ? Ui(20f) : 0f));
        DrawUsernameAt(draw,text,visibleName,Vector4.One,user.AccountId,verificationTooltip:false);
        var nameEnd = text + new Vector2(ImGui.CalcTextSize(visibleName).X, 0);
        var verificationPosition = nameEnd + UiVec(4, 0);
        var verifiedHovered = verified && ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(verificationPosition,
            verificationPosition + new Vector2(ImGui.GetFontSize()));
        var statusDot = nameEnd + new Vector2(verificationWidth + Ui(9f), ImGui.GetFontSize() * .5f);
        if (!dm) draw.AddCircleFilled(statusDot, Ui(3.5f), ImGui.GetColorU32(user.ChatActive ?
            new Vector4(.16f, .81f, .39f, 1f) : new Vector4(1f, .62f, .2f, 1f)));
        var statusHovered = !dm && ImGui.IsWindowHovered() && ChatPointerInput.IsHoveringRect(statusDot - UiVec(6f, 6f), statusDot + UiVec(6f, 6f));
        if (verifiedHovered) UiFonts.SetTooltip("Verified");
        else if (statusHovered)
            UiFonts.SetTooltip(party ? "In Watch Party" : user.ChatActive ? "Active in Alpha Chat" : "Background presence");
        if (PatreonBadgeTier(user.AccountId) > 0)
        {
            ImGui.SetCursorScreenPos(nameEnd + new Vector2(verificationWidth + Ui(dm ? 4f : 20f), 0));
            DrawAlphaChatSupporterHeart();
        }
        if (host)
        {
            ImGui.SetCursorScreenPos(nameEnd+new Vector2(verificationWidth+Ui(20)+(PatreonBadgeTier(user.AccountId)>0?Ui(22):0),0));
            DrawPartyChatHostTag();
        }
        if(label.Length>0) using (!party ? UiFonts.PushRelative(.90f) : UiFonts.Empty) draw.AddText(text+UiVec(0f,21f),ImGui.GetColorU32(MutedText),label);
        if(!own)
        {
            ImGui.SetCursorScreenPos(origin+new Vector2(width-Ui(212f),Ui(16f)));
            using(UiDisabled.Scope(friend||pending||blocked))
                if(AlphaChatButton("friend",friend?"Friends":pending?"Pending":"Add Friend",friend?FontAwesomeIcon.UserCheck:pending?FontAwesomeIcon.Clock:FontAwesomeIcon.UserPlus,Ui(112f),Ui(32f)))
                    _=SendPartyFriendRequestAsync(session,user.AccountId,user.DisplayName);
            ImGui.SetCursorScreenPos(origin+new Vector2(width-Ui(92f),Ui(16f)));
            if(AlphaChatButton("block",blocked?"Unblock":"Block",FontAwesomeIcon.Ban,Ui(82f),Ui(32f),danger:true))
            { if(blocked) UnblockAccount(session,user.AccountId); else QueueFriendBlockConfirmation(user.AccountId,user.DisplayName); }
        }
        ImGui.SetCursorScreenPos(origin);
        if (ImGui.InvisibleButton("##rosterPerson",new Vector2(own ? width : MathF.Max(Ui(60f),width-Ui(220f)),height)) && !blocked)
            OpenProfilePage(session,user.AccountId,user.DisplayName);
        if (!blocked && ImGui.IsItemHovered() && !verifiedHovered && !statusHovered &&
            ImGui.GetIO().MousePos.X < nameEnd.X)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            UiFonts.SetTooltip("View Profile");
        }
        using(ImRaii.PushStyle(ImGuiStyleVar.PopupBorderSize, Ui(1f)).Push(ImGuiStyleVar.PopupRounding, !party ? Ui(10f) : ImGui.GetStyle().PopupRounding))
        using(ImRaii.PushColor(ImGuiCol.Border, new Vector4(AccentHover.X, AccentHover.Y, AccentHover.Z, .3f)))
        using(var popup=ImRaii.ContextPopupItem("##rosterPersonMenu"))
            if(popup) { if (dm && CurrentSession is { } dmSession) DrawDmPersonActions(dmSession,user.AccountId,user.DisplayName); else if (party) DrawPartyChatRosterActions(user); else DrawAlphaChatPersonActions(user); }
        ImGui.SetCursorScreenPos(origin); ImGui.Dummy(new Vector2(width,height)); ImGui.Dummy(UiVec(0f,2f));
    }
}

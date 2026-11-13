using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool friendsAddOpen;
    private CharacterSession? friendsAddSession;
    private int friendsAddTab;
    private bool friendsAddFocusInput;
    private string? friendsAddNotice;
    private bool friendsAddNoticeSuccess;
    private DateTime friendsAddCodeCopiedUntil;

    private void OpenFriendsAddPanel(CharacterSession session)
    {
        if (!ReferenceEquals(friendsAddSession,session))
        {
            Interlocked.Increment(ref friendSearchGeneration);
            friendSearchInput=""; friendSearchQuery=""; friendSearchResults=[]; friendSearchLoading=false;
            inviteCodeInput=""; inviteCodeError=null; inviteCodeRedeeming=false;
        }
        friendsAddSession=session;
        friendsAddOpen=true; friendsAddTab=0; friendsAddFocusInput=true; friendsAddNotice=null;
    }

    private void DrawFriendsAddOverlay()
    {
        if (!friendsAddOpen) return;
        if (CurrentSession is not { } session || !ReferenceEquals(session,friendsAddSession) || currentPage!=HomePage.Friends)
        { friendsAddOpen=false; return; }
        var parentPosition=ImGui.GetWindowPos();
        var parentSize=ImGui.GetWindowSize();
        var panelSize=new Vector2(MathF.Min(Ui(520),parentSize.X-Ui(36)),MathF.Min(Ui(440 + FriendsAddResultsHeight()),parentSize.Y-Ui(36)));
        var panelPosition=parentPosition+(parentSize-panelSize)*.5f;
        ImGui.SetNextWindowPos(parentPosition,ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize,ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        const ImGuiWindowFlags flags=ImGuiWindowFlags.NoTitleBar|ImGuiWindowFlags.NoResize|ImGuiWindowFlags.NoMove|
            ImGuiWindowFlags.NoScrollbar|ImGuiWindowFlags.NoScrollWithMouse|ImGuiWindowFlags.NoCollapse|
            ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoDocking|ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##friendsAddOverlay",flags)) { ImGui.End(); return; }
        ImGui.GetWindowDrawList().AddRectFilled(parentPosition,parentPosition+parentSize,ImGui.GetColorU32(new Vector4(0,0,0,.58f)));
        if (ImGui.IsKeyPressed(ImGuiKey.Escape) || ImGui.IsMouseClicked(ImGuiMouseButton.Left)&&!ImGui.IsMouseHoveringRect(panelPosition,panelPosition+panelSize))
            friendsAddOpen=false;
        ImGui.SetCursorScreenPos(panelPosition);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding,Ui(14)).Push(ImGuiStyleVar.ChildBorderSize,Ui(1))
            .Push(ImGuiStyleVar.WindowPadding,UiVec(24,20)).Push(ImGuiStyleVar.FrameRounding,Ui(8))
            .Push(ImGuiStyleVar.FrameBorderSize,Ui(1)).Push(ImGuiStyleVar.ItemSpacing,UiVec(8,8)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg,CardBg).Push(ImGuiCol.Border,AccentInk(.65f))
            .Push(ImGuiCol.FrameBg,FrameBg).Push(ImGuiCol.Button,FrameBg).Push(ImGuiCol.ButtonHovered,FrameBgHover))
        {
            using var panel=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##friendsAddPanel",panelSize,true,ImGuiWindowFlags.AlwaysUseWindowPadding|ImGuiWindowFlags.NoScrollbar));
            if (panel)
            {
                var start=ImGui.GetCursorScreenPos();
                var width=ImGui.GetContentRegionAvail().X;
                // The form scrolls on short windows; Close always remains accessible.
                using (ImRaii.PushColor(ImGuiCol.ChildBg,Vector4.Zero))
                using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,Vector2.Zero))
                using (var form=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##friendsAddForm",new Vector2(width,MathF.Max(Ui(80),ImGui.GetContentRegionAvail().Y-Ui(48))),false)))
                    if (form) DrawFriendsAddForm(session);
                var closePosition=new Vector2(start.X+width-Ui(110),panelPosition.Y+panelSize.Y-Ui(56));
                ImGui.SetCursorScreenPos(closePosition);
                if (AlphaChatButton("friendsAddClose","Close",FontAwesomeIcon.Times,Ui(110))) friendsAddOpen=false;
            }
        }
        ImGui.End();
    }

    private float FriendsAddResultsHeight() => friendsAddTab == 0 ? Math.Clamp(friendSearchResults.Length * 72f, 32f, 140f) : 32f;

    private void DrawFriendsAddForm(CharacterSession session)
    {
        var origin=ImGui.GetCursorScreenPos();
        var width=ImGui.GetContentRegionAvail().X;
        DrawSocialHeading("Add friends", 1.3f);
        ImGui.SetCursorScreenPos(origin+new Vector2(width-Ui(30),0));
        if (AlphaChatTextAction("friendsAddX","",FontAwesomeIcon.Times,Vector4.One,Ui(30),Ui(30))) friendsAddOpen=false;
        ImGui.SetCursorScreenPos(origin+UiVec(0,28));
        DrawSocialHelper("Find someone or share your code to connect.");
        ImGui.SetCursorScreenPos(origin+UiVec(0,64));
        var tabWidth=(width-Ui(8))*.5f;
        if (AlphaChatButton("friendsFindTab","Find by username",FontAwesomeIcon.Search,tabWidth,primary:friendsAddTab==0))
        { friendsAddTab=0; friendsAddFocusInput=true; friendsAddNotice=null; }
        ImGui.SameLine(0,Ui(8));
        if (AlphaChatButton("friendsCodeTab","Enter friend code",FontAwesomeIcon.UserFriends,tabWidth,primary:friendsAddTab==1))
        { friendsAddTab=1; friendsAddFocusInput=true; friendsAddNotice=null; }
        ImGui.SetCursorScreenPos(origin+UiVec(0,120));
        ImGui.TextColored(MutedText,friendsAddTab==0?"Username":"Friend code");
        ImGui.SetNextItemWidth(width);
        if (friendsAddFocusInput) { ImGui.SetKeyboardFocusHere(); friendsAddFocusInput=false; }
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding,new Vector2(Ui(12),(Ui(36)-ImGui.GetFontSize())*.5f)))
        {
            if (friendsAddTab==0)
            {
                if (ImGui.InputTextWithHint("##friendsAddUsername","Search by username…",ref friendSearchInput,DisplayNameRules.MaxLength))
                    RequestFriendSearch(session,friendSearchInput);
            }
            else ImGui.InputTextWithHint("##friendsAddCode","Paste friend code…",ref inviteCodeInput,16);
        }
        ImGui.Dummy(UiVec(0,4));
        if (friendsAddTab==0)
        {
            ImGui.TextWrapped("Search by username for accounts without a verified character.");
            ImGui.TextColored(MutedText,"Results");
            using var background=ImRaii.PushColor(ImGuiCol.ChildBg,Vector4.Zero);
            using var spacing=ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,Vector2.Zero);
            using var results=UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##friendsAddResults",new Vector2(width,Ui(FriendsAddResultsHeight())),false));
            if (results)
            {
                if (friendSearchLoading) ImGui.TextColored(MutedText,"Searching…");
                else if (friendSearchQuery.Length<DisplayNameRules.MinLength)
                    DrawSocialHelperWrapped($"Type at least {DisplayNameRules.MinLength} characters to find someone.");
                else if (friendSearchResults.Length==0) ImGui.TextColored(MutedText,"No one found with that name.");
                else foreach (var result in friendSearchResults) DrawFriendsAddResult(session,result,ImGui.GetContentRegionAvail().X);
            }
        }
        else
        {
            using (UiDisabled.Scope(inviteCodeRedeeming||string.IsNullOrWhiteSpace(inviteCodeInput)))
                if (AlphaChatButton("redeemFriendsCode",inviteCodeRedeeming?"Adding…":"Add friend",FontAwesomeIcon.UserPlus,width,primary:true))
                    _=RedeemFriendsAddCodeAsync(session,inviteCodeInput.Trim());
            DrawSocialHelperWrapped("Enter the code your friend shared with you to connect.");
            ImGui.Dummy(UiVec(0,4));
        }
        if (friendsAddNotice is { } notice) ImGui.TextColored(friendsAddNoticeSuccess?Good:Danger,notice);
        if (inviteCodeError is { } codeError && friendsAddTab==1) ImGui.TextWrapped(codeError);
        ImGui.SetCursorScreenPos(new Vector2(origin.X, MathF.Max(ImGui.GetCursorScreenPos().Y, ImGui.GetWindowPos().Y + ImGui.GetWindowSize().Y - Ui(124))));
        ImGui.Separator(); ImGui.Dummy(UiVec(0,4));
        DrawSocialHeading("Your friend code"); ImGui.SameLine(0,Ui(8));
        AlphaChatTextAction("friendCodeInfo","",FontAwesomeIcon.InfoCircle,MutedText,Ui(18),Ui(18));
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Your code regenerates after someone uses it. Copy the new code when sharing again.");
        DrawSocialHelper("Share this code so someone can add you.");
        var codeOrigin=ImGui.GetCursorScreenPos();
        var draw=ImGui.GetWindowDrawList();
        draw.AddRectFilled(codeOrigin,codeOrigin+new Vector2(width,Ui(46)),ImGui.GetColorU32(FrameBg),Ui(8));
        draw.AddRect(codeOrigin,codeOrigin+new Vector2(width,Ui(46)),ImGui.GetColorU32(AccentInk(.4f)),Ui(8));
        ImGui.SetCursorScreenPos(codeOrigin+UiVec(14,12));
        using var uiFontScale8550=UiFonts.PushRelative(1.2f); ImGui.TextUnformatted(session.InviteCode); uiFontScale8550.Dispose();
        ImGui.SetCursorScreenPos(codeOrigin+new Vector2(width-Ui(142),Ui(5)));
        if (AlphaChatButton("copyFriendsCode",DateTime.UtcNow<friendsAddCodeCopiedUntil?"Copied!":"Copy code",FontAwesomeIcon.Copy,Ui(136)))
        { ImGui.SetClipboardText(session.InviteCode); friendsAddCodeCopiedUntil=DateTime.UtcNow.AddSeconds(3); }
        ImGui.SetCursorScreenPos(codeOrigin); ImGui.Dummy(new Vector2(width,Ui(46)));
    }

    private void DrawFriendsAddResult(CharacterSession session,FriendSearchResultDto result,float width)
    {
        using var id=ImRaii.PushId(result.AccountId);
        var origin=ImGui.GetCursorScreenPos();
        var draw=ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin,origin+new Vector2(width,Ui(64)),ImGui.GetColorU32(FrameBg),Ui(9));
        draw.AddRect(origin,origin+new Vector2(width,Ui(64)),ImGui.GetColorU32(AccentInk(.3f)),Ui(9));
        DrawAvatarAt(origin+UiVec(12,12),result.AvatarIcon,result.AvatarColorHex,Ui(40),result.AvatarImageUrl);
        var friend=result.Relation==FriendSearchRelation.Friends || friends.Any(f=>f.AccountId==result.AccountId);
        var pending=result.Relation==FriendSearchRelation.Pending || friendRequests.Incoming.Concat(friendRequests.Outgoing).Any(r=>r.OtherAccountId==result.AccountId);
        var sending=partyFriendRequestsSending.ContainsKey((session.Token,result.AccountId));
        ImGui.SetCursorScreenPos(origin+UiVec(64,12));
        DrawUsername(result.AccountId,FitPartyTextToWidth(result.DisplayName,MathF.Max(Ui(30),width-Ui(220))));
        ImGui.SetCursorScreenPos(origin+UiVec(64,35));
        DrawSocialHelper(friend?"Already friends":pending?"Request sent":"Alpha Channel user");
        ImGui.SetCursorScreenPos(origin+new Vector2(width-Ui(144),Ui(14)));
        using (UiDisabled.Scope(friend||pending||sending))
            if (AlphaChatButton("addSearchFriend",friend?"Friends":pending?"Request sent":sending?"Sending…":"Add friend",FontAwesomeIcon.UserPlus,Ui(132),primary:!friend&&!pending))
                _=SendPartyFriendRequestAsync(session,result.AccountId,result.DisplayName);
        ImGui.SetCursorScreenPos(origin); ImGui.Dummy(new Vector2(width,Ui(64)));
    }

    private async Task RedeemFriendsAddCodeAsync(CharacterSession session,string code)
    {
        inviteCodeRedeeming=true; inviteCodeError=null; friendsAddNotice=null;
        try
        {
            var success=await friendsClient.RedeemInviteCodeAsync(session.Token,code);
            await Plugin.Framework.RunOnFrameworkThread(()=>
            {
                if (!ReferenceEquals(CurrentSession,session)) return;
                inviteCodeRedeeming=false;
                if (success)
                {
                    inviteCodeInput=""; friendsDirty=true;
                    friendsAddNoticeSuccess=true; friendsAddNotice="Friend added!";
                }
                else inviteCodeError="Couldn't redeem that code. It may be incorrect, expired or already used.";
            });
        }
        catch (Exception)
        {
            await Plugin.Framework.RunOnFrameworkThread(()=>
            {
                if (!ReferenceEquals(CurrentSession,session)) return;
                inviteCodeRedeeming=false; inviteCodeError="Couldn't add your friend. Please try again.";
            });
        }
    }
}

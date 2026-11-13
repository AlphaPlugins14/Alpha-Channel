using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string hubGroupIcon = "Users", hubGroupColor = "#4D96FF";
    private string cloudGroupEditIcon = "Users", cloudGroupEditColor = "#4D96FF";
    private bool cloudGroupAppearanceSaved;
    private static readonly FontAwesomeIcon[] GroupAvatarIcons = [
        FontAwesomeIcon.Users, FontAwesomeIcon.Comments, FontAwesomeIcon.Film, FontAwesomeIcon.Music,
        FontAwesomeIcon.Headphones, FontAwesomeIcon.Gamepad, FontAwesomeIcon.Dice, FontAwesomeIcon.Globe,
        FontAwesomeIcon.Camera, FontAwesomeIcon.Book, FontAwesomeIcon.Star, FontAwesomeIcon.Heart,
        FontAwesomeIcon.Moon, FontAwesomeIcon.Sun, FontAwesomeIcon.Bolt, FontAwesomeIcon.Dragon,
        FontAwesomeIcon.Cat, FontAwesomeIcon.Crown, FontAwesomeIcon.Gem, FontAwesomeIcon.Rocket];
    private static readonly (string Hex, string Name)[] GroupAvatarColors = [
        ("#9966FA","Purple"),("#FF6B6B","Coral"),("#4ECDC4","Teal"),("#FFD93D","Gold"),("#6BCB77","Green"),
        ("#4D96FF","Blue"),("#FF922B","Orange"),("#F783AC","Pink"),("#A0A0A0","Grey"),("#00C2A8","Cyan")];

    private void DrawCreateGroupAppearance(float width)
    {
        var origin = ImGui.GetCursorScreenPos(); var size = new Vector2(width,Ui(52)); var draw = ImGui.GetWindowDrawList();
        var clicked = ImGui.InvisibleButton("##chooseGroupAppearance",size); var hovered = ImGui.IsItemHovered();
        draw.AddRectFilled(origin,origin+size,ImGui.GetColorU32(hovered?CardBgHover:FrameBg),Ui(8));
        draw.AddRect(origin,origin+size,ImGui.GetColorU32(AccentInk(hovered ? .6f : .25f)),Ui(8));
        DrawAvatarAt(origin+UiVec(8,8),hubGroupIcon,hubGroupColor,Ui(36));
        draw.AddText(origin+UiVec(56,8),ImGui.GetColorU32(Vector4.One),"Group icon & colour");
        using (UiFonts.PushRelative(.85f)) draw.AddText(origin+UiVec(56,29),ImGui.GetColorU32(MutedText),"Choose how your group appears");
        UiFonts.DrawCenteredIcon(draw,Ui(16),origin+new Vector2(width-Ui(22),Ui(26)),ImGui.GetColorU32(AccentHover),FontAwesomeIcon.ChevronRight.ToIconString());
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (clicked) OpenChatsPopup("##createGroupAppearance");
        PrepareChatsPopup("##createGroupAppearance",UiVec(420,390));
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,UiVec(16,16)).Push(ImGuiStyleVar.PopupRounding,Ui(10)).Push(ImGuiStyleVar.PopupBorderSize,Ui(1)))
        using (ImRaii.PushColor(ImGuiCol.PopupBg,CardBg).Push(ImGuiCol.Border,AccentInk(.4f)))
        using (var popup=UiFonts.Window(ImGui.GetFontSize(),ImRaii.Popup("##createGroupAppearance"))) if (popup) {
            DrawGroupAppearancePicker(ref hubGroupIcon,ref hubGroupColor);
            if (AlphaChatButton("groupAppearanceDone","Done",FontAwesomeIcon.Check,ImGui.GetContentRegionAvail().X,primary:true)) ImGui.CloseCurrentPopup();
        }
        ImGui.SetCursorScreenPos(origin); ImGui.Dummy(size);
    }

    private void DrawGroupAppearancePicker(ref string icon, ref string color)
    {
        var origin=ImGui.GetCursorScreenPos();
        DrawAvatarAt(origin,icon,color,Ui(44));
        ImGui.SetCursorScreenPos(origin+UiVec(58,3)); DrawSocialHeading("Group appearance");
        ImGui.SetCursorScreenPos(origin+UiVec(58,26));
        using (UiFonts.PushRelative(.85f)) ImGui.TextColored(MutedText,"Choose an icon and colour.");
        ImGui.SetCursorScreenPos(origin+UiVec(0,58));
        ImGui.TextColored(MutedText,"Icon");
        var columns=Math.Max(1,(int)((ImGui.GetContentRegionAvail().X+Ui(6))/Ui(38)));
        for(var i=0;i<GroupAvatarIcons.Length;i++) {
            if(i%columns!=0) ImGui.SameLine(0,Ui(6));
            var item=GroupAvatarIcons[i];var name=item.ToString(); var selected=icon==name;
            var p=ImGui.GetCursorScreenPos();var size=UiVec(32,32);var draw=ImGui.GetWindowDrawList();
            if(ImGui.InvisibleButton("##groupIcon"+name,size)) icon=name;
            var hovered=ImGui.IsItemHovered();
            draw.AddRectFilled(p,p+size,ImGui.GetColorU32(selected?AccentInk(.35f):hovered?CardBgHover:FrameBg),Ui(6));
            if(selected) draw.AddRect(p,p+size,ImGui.GetColorU32(AccentHover),Ui(6));
            UiFonts.DrawCenteredIcon(draw,Ui(18),p+size*.5f,ImGui.GetColorU32(Vector4.One),item.ToIconString());
            if(hovered) { ImGui.SetMouseCursor(ImGuiMouseCursor.Hand); UiFonts.SetTooltip(name); }
        }
        ImGui.TextColored(MutedText,"Colour");
        var colorColumns=Math.Max(1,(int)((ImGui.GetContentRegionAvail().X+Ui(6))/Ui(36)));
        for(var i=0;i<GroupAvatarColors.Length;i++) {
            if(i%colorColumns!=0) ImGui.SameLine(0,Ui(6));
            var item=GroupAvatarColors[i];var p=ImGui.GetCursorScreenPos();var size=UiVec(30,30);var draw=ImGui.GetWindowDrawList();
            if(ImGui.InvisibleButton("##groupColor"+item.Hex,size)) color=item.Hex;
            var hovered=ImGui.IsItemHovered();var selected=string.Equals(color,item.Hex,StringComparison.OrdinalIgnoreCase);
            draw.AddCircleFilled(p+size*.5f,Ui(12),ImGui.GetColorU32(ParseAvatarColor(item.Hex)));
            if(selected||hovered) draw.AddCircle(p+size*.5f,Ui(14),ImGui.GetColorU32(selected?Vector4.One:MutedText),0,Ui(1.5f));
            if(hovered) { ImGui.SetMouseCursor(ImGuiMouseCursor.Hand); UiFonts.SetTooltip(item.Name); }
        }
        ImGui.Dummy(UiVec(0,6));
    }

    private void DrawCloudGroupAppearanceEditor(CharacterSession session, GroupSummary group, string conversation)
    {
        if(group.OwnerId!=session.AccountId) return;
        if(AlphaChatButton("cloudEditAppearance","Icon & colour",FontAwesomeIcon.Palette,ImGui.GetContentRegionAvail().X,Ui(28))) {
            cloudGroupEditIcon=group.AvatarIcon??"Users"; cloudGroupEditColor=group.AvatarColorHex??"#4D96FF";
            cloudGroupAppearanceSaved=false;
            OpenChatsPopup("##cloudGroupAppearance");
        }
        PrepareChatsPopup("##cloudGroupAppearance",UiVec(420,420));
        using var popup=UiFonts.Window(ImGui.GetFontSize(),ImRaii.Popup("##cloudGroupAppearance"));
        if(!popup) return;
        using var disabled=UiDisabled.Scope(cloudGroupBusy);
        DrawGroupAppearancePicker(ref cloudGroupEditIcon,ref cloudGroupEditColor);
        if(cloudGroupError is { } error) ImGui.TextWrapped(error);
        var changed=group.AvatarIcon!=cloudGroupEditIcon||!string.Equals(group.AvatarColorHex,cloudGroupEditColor,StringComparison.OrdinalIgnoreCase);
        using (UiFonts.PushRelative(.85f)) ImGui.TextColored(cloudGroupAppearanceSaved&&!changed?AccentHover:MutedText,
            cloudGroupAppearanceSaved&&!changed?"Saved for everyone in this group.":"Your changes will be shared with everyone in this group.");
        using (UiDisabled.Scope(!changed))
        if(AlphaChatButton("cloudSaveAppearance",cloudGroupBusy?"Saving…":"Save changes",FontAwesomeIcon.Check,ImGui.GetContentRegionAvail().X,primary:true)) {
            var selectedIcon=cloudGroupEditIcon;var selectedColor=cloudGroupEditColor;
            RunCloudGroupAction(session,async()=> {
                await dmClient.Groups.UpdateAppearanceAsync(session.Token,conversation,selectedIcon,selectedColor);
                await Plugin.Framework.RunOnFrameworkThread(()=> { if(IsCurrentMessageSession(session)) { chatsDirectoryAccount=null; cloudGroupAppearanceSaved=true; } });
            });
        }
        if(AlphaChatButton("cloudAppearanceClose","Close",FontAwesomeIcon.Times,ImGui.GetContentRegionAvail().X,Ui(28))) ImGui.CloseCurrentPopup();
    }
}

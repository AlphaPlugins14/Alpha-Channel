using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    internal Vector2 QuickVolumePanelSize
    {
        get
        {
            var titleHeight=ImGui.GetTextLineHeight();
            float helperHeight;
            using(UiFonts.PushRelative(.90f))helperHeight=ImGui.GetTextLineHeight();
            return new Vector2(Ui(316f),titleHeight+helperHeight+ImGui.GetFrameHeight()+Ui(60f)+ImGui.GetStyle().ItemSpacing.Y*4f);
        }
    }
    internal void DrawQuickVolumeContents()
    {
        var width=Ui(288f);
        var start=ImGui.GetCursorPos();
        DrawSocialHeading("TV volume");
        var value=Plugin.Cfg.Muted?"Muted":$"{Plugin.Cfg.Volume}%";
        ImGui.SameLine();ImGui.SetCursorPosX(start.X+width-ImGui.CalcTextSize(value).X);
        ImGui.TextColored(Plugin.Cfg.Volume>100?Gold:MutedText,value);
        var volume=Plugin.Cfg.Volume;
        ImGui.SetNextItemWidth(width);
        using(ImRaii.PushColor(ImGuiCol.SliderGrab,volume>100?Gold:Accent)
            .Push(ImGuiCol.SliderGrabActive,volume>100?Gold:AccentHover))
        {
            if(ImGui.SliderInt("##quickVolume",ref volume,0,130,""))
            {
                Plugin.Cfg.Volume=volume;
                if(volume>0)Plugin.Cfg.Muted=false;
                ApplyListeningVolume();
            }
        }
        if(ImGui.IsItemDeactivatedAfterEdit())Plugin.Cfg.Save();
        using (UiFonts.PushRelative(.90f))
        {
        ImGui.TextColored(MutedText,"Quiet");ImGui.SameLine();
        ImGui.SetCursorPosX(start.X+width-ImGui.CalcTextSize("Boost").X);ImGui.TextColored(MutedText,"Boost");
        }
        ImGui.Spacing();
        var half=(width-Ui(10f))*.5f;
        if(DrawQuickMuteToggle("quickMuteFfxiv","Mute FFXIV",IsFfxivSoundMuted(),half))SetFfxivSoundMuted(!IsFfxivSoundMuted());
        ImGui.SameLine(0,Ui(10f));
        if(DrawQuickMuteToggle("quickMuteTv","Mute TV",Plugin.Cfg.Muted||Plugin.Cfg.Volume==0,half))
        {
            if(Plugin.Cfg.Muted||Plugin.Cfg.Volume==0)
            {if(Plugin.Cfg.Volume==0)Plugin.Cfg.Volume=50;Plugin.Cfg.Muted=false;}
            else Plugin.Cfg.Muted=true;
            ApplyListeningVolume();Plugin.Cfg.Save();
        }
    }

    private bool DrawQuickMuteToggle(string id,string label,bool selected,float width)
    {
        var origin=ImGui.GetCursorScreenPos();var size=new Vector2(width,Ui(36f));
        var clicked=ImGui.InvisibleButton("##"+id,size);var hovered=ImGui.IsItemHovered();
        var draw=ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin,origin+size,ImGui.GetColorU32(selected?AccentInk(.25f):hovered?CardBgHover:FrameBg),Ui(6f));
        draw.AddRect(origin,origin+size,ImGui.GetColorU32(hovered?AccentHover:AccentInk(.55f)),Ui(6f));
        var text=ImGui.CalcTextSize(label);var left=origin+new Vector2((width-text.X-Ui(24f))*.5f,(size.Y-text.Y)*.5f);
        var box=left+new Vector2(0,(text.Y-Ui(18f))*.5f);
        draw.AddRectFilled(box,box+UiVec(18f,18f),ImGui.GetColorU32(selected?Accent:CardBg),Ui(4f));
        draw.AddRect(box,box+UiVec(18f,18f),ImGui.GetColorU32(selected?AccentHover:MutedText),Ui(4f),ImDrawFlags.None,Ui(1.5f));
        if(selected)UiFonts.DrawIcon(draw, Ui(12f), box+UiVec(3f,2f), ImGui.GetColorU32(Vector4.One), FontAwesomeIcon.Check.ToIconString());
        draw.AddText(left+UiVec(24f,0),ImGui.GetColorU32(Vector4.One),label);
        if(hovered)ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return clicked;
    }
}

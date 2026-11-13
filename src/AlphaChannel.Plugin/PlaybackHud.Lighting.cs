using AlphaChannel.Plugin.Lighting;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class PlaybackHud
{
    private bool lightingIndoorTab, closeLightingPopup;
    internal void DrawHeaderLightingPanel(Vector2 buttonOrigin,bool clicked)
    {
        if(clicked)
        {
            if(ImGui.IsPopupOpen("##playbackHudLighting")) closeLightingPopup=true;
            else { lightingIndoorTab=WorldLightingController.CanUseIndoor; ImGui.OpenPopup("##playbackHudLighting"); }
        }
        DrawLightingPopup(buttonOrigin,buttonOrigin,DesktopMonitors.ForWindow(buttonOrigin,new Vector2(30)),DockColors.Accent,
            new Vector4(.47f,.49f,.57f,1),fromHeader:true);
    }
    private void DrawLightingPopup(Vector2 dock,Vector2 bulb,AlphaChannel.Plugin.Core.MonitorWorkArea monitor,Vector4 accent,Vector4 muted,bool fromHeader=false)
    {
        if(!ImGui.IsPopupOpen("##playbackHudLighting")) { closeLightingPopup=false; return; }
        var size=new Vector2(320,408);
        var above=!fromHeader&&dock.Y-monitor.Origin.Y>=size.Y+10;
        var at=monitor.Clamp(new Vector2(fromHeader?bulb.X+30-size.X:dock.X+Width-size.X,
            above?dock.Y-size.Y-10:fromHeader?bulb.Y+40:dock.Y+Height+10),size);
        ImGui.SetNextWindowPos(at,ImGuiCond.Always);
        ImGui.SetNextWindowSize(size,ImGuiCond.Always);
        using var colors=ImRaii.PushColor(ImGuiCol.PopupBg,WithAlpha(DockColors.WindowBg,.98f))
            .Push(ImGuiCol.Border,accent).Push(ImGuiCol.Text,Vector4.One);
        using var styles=ImRaii.PushStyle(ImGuiStyleVar.PopupRounding,CornerRadius)
            .Push(ImGuiStyleVar.PopupBorderSize,1.2f)
            .Push(ImGuiStyleVar.WindowPadding,new Vector2(16,16));
        if(!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup("##playbackHudLighting",ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoResize|
            ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoScrollbar|ImGuiWindowFlags.NoScrollWithMouse))) return;
        try
        {
            if(closeLightingPopup) { closeLightingPopup=false; ImGui.CloseCurrentPopup(); return; }
            var origin=ImGui.GetWindowPos();
            var draw=ImGui.GetWindowDrawList();
            var controller=Plugin.LightingControls;
            var canAdjust=lightingIndoorTab?WorldLightingController.CanUseIndoor:WorldLightingController.CanUseOutdoor;
            DrawIcon(draw,FontAwesomeIcon.Lightbulb,origin+new Vector2(16,17),24,accent);
            using(UiFonts.PushRelative(1.1f,emphasis:true)) UiFonts.DrawText(draw, ImGui.GetFontSize(), origin+new Vector2(52,18), Color(new Vector4(.886f,.906f,.937f,1f)), "Lighting");
            ImGui.SetCursorScreenPos(origin+new Vector2(281,16));
            if(ImGui.InvisibleButton("##lightingClose",new Vector2(24)))
                ImGui.CloseCurrentPopup();
            var closeInk=ImGui.IsItemHovered()?Vector4.One:muted;
            draw.AddLine(origin+new Vector2(285,20),origin+new Vector2(301,36),Color(closeInk),1.8f);
            draw.AddLine(origin+new Vector2(301,20),origin+new Vector2(285,36),Color(closeInk),1.8f);

            using(UiDisabled.Scope(!WorldLightingController.CanUseIndoor))
            if(LightingButton(draw,origin+new Vector2(16,54),new Vector2(144,34),"lightingIndoor","Indoor",lightingIndoorTab,accent,disabled:!WorldLightingController.CanUseIndoor))
                lightingIndoorTab=true;
            if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)&&!WorldLightingController.CanUseIndoor)
                UiFonts.SetTooltip("Indoor lighting is available inside housing interiors.");
            using(UiDisabled.Scope(!WorldLightingController.CanUseOutdoor))
            if(LightingButton(draw,origin+new Vector2(160,54),new Vector2(144,34),"lightingOutdoor","Outdoor",!lightingIndoorTab,accent,disabled:!WorldLightingController.CanUseOutdoor))
                lightingIndoorTab=false;
            if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)&&!WorldLightingController.CanUseOutdoor)
                UiFonts.SetTooltip("Outdoor lighting is available outside housing interiors.");
            canAdjust=lightingIndoorTab?WorldLightingController.CanUseIndoor:WorldLightingController.CanUseOutdoor;
            var level=lightingIndoorTab?controller.IndoorLevel:controller.OutdoorLevel;
            draw.AddText(origin+new Vector2(16,108),Color(Vector4.One),"Brightness");
            var percent=$"{level:P0}";
            draw.AddText(origin+new Vector2(304-ImGui.CalcTextSize(percent).X,108),Color(Vector4.One),percent);
            ImGui.SetCursorScreenPos(origin+new Vector2(16,130));
            ImGui.SetNextItemWidth(288);
            using(UiDisabled.Scope(!canAdjust))
            using(ImRaii.PushColor(ImGuiCol.FrameBg,Vector4.Zero)
                .Push(ImGuiCol.FrameBgHovered,Vector4.Zero).Push(ImGuiCol.FrameBgActive,Vector4.Zero)
                .Push(ImGuiCol.SliderGrab,Vector4.Zero).Push(ImGuiCol.SliderGrabActive,Vector4.Zero))
            if(ImGui.SliderFloat("##lightingBrightness",ref level,0,1,"",ImGuiSliderFlags.AlwaysClamp))
            {
                if(lightingIndoorTab)
                { if(controller.IndoorActive) controller.SetIndoorLevel(level); else controller.StartIndoor(level); }
                else
                { if(controller.OutdoorActive) controller.SetOutdoorLevel(level); else controller.StartOutdoor(level); }
            }
            var railMin=origin+new Vector2(16,139);
            draw.AddRectFilled(railMin,railMin+new Vector2(288,7),Color(new Vector4(.21f,.27f,.37f,1)),4);
            draw.AddRectFilled(railMin,railMin+new Vector2(MathF.Max(1,288*level),7),Color(accent),4);
            var knob=railMin+new Vector2(288*level,3.5f);
            draw.AddCircleFilled(knob,8,Color(accent));
            draw.AddCircleFilled(knob,5,Color(Vector4.One));
            draw.AddText(origin+new Vector2(16,159),Color(muted),"Dark");
            draw.AddText(origin+new Vector2(304-ImGui.CalcTextSize("Normal").X,159),Color(muted),"Normal");

            var canTime = canAdjust && controller.CanAdjustTime;
            var minutes = controller.TimeMinutes;
            draw.AddText(origin+new Vector2(16,188),Color(Vector4.One),"Time of day");
            var timeLabel = $"{minutes / 60:00}:{minutes % 60:00}";
            draw.AddText(origin+new Vector2(304-ImGui.CalcTextSize(timeLabel).X,188),Color(Vector4.One),timeLabel);
            ImGui.SetCursorScreenPos(origin+new Vector2(16,210));
            ImGui.SetNextItemWidth(288);
            using(UiDisabled.Scope(!canTime))
            using(ImRaii.PushColor(ImGuiCol.FrameBg,Vector4.Zero)
                .Push(ImGuiCol.FrameBgHovered,Vector4.Zero).Push(ImGuiCol.FrameBgActive,Vector4.Zero)
                .Push(ImGuiCol.SliderGrab,Vector4.Zero).Push(ImGuiCol.SliderGrabActive,Vector4.Zero))
                if(ImGui.SliderInt("##lightingTime",ref minutes,0,1439,"",ImGuiSliderFlags.AlwaysClamp))
                    controller.SetTimeMinutes(minutes);
            if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                UiFonts.SetTooltip(canTime ? "Hold the local lighting at this time. Indoor window lighting varies by room." : controller.TimeStatus);
            var timeRail=origin+new Vector2(16,219);
            var timeAccent=canTime?accent:muted;
            var timeFraction=minutes/1439f;
            draw.AddRectFilled(timeRail,timeRail+new Vector2(288,7),Color(new Vector4(.21f,.27f,.37f,1)),4);
            draw.AddRectFilled(timeRail,timeRail+new Vector2(MathF.Max(1,288*timeFraction),7),Color(timeAccent),4);
            var timeKnob=timeRail+new Vector2(288*timeFraction,3.5f);
            draw.AddCircleFilled(timeKnob,8,Color(timeAccent));
            draw.AddCircleFilled(timeKnob,5,Color(Vector4.One));
            draw.AddText(origin+new Vector2(16,239),Color(muted),"00:00");
            draw.AddText(origin+new Vector2(304-ImGui.CalcTextSize("23:59").X,239),Color(muted),"23:59");
            using(UiDisabled.Scope(!canTime || !controller.CustomTimeActive))
                if(LightingButton(draw,origin+new Vector2(16,261),new Vector2(288,26),"lightingTimeReset","Use current time",
                    false,accent,reset:true,disabled:!canTime || !controller.CustomTimeActive)) controller.ResetTime();
            if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                UiFonts.SetTooltip(canTime ? "Resume the game's current time of day." : controller.TimeStatus);

            var atmosphere=controller.DimAtmosphere;
            var sceneLights=controller.DimSceneLights;
            using(UiDisabled.Scope(lightingIndoorTab||!canAdjust))
            {
                if(LightingButton(draw,origin+new Vector2(16,296),new Vector2(139,36),"lightingAtmosphere","Atmosphere",atmosphere,accent,check:true,disabled:lightingIndoorTab||!canAdjust))
                { controller.SetExtraControls(!atmosphere,sceneLights); EnsureOutdoorLighting(controller,level); }
                if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    UiFonts.SetTooltip(lightingIndoorTab?"Additional dimming currently applies to outdoor lighting.":"Dim moon, fog and clouds along with outdoor brightness.");
                if(LightingButton(draw,origin+new Vector2(165,296),new Vector2(139,36),"lightingSceneLights","Scene lights",sceneLights,accent,check:true,disabled:lightingIndoorTab||!canAdjust))
                { controller.SetExtraControls(controller.DimAtmosphere,!sceneLights); EnsureOutdoorLighting(controller,level); }
                if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    UiFonts.SetTooltip(lightingIndoorTab?"Additional dimming currently applies to outdoor lighting.":"Dim lamps and other scene lights along with outdoor brightness.");
            }
            var small=ImGui.GetFontSize()*.90f;
            UiFonts.DrawText(draw, small, origin+new Vector2(16,339), Color(muted), "Moon, fog and clouds");
            UiFonts.DrawText(draw, small, origin+new Vector2(165,339), Color(muted), "Lamps and other lights");
            if(LightingButton(draw,origin+new Vector2(16,366),new Vector2(288,30),"lightingReset","Reset lighting",false,accent,reset:true))
                controller.ResetAll();
            if(ImGui.IsItemHovered()) UiFonts.SetTooltip("Restore current time, original lighting and turn off extra dimming.");
            // A small pointer towards the bulb, matching the mockup.
            if(above||fromHeader)
            {
                var x=Math.Clamp(bulb.X+15,origin.X+14,origin.X+size.X-14);
                var y=fromHeader?origin.Y:origin.Y+size.Y;
                var direction=fromHeader?-1f:1;
                var foreground=ImGui.GetForegroundDrawList();
                foreground.AddTriangleFilled(new Vector2(x-7,y-direction),new Vector2(x+7,y-direction),new Vector2(x,y+direction*7),Color(DockColors.WindowBg));
                foreground.AddLine(new Vector2(x-7,y),new Vector2(x,y+direction*7),Color(accent),1.2f);
                foreground.AddLine(new Vector2(x,y+direction*7),new Vector2(x+7,y),Color(accent),1.2f);
            }
            if(!canAdjust || (level<1 && !(lightingIndoorTab?controller.IndoorActive:controller.OutdoorActive)))
            {
                DrawIcon(draw,FontAwesomeIcon.ExclamationTriangle,origin+new Vector2(250,22),12,new Vector4(1,.65f,.2f,1));
                ImGui.SetCursorScreenPos(origin+new Vector2(246,16));
                ImGui.InvisibleButton("##lightingUnavailable",new Vector2(25));
                if(ImGui.IsItemHovered()) UiFonts.SetTooltip(canAdjust?controller.Status:"Lighting is unavailable while changing areas.");
            }
        }
        finally { ImGui.EndPopup(); }
    }
    private static void EnsureOutdoorLighting(WorldLightingController controller,float level)
    { if(!controller.OutdoorActive) controller.StartOutdoor(level); }
    private static bool LightingButton(ImDrawListPtr draw,Vector2 at,Vector2 size,string id,string text,bool selected,
        Vector4 accent,bool check=false,bool reset=false,bool disabled=false)
    {
        ImGui.SetCursorScreenPos(at);
        var clicked=ImGui.InvisibleButton("##"+id,size)&&!disabled;
        var hovered=!disabled&&ImGui.IsItemHovered();
        var alpha=disabled?.4f:1;
        // Disabled controls must not brighten when focus handling resets the global style alpha.
        uint ButtonColor(Vector4 color) => disabled ? ImGui.ColorConvertFloat4ToU32(color) : Color(color);
        var fill=selected?(check?Vector4.Lerp(DockColors.CardBg,accent,.30f):accent):DockColors.WindowBg;
        draw.AddRectFilled(at,at+size,ButtonColor(WithAlpha(hovered?Vector4.Lerp(fill,accent,.15f):fill,alpha)),6);
        draw.AddRect(at,at+size,ButtonColor(WithAlpha(accent,alpha)),6,ImDrawFlags.None,1);
        var ink=WithAlpha(reset?accent:Vector4.One,alpha);
        var textSize=ImGui.CalcTextSize(text);
        var total=textSize.X+(check||reset?24:0);
        var start=at+new Vector2((size.X-total)*.5f,(size.Y-textSize.Y)*.5f);
        if(check)
        {
            draw.AddRectFilled(start+new Vector2(-1,-2),start+new Vector2(17,textSize.Y+2),ButtonColor(WithAlpha(selected?accent:DockColors.FrameBg,alpha)),4);
            if(selected) UiFonts.DrawIcon(draw, 12, start+new Vector2(2,1), ButtonColor(ink), FontAwesomeIcon.Check.ToIconString());
            start.X+=24;
        }
        else if(reset) { DrawIcon(draw,FontAwesomeIcon.Undo,start,16,ink); start.X+=24; }
        draw.AddText(start,ButtonColor(ink),text);
        return clicked;
    }
}

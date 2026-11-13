using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawPlayerHubPanel(string id,float width,float height,bool prominent,Action content,float contentPadding=18f)
    {
        var origin=ImGui.GetCursorScreenPos();
        DrawArcadeGradientPanel(ImGui.GetWindowDrawList(),origin,new(width,height),
            HubSurface(prominent?0x10203A:0x101A2D),HubSurface(0x0A1222),
            prominent?HubTint(0x3478BA):HubTint(0x24456B),10);
        using var padding=ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,UiVec(contentPadding,contentPadding))
            .Push(ImGuiStyleVar.ChildRounding,Ui(10));
        using var colors=ImRaii.PushColor(ImGuiCol.ChildBg,Vector4.Zero);
        using var panel=UiFonts.Window(ImGui.GetFontSize(),ImRaii.Child(id,new(width,height),false,
            ImGuiWindowFlags.AlwaysUseWindowPadding|ImGuiWindowFlags.NoScrollbar|ImGuiWindowFlags.NoScrollWithMouse));
        if(panel) content();
    }

    // Theme-aware, passive vector artwork: no image downloads or playback work.
    private static void DrawPlayerEmptyArtwork(ImDrawListPtr draw,Vector2 origin,float scale)
    {
        Vector2 P(float x,float y)=>origin+new Vector2(x,y)*scale;
        uint Tint(float opacity,bool light=false)=>ImGui.GetColorU32((light?AccentHover:Accent) with {W=opacity});
        draw.AddLine(P(0,146),P(203,146),Tint(.24f),scale);
        draw.AddRectFilled(P(39,19),P(190,119),ImGui.GetColorU32(HubSurface(0x091529)),5*scale);
        draw.AddRectFilledMultiColor(P(44,24),P(185,114),Tint(.14f),Tint(.05f),Tint(.02f),Tint(.1f));
        draw.AddRect(P(39,19),P(190,119),Tint(.6f,true),5*scale,ImDrawFlags.None,2*scale);
        draw.AddRectFilled(P(106,120),P(124,132),Tint(.3f));
        draw.AddRectFilled(P(86,132),P(146,136),Tint(.5f),2*scale);
        draw.AddRectFilled(P(66,139),P(166,145),Tint(.18f),2*scale);
        draw.AddCircleFilled(P(115,69),22*scale,Tint(.22f),40);
        draw.AddCircle(P(115,69),22*scale,Tint(.7f,true),40,1.5f*scale);
        draw.AddTriangleFilled(P(110,59),P(110,79),P(125,69),Tint(.95f,true));
        // Small plant and books keep the scene grounded without a large hero.
        draw.AddQuadFilled(P(10,125),P(31,125),P(28,144),P(13,144),Tint(.32f));
        draw.AddLine(P(21,125),P(21,99),Tint(.45f),1.5f*scale);
        draw.AddBezierCubic(P(21,116),P(9,114),P(8,104),P(10,102),Tint(.32f),6*scale);
        draw.AddBezierCubic(P(21,108),P(31,103),P(34,96),P(32,94),Tint(.4f),6*scale);
        draw.AddRectFilled(P(173,139),P(196,144),Tint(.26f),scale);
        draw.AddRectFilled(P(177,133),P(199,138),Tint(.18f),scale);
        draw.AddRectFilled(P(25,42),P(29,46),Tint(.55f));
        draw.AddRectFilled(P(8,62),P(13,67),Tint(.32f));
        draw.AddRectFilled(P(197,96),P(201,100),Tint(.45f));
    }
}

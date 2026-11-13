using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static void DrawSpookyPreviewSprite(ImDrawListPtr draw, Vector2 centre, float radius, int kind, float opacity = 1)
    {
        uint Colour(float r, float g, float b, float a = 1) => ImGui.GetColorU32(new Vector4(r, g, b, a * opacity));
        Vector2 P(float x, float y) => centre + new Vector2(x, y) * radius;
        if (kind == 0)
        {
            void Ellipse(float x, float y, float rx, float ry, uint colour)
            {
                for (var i = 0; i < 32; i++)
                {
                    var angle = i * MathF.Tau / 32;
                    draw.PathLineTo(P(x + MathF.Cos(angle) * rx, y + MathF.Sin(angle) * ry));
                }
                draw.PathFillConvex(colour);
            }
            Ellipse(-.35f, .05f, .61f, .72f, Colour(.72f, .22f, .015f));
            Ellipse(.35f, .05f, .61f, .72f, Colour(.78f, .26f, .015f));
            Ellipse(0, .04f, .70f, .77f, Colour(.96f, .37f, .025f));
            Ellipse(-.08f, -.02f, .35f, .68f, Colour(1, .46f, .045f));
            for (var rib = -1; rib <= 1; rib++)
            {
                for (var i = 0; i < 20; i++)
                {
                    var y = -.67f + i * .072f;
                    var x = rib * .34f * MathF.Sqrt(Math.Max(0, 1-y*y/.64f));
                    draw.PathLineTo(P(x,y));
                }
                draw.PathStroke(Colour(.60f,.18f,.01f,.45f), ImDrawFlags.None, Math.Max(.6f,radius*.035f));
            }
            for (var side = -1; side <= 1; side += 2)
            {
                draw.AddTriangleFilled(P(side * .32f, -.37f), P(side * .32f - .13f, -.10f), P(side * .32f + .13f, -.10f), Colour(1, .79f, .16f));
            }
            draw.AddQuadFilled(P(-.12f,-1.04f), P(.03f,-1.04f), P(.09f,-.62f), P(-.08f,-.62f), Colour(.25f,.32f,.065f));
            draw.AddTriangleFilled(P(0,-.09f),P(-.075f,.08f),P(.075f,.08f),Colour(1,.79f,.16f));
            for (var i = 0; i < 20; i++)
            {
                var x = -.53f + i * .053f;
                draw.AddLine(P(x,.24f-.32f*x*x),P(x,.48f-.62f*x*x),Colour(1,.79f,.16f),Math.Max(.6f,radius*.055f));
            }
            draw.AddRectFilled(P(-.225f,.23f),P(-.135f,.31f),Colour(.94f,.34f,.025f));
            draw.AddRectFilled(P(.145f,.37f),P(.235f,.45f),Colour(.94f,.34f,.025f));
        }
        else if (kind == 1)
        {
            var white = Colour(.78f, .91f, 1, .8f);
            draw.AddCircleFilled(P(0, -.3f), radius * .66f, white, 20);
            draw.AddRectFilled(P(-.66f, -.3f), P(.66f, .55f), white);
            for (var i = -1; i <= 1; i++) draw.AddCircleFilled(P(i * .44f, .55f), radius * .22f, white, 12);
            for (var side = -1; side <= 1; side += 2) draw.AddCircleFilled(P(side * .23f, -.22f), radius * .11f, Colour(.12f, .09f, .25f), 12);
        }
    }

    private void DrawSpookyPreviewDecorations(ImDrawListPtr draw, Vector2 min, Vector2 max)
    {
        var centre = (min + max) / 2;
        var w = max.X - min.X;
        var h = max.Y - min.Y;
        for (var side = -1; side <= 1; side += 2)
        {
            var pumpkinRadius = w * .085f;
            DrawSpookyPreviewSprite(draw, new Vector2(centre.X + side * w * .62f,
                min.Y + h * .03f - pumpkinRadius * .82f), pumpkinRadius, 0, .55f);
            DrawSpookyPreviewSprite(draw, centre + new Vector2(side * w * 1.1f, -h * .18f), h * .15f, 1);
        }
        for (var i = 0; i < 10; i++)
        {
            var angle = i * MathF.Tau / 10;
            var pos = centre + new Vector2(MathF.Cos(angle) * w * 1.12f, MathF.Sin(angle) * h * 1.1f);
            var tint = i % 2 == 0 ? new Vector3(1, .35f, .06f) : new Vector3(.65f, .18f, 1);
            for (var glow = 4; glow > 0; glow--) draw.AddCircleFilled(pos, Ui(glow * 2), ImGui.GetColorU32(new Vector4(tint, .08f)), 16);
            draw.AddCircleFilled(pos, Ui(2), ImGui.GetColorU32(new Vector4(tint, .9f)), 12);
        }
    }

    private void DrawSpookyTimeOption(ref MusicSceneEffects scene)
    {
        using var id = ImRaii.PushId("spookyTime");
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = Math.Max(Ui(34), ImGui.GetFrameHeight());
        var orange = new Vector4(1, .53f, .14f, 1);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + new Vector2(width, height), ImGui.GetColorU32(new Vector4(.19f, .10f, .24f, 1)), Ui(6));
        draw.AddRect(start, start + new Vector2(width, height), ImGui.GetColorU32(orange with { W = .65f }), Ui(6));
        using (UiFonts.PushIcon())
            draw.AddText(start + UiVec(8, 9), ImGui.GetColorU32(orange), FontAwesomeIcon.Ghost.ToIconString());
        draw.AddText(start + UiVec(32, 9), ImGui.GetColorU32(orange), "Spooky Time");
        ImGui.SetCursorScreenPos(start + new Vector2(width - Ui(82), (height - Ui(24)) / 2));
        if (GameLayoutButton("", FontAwesomeIcon.Cog, Ui(26), height: 24)) ImGui.OpenPopup("##settings");
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Spooky Time floor settings");
        ImGui.SetCursorScreenPos(start + new Vector2(width - Ui(46), (height - Ui(20)) / 2));
        var enabled = scene.IsSpooky;
        if (DrawSettingsToggle("##toggle", "", ref enabled))
        {
            musicEffects = scene = scene with { Mask = enabled ? scene.Mask | MusicSceneEffects.SpookyTime : scene.Mask & ~MusicSceneEffects.SpookyTime };
            ApplyMusicScene();
        }
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Halloween decorations and a haunted dance floor. Replaces the normal dance floor.");
        if (ImGui.IsPopupOpen("##settings")) ImGui.SetNextWindowSize(new Vector2(Ui(280), 0), ImGuiCond.Always);
        using (var popupStyle = ImRaii.PushStyle(ImGuiStyleVar.PopupBorderSize, Ui(1f))
                   .Push(ImGuiStyleVar.PopupRounding, Ui(8f)))
        using (var colours = ImRaii.PushColor(ImGuiCol.PopupBg, CardBg).Push(ImGuiCol.Border, Accent)
                   .Push(ImGuiCol.Text, Hex(0xE2E7EF)))
        using (var popup = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Popup("##settings", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (popup)
            {
                using (UiFonts.PushRelative(1f, emphasis: true)) ImGui.TextUnformatted("Spooky Time");
                ImGui.Separator(); ImGui.Spacing();
                ImGui.TextUnformatted("Height offset");
                var offset = scene.FloorOffset;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderFloat("##floorOffset", ref offset, -5, 5, "%.2f"))
                { musicEffects = scene = scene with { FloorOffset = offset }; ApplyMusicScene(); }
            }
        }
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(width, height));
        ImGui.Dummy(UiVec(0, 9));
    }
}

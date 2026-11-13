using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawSidebarDivider()
    {
        var origin = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(origin,
            origin + new Vector2(ImGui.GetContentRegionAvail().X, 0f),
            ImGui.GetColorU32(BorderSubtle), Ui(1f));
        ImGui.Dummy(UiVec(0f, 1f));
    }

    private static Vector2 MeasureSidebarIcon(string icon)
    {
        using var font = UiFonts.PushIcon();
        return ImGui.CalcTextSize(icon);
    }

    // The bundled icon font has no brand glyphs. Draw the small Discord mark
    // directly so it remains sharp at every interface scale.
    private static void DrawSidebarDiscordIcon(Vector2 origin, Vector2 size, Vector4 ink, Vector4 background)
    {
        var draw = ImGui.GetWindowDrawList();
        Vector2 P(float x, float y) => origin + new Vector2(size.X * x, size.Y * y);
        foreach (var point in new Vector2[]
        {
            P(0f, .76f), P(.05f, .4f), P(.18f, .12f), P(.35f, .07f),
            P(.65f, .07f), P(.82f, .12f), P(.95f, .4f), P(1f, .76f),
            P(.8f, .9f), P(.2f, .9f),
        })
            draw.PathLineTo(point);
        draw.PathFillConvex(ImGui.GetColorU32(ink));
        var cutout = ImGui.GetColorU32(background);
        draw.AddCircleFilled(P(.34f, .52f), size.Y * .13f, cutout);
        draw.AddCircleFilled(P(.66f, .52f), size.Y * .13f, cutout);
        draw.AddTriangleFilled(P(.34f, .9f), P(.42f, .76f), P(.5f, .9f), cutout);
        draw.AddTriangleFilled(P(.5f, .9f), P(.58f, .76f), P(.66f, .9f), cutout);
    }

    private void DrawSidebarCollapseToggle()
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = UiVec(28f, 28f);
        if (ImGui.InvisibleButton("##sidebarCollapse", size))
        {
            // Use the main window's actual size, not this sidebar child's dimensions.
            Plugin.Cfg.SidebarCollapsed = !sidebarCollapsedForFrame;
            userWindowSize = new Vector2(
                Math.Clamp(sidebarMainWindowSize.X, CurrentMinimumWindowSize.X, CurrentMaximumWindowSize.X),
                sidebarMainWindowSize.Y);
            var sizeChanged = MathF.Abs(userWindowSize.X - sidebarMainWindowSize.X) > .01f;
            userResized |= sizeChanged;
            Plugin.Cfg.WindowWidth = userWindowSize.X;
            Plugin.Cfg.WindowHeight = userWindowSize.Y;
            windowSizeSavePending = false;
            Plugin.Cfg.Save();
        }

        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size,
            ImGui.GetColorU32(hovered ? Accent : CardBgHover), Ui(6f));
        draw.AddRect(origin, origin + size,
            ImGui.GetColorU32(BorderSubtle),
            Ui(6f), ImDrawFlags.None, Ui(1f));
        var icon = (sidebarCollapsedForFrame ? FontAwesomeIcon.AngleDoubleRight : FontAwesomeIcon.AngleDoubleLeft)
            .ToIconString();
        var iconSize = MeasureSidebarIcon(icon);
        UiFonts.DrawIcon(draw, ImGui.GetFontSize(), origin + (size - iconSize) * 0.5f, ImGui.GetColorU32(hovered ? Vector4.One : MutedText), icon);
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            UiFonts.SetTooltip(sidebarCollapsedForFrame ? "Expand sidebar" : "Collapse sidebar");
        }
    }
}

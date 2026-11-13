using System;
using System.Numerics;
using AlphaChannel.Plugin.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private double roomClosingNoticeStarted = -1d;

    private void ShowRoomClosingNotice()
    {
        joinError = null;
        roomClosingNoticeStarted = ImGui.GetTime();
    }

    private void DrawRoomClosingNotice()
    {
        DrawSlidingNotice("##roomClosingNotice",ref roomClosingNoticeStarted,RoomCloseReconnectState.Message);
    }

    private void DrawSlidingNotice(string id, ref double started, string message)
    {
        if (started < 0d) return;
        const double duration = 3.2d, slideDuration = .24d;
        var elapsed = ImGui.GetTime() - started;
        if (elapsed >= duration) { started = -1d; return; }
        var progress = (float)Math.Clamp(Math.Min(elapsed, duration - elapsed) / slideDuration, 0d, 1d);
        progress = progress * progress * (3f - 2f * progress);
        var parent = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var width=MathF.Min(MathF.Max(Ui(480f),ImGui.CalcTextSize(message).X+Ui(58f)),parentSize.X-SidebarWidth-Ui(24f));
        var textSize=ImGui.CalcTextSize(message,false,MathF.Max(1f,width-Ui(54f)));
        var size = new Vector2(width, MathF.Max(Ui(54f),textSize.Y+Ui(24f)));
        var origin = new Vector2(parent.X + SidebarWidth + (parentSize.X - SidebarWidth - size.X) * .5f,
            parent.Y - size.Y + (size.Y + Ui(8f)) * progress);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs;
        if (BeginWindowOverlay(id, flags))
        {
            var draw = ImGui.GetWindowDrawList();
            draw.PushClipRect(parent, parent + parentSize, false);
            draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(CardBg), Ui(9f));
            draw.AddRect(origin, origin + size, ImGui.GetColorU32(Accent), Ui(9f));
            var icon = FontAwesomeIcon.InfoCircle.ToIconString();
            var iconSize = MeasureSidebarIcon(icon);
            UiFonts.DrawIcon(draw, ImGui.GetFontSize(), origin + new Vector2(Ui(14f), (size.Y - iconSize.Y) * .5f), ImGui.GetColorU32(AccentHover), icon);
            UiFonts.DrawText(draw, ImGui.GetFontSize(), origin + new Vector2(Ui(40f), (size.Y - textSize.Y) * .5f), ImGui.GetColorU32(Vector4.One), message, MathF.Max(1f,width-Ui(54f)));
            draw.PopClipRect();
        }
        ImGui.End();
    }
}

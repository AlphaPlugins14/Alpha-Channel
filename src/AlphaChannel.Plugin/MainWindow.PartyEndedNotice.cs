using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private int viewerPartyEndedNoticePending;
    private bool viewerPartyEndedNoticeVisible;

    private void DrawViewerPartyEndedNotice()
    {
        if (!viewerPartyEndedNoticeVisible) return;
        var origin = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var panelSize = new Vector2(MathF.Min(Ui(460f), size.X - Ui(32f)), Ui(180f));
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##viewerPartyEndedNotice", flags)) { ImGui.End(); return; }
        ImGui.GetWindowDrawList().AddRectFilled(origin, origin + size,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.4f)));
        ImGui.SetCursorScreenPos(origin + (size - panelSize) * 0.5f);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(24f, 20f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, Accent))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##viewerPartyEndedNoticePanel", panelSize, true)))
        {
            if (panel)
            {
                ImGui.TextColored(Accent, "Watch Party ended");
                ImGui.Spacing();
                ImGui.TextWrapped("The host ended the Watch Party. You are no longer connected to that party.");
                ImGui.Spacing();
                using (ImRaii.PushColor(ImGuiCol.Button, Accent).Push(ImGuiCol.ButtonHovered, AccentHover))
                {
                    if (ImGui.Button("OK", UiVec(100f, 34f))) viewerPartyEndedNoticeVisible = false;
                }
            }
        }
        ImGui.End();
    }
}

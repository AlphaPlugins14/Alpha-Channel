using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private long youtubeSetupNoticeUntilTicks;
    private int youtubeSetupNoticeSeconds;

    internal void ShowYouTubeSetupNotice(int seconds)
    {
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref youtubeSetupNoticeUntilTicks)) return;
        youtubeSetupNoticeSeconds = seconds;
        Interlocked.Exchange(ref youtubeSetupNoticeUntilTicks, DateTime.UtcNow.AddSeconds(3).Ticks);
        OpenUi();
    }

    private void DrawYouTubeSetupNotice()
    {
        if (DateTime.UtcNow.Ticks >= Interlocked.Read(ref youtubeSetupNoticeUntilTicks)) return;
        var parentPosition = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var panelSize = new Vector2(MathF.Min(Ui(490f), parentSize.X - Ui(32f)), MathF.Min(Ui(180f), parentSize.Y - Ui(32f)));
        ImGui.SetNextWindowPos(parentPosition, ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##youtubeSetupNotice", flags)) { ImGui.End(); return; }
        ImGui.GetWindowDrawList().AddRectFilled(parentPosition, parentPosition + parentSize,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.4f)));
        ImGui.SetCursorScreenPos(parentPosition + (parentSize - panelSize) * 0.5f);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f)).Push(ImGuiStyleVar.WindowPadding, UiVec(24f, 20f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.025f, 0.03f, 0.06f, 0.995f))
            .Push(ImGuiCol.Border, Accent))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##youtubeSetupNoticePanel", panelSize, true)))
        {
            if (panel)
            {
                ImGui.TextColored(Accent, "YouTube playback setup");
                ImGui.Spacing();
                ImGui.TextWrapped("Alpha Channel is currently completing the first-time setup for YouTube playback.");
                ImGui.Spacing();
                ImGui.TextWrapped($"Please try to play the video again in about {youtubeSetupNoticeSeconds} seconds.");
            }
        }
        ImGui.End();
    }
}

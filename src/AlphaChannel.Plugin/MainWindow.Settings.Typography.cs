using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static Vector4 SettingsReviewBorder => settingsControlsActive ? AccentInk(.35f) : BorderSubtle;

    private static void DrawSettingsHelper(string text)
    {
        using var font=settingsControlsActive ? UiFonts.PushRelative(.90f) : UiFonts.Empty;
        ImGui.TextColored(MutedText,text);
    }

    private static void DrawSettingsExplanation(string text)
    {
        using var font=settingsControlsActive ? UiFonts.PushRelative(.90f) : UiFonts.Empty;
        using var color=ImRaii.PushColor(ImGuiCol.Text,settingsControlsActive ? MutedText : ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
        ImGui.TextWrapped(text);
    }
}

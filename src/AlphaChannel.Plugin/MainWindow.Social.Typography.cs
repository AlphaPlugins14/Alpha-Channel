using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static void DrawSocialHeading(string text, float scale = 1f)
    {
        using var font = UiFonts.PushRelative(scale, emphasis: true);
        ImGui.TextColored(Hex(0xE2E7EF), text);
    }

    private static void DrawSocialHelper(string text)
    {
        using var font = UiFonts.PushRelative(.90f);
        ImGui.TextColored(MutedText, text);
    }

    private static void DrawSocialHelperWrapped(string text)
    {
        using var font = UiFonts.PushRelative(.90f);
        using var color = Dalamud.Interface.Utility.Raii.ImRaii.PushColor(ImGuiCol.Text, MutedText);
        ImGui.TextWrapped(text);
    }
}

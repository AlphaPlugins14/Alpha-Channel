using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawAudioOnlyStatus()
    {
        using (UiFonts.PushIcon()) ImGui.TextColored(AccentHover, FontAwesomeIcon.Headphones.ToIconString());
        ImGui.SameLine(0, Ui(6));
        ImGui.TextColored(AccentHover, "Audio Only");
    }
}

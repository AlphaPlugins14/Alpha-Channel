using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    internal static ImGuiWindowFlags AutomaticPageScrollbars(ImGuiWindowFlags flags) =>
        flags & ~ImGuiWindowFlags.AlwaysVerticalScrollbar;
}

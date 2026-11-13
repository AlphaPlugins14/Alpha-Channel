using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal static class ChatPointerInput
{
    // Coordinate-only checks bypass ImGui's popup/window ownership. Match native
    // widgets: the current window must actually be receiving pointer input.
    // Keep hover-only controls submitted while their button owns the active ID,
    // including the release frame; popup and overlapping-window blocking remains.
    internal static bool IsHoveringRect(Vector2 minimum, Vector2 maximum) =>
        ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) && ImGui.IsMouseHoveringRect(minimum, maximum);
}

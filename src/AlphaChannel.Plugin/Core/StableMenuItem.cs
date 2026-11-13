using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin.Core;

internal static class StableMenuItem
{
    internal static bool Draw(string label,bool enabled,Vector4 muted)
    {
        if (enabled) return ImGui.MenuItem(label,"",false,true);
        // Inactive input masking sets DisabledAlpha=1, so both label colour
        // paths must explicitly remain muted rather than relying on opacity.
        ImGui.PushStyleColor(ImGuiCol.Text,muted);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled,muted);
        try { return ImGui.MenuItem(label,"",false,false); }
        finally { ImGui.PopStyleColor(2); }
    }
}

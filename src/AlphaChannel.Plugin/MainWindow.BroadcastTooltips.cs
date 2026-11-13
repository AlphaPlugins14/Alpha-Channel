using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawMissingBroadcastStreamKeyTooltip(bool startAction = true)
    {
        if (startAction && AccountBroadcastRestricted)
        {
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                UiFonts.SetTooltip("Broadcasting requires XIVAuth verification or Patreon");
            return;
        }
        if (startAction && CurrentSession is { } session &&
            string.IsNullOrWhiteSpace(Plugin.Cfg.StreamKeys.GetValueOrDefault(session.AccountId)) &&
            ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiFonts.SetTooltip("Generate stream key in settings");
    }
}

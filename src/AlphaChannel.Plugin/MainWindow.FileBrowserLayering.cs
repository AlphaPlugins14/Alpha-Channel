using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static void DrawFileBrowserAboveOverlay(Action drawBrowser)
    {
        // FileDialog's internal window name need not start with its visible
        // title. Identify windows submitted by Draw rather than guessing names.
        var context = ImGui.GetCurrentContext();
        var before = context.Windows.AsSpan().ToArray().Where(window => window.Active)
            .ToDictionary(window => window.ID, window => window.BeginCount);
        drawBrowser();
        var windows = context.Windows.AsSpan().ToArray();
        foreach (var window in windows.Where(window => window.Active && !window.Hidden &&
            (window.Flags & ImGuiWindowFlags.ChildWindow) == 0 &&
            (!before.TryGetValue(window.ID, out var count) || window.BeginCount > count)))
            ImGuiP.BringWindowToDisplayFront(window);
        // Keep browser menus and existing modal popups above its main window.
        foreach (var window in windows.Where(window => (window.Flags & ImGuiWindowFlags.Popup) != 0 &&
            (window.Active || window.WasActive) && !window.Hidden))
            ImGuiP.BringWindowToDisplayFront(window);
    }
}

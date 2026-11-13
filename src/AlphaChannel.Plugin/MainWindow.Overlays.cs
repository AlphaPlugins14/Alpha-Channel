using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private uint draggingOverlayId;

    private void HandleOverlayBackdropDrag(ImGuiWindowPtr parent, ImGuiWindowPtr overlay)
    {
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) draggingOverlayId = 0;
        // Only full-window veils qualify. Small suggestions and notices are not
        // drag surfaces, and a child panel/dropdown must retain its own input.
        var root = parent.RootWindow;
        if ((overlay.Flags & ImGuiWindowFlags.NoMouseInputs) != 0 ||
            overlay.Size.X < root.Size.X * .9f || overlay.Size.Y < root.Size.Y * .9f) return;
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && ImGui.IsWindowHovered())
            draggingOverlayId = overlay.ID;
        if (draggingOverlayId == overlay.ID && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            ImGuiP.SetWindowPos(root, root.Pos + ImGui.GetIO().MouseDelta, ImGuiCond.None);
    }
    private static ImGuiWindowFlags MainWindowFocusFlags(ImGuiWindowFlags flags) =>
        flags & ~ImGuiWindowFlags.NoBringToFrontOnFocus;

    // Fixed header chrome belongs to the main window's display group. A child
    // stays above its page content without being raised above unrelated windows.
    private bool BeginWindowChrome(string id, System.Numerics.Vector2 size, ImGuiWindowFlags flags) =>
        UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild(id, size, false, flags));

    // These themed overlays use ordinary windows rather than BeginPopupModal.
    // Keep display order explicit; repeatedly setting keyboard focus can close
    // combo popups or let other overlays steal focus back on the next frame.
    private bool BeginWindowOverlay(string id, ImGuiWindowFlags flags)
    {
        var parent = ImGuiP.GetCurrentWindow();
        // This overlay covers the parent window, so both must render on the
        // same viewport/native surface. Automatic viewport selection can create
        // a separate platform window whose OS ordering is independent of ImGui.
        ImGui.SetNextWindowViewport(parent.ViewportId);
        var visible = UiFonts.Window(ImGui.GetFontSize(), ImGui.Begin(id, flags | ImGuiWindowFlags.NoFocusOnAppearing));
        var overlay = ImGuiP.GetCurrentWindow();
        HandleOverlayBackdropDrag(parent, overlay);
        // A popup from another window/plugin must not prevent raising this
        // overlay. Preserve existing popup order above it without changing focus.
        var popups = ImGui.GetCurrentContext().Windows.AsSpan().ToArray()
            .Where(window => (window.Flags & ImGuiWindowFlags.Popup) != 0 &&
                (window.Active || window.WasActive) && !window.Hidden).ToArray();
        ImGuiP.BringWindowToDisplayFront(overlay);
        foreach (var popup in popups)
            ImGuiP.BringWindowToDisplayFront(popup);

        return visible;
    }
}

using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin.Core;

internal sealed class PageNavigationScroll
{
    private object? lastKey;
    private int resetRequested;

    // Safe for framework/context-menu callbacks: no native UI calls here.
    internal void RequestReset() => Interlocked.Exchange(ref resetRequested, 1);

    internal void ResetIfChanged(object key)
    {
        var requested = Interlocked.Exchange(ref resetRequested, 0) != 0;
        if (!requested && Equals(lastKey, key)) return;
        lastKey = key;
        // SetScroll alone applies next frame. Clear the current offset as well,
        // so the first frame of a newly selected page starts at the top.
        var window = ImGuiP.GetCurrentWindow();
        var cursor = ImGui.GetCursorScreenPos() + window.Scroll;
        window.Scroll = Vector2.Zero;
        ImGui.SetScrollX(0f);
        ImGui.SetScrollY(0f);
        ImGui.SetCursorScreenPos(cursor);
    }
}

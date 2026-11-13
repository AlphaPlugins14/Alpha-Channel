using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal static class NativeUiSizingMetrics
{
    // The original layout is built around ImGui's 13px body text.
    internal const float DesignFontSize = 13f;
    internal static bool RecommendsNative(float width, float height) =>
        float.IsFinite(width) && float.IsFinite(height) &&
        MathF.Max(width, height) >= 3840f && MathF.Min(width, height) >= 2160f;

    internal static float LayoutScale(float actualWidth, float designWidth) =>
        float.IsFinite(actualWidth) && float.IsFinite(designWidth) && designWidth > 0f
            ? Math.Clamp(actualWidth / designWidth, .65f, 3f) : 1f;

    internal static float FontScale(float fontSize, float layoutScale) =>
        DesignFontSize * layoutScale / MathF.Max(1f, fontSize);
}

// Only scope this around the main window's Draw(), after its root window begins.
// Children, icon fonts, menus and overlays inherit the same global multiplier.
// Always restore it so unrelated Dalamud windows retain their own font settings.
internal readonly struct NativeUiFontScaleScope : IDisposable
{
    private readonly bool enabled;
    private readonly float previousGlobalScale;
    private readonly float previousWindowScale;

    internal NativeUiFontScaleScope(bool enabled, float layoutScale)
    {
        this.enabled = enabled;
        previousGlobalScale = ImGui.GetIO().FontGlobalScale;
        previousWindowScale = ImGuiP.GetCurrentWindow().FontWindowScale;
        if (!enabled) return;
        ImGui.GetIO().FontGlobalScale = NativeUiSizingMetrics.FontScale(ImGui.GetFont().FontSize, layoutScale);
        // Refresh ImGui's cached font metrics after changing the global multiplier.
        ImGui.PushFont(ImGui.GetFont());
        ImGui.SetWindowFontScale(1f);
    }

    public void Dispose()
    {
        if (!enabled) return;
        ImGui.GetIO().FontGlobalScale = previousGlobalScale;
        ImGui.PopFont();
        ImGui.SetWindowFontScale(previousWindowScale);
    }
}

// Each preview uses the same font sizing as its real page, independent of the
// chooser's selected mode. Restore both global and child-window font state.
internal readonly struct InterfaceSizingPreviewFontScope : IDisposable
{
    private readonly float previousGlobalScale;
    private readonly float previousWindowScale;

    internal InterfaceSizingPreviewFontScope(bool native, float scale, float standardGlobalScale)
    {
        previousGlobalScale = ImGui.GetIO().FontGlobalScale;
        previousWindowScale = ImGuiP.GetCurrentWindow().FontWindowScale;
        ImGui.GetIO().FontGlobalScale = native
            ? NativeUiSizingMetrics.FontScale(ImGui.GetFont().FontSize, scale)
            : standardGlobalScale * scale;
        ImGui.PushFont(ImGui.GetFont());
        ImGui.SetWindowFontScale(1f);
    }

    public void Dispose()
    {
        ImGui.GetIO().FontGlobalScale = previousGlobalScale;
        ImGui.PopFont();
        ImGui.SetWindowFontScale(previousWindowScale);
    }
}

// Block widgets and custom rectangle-based card clicks while preserving the
// homepage's normal appearance. Restore input before drawing the live chooser.
internal sealed class InterfaceSizingPageInputScope : IDisposable
{
    private readonly bool enabled;
    private readonly Vector2 mousePosition;
    private bool disposed;
    internal InterfaceSizingPageInputScope(bool enabled)
    {
        this.enabled = enabled;
        if (!enabled) return;
        mousePosition = ImGui.GetIO().MousePos;
        ImGui.GetIO().MousePos = new Vector2(-float.MaxValue);
        ImGui.PushStyleVar(ImGuiStyleVar.DisabledAlpha, 1f);
        ImGui.BeginDisabled();
    }
    public void Dispose()
    {
        if (!enabled || disposed) return;
        disposed = true;
        ImGui.EndDisabled();
        ImGui.PopStyleVar();
        ImGui.GetIO().MousePos = mousePosition;
    }
}

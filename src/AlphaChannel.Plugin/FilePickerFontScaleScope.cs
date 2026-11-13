using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

// Adapt the built-in picker's fixed 100 * global-scale footer reservation
// to our measured buttons, while keeping the baked fonts at their current size.
internal sealed class FilePickerFontScaleScope : IDisposable
{
    private readonly ImFontPtr text;
    private readonly ImFontPtr icons;
    private readonly float globalScale;
    private readonly float textScale;
    private readonly float iconScale;
    private bool disposed;

    internal FilePickerFontScaleScope(float requiredWidth, ImFontPtr iconFont)
    {
        var io = ImGui.GetIO();
        text = ImGui.GetFont();
        icons = iconFont;
        globalScale = io.FontGlobalScale;
        textScale = text.Scale;
        iconScale = icons.Scale;
        var pickerScale = MathF.Max(globalScale, requiredWidth / 100f);
        text.Scale = textScale * globalScale / pickerScale;
        icons.Scale = iconScale * globalScale / pickerScale;
        io.FontGlobalScale = pickerScale;
        UiFonts.RefreshMetrics();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        text.Scale = textScale;
        icons.Scale = iconScale;
        ImGui.GetIO().FontGlobalScale = globalScale;
        UiFonts.RefreshMetrics();
    }
}

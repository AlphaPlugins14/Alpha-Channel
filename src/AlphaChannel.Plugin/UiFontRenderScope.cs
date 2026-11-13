using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal enum UiFontFace { Text, Emphasis, Icon }

internal sealed class UiFontRenderScope : IDisposable
{
    private readonly float global,window;
    private readonly IDisposable? pushed;
    private readonly ImGuiWindowPtr owner;
    private bool disposed;

    internal UiFontRenderScope(ImFontPtr font,UiFontFace face,int size)
    {
        global=ImGui.GetIO().FontGlobalScale;
        owner=ImGuiP.GetCurrentWindow();
        window=owner.FontWindowScale;
        ImGui.GetIO().FontGlobalScale=1;
        // The cache retains the selected font's atlas through this draw frame.
        // A fallback is needed only before this size has ever been loaded.
        pushed=!font.IsNull ? ImRaii.PushFont(font) :
            face==UiFontFace.Icon?ImRaii.PushFont(UiBuilder.IconFont):null;
        if(pushed is null)RefreshMetrics();
        // Refresh metrics, then compensate for any viewport DPI factor already
        // present. The atlas size already includes our standard/native sizing.
        ImGui.SetWindowFontScale(1);
        ImGui.SetWindowFontScale(size/MathF.Max(1,ImGui.GetFontSize()));
    }

    public void Dispose()
    {
        if(disposed)return;
        disposed=true;
        ImGui.GetIO().FontGlobalScale=global;
        pushed?.Dispose();
        if(pushed is null)RefreshMetrics();
        owner.FontWindowScale=window;
        ImGui.SetWindowFontScale(ImGuiP.GetCurrentWindow().FontWindowScale);
    }

    private static void RefreshMetrics(){ImGui.PushFont(ImGui.GetFont());ImGui.PopFont();}
}

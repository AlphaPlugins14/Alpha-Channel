using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed class MiniWindowStyle : IDisposable
{
    private readonly IDisposable styles;
    private readonly IDisposable colors;

    internal MiniWindowStyle(ThemeColors theme)
    {
        var border=theme.Accent with { W=.4f };
        styles=ImRaii.PushStyle(ImGuiStyleVar.FrameRounding,6f)
            .Push(ImGuiStyleVar.FrameBorderSize,1f).Push(ImGuiStyleVar.ChildRounding,8f);
        colors=ImRaii.PushColor(ImGuiCol.ChildBg,theme.CardBg).Push(ImGuiCol.FrameBg,theme.FrameBg)
            .Push(ImGuiCol.Button,theme.FrameBg).Push(ImGuiCol.ButtonHovered,theme.CardBgHover)
            .Push(ImGuiCol.ButtonActive,theme.Accent).Push(ImGuiCol.Border,border)
            .Push(ImGuiCol.SliderGrab,theme.Accent).Push(ImGuiCol.SliderGrabActive,theme.AccentHover);
        var draw=ImGui.GetWindowDrawList();var min=ImGui.GetWindowPos();var max=min+ImGui.GetWindowSize();
        draw.PushClipRect(min,max,false);
        draw.AddRectFilled(min,max,ImGui.GetColorU32(theme.CardBg),10f);
        draw.AddRect(min,max,ImGui.GetColorU32(border),10f);
        draw.PopClipRect();
    }

    public void Dispose(){colors.Dispose();styles.Dispose();}
}

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private PluginFilePicker[]? pluginFilePickers;

    private void DrawPluginFilePickers()
    {
        var pickers = pluginFilePickers ??= [groupImageFileDialog, groupImageSaveDialog, localVideoFileDialog, profileAvatarFileDialog, studioFileDialog,
            snesFileDialog, gameBoyFileDialog, nesFileDialog, gameBoyAdvanceFileDialog, masterSystemFileDialog, gameGearFileDialog];
        if (pickers.FirstOrDefault(picker => picker.IsOpen) is { } picker) DrawPluginFilePicker(picker);
    }

    private void DrawPluginFilePicker(PluginFilePicker picker)
    {
        var parent = ImGuiP.GetCurrentWindow().RootWindow;
        var position = parent.Pos;
        var size = parent.Size;
        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        if (!BeginWindowOverlay("##filePickerBackdrop", ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
                ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground))
        {
            ImGui.End();
            return;
        }
        try
        {
            ImGui.GetWindowDrawList().AddRectFilled(position, position + size,
                ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.54f)));
            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                picker.Reset();
                return;
            }

            var pickerSize = new Vector2(MathF.Min(Ui(900f), size.X - Ui(40f)),
                MathF.Min(Ui(600f), size.Y - Ui(40f)));
            pickerSize = Vector2.Max(Vector2.One, pickerSize);
            ImGui.SetNextWindowPos(position + (size - pickerSize) * 0.5f, ImGuiCond.Always);
            // The built-in picker supplies its own appearing size. Constraints
            // keep that size, saved sizes and later window resizing in bounds.
            ImGui.SetNextWindowSizeConstraints(pickerSize, pickerSize);
            ImGui.SetNextWindowViewport(parent.ViewportId);
            using var rounding = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, Ui(14f))
                .Push(ImGuiStyleVar.WindowBorderSize, Ui(1f))
                .Push(ImGuiStyleVar.WindowPadding, UiVec(16f, 14f));
            using var colours = ImRaii.PushColor(ImGuiCol.WindowBg, CardBg)
                .Push(ImGuiCol.TitleBg, CardBg).Push(ImGuiCol.TitleBgActive, CardBg)
                .Push(ImGuiCol.Border, Accent).Push(ImGuiCol.Text, Hex(0xE2E7EF));

            // Dalamud uses hard-coded white for some file types. Tint only
            // white vertices appended by this picker, leaving its coloured
            // folders and all previously drawn plugin/other-plugin UI intact.
            var previousVertices = ImGui.GetCurrentContext().Windows.AsSpan().ToArray()
                .Where(window => window.Active)
                .ToDictionary(window => window.ID, window => window.DrawList.VtxBuffer.Size);
            DrawFileBrowserAboveOverlay(() => DrawFilePickerWithFooterSpace(picker));
            var tint = ImGui.GetColorU32(Hex(0xE2E7EF)) & 0x00ffffffu;
            foreach (var window in ImGui.GetCurrentContext().Windows.AsSpan().ToArray())
            {
                if (!window.Active) continue;
                // The veil is raised every frame; keep the picker and its
                // own popups above it without raising unrelated windows.
                if (!previousVertices.ContainsKey(window.ID))
                    ImGuiP.BringWindowToDisplayFront(window.RootWindow);
                var vertices = window.DrawList.VtxBuffer;
                var first = previousVertices.GetValueOrDefault(window.ID);
                for (var index = first; index < vertices.Size; index++)
                {
                    var vertex = vertices[index];
                    if ((vertex.Col & 0x00ffffffu) == 0x00ffffffu)
                        vertex.Col = (vertex.Col & 0xff000000u) | tint;
                }
            }
        }
        finally { ImGui.End(); }
    }

    private void DrawFilePickerWithFooterSpace(PluginFilePicker picker)
    {
        // Dalamud reserves 100 * FontGlobalScale for Ok, Cancel and their
        // gaps. Our baked fonts use global scale 1, so that reservation can
        // be too small. Supply the measured reservation while compensating
        // the fonts, preserving their displayed size and restoring all state.
        var padding = Ui(6f);
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing,
            new Vector2(padding, ImGui.GetStyle().ItemSpacing.Y))
            .Push(ImGuiStyleVar.FramePadding, new Vector2(padding, ImGui.GetStyle().FramePadding.Y));
        var reserve = ImGui.CalcTextSize("Ok").X + ImGui.CalcTextSize("Cancel").X + padding * 7f + Ui(4f);
        using var footerScale = new FilePickerFontScaleScope(reserve, UiBuilder.IconFont);
        picker.Draw();
    }
}

using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawQueueDragPreview(string title)
    {
        var viewport = ImGui.GetWindowViewport();
        var size = UiVec(320, 64);
        var position = Vector2.Clamp(ImGui.GetMousePos() + UiVec(18, 16), viewport.Pos,
            Vector2.Max(viewport.Pos, viewport.Pos + viewport.Size - size));
        var draw = ImGui.GetForegroundDrawList(viewport);
        draw.AddRectFilled(position + UiVec(3, 4), position + size + UiVec(3, 4), ImGui.GetColorU32(new Vector4(0, 0, 0, .35f)), Ui(9));
        draw.AddRectFilled(position, position + size, ImGui.GetColorU32(CardBg with { W = .97f }), Ui(9));
        draw.AddRect(position, position + size, ImGui.GetColorU32(Accent), Ui(9), ImDrawFlags.None, Ui(1.5f));
        draw.AddText(position + UiVec(14, 11), ImGui.GetColorU32(Vector4.One), FitPartyTextToWidth(title, size.X - Ui(28)));
        draw.AddText(position + UiVec(14, 36), ImGui.GetColorU32(AccentHover), "Release on a row to reorder");
        ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
    }

    private void DrawQueueDropMarker(Vector2 origin, float width, float rowHeight, bool below)
    {
        var pos = origin + new Vector2(Ui(24), below ? rowHeight : 0);
        var draw = ImGui.GetWindowDrawList();
        draw.AddLine(pos, new(origin.X + width, pos.Y), ImGui.GetColorU32(Accent), Ui(3));
        draw.AddCircleFilled(pos, Ui(4), ImGui.GetColorU32(Accent));
    }
}

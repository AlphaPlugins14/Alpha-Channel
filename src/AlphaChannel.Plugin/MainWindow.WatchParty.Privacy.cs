using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawWatchPartyPrivacyOptions(float width)
    {
        var row = ImGui.GetCursorScreenPos();
        DrawWatchPartyAdultToggle();
        var adultRight = ImGui.GetItemRectMax().X;
        var privateWidth = Ui(47f) + ImGui.CalcTextSize("Private party").X;
        var x = row.X + width - privateWidth;
        var y = x >= adultRight + Ui(16f) ? row.Y : row.Y + Ui(28f);
        ImGui.SetCursorScreenPos(new Vector2(MathF.Max(row.X, x), y));
        ImGui.BeginGroup();
        DrawSettingsToggle("##createRoomPrivate", "Private party", ref createRoomPrivate);
        ImGui.EndGroup();
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Party is hidden from party directory listings");
    }
}

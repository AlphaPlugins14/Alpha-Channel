using AlphaChannel.Contracts;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly Vector4 HalloweenCategoryColor = new(1f, 0.52f, 0.12f, 1f);

    private static Vector4 WatchPartyCategoryColor(RoomDirectoryDto room) =>
        GetWatchPartyCategory(room) == WatchPartyCategory.Halloween ? HalloweenCategoryColor : AccentHover;

    private static Vector4 PartyRoomCategoryColor(int categoryIndex) =>
        PartyRoomCategoryName(categoryIndex) == "Halloween" ? HalloweenCategoryColor : AccentHover;

    private void DrawWatchPartyCategoryCombo(string id, ref int selectedIndex)
    {
        selectedIndex = Math.Clamp(selectedIndex, 0, WatchPartyCategoryOptions.Length - 1);
        if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo(id, WatchPartyCategoryOptions[selectedIndex]))) return;
        var halloweenIndex = Array.IndexOf(WatchPartyCategoryOptions, "Halloween");
        for (var row = 0; row < WatchPartyCategoryOptions.Length; row++)
        {
            var index = halloweenIndex < 0 ? row : row == 0 ? halloweenIndex :
                row <= halloweenIndex ? row - 1 : row;
            var label = WatchPartyCategoryOptions[index];
            var selected = index == selectedIndex;
            if (label == "Halloween")
            {
                var position = ImGui.GetCursorScreenPos();
                if (ImGui.Selectable("##halloweenCategory", selected)) selectedIndex = index;
                var draw = ImGui.GetWindowDrawList();
                using (UiFonts.PushIcon())
                    draw.AddText(position, ImGui.GetColorU32(HalloweenCategoryColor), FontAwesomeIcon.Ghost.ToIconString());
                draw.AddText(position + UiVec(24f, 0f), ImGui.GetColorU32(HalloweenCategoryColor), label);
            }
            else if (ImGui.Selectable(label, selected)) selectedIndex = index;
            if (selected) ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }
}

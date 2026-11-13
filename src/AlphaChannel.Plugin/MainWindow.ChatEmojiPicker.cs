using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static void PositionPartyChatPopup(Vector2 size)
    {
        var root = ImGuiP.GetCurrentWindow().RootWindow;
        var minimum = root.Pos + UiVec(8f, 8f);
        var maximum = Vector2.Max(minimum, root.Pos + root.Size - size - UiVec(8f, 8f));
        var anchor = ImGui.GetItemRectMin();
        var y = ImGui.GetItemRectMax().Y + Ui(6f);
        if (y + size.Y > root.Pos.Y + root.Size.Y - Ui(8f)) y = anchor.Y - size.Y - Ui(6f);
        ImGui.SetNextWindowPos(Vector2.Clamp(new Vector2(anchor.X, y), minimum, maximum), ImGuiCond.Always);
    }
    // Shared picker so Watch Party and Alpha Chat keep the same options and layout.
    private void DrawChatComposerEmojiPopup(string popupId, Action<int> onPick, string heading = "Add an emoji")
    {
        if ((currentPage is HomePage.WatchAlong or HomePage.Messages or HomePage.AlphaChat) && ImGui.IsPopupOpen(popupId)) PositionPartyChatPopup(UiVec(258f, 254f));
        ImGui.SetNextWindowSize(UiVec(258f, 0f), ImGuiCond.Appearing);
        using (ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, Ui(10f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(12f, 12f))
            .Push(ImGuiStyleVar.ItemSpacing, UiVec(6f, 6f))
            .Push(ImGuiStyleVar.PopupBorderSize, Ui(1f)))
        using (ImRaii.PushColor(ImGuiCol.PopupBg, CardBg).Push(ImGuiCol.Border, Accent))
        {
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup(popupId)))
            {
                using (UiFonts.PushRelative(1f, emphasis: true)) ImGui.TextColored(Hex(0xE2E7EF), heading);
                ImGui.Dummy(UiVec(0f, 4f));
                for (var emoji = 0; emoji < ChatReactionGlyphs.Length; emoji++)
                {
                    if (emoji % 5 != 0) ImGui.SameLine();
                    var clicked = ImGui.InvisibleButton($"##composerEmoji{emoji}", UiVec(40f, 40f));
                    var min = ImGui.GetItemRectMin();
                    var max = ImGui.GetItemRectMax();
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.GetWindowDrawList().AddRectFilled(min, max,
                            ImGui.GetColorU32(FrameBgHover), Ui(8f));
                        UiFonts.SetTooltip(ChatReactionLabels[emoji]);
                    }
                    var icon = ChatReactionIcons[emoji].ToIconString();
                    Vector2 iconSize;
                    using (UiFonts.PushIcon())
                        iconSize = ImGui.CalcTextSize(icon);
                    UiFonts.DrawIcon(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), min + (max - min - iconSize) * 0.5f, ImGui.GetColorU32(ChatEmojiColor(emoji)), icon);
                    if (clicked)
                    {
                        onPick(emoji);
                        ImGui.CloseCurrentPopup();
                    }
                }
                ImGui.EndPopup();
            }
        }
    }
}

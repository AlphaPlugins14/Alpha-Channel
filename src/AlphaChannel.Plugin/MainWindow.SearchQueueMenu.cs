using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string? searchQueueUrl;
    private Vector2 searchQueueAnchor;
    private bool openSearchQueueMenu;

    private void DrawSearchQueueButton(string id, string query)
    {
        ImGui.SameLine(0, Ui(6));
        var valid = Uri.TryCreate(query.Trim(), UriKind.Absolute, out var uri) &&
            uri.Scheme is "https" or "http";
        using (UiDisabled.Scope(!valid))
        using (ImRaii.PushId("searchQueue" + id))
        {
            var anchor = ImGui.GetCursorScreenPos();
            if (GameLayoutButton(string.Empty, FontAwesomeIcon.Plus, Ui(34), height: 40))
            {
                searchQueueUrl = query.Trim();
                searchQueueAnchor = anchor + UiVec(0, 44);
                openSearchQueueMenu = true;
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiFonts.SetTooltip(valid ? "Add video to queue" : "Paste a video link to add it to a queue");
    }

    private void DrawSearchQueueMenu()
    {
        if (openSearchQueueMenu)
        {
            ImGui.OpenPopup("##searchLinkQueueMenu");
            openSearchQueueMenu = false;
        }
        if (!ImGui.IsPopupOpen("##searchLinkQueueMenu") || searchQueueUrl is not { } url) return;
        var parent = ImGui.GetWindowPos();
        var size = new Vector2(Ui(230), ImGui.GetTextLineHeight() +
            MathF.Max(Ui(24), ImGui.GetTextLineHeight() + Ui(8)) *
            (Video.QueueManager.StandardSlotCount + (HomeSearchHasParty ? 1 : 0)) + Ui(32));
        ImGui.SetNextWindowPos(Vector2.Clamp(searchQueueAnchor, parent + UiVec(8, 8),
            Vector2.Max(parent + UiVec(8, 8), parent + ImGui.GetWindowSize() - size - UiVec(8, 8))));
        ImGui.SetNextWindowSize(size);
        using var styles = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(10, 10))
            .Push(ImGuiStyleVar.ItemSpacing, UiVec(4, 3)).Push(ImGuiStyleVar.PopupBorderSize, Ui(1))
            .Push(ImGuiStyleVar.PopupRounding, Ui(10));
        using var colors = ImRaii.PushColor(ImGuiCol.PopupBg, CardBg)
            .Push(ImGuiCol.Border, AccentHover);
        using var popup = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Popup("##searchLinkQueueMenu"));
        if (!popup) return;
        ImGui.TextColored(MutedText, "ADD TO QUEUE");
        if (HomeSearchHasParty && DrawYouTubeMenuRow("Watch Party", FontAwesomeIcon.Users))
        {
            BeginHomeSearchQueueAddition(url, true);
            ImGui.CloseCurrentPopup();
        }
        for (var slot = 0; slot < Video.QueueManager.StandardSlotCount; slot++)
        {
            var name = queueManager.Slots[slot]?.Name ?? $"Queue {slot + 1}";
            if (DrawYouTubeMenuRow($"{name}##searchSlot{slot}", FontAwesomeIcon.ListUl))
            {
                BeginHomeSearchQueueAddition(url, false, destinationSlot: slot);
                ImGui.CloseCurrentPopup();
            }
        }
    }
}

using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private enum PersonalQueueDialog { None, Import, Save, Clear }
    private PersonalQueueDialog personalQueueDialog;
    private SavedQueueProfile? personalQueueDialogProfile;

    private void OpenPersonalQueueDialog(PersonalQueueDialog dialog, SavedQueueProfile profile)
    {
        personalQueueDialog = dialog;
        personalQueueDialogProfile = profile;
        var viewedSlot = Enumerable.Range(0, queueManager.Slots.Count).First(i => ReferenceEquals(queueManager.Slots[i], profile));
        personalQueueCopySlot = Enumerable.Range(0, queueManager.Slots.Count).FirstOrDefault(i => i != viewedSlot &&
            (dialog == PersonalQueueDialog.Save ? queueManager.Slots[i] is null : queueManager.Slots[i] is not null), viewedSlot);
        personalQueueCopyName = profile.Name + " copy";
    }

    // Draw after the page, alongside the existing queue editor/deletion overlays.
    private void DrawPersonalQueueDialog()
    {
        if (personalQueueDialog == PersonalQueueDialog.None) return;
        var slot = Enumerable.Range(0, queueManager.Slots.Count)
            .FirstOrDefault(i => ReferenceEquals(queueManager.Slots[i], personalQueueDialogProfile), -1);
        if (slot < 0)
        { personalQueueDialog = PersonalQueueDialog.None; personalQueueDialogProfile = null; return; }
        var clearing = personalQueueDialog == PersonalQueueDialog.Clear;
        var parent = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var size = Vector2.Min(UiVec(500, clearing ? 260 : 350), Vector2.Max(Vector2.One, parentSize - UiVec(32, 32)));
        var origin = parent + (parentSize - size) * .5f;
        var colour = clearing ? Danger : Accent;
        ImGui.SetNextWindowPos(parent, ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##personalQueueDialogOverlay", flags)) { ImGui.End(); return; }
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(parent, parent + parentSize, ImGui.GetColorU32(new Vector4(0, 0, 0, .58f)));
        draw.AddRect(origin - UiVec(3, 3), origin + size + UiVec(3, 3), ImGui.GetColorU32(colour with { W = .22f }), Ui(16), ImDrawFlags.None, Ui(2));
        ImGui.SetCursorScreenPos(origin);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14)).Push(ImGuiStyleVar.ChildBorderSize, Ui(1))
                   .Push(ImGuiStyleVar.WindowPadding, UiVec(24, 20)).Push(ImGuiStyleVar.ItemSpacing, UiVec(8, 12)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, colour with { W = .8f }))
        using (var card = ImRaii.Child("##personalQueueDialogCard", size, true))
        {
            if (card)
            {
                using (UiFonts.PushRelative(1.2f, emphasis: true))
                    ImGui.TextColored(clearing ? Danger : AccentHover,
                        clearing ? "Clear queue?" : personalQueueDialog == PersonalQueueDialog.Import ? "Import videos" : "Save queue copy");
                ImGui.TextColored(MutedText, FitPartyTextToWidth(personalQueueDialogProfile!.Name, ImGui.GetContentRegionAvail().X));
                ImGui.Separator();
                if (clearing)
                {
                    ImGui.TextWrapped("Remove all upcoming videos from this queue? The current video keeps playing.");
                    ImGui.Dummy(UiVec(0, 8));
                    if (ActivePartyButton("Clear queue", UiVec(140, 34), danger: true))
                    {
                        if (slot == queueManager.ActiveSlotIndex) personalQueue.ReplaceUpcomingEntries(Array.Empty<VideoQueueRecord>());
                        else
                        {
                            var profile = personalQueueDialogProfile;
                            profile.Entries.RemoveAll(record => !personalQueue.IsCurrentSavedRecord(profile, record));
                            Plugin.Cfg.Save();
                        }
                        personalQueueDialog = PersonalQueueDialog.None;
                    }
                    ImGui.SameLine();
                    if (ActivePartyButton("Cancel", UiVec(100, 34))) personalQueueDialog = PersonalQueueDialog.None;
                }
                else DrawPersonalQueueCopyContents(slot, personalQueueDialog == PersonalQueueDialog.Import);
            }
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) personalQueueDialog = PersonalQueueDialog.None;
        ImGui.End();
    }
}

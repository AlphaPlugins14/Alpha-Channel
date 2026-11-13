using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private enum WheelDialog { None, Appearance, Save, Load, Rename, Delete, Apply, Icon }
    private string? luckyWheelIconOption;
    private WheelDialog luckyWheelDialogRequest;
    private WheelDialog luckyWheelDialog;
    private string luckyWheelSaveName = "";
    private int luckyWheelDialogSelection = -1;
    private void RequestWheelSave()
    {
        luckyWheelSaveName = luckyWheelDraft.Title;
        luckyWheelDialogRequest = WheelDialog.Save;
    }
    private void RequestSavedWheelAction(WheelDialog action, int index)
    {
        var saved = Plugin.Cfg.SavedLuckyWheels ??= [];
        if (index < 0 || index >= saved.Count) return;
        luckyWheelDialogSelection = index;
        if (action == WheelDialog.Rename) luckyWheelSaveName = saved[index].Title;
        if (action == WheelDialog.Load && !LuckyWheelDraftDirty) LoadLuckyWheelSaved(index);
        else luckyWheelDialogRequest = action;
    }
    private void LoadLuckyWheelSaved(int index)
    {
        var saved = Plugin.Cfg.SavedLuckyWheels ??= [];
        var loaded = index >= 0 && index < saved.Count ? PrepareSavedLuckyWheel(saved[index]) : null;
        if (loaded is null)
        { luckyWheelEditorMessage = "This saved wheel is invalid. Save a new wheel instead."; return; }
        var shortened = saved[index].Segments.Length > LuckyWheelRules.MaxSegments;
        var shortenedLabels = saved[index].Segments.Any(s => s.Text?.Length > LuckyWheelRules.MaxLabelLength);
        luckyWheelDraft = loaded;
        luckyWheelDraftBaseline = JsonSerializer.Serialize(luckyWheelDraft);
        luckyWheelEditorTab = WheelEditorTab.Design;
        luckyWheelEditorMessage = LuckyWheelActive ? "Wheel loaded. Apply the design to update your active wheel." : "Wheel loaded and ready to edit or start.";
        if (shortened) luckyWheelEditorMessage = "Loaded the first 30 options. The original saved wheel is unchanged until you save over it.";
        if (shortenedLabels) luckyWheelEditorMessage = "Loaded with option labels shortened to 20 characters. The original saved wheel is unchanged until you save over it.";
        luckyWheelColours.Reset(luckyWheelDraft);
        ResetLuckyWheelPreview();
    }
    private static LuckyWheelDesign? PrepareSavedLuckyWheel(LuckyWheelDesign wheel)
    {
        if (wheel?.Segments is null) return null;
        var copy = wheel.Copy();
        copy.Segments = copy.Segments.Take(LuckyWheelRules.MaxSegments)
            .Select(s => s?.Text is { Length: > LuckyWheelRules.MaxLabelLength }
                ? s with { Text = s.Text[..LuckyWheelRules.MaxLabelLength] } : s!).ToArray();
        return LuckyWheelRules.ValidDesign(copy) ? copy : null;
    }
    private void DrawLuckyWheelSavedPanel()
    {
        var width = ImGui.GetContentRegionAvail().X;
        var saved = Plugin.Cfg.SavedLuckyWheels ??= [];
        // A short library stays short; larger libraries scroll inside a clear panel.
        var listHeight = Ui(Math.Clamp(saved.Count, 1, 4) * 118);
        GameLayoutPanel("##wheelSavedPanel", width, listHeight + Ui(140), () =>
        {
            GameLayoutHeading("Your saved wheels", FontAwesomeIcon.Save);
            using (UiFonts.PushRelative(.90f)) ImGui.TextColored(MutedText, "Load a design to edit or use again.");
            using (UiDisabled.Scope(!LuckyWheelRules.ValidDesign(luckyWheelDraft)))
                if (GameLayoutButton("Save current wheel", FontAwesomeIcon.Save, Ui(200), height: 32)) RequestWheelSave();
            using (ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero))
            using (var list = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##savedWheelRows", new Vector2(-1, listHeight), false)))
            {
                if (!list) return;
                if (saved.Count == 0)
                {
                    ImGui.Dummy(UiVec(0, 16));
                    ImGui.TextUnformatted("No saved wheels yet");
                    ImGui.TextWrapped("Design a wheel, then choose Save wheel to keep it for another time.");
                }
                for (var i = 0; i < saved.Count; i++)
                {
                    using var id = ImRaii.PushId(i);
                    var index = i;
                    var wheel = saved[i];
                    GameLayoutPanel("##savedWheel", ImGui.GetContentRegionAvail().X, Ui(104), () =>
                    {
                        var actionWidth = Ui(160);
                        if (ImGui.BeginTable("##savedWheelColumns", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoPadOuterX))
                        {
                            ImGui.TableSetupColumn("Design", ImGuiTableColumnFlags.WidthStretch);
                            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, actionWidth);
                            ImGui.TableNextColumn();
                            ImGui.TextColored(Hex(0xE2E7EF), string.IsNullOrWhiteSpace(wheel.Title) ? "Untitled wheel" : wheel.Title);
                            using (UiFonts.PushRelative(.90f)) ImGui.TextColored(MutedText, $"{wheel.Segments?.Length ?? 0} options");
                            DrawWheelSavedSwatches(wheel);
                            ImGui.TableNextColumn();
                            using (UiDisabled.Scope(PrepareSavedLuckyWheel(wheel) is null))
                                if (GameLayoutButton("Load", FontAwesomeIcon.FolderOpen, Ui(112), true, height: 32)) RequestSavedWheelAction(WheelDialog.Load, index);
                            ImGui.SameLine(0, Ui(8));
                            if (GameLayoutButton("", FontAwesomeIcon.EllipsisH, Ui(32), height: 32)) ImGui.OpenPopup("##savedWheelActions");
                            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup("##savedWheelActions")))
                            {
                                if (ImGui.MenuItem("Rename")) RequestSavedWheelAction(WheelDialog.Rename, index);
                                if (ImGui.MenuItem("Delete")) RequestSavedWheelAction(WheelDialog.Delete, index);
                                ImGui.EndPopup();
                            }
                            ImGui.EndTable();
                        }
                    }, 12);
                    SetGameLayoutRowGap(Ui(14));
                }
            }
        });
        SetGameLayoutRowGap(Ui(14));
        if (GameLayoutButton("Back to design", FontAwesomeIcon.ArrowLeft, Ui(170))) luckyWheelEditorTab = WheelEditorTab.Design;
    }
    private static void DrawWheelSavedSwatches(LuckyWheelDesign wheel)
    {
        var colours = (wheel.Segments ?? []).Where(s => s is not null).Take(12).Select(s => s.Colour).ToArray();
        for (var j = 0; j < colours.Length; j++)
        {
            if (j > 0 && ImGui.GetContentRegionAvail().X >= Ui(20)) ImGui.SameLine(0, Ui(4));
            using var id = ImRaii.PushId(j);
            ImGui.ColorButton("##savedColour", new Vector4(WheelRgb(colours[j]), 1), ImGuiColorEditFlags.NoTooltip, UiVec(16, 16));
            DrawWheelSwatchBorder(ImGui.GetItemRectMin(), ImGui.GetItemRectSize());
        }
    }
    private void DrawLuckyWheelEditorDialogs()
    {
        const string overlayId = "Lucky Wheel editor";
        if (luckyWheelDialogRequest != WheelDialog.None)
        {
            luckyWheelDialog = luckyWheelDialogRequest;
            luckyWheelDialogRequest = WheelDialog.None;
            activeGameDialog = overlayId;
        }
        if (luckyWheelDialog == WheelDialog.None) return;
        var title = luckyWheelDialog switch
        {
            WheelDialog.Icon => "Choose option icon",
            WheelDialog.Appearance => "Customise appearance",
            WheelDialog.Save => "Save wheel",
            WheelDialog.Load => "Load saved wheel?",
            WheelDialog.Rename => "Rename saved wheel",
            WheelDialog.Delete => "Delete saved wheel?",
            _ => "Apply changes & reset results?",
        };
        var icon = luckyWheelDialog is WheelDialog.Appearance or WheelDialog.Icon ? FontAwesomeIcon.Palette : FontAwesomeIcon.Save;
        var height = luckyWheelDialog == WheelDialog.Icon ? 330f : luckyWheelDialog == WheelDialog.Appearance ? 370f : luckyWheelDialog is WheelDialog.Save or WheelDialog.Rename ? 300f : 250f;
        if (!BeginGameDialog(overlayId, title, icon, 560f, height)) return;
        var afterHeading = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(ImGui.GetWindowPos() + new Vector2(ImGui.GetWindowSize().X - Ui(44), Ui(16)));
        if (WheelSecondaryButton("X##closeWheelDialog", UiVec(28, 28))) CloseLuckyWheelDialog();
        ImGui.SetCursorScreenPos(afterHeading);
        if (luckyWheelDialog == WheelDialog.None) { EndGameDialog(); return; }
        var saved = Plugin.Cfg.SavedLuckyWheels ??= [];
        var selected = luckyWheelDialogSelection >= 0 && luckyWheelDialogSelection < saved.Count ? saved[luckyWheelDialogSelection] : null;
        switch (luckyWheelDialog)
        {
            case WheelDialog.Icon:
                DrawLuckyWheelOptionIconPicker();
                break;
            case WheelDialog.Appearance:
                ImGui.TextWrapped("These colours control the shared TV screen.");
                var background = luckyWheelDraft.Background; if (WheelColourPicker("Screen background", ref background)) luckyWheelDraft.Background = background;
                var text = luckyWheelDraft.TextColour; if (WheelColourPicker("Screen text", ref text)) luckyWheelDraft.TextColour = text;
                var border = luckyWheelDraft.BorderColour; if (WheelColourPicker("Wheel border & pointer", ref border)) luckyWheelDraft.BorderColour = border;
                var automatic = luckyWheelDraft.AutomaticLabelColour;
                if (DrawSettingsToggle("##wheelContrast", "Automatic contrasting option text", ref automatic)) luckyWheelDraft.AutomaticLabelColour = automatic;
                ImGui.TextWrapped("Automatic contrast chooses light or dark option text for each segment colour.");
                if (ImGui.Button("Done", UiVec(120, 36))) CloseLuckyWheelDialog();
                break;
            case WheelDialog.Save:
                using (UiFonts.PushRelative(.90f)) ImGui.TextColored(MutedText, "This name is also the title shown on the wheel screen.");
                ImGui.SetNextItemWidth(-1); ImGui.InputText("##savedWheelName", ref luckyWheelSaveName, 60);
                var name = luckyWheelSaveName.Trim();
                var existing = saved.FindIndex(d => string.Equals(d.Title, name, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0) ImGui.TextWrapped("A wheel with this name already exists. Replacing it overwrites that saved design.");
                var copy = luckyWheelDraft.Copy(); copy.Title = name;
                using (UiDisabled.Scope(!LuckyWheelRules.ValidDesign(copy) || existing < 0 && saved.Count >= 30))
                    if (ImGui.Button(existing >= 0 ? "Replace saved wheel" : "Save wheel", UiVec(190, 36)))
                    {
                        if (existing >= 0) saved[existing] = copy; else { saved.Add(copy); existing = saved.Count - 1; }
                        luckyWheelDraft.Title = name;
                        luckyWheelDraftBaseline = JsonSerializer.Serialize(luckyWheelDraft);
                        Plugin.Cfg.Save(); luckyWheelEditorMessage = "Wheel saved."; CloseLuckyWheelDialog();
                    }
                if (existing < 0 && saved.Count >= 30) ImGui.TextWrapped("You have 30 saved wheels. Delete one or replace an existing wheel.");
                ImGui.SameLine(); if (WheelSecondaryButton("Cancel", UiVec(100, 36))) CloseLuckyWheelDialog();
                break;
            case WheelDialog.Load:
                ImGui.TextWrapped($"Loading '{selected?.Title}' replaces your unsaved design edits. The active wheel will not change until you apply the design.");
                using (UiDisabled.Scope(selected is null))
                    if (ImGui.Button("Discard edits & load", UiVec(190, 36))) { LoadLuckyWheelSaved(luckyWheelDialogSelection); CloseLuckyWheelDialog(); }
                ImGui.SameLine(); if (WheelSecondaryButton("Cancel", UiVec(100, 36))) CloseLuckyWheelDialog();
                break;
            case WheelDialog.Rename:
                ImGui.TextWrapped("This changes the title stored in the saved design. Your current design is unchanged.");
                ImGui.SetNextItemWidth(-1); ImGui.InputText("##renameWheel", ref luckyWheelSaveName, 60);
                var renamed = luckyWheelSaveName.Trim();
                var duplicate = saved.Where((_, index) => index != luckyWheelDialogSelection).Any(d => string.Equals(d.Title, renamed, StringComparison.OrdinalIgnoreCase));
                if (duplicate) ImGui.TextColored(Danger, "Another saved wheel already has this name.");
                using (UiDisabled.Scope(selected is null || renamed.Length == 0 || duplicate))
                    if (ImGui.Button("Rename", UiVec(120, 36))) { selected!.Title = renamed; Plugin.Cfg.Save(); CloseLuckyWheelDialog(); }
                ImGui.SameLine(); if (WheelSecondaryButton("Cancel", UiVec(100, 36))) CloseLuckyWheelDialog();
                break;
            case WheelDialog.Delete:
                ImGui.TextWrapped($"'{selected?.Title}' will be removed from your saved wheels. Your current design and active wheel are unchanged.");
                using (UiDisabled.Scope(selected is null))
                    if (ImGui.Button("Delete wheel", UiVec(140, 36)))
                    { saved.RemoveAt(luckyWheelDialogSelection); Plugin.Cfg.Save(); CloseLuckyWheelDialog(); }
                ImGui.SameLine(); if (WheelSecondaryButton("Cancel", UiVec(100, 36))) CloseLuckyWheelDialog();
                break;
            case WheelDialog.Apply:
                ImGui.TextWrapped("Everyone will see the updated design. Recent results will be cleared and all options will be restored.");
                using (UiDisabled.Scope(luckyWheelState is not { Spinning: false } || !LuckyWheelRules.ValidDesign(luckyWheelDraft)))
                    if (ImGui.Button("Apply & reset", UiVec(150, 36)))
                    { ApplyLuckyWheelDesign(); luckyWheelDraftBaseline = JsonSerializer.Serialize(luckyWheelDraft); ResetLuckyWheelPreview(); CloseLuckyWheelDialog(); }
                ImGui.SameLine(); if (WheelSecondaryButton("Cancel", UiVec(100, 36))) CloseLuckyWheelDialog();
                break;
        }
        EndGameDialog();
    }
    private void DrawLuckyWheelOptionIconPicker()
    {
        var index = Array.FindIndex(luckyWheelDraft.Segments, segment => segment.Id == luckyWheelIconOption);
        if (index < 0) { CloseLuckyWheelDialog(); return; }
        ImGui.TextWrapped($"Choose an icon for '{luckyWheelDraft.Segments[index].Text}'.");
        if (GameLayoutButton("No icon", FontAwesomeIcon.Times, Ui(135), luckyWheelDraft.Segments[index].Icon is null, height: 32))
        { luckyWheelDraft.Segments[index] = luckyWheelDraft.Segments[index] with { Icon = null }; CloseLuckyWheelDialog(); }
        ImGui.Dummy(UiVec(0, 6));
        var size = Ui(44); var gap = Ui(8);
        var columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + gap) / (size + gap)));
        for (var i = 0; i < LuckyWheelIcons.All.Length; i++)
        {
            var choice = LuckyWheelIcons.All[i];
            if (i % columns != 0) ImGui.SameLine(0, gap);
            using var id = ImRaii.PushId(choice.Name);
            if (GameLayoutButton("", Enum.Parse<FontAwesomeIcon>(choice.Name), size, luckyWheelDraft.Segments[index].Icon == choice.Name, height: 44))
            {
                luckyWheelDraft.Segments[index] = luckyWheelDraft.Segments[index] with { Icon = choice.Name };
                CloseLuckyWheelDialog();
            }
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip(choice.Name);
        }
        ImGui.Dummy(UiVec(0, 6));
        if (WheelSecondaryButton("Cancel", UiVec(100, 36))) CloseLuckyWheelDialog();
    }
    private void CloseLuckyWheelDialog()
    {
        activeGameDialog = null;
        luckyWheelDialog = WheelDialog.None;
    }
}

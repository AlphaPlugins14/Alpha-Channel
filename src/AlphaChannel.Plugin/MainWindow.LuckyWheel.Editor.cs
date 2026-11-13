using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private enum WheelEditorTab { Design, Preview, Saved }
    private WheelEditorTab luckyWheelEditorTab;
    private string? luckyWheelEditorMessage;
    private string? luckyWheelDraftBaseline;
    private readonly LuckyWheelColours luckyWheelColours = new();
    private string? luckyWheelPreviewKey;
    private static Vector3 WheelRgb(uint rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
    private static uint WheelHex(Vector3 colour) => (uint)((int)Math.Round(Math.Clamp(colour.X, 0, 1) * 255) << 16 |
        (int)Math.Round(Math.Clamp(colour.Y, 0, 1) * 255) << 8 | (int)Math.Round(Math.Clamp(colour.Z, 0, 1) * 255));
    private static bool WheelColourPicker(string label, ref uint colour)
    {
        var rgb = WheelRgb(colour);
        var changed = ImGui.ColorEdit3(label, ref rgb, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel);
        DrawWheelSwatchBorder(ImGui.GetItemRectMin(), new Vector2(ImGui.GetFrameHeight()));
        if (!label.StartsWith("##", StringComparison.Ordinal)) { ImGui.SameLine(); ImGui.TextUnformatted(label); }
        if (!changed) return false;
        colour = WheelHex(rgb); return true;
    }
    private static void DrawWheelSwatchBorder(Vector2 origin, Vector2 size)
    {
        ImGui.GetWindowDrawList().AddRect(origin, origin + size,
            ImGui.GetColorU32(new Vector4(1, 1, 1, .55f)), Ui(4), ImDrawFlags.None, Ui(1));
    }
    private uint NewWheelSegmentColour(int index) => luckyWheelColours.Selected switch
    {
        LuckyWheelColourPreset.AlternatingAccent => index % 2 == 0 ? WheelHex(new Vector3(Accent.X, Accent.Y, Accent.Z)) : WheelHex(new Vector3(AccentActive.X, AccentActive.Y, AccentActive.Z)),
        LuckyWheelColourPreset.PluginAccent => WheelHex(new Vector3(Accent.X, Accent.Y, Accent.Z)),
        _ => LuckyWheelRules.Rainbow[index % LuckyWheelRules.Rainbow.Length],
    };
    private bool LuckyWheelDraftDirty => luckyWheelDraftBaseline != JsonSerializer.Serialize(luckyWheelDraft);
    private void BeginLuckyWheelEditor()
    {
        luckyWheelEditorTab = WheelEditorTab.Design;
        luckyWheelDraftBaseline = JsonSerializer.Serialize(luckyWheelDraft);
        luckyWheelEditorMessage = null;
        luckyWheelColours.Reset(luckyWheelDraft);
        ResetLuckyWheelPreview();
    }
    private static bool WheelSecondaryButton(string label, Vector2 size = default)
    {
        using var colours = ImRaii.PushColor(ImGuiCol.Button, FrameBg)
            .Push(ImGuiCol.ButtonHovered, FrameBgHover).Push(ImGuiCol.ButtonActive, AccentFieldActive);
        return ImGui.Button(label, size);
    }
    private void DrawLuckyWheelEditor()
    {
        if (stream.Mode == StreamMode.Viewing)
        {
            using (UiFonts.PushRelative(.90f)) ImGui.TextColored(MutedText, "Waiting for a spin…");
            return;
        }
        if (luckyWheelDraftBaseline is null)
        {
            luckyWheelDraftBaseline = JsonSerializer.Serialize(luckyWheelDraft);
            luckyWheelColours.Reset(luckyWheelDraft);
        }
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8, 6));
        using var frames = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(10, 6))
            .Push(ImGuiStyleVar.FrameRounding, Ui(7)).Push(ImGuiStyleVar.FrameBorderSize, 0f);
        using var controls = ImRaii.PushColor(ImGuiCol.FrameBg, FrameBg)
            .Push(ImGuiCol.FrameBgHovered, FrameBgHover).Push(ImGuiCol.FrameBgActive, AccentFieldActive)
            .Push(ImGuiCol.CheckMark, Accent);
        var width = ImGui.GetContentRegionAvail().X;
        var gap = Ui(14);
        var tabWidth = (width - gap * 2) / 3;
        if (GameLayoutButton("Design", FontAwesomeIcon.Palette, tabWidth, luckyWheelEditorTab == WheelEditorTab.Design, height: 44)) luckyWheelEditorTab = WheelEditorTab.Design;
        ImGui.SameLine(0, gap);
        if (GameLayoutButton("Preview", FontAwesomeIcon.Eye, tabWidth, luckyWheelEditorTab == WheelEditorTab.Preview, height: 44)) luckyWheelEditorTab = WheelEditorTab.Preview;
        ImGui.SameLine(0, gap);
        if (GameLayoutButton("Saved wheels", FontAwesomeIcon.Save, tabWidth, luckyWheelEditorTab == WheelEditorTab.Saved, height: 44)) luckyWheelEditorTab = WheelEditorTab.Saved;
        SetGameLayoutRowGap(gap);
        switch (luckyWheelEditorTab)
        {
            case WheelEditorTab.Design: DrawLuckyWheelDesignPanels(); break;
            case WheelEditorTab.Preview: DrawLuckyWheelDesignPreview(); break;
            case WheelEditorTab.Saved: DrawLuckyWheelSavedPanel(); break;
        }
        if (luckyWheelEditorMessage is { } message) ImGui.TextWrapped(message);
        DrawLuckyWheelEditorDialogs();
    }
    private void DrawLuckyWheelDesignPanels()
    {
        var width = ImGui.GetContentRegionAvail().X;
        var gap = Ui(14);
        var wide = width >= Ui(780);
        var height = Ui(606);
        if (wide)
        {
            var leftWidth = (width - gap) * .62f;
            DrawLuckyWheelOptionsPanel(leftWidth, height);
            ImGui.SameLine(0, gap);
            // A separate child owns the entire right column. Its nested panels
            // advance within that child, never back to the page's left edge.
            using (ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero))
            using (var right = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##wheelSettingsColumn", new Vector2(width - leftWidth - gap, height), false)))
                if (right) DrawLuckyWheelSettingsPanels(ImGui.GetContentRegionAvail().X, height);
        }
        else
        {
            DrawLuckyWheelSettingsPanels(width, height);
            SetGameLayoutRowGap(gap);
            DrawLuckyWheelOptionsPanel(width, Ui(450));
        }
        SetGameLayoutRowGap(gap);
        GameLayoutPanel("##wheelDesignActions", width, Ui(92), DrawLuckyWheelDesignActions, 12);
    }
    private void DrawLuckyWheelDesignActions()
    {
        var valid = LuckyWheelRules.ValidDesign(luckyWheelDraft);
        var width = ImGui.GetContentRegionAvail().X;
        var gap = Ui(8);
        var saveWidth = Math.Min(Ui(150), width * .24f);
        using (UiDisabled.Scope(!valid))
            if (GameLayoutButton("Save wheel", FontAwesomeIcon.Save, saveWidth, height: 36)) RequestWheelSave();
        ImGui.SameLine(0, gap);
        var actionWidth = width - saveWidth - gap;
        if (LuckyWheelActive)
        {
            var cancelWidth = Math.Min(Ui(160), actionWidth * .35f);
            if (GameLayoutButton("Cancel editing", FontAwesomeIcon.ArrowLeft, cancelWidth, height: 36))
            {
                luckyWheelDraft = luckyWheelState!.Design.Copy();
                luckyWheelDraftBaseline = JsonSerializer.Serialize(luckyWheelDraft);
                luckyWheelEditorOpen = false; ResetLuckyWheelPreview(); InvalidateLuckyWheelFrame();
            }
            ImGui.SameLine(0, gap);
            using (UiDisabled.Scope(!valid || luckyWheelState!.Spinning))
                if (GameLayoutButton("Apply changes & reset results", FontAwesomeIcon.Check, actionWidth - cancelWidth - gap, true, height: 36)) luckyWheelDialogRequest = WheelDialog.Apply;
        }
        else
        {
            using var disabled = UiDisabled.Scope(!valid || CurrentSession is null || !CanLaunchActivity(ActiveActivityKind.LuckyWheel));
            if (stream.Mode == StreamMode.Hosting)
            {
                if (GameLayoutButton("Start in Watch Party", FontAwesomeIcon.Play, actionWidth, true, height: 36)) StartLuckyWheel();
            }
            else
            {
                var buttonWidth = (actionWidth - gap) / 2;
                if (GameLayoutButton("Start", FontAwesomeIcon.Play, buttonWidth, true, height: 36)) StartLuckyWheel();
                ImGui.SameLine(0, gap);
                if (GameLayoutButton("Start in party", FontAwesomeIcon.Users, buttonWidth, height: 36))
                { pendingWatchPartyMediaKind = PendingWatchPartyMediaKind.LuckyWheel; OpenPendingWatchPartyCreation(); }
            }
        }
        if (!valid) ImGui.TextColored(Danger, "Give the wheel a title and each option a label (up to 20 characters).");
        else if (CurrentSession is null) using (UiFonts.PushRelative(.90f)) ImGui.TextColored(MutedText, "Sign in to start Lucky Wheel.");
        else using (UiFonts.PushRelative(.90f)) ImGui.TextColored(MutedText, "Ready for a little luck? Your wheel is ready to play.");
    }
    private void DrawLuckyWheelOptionsPanel(float width, float height)
    {
        GameLayoutPanel("##wheelOptionsPanel", width, height, () =>
        {
            GameLayoutHeading("Wheel options", FontAwesomeIcon.List);
            using (UiFonts.PushRelative(.90f)) ImGui.TextColored(MutedText, $"{luckyWheelDraft.Segments.Length} / 30 options - Equal chance per option");
            using (UiDisabled.Scope(luckyWheelDraft.Segments.Length >= LuckyWheelRules.MaxSegments))
                if (GameLayoutButton("Add option", FontAwesomeIcon.Plus, Ui(145), height: 32))
                {
                    var items = luckyWheelDraft.Segments.ToList();
                    items.Add(new(Guid.NewGuid().ToString("N"), $"Option {items.Count + 1}", NewWheelSegmentColour(items.Count)));
                    luckyWheelDraft.Segments = items.ToArray();
                }
            using (ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero))
            using (var list = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##wheelOptionRows", new Vector2(-1, Math.Max(Ui(180), ImGui.GetContentRegionAvail().Y - Ui(82))), false)))
            {
                if (list)
                for (var i = 0; i < luckyWheelDraft.Segments.Length; i++)
                {
                    using var id = ImRaii.PushId(luckyWheelDraft.Segments[i].Id);
                    var segment = luckyWheelDraft.Segments[i];
                    var label = segment.Text;
                    ImGui.AlignTextToFramePadding(); using (UiFonts.PushRelative(.90f)) ImGui.TextColored(MutedText, $"{i + 1}");
                    ImGui.SameLine(Ui(30));
                    ImGui.SetNextItemWidth(Math.Max(Ui(80), ImGui.GetContentRegionAvail().X - Ui(122)));
                    if (ImGui.InputText("##option", ref label, LuckyWheelRules.MaxLabelLength))
                        luckyWheelDraft.Segments[i] = segment = segment with { Text = label };
                    ImGui.SameLine();
                    var colour = segment.Colour;
                    if (WheelColourPicker("##colour", ref colour))
                    {
                        luckyWheelDraft.Segments[i] = segment = segment with { Colour = colour };
                        luckyWheelColours.RememberCustom(luckyWheelDraft);
                    }
                    ImGui.SameLine();
                    var optionIcon = segment.Icon is null ? FontAwesomeIcon.Plus : Enum.Parse<FontAwesomeIcon>(segment.Icon);
                    if (WheelOptionIconButton(optionIcon))
                    {
                        luckyWheelIconOption = segment.Id;
                        luckyWheelDialogRequest = WheelDialog.Icon;
                    }
                    if (ImGui.IsItemHovered()) UiFonts.SetTooltip(segment.Icon is null ? "Assign option icon" : $"Change icon: {segment.Icon}");
                    ImGui.SameLine();
                    if (GameLayoutButton("", FontAwesomeIcon.EllipsisH, Ui(30), height: 30)) ImGui.OpenPopup("##optionActions");
                    if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Move, duplicate or remove option");
                    var changed = false;
                    if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup("##optionActions")))
                    {
                        using (UiDisabled.Scope(i == 0))
                            if (ImGui.MenuItem("Move up")) { (luckyWheelDraft.Segments[i - 1], luckyWheelDraft.Segments[i]) = (segment, luckyWheelDraft.Segments[i - 1]); changed = true; }
                        using (UiDisabled.Scope(i == luckyWheelDraft.Segments.Length - 1))
                            if (ImGui.MenuItem("Move down")) { (luckyWheelDraft.Segments[i + 1], luckyWheelDraft.Segments[i]) = (segment, luckyWheelDraft.Segments[i + 1]); changed = true; }
                        ImGui.Separator();
                        using (UiDisabled.Scope(luckyWheelDraft.Segments.Length >= LuckyWheelRules.MaxSegments))
                            if (ImGui.MenuItem("Duplicate"))
                            {
                                var items = luckyWheelDraft.Segments.ToList(); items.Insert(i + 1, segment with { Id = Guid.NewGuid().ToString("N") });
                                luckyWheelDraft.Segments = items.ToArray(); changed = true;
                            }
                        using (UiDisabled.Scope(luckyWheelDraft.Segments.Length <= 2))
                            if (ImGui.MenuItem("Remove")) { luckyWheelDraft.Segments = luckyWheelDraft.Segments.Where((_, index) => index != i).ToArray(); changed = true; }
                        ImGui.EndPopup();
                    }
                    if (changed) break;
                }
            }
            ImGui.Separator();
            using (UiFonts.PushRelative(.90f)) ImGui.TextColored(MutedText, "Colour preset");
            var preset = (int)luckyWheelColours.Selected;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##wheelPalette", ref preset, new[] { "Custom", "Rainbow", "Alternating accent", "Plugin accent" }, 4))
                luckyWheelColours.Select((LuckyWheelColourPreset)preset, luckyWheelDraft,
                    WheelHex(new Vector3(Accent.X, Accent.Y, Accent.Z)), WheelHex(new Vector3(AccentActive.X, AccentActive.Y, AccentActive.Z)));
        });
    }
    private bool WheelOptionIconButton(FontAwesomeIcon icon)
    {
        using var id = ImRaii.PushId("optionIcon");
        return GameLayoutButton("", icon, Ui(30), height: 30);
    }
    private void DrawLuckyWheelSettingsPanels(float width, float height)
    {
        var gap = Ui(14);
        var titleHeight = Ui(186);
        var appearanceHeight = Ui(160);
        GameLayoutPanel("##wheelTitlePanel", width, titleHeight, () =>
        {
            GameLayoutHeading("Title & subtitle", FontAwesomeIcon.Font);
            using (UiFonts.PushRelative(.90f)) ImGui.TextColored(MutedText, "Wheel title");
            var title = luckyWheelDraft.Title;
            ImGui.SetNextItemWidth(-1); if (ImGui.InputText("##wheelTitle", ref title, 60)) luckyWheelDraft.Title = title;
            using (UiFonts.PushRelative(.90f)) ImGui.TextColored(MutedText, "Subtitle");
            var subtitle = luckyWheelDraft.Subtitle;
            ImGui.SetNextItemWidth(-1); if (ImGui.InputText("##wheelSubtitle", ref subtitle, 100)) luckyWheelDraft.Subtitle = subtitle;
        });
        SetGameLayoutRowGap(gap);
        GameLayoutPanel("##wheelAppearancePanel", width, appearanceHeight, () =>
        {
            GameLayoutHeading("Appearance", FontAwesomeIcon.Palette);
            var swatches = new[] { luckyWheelDraft.Background, luckyWheelDraft.TextColour, luckyWheelDraft.BorderColour };
            for (var i = 0; i < swatches.Length; i++)
            {
                if (i > 0) ImGui.SameLine(0, Ui(6));
                using var id = ImRaii.PushId(i);
                ImGui.ColorButton("##swatch", new Vector4(WheelRgb(swatches[i]), 1), ImGuiColorEditFlags.NoTooltip, UiVec(20, 20));
                DrawWheelSwatchBorder(ImGui.GetItemRectMin(), ImGui.GetItemRectSize());
            }
            using (UiFonts.PushRelative(.90f)) ImGui.TextColored(MutedText, "Background / Text / Border");
            if (GameLayoutButton("Customise appearance", FontAwesomeIcon.Cog, ImGui.GetContentRegionAvail().X, height: 32)) luckyWheelDialogRequest = WheelDialog.Appearance;
        });
        SetGameLayoutRowGap(gap);
        GameLayoutPanel("##wheelSpinPanel", width, height - titleHeight - appearanceHeight - gap * 2, () =>
        {
            GameLayoutHeading("Spin settings", FontAwesomeIcon.Cog);
            var spinMode = luckyWheelDraft.EveryoneTakesTurn ? 1 : 0;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##wheelSpinMode", ref spinMode, new[] { "Host only spins", "Everyone takes a turn" }, 2))
                luckyWheelDraft.EveryoneTakesTurn = spinMode == 1;
            var remove = luckyWheelDraft.RemoveWinner;
            if (ImGui.Checkbox("Remove winner after each spin", ref remove)) luckyWheelDraft.RemoveWinner = remove;
            var sound = luckyWheelDraft.Sound;
            if (ImGui.Checkbox("Spin sound effects", ref sound)) luckyWheelDraft.Sound = sound;
            var confetti = luckyWheelDraft.Confetti;
            if (ImGui.Checkbox("Result confetti", ref confetti)) luckyWheelDraft.Confetti = confetti;
        });
    }
}

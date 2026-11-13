using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly Dictionary<string, float> musicScenePanelHeights = new(StringComparer.Ordinal);

    private void MusicSceneContentPanel(string id, float width, Action draw, float verticalPadding = 16f)
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(16f, verticalPadding));
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(12f));
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        // This ImGui version has no auto-height child flags. Measure the actual
        // contents each frame, including wrapping/font changes and optional rows.
        var height = musicScenePanelHeights.TryGetValue(id, out var measured) ? measured : Ui(700);
        using var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(id, new Vector2(width, height), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse));
        if (!panel) return;
        ImGui.SetScrollY(0);
        draw();
        musicScenePanelHeights[id] = MathF.Ceiling(MathF.Max(1,
            ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y + Ui(verticalPadding)));
    }

    private void DrawMusicScenePreviewHeading()
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var badge = "Static preview";
        var badgeSize = ImGui.CalcTextSize(badge) + UiVec(18, 8);
        var inline = width >= ImGui.CalcTextSize("Scene preview").X + badgeSize.X + Ui(16);
        var badgeStart = inline ? origin + new Vector2(width - badgeSize.X, -Ui(2)) : origin + UiVec(0, 26);
        var draw = ImGui.GetWindowDrawList();
        var tint = new Vector4(Accent.X, Accent.Y, Accent.Z, .2f);
        draw.AddRectFilled(badgeStart, badgeStart + badgeSize, ImGui.GetColorU32(tint), Ui(6));
        draw.AddText(badgeStart + UiVec(9, 4), ImGui.GetColorU32(AccentHover), badge);
        using (UiFonts.PushRelative(1f, emphasis: true))
            ImGui.TextColored(Hex(0xE2E7EF), "Scene preview");
        if (!inline) { ImGui.SetCursorScreenPos(badgeStart); ImGui.Dummy(badgeSize); }
        ImGui.Dummy(UiVec(0, 10));
    }

    private void MusicSceneFieldLabel(string label)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(MutedText, label);
        ImGui.SameLine();
        ImGui.SetCursorPosX(Ui(16) + Math.Min(Ui(138), ImGui.GetWindowWidth() * .43f));
        ImGui.SetNextItemWidth(-1);
    }

    private void DrawMusicVisualiser(bool twoRows)
    {
        var gap = Ui(10f);
        var width = ImGui.GetContentRegionAvail().X;
        var labelWidth = ImGui.CalcTextSize("Visualiser").X + ImGui.CalcTextSize("Style").X +
            ImGui.CalcTextSize("Theme").X + ImGui.CalcTextSize("Background").X;
        var comboWidth = MathF.Max(Ui(60f), (width - labelWidth - ImGui.GetFrameHeight() - Ui(24f) - gap * 8f) * .5f);
        ImGui.AlignTextToFramePadding();
        using (UiFonts.PushIcon()) ImGui.TextColored(Accent, FontAwesomeIcon.ChartBar.ToIconString());
        ImGui.SameLine(0f, Ui(6f));
        using (UiFonts.PushRelative(1f, emphasis: true))
            ImGui.TextColored(Hex(0xE2E7EF), "Visualiser");
        ImGui.SameLine(0f, gap * 2f);
        ImGui.TextColored(MutedText, "Style");
        ImGui.SameLine(0f, gap);
        ImGui.SetNextItemWidth(twoRows ? ImGui.GetContentRegionAvail().X : comboWidth);
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##musicVisualiser", AudioVisualizerSelection.GetDisplayName(partyVisualizerMode))))
        {
            foreach (var mode in Enum.GetValues<AudioVisualizerMode>())
            {
                if (ImGui.Selectable(AudioVisualizerSelection.GetDisplayName(mode), mode == partyVisualizerMode))
                { partyVisualizerMode = mode; ApplyMusicScene(); }
                if (mode == AudioVisualizerMode.ClassicBars && ImGui.IsItemHovered())
                    UiFonts.SetTooltip("Less CPU intensive, only reacts to volume");
            }
            ImGui.EndCombo();
        }
        if (partyVisualizerMode == AudioVisualizerMode.ClassicBars && ImGui.IsItemHovered())
            UiFonts.SetTooltip("Less CPU intensive, only reacts to volume");
        if (twoRows) ImGui.AlignTextToFramePadding();
        else ImGui.SameLine(0f, gap * 2f);
        ImGui.TextColored(MutedText, "Theme");
        ImGui.SameLine(0f, gap);
        ImGui.SetNextItemWidth(twoRows
            ? MathF.Max(Ui(60), ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("Background").X -
                ImGui.GetFrameHeight() - gap * 3f)
            : comboWidth);
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##musicTheme", AudioVisualizerSelection.GetThemeDisplayName(partyVisualizerTheme))))
            {
                foreach (var theme in Enum.GetValues<AudioVisualizerTheme>())
                    if (ImGui.Selectable(AudioVisualizerSelection.GetThemeDisplayName(theme), theme == partyVisualizerTheme))
                    { partyVisualizerTheme = theme; ApplyMusicScene(); }
                ImGui.EndCombo();
            }
        ImGui.SameLine(0f, gap * 2f);
        ImGui.TextColored(MutedText, "Background");
        ImGui.SameLine(0f, gap);
        var background = GetMusicEffects().Background;
        if (WheelColourPicker("##musicBackground", ref background))
        { musicEffects = musicEffects with { Background = background }; ApplyMusicScene(); }
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Visualiser background colour");
    }

    private void DrawMusicEffects()
    {
        using (UiFonts.PushRelative(1f, emphasis: true))
        using (ImRaii.PushColor(ImGuiCol.Text, Hex(0xE2E7EF)))
            GameLayoutHeading("Surrounding effects", FontAwesomeIcon.Lightbulb);
        var scene = GetMusicEffects();
        DrawSpookyTimeOption(ref scene);
        string[] labels = ["Speakers", "Lasers", "Halo", "LED strips", "Expanding rings", "Sparkles", "Sweeping spotlights", "Dance floor", "Disco ball", "Neon frame", "Orbiting shapes", "Backdrop curtain", "Spotlight"];
        FontAwesomeIcon[] icons = [FontAwesomeIcon.Music, FontAwesomeIcon.Bolt, FontAwesomeIcon.Circle,
            FontAwesomeIcon.ChartBar, FontAwesomeIcon.Circle, FontAwesomeIcon.Star,
            FontAwesomeIcon.Lightbulb, FontAwesomeIcon.Th, FontAwesomeIcon.Circle,
            FontAwesomeIcon.Square, FontAwesomeIcon.Star, FontAwesomeIcon.ChartBar, FontAwesomeIcon.Lightbulb];
        int[] bits = [1, 32, 2, 4, 8, 16, 64, 128, 256, 1024, 2048, 8192, MusicSceneEffects.Spotlight];
        uint[] colours = [scene.Speakers, scene.Lasers, scene.Halo, scene.Leds, scene.Rings, scene.Sparkles, scene.Spotlights, scene.DanceFloor, scene.DiscoBall, scene.NeonFrame, scene.Orbits, scene.Curtain, scene.SpotlightColour];
        for (var i = 0; i < labels.Length; i++)
        {
            using var id = ImRaii.PushId(i);
            using var disabled = UiDisabled.Scope(scene.IsSpooky && i == 7);
            var start = ImGui.GetCursorScreenPos();
            var width = ImGui.GetContentRegionAvail().X;
            var rowHeight = Math.Max(Ui(30), ImGui.GetFrameHeight());
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(start, start + new Vector2(width, rowHeight), ImGui.GetColorU32(FrameBg), Ui(6));
            draw.AddRect(start, start + new Vector2(width, rowHeight), ImGui.GetColorU32(BorderSubtle), Ui(6));
            using (UiFonts.PushIcon())
                draw.AddText(start + UiVec(8, 7), ImGui.GetColorU32(MutedText), icons[i].ToIconString());
            draw.AddText(start + UiVec(32, 7), ImGui.GetColorU32(Hex(0xE2E7EF)),
                TruncateToWidth(labels[i], Math.Max(0, width - Ui(i is 3 or 7 or 12 ? 158 : 126))));
            var enabled = (scene.EffectiveMask & bits[i]) != 0;
            ImGui.SetCursorScreenPos(start + new Vector2(width - Ui(92), (rowHeight - Ui(20)) / 2));
            var changed = DrawSettingsToggle("##effectToggle", "", ref enabled);
            ImGui.SetCursorScreenPos(start + new Vector2(width - ImGui.GetFrameHeight() - Ui(8), (rowHeight - ImGui.GetFrameHeight()) / 2));
            var colour = colours[i];
            var colorChanged = WheelColourPicker("##effectColour", ref colour);
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip(labels[i] + " colour");
            if (changed || colorChanged)
            {
                musicEffects = scene with { Mask = enabled ? scene.Mask | bits[i] : scene.Mask & ~bits[i] };
                musicEffects = i switch
                {
                    0 => musicEffects with { Speakers = colour }, 1 => musicEffects with { Lasers = colour },
                    2 => musicEffects with { Halo = colour },
                    3 => musicEffects with { Leds = colour, RainbowLeds = colorChanged ? false : scene.RainbowLeds },
                    4 => musicEffects with { Rings = colour }, 5 => musicEffects with { Sparkles = colour },
                    6 => musicEffects with { Spotlights = colour }, 7 => musicEffects with { DanceFloor = colour },
                    8 => musicEffects with { DiscoBall = colour }, 9 => musicEffects with { NeonFrame = colour },
                    10 => musicEffects with { Orbits = colour }, 11 => musicEffects with { Curtain = colour },
                    _ => musicEffects with { SpotlightColour = colour }
                };
                ApplyMusicScene(); scene = musicEffects;
            }
            if (i is 3 or 7 or 12)
            {
                ImGui.SetCursorScreenPos(start + new Vector2(width - Ui(124), (rowHeight - Ui(24)) / 2));
                if (GameLayoutButton("", FontAwesomeIcon.Cog, Ui(26), height: 24)) ImGui.OpenPopup("##effectSettings");
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip(labels[i] + " settings");
                if (ImGui.IsPopupOpen("##effectSettings")) ImGui.SetNextWindowSize(new Vector2(Ui(280), 0), ImGuiCond.Always);
                using var popupStyle = ImRaii.PushStyle(ImGuiStyleVar.PopupBorderSize, Ui(1f))
                    .Push(ImGuiStyleVar.PopupRounding, Ui(8f));
                using var popupColours = ImRaii.PushColor(ImGuiCol.PopupBg, CardBg)
                    .Push(ImGuiCol.Border, Accent).Push(ImGuiCol.Text, Hex(0xE2E7EF));
                using var popup = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Popup("##effectSettings", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse));
                if (popup)
                {
                    using (UiFonts.PushRelative(1f, emphasis: true)) ImGui.TextUnformatted(labels[i]);
                    ImGui.Separator(); ImGui.Spacing();
                    if (i == 3)
                    {
                        var rainbow = scene.RainbowLeds;
                        if (ImGui.Checkbox("Rainbow LED colours", ref rainbow))
                        { musicEffects = scene = scene with { RainbowLeds = rainbow }; ApplyMusicScene(); }
                    }
                    else if (i == 12)
                    {
                        ImGui.TextUnformatted("Brightness");
                        var brightness = scene.SpotlightBrightness * 100;
                        ImGui.SetNextItemWidth(-1);
                        if (ImGui.SliderFloat("##spotlightBrightness", ref brightness, 0, 500, "%.0f%%"))
                        { musicEffects = scene = scene with { SpotlightBrightness = brightness / 100 }; ApplyMusicScene(); }
                        ImGui.TextUnformatted("Range");
                        var range = scene.SpotlightRange;
                        ImGui.SetNextItemWidth(-1);
                        if (ImGui.SliderFloat("##spotlightRange", ref range, 1, 60, "%.1f yalms"))
                        { musicEffects = scene = scene with { SpotlightRange = range }; ApplyMusicScene(); }
                    }
                    else
                    {
                        ImGui.TextUnformatted("Height offset");
                        var offset = scene.FloorOffset;
                        ImGui.SetNextItemWidth(-1);
                        if (ImGui.SliderFloat("##floorOffset", ref offset, -5, 5, "%.2f"))
                        { musicEffects = scene = scene with { FloorOffset = offset }; ApplyMusicScene(); }
                    }
                }
            }
            ImGui.SetCursorScreenPos(start);
            ImGui.Dummy(new Vector2(width, rowHeight));
            SetGameLayoutRowGap(Ui(3));
        }
    }

    private void DrawMusicSceneText()
    {
        var heading = ImGui.GetCursorScreenPos();
        var panelWidth = ImGui.GetContentRegionAvail().X;
        using (UiFonts.PushRelative(1f, emphasis: true))
        using (ImRaii.PushColor(ImGuiCol.Text, Hex(0xE2E7EF)))
            GameLayoutHeading("Screen text", FontAwesomeIcon.Font);
        var belowHeading = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(heading + new Vector2(panelWidth - Ui(94), -Ui(2)));
        if (GameLayoutButton("Variables", FontAwesomeIcon.InfoCircle, Ui(94), height: 26)) OpenMusicDialog("variables");
        ImGui.SetCursorScreenPos(belowHeading);
        MusicSceneFieldLabel("Above (top)");
        var top = partyAudioScreenText.TopText;
        if (ImGui.InputTextWithHint("##musicTopText", "None", ref top, AudioScreenTextSelection.MaxTextLength + 1))
            partyAudioScreenText = partyAudioScreenText with { TopText = top };
        if (ImGui.IsItemDeactivatedAfterEdit()) ApplyMusicScene();
        MusicSceneFieldLabel("Middle (centre)");
        var middle = partyAudioScreenText.MiddleText;
        if (ImGui.InputTextWithHint("##musicMiddleText", "None", ref middle, AudioScreenTextSelection.MaxTextLength + 1))
            partyAudioScreenText = partyAudioScreenText with { MiddleText = middle };
        if (ImGui.IsItemDeactivatedAfterEdit()) ApplyMusicScene();
        MusicSceneFieldLabel("Below (bottom)");
        var bottom = partyAudioScreenText.BottomText;
        if (ImGui.InputTextWithHint("##musicBottomText", "None", ref bottom, AudioScreenTextSelection.MaxTextLength + 1))
            partyAudioScreenText = partyAudioScreenText with { BottomText = bottom };
        if (ImGui.IsItemDeactivatedAfterEdit()) ApplyMusicScene();
        if (GameLayoutButton("Edit text", FontAwesomeIcon.Font, ImGui.GetContentRegionAvail().X, height: 32))
            OpenMusicDialog("text");
    }
}

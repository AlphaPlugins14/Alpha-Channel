using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawPartyAudioSettingsOverlay()
    {
        if (!partyAudioSettingsOverlayOpen) return;

        // Radio and DJ audio can play outside the saved video queue. Keep the
        // entry that opened this overlay, and ensure it still matches playback.
        var current = partyAudioSettingsMedia;
        if (stream.Mode == StreamMode.Viewing || !video.IsAudioOnly || current is null ||
            string.IsNullOrWhiteSpace(video.CurrentMediaUrl) ||
            !AudioVisualizerSelection.IsSameMedia(current.Url, video.CurrentMediaUrl))
        {
            partyAudioSettingsOverlayOpen = false;
            partyAudioSettingsMedia = null;
            return;
        }

        var parentPosition = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var panelWidth = MathF.Min(Ui(650f), parentSize.X - Ui(32f));
        var panelHeight = MathF.Min(Ui(685f), parentSize.Y - Ui(32f));
        var panelPosition = parentPosition + new Vector2(
            (parentSize.X - panelWidth) * 0.5f,
            (parentSize.Y - panelHeight) * 0.5f);

        ImGui.SetNextWindowPos(parentPosition, ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;

        if (!BeginWindowOverlay("##partyAudioSettingsOverlay", overlayFlags))
        {
            ImGui.End();
            return;
        }

        ImGui.GetWindowDrawList().AddRectFilled(
            parentPosition, parentPosition + parentSize,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.54f)));
        ImGui.SetCursorScreenPos(panelPosition);

        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildBorderSize, Ui(1f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(24f, 20f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8f, 9f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg))
        using (ImRaii.PushColor(ImGuiCol.Border,
                   new Vector4(Accent.X, Accent.Y, Accent.Z, 0.82f)))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##partyAudioSettingsPanel",
                   new Vector2(panelWidth, panelHeight), true)))
        {
            if (panel)
            {
                using var uiFontScale2944=UiFonts.PushRelative(1.18f);
                ImGui.TextColored(Vector4.One, "Adjust visualizer / on-screen text");
                uiFontScale2944.Dispose();
                if (panelWidth > Ui(530f))
                {
                    ImGui.SameLine();
                    ImGui.SetCursorPosX(panelWidth - Ui(55f));
                    if (ImGui.SmallButton("X##partyAudioSettingsCloseTop"))
                        partyAudioSettingsOverlayOpen = false;
                }
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
                ImGui.TextColored(MutedText,
                    "Your visualizer choice and text will be shared with your watch party");
                ImGui.PopTextWrapPos();
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                DrawPartyAudioVisualizerFields(current);
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                DrawPartyAudioTextSection(current, 0);
                ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
                DrawPartyAudioTextSection(current, 1);
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                DrawPartyAudioTextSection(current, 2);
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                var closeWidth = Ui(116f);
                ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(),
                    panelWidth - Ui(24f) - closeWidth));
                if (ImGui.Button("Close##partyAudioSettingsClose",
                        new Vector2(closeWidth, Ui(38f))))
                    partyAudioSettingsOverlayOpen = false;
            }
        }

        ImGui.End();
    }

    private void DrawPartyAudioVisualizerFields(VideoQueueEntry current)
    {
        var gap = Ui(16f);
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var compact = availableWidth < Ui(430f);
        var fieldWidth = compact ? availableWidth : (availableWidth - gap) * 0.5f;
        using (ImRaii.Group())
        {
            ImGui.TextColored(Vector4.One, "Visualizer");
            ImGui.SetNextItemWidth(fieldWidth);
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##partyAudioVisualizerModal",
                    AudioVisualizerSelection.GetDisplayName(partyVisualizerMode))))
            {
                foreach (var option in Enum.GetValues<AudioVisualizerMode>())
                    DrawPartyVisualizerOption(current, option);
                ImGui.EndCombo();
            }
        }

        if (!compact) ImGui.SameLine(0f, gap);
        using (ImRaii.Group())
        {
            ImGui.TextColored(Vector4.One, "Colour");
            ImGui.SetNextItemWidth(fieldWidth);
            {
                if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##partyAudioThemeModal",
                        AudioVisualizerSelection.GetThemeDisplayName(partyVisualizerTheme))))
                {
                    foreach (var option in Enum.GetValues<AudioVisualizerTheme>())
                        DrawPartyVisualizerThemeOption(current, option);
                    ImGui.EndCombo();
                }
            }
        }

        ImGui.Spacing();
        DrawTvAudioEffectsSetting();
    }

    private void DrawPartyAudioTextSection(VideoQueueEntry current, int band)
    {
        using (musicDialog == "text" ? UiFonts.PushRelative(1f, emphasis: true) : UiFonts.Empty)
            ImGui.TextColored(musicDialog == "text" ? Hex(0xE2E7EF) : Vector4.One,
                band == 0 ? "Top text" : band == 1 ? "Middle text" : "Bottom text");
        using (musicDialog == "text" ? UiFonts.PushRelative(0.90f) : UiFonts.Empty)
        ImGui.TextColored(MutedText,
            band == 0 ? "Shown over the visualiser at the top of the TV."
                : band == 1 ? "Shown over the visualiser in the middle of the TV."
                : "Shown over the visualiser at the bottom of the TV.");

        using var inputBorder = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, Ui(1));
        using var inputColours = ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Lerp(FrameBg, Vector4.One, .055f))
            .Push(ImGuiCol.FrameBgHovered, FrameBgHover)
            .Push(ImGuiCol.FrameBgActive, AccentFieldActive)
            .Push(ImGuiCol.Border, BorderSubtle);
        var value = band == 0 ? partyAudioScreenText.TopText : band == 1 ? partyAudioScreenText.MiddleText : partyAudioScreenText.BottomText;
        var previousValue = value;
        float counterWidth;
        using (musicDialog == "text" ? UiFonts.PushRelative(0.90f) : UiFonts.Empty)
            counterWidth = ImGui.CalcTextSize($"{AudioScreenTextSelection.MaxTextLength}/{AudioScreenTextSelection.MaxTextLength}").X;
        var counterGap = Ui(10f);
        ImGui.SetNextItemWidth(MathF.Max(Ui(100f),
            ImGui.GetContentRegionAvail().X - counterWidth - counterGap));
        // ImGui reserves one buffer byte for the terminating zero. The
        // sanitizer limits pasted and typed text to the screen text maximum.
        if (ImGui.InputTextWithHint("##partyAudioText" + band,
                band == 0 ? "Enter top text..." : band == 1 ? "Enter middle text..." : "Enter bottom text...",
                ref value, AudioScreenTextSelection.MaxTextLength + 1))
        {
            value = AudioScreenTextSelection.SanitizeText(value);
            if (value != previousValue)
            {
                partyAudioScreenText = band == 0 ? partyAudioScreenText with { TopText = value }
                    : band == 1 ? partyAudioScreenText with { MiddleText = value }
                    : partyAudioScreenText with { BottomText = value };
                ApplyPartyAudioPresentation(current);
            }
        }
        if (ImGui.IsItemHovered())
            UiFonts.SetTooltip("Use ordinary text or placeholders such as {nowplaying} and {listeners}.");
        ImGui.SameLine(0f, counterGap);
        using (musicDialog == "text" ? UiFonts.PushRelative(0.90f) : UiFonts.Empty)
        ImGui.TextColored(value.Length >= AudioScreenTextSelection.MaxTextLength
                ? Danger : MutedText,
            $"{value.Length}/{AudioScreenTextSelection.MaxTextLength}");

        var compact = ImGui.GetContentRegionAvail().X < Ui(520f);
        var tableOpen = !compact && ImGui.BeginTable(
            "##partyAudioStyleTable" + band,
            4, ImGuiTableFlags.SizingStretchProp);
        if (tableOpen)
        {
            ImGui.TableSetupColumn("Font", ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableSetupColumn("Alignment", ImGuiTableColumnFlags.WidthFixed, Ui(100));
            ImGui.TableSetupColumn("Font size", ImGuiTableColumnFlags.WidthFixed, Ui(80));
            ImGui.TableSetupColumn("Text colour style", ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableNextColumn();
        }
        var fontChoice = band == 0 ? partyAudioScreenText.TopFont : band == 1 ? partyAudioScreenText.MiddleFont : partyAudioScreenText.BottomFont;
        using (ImRaii.Group())
        {
            ImGui.TextColored(MutedText, "Font");
            ImGui.SetNextItemWidth(tableOpen ? -1f : Ui(190f));
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##partyAudioFont" + band,
                    AudioScreenTextSelection.FontName(fontChoice))))
            {
                foreach (var option in Enum.GetValues<AudioTextFont>())
                {
                    if (ImGui.Selectable(AudioScreenTextSelection.FontName(option),
                            option == fontChoice))
                    {
                        partyAudioScreenText = band == 0 ? partyAudioScreenText with { TopFont = option }
                    : band == 1 ? partyAudioScreenText with { MiddleFont = option }
                    : partyAudioScreenText with { BottomFont = option };
                        ApplyPartyAudioPresentation(current);
                    }
                }
                ImGui.EndCombo();
            }
        }

        if (tableOpen) ImGui.TableNextColumn();
        var alignment = band == 0 ? partyAudioScreenText.TopAlignment : band == 1 ? partyAudioScreenText.MiddleAlignment : partyAudioScreenText.BottomAlignment;
        using (ImRaii.Group())
        {
            ImGui.TextColored(MutedText, "Alignment");
            ImGui.SetNextItemWidth(tableOpen ? -1f : Ui(100f));
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##partyAudioAlign" + band,
                    alignment.ToString())))
            {
                foreach (var option in Enum.GetValues<AudioTextAlignment>())
                {
                    if (ImGui.Selectable(option.ToString(), option == alignment))
                    {
                        partyAudioScreenText = band == 0 ? partyAudioScreenText with { TopAlignment = option }
                    : band == 1 ? partyAudioScreenText with { MiddleAlignment = option }
                    : partyAudioScreenText with { BottomAlignment = option };
                        ApplyPartyAudioPresentation(current);
                    }
                }
                ImGui.EndCombo();
            }
        }

        if (tableOpen) ImGui.TableNextColumn();
        var fontSize = band == 0 ? partyAudioScreenText.TopFontSize : band == 1 ? partyAudioScreenText.MiddleFontSize : partyAudioScreenText.BottomFontSize;
        using (ImRaii.Group())
        {
            ImGui.TextColored(MutedText, "Font size");
            ImGui.SetNextItemWidth(tableOpen ? -1f : Ui(80f));
            if (ImGui.DragInt("##partyAudioFontSize" + band, ref fontSize, 1,
                    AudioScreenTextSelection.MinFontSize, AudioScreenTextSelection.MaxFontSize))
            {
                fontSize = AudioScreenTextSelection.ClampFontSize(fontSize);
                partyAudioScreenText = band == 0 ? partyAudioScreenText with { TopFontSize = fontSize }
                    : band == 1 ? partyAudioScreenText with { MiddleFontSize = fontSize }
                    : partyAudioScreenText with { BottomFontSize = fontSize };
                ApplyPartyAudioPresentation(current);
            }
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Drag to resize the text. Ctrl-click to type a size. Default: 47.");
        }

        if (tableOpen) ImGui.TableNextColumn();
        var gradient = band == 0 ? partyAudioScreenText.TopGradient : band == 1 ? partyAudioScreenText.MiddleGradient : partyAudioScreenText.BottomGradient;
        var packedColor = band == 0 ? partyAudioScreenText.TopColor : band == 1 ? partyAudioScreenText.MiddleColor : partyAudioScreenText.BottomColor;
        var color = new Vector3(
            ((packedColor >> 16) & 0xFF) / 255f,
            ((packedColor >> 8) & 0xFF) / 255f,
            (packedColor & 0xFF) / 255f);
        using (ImRaii.Group())
        {
            ImGui.TextColored(MutedText, "Text colour style");
            ImGui.SetNextItemWidth(tableOpen && gradient == AudioTextGradient.Solid
                ? MathF.Max(Ui(100f), ImGui.GetContentRegionAvail().X - Ui(40f))
                : tableOpen ? -1f : Ui(170f));
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##partyAudioGradient" + band,
                    AudioScreenTextSelection.GradientName(gradient))))
            {
                foreach (var option in Enum.GetValues<AudioTextGradient>())
                {
                    if (ImGui.Selectable(AudioScreenTextSelection.GradientName(option),
                            option == gradient))
                    {
                        partyAudioScreenText = band == 0 ? partyAudioScreenText with { TopGradient = option }
                    : band == 1 ? partyAudioScreenText with { MiddleGradient = option }
                    : partyAudioScreenText with { BottomGradient = option };
                        ApplyPartyAudioPresentation(current);
                    }
                }
                ImGui.EndCombo();
            }

            if (gradient == AudioTextGradient.Solid)
            {
                ImGui.SameLine(0f, Ui(8f));
                if (ImGui.ColorEdit3("##partyAudioColor" + band, ref color,
                        ImGuiColorEditFlags.NoInputs))
                {
                    var packed = (uint)(Math.Clamp((int)(color.X * 255f), 0, 255) << 16 |
                        Math.Clamp((int)(color.Y * 255f), 0, 255) << 8 |
                        Math.Clamp((int)(color.Z * 255f), 0, 255));
                    partyAudioScreenText = band == 0 ? partyAudioScreenText with { TopColor = packed }
                    : band == 1 ? partyAudioScreenText with { MiddleColor = packed }
                    : partyAudioScreenText with { BottomColor = packed };
                    ApplyPartyAudioPresentation(current);
                }
            }
        }
        if (tableOpen) ImGui.EndTable();
    }
}

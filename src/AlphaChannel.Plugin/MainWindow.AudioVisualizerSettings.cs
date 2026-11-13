using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private const string ClassicBarsPreviewCaption =
        "Classic Bars · Less performance cost · Reacts to audio volume only";
    private const string EnhancedEffectsPreviewCaption =
        "Enhanced effects · Higher performance cost · React to waveform and frequency detail";

    private void DrawViewerAudioVisualizerSettings()
    {
        const string description = "Some audio visualisers are more intensive to generate. " +
            "Classic Bars react less to the audio, but require much less processing power. " +
            "This override only applies when you are a Watch Party viewer; solo playback and hosting use your chosen visualiser.";
        var wrapWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X - Ui(42f));
        var lineHeight = ImGui.GetTextLineHeight();
        var descriptionHeight = ImGui.CalcTextSize(description, false, wrapWidth).Y;
        var previewWidth = MathF.Max(1f, (wrapWidth - Ui(12f)) * 0.5f);
        var captionHeight = MathF.Max(
            ImGui.CalcTextSize(ClassicBarsPreviewCaption, false, previewWidth).Y,
            ImGui.CalcTextSize(EnhancedEffectsPreviewCaption, false, previewWidth).Y);
        // Include the panel padding, toggle, both spacer items, preview and
        // caption, plus the actual item spacing inherited from Settings.
        // The old fixed allowance omitted that spacing and clipped the bottom.
        var height = (Ui(146f) + MathF.Max(Ui(20f), lineHeight) +
            lineHeight * 2f + captionHeight + descriptionHeight + ImGui.GetStyle().ItemSpacing.Y * 9f) / Ui(1f);
        DrawOtherSettingsPanel("##viewerAudioVisualizerSettings", height, () =>
        {
            SettingsSection("Watch Party audio visualisers", "Use a lighter visualiser while watching.");
            var classicOnly = Plugin.Cfg.PreferClassicBarsWhenViewing;
            if (DrawSettingsToggle("##viewerClassicBarsOverride", "Always use Classic Bars as a viewer", ref classicOnly))
            {
                Plugin.Cfg.PreferClassicBarsWhenViewing = classicOnly;
                Plugin.Cfg.Save();
                video.SetViewerClassicBarsOverride(stream.Mode == StreamMode.Viewing && classicOnly);
            }
            ImGui.Dummy(UiVec(0f, 8f));
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            DrawSettingsHelper(description);
            ImGui.PopTextWrapPos();
            ImGui.Dummy(UiVec(0f, 12f));

            var width = (ImGui.GetContentRegionAvail().X - Ui(12f)) * 0.5f;
            DrawAudioVisualizerSettingsPreview(width, classic: true);
            ImGui.SameLine(0f, Ui(12f));
            DrawAudioVisualizerSettingsPreview(width, classic: false);
        });
    }

    private void DrawTvAudioEffectsSetting()
    {
        ImGui.TextWrapped("The host controls the shared music scene. You can also design scenes when playing solo.");
        if (GameLayoutButton("Open music scene", Dalamud.Interface.FontAwesomeIcon.Palette, ImGui.GetContentRegionAvail().X))
        { currentPage = HomePage.Music; musicTab = MusicTab.Scene; }
    }

    private static void DrawAudioVisualizerSettingsPreview(float width, bool classic)
    {
        ImGui.BeginGroup();
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, Ui(86f));
        ImGui.Dummy(size);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(CardBg), Ui(8f));
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(BorderSubtle), Ui(8f));
        var centerY = origin.Y + size.Y * 0.5f;
        var left = origin.X + Ui(16f);
        var innerWidth = MathF.Max(1f, width - Ui(32f));
        if (classic)
        {
            ReadOnlySpan<float> heights = [16f, 28f, 43f, 59f, 38f, 51f, 64f, 48f, 32f, 57f, 42f, 25f, 15f];
            var step = innerWidth / heights.Length;
            for (var i = 0; i < heights.Length; i++)
            {
                var halfHeight = Ui(heights[i]) * 0.5f;
                var x = left + step * (i + 0.2f);
                draw.AddRectFilled(new Vector2(x, centerY - halfHeight),
                    new Vector2(x + step * 0.6f, centerY + halfHeight),
                    ImGui.GetColorU32(new Vector4(0.65f, 0.48f, 1f, 1f)), Ui(1f));
            }
        }
        else
        {
            Vector2 Point(float t) => new(left + t * innerWidth,
                centerY + MathF.Sin(t * MathF.PI * 7f) * (0.35f + 0.65f * MathF.Sin(t * MathF.PI)) * Ui(27f));
            for (var i = 0; i < 64; i++)
            {
                var t = i / 64f;
                var hue = t * MathF.PI * 2f;
                var color = new Vector4(0.5f + 0.5f * MathF.Sin(hue),
                    0.5f + 0.5f * MathF.Sin(hue + 2f * MathF.PI / 3f),
                    0.5f + 0.5f * MathF.Sin(hue + 4f * MathF.PI / 3f), 0.18f);
                var from = Point(t);
                var to = Point((i + 1) / 64f);
                draw.AddLine(from, to, ImGui.GetColorU32(color), Ui(7f));
                color.W = 1f;
                draw.AddLine(from, to, ImGui.GetColorU32(color), Ui(2f));
            }
        }
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        DrawSettingsHelper(classic ? ClassicBarsPreviewCaption : EnhancedEffectsPreviewCaption);
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
    }
}

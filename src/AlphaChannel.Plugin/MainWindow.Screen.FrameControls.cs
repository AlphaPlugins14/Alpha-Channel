using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly Vector4 FrameDisabledText = new(.40f, .45f, .54f, 1);
    private static readonly Vector4 FrameDisabledFill = new(.20f, .23f, .29f, 1);
    private static readonly Vector4 FrameDisabledBorder = new(.29f, .33f, .41f, 1);
    private bool frameColourPopupRequested;
    private bool frameColourPopupDirty;

    private void DrawTvFrameColourControls(TvFrameSelection frame, bool enabled)
    {
        // Explicit colours survive the inactive-window input mask's DisabledAlpha=1.
        using var alpha = ImRaii.PushStyle(ImGuiStyleVar.DisabledAlpha, 1f);
        using var disabled = UiDisabled.Scope(!enabled);
        uint[] swatches = [0x9B55FF, 0x3988FF, 0x00CCDD, 0xEE55AA, 0xFFCC55];
        var diameter = MathF.Min(Ui(32), (ImGui.GetContentRegionAvail().X - Ui(40)) / 6);
        var draw = ImGui.GetWindowDrawList();
        for (var i = 0; i <= swatches.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, Ui(8));
            var custom = i == swatches.Length;
            var origin = ImGui.GetCursorScreenPos();
            var centre = origin + new Vector2(diameter * .5f);
            var radius = diameter * .5f - Ui(2);
            var clicked = ImGui.InvisibleButton("##frameColourCircle" + i, new Vector2(diameter));
            var hovered = enabled && ImGui.IsItemHovered() && Vector2.DistanceSquared(ImGui.GetMousePos(), centre) <= radius * radius;
            var selected = !custom && frame.Colour == swatches[i];
            var fill = !enabled ? FrameDisabledFill : custom ? FrameBg : new Vector4(FrameRgb(swatches[i]), 1);
            draw.AddCircleFilled(centre, radius, ImGui.GetColorU32(fill), 32);
            draw.AddCircle(centre, radius, ImGui.GetColorU32(!enabled ? FrameDisabledBorder : hovered ? AccentHover : BorderSubtle), 32, Ui(1));
            if (selected)
                draw.AddCircle(centre, radius + Ui(2), ImGui.GetColorU32(enabled ? Accent : FrameDisabledBorder), 32, Ui(2));
            if (custom)
            {
                using var icon = UiFonts.PushIcon();
                var text = FontAwesomeIcon.EyeDropper.ToIconString();
                var textSize = ImGui.CalcTextSize(text);
                draw.AddText(centre - textSize * .5f, ImGui.GetColorU32(enabled ? MutedText : FrameDisabledText), text);
            }
            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                UiFonts.SetTooltip(custom ? "Choose a custom colour" : "#" + swatches[i].ToString("X6"));
            }
            if (!enabled || !clicked || !hovered) continue;
            if (custom) frameColourPopupRequested = true;
            else { SaveTvFrame(frame with { Colour = swatches[i] }); frame = CurrentTvFrame; }
        }
    }

    private void DrawTvFrameGlowControls(TvFrameSelection frame, bool enabled)
    {
        using var alpha = ImRaii.PushStyle(ImGuiStyleVar.DisabledAlpha, 1f);
        using var disabled = UiDisabled.Scope(!enabled);
        ImGui.TextColored(enabled ? ImGui.GetStyle().Colors[(int)ImGuiCol.Text] : FrameDisabledText, "Glow");
        var width = (ImGui.GetContentRegionAvail().X - Ui(8)) / 3;
        string[] names = ["Off", "Subtle", "Bright"];
        for (var i = 0; i < names.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, Ui(4));
            var selected = (int)frame.Glow == i;
            var background = enabled ? selected ? Accent : FrameBg : selected ? FrameDisabledFill : FrameBg;
            using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(6));
            using var border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, Ui(1));
            using var colours = ImRaii.PushColor(ImGuiCol.Button, background)
                .Push(ImGuiCol.ButtonHovered, enabled ? selected ? AccentHover : CardBgHover : background)
                .Push(ImGuiCol.ButtonActive, enabled ? AccentActive : background)
                .Push(ImGuiCol.Text, enabled ? Vector4.One : FrameDisabledText)
                .Push(ImGuiCol.Border, enabled ? BorderSubtle : FrameDisabledBorder);
            if (ImGui.Button(names[i] + "##frameGlow", new Vector2(width, Ui(34))) && enabled)
            {
                SaveTvFrame(frame with { Glow = (TvFrameGlow)i });
                frame = CurrentTvFrame;
            }
        }
    }

    private void DrawTvFrameColourPopup(bool followsHost)
    {
        const string id = "Frame colour##screenFrameColour";
        var allowed = !followsHost && TvFrameAppearance.SupportsColour(CurrentTvFrame.Style)
            && TvFrameAccess.CanSelect(CurrentTvFrame.Style, CurrentVerifiedPatreonTier());
        if (frameColourPopupRequested)
        {
            frameColourPopupRequested = false;
            if (allowed) ImGui.OpenPopup(id);
        }
        if (!ImGui.IsPopupOpen(id))
        {
            if (frameColourPopupDirty) { Plugin.Cfg.Save(); frameColourPopupDirty = false; }
            return;
        }
        var root = ImGuiP.GetCurrentWindow().RootWindow;
        var monitor = DesktopMonitors.ForWindow(root.Pos, root.Size);
        var size = Vector2.Min(UiVec(310, 410), monitor.Size);
        var position = monitor.Clamp(root.Pos + (root.Size - size) * .5f, size);
        ImGui.SetNextWindowPos(position);
        ImGui.SetNextWindowSize(size);
        ImGui.SetNextWindowViewport(root.ViewportId);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(16, 16));
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, Ui(12));
        using var border = ImRaii.PushStyle(ImGuiStyleVar.PopupBorderSize, Ui(1));
        using var colours = ImRaii.PushColor(ImGuiCol.PopupBg, CardBg).Push(ImGuiCol.Border, AccentInk(.65f));
        if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup(id, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize))) return;
        try
        {
            if (!allowed)
            {
                if (frameColourPopupDirty) { Plugin.Cfg.Save(); frameColourPopupDirty = false; }
                ImGui.CloseCurrentPopup(); return;
            }
            ImGui.TextUnformatted("Frame colour");
            ImGui.Spacing();
            var colour = FrameRgb(CurrentTvFrame.Colour);
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            if (ImGui.ColorPicker3("##frameColourPicker", ref colour,
                ImGuiColorEditFlags.NoSidePreview | ImGuiColorEditFlags.NoSmallPreview | ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoOptions))
            {
                colour = Vector3.Clamp(colour, Vector3.Zero, Vector3.One);
                Plugin.Cfg.TvFrame = CurrentTvFrame with { Colour = (uint)MathF.Round(colour.X * 255) << 16 | (uint)MathF.Round(colour.Y * 255) << 8 | (uint)MathF.Round(colour.Z * 255) };
                UpdateTvFrame();
                frameColourPopupDirty = true;
            }
            if (ImGui.IsItemDeactivatedAfterEdit() && frameColourPopupDirty)
            { Plugin.Cfg.Save(); frameColourPopupDirty = false; }
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            if (ImGui.ColorEdit3("##frameColourHex", ref colour, ImGuiColorEditFlags.NoPicker |
                ImGuiColorEditFlags.NoSmallPreview | ImGuiColorEditFlags.NoOptions | ImGuiColorEditFlags.DisplayHex))
            {
                SaveTvFrame(CurrentTvFrame with { Colour = (uint)MathF.Round(colour.X * 255) << 16 |
                    (uint)MathF.Round(colour.Y * 255) << 8 | (uint)MathF.Round(colour.Z * 255) });
                frameColourPopupDirty = false;
            }
            ImGui.Spacing();
            if (GameLayoutButton("Done", FontAwesomeIcon.Check, ImGui.GetContentRegionAvail().X, primary: true))
            {
                if (frameColourPopupDirty) { Plugin.Cfg.Save(); frameColourPopupDirty = false; }
                ImGui.CloseCurrentPopup();
            }
        }
        finally { ImGui.EndPopup(); }
    }
}

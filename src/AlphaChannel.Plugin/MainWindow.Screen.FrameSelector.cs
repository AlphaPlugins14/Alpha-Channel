using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool frameSelectorPremium;
    private string frameSelectorSearch = string.Empty;
    private bool frameBrowserInitialized;
    private TvFrameStyle frameBrowserLastApplied;
    private TvFrameStyle? frameBrowserPreviewStyle;
    private bool frameBrowserScrollToSelection;
    private TvFrameSelection FramePreviewSelection => frameBrowserPreviewStyle is { } style
        ? CurrentTvFrame with { Style = style } : CurrentTvFrame;

    private void SynchronizeFrameBrowser(bool followsHost)
    {
        var applied = CurrentTvFrame.Style;
        if (!frameBrowserInitialized || applied != frameBrowserLastApplied || followsHost)
        {
            frameSelectorPremium = TvFrameAccess.RequiresPatreon(applied);
            frameBrowserPreviewStyle = null;
            frameBrowserLastApplied = applied;
            frameBrowserInitialized = true;
            frameBrowserScrollToSelection = true;
        }
    }

    private TvFrameStyle[] MatchingFrameStyles() => Enum.GetValues<TvFrameStyle>()
        .Where(style => TvFrameAccess.RequiresPatreon(style) == frameSelectorPremium &&
            TvFrameNames[(int)style].Contains(frameSelectorSearch.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();

    private void SelectFrameForBrowse(TvFrameStyle style)
    {
        if (TvFrameAccess.CanSelect(style, CurrentVerifiedPatreonTier()))
        {
            SaveTvFrame(CurrentTvFrame with { Style = style });
            frameBrowserPreviewStyle = null;
            frameBrowserLastApplied = style;
        }
        else frameBrowserPreviewStyle = style; // UI preview only; never published or sent to the renderer.
    }

    private void CycleFramePreview(int direction)
    {
        var matches = MatchingFrameStyles();
        if (matches.Length == 0) return;
        var index = Array.IndexOf(matches, FramePreviewSelection.Style);
        var next = index < 0 ? direction < 0 ? matches.Length - 1 : 0
            : (index + direction + matches.Length) % matches.Length;
        SelectFrameForBrowse(matches[next]);
        frameBrowserScrollToSelection = true;
    }

    private void DrawTvFrameBrowser(bool followsHost)
    {
        var width = ImGui.GetContentRegionAvail().X;
        using var alpha = ImRaii.PushStyle(ImGuiStyleVar.DisabledAlpha, followsHost ? .5f : 1f);
        using var disabled = UiDisabled.Scope(followsHost);
        var half = (width - Ui(8)) * .5f;
        if (GameLayoutButton("Free", FontAwesomeIcon.Tv, half, !frameSelectorPremium, height: 32)) frameSelectorPremium = false;
        ImGui.SameLine(0, Ui(8));
        if (GameLayoutButton("Patreon", FontAwesomeIcon.Heart, half, frameSelectorPremium, height: 32)) frameSelectorPremium = true;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##frameSearch", "Search frames…", ref frameSelectorSearch, 100);
        var rowHeight = ImGui.GetTextLineHeight() + Ui(12);
        var listHeight = rowHeight * 5 + ImGui.GetStyle().ItemSpacing.Y * 4;
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(7));
        using var border = ImRaii.PushColor(ImGuiCol.Border, BorderSubtle);
        using (var list = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##frameChoices", new Vector2(0, listHeight), true, ImGuiWindowFlags.AlwaysVerticalScrollbar)))
        {
            if (list)
            {
                var matches = MatchingFrameStyles();
                foreach (var style in matches)
                {
                    if (DrawTvFrameChoice(style, ImGui.GetContentRegionAvail().X, CurrentVerifiedPatreonTier()) && !followsHost)
                        SelectFrameForBrowse(style);
                    if (frameBrowserScrollToSelection && FramePreviewSelection.Style == style) ImGui.SetScrollHereY(.5f);
                }
                if (matches.Length == 0) ImGui.TextColored(MutedText, "No matching frames.");
            }
        }
        frameBrowserScrollToSelection = false;
    }

    private bool DrawTvFrameChoice(TvFrameStyle style, float width, int tier)
    {
        var premium = TvFrameAccess.RequiresPatreon(style);
        var allowed = TvFrameAccess.CanSelect(style, tier);
        var selected = FramePreviewSelection.Style == style;
        var p = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, ImGui.GetTextLineHeight() + Ui(12));
        var clicked = ImGui.InvisibleButton("##frameChoice" + (int)style, size);
        var hover = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(p, p + size, ImGui.GetColorU32(selected ? Accent : hover ? CardBgHover : FrameBg), Ui(5));
        draw.PushClipRect(p + UiVec(8, 0), p + size - UiVec(28, 0), true);
        draw.AddText(p + new Vector2(Ui(10), (size.Y - ImGui.GetTextLineHeight()) * .5f),
            ImGui.GetColorU32(selected || allowed ? Vector4.One : MutedText), TvFrameNames[(int)style]);
        draw.PopClipRect();
        if (premium || selected)
        {
            using var icon = UiFonts.PushIcon();
            var glyph = !allowed ? FontAwesomeIcon.Lock : selected ? FontAwesomeIcon.Check : FontAwesomeIcon.Heart;
            draw.AddText(p + new Vector2(width - Ui(23), (size.Y - ImGui.GetFontSize()) * .5f),
                ImGui.GetColorU32(selected ? Vector4.One : MutedText), glyph.ToIconString());
        }
        if (hover && !allowed) UiFonts.SetTooltip("Preview only · Patreon tier 1 required to apply");
        return clicked;
    }

    private void DrawTvFramePreviewNavigation(float width, bool followsHost)
    {
        var origin = ImGui.GetCursorScreenPos();
        using (UiDisabled.Scope(followsHost || MatchingFrameStyles().Length == 0))
        {
            using (UiFonts.PushIcon())
                if (ImGui.Button(FontAwesomeIcon.ChevronLeft.ToIconString() + "##framePrevious", UiVec(32, 32))) CycleFramePreview(-1);
            ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(32), 0));
            using (UiFonts.PushIcon())
                if (ImGui.Button(FontAwesomeIcon.ChevronRight.ToIconString() + "##frameNext", UiVec(32, 32))) CycleFramePreview(1);
        }
        var name = TvFrameNames[(int)FramePreviewSelection.Style];
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(origin + UiVec(40, 0), origin + new Vector2(width - Ui(40), Ui(32)), true);
        draw.AddText(origin + new Vector2((width - ImGui.CalcTextSize(name).X) * .5f, (Ui(32) - ImGui.GetTextLineHeight()) * .5f),
            ImGui.GetColorU32(Vector4.One), name);
        draw.PopClipRect();
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, Ui(32)));
    }
}

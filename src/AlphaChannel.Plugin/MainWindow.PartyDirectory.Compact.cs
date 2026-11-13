using AlphaChannel.Contracts;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private (FontAwesomeIcon Icon, string Text, Vector4 Color)[] DirectoryRoomTags(RoomDirectoryDto room)
    {
        var tags = new List<(FontAwesomeIcon, string, Vector4)>
        {
            (WatchPartyVisibilityIcon(room.Kind), WatchPartyVisibilityText(room.Kind), WatchPartyVisibilityColor(room.Kind)),
            (WatchPartyCategoryIcon(room), WatchPartyCategoryText(room), WatchPartyCategoryColor(room)),
        };
        var server = WatchPartyServer(room);
        if (!string.IsNullOrWhiteSpace(server))
            tags.Add((FontAwesomeIcon.Server, server.ToUpperInvariant(), new Vector4(0.35f, 0.65f, 1f, 1f)));
        tags.Add((FontAwesomeIcon.Eye, RoomCapacityText(room.HostAccountId,room.ViewerCount,includeHost:true), MutedText));
        if (WatchPartyIsAdultOnly(room))
            tags.Add((FontAwesomeIcon.ExclamationTriangle, "18+", new Vector4(1f, 0.28f, 0.32f, 1f)));
        return tags.ToArray();
    }

    // Measure with the same icon scale and padding used by DrawWatchPartyTag.
    private float DirectoryTagWidth(FontAwesomeIcon icon, string text)
    {
        float iconWidth;
        using (UiFonts.PushIcon())
            iconWidth = ImGui.CalcTextSize(icon.ToIconString()).X * 0.82f;
        return Ui(20f) + iconWidth + ImGui.CalcTextSize(text).X;
    }

    private int LayoutDirectoryRoomTags(ImDrawListPtr draw, Vector2 origin, float width,
        (FontAwesomeIcon Icon, string Text, Vector4 Color)[] tags, bool render)
    {
        var x = 0f;
        var row = 0;
        foreach (var tag in tags)
        {
            using var tagFont = tag.Icon == FontAwesomeIcon.Eye ? UiFonts.PushRelative(.90f) : UiFonts.Empty;
            var overhead = DirectoryTagWidth(tag.Icon, string.Empty);
            var label = PartyDirectoryTrimToWidth(tag.Text, MathF.Max(1f, width - overhead));
            var tagWidth = DirectoryTagWidth(tag.Icon, label);
            if (x > 0f && x + tagWidth > width)
            {
                x = 0f;
                row++;
            }
            if (render)
            {
                var position = origin + new Vector2(x, row * Ui(28f));
                DrawWatchPartyTag(draw, position, tag.Icon, label, tag.Color);
                if (label != tag.Text && ImGui.IsMouseHoveringRect(position, position + new Vector2(tagWidth, Ui(22f))))
                    UiFonts.SetTooltip(tag.Text);
            }
            x += tagWidth + Ui(7f);
        }
        return row + 1;
    }

    private int PartyDirectoryActiveFilterCount() =>
        (partyDirectoryRoomKinds.Count != Enum.GetValues<RoomKind>().Length ? 1 : 0) +
        (partyDirectoryCategories.Count != Enum.GetValues<WatchPartyCategory>()
            .Count(category => category != WatchPartyCategory.Unknown) ? 1 : 0) +
        (partyDirectoryContentTypeFilter is not null ? 1 : 0) +
        (partyDirectoryRatingFilter != 0 ? 1 : 0) +
        (!string.IsNullOrWhiteSpace(partyDirectoryServer) ? 1 : 0) +
        (partyDirectoryVibeFilters.Count > 0 ? 1 : 0);

    private void DrawPartyDirectoryFilterButton(string label, float width, float height)
    {
        var origin = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton("##directoryFiltersToggle", new Vector2(width, height)))
            ImGui.OpenPopup("##directoryFiltersPopup");
        var highlighted = ImGui.IsItemHovered() || ImGui.IsPopupOpen("##directoryFiltersPopup");
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + new Vector2(width, height),
            ImGui.GetColorU32(highlighted ? CardBgHover : CardBg), Ui(8f));
        draw.AddRect(origin, origin + new Vector2(width, height),
            ImGui.GetColorU32(highlighted ? AccentHover : BorderSubtle), Ui(8f));
        var textY = (height - ImGui.GetTextLineHeight()) * 0.5f;
        UiFonts.DrawIcon(draw, ImGui.GetFontSize(), origin + new Vector2(Ui(10f), textY), ImGui.GetColorU32(AccentHover), FontAwesomeIcon.Filter.ToIconString());
        draw.AddText(origin + new Vector2(Ui(30f), textY), ImGui.GetColorU32(Vector4.One), label);
        UiFonts.DrawIcon(draw, ImGui.GetFontSize() * 0.8f, origin + new Vector2(width - Ui(21f), textY + Ui(2f)), ImGui.GetColorU32(MutedText), FontAwesomeIcon.ChevronDown.ToIconString());

        var viewport = ImGui.GetWindowViewport();
        var workMin = viewport.WorkPos + UiVec(12f, 12f);
        var workMax = viewport.WorkPos + viewport.WorkSize - UiVec(12f, 12f);
        var popupWidth = MathF.Min(Ui(440f), ImGui.GetWindowSize().X - Ui(24f));
        popupWidth = MathF.Min(MathF.Max(Ui(200f), popupWidth), MathF.Max(1f, workMax.X - workMin.X));
        var popupX = MathF.Max(ImGui.GetWindowPos().X + Ui(12f), origin.X + width - popupWidth);
        popupX = Math.Clamp(popupX, workMin.X, MathF.Max(workMin.X, workMax.X - popupWidth));
        // Align the popup's right edge with the button, extending over the directory.
        // Desktop coordinates can be negative or beyond the game's main display.
        ImGui.SetNextWindowViewport(ImGuiP.GetCurrentWindow().ViewportId);
        ImGui.SetNextWindowPos(new Vector2(popupX, origin.Y + height + Ui(5f)), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(popupWidth, 0f), ImGuiCond.Always);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(12f, 12f));
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8f, 9f));
        using var framePadding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(8f, 5f));
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, Ui(9f));
        using var popupBorder = ImRaii.PushStyle(ImGuiStyleVar.PopupBorderSize, Ui(1f));
        using var popupColors = ImRaii.PushColor(ImGuiCol.PopupBg, CardBg)
            .Push(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, .4f));
        using var dropdownColors = ImRaii.PushColor(ImGuiCol.FrameBg, FrameBg)
            .Push(ImGuiCol.FrameBgHovered, FrameBgHover)
            .Push(ImGuiCol.FrameBgActive, AccentFieldActive);
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup("##directoryFiltersPopup")))
        {
            DrawSocialHeading("Filters");
            ImGui.Dummy(UiVec(0f, 2f));
            var contentWidth = ImGui.GetContentRegionAvail().X;
            if (contentWidth >= Ui(320f))
            {
                var columnWidth = (contentWidth - Ui(8f)) * 0.5f;
                DrawPartyDirectoryRoomTypeFilter(columnWidth);
                ImGui.SameLine(0f, Ui(8f));
                DrawPartyDirectoryCategoryFilter(columnWidth);
                DrawPartyDirectoryContentTypeFilter(columnWidth);
                ImGui.SameLine(0f, Ui(8f));
                DrawPartyDirectoryRatingFilter(columnWidth);
                DrawPartyDirectoryServerPlaceholder(columnWidth);
                ImGui.SameLine(0f, Ui(8f));
                DrawPartyDirectoryVibeFilter(columnWidth);
            }
            else
            {
                var filterWidth = ImGui.GetContentRegionAvail().X;
                DrawPartyDirectoryRoomTypeFilter(filterWidth);
                DrawPartyDirectoryCategoryFilter(filterWidth);
                DrawPartyDirectoryContentTypeFilter(filterWidth);
                DrawPartyDirectoryRatingFilter(filterWidth);
                DrawPartyDirectoryServerPlaceholder(filterWidth);
                DrawPartyDirectoryVibeFilter(filterWidth);
            }
            using (UiDisabled.Scope(PartyDirectoryActiveFilterCount() == 0 && string.IsNullOrEmpty(partyDirectorySearch)))
                if (ImGui.Button("Clear filters", new Vector2(contentWidth, 0f)))
                    ResetPartyDirectoryFilters();
            ImGui.EndPopup();
        }
    }

    private bool DrawDirectoryToolbarButton(string id, string label, FontAwesomeIcon icon, float width, float height)
    {
        var origin = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + new Vector2(width, height),
            ImGui.GetColorU32(hovered ? CardBgHover : CardBg), Ui(8f));
        draw.AddRect(origin, origin + new Vector2(width, height),
            ImGui.GetColorU32(hovered ? AccentHover : BorderSubtle), Ui(8f));
        var textY = (height - ImGui.GetTextLineHeight()) * 0.5f;
        UiFonts.DrawIcon(draw, ImGui.GetFontSize(), origin + new Vector2(Ui(10f), textY), ImGui.GetColorU32(AccentHover), icon.ToIconString());
        draw.AddText(origin + new Vector2(Ui(28f), textY), ImGui.GetColorU32(Vector4.One), label);
        return clicked;
    }

    private void SetPartyDirectorySort(bool featuredFirst)
    {
        if (Plugin.Cfg.PartyDirectoryFeaturedFirst == featuredFirst) return;
        Plugin.Cfg.PartyDirectoryFeaturedFirst = featuredFirst;
        Plugin.Cfg.Save();
        partyDirectoryVisibleCount = PartyDirectoryInitialVisibleCount;
    }
}

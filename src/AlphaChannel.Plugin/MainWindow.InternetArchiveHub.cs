using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawInternetArchiveHubHeader()
    {
        var width = ImGui.GetContentRegionAvail().X;
        var innerWidth = width - Ui(32);
        var stacked = innerWidth < Ui(500);
        var extraTabWidth = 0f;
        float TabWidth(string label) => ImGui.CalcTextSize(label).X + Ui(20) + extraTabWidth;
        var rows = 1;
        var used = TabWidth("Popular");
        foreach (var label in new[] { "All" }.Concat(ArchiveCategories.Select(c => c.Label)))
        {
            var tabWidth = TabWidth(label);
            if (used + tabWidth > innerWidth) { rows++; used = tabWidth; }
            else used += tabWidth;
        }
        if (rows == 1) extraTabWidth = MathF.Max(0, innerWidth - used) / (ArchiveCategories.Length + 2);
        var height = Ui(138 + (stacked ? 49 : 0) + (rows - 1) * 38);
        var origin = ImGui.GetCursorScreenPos();
        var panelDraw = ImGui.GetWindowDrawList();
        DrawArcadeGradientPanel(panelDraw, origin, new Vector2(width, height),
            HubSurface(0x0D203B), HubSurface(0x050B17), HubTint(0x24456B), 10);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(16, 14));
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10));
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero)
            .Push(ImGuiCol.Border, HubTint(0x24456B));
        using var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##archiveHubHeader", new Vector2(width, height), false,
            ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse));
        if (!panel) return;
        ImGui.SetCursorPos(UiVec(16, 14));
        using (UiFonts.PushRelative(1.35f, emphasis: true)) ImGui.TextUnformatted("Explore the archive.");
        if (width >= Ui(450)) DrawInternetArchiveHubArtwork(ImGui.GetWindowDrawList(), ImGui.GetWindowPos() + new Vector2(width - Ui(70), Ui(8)));
        ImGui.SetCursorPos(UiVec(16, 50));
        DrawInternetArchiveSearch(stacked);
        ImGui.SetCursorPos(UiVec(16, stacked ? 149 : 100));

        void Tab(string label, bool selected, Action activate)
        {
            var tabOrigin = ImGui.GetCursorScreenPos();
            var tabSize = new Vector2(TabWidth(label), Ui(38));
            if (ImGui.InvisibleButton("##archiveHubTab_" + label, tabSize)) activate();
            var draw = ImGui.GetWindowDrawList();
            if (ImGui.IsItemHovered())
                draw.AddRectFilled(tabOrigin, tabOrigin + tabSize, ImGui.GetColorU32(AccentFieldBg), Ui(5));
            var labelWidth = ImGui.CalcTextSize(label).X;
            draw.AddText(tabOrigin + new Vector2((tabSize.X - labelWidth) / 2, Ui(9)),
                ImGui.GetColorU32(selected ? AccentHover : MutedText), label);
            if (selected)
                panelDraw.AddRectFilled(new Vector2(tabOrigin.X + Ui(5), origin.Y + height - Ui(3)),
                    new Vector2(tabOrigin.X + tabSize.X - Ui(5), origin.Y + height),
                    ImGui.GetColorU32(Accent), Ui(1));
        }
        Tab("Popular", internetArchivePopularTab, () =>
        {
            CancelInternetArchiveSearch();
            CancelInternetArchiveLoadMore();
            internetArchivePopularTab = true;
            RefreshArchivePopular();
        });
        used = TabWidth("Popular");
        void NextTab(string label)
        {
            var tabWidth = TabWidth(label);
            if (used + tabWidth <= innerWidth + .5f) { ImGui.SameLine(0, 0); used += tabWidth; }
            else { ImGui.SetCursorPosX(Ui(16)); used = tabWidth; }
        }
        NextTab("All");
        Tab("All", !internetArchivePopularTab && internetArchiveViewTitle is null, () =>
        {
            CancelInternetArchiveSearch();
            CancelInternetArchiveLoadMore();
            internetArchivePopularTab = false;
            internetArchiveViewTitle = null;
            internetArchiveViewCategoryKey = null;
            internetArchiveViewResults = Array.Empty<Video.InternetArchiveItem>();
        });
        foreach (var category in ArchiveCategories)
        {
            NextTab(category.Label);
            Tab(category.Label, !internetArchivePopularTab && internetArchiveViewCategoryKey == category.Key, () => ShowInternetArchiveCategory(category));
        }
    }

    private static void DrawInternetArchiveHubArtwork(ImDrawListPtr draw, Vector2 origin)
    {
        Vector2 Point(float x, float y) => origin + UiVec(x * .85f, y * .85f);
        var ink = ImGui.GetColorU32(AccentHover);
        var dark = ImGui.GetColorU32(HubSelected(0x17345D));
        draw.AddTriangleFilled(Point(34, 1), Point(7, 12), Point(61, 12), ink);
        draw.AddRectFilled(Point(9, 13), Point(59, 17), ink);
        for (var i = 0; i < 4; i++)
        {
            var x = 14 + i * 11;
            draw.AddRectFilled(Point(x, 19), Point(x + 6, 38), ink);
            draw.AddRectFilled(Point(x + 3, 19), Point(x + 6, 38), dark);
        }
        draw.AddRectFilled(Point(8, 40), Point(60, 43), ink);
        draw.AddRectFilled(Point(4, 45), Point(64, 48), ink);
    }
}

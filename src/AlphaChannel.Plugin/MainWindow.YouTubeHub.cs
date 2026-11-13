using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool youtubeHubCacheUpdating;
    private float YouTubeCardGridWidth => MathF.Max(1, ImGui.GetContentRegionAvail().X - Ui(12));

    private static readonly (int Tab, string Label, FontAwesomeIcon Icon)[] YouTubeHubTabs =
    [
        (4, "Popular", FontAwesomeIcon.Fire),
        (0, "Topics", FontAwesomeIcon.ThLarge),
        (1, "Subscriptions", FontAwesomeIcon.Rss),
        (2, "Likes", FontAwesomeIcon.Heart),
        (3, "Recommended", FontAwesomeIcon.Star)
    ];

    private void DrawYouTubeHubHeader()
    {
        var width = ImGui.GetContentRegionAvail().X;
        var stacked = width < Ui(650);
        var height = Ui(stacked ? 158 : 108);
        var origin = ImGui.GetCursorScreenPos();
        var panelDraw = ImGui.GetWindowDrawList();
        DrawArcadeGradientPanel(panelDraw, origin, new Vector2(width, height),
            HubSurface(0x10223D), HubSurface(0x070F1D), HubTint(0x24456B), 10);
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(16, 14)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero))
        using (var header = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##youtubeHubHeader", new Vector2(width, height), false,
            ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (header)
            {
                ImGui.SetCursorPos(UiVec(16, 14));
                var inner = width - Ui(32);
                var accountWidth = stacked ? MathF.Min(inner, Ui(270)) : Math.Clamp(inner * .34f, Ui(200), Ui(270));
                var searchWidth = stacked ? inner : inner - accountWidth - Ui(12);
                using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7))
                    .Push(ImGuiStyleVar.FramePadding, UiVec(35, 12)).Push(ImGuiStyleVar.FrameBorderSize, Ui(1)))
                using (ImRaii.PushColor(ImGuiCol.FrameBg, HubSurface(0x101A2D))
                    .Push(ImGuiCol.Border, HubTint(0x395A84)))
                {
                    ImGui.SetNextItemWidth(MathF.Max(Ui(80), searchWidth - Ui(88)));
                    if (ImGui.InputTextWithHint("##youtubeHubQuery", "Search YouTube or paste a video link…",
                        ref youtubeBrowseQuery, 200, ImGuiInputTextFlags.EnterReturnsTrue)) StartYouTubeBrowseSearch();
                    var field = ImGui.GetItemRectMin();
                    UiFonts.DrawCenteredIcon(ImGui.GetWindowDrawList(), Ui(16),
                        field + new Vector2(Ui(17), ImGui.GetItemRectSize().Y / 2), ImGui.GetColorU32(MutedText), FontAwesomeIcon.Search.ToIconString());
                }
                ImGui.SameLine(0, Ui(6));
                var link = IsYouTubeBrowseVideoUrl(youtubeBrowseQuery);
                if (GameLayoutButton(string.Empty, link ? FontAwesomeIcon.Play : FontAwesomeIcon.Search, Ui(42), true, height: 40))
                    StartYouTubeBrowseSearch();
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip(link ? "Play this YouTube video" : "Search YouTube");
                DrawSearchQueueButton("youtube", youtubeBrowseQuery);
                if (stacked) ImGui.SetCursorPos(UiVec(16, 64));
                else ImGui.SameLine(0, Ui(12));
                DrawYouTubeBrowseAccount(accountWidth);

                ImGui.SetCursorPos(UiVec(16, stacked ? 114 : 64));
                var tabWidth = inner / YouTubeHubTabs.Length;
                var previous = browseVideoSectionTab;
                foreach (var tab in YouTubeHubTabs)
                {
                    if (tab != YouTubeHubTabs[0]) ImGui.SameLine(0, 0);
                    var position = ImGui.GetCursorScreenPos();
                    var selected = browseVideoSectionTab == tab.Tab;
                    if (ImGui.InvisibleButton("##youtubeHubTab" + tab.Tab, new Vector2(tabWidth, Ui(44)))) browseVideoSectionTab = tab.Tab;
                    var draw = ImGui.GetWindowDrawList();
                    if (ImGui.IsItemHovered())
                    {
                        draw.AddRectFilled(position, position + new Vector2(tabWidth, Ui(42)), ImGui.GetColorU32(AccentFieldBg), Ui(5));
                    }
                    using (UiFonts.PushRegularRelative(1f))
                    {
                        var showIcon = tabWidth >= Ui(102);
                        var label = TruncateToWidth(tab.Label, MathF.Max(Ui(20), tabWidth - Ui(showIcon ? 34 : 12)));
                        var textSize = ImGui.CalcTextSize(label);
                        var x = position.X + (tabWidth - textSize.X - Ui(showIcon ? 25 : 0)) / 2;
                        var ink = ImGui.GetColorU32(selected ? AccentHover : MutedText);
                        if (showIcon) UiFonts.DrawCenteredIcon(draw, Ui(19), new Vector2(x + Ui(9), position.Y + Ui(20)), ink, tab.Icon.ToIconString());
                        draw.AddText(new Vector2(x + Ui(showIcon ? 25 : 0), position.Y + Ui(20) - textSize.Y / 2), ink, label);
                    }
                    if (selected) panelDraw.AddRectFilled(new Vector2(position.X, origin.Y + height - Ui(3)),
                        new Vector2(position.X + tabWidth, origin.Y + height), ImGui.GetColorU32(Accent), Ui(1));
                }
                if (previous != browseVideoSectionTab)
                {
                    youtubeBrowseShowingSearch = false;
                    SeedYouTubeMetadataCache();
                    if (browseVideoSectionTab == 4) RefreshPopularList();
                }
            }
        }
        SetGameLayoutRowGap(Ui(12));
    }

    private void DrawYouTubeHubFeedHeading(string title, int count)
    {
        GetYouTubeHubToolbarWidths(out var sortWidth, out var filterWidth);
        var available = ImGui.GetWindowContentRegionMax().X - ImGui.GetCursorPosX();
        var titleWidth = MathF.Max(Ui(70), available - sortWidth - filterWidth - Ui(filterWidth > 0 ? 88 : 80));
        var suffix = $" ({count} {(count == 1 ? "video" : "videos")})";
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Ui(5));
        using (UiFonts.PushRelative(1.08f, emphasis: true))
        {
            var label = TruncateToWidth(title, MathF.Max(Ui(20), titleWidth - ImGui.CalcTextSize(suffix).X)) + suffix;
            ImGui.TextColored(Hex(0xE2E7EF), label);
            if (label != title + suffix && ImGui.IsItemHovered()) UiFonts.SetTooltip(title + suffix);
        }
        DrawYouTubeBrowseInlineStatus();
    }

    private void GetYouTubeHubToolbarWidths(out float sortWidth, out float filterWidth)
    {
        var wide = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X >= Ui(600);
        sortWidth = Ui(wide ? 170 : 150);
        filterWidth = browseVideoSectionTab is 0 or 1 ? Ui(wide ? 150 : 120) : 0;
    }

    private void DrawYouTubeHubResultsSort()
    {
        var right = ImGui.GetWindowContentRegionMax().X;
        GetYouTubeHubToolbarWidths(out var sortWidth, out var filterWidth);
        var sortX = right - sortWidth - Ui(40);
        var headingTop = ImGui.GetItemRectMin().Y - ImGui.GetWindowPos().Y;
        var controlY = headingTop - Ui(5);
        if (youtubeHubCacheUpdating)
        {
            var statusX = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + Ui(8);
            ImGui.SetCursorPos(new Vector2(statusX, headingTop));
            using (UiFonts.PushIcon()) ImGui.TextColored(AccentHover, FontAwesomeIcon.InfoCircle.ToIconString());
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Updating video cache...");
        }
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(10, 5))
            .Push(ImGuiStyleVar.FrameRounding, Ui(6)).Push(ImGuiStyleVar.FrameBorderSize, Ui(1));
        using var colors = ImRaii.PushColor(ImGuiCol.FrameBg, HubSurface(0x0D1729))
            .Push(ImGuiCol.Button, HubSurface(0x14243C)).Push(ImGuiCol.Border, HubTint(0x24456B));
        if (browseVideoSectionTab is 0 or 1)
        {
            ImGui.SetCursorPos(new Vector2(sortX - filterWidth - Ui(8), controlY));
            if (browseVideoSectionTab == 0) DrawBrowseTopicSelector(filterWidth);
            else DrawYouTubeBrowseSubscriptionSelector(filterWidth);
        }
        ImGui.SetCursorPos(new Vector2(sortX, controlY));
        var labels = browseVideoSectionTab switch
        {
            4 => new[] { "Popularity" },
            3 => new[] { "Recommended", "Newest", "Most Viewed" },
            2 => new[] { "Trending", "Newest", "Most Viewed", "Recently Liked" },
            _ => new[] { "Trending", "Newest", "Most Viewed" }
        };
        var current = browseVideoSectionTab switch
        {
            0 => browseVideoSortMode, 1 => subscriptionVideoSortMode,
            2 => favouriteVideoSortMode, 3 => recommendedVideoSortMode, _ => 0
        };
        current = Math.Clamp(current, 0, labels.Length - 1);
        ImGui.SetNextItemWidth(sortWidth);
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##youtubeHubSort", "Sort: " + labels[current])))
        {
            for (var i = 0; i < labels.Length; i++)
                if (ImGui.Selectable(labels[i], current == i))
                    switch (browseVideoSectionTab)
                    {
                        case 0: browseVideoSortMode = i; break;
                        case 1: subscriptionVideoSortMode = i; break;
                        case 2: favouriteVideoSortMode = i; break;
                        case 3: recommendedVideoSortMode = i; break;
                    }
            ImGui.EndCombo();
        }
    }
}

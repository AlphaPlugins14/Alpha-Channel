using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private int welcomeTopicCategory;
    private sealed record WelcomeTopic(string Label, Func<Configuration, bool> Selected, Action<Configuration, bool> SetSelected);
    private static readonly string[] WelcomeTopicCategories = ["Entertainment", "World & Knowledge", "Lifestyle & Creative"];
    private static readonly WelcomeTopic[][] WelcomeTopics =
    [
        [
            new("Gaming", static c => c.TrendingGaming, static (c, value) => c.TrendingGaming = value),
            new("MMORPG", static c => c.TrendingMMORPG, static (c, value) => c.TrendingMMORPG = value),
            new("Final Fantasy", static c => c.TrendingFinalFantasy, static (c, value) => c.TrendingFinalFantasy = value),
            new("FFXIV", static c => c.TrendingFfxiv, static (c, value) => c.TrendingFfxiv = value),
            new("Anime", static c => c.TrendingAnime, static (c, value) => c.TrendingAnime = value),
            new("Movies", static c => c.TrendingMovies, static (c, value) => c.TrendingMovies = value),
            new("TV Shows", static c => c.TrendingTvShows, static (c, value) => c.TrendingTvShows = value),
            new("Music", static c => c.TrendingMusic, static (c, value) => c.TrendingMusic = value),
            new("Memes", static c => c.TrendingMemes, static (c, value) => c.TrendingMemes = value),
            new("Cartoons", static c => c.TrendingCartoons, static (c, value) => c.TrendingCartoons = value),
            new("Horror", static c => c.TrendingHorror, static (c, value) => c.TrendingHorror = value),
            new("Sci-Fi", static c => c.TrendingSciFi, static (c, value) => c.TrendingSciFi = value),
            new("Comedy", static c => c.TrendingComedy, static (c, value) => c.TrendingComedy = value),
            new("Minecraft", static c => c.TrendingMinecraft, static (c, value) => c.TrendingMinecraft = value),
            new("Disney", static c => c.TrendingDisney, static (c, value) => c.TrendingDisney = value),
            new("Fantasy", static c => c.TrendingFantasy, static (c, value) => c.TrendingFantasy = value),
        ],
        [
            new("Wildlife", static c => c.TrendingWildlife, static (c, value) => c.TrendingWildlife = value),
            new("Architecture", static c => c.TrendingArchitecture, static (c, value) => c.TrendingArchitecture = value),
            new("Science", static c => c.TrendingScience, static (c, value) => c.TrendingScience = value),
            new("Space", static c => c.TrendingSpace, static (c, value) => c.TrendingSpace = value),
            new("History", static c => c.TrendingHistory, static (c, value) => c.TrendingHistory = value),
            new("Technology", static c => c.TrendingTechnology, static (c, value) => c.TrendingTechnology = value),
            new("Urban Exploration", static c => c.TrendingUrbanExploration, static (c, value) => c.TrendingUrbanExploration = value),
        ],
        [
            new("Pets", static c => c.TrendingPets, static (c, value) => c.TrendingPets = value),
            new("Food", static c => c.TrendingFood, static (c, value) => c.TrendingFood = value),
            new("Travel", static c => c.TrendingTravel, static (c, value) => c.TrendingTravel = value),
            new("Cars", static c => c.TrendingCars, static (c, value) => c.TrendingCars = value),
            new("Sports", static c => c.TrendingSports, static (c, value) => c.TrendingSports = value),
            new("Arts & Crafts", static c => c.TrendingArtsAndCrafts, static (c, value) => c.TrendingArtsAndCrafts = value),
            new("Cosplaying", static c => c.TrendingCosplaying, static (c, value) => c.TrendingCosplaying = value),
            new("DIY", static c => c.TrendingDiy, static (c, value) => c.TrendingDiy = value),
            new("Fashion", static c => c.TrendingFashion, static (c, value) => c.TrendingFashion = value),
        ],
    ];
    private void DrawWelcomeTopicPicker(Vector2 origin, Vector2 size, float scale)
    {
        var draw = ImGui.GetWindowDrawList();
        var cfg = Plugin.Cfg;
        Vector2 P(float x, float y) => origin + new Vector2(x, y) * scale;
        var width = size.X / scale;
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(Hex(0x0D1729)), 12 * scale);
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(Hex(0x2B527C)), 12 * scale);
        var tabWidth = (size.X - 36 * scale) / 3;
        for (var i = 0; i < WelcomeTopicCategories.Length; i++)
        {
            var tab = origin + new Vector2(18 * scale + i * tabWidth, 8 * scale);
            ImGui.SetCursorScreenPos(tab);
            if (ImGui.InvisibleButton("##welcomeTopicCategory" + i, new Vector2(tabWidth, 38 * scale))) welcomeTopicCategory = i;
            var active = welcomeTopicCategory == i;
            using var font = UiFonts.PushRelative(16 * scale / ImGui.GetFontSize(), emphasis: active);
            var text = WelcomeTopicCategories[i];
            var fontPixels = MathF.Min(ImGui.GetFontSize(), ImGui.GetFontSize() * (tabWidth - 12 * scale) / ImGui.CalcTextSize(text).X);
            using var fitted = UiFonts.PushRelative(fontPixels / ImGui.GetFontSize(), emphasis: active);
            var measured = ImGui.CalcTextSize(text);
            UiFonts.DrawText(draw, ImGui.GetFontSize(), tab + new Vector2((tabWidth - measured.X) / 2, (36 * scale - measured.Y) / 2),
                ImGui.GetColorU32(active ? AccentHover : MutedText), text);
            if (active) draw.AddRectFilled(tab + new Vector2(10, 36) * scale,
                tab + new Vector2(tabWidth - 10 * scale, 39 * scale), ImGui.GetColorU32(Accent), scale);
        }
        draw.AddLine(P(24, 48), P(width - 24, 48), ImGui.GetColorU32(BorderSubtle), scale);

        ImGui.SetCursorScreenPos(P(24, 58));
        var changed = false;
        using (UiFonts.PushRelative(16 * scale / ImGui.GetFontSize()))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(0, 2) * scale)
            .Push(ImGuiStyleVar.ItemSpacing, new Vector2(10, 8) * scale))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero))
        using (var child = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##welcomeTopicChips" + welcomeTopicCategory,
            new Vector2(size.X - 48 * scale, 120 * scale), false)))
        {
            if (child)
            {
                var available = ImGui.GetContentRegionAvail().X;
                var x = 0f; var y = 2 * scale;
                foreach (var topic in WelcomeTopics[welcomeTopicCategory])
                {
                    var selected = topic.Selected(cfg);
                    var limitReached = !selected && GetSubscribedTopicCount() >= 15;
                    var labelSize = ImGui.CalcTextSize(topic.Label);
                    var chipWidth = MathF.Min(available, MathF.Max(140 * scale, labelSize.X + 50 * scale));
                    if (x > 0 && x + chipWidth > available) { x = 0; y += 40 * scale; }
                    ImGui.SetCursorPos(new Vector2(x, y));
                    var position = ImGui.GetCursorScreenPos();
                    using (UiDisabled.Scope(limitReached))
                    using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 9 * scale).Push(ImGuiStyleVar.FrameBorderSize, scale))
                    using (ImRaii.PushColor(ImGuiCol.Button, selected ? Hex(0x246AC6) : Hex(0x0B1425))
                        .Push(ImGuiCol.ButtonHovered, selected ? Hex(0x347FD9) : CardBgHover)
                        .Push(ImGuiCol.ButtonActive, AccentActive).Push(ImGuiCol.Border, selected ? AccentHover : BorderSubtle))
                    {
                        if (ImGui.Button("##welcomeTopic_" + topic.Label, new Vector2(chipWidth, 32 * scale)))
                        {
                            topic.SetSelected(cfg, !selected);
                            selected = !selected;
                            topicSelectionLimitWarning = false;
                            changed = true;
                        }
                    }
                    if (limitReached && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        UiFonts.SetTooltip("You can select up to 15 topics. Remove one to choose another.");
                        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) topicSelectionLimitWarning = true;
                    }
                    var ink = ImGui.GetColorU32(limitReached ? new Vector4(MutedText.X, MutedText.Y, MutedText.Z, .45f) : Vector4.One);
                    if (selected) UiFonts.DrawCenteredIcon(ImGui.GetWindowDrawList(), 13 * scale,
                        position + new Vector2(20, 16) * scale, ink, FontAwesomeIcon.Check.ToIconString());
                    var textX = selected ? MathF.Max(34 * scale, (chipWidth - labelSize.X) / 2 + 8 * scale) : (chipWidth - labelSize.X) / 2;
                    ImGui.GetWindowDrawList().PushClipRect(position, position + new Vector2(chipWidth, 32 * scale), true);
                    UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), position + new Vector2(textX, (32 * scale - labelSize.Y) / 2), ink, topic.Label);
                    ImGui.GetWindowDrawList().PopClipRect();
                    x += chipWidth + 10 * scale;
                }
            }
        }
        if (changed) cfg.Save();
        draw.AddLine(P(24, 188), P(width - 24, 188), ImGui.GetColorU32(BorderSubtle), scale);
        var count = GetSubscribedTopicCount();
        var valid = count is >= 3 and <= 15;
        var status = count < 3 ? $"Choose {3 - count} more {(3 - count == 1 ? "topic" : "topics")}" : $"{count} topics selected";
        var statusInk = valid ? AccentHover : MutedText;
        UiFonts.DrawCenteredIcon(draw, 18 * scale, P(33, 210), ImGui.GetColorU32(statusInk),
            (valid ? FontAwesomeIcon.CheckCircle : FontAwesomeIcon.InfoCircle).ToIconString());
        using (UiFonts.PushRelative(15 * scale / ImGui.GetFontSize()))
        {
            UiFonts.DrawText(draw, ImGui.GetFontSize(), P(50, 201), ImGui.GetColorU32(statusInk), status);
            if (topicSelectionLimitWarning || count >= 15)
            {
                const string limit = "Up to 15 topics";
                UiFonts.DrawText(draw, ImGui.GetFontSize(), P(width - 24, 201) - new Vector2(ImGui.CalcTextSize(limit).X, 0),
                    ImGui.GetColorU32(MutedText), limit);
            }
        }
        ImGui.SetCursorScreenPos(P(24, 228));
        using (UiFonts.PushRelative(18 * scale / ImGui.GetFontSize()))
        using (UiDisabled.Scope(!valid || firstLaunchSetupSubmitted || guestRegistrationBusy))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 9 * scale))
        using (ImRaii.PushColor(ImGuiCol.Button, Hex(0x347FF1)).Push(ImGuiCol.ButtonHovered, Hex(0x5095FF)))
            if (ImGui.Button((guestRegistrationBusy ? "Creating account..." :
                    !firstLaunchUsingAccount && CurrentSession is null ? "Create account" : "Continue") + "##welcomeTopicsContinue",
                    new Vector2(size.X - 48 * scale, 48 * scale))) SubmitFirstLaunchSetup();
        const string footer = "You can change these later in Settings.";
        using (UiFonts.PushRelative(14 * scale / ImGui.GetFontSize()))
            UiFonts.DrawText(draw, ImGui.GetFontSize(), P(width / 2, 288) - new Vector2(ImGui.CalcTextSize(footer).X / 2, 0),
                ImGui.GetColorU32(MutedText), footer);
    }
}

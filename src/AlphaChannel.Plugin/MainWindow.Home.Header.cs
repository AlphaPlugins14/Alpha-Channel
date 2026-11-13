using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private float homeSearchInputHeight;
    private readonly record struct HomeHeaderLayout(Vector2 Start, float Width, float WelcomeY,
        float WelcomeWidth, float SearchY, float SearchWidth, float Height);

    private HomeHeaderLayout CalculateHomeHeaderLayout(Vector2 start, float width)
    {
        var welcomeWidth = MathF.Max(Ui(90f), HeaderTextRight - start.X);
        var welcomeY = sharedHeaderOrigin.Y + Ui(12f) - ImGui.GetWindowPos().Y;
        var searchY = sharedHeaderOrigin.Y + Ui(sharedHeaderLayout.SearchY) - ImGui.GetWindowPos().Y;
        var searchRight = sharedHeaderOrigin.X + Ui(sharedHeaderLayout.SearchRight(HeaderHasActionBar, HeaderActivityWidth / Ui(1))) - ImGui.GetWindowPos().X;
        var searchWidth = MathF.Max(Ui(90f), searchRight - start.X);
        return new(start, width, welcomeY, welcomeWidth, searchY, searchWidth,
            PageHeaderHeight);
    }

    private void DrawHomeHeader(HomeHeaderLayout layout)
    {
        DrawHeaderSurface(layout.Start.X, layout.Start.Y, layout.Width, layout.Height);
        ImGui.SetCursorPos(new Vector2(layout.Start.X, layout.WelcomeY));
        var titleOrigin = ImGui.GetCursorScreenPos();
        const float textX = 0;
        float titleHeight;
        using (UiFonts.PushRelative(1.35f, emphasis: true))
        {
            const string welcome = "Welcome to ";
            const string brand = "Alpha Channel";
            var text = ImGui.CalcTextSize(welcome + brand);
            var fit = MathF.Min(1f, MathF.Max(1f, layout.WelcomeWidth - textX) / MathF.Max(1f, text.X));
            var pixels = ImGui.GetFontSize() * fit;
            titleHeight = text.Y * fit;
            var titleDraw = ImGui.GetWindowDrawList();
            UiFonts.DrawFittedText(titleDraw, pixels, titleOrigin + new Vector2(textX, 0), ImGui.GetColorU32(Hex(0xD5DCE8)), welcome);
            UiFonts.DrawFittedText(titleDraw, pixels, titleOrigin + new Vector2(textX + ImGui.CalcTextSize(welcome).X * fit, 0),
                ImGui.GetColorU32(Accent), brand);
        }
        ImGui.Dummy(new Vector2(layout.WelcomeWidth, titleHeight));

        // Match the icon's visible left edge, accounting for its font bearing.
        ImGui.SetCursorPos(new Vector2(layout.Start.X - Ui(2f), layout.SearchY));
        ImGui.SetNextItemWidth(layout.SearchWidth + Ui(2f));
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(18f))
                   .Push(ImGuiStyleVar.FramePadding, UiVec(38f, 10f)))
        {
            homeSearchInputPos = ImGui.GetCursorScreenPos();
            if (ImGui.InputTextWithHint($"##homeSearch_{homeSearchInputVersion}",
                    layout.SearchWidth < Ui(360f) ? "Search or paste a link..." : "Search videos, channels, or paste a link...", ref homeSearch, 256,
                    ImGuiInputTextFlags.EnterReturnsTrue))
                SubmitHomeSearch(homeSearch);
            homeSearchInputHeight = ImGui.GetItemRectSize().Y;
            if (ImGui.IsItemActive() && !string.IsNullOrWhiteSpace(homeSearch))
                homeSearchPopupOpen = true;
        }
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(homeSearchInputPos + UiVec(2f, 2f),
            homeSearchInputPos + new Vector2(Ui(36f), homeSearchInputHeight - Ui(2f)),
            ImGui.GetColorU32(FrameBg), Ui(18f), ImDrawFlags.RoundCornersLeft);
        var searchIcon = FontAwesomeIcon.Search.ToIconString();
        var iconSize = MeasureSidebarIcon(searchIcon);
        UiFonts.DrawIcon(draw, ImGui.GetFontSize(), homeSearchInputPos + new Vector2(Ui(13f), (homeSearchInputHeight - iconSize.Y) * .5f), ImGui.GetColorU32(Accent), searchIcon);

        DrawPageHeaderContentGap(layout.Start);
    }
}

using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private float PageHeaderHeight => Ui(sharedHeaderLayout.Height);

    private void DrawPageHeaderContentGap(Vector2 headerStart)
    {
        ImGui.SetCursorPos(headerStart + new Vector2(0f, PageHeaderHeight));
        // Match the standard item spacing above and below Home's featured card.
        ImGui.Dummy(Vector2.Zero);
    }

    private void DrawHeaderSurface(float x, float y, float width, float height)
    {
        var origin = ImGui.GetWindowPos() + new Vector2(x, y) - new Vector2(0f, ImGui.GetScrollY());
        var draw = ImGui.GetWindowDrawList();
        var windowMin = ImGui.GetWindowPos();
        var windowMax = windowMin + ImGui.GetWindowSize();
        var bottom = origin.Y + height;
        if (bottom <= windowMin.Y) return;
        // The page's padding is for its contents, not the header surface.
        // Include both padded gutters (and the scrollbar gutter) in the frame.
        draw.PushClipRect(windowMin, windowMax, false);
        draw.AddRectFilled(windowMin, new Vector2(windowMax.X, bottom),
            ImGui.GetColorU32(SidebarBg));
        UiDecorationRenderer.Draw(draw,windowMin,new Vector2(windowMax.X-windowMin.X,MathF.Min(bottom,windowMax.Y)-windowMin.Y),
            Plugin.Cfg.UiDecorationTheme,Colors,Ui(1f),UiDecorationRenderer.Region.Header);
        draw.AddLine(new Vector2(windowMin.X, bottom), new Vector2(windowMax.X, bottom),
            ImGui.GetColorU32(BorderSubtle), Ui(1f));
        draw.PopClipRect();
    }

    private string HeaderPartyChipLabel
    {
        get
        {
            var hostId = stream.Mode == StreamMode.Hosting ? stream.LocalUserId : stream.HostId;
            // The relay roster contains viewers. Include the host once, even
            // when an older server includes them in its participant list.
            var count = stream.Roster.Count(participant => participant.UserId != hostId) + 1;
            return stream.Mode == StreamMode.Hosting
                ? $"Hosting ({count})"
                : $"Watching ({count})";
        }
    }

    private string HeaderPartyExitLabel => stream.Mode == StreamMode.Hosting ? "End Party" : "Leave Party";

    private float HeaderPartyChipWidth => HeaderBarSegmentWidth(HeaderPartyChipLabel);

    private void DrawPolishedPageHeader(string text, string purpose)
    {
        using var eventFontScope=currentPage==HomePage.Events?BeginUiFonts():null;
        var start = ImGui.GetCursorPos();
        var width = ImGui.GetContentRegionAvail().X;
        var titleY = sharedHeaderOrigin.Y + Ui(12f) - ImGui.GetWindowPos().Y;
        DrawHeaderSurface(start.X, start.Y, width, PageHeaderHeight);
        var titleRight = HeaderTextRight;
        var titleLeft = start.X + Ui(42f);
        // Reserve the same right-hand block on every page, whether or not
        // party and activity controls are currently visible.
        ImGui.SetCursorPos(new Vector2(start.X, titleY));
        DrawPageBackButton();
        ImGui.SetCursorPos(new Vector2(titleLeft, titleY));
        using(PushUiFont(1.45f,true))
        {
            var displayed = TruncateToWidth(text, MathF.Max(Ui(30f), titleRight - titleLeft));
            ImGui.TextColored(currentPage is HomePage.Player or HomePage.Music or HomePage.VideoGrid or HomePage.InternetArchive or HomePage.PlaySnes or HomePage.Browser or HomePage.WatchAlong or HomePage.Arcade or HomePage.Whiteboard or HomePage.Pictionary or HomePage.PictionaryTeams or HomePage.Quiz or HomePage.Roulette or HomePage.RacePreview or HomePage.Bingo or HomePage.SkyRail or HomePage.Pixels or HomePage.LuckyWheel or HomePage.SlotMachine or HomePage.Poker or HomePage.Friends or HomePage.Profile or HomePage.Chats or HomePage.Messages or HomePage.PartyDirectory or HomePage.Events or HomePage.AlphaChat or HomePage.Screen or HomePage.Settings or HomePage.Help ? Hex(0xD5DCE8) : Vector4.One, displayed);
            if (displayed != text && ImGui.IsItemHovered()) UiFonts.SetTooltip(text);
        }
        var titleBottom = ImGui.GetItemRectMax().Y - ImGui.GetWindowPos().Y;
        var purposeY = sharedHeaderLayout.Compact
            ? sharedHeaderOrigin.Y + Ui(126f) - ImGui.GetWindowPos().Y
            : titleBottom + Ui(6f);
        ImGui.SetCursorPos(new Vector2(titleLeft, purposeY));
        var purposeRight = sharedHeaderLayout.Compact ? start.X + width : HeaderHasActionBar
            ? MathF.Min(titleRight, sharedHeaderOrigin.X + Ui(HeaderActivityLeft - 12) - ImGui.GetWindowPos().X)
            : titleRight;
        if (sharedHeaderLayout.Compact)
        {
            var subtitle = FitPartyTextToWidth(purpose, MathF.Max(Ui(20f), purposeRight - titleLeft));
            ImGui.TextColored(MutedText, subtitle);
            if (subtitle != purpose && ImGui.IsItemHovered()) UiFonts.SetTooltip(purpose);
        }
        else
        {
            // Keep both lines in the title column, clear of the profile and activity bar.
            var draw = ImGui.GetWindowDrawList();
            var subtitleOrigin = ImGui.GetCursorScreenPos();
            var subtitleWidth = MathF.Max(Ui(20f), purposeRight - titleLeft);
            draw.PushClipRect(subtitleOrigin,
                subtitleOrigin + new Vector2(subtitleWidth, ImGui.GetTextLineHeight() * 2f), true);
            ImGui.PushTextWrapPos(titleLeft + subtitleWidth);
            ImGui.TextColored(MutedText, purpose);
            ImGui.PopTextWrapPos();
            draw.PopClipRect();
            if (ImGui.GetItemRectSize().Y > ImGui.GetTextLineHeight() * 2f && ImGui.IsItemHovered())
                UiFonts.SetTooltip(purpose);
        }
        DrawPageHeaderContentGap(start);
    }
}

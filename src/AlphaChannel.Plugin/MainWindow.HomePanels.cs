using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool continueWatchingOverlayOpen;

    private void ResumeHomeVideo(RecentlyWatchedVideoRecord watched)
    {
        if (video.IsPlayingLocalVideo) return;
        var entry = new VideoQueueEntry(watched.Url, watched.Title,
            watched.ChannelName, TimeSpan.FromSeconds(watched.DurationSeconds),
            watched.ThumbnailUrl, resumePositionSeconds: watched.WatchedSeconds);
        HandlePlayNow(entry);
        activePlayerDrawer = PlayerDrawer.Player;
        currentPage = HomePage.Player;
    }

    private void DrawContinueWatchingOverlay()
    {
        if (!continueWatchingOverlayOpen) return;

        var parentPosition = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var panelWidth = MathF.Min(Ui(760f), parentSize.X - Ui(24f));
        var panelHeight = MathF.Min(Ui(610f), parentSize.Y - Ui(24f));
        var panelPosition = parentPosition + new Vector2(
            (parentSize.X - panelWidth) * 0.5f,
            (parentSize.Y - panelHeight) * 0.5f);

        ImGui.SetNextWindowPos(parentPosition, ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        const ImGuiWindowFlags overlayFlags = ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##continueWatchingOverlay", overlayFlags))
        {
            ImGui.End();
            return;
        }

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(parentPosition, parentPosition + parentSize,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.54f)));
        ImGui.SetCursorScreenPos(panelPosition);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(18f, 16f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg,
                   CardBg)
            .Push(ImGuiCol.Border,
                new Vector4(Accent.X, Accent.Y, Accent.Z, 0.82f)))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##continueWatchingPanel",
                   new Vector2(panelWidth, panelHeight), true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (panel)
            {
                var panelDraw = ImGui.GetWindowDrawList();
                ImGui.SetCursorScreenPos(panelPosition + UiVec(22f, 18f));
                using var uiFontScale12807=UiFonts.PushRelative(1.16f);
                ImGui.TextColored(Vector4.One, "Continue Watching");
                uiFontScale12807.Dispose();
                ImGui.SetCursorScreenPos(panelPosition + UiVec(22f, 47f));
                ImGui.TextColored(MutedText, "Pick up where you left off.");

                var closeOrigin = panelPosition +
                    new Vector2(panelWidth - Ui(43f), Ui(19f));
                ImGui.SetCursorScreenPos(closeOrigin);
                if (ImGui.InvisibleButton("##closeContinueWatching", UiVec(24f, 24f)))
                    continueWatchingOverlayOpen = false;
                UiFonts.DrawIcon(panelDraw, ImGui.GetFontSize(), closeOrigin + UiVec(5f, 4f), ImGui.GetColorU32(ImGui.IsItemHovered() ? AccentHover : MutedText), FontAwesomeIcon.Times.ToIconString());

                panelDraw.AddLine(panelPosition + UiVec(18f, 78f),
                    panelPosition + new Vector2(panelWidth - Ui(18f), Ui(78f)),
                    ImGui.GetColorU32(BorderSubtle));

                ImGui.SetCursorScreenPos(panelPosition + UiVec(18f, 91f));
                using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
                using (var list = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##continueWatchingList",
                           new Vector2(panelWidth - Ui(36f), panelHeight - Ui(151f)), false)))
                {
                    if (list)
                    {
                        var entries = Plugin.Cfg.RecentlyWatchedVideos
                            .Where(IsResumableHistoryMedia).ToArray();
                        if (entries.Length == 0)
                        {
                            ImGui.SetCursorPos(UiVec(12f, 18f));
                            ImGui.TextColored(MutedText, "There are no videos to resume yet.");
                        }
                        else
                        {
                            for (var i = 0; i < entries.Length; i++)
                                DrawContinueWatchingEntry(entries[i], i);
                        }
                    }
                }

                ImGui.SetCursorScreenPos(panelPosition +
                    new Vector2(Ui(18f), panelHeight - Ui(47f)));
                if (ImGui.Button("Close##continueWatching", UiVec(112f, 34f)))
                    continueWatchingOverlayOpen = false;
            }
        }
        ImGui.End();
    }

    private void DrawContinueWatchingEntry(RecentlyWatchedVideoRecord watched, int index)
    {
        ImGui.PushID(index);
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var compact = width < Ui(520f);
        var height = Ui(compact ? 78f : 94f);
        if (ImGui.InvisibleButton("##resume", new Vector2(width, height)))
        {
            continueWatchingOverlayOpen = false;
            ResumeHomeVideo(watched);
        }
        var hovered = ImGui.IsItemHovered();
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + new Vector2(width, height),
            ImGui.GetColorU32(hovered ? CardBgHover : CardBg), Ui(8f));
        draw.AddRect(origin, origin + new Vector2(width, height),
            ImGui.GetColorU32(hovered ? AccentHover : BorderSubtle), Ui(8f));

        var thumbnailOrigin = origin + UiVec(8f, 8f);
        var thumbnailSize = new Vector2(Ui(compact ? 100f : 138f), height - Ui(16f));
        var thumbnail = thumbnails.Get(watched.ThumbnailUrl);
        if (thumbnail is not null)
        {
            var (uv0, uv1) = CoverUvs(thumbnail.Width, thumbnail.Height,
                thumbnailSize.X, thumbnailSize.Y);
            draw.AddImageRounded(thumbnail.Handle, thumbnailOrigin,
                thumbnailOrigin + thumbnailSize, uv0, uv1,
                ImGui.GetColorU32(Vector4.One), Ui(6f));
        }
        else
        {
            draw.AddRectFilled(thumbnailOrigin, thumbnailOrigin + thumbnailSize,
                ImGui.GetColorU32(SidebarBg), Ui(6f));
            UiFonts.DrawIcon(draw, ImGui.GetFontSize() * 1.5f, thumbnailOrigin + UiVec(35f, 20f), ImGui.GetColorU32(AccentHover), FontAwesomeIcon.Play.ToIconString());
        }

        var progress = watched.DurationSeconds > 0d
            ? Math.Clamp(watched.WatchedSeconds / watched.DurationSeconds, 0d, 1d)
            : 0d;
        var trackY = thumbnailOrigin.Y + thumbnailSize.Y - Ui(4f);
        draw.AddRectFilled(new Vector2(thumbnailOrigin.X, trackY),
            thumbnailOrigin + thumbnailSize, ImGui.GetColorU32(BorderSubtle));
        draw.AddRectFilled(new Vector2(thumbnailOrigin.X, trackY),
            new Vector2(thumbnailOrigin.X + thumbnailSize.X * (float)progress,
                thumbnailOrigin.Y + thumbnailSize.Y), ImGui.GetColorU32(AccentHover));

        var textX = thumbnailOrigin.X + thumbnailSize.X + Ui(14f);
        var actionWidth = Ui(compact ? 52f : 84f);
        var textWidth = MathF.Max(Ui(40f), origin.X + width - actionWidth - Ui(23f) - textX);
        draw.AddText(new Vector2(textX, origin.Y + Ui(13f)), ImGui.GetColorU32(Vector4.One),
            ArcadeFitText(watched.Title, textWidth));
        draw.AddText(new Vector2(textX, origin.Y + Ui(36f)), ImGui.GetColorU32(MutedText),
            ArcadeFitText(watched.ChannelName, textWidth));
        draw.AddText(new Vector2(textX, origin.Y + Ui(compact ? 57f : 66f)),
            ImGui.GetColorU32(AccentHover), $"{progress:P0} watched");
        draw.AddText(new Vector2(origin.X + width - actionWidth - Ui(9f),
                origin.Y + Ui(compact ? 30f : 38f)),
            ImGui.GetColorU32(hovered ? Vector4.One : AccentHover),
            compact ? ">" : "Resume  >");

        ImGui.SetCursorScreenPos(origin + new Vector2(0f, height + Ui(8f)));
        ImGui.PopID();
    }
}

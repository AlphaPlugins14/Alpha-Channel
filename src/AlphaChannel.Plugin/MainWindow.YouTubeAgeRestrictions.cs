using System.Collections.Concurrent;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly ConcurrentDictionary<string, byte> pendingYouTubeAgeWarnings = new(StringComparer.Ordinal);
    private readonly YouTubeAgeRestrictionWarningScope youtubeAgeWarningScope = new();
    private string? youtubeAgeWarningTitle;
    private int youtubeConnectionScrollFrames;

    internal void InitializeYouTubeAgeRestrictions()
    {
        Plugin.Cfg.YouTubeAgeRestrictedVideos ??= new(StringComparer.Ordinal);
        screenController.Engine.YouTubeAgeRestrictions.Restore(Plugin.Cfg.YouTubeAgeRestrictedVideos.Keys);
        screenController.Engine.YouTubeConnectionUsable = () => Plugin.Cfg.YouTubeEmbeddedBrowserSessionEnabled &&
            !Plugin.Cfg.YouTubeSessionNeedsReconnect &&
            YouTubeEmbeddedBrowserSession.HasUnexpiredAuthenticationCookies(video.CookiesPath);
    }

    // Native playback callbacks may run off-thread. UI and configuration stay on the framework thread.
    internal void QueueYouTubeAgeWarning(string url) => pendingYouTubeAgeWarnings.TryAdd(url, 0);

    internal void ObserveYouTubeAgePlayback(StreamControl state)
    {
        var id = YouTubeAgeRestrictionCache.VideoId(state.Url);
        youtubeAgeWarningScope.Observe(stream.Mode == StreamMode.Viewing && id is not null
            ? stream.HostId + "|" + stream.HostingSessionId + "|" + id : null, state.PositionSeconds);
    }

    internal bool BlockAgeRestrictedRemotePlayback(string url)
    {
        if (!screenController.Engine.TryBlockAgeRestrictedYouTube(url)) return false;
        if (video.CurrentMediaUrl is { } current && YouTubeAgeRestrictionCache.VideoId(current) != YouTubeAgeRestrictionCache.VideoId(url))
            video.CompletePlayback(); // Do not leave the previous host video playing under the blocked item.
        video.GetProgress(); // Consume the terminal failure and release its decoder, even while sync is blocked.
        return true;
    }

    internal void TickYouTubeAgeRestrictions()
    {
        if (Volatile.Read(ref disposeStarted) != 0) return;
        if (stream.Mode == StreamMode.Viewing && stream.CurrentRoomState is { } remote) ObserveYouTubeAgePlayback(remote);
        else youtubeAgeWarningScope.Observe(null);
        var save = false;
        foreach (var url in pendingYouTubeAgeWarnings.Keys)
        {
            if (!pendingYouTubeAgeWarnings.TryRemove(url, out _) || YouTubeAgeRestrictionCache.VideoId(url) is not { } id) continue;
            if (YouTubeAgeRestrictionCache.VideoId(video.CurrentMediaUrl) == id) video.GetProgress();
            var title = ResolveYouTubeAgeWarningTitle(id);
            if (!Plugin.Cfg.YouTubeAgeRestrictedVideos.TryGetValue(id, out var oldTitle) || title != oldTitle)
            { Plugin.Cfg.YouTubeAgeRestrictedVideos[id] = title; save = true; }
            if (screenController.Engine.YouTubeConnectionUsable?.Invoke() == true) continue;
            // Ignore a stale callback's popup if the party has already moved on.
            if (stream.Mode == StreamMode.Viewing && YouTubeAgeRestrictionCache.VideoId(stream.CurrentRoomState?.Url) != id) continue;
            if (!youtubeAgeWarningScope.ShouldShow(stream.Mode == StreamMode.Viewing)) continue;
            youtubeAgeWarningTitle = title;
            // Present the overlay in place, preserving the selected page and its tabs.
            // An already visible window needs no open/focus action for this notice.
            if (!IsOpen || windowMinimized)
            {
                SetMinimized(false);
                IsOpen = true;
            }
        }
        if (save) Plugin.Cfg.SavePublicStateDeferred();
    }

    private string ResolveYouTubeAgeWarningTitle(string id)
    {
        string? title = stream.Mode == StreamMode.Viewing && YouTubeAgeRestrictionCache.VideoId(stream.CurrentRoomState?.Url) == id
            ? stream.CurrentRoomState?.MediaTitle : null;
        if (!YouTubeMetadataCache.HasTitle(title) && YouTubeAgeRestrictionCache.VideoId(queue.Current?.Url) == id) title = queue.Current?.Title;
        if (!YouTubeMetadataCache.HasTitle(title) && YouTubeAgeRestrictionCache.VideoId(video.LastDisplayMediaUrl) == id) title = video.LastDisplayMediaTitle;
        if (!YouTubeMetadataCache.HasTitle(title) && YouTubeMetadataCache.Shared.TryGet(id, out var cached)) title = cached?.Title;
        if (!YouTubeMetadataCache.HasTitle(title)) Plugin.Cfg.YouTubeAgeRestrictedVideos.TryGetValue(id, out title);
        return YouTubeMetadataCache.HasTitle(title) ? new string(title!.Where(c => !char.IsControl(c)).Take(500).ToArray()) : "YouTube video (" + id + ")";
    }

    private void OpenYouTubeConnectionFromAgeWarning()
    {
        youtubeAgeWarningTitle = null;
        currentPage = HomePage.Settings;
        settingsTab = SettingsTab.YouTube;
        youtubeConnectionScrollFrames = 3;
    }

    private void DrawYouTubeAgeRestrictionWarning()
    {
        if (youtubeAgeWarningTitle is not { } title) return;
        var parent = ImGui.GetWindowPos(); var parentSize = ImGui.GetWindowSize();
        var width = MathF.Min(Ui(540), MathF.Max(Ui(1), parentSize.X - Ui(32)));
        var titleHeight = ImGui.CalcTextSize("Video title: " + title, false, MathF.Max(Ui(1), width - Ui(48))).Y;
        var size = new Vector2(width, MathF.Min(Ui(265) + titleHeight, MathF.Max(Ui(1), parentSize.Y - Ui(32))));
        var warning = new Vector4(1f, .69f, .24f, 1f);
        ImGui.SetNextWindowPos(parent); ImGui.SetNextWindowSize(parentSize); ImGui.SetNextWindowBgAlpha(0);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##youtubeAgeOverlay", flags)) { ImGui.End(); return; }
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(parent, parent + parentSize, ImGui.GetColorU32(new Vector4(0, 0, 0, .67f)));
        var origin = parent + (parentSize - size) * .5f;
        draw.AddRect(origin - UiVec(3, 3), origin + size + UiVec(3, 3), ImGui.GetColorU32(warning with { W = .35f }), Ui(16), ImDrawFlags.None, Ui(2));
        ImGui.SetCursorScreenPos(origin);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14)).Push(ImGuiStyleVar.WindowPadding, UiVec(24, 20))
            .Push(ImGuiStyleVar.ItemSpacing, UiVec(8, 12)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(.075f, .045f, .025f, 1)).Push(ImGuiCol.Border, warning))
        using (var card = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##youtubeAgeCard", size, true)))
        {
            if (card)
            {
                using (UiFonts.PushRelative(1.35f, emphasis: true)) ImGui.TextColored(warning, "Video blocked by YouTube");
                ImGui.Separator();
                ImGui.TextWrapped("Video title: " + title);
                ImGui.TextColored(warning, "Reason: Age restricted video");
                ImGui.TextWrapped("You can connect your YouTube account to Alpha Channel");
                var linkOrigin = ImGui.GetCursorScreenPos();
                var linkSize = ImGui.CalcTextSize("here");
                if (ImGui.InvisibleButton("##youtubeAgeConnection", linkSize)) OpenYouTubeConnectionFromAgeWarning();
                var linkColour = ImGui.GetColorU32(ImGui.IsItemHovered() ? AccentHover : Accent);
                var linkDraw = ImGui.GetWindowDrawList();
                linkDraw.AddText(linkOrigin, linkColour, "here");
                linkDraw.AddLine(linkOrigin + new Vector2(0, linkSize.Y), linkOrigin + linkSize, linkColour);
                if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SameLine(0, Ui(4));
                ImGui.TextWrapped("to prevent this issue in future");
                if (ActivePartyButton("Dismiss##youtubeAge", new(-1, Ui(36)))) youtubeAgeWarningTitle = null;
            }
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) youtubeAgeWarningTitle = null;
        ImGui.End();
    }
}

using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.YouTube;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// When clicking any youtube channel name, shows latest videos from that channel.
internal sealed partial class MainWindow
{
    private void DrawYouTubeChannelPage()
    {
        if (ImGui.Button("Back to videos##youtubeChannelBack"))
        {
            CloseYouTubeChannel();
            return;
        }
        ImGui.Dummy(UiVec(0f, 10f));

        using (UiFonts.PushRelative(1f, emphasis: true))
        using (ImRaii.TextWrapPos(0f))
            ImGui.TextUnformatted(viewedChannelName ?? "Channel");
        ImGui.TextColored(MutedText, viewedChannelVideos is { Count: > 0 } uploads
            ? $"Latest uploads ({uploads.Count} videos)"
            : "Latest uploads");
        ImGui.Dummy(UiVec(0f, 6f));

        if (isLoadingViewedChannelVideos && viewedChannelVideos is null)
        {
            ImGui.TextColored(MutedText, "Loading channel videos...");
            return;
        }
        if (viewedChannelVideos is not { Count: > 0 } videos)
        {
            ImGui.TextColored(MutedText, "No videos found.");
            return;
        }

        using var content = UiFonts.Window(ImGui.GetFontSize(),
            ImRaii.Child("##youtubeChannelVideos_" + viewedChannelId, new Vector2(0f, -1f), false));
        if (!content) return;

        var contentWidth = YouTubeCardGridWidth;
        var columns = BrowseVideoGridColumns(contentWidth);
        var gap = Ui(12f);
        var cardWidth = (contentWidth - gap * (columns - 1)) / columns;
        var cardHeight = BrowseVideoCardHeight;
        var origin = ImGui.GetCursorPos();

        for (var index = 0; index < videos.Count; index++)
        {
            if (index > 0)
            {
                if (index % columns == 0)
                    ImGui.SetCursorPos(origin + new Vector2(0f, (index / columns) * (cardHeight + gap)));
                else
                    ImGui.SameLine(0f, gap);
            }
            using var cardId = ImRaii.PushId($"channelVideo_{index}");
            DrawBrowseVideoCardSurface(videos[index], null, cardWidth, cardHeight,
                showFavouriteAction: true);
        }
    }
}

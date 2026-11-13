using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private VideoSearchEntry? youtubeCardMenuVideo;
    private Vector2 youtubeCardMenuAnchor;
    private bool openYouTubeCardMenu;
    private string? hiddenYouTubeUndoKey;
    private string? hiddenYouTubeUndoName;
    private double hiddenYouTubeUndoUntil;

    private bool DrawYouTubeCardFooter(VideoSearchEntry video, Vector2 origin,
        float width, float height)
    {
        return DrawBrowseVideoFooter(origin, width, height, "Play", FontAwesomeIcon.Play,
            () => HandlePlayNow(new VideoQueueEntry(video.Url, video.Title, video.ChannelName,
                video.Duration, video.ThumbnailUrl)), () =>
            {
                var anchor = ImGui.GetCursorScreenPos();
                var clicked = GameLayoutButton(string.Empty, FontAwesomeIcon.EllipsisH, Ui(40), height: 34);
                if (clicked) OpenYouTubeCardMenu(video, anchor);
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Video options");
                return clicked;
            });
    }

    private bool IsYouTubeChannelHidden(VideoSearchEntry video) =>
        Plugin.Cfg.HiddenYouTubeChannels.ContainsKey(GetYouTubeBrowseChannelKey(video));

    private int youtubeHiddenChannelsRevision;

    private void UpdateHiddenYouTubeChannelSelection()
    {
        youtubeHiddenChannelsRevision++;
        // Rebuild selections from existing caches without fetching, changing
        // the active sort, or choosing a different set of Home topics.
        youtubeBrowseRecommendedVideos = null;
        popularPageVideos = null;
        RebuildHomeYouTubeFromBrowseCache(chooseNewTopics: false);
        Plugin.Cfg.Save();
    }

    private void OpenYouTubeCardMenu(VideoSearchEntry video, Vector2 anchor)
    {
        youtubeCardMenuVideo = video;
        youtubeCardMenuAnchor = anchor;
        openYouTubeCardMenu = true;
    }

    private void AddVideoToQueueSlot(VideoSearchEntry video, int slot)
    {
        if (queueManager.Slots[slot] is null)
            queueManager.Create(slot, $"Queue {slot + 1}", "Tv");
        var entry = new VideoQueueEntry(video.Url, video.Title, video.ChannelName,
            video.Duration, video.ThumbnailUrl);
        // Adding to a saved slot never switches queues or requests host playback.
        if (slot == queueManager.ActiveSlotIndex)
            personalQueue.Add(entry);
        else if (queueManager.Slots[slot] is { } profile)
        {
            profile.Entries.Add(new VideoQueueRecord
            {
                Url = entry.Url, Title = entry.Title, Source = entry.Source,
                DurationSeconds = entry.Duration?.TotalSeconds, ThumbnailUrl = entry.ThumbnailUrl
            });
            Plugin.Cfg.Save();
        }
        queueAddedFeedbackUntil = ImGui.GetTime() + 2.0;
    }

    private bool IsVideoInQueueSlot(VideoSearchEntry video, int slot)
    {
        return slot == queueManager.ActiveSlotIndex
            ? personalQueue.Entries.Any(entry => MatchesQueueVideo(video, entry.Url))
            : queueManager.Slots[slot]?.Entries.Any(entry => MatchesQueueVideo(video, entry.Url)) == true;
    }

    private static bool MatchesQueueVideo(VideoSearchEntry video, string url)
    {
        var videoId = GetYouTubeVideoId(video.Url);
        return string.Equals(video.Url, url, StringComparison.Ordinal) ||
            (videoId is not null && string.Equals(videoId, GetYouTubeVideoId(url), StringComparison.Ordinal));
    }

    private void ToggleVideoInQueueSlot(VideoSearchEntry video, int slot)
    {
        if (!IsVideoInQueueSlot(video, slot))
        {
            AddVideoToQueueSlot(video, slot);
            return;
        }

        if (slot == queueManager.ActiveSlotIndex)
        {
            foreach (var entry in personalQueue.Entries.Where(entry => MatchesQueueVideo(video, entry.Url)).ToArray())
                personalQueue.Remove(entry);
        }
        else if (queueManager.Slots[slot] is { } profile)
        {
            profile.Entries.RemoveAll(entry => MatchesQueueVideo(video, entry.Url));
            Plugin.Cfg.Save();
        }
    }

    private void DrawYouTubeCardMenu()
    {
        var parentPos = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        if (openYouTubeCardMenu)
        {
            ImGui.OpenPopup("##youtubeCardMenu");
            openYouTubeCardMenu = false;
        }
        if (ImGui.IsPopupOpen("##youtubeCardMenu") && youtubeCardMenuVideo is { } video)
        {
            // Size from the same row metrics used below so every option fits,
            // even when the plugin's font/UI scale changes.
            var rowHeight = MathF.Max(Ui(24f), ImGui.GetTextLineHeight() + Ui(8f));
            var inParty = stream.Mode is StreamMode.Hosting or StreamMode.Viewing;
            var menuHeight = ImGui.GetTextLineHeight() + rowHeight * (QueueManager.StandardSlotCount + 3 + (inParty ? 1 : 0)) + Ui(51f);
            var menuSize = new Vector2(MathF.Min(Ui(220f), parentSize.X - Ui(16f)), menuHeight);
            var min = parentPos + UiVec(8f, 8f);
            var max = Vector2.Max(min, parentPos + parentSize - menuSize - UiVec(8f, 8f));
            var position = Vector2.Clamp(youtubeCardMenuAnchor - new Vector2(0f, menuSize.Y + Ui(5f)), min, max);
            ImGui.SetNextWindowPos(position);
            ImGui.SetNextWindowSize(menuSize);
            using var styles = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(10f, 10f))
                .Push(ImGuiStyleVar.ItemSpacing, UiVec(4f, 3f))
                .Push(ImGuiStyleVar.PopupBorderSize, Ui(1f))
                .Push(ImGuiStyleVar.PopupRounding, Ui(10f));
            using var colors = ImRaii.PushColor(ImGuiCol.PopupBg, CardBg)
                .Push(ImGuiCol.Border, new Vector4(AccentHover.X, AccentHover.Y, AccentHover.Z, 0.38f))
                .Push(ImGuiCol.Header, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.25f))
                .Push(ImGuiCol.HeaderHovered, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.4f));
            using var popup = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Popup("##youtubeCardMenu", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse));
            if (popup)
            {
                if (inParty && DrawYouTubeMenuRow("Add to Watch Party", FontAwesomeIcon.Users))
                {
                    AddPartySearchResult(video);
                    ImGui.CloseCurrentPopup();
                }
                ImGui.TextColored(MutedText, "ADD TO QUEUE");
                ImGui.Dummy(UiVec(0f, 2f));
                for (var slot = 0; slot < QueueManager.StandardSlotCount; slot++)
                {
                    var name = queueManager.Slots[slot]?.Name ?? $"Queue {slot + 1}";
                    if (DrawYouTubeMenuRow($"{name}##slot{slot}", FontAwesomeIcon.ListUl,
                            IsVideoInQueueSlot(video, slot)))
                    {
                        ToggleVideoInQueueSlot(video, slot);
                        ImGui.CloseCurrentPopup();
                    }
                }
                ImGui.Separator();
                using (UiDisabled.Scope(string.IsNullOrWhiteSpace(video.Url)))
                    if (DrawYouTubeMenuRow("Copy video url", FontAwesomeIcon.Copy))
                    {
                        ImGui.SetClipboardText(video.Url);
                        ImGui.CloseCurrentPopup();
                    }
                using (UiDisabled.Scope(string.IsNullOrWhiteSpace(video.ChannelId)))
                    if (DrawYouTubeMenuRow("View channel", FontAwesomeIcon.User))
                    {
                        OpenYouTubeChannel(video.ChannelId!, video.ChannelName);
                        ImGui.CloseCurrentPopup();
                    }
                var key = GetYouTubeBrowseChannelKey(video);
                var hidden = IsYouTubeChannelHidden(video);
                if (DrawYouTubeMenuRow(hidden ? "Unhide this channel" : "Hide this channel", FontAwesomeIcon.EyeSlash))
                {
                    if (hidden) Plugin.Cfg.HiddenYouTubeChannels.Remove(key);
                    else
                    {
                        Plugin.Cfg.HiddenYouTubeChannels[key] = video.ChannelName;
                        hiddenYouTubeUndoKey = key;
                        hiddenYouTubeUndoName = video.ChannelName;
                        hiddenYouTubeUndoUntil = ImGui.GetTime() + 6.0;
                    }
                    UpdateHiddenYouTubeChannelSelection();
                    ImGui.CloseCurrentPopup();
                }
            }
        }
        if (hiddenYouTubeUndoKey is not null && ImGui.GetTime() < hiddenYouTubeUndoUntil)
        {
            ImGui.SetNextWindowPos(parentPos + new Vector2(parentSize.X * 0.5f, Ui(100f)), ImGuiCond.Always, new Vector2(0.5f, 0f));
            using var colors = ImRaii.PushColor(ImGuiCol.WindowBg, CardBg);
            var toast = UiFonts.Window(ImGui.GetFontSize(), ImGui.Begin("##hiddenChannelUndo", ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing));
            try
            {
                if (!toast) return;
                ImGui.TextUnformatted("Channel hidden");
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip(hiddenYouTubeUndoName ?? string.Empty);
                ImGui.SameLine();
                if (ImGui.SmallButton("Undo"))
                {
                    Plugin.Cfg.HiddenYouTubeChannels.Remove(hiddenYouTubeUndoKey);
                    UpdateHiddenYouTubeChannelSelection();
                    hiddenYouTubeUndoKey = null;
                }
            }
            finally { ImGui.End(); }
        }
    }

    private bool DrawYouTubeMenuRow(string label, FontAwesomeIcon icon, bool selected = false)
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var rowHeight = MathF.Max(Ui(24f), ImGui.GetTextLineHeight() + Ui(8f));
        var clicked = ImGui.Selectable($"##{label}", selected, ImGuiSelectableFlags.None, new Vector2(width, rowHeight));
        var draw = ImGui.GetWindowDrawList();
        using (UiFonts.PushIcon())
        {
            var iconY = (rowHeight - ImGui.CalcTextSize(icon.ToIconString()).Y) * 0.5f;
            UiFonts.DrawText(draw, ImGui.GetFontSize(), origin + new Vector2(Ui(4f), iconY), ImGui.GetColorU32(MutedText), icon.ToIconString());
            if (selected)
                UiFonts.DrawText(draw, ImGui.GetFontSize(), origin + new Vector2(width - Ui(18f), iconY), ImGui.GetColorU32(AccentHover), FontAwesomeIcon.Check.ToIconString());
        }
        var text = label.Split("##", StringSplitOptions.None)[0];
        draw.AddText(origin + new Vector2(Ui(28f), (rowHeight - ImGui.GetTextLineHeight()) * 0.5f), ImGui.GetColorU32(Vector4.One),
            TruncateToWidth(text, width - Ui(50f)));
        return clicked;
    }

    private void DrawHiddenYouTubeChannelSettings()
    {
        var channels = Plugin.Cfg.HiddenYouTubeChannels.ToArray();
        var columns = ImGui.GetContentRegionAvail().X >= Ui(540f) ? 2 : 1;
        var visibleRows = Math.Clamp((channels.Length + columns - 1) / columns, 1, 4);
        var listHeight = 16f + visibleRows * 46f + (visibleRows - 1) * 10f;
        DrawOtherSettingsPanel("##hiddenYouTubeChannels", listHeight + 110f, () =>
        {
            SettingsSection("Hidden channels", "Hidden from Home trending, Topics, Recommendations, Popular and YouTube search results.");
            DrawSettingsHelper($"{channels.Length} hidden YouTube channels");
            ImGui.Dummy(UiVec(0f, 8f));
            if (channels.Length == 0)
            {
                ImGui.TextColored(MutedText, "No hidden channels.");
                return;
            }
            using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(8f, 8f));
            using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f));
            using var border = ImRaii.PushColor(ImGuiCol.Border, SettingsReviewBorder);
            using var list = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##hiddenChannelList", new Vector2(0f, Ui(listHeight)), true));
            if (!list) return;
            var start = ImGui.GetCursorScreenPos();
            var gap = Ui(10f);
            var width = (ImGui.GetContentRegionAvail().X - gap * (columns - 1)) / columns;
            for (var index = 0; index < channels.Length; index++)
            {
                var channel = channels[index];
                ImGui.SetCursorScreenPos(start + new Vector2(
                    (index % columns) * (width + gap), (index / columns) * (Ui(46f) + gap)));
                ImGui.PushID(channel.Key);
                if (DrawHiddenYouTubeChannelRow(channel.Value, width))
                {
                    Plugin.Cfg.HiddenYouTubeChannels.Remove(channel.Key);
                    UpdateHiddenYouTubeChannelSelection();
                }
                ImGui.PopID();
            }
        }, autoHeight: true);
    }

    private bool DrawHiddenYouTubeChannelRow(string name, float width)
    {
        var origin = ImGui.GetCursorScreenPos();
        var rowHeight = Ui(46f);
        var buttonSize = Ui(32f);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + new Vector2(width, rowHeight),
            ImGui.GetColorU32(CardBg), Ui(8f));
        draw.AddRect(origin, origin + new Vector2(width, rowHeight), ImGui.GetColorU32(BorderSubtle), Ui(8f));
        using (UiFonts.PushIcon())
            UiFonts.DrawText(draw, ImGui.GetFontSize(), origin + UiVec(12f, 13f), ImGui.GetColorU32(AccentHover), FontAwesomeIcon.User.ToIconString());
        draw.AddText(origin + new Vector2(Ui(34f), (rowHeight - ImGui.GetTextLineHeight()) * 0.5f),
            ImGui.GetColorU32(Vector4.One), TruncateToWidth(name, MathF.Max(Ui(20f), width - Ui(82f))));
        ImGui.SetCursorScreenPos(origin + new Vector2(width - buttonSize - Ui(7f), Ui(7f)));
        bool clicked;
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7f))
                   .Push(ImGuiStyleVar.FramePadding, Vector2.Zero)
                   .Push(ImGuiStyleVar.ButtonTextAlign, new Vector2(0.5f)))
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.12f))
                   .Push(ImGuiCol.ButtonHovered, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.30f)))
        using (UiFonts.PushIcon())
            clicked = ImGui.Button($"{FontAwesomeIcon.Eye.ToIconString()}##unhide", new Vector2(buttonSize));
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip($"Unhide {name}");
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, rowHeight));
        return clicked;
    }
}

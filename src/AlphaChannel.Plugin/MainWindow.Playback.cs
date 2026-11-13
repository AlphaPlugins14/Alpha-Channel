using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Player transport deck — only draws the stage while something is actually playing.
internal sealed partial class MainWindow
{
    private string urlInput = string.Empty;

    // The seek bar tracks live playback position every frame except while the user is actively
    // dragging it - see the Draw body below for why: mpv keeps advancing position during a drag,
    // and resetting the slider's value from that every frame would fight the user's own drag
    // input, snapping back to "now" instead of following the mouse.
    private float seekPreview;
    private bool seekDragging;

    // Playback failure toast
    private string? playbackErrorToast;
    private double playbackErrorToastStartedAt;
    private int lastPlaybackErrorAttempt;
    private VideoQueueEntry? playbackErrorRetryEntry;
    private bool playbackErrorChooseVideo;
    private StreamMode playbackErrorMode;
    private string? playbackErrorHost, playbackErrorAccount;

    private void DrawPlaybackErrorToast()
    {
        if (stream.Mode == StreamMode.Viewing &&
            (Plugin.IsLiveBroadcastUrl(stream.CurrentRoomState?.Url ?? string.Empty) ||
             LiveDjStream.AppliesTo(stream.CurrentRoomState?.Url)))
        {
            playbackErrorToast = null;
            lastPlaybackErrorAttempt = video.PlaybackAttemptId;
            return;
        }

        // ---------------------------------------------------------
        // Detect a new playback failure
        // ---------------------------------------------------------

        if (video.State == VideoPlaybackState.Failed &&
      video.LastError is { Length: > 0 } error &&
      video.PlaybackAttemptId != lastPlaybackErrorAttempt)
        {
            lastPlaybackErrorAttempt = video.PlaybackAttemptId;

            playbackErrorToast = error;
            playbackErrorToastStartedAt = ImGui.GetTime();
            playbackErrorMode = stream.Mode;
            playbackErrorHost = stream.HostId;
            playbackErrorAccount = CurrentSession?.AccountId;
            playbackErrorChooseVideo = false;
            playbackErrorRetryEntry = null;
            if (video.CurrentMediaUrl is { } failedUrl)
            {
                if (VideoUrlResolver.IsYouTubeUrl(failedUrl))
                    playbackErrorRetryEntry = queue.Current is { } current && current.Url == failedUrl ? current
                        : new VideoQueueEntry(failedUrl, failedUrl, string.Empty, null, null);
                else
                {
                    var page = ResolvedMediaLink.TryDecode(failedUrl, out var selected) ? selected.Page
                        : WebMediaUrlResolver.NeedsPageChoice(failedUrl) ? failedUrl : null;
                    if (page is not null)
                    {
                        playbackErrorChooseVideo = true;
                        playbackErrorRetryEntry = new VideoQueueEntry(page, page, string.Empty, null, null);
                    }
                }
            }
        }

        if (playbackErrorToast is null)
        {
            return;
        }

        if (video.PlaybackAttemptId != lastPlaybackErrorAttempt)
        {
            playbackErrorToast = null;
            return;
        }

        var elapsed =
            ImGui.GetTime() -
            playbackErrorToastStartedAt;

        const double totalDuration = 12;
        const double slideDuration = 0.25;
        const double fadeDuration = 0.5;

        if (elapsed >= totalDuration)
        {
            playbackErrorToast = null;
            return;
        }

        // ---------------------------------------------------------
        // Slide animation
        // ---------------------------------------------------------

        var slideProgress =
            Math.Clamp(
                elapsed / slideDuration,
                0.0,
                1.0);

        // Smooth-step so it doesn't move mechanically.
        slideProgress =
            slideProgress *
            slideProgress *
            (3.0 - 2.0 * slideProgress);

        var alpha = 1f;

        if (elapsed >
            totalDuration - fadeDuration)
        {
            alpha =
                (float)Math.Clamp(
                    (totalDuration - elapsed) /
                    fadeDuration,
                    0.0,
                    1.0);
        }

        var width = NativeUi(420f);
        var height = NativeUi(playbackErrorRetryEntry is null ? 78f : 112f);
        var margin = NativeUi(18f);

        var windowPos =
            ImGui.GetWindowPos();

        var windowSize =
            ImGui.GetWindowSize();

        var finalX =
            windowPos.X +
            windowSize.X -
            width -
            margin;

        var finalY =
            windowPos.Y +
            margin;

        // Start above the window and slide downward.
        var startY =
            windowPos.Y -
            height -
            10f;

        var y =
            startY +
            (finalY - startY) *
            (float)slideProgress;

        var min =
            new Vector2(
                finalX,
                y);

        var max =
            new Vector2(
                finalX + width,
                y + height);

        var drawList =
            ImGui.GetForegroundDrawList();

        // ---------------------------------------------------------
        // Background
        // ---------------------------------------------------------

        var background =
            new Vector4(
                0.055f,
                0.06f,
                0.09f,
                0.96f * alpha);

        var border =
            new Vector4(
                Danger.X,
                Danger.Y,
                Danger.Z,
                alpha);

        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(background),
            9f);

        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(border),
            9f,
            ImDrawFlags.None,
            1.5f);

        // Red accent strip.
        drawList.AddRectFilled(
            min,
            new Vector2(
                min.X + Ui(4f),
                max.Y),
            ImGui.GetColorU32(border),
            9f);

        // ---------------------------------------------------------
        // Text
        // ---------------------------------------------------------

        var titlePos =
            new Vector2(
                min.X + Ui(18f),
                min.Y + Ui(13f));

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), titlePos, ImGui.GetColorU32(
                new Vector4(
                    1f,
                    1f,
                    1f,
                    alpha)), "Video couldn't be played");

        var message =
            playbackErrorToast;

        // Keep the raw MPV error from taking over the whole window.
        if (message.Length > 90)
        {
            message =
                message[..87] + "...";
        }

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                min.X + Ui(18f),
                min.Y + Ui(40f)), ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    alpha)), message);

        if (playbackErrorRetryEntry is { } retryEntry)
        {
            // A real overlay owns clicks, keeping the Retry action from clicking
            // through to whichever page happens to be underneath the toast.
            ImGui.SetNextWindowPos(min);
            ImGui.SetNextWindowSize(new(width, height));
            using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            var visible = BeginWindowOverlay("##playbackFailureActions", ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoDocking);
            try
            {
                if (visible)
                {
                    ImGui.SetCursorScreenPos(min + UiVec(18, 72));
                    var sameContext = stream.Mode == playbackErrorMode && stream.HostId == playbackErrorHost &&
                        CurrentSession?.AccountId == playbackErrorAccount && video.State == VideoPlaybackState.Failed;
                    using (UiDisabled.Scope(!sameContext))
                        if (DrawPlaybackToastAction(playbackErrorChooseVideo ? "Choose video" : "Retry", drawList, alpha))
                        {
                            playbackErrorToast = null;
                            if (stream.Mode == StreamMode.Viewing) OnPartyPlaybackResyncRequested?.Invoke();
                            else if (playbackErrorChooseVideo) ChoosePageVideo(retryEntry, HandlePlayNow, forceChoice: true);
                            else HandlePlayNow(retryEntry);
                        }
                    ImGui.SameLine();
                    if (DrawPlaybackToastAction("Dismiss", drawList, alpha)) playbackErrorToast = null;
                }
            }
            finally { ImGui.End(); }
        }
    }

    private bool DrawPlaybackToastAction(string label, ImDrawListPtr draw, float alpha)
    {
        var clicked = ImGui.InvisibleButton(label + "##failedYouTube", UiVec(92, 26));
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var color = ImGui.IsItemHovered() ? AccentHover : Accent;
        color.W *= alpha * ImGui.GetStyle().Alpha;
        draw.AddRectFilled(min, max, ImGui.GetColorU32(color), Ui(5));
        UiFonts.DrawText(draw, ImGui.GetFontSize(), min + (max - min - ImGui.CalcTextSize(label)) * .5f,
            ImGui.GetColorU32(new Vector4(1, 1, 1, alpha * ImGui.GetStyle().Alpha)), label);
        return clicked;
    }

    private void DrawVolumeControl()
    {
        if (IconButton(Plugin.Cfg.Muted ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp))
        {
            Plugin.Cfg.Muted = !Plugin.Cfg.Muted;
            ApplyListeningVolume();
            Plugin.Cfg.Save();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);

        var volume = Plugin.Cfg.Volume;

        using (ImRaii.PushColor(ImGuiCol.Text, volume > 100 ? Gold : Vector4.One))
        if (ImGui.SliderInt("##volume", ref volume, 0, 130, "%d%%"))
        {
            Plugin.Cfg.Volume = volume;
            if (volume > 0) Plugin.Cfg.Muted = false;
            ApplyListeningVolume();
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            Plugin.Cfg.Save();
        }
    }

    private static string FormatTime(float totalSeconds)
    {
        if (totalSeconds < 0 || float.IsNaN(totalSeconds) || float.IsInfinity(totalSeconds))
        {
            totalSeconds = 0;
        }

        var span = TimeSpan.FromSeconds(totalSeconds);
        return span.Hours > 0 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    internal void UpdateRecentlyWatched(
    VideoQueueEntry entry,
    double positionSeconds,
    double durationSeconds)
    {
        if (!IsResumableHistoryMedia(
                entry.Url,
                entry.Source,
                durationSeconds) ||
            entry.IsTransient)
        {
            return;
        }

    
        var existing =
            Plugin.Cfg.RecentlyWatchedVideos
                .FirstOrDefault(
                    x =>
                        string.Equals(
                            x.Url,
                            entry.Url,
                            StringComparison.OrdinalIgnoreCase));


        if (existing is null)
        {
            existing = new RecentlyWatchedVideoRecord
            {
                Url = entry.Url
            };

            Plugin.Cfg.RecentlyWatchedVideos.Insert(
                0,
                existing);
        }


        var metadata = new VideoSearchEntry(entry.Title, entry.Url, entry.Source,
            double.IsFinite(durationSeconds) && durationSeconds > 0 && durationSeconds < TimeSpan.MaxValue.TotalSeconds
                ? TimeSpan.FromSeconds(durationSeconds) : null, entry.ThumbnailUrl);
        if (VideoUrlResolver.IsYouTubeUrl(entry.Url))
        {
            searchResolver.RememberVideoMetadata([metadata]);
            metadata = searchResolver.ApplyCachedMetadata(metadata);
        }
        existing.Title = YouTubeMetadataCache.HasTitle(metadata.Title) ? metadata.Title : existing.Title;
        existing.ThumbnailUrl = metadata.ThumbnailUrl ?? existing.ThumbnailUrl;
        existing.ChannelName = string.IsNullOrWhiteSpace(metadata.ChannelName) ? existing.ChannelName : metadata.ChannelName;
        existing.WatchedSeconds = positionSeconds;
        existing.DurationSeconds = durationSeconds;
        existing.LastWatchedUtc = DateTime.UtcNow;


        Plugin.Cfg.RecentlyWatchedVideos =
            Plugin.Cfg.RecentlyWatchedVideos
                .OrderByDescending(
                    x => x.LastWatchedUtc)
                .Take(15)
                .ToList();


        Plugin.Cfg.Save();
    }

    private static bool IsResumableHistoryMedia(
        string url,
        string source,
        double durationSeconds)
    {
        // Resume history is only useful for finite media. Live services commonly
        // report no duration (or an infinite duration), while Alpha Channel's own
        // broadcast feeds use HLS/RTMP endpoints.
        if (durationSeconds <= 0d ||
            double.IsNaN(durationSeconds) ||
            double.IsInfinity(durationSeconds))
        {
            return false;
        }

        if (string.Equals(source, "Radio", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source, "Music / DJ", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source, "Live Stream", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Host, "twitch.tv", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".twitch.tv", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    private static bool IsResumableHistoryMedia(
        RecentlyWatchedVideoRecord record) =>
        IsResumableHistoryMedia(
            record.Url,
            record.ChannelName,
            record.DurationSeconds);

}

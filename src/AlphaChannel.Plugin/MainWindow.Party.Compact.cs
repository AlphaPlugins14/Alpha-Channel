using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.BroadcastAllowance;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawCompactPartyPlayback(VideoQueueEntry? current, float position, float duration, bool paused)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var narrow = width < Ui(600);
        // Keep the compact player's height stable as the window widens so the
        // extra space goes to the queue rather than a larger thumbnail.
        var thumbWidth = narrow ? 0 : Math.Clamp((width - Ui(40)) * .34f, Ui(180), Ui(224));
        var mediaHeight = Ui(126);
        DrawActivePartyCard("##compactPartyPlayback", new(width, mediaHeight + Ui(66)), () => {
            var origin = ImGui.GetCursorScreenPos();
            var inner = ImGui.GetContentRegionAvail().X;
            var countdown = VisiblePartyCountdown;
            using (UiFonts.PushRelative(1.12f, emphasis: true)) DrawSectionTitle(FontAwesomeIcon.PlayCircle, "Now playing");
            var host = stream.Mode == StreamMode.Hosting;
            if (!narrow)
            {
                var name = host ? CurrentSession?.DisplayName ?? "You" : joinedHostDisplayName ?? "Host";
                var summary = FitPartyTextToWidth($"{stream.Roster.Length} watching · Host: {name}", inner * .48f);
                ImGui.SetCursorScreenPos(origin + new Vector2(inner - ImGui.CalcTextSize(summary).X, Ui(2)));
                ImGui.TextColored(MutedText, summary);
            }
            var media = origin + UiVec(0, 30);
            if (countdown is not null && thumbWidth > 0)
                DrawCountdownThumbnail(countdown, media, new(thumbWidth, mediaHeight));
            else if (current is not null && thumbWidth > 0)
                DrawActivePartyThumbnail(current, media, new(thumbWidth, mediaHeight));
            var details = media + new Vector2(thumbWidth > 0 ? thumbWidth + Ui(20) : 0, 0);
            var detailWidth = inner - (details.X - origin.X);
            ImGui.SetCursorScreenPos(details);
            using (UiFonts.PushRelative(1.1f, emphasis: true))
                ImGui.TextUnformatted(FitPartyTextToWidth(countdown?.State.Label ?? (current is null ? "Nothing is playing" :
                    PrepareTwitchDisplayText(WatchPartyMediaDisplay.Title(current.Url, current.Title, video.IsAudioOnly))), detailWidth));
            if (current is not null && ImGui.IsItemHovered()) UiFonts.SetTooltip(current.Title);
            ImGui.SetCursorScreenPos(details + UiVec(0, 26));
            ImGui.TextColored(MutedText, FitPartyTextToWidth(countdown is not null ?
                !host ? "The host is preparing the next content." : countdownStart?.Target is null ? "Start your content manually when ready." : "The chosen queued video starts automatically." :
                countdownMessage ?? current?.Source ?? "Add a video to get the party started.", detailWidth));
            var state = video.State;
            var status = countdown is not null ? $"Starting in {countdown.Clock}" : current is null ? "Ready when you are" : state == VideoPlaybackState.Failed ? "Playback unavailable" :
                state == VideoPlaybackState.Loading ? "Connecting…" : paused ? "Paused" : "Playing";
            var statusColor = state == VideoPlaybackState.Failed ? Danger : current is null ? MutedText : Good;
            if (countdown is null && current is not null && host && HostBroadcastPanelMethod() == BroadcastAllowanceMethod.DjLive)
                (status, statusColor) = HostPartyBroadcastStatus(BroadcastAllowanceMethod.DjLive);
            ImGui.SetCursorScreenPos(details + UiVec(0, 52));
            DrawPartyStatusDot(statusColor);
            ImGui.SameLine(0, Ui(8));
            using (UiFonts.PushRelative(1f, emphasis: true)) ImGui.TextUnformatted(status);
            if (current is not null && duration > 0 && !IsPartyLiveMedia(current))
            { ImGui.SameLine(0, Ui(16)); ImGui.TextColored(MutedText, $"{FormatTime(position)} / {FormatTime(duration)}"); }
            var bar = details + UiVec(0, 77);
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(bar, bar + new Vector2(detailWidth, Ui(7)), ImGui.GetColorU32(FrameBg), Ui(4));
            var progress = countdown?.Progress ?? (duration > 0 ? Math.Clamp(position / duration, 0, 1) : 0);
            if (progress > 0) draw.AddRectFilled(bar, bar + new Vector2(MathF.Max(Ui(7), detailWidth * progress), Ui(7)), ImGui.GetColorU32(Accent), Ui(4));
            var controls = details + new Vector2(0, mediaHeight - Ui(34));
            CapturePartyPopupBounds();
            var transport = host && current is not null && !video.IsPlayingGame && !video.IsPlayingBrowser &&
                !video.IsPlayingWhiteboard && !IsBroadcastPartyMedia(current) && state is VideoPlaybackState.Playing or VideoPlaybackState.Paused;
            using var controlsBorder = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, Ui(1))
                .Push(ImGuiStyleVar.ItemSpacing, UiVec(10, 0));
            using var controlColours = ImRaii.PushColor(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, .32f));
            ImGui.SetCursorScreenPos(controls);
            DrawPartyTransportRestartButton("compactPartyRestart", Ui(34), StudioTvTransportActive ? !CanSkipStudioSong :
                !transport || current is null || IsPartyLiveMedia(current), "Restart", rectangular: true);
            ImGui.SameLine();
            DrawPartyTransportPlayPauseButton("compactPartyPause", Ui(34), !transport, paused, rectangular: true);
            ImGui.SameLine();
            DrawPartyTransportNextButton("compactPartyNext", Ui(34), StudioTvTransportActive ? !CanSkipStudioSong :
                !host || video.IsAudioOnly || video.IsPlayingGame || video.IsPlayingBrowser || video.IsPlayingWhiteboard ||
                video.IsPlayingLocalVideo || !queue.Entries.Any(x => x.Id != queue.Current?.Id), "Next video", rectangular: true);
            ImGui.SameLine(); DrawSubtitleButton("compactPartySubtitles", Ui(34), boundToPlugin: true);
            var playerWidth = MathF.Min(Ui(142), detailWidth - Ui(182));
            ImGui.SetCursorScreenPos(controls + new Vector2(detailWidth - playerWidth, 0));
            DrawPartyCountdownControls(new(playerWidth, Ui(34)));
        });
    }

    private void DrawCompactPartyTvAudio(VideoQueueEntry? current)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var narrow = width < Ui(600);
        DrawActivePartyCard("##compactPartyTvAudio", new(width, Ui(narrow ? 248 : 170)), () => {
            var inner = ImGui.GetContentRegionAvail().X;
            var origin = ImGui.GetCursorScreenPos();
            var host = stream.Mode == StreamMode.Hosting;
            using (UiFonts.PushRelative(1.12f, emphasis: true)) DrawSectionTitle(FontAwesomeIcon.Tv, "TV & audio");
            var spawned = host ? screenController.Engine.IsActive : ViewerTvEnabled;
            ImGui.SetCursorScreenPos(origin + UiVec(0, 40));
            DrawPartyStatusDot(spawned ? Good : MutedText); ImGui.SameLine(0, Ui(8));
            using (UiFonts.PushRelative(1f, emphasis: true)) ImGui.TextUnformatted(spawned ? "TV spawned" : "TV hidden");
            var groupWidth = narrow ? inner : MathF.Min(Ui(470), inner * .73f);
            var buttonWidth = (groupWidth - Ui(34 + 24)) / 3;
            var controls = origin + new Vector2(inner - groupWidth, Ui(narrow ? 66 : 30));
            ImGui.SetCursorScreenPos(controls);
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, Ui(1)))
            using (ImRaii.PushColor(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, .4f)))
                DrawPartyTvSpawnButton(buttonWidth, 34);
            ImGui.SameLine(0, Ui(8));
            var miniOpen = IsMiniPlayerOpen?.Invoke() ?? false;
            if (ActivePartyButton("Mini player", new(buttonWidth, Ui(34)), primary: !miniOpen)) OnMiniPlayerToggleRequested?.Invoke();
            ImGui.SameLine(0, Ui(8));
            if (ActivePartyButton("Screen settings", new(buttonWidth, Ui(34)))) currentPage = HomePage.Screen;
            ImGui.SameLine(0, Ui(8));
            if (PartyIconButton("partyTvMore", FontAwesomeIcon.EllipsisH, UiVec(34, 34), true)) ImGui.OpenPopup("##partyTvMore");
            DrawCompactPartyTvMenu(current, host);
            var divider = origin + UiVec(0, narrow ? 112 : 76);
            ImGui.GetWindowDrawList().AddLine(divider, divider + new Vector2(inner, 0), ImGui.GetColorU32(BorderSubtle));
            var audio = origin + UiVec(0, narrow ? 125 : 88);
            ImGui.SetCursorScreenPos(audio + UiVec(0, 5));
            using (UiFonts.PushIcon()) ImGui.TextColored(MutedText, FontAwesomeIcon.VolumeUp.ToIconString());
            ImGui.SameLine(0, Ui(8)); ImGui.TextUnformatted("TV Volume");
            var muteWidth = narrow ? (inner - Ui(8)) / 2 : Ui(112);
            var sliderWidth = inner - Ui(narrow ? 148 : 388);
            ImGui.SetCursorScreenPos(audio + UiVec(102, 0));
            DrawCompactPartyVolume(sliderWidth);
            ImGui.SetCursorScreenPos(audio + new Vector2(Ui(110) + sliderWidth, Ui(5)));
            ImGui.TextUnformatted($"{Plugin.Cfg.Volume}%");
            var muteOrigin = narrow ? audio + UiVec(0, 40) : audio + new Vector2(inner - muteWidth * 2 - Ui(8), 0);
            ImGui.SetCursorScreenPos(muteOrigin);
            if (ActivePartyButton(Plugin.Cfg.Muted ? "Unmute TV" : "Mute TV", new(muteWidth, Ui(30))))
            { Plugin.Cfg.Muted = !Plugin.Cfg.Muted; ApplyListeningVolume(); Plugin.Cfg.Save(); }
            ImGui.SameLine(0, Ui(8));
            var gameMuted = IsFfxivSoundMuted();
            if (ActivePartyButton(gameMuted ? "Unmute FFXIV" : "Mute FFXIV", new(muteWidth, Ui(30)))) SetFfxivSoundMuted(!gameMuted);
            ImGui.SetCursorScreenPos(origin + UiVec(0, narrow ? 195 : 118));
            ImGui.TextColored(MutedText, FitPartyTextToWidth(host ? "You're hosting playback and TV placement." :
                PartySyncTvPlacement ? "Following the host's playback and TV placement." : "Following host playback · using your own TV placement.", inner));
        });
    }

    private void DrawCompactPartyVolume(float width)
    {
        var origin = ImGui.GetCursorScreenPos();
        var volume = Plugin.Cfg.Volume;
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(0, 7))
                   .Push(ImGuiStyleVar.FrameBorderSize, 0).Push(ImGuiStyleVar.GrabMinSize, Ui(12)))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero).Push(ImGuiCol.FrameBgHovered, Vector4.Zero)
                   .Push(ImGuiCol.FrameBgActive, Vector4.Zero).Push(ImGuiCol.SliderGrab, Vector4.Zero).Push(ImGuiCol.SliderGrabActive, Vector4.Zero))
        {
            ImGui.SetNextItemWidth(width);
            if (ImGui.SliderInt("##compactPartyVolume", ref volume, 0, 130, ""))
            { Plugin.Cfg.Volume = volume; if (volume > 0) Plugin.Cfg.Muted = false; ApplyListeningVolume(); Plugin.Cfg.Save(); }
        }
        var draw = ImGui.GetWindowDrawList();
        var centerY = (ImGui.GetItemRectMin().Y + ImGui.GetItemRectMax().Y) / 2;
        var centerX = origin.X + Ui(7) + (width - Ui(14)) * volume / 130f;
        draw.AddRectFilled(new(origin.X + Ui(7), centerY - Ui(3)), new(origin.X + width - Ui(7), centerY + Ui(3)), ImGui.GetColorU32(FrameBg), Ui(3));
        draw.AddRectFilled(new(origin.X + Ui(7), centerY - Ui(3)), new(centerX, centerY + Ui(3)), ImGui.GetColorU32(Accent), Ui(3));
        draw.AddRectFilled(new(centerX - Ui(6), centerY - Ui(12)), new(centerX + Ui(6), centerY + Ui(12)), ImGui.GetColorU32(ImGui.IsItemActive() ? AccentHover : Accent), Ui(5));
    }

    private void DrawCompactPartyTvMenu(VideoQueueEntry? current, bool host)
    {
        CapturePartyPopupBounds();
        var help = false;
        var musicOption = current is not null && video.IsAudioOnly && host;
        var rows = 3 + (host ? 0 : 1) + (musicOption ? 1 : 0);
        if (BeginPartyPopup("##partyTvMore", new(Ui(285), Ui(28) + rows * (PartyActionRowHeight + ImGui.GetStyle().ItemSpacing.Y))))
        {
            if (!host)
            {
                var sync = PartySyncTvPlacement;
                if (PartyActionMenuItem("Follow host TV placement", FontAwesomeIcon.Desktop, selected: sync))
                { sync = !sync; SetPartySyncTvPlacement(sync); if (!sync) ApplySavedViewerScreenPlacement(); }
            }
            if (PartyActionMenuItem("Reset screen position", FontAwesomeIcon.Undo, host || !PartySyncTvPlacement)) screenController.Engine.RecenterScreen();
            if (musicOption && PartyActionMenuItem("Adjust music scene", FontAwesomeIcon.Music)) OpenAudioPresentationSettings(current!);
            if (PartyActionMenuItem("Resync playback", FontAwesomeIcon.Sync)) OnPartyPlaybackResyncRequested?.Invoke();
            help = PartyActionMenuItem("Playback help", FontAwesomeIcon.QuestionCircle);
            ImGui.EndPopup();
        }
        if (help) ImGui.OpenPopup("##partyPlaybackHelp");
        if (BeginPartyPopup("##partyPlaybackHelp", UiVec(460, 240), centered: true))
        {
            if (host) DrawHostBroadcastRepair(ImGui.GetContentRegionAvail().X);
            else ImGui.TextUnformatted("Try resyncing playback or rejoining the party.");
            if (ActivePartyButton("Close", UiVec(100, 30))) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }
}

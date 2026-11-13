using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private const string LocalVideoPrivacy = "Your file stays on your computer. It is only uploaded while Watch Party viewers are present.";

    private void DrawLocalVideoSource()
    {
        var engine = screenController.Engine;
        var playing = video.IsPlayingLocalVideo;
        var gameActive = engine.IsPlayingGame || engine.IsPlayingBrowser || engine.IsPlayingWhiteboard;
        var available = stream.Mode != StreamMode.Viewing && !gameActive && queue.Current is null;
        var width = ImGui.GetContentRegionAvail().X;
        var narrow = width < Ui(540);
        var blocked = !playing && !available;
        DrawPlayerHubPanel("##localVideoHub", width, Ui((playing ? 328 : 300) + (narrow ? 48 : 0) + (blocked ? 52 : 0)), true, () =>
        {
            var origin = ImGui.GetCursorScreenPos();
            var innerWidth = ImGui.GetContentRegionAvail().X;
            var draw = ImGui.GetWindowDrawList();
            using (UiFonts.PushRegularRelative(.78f))
                ImGui.TextColored(AccentHover, playing ? "PLAYING LOCALLY" : "LOCAL VIDEO");

            if (playing)
            {
                var (position, duration, paused) = video.GetProgress();
                var name = string.IsNullOrWhiteSpace(localVideoSelectedPath) ? "Local video" : Path.GetFileName(localVideoSelectedPath);
                var tile = origin + UiVec(0, 32);
                draw.AddRectFilled(tile, tile + UiVec(62, 68), ImGui.GetColorU32(AccentInk(.12f)), Ui(8));
                UiFonts.DrawCenteredIcon(draw, Ui(28), tile + UiVec(31, 34), ImGui.GetColorU32(AccentHover), FontAwesomeIcon.FileVideo.ToIconString());
                var textOrigin = origin + UiVec(78, 32);
                using (UiFonts.PushRelative(1.08f, emphasis: true))
                    DrawWrappedLines(draw, textOrigin, MathF.Max(1, innerWidth - Ui(78)), ImGui.GetTextLineHeight(), 2,
                        ImGui.GetColorU32(Vector4.One), name);
                ImGui.SetCursorScreenPos(textOrigin);
                ImGui.Dummy(new(MathF.Max(1, innerWidth - Ui(78)), Ui(48)));
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip(name);
                ImGui.SetCursorScreenPos(origin + UiVec(78, 86));
                using (UiFonts.PushRegularRelative(.86f))
                    ImGui.TextColored(paused ? Gold : Good, paused ? "● Local playback paused" : "● Playing on your TV");

                ImGui.SetCursorScreenPos(origin + UiVec(0, 130));
                var progressWidth = narrow ? innerWidth : innerWidth - Ui(152);
                using (ImRaii.PushColor(ImGuiCol.PlotHistogram, Accent).Push(ImGuiCol.FrameBg, HubSurface(0x14223A)))
                    ImGui.ProgressBar(duration > 0 ? Math.Clamp(position / duration, 0, 1) : 0, new(progressWidth, Ui(8)), string.Empty);
                using (UiFonts.PushRegularRelative(.86f))
                {
                    var format = duration >= 3600 ? @"h\:mm\:ss" : @"m\:ss";
                    var elapsed = TimeSpan.FromSeconds(Math.Max(0, position)).ToString(format);
                    var total = duration > 0 ? TimeSpan.FromSeconds(duration).ToString(format) : "--:--";
                    ImGui.TextColored(MutedText, $"{elapsed} / {total}");
                }
                ImGui.SetCursorScreenPos(origin + new Vector2(narrow ? 0 : innerWidth - Ui(138), Ui(narrow ? 176 : 120)));
                if (GameLayoutButton("Stop video", FontAwesomeIcon.Stop, Ui(138)))
                {
                    StopLocalVideoWatchPartyBroadcast();
                    video.Stop();
                    localVideoError = null;
                }
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Stop playback and remove the TV. This also stops any local-video broadcast.");
                ImGui.SetCursorScreenPos(origin + UiVec(0, narrow ? 224 : 188));
                DrawLocalVideoAudioTracks(innerWidth);
            }
            else
            {
                using (UiFonts.PushRelative(1.65f, emphasis: true))
                    DrawWrappedLines(draw, origin + UiVec(0, 23), innerWidth - Ui(narrow ? 0 : 90), ImGui.GetTextLineHeight(), 2,
                        ImGui.GetColorU32(Vector4.One), "Your files. Your screen.");
                if (!narrow)
                    UiFonts.DrawCenteredIcon(draw, Ui(54), origin + new Vector2(innerWidth - Ui(35), Ui(48)),
                        ImGui.GetColorU32(AccentInk(.22f)), FontAwesomeIcon.FileVideo.ToIconString());
                using (UiFonts.PushRegularRelative(.86f))
                    DrawWrappedLines(draw, origin + UiVec(0, narrow ? 82 : 65), innerWidth, ImGui.GetTextLineHeight(), 2,
                        ImGui.GetColorU32(MutedText), "Play a video from your computer, or bring your Watch Party along.");
                var rowY = narrow ? 132f : 108f;
                ImGui.SetCursorScreenPos(origin + UiVec(0, rowY));
                using (UiFonts.PushRegularRelative(.86f)) ImGui.TextUnformatted("Video file");
                var fileOrigin = origin + UiVec(0, rowY + 25);
                var fileWidth = MathF.Max(1, innerWidth - Ui(130));
                draw.AddRectFilled(fileOrigin, fileOrigin + new Vector2(fileWidth, Ui(38)), ImGui.GetColorU32(HubSurface(0x0A1526)), Ui(7));
                draw.AddRect(fileOrigin, fileOrigin + new Vector2(fileWidth, Ui(38)), ImGui.GetColorU32(HubTint(0x345476)), Ui(7));
                UiFonts.DrawCenteredIcon(draw, Ui(17), fileOrigin + UiVec(20, 19), ImGui.GetColorU32(AccentHover), FontAwesomeIcon.FileVideo.ToIconString());
                var display = string.IsNullOrWhiteSpace(localVideoSelectedPath) ? "No video selected" : Path.GetFileName(localVideoSelectedPath);
                using (UiFonts.PushRegularRelative(.9f))
                    draw.AddText(fileOrigin + UiVec(38, 10), ImGui.GetColorU32(Vector4.One), TruncateToWidth(display, MathF.Max(1, fileWidth - Ui(48))));
                ImGui.SetCursorScreenPos(fileOrigin);
                ImGui.Dummy(new(fileWidth, Ui(38)));
                if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(localVideoSelectedPath)) UiFonts.SetTooltip(localVideoSelectedPath);
                ImGui.SetCursorScreenPos(fileOrigin + new Vector2(fileWidth + Ui(10), 0));
                using (UiDisabled.Scope(!available))
                    if (GameLayoutButton("Browse…", FontAwesomeIcon.FolderOpen, Ui(120), height: 38)) OpenLocalVideoFilePicker();
                ImGui.SetCursorScreenPos(origin + UiVec(0, rowY + 78));
                using (UiDisabled.Scope(!available || string.IsNullOrWhiteSpace(localVideoSelectedPath) || !File.Exists(localVideoSelectedPath)))
                    if (GameLayoutButton("Play video", FontAwesomeIcon.Play, innerWidth, primary: true, height: 38))
                        StartSelectedLocalVideo(broadcast: false);
            }

            ImGui.SetCursorScreenPos(origin + UiVec(0, (playing ? 260 : 234) + (narrow ? 48 : 0)));
            using (UiFonts.PushRegularRelative(.78f))
            using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText, LocalVideoPrivacy);
            if (blocked)
            {
                var reason = stream.Mode == StreamMode.Viewing ? "Leave the current Watch Party before playing a local video."
                    : gameActive ? engine.IsPlayingWhiteboard ? $"End {ActiveCanvasTitle} before playing a local video."
                        : "Exit the current game or browser before playing a local video."
                    : "Stop the current media playback before playing a local video.";
                using (UiFonts.PushRegularRelative(.85f))
                using (ImRaii.TextWrapPos(0)) ImGui.TextColored(Gold, reason);
            }
        });
        SetGameLayoutRowGap(Ui(16));
        DrawLocalVideoBroadcastSettings();
        if (!string.IsNullOrWhiteSpace(localVideoError))
        {
            SetGameLayoutRowGap(Ui(10));
            using (ImRaii.TextWrapPos(0)) ImGui.TextColored(Danger, localVideoError);
        }
    }

    private void OpenLocalVideoFilePicker()
    {
        localVideoFileDialog.OpenFileDialog("Select local video", ".*", (success, path) =>
        {
            if (!success || string.IsNullOrWhiteSpace(path)) return;
            if (!File.Exists(path))
            {
                localVideoError = "The selected video file could not be found.";
                return;
            }
            localVideoSelectedPath = path;
            localVideoError = null;
        });
    }
}

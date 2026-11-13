using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool CanStartSelectedLocalVideoBroadcast()
    {
        var engine = screenController.Engine;
        return CurrentSession is { } session && stream.Mode == StreamMode.Hosting &&
            !string.IsNullOrWhiteSpace(Plugin.Cfg.StreamKeys.GetValueOrDefault(session.AccountId)) &&
            !string.IsNullOrWhiteSpace(localVideoSelectedPath) && File.Exists(localVideoSelectedPath) &&
            !engine.IsPlayingGame && !engine.IsPlayingBrowser && !engine.IsPlayingWhiteboard &&
            (video.IsPlayingLocalVideo || queue.Current is null);
    }

    private void DrawLocalVideoBroadcastStrip()
    {
        if (AccountBroadcastRestricted)
        {
            DrawPlayerHubPanel("##localVideoBroadcastSettings", ImGui.GetContentRegionAvail().X, Ui(210), false, () =>
            {
                using (UiFonts.PushRelative(.94f, emphasis: true)) ImGui.TextUnformatted("Bring your party along");
                ImGui.Spacing();
                DrawAccountBroadcastRestriction("Local video");
            });
            return;
        }
        var viewing = stream.Mode == StreamMode.Viewing;
        var hosting = stream.Mode == StreamMode.Hosting;
        var actionLabel = localVideoBroadcastArmed ? "Stop Broadcast"
            : viewing ? "Host required" : hosting ? "Start Broadcast" : "Create Watch Party";
        var actionIcon = localVideoBroadcastArmed ? FontAwesomeIcon.Stop
            : hosting ? FontAwesomeIcon.BroadcastTower : FontAwesomeIcon.Users;
        var width = ImGui.GetContentRegionAvail().X;
        var stacked = width < Ui(680);
        DrawPlayerHubPanel("##localVideoBroadcastSettings", width, Ui(stacked ? 274 : 156), false, () =>
        {
            var origin = ImGui.GetCursorScreenPos();
            var innerWidth = ImGui.GetContentRegionAvail().X;
            var statusWidth = stacked ? innerWidth : Ui(184);
            using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + statusWidth))
            {
                using (UiFonts.PushIcon()) ImGui.TextColored(Accent, FontAwesomeIcon.BroadcastTower.ToIconString());
                ImGui.SameLine(0, Ui(7));
                using (UiFonts.PushRelative(.94f, emphasis: true)) ImGui.TextUnformatted("Bring your party along");
                var status = !localVideoBroadcastArmed ? "Not broadcasting"
                    : localVideoBroadcastStartFailed ? "Broadcast failed"
                    : localVideoBroadcastPaused ? "Broadcast paused"
                    : localVideoSubtitlesPreparing ? "Preparing subtitles"
                    : screenController.Engine.IsLocalVideoBroadcasting ? $"Live · {stream.Roster.Length} viewer{(stream.Roster.Length == 1 ? string.Empty : "s")}"
                    : CanStartLocalVideoUpload ? "Starting relay upload…" : "Waiting for viewers";
                using (UiFonts.PushRegularRelative(.82f))
                {
                    ImGui.TextColored(localVideoBroadcastStartFailed ? Danger : localVideoBroadcastArmed ? Good : MutedText, "● " + status);
                    if (localVideoBroadcastArmed)
                        ImGui.TextColored(MutedText, $"{Plugin.Cfg.LocalVideoBroadcastQuality} · Up to {LocalVideoBroadcastQualityProfiles.NormalizeFrameRate(Plugin.Cfg.LocalVideoBroadcastFramesPerSecond)} FPS");
                }
            }
            var actionWidth = Ui(190);
            var allowanceWidth = stacked ? innerWidth : MathF.Max(1, innerWidth - statusWidth - actionWidth - Ui(32));
            ImGui.SetCursorScreenPos(origin + new Vector2(stacked ? 0 : statusWidth + Ui(16), Ui(stacked ? 78 : 0)));
            using (var allowance = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##localVideoHubAllowance", new(allowanceWidth, Ui(120)), false,
                       ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
            {
                if (allowance) DrawLocalVideoBroadcastAllowancePlaceholder();
            }
            ImGui.SetCursorScreenPos(origin + new Vector2(stacked ? 0 : innerWidth - actionWidth, Ui(stacked ? 210 : 12)));
            if (localVideoBroadcastArmed)
            {
                if (GameLayoutButton("Stop Broadcast", FontAwesomeIcon.Stop, stacked ? innerWidth : actionWidth, danger: true))
                    StopLocalVideoWatchPartyBroadcast();
            }
            else
            {
                using (UiDisabled.Scope(viewing || CurrentSession is null || (hosting && !CanStartSelectedLocalVideoBroadcast())))
                    if (GameLayoutButton(actionLabel, actionIcon, stacked ? innerWidth : actionWidth, primary: true))
                        BeginLocalVideoWatchPartyBroadcast(!video.IsPlayingLocalVideo);
                if (viewing && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    UiFonts.SetTooltip("You must host a Watch Party to broadcast a local video.");
            }
            DrawMissingBroadcastStreamKeyTooltip(hosting && !localVideoBroadcastArmed);
        });
    }

    private void DrawLocalVideoBroadcastDialog()
    {
        if (!BeginGameDialog("Local video broadcast", "Local Video Broadcast", FontAwesomeIcon.BroadcastTower, 540f, 480f)) return;
        ImGui.TextWrapped("Choose the quality and frame rate for your video broadcast.");
        ImGui.Spacing();
        DrawLocalVideoBroadcastAllowancePlaceholder();
        ImGui.Spacing();
        DrawLocalVideoBroadcastQualitySelector(ConfiguredPatreonTier());
        ImGui.Spacing();
        using var uiFontScale5292=UiFonts.PushRelative(0.72f);
        ImGui.TextColored(MutedText, "BROADCAST FPS");
        uiFontScale5292.Dispose();
        var fps = LocalVideoBroadcastQualityProfiles.NormalizeFrameRate(Plugin.Cfg.LocalVideoBroadcastFramesPerSecond);
        using (UiDisabled.Scope(localVideoBroadcastArmed))
        {
            ImGui.SetNextItemWidth(-1f);
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##localVideoBroadcastFps", $"{fps} FPS")))
            {
                foreach (var option in new[] { (15, "15 FPS (Lowest CPU usage)"), (24, "24 FPS"), (30, "30 FPS") })
                {
                    if (ImGui.Selectable(option.Item2, fps == option.Item1))
                    {
                        Plugin.Cfg.LocalVideoBroadcastFramesPerSecond = option.Item1;
                        Plugin.Cfg.Save();
                    }
                    if (fps == option.Item1) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiFonts.SetTooltip("If you experience lag or viewers report broadcast stream issues then lower FPS");
        }
        ImGui.TextWrapped("FPS is a maximum. Lower-frame-rate videos keep their source rate. Lower FPS reduces broadcast encoding work without changing local playback.");
        ImGui.Spacing();
        var buttonWidth = (ImGui.GetContentRegionAvail().X - Ui(10f)) / 2f;
        if (GameLayoutButton("Cancel", FontAwesomeIcon.Times, buttonWidth)) activeGameDialog = null;
        ImGui.SameLine(0f, Ui(10f));
        using (UiDisabled.Scope(localVideoBroadcastArmed || !CanStartSelectedLocalVideoBroadcast()))
            if (GameLayoutButton("Start Broadcast", FontAwesomeIcon.BroadcastTower, buttonWidth, true))
            {
                StartLocalVideoBroadcastInHostedRoom(!video.IsPlayingLocalVideo);
                if (localVideoBroadcastArmed) activeGameDialog = null;
            }
        DrawMissingBroadcastStreamKeyTooltip(!localVideoBroadcastArmed);
        EndGameDialog();
    }
}


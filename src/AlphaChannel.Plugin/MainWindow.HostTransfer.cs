using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private DateTime hostTransferNoticeUntilUtc;
    private string? hostTransferNoticeText;

    private bool CurrentContentBlocksHostTransfer()
    {
        var engine = screenController.Engine;
        var url = stream.CurrentRoomState?.Url ?? queue.Current?.Url ?? string.Empty;
        // Games and locally produced streams cannot continue on another host's PC.
        return HalloweenActivityActive || IsArcadeGameActive || engine.HasActiveNonVideoPlayback || engine.IsPlayingLocalVideo ||
               IsGameBroadcastArmed || IsBrowserBroadcastArmed || IsLocalVideoBroadcastArmed ||
               djBroadcastingToWatchParty || IsPlayingOwnIcecastDjStream || StudioTvTransportActive ||
               Plugin.IsLiveBroadcastUrl(url) ||
               RelayRadioUrl.IsMatch(url, RelayServerProfile.ServerUrl, Configuration.ProductionRelayServerUrl);
    }

    private void DrawHostTransferDisabledTooltip(bool blocked)
    {
        if (blocked && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiFonts.SetTooltip("End current game/stream to transfer host");
    }

    internal void ApplyHostTransferUi(bool becameHost, StreamControl? state)
    {
        viewerMode = !becameHost;
        joinClearQueuePending = false;
        if (becameHost) proximityJoined = false;
        joinedHostDisplayName = becameHost ? null : stream.CurrentHostDisplayName;
        joinedHostIdentity = becameHost ? null : stream.HostId;
        hostLeaveConfirmationRequested = false;
        ResetViewerMediaAction();
        partyAudioSettingsOverlayOpen = false;
        partyAudioSettingsMedia = null;
        seekDragging = false;
        seekPreview = 0f;
        playbackErrorToast = null;
        lastPlaybackErrorAttempt = video.PlaybackAttemptId;
        playbackWasActive = false;
        lastPlaybackState = false;
        playbackStoppedAt = ImGui.GetTime() - 1d;
        // Preserve the already visible TV; no fresh join prompt or respawn.
        ResetViewerTvSpawnPrompt();
        ViewerTvEnabled = screenController.Engine.IsActive || becameHost;
        viewerTvSpawnPromptShownForCurrentRoom = true;
        if (becameHost && state?.Url is { Length: > 0 } url)
        {
            var selection = AudioVisualizerSelection.Parse(url);
            partyVisualizerMediaUrl = selection.MediaUrl;
            partyVisualizerMode = selection.Mode;
            partyVisualizerTheme = selection.Theme;
            partyAudioScreenText = AudioScreenTextSelection.Parse(url);
            musicEffects = MusicSceneEffects.Parse(url);
            musicSceneInitialized = true;
            var entry = CreatePartyMediaDisplayEntry(url, state.MediaTitle ?? url, state.MediaThumbnailUrl);
            if (AudioVisualizerSelection.HasAudioVisualizerMetadata(url))
                entry.Source = url.Contains("ac_live=1", StringComparison.OrdinalIgnoreCase) ? "Radio" : "Audio";
            queue.AdoptCurrentPlaybackForHostTransfer(entry);
        }
        else
        {
            queue.ReleaseCurrentPlaybackForHostTransfer();
        }
    }

    private void DrawHostTransferNotice()
    {
        while (hostTransferMessages.TryDequeue(out var message))
        {
            hostTransferNoticeText = message;
            hostTransferNoticeUntilUtc = DateTime.UtcNow.AddSeconds(10);
        }
        if (DateTime.UtcNow >= hostTransferNoticeUntilUtc) return;
        var origin = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var panelSize = new Vector2(MathF.Min(Ui(500f), size.X - Ui(32f)), Ui(190f));
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##hostTransferNotice", flags)) { ImGui.End(); return; }
        ImGui.GetWindowDrawList().AddRectFilled(origin, origin + size,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.4f)));
        ImGui.SetCursorScreenPos(origin + (size - panelSize) * 0.5f);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14f)).Push(ImGuiStyleVar.WindowPadding, UiVec(24f, 20f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, Accent))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##hostTransferNoticePanel", panelSize, true)))
        {
            if (panel)
            {
                ImGui.TextColored(Accent, "Host transfer");
                ImGui.Spacing();
                ImGui.TextWrapped(hostTransferNoticeText ?? "This content runs on your device and cannot move to another host. Stop the broadcast or TV activity before passing host. Shared videos and radio can continue during a host transfer.");
                if (ImGui.Button("Close", UiVec(90f, 30f))) hostTransferNoticeUntilUtc = DateTime.MinValue;
            }
        }
        ImGui.End();
    }
}

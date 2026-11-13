using AlphaChannel.Contracts;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool viewerTvPlacementPromptRequested;
    private ViewerTvPlacementStatus viewerTvPlacementPromptStatus;
    private bool? partySyncTvPlacementBeforeTemporaryOverride;
    private readonly WatchPartyScreenSyncPause hostScreenSyncPause = new();
    private string? placementPauseHostId;

    internal void ObserveHostTvPlacement(StreamControl state)
    {
        if (!string.Equals(placementPauseHostId, state.HostId, StringComparison.Ordinal))
        {
            if (partySyncTvPlacementBeforeTemporaryOverride is { } hostChangeSync)
                partySyncTvPlacement = hostChangeSync;
            partySyncTvPlacementBeforeTemporaryOverride = null;
            hostScreenSyncPause.Reset();
            placementPauseHostId = state.HostId;
        }

        var status = WatchPartyTvLocation.Assess(state, 45f);
        var sameArea = status is ViewerTvPlacementStatus.Near or ViewerTvPlacementStatus.TooFar;
        hostScreenSyncPause.ObserveViewer(state.Reason, sameArea,
            status == ViewerTvPlacementStatus.Near,
            ref partySyncTvPlacement, ref partySyncTvPlacementBeforeTemporaryOverride);
    }

    // A deliberate toggle always wins over an automatic local-placement override.
    private void SetPartySyncTvPlacement(bool enabled)
    {
        partySyncTvPlacementBeforeTemporaryOverride = null;
        partySyncTvPlacement = enabled;
    }

    internal void HideViewerTvForDuty()
    {
        DespawnViewerTv(stopPlayback: false);
        viewerTvSpawnPromptRequested = false;
        viewerTvPlacementPromptRequested = false;
        viewerTvSpawnPromptShownForCurrentRoom = true;
    }

    internal bool CanApplyRemoteTvPlacement(StreamControl state) =>
        PartySyncTvPlacement &&
        WatchPartyTvLocation.Assess(state, 45f) == ViewerTvPlacementStatus.Near;

    internal void HandleUnsafeRemoteTvPlacement(StreamControl state)
    {
        if (HalloweenActivityActive) return;
        if (!ViewerTvEnabled || !PartySyncTvPlacement) return;
        var status = WatchPartyTvLocation.Assess(state, 45f);
        if (status is ViewerTvPlacementStatus.Near or ViewerTvPlacementStatus.Unknown)
            return;

        DespawnViewerTv(stopPlayback: !(IsMiniPlayerOpen?.Invoke() ?? false));
        RequestViewerTvPlacementPrompt(status);
    }

    internal bool RequestViewerTvPlacementRecheck(bool allowUnknown)
    {
        if (stream.Mode != StreamMode.Viewing || ViewerTvEnabled || screenController.Engine.AudioOnlyMode)
            return true;
        if (viewerTvSpawnPromptRequested || viewerTvPlacementPromptRequested)
            return true;
        if (stream.CurrentRoomState is not { Url: { Length: > 0 } } state)
            return false;
        if (PartySyncTvPlacement &&
            WatchPartyTvLocation.Assess(state) == ViewerTvPlacementStatus.Unknown &&
            !allowUnknown)
            return false;

        viewerTvSpawnPromptShownForCurrentRoom = false;
        RequestViewerTvSpawnPrompt();
        return true;
    }

    private void RequestViewerTvPlacementPrompt(ViewerTvPlacementStatus status)
    {
        viewerTvSpawnPromptRequested = false;
        viewerTvSpawnPromptShownForCurrentRoom = true;
        viewerTvPlacementPromptStatus = status;
        viewerTvPlacementPromptRequested = true;
    }

    private void RequestViewerTvSpawn()
    {
        if (screenController.Engine.DutyScreen.InDuty)
        {
            OnMiniPlayerRequested?.Invoke();
            return;
        }
        if (stream.Mode != StreamMode.Viewing) return;
        if (!WatchPartyTvLocation.IsLocalPlayerReady())
        {
            Plugin.ChatGui.Print("[AlphaChannel] Wait for the area to finish loading before spawning your TV.");
            return;
        }
        if (stream.CurrentRoomState is { } currentState) ObserveHostTvPlacement(currentState);
        if (!PartySyncTvPlacement)
        {
            // While the host's placement is held, respawn relative to this
            // viewer rather than using its stale pre-duty coordinates.
            SpawnViewerTvWithPlacement(localPlacement: hostScreenSyncPause.IsPaused);
            return;
        }

        var status = WatchPartyTvLocation.Assess(stream.CurrentRoomState);
        if (status == ViewerTvPlacementStatus.Near)
            SpawnViewerTvWithPlacement(localPlacement: false);
        else if (status is ViewerTvPlacementStatus.TooFar or ViewerTvPlacementStatus.DifferentLocation)
            // Explicitly spawning away from the host uses a local screen.
            // The temporary sync override prevents the next host update
            // from sending it back to the distant position.
            SpawnViewerTvWithPlacement(localPlacement: true);
        else
            RequestViewerTvPlacementPrompt(status);
    }

    private void SpawnViewerTvWithPlacement(bool localPlacement, bool recenterLocalPlacement = true)
    {
        if (localPlacement && partySyncTvPlacementBeforeTemporaryOverride is null)
            partySyncTvPlacementBeforeTemporaryOverride = partySyncTvPlacement;
        if (localPlacement)
        {
            partySyncTvPlacement = false;
            screenController.Engine.ReleaseWatchPartyScreenPlacement();
        }

        ViewerTvEnabled = true;
        viewerTvPlacementPromptRequested = false;
        OnViewerTvSpawnRequested?.Invoke();
        if (localPlacement && recenterLocalPlacement && screenController.Engine.ShouldRecenterLocalScreenOnSpawn)
            screenController.Engine.RecenterScreen(manualAdjustment: false);
    }

    private void DrawViewerTvPlacementPrompt()
    {
        if (!viewerTvPlacementPromptRequested) return;

        var parentPos = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var width = MathF.Min(Ui(490f), parentSize.X - Ui(24f));
        var height = MathF.Min(Ui(230f), parentSize.Y - Ui(24f));
        var pos = parentPos + (parentSize - new Vector2(width, height)) * 0.5f;
        ImGui.SetNextWindowPos(parentPos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##viewerTvPlacementOverlay", flags))
        {
            ImGui.End();
            return;
        }

        ImGui.GetWindowDrawList().AddRectFilled(parentPos, parentPos + parentSize,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.55f)));
        ImGui.SetCursorScreenPos(pos);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(12f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(22f, 20f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.035f, 0.04f, 0.075f, 1f))
            .Push(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.7f)))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##viewerTvPlacementPanel",
            new Vector2(width, height), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (panel)
            {
                ImGui.TextColored(Vector4.One, "Spawn your TV here?");
                ImGui.Dummy(UiVec(0f, 9f));
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - Ui(44f));
                var message = viewerTvPlacementPromptStatus switch
                {
                    ViewerTvPlacementStatus.TooFar =>
                        "You're too far from the host's TV. Move closer to use its position, or spawn your own TV here with position syncing off.",
                    ViewerTvPlacementStatus.DifferentLocation =>
                        "The host's TV is in a different location. To watch here, spawn your own TV with position syncing off.",
                    _ =>
                        "The host's TV location can't be confirmed. You can spawn your own TV here with position syncing off.",
                };
                ImGui.TextColored(MutedText, message);
                ImGui.PopTextWrapPos();

                ImGui.SetCursorScreenPos(pos + new Vector2(Ui(22f), height - Ui(62f)));
                var buttonWidth = (width - Ui(54f)) * 0.5f;
                if (ImGui.Button("Cancel##viewerTvPlacement", UiVec(buttonWidth, 39f)))
                    viewerTvPlacementPromptRequested = false;
                ImGui.SameLine(0f, Ui(10f));
                using (ImRaii.PushColor(ImGuiCol.Button, Accent)
                    .Push(ImGuiCol.ButtonHovered, AccentHover)
                    .Push(ImGuiCol.ButtonActive, AccentActive))
                {
                    if (ImGui.Button("Spawn TV here##viewerTvPlacement", UiVec(buttonWidth, 39f)))
                        SpawnViewerTvWithPlacement(localPlacement: true);
                }
            }
        }
        ImGui.End();
    }
}

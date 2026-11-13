using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawStudioStopConfirmation()
    {
        if (!studioStopConfirmationRequested) return;
        if (!IsLocalActivityHost || !studioBroadcastRequested || studioStopWasSpotify != spotifyOwnsStudio)
        { studioStopConfirmationRequested = false; return; }

        var spotify = studioStopWasSpotify;
        var parent = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var size = Vector2.Min(UiVec(520, 260), parentSize - UiVec(32, 32));
        ImGui.SetNextWindowPos(parent);
        ImGui.SetNextWindowSize(parentSize);
        ImGui.SetNextWindowBgAlpha(0);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (!BeginWindowOverlay("##studioStopOverlay", flags)) { ImGui.End(); return; }
        ImGui.GetWindowDrawList().AddRectFilled(parent, parent + parentSize,
            ImGui.GetColorU32(new Vector4(0, 0, 0, .58f)));
        ImGui.SetCursorScreenPos(parent + (parentSize - size) * .5f);
        using (spotify ? SpotifyLibraryColors() : ImRaii.PushColor(ImGuiCol.Button, Accent).Push(ImGuiCol.ButtonHovered, AccentHover))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14)))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7)))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(24, 20)))
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8, 12)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, spotify ? Hex(0x111917) : CardBg)
            .Push(ImGuiCol.Border, spotify ? SpotifyGreen : Accent))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##studioStopPanel", size, true)))
        {
            if (panel)
            {
                var heading = ImGui.GetCursorScreenPos();
                var width = ImGui.GetContentRegionAvail().X;
                using (UiFonts.PushRelative(1.3f, emphasis: true))
                    ImGui.TextColored(spotify ? SpotifyGreen : AccentHover,
                        spotify ? "End Spotify playback?" : "End studio broadcast?");
                ImGui.SetCursorScreenPos(heading + new Vector2(Math.Max(0, width - Ui(34)), 0));
                using (ImRaii.PushId("studioStopClose"))
                    if (GameLayoutButton(string.Empty, FontAwesomeIcon.Times, Ui(34), height: 30)) studioStopConfirmationRequested = false;
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Cancel");
                ImGui.SetCursorScreenPos(heading + UiVec(0, 44));
                ImGui.Separator();
                ImGui.TextWrapped(spotify
                    ? "This will pause Spotify and end Spotify playback for everyone in your Watch Party. Your Watch Party will stay open."
                    : "This will end your live broadcast for everyone and stop the current song or application capture. Your Watch Party will stay open.");
                ImGui.Spacing();
                var buttonWidth = Math.Max(Ui(80), (ImGui.GetContentRegionAvail().X - Ui(12)) / 2);
                if (ImGui.Button(spotify ? "End Spotify playback" : "End broadcast", new Vector2(buttonWidth, Ui(36))))
                {
                    studioStopConfirmationRequested = false;
                    EndPlaybackAndDespawnTvConfirmed();
                }
                ImGui.SameLine(0, Ui(12));
                if (ImGui.Button("Cancel", new Vector2(buttonWidth, Ui(36)))) studioStopConfirmationRequested = false;
            }
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) studioStopConfirmationRequested = false;
        ImGui.End();
    }
}

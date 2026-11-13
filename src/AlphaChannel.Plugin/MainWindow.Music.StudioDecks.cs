using AlphaChannel.BroadcastStudio;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool studioMonitoringExpanded;
    private readonly float[] studioDeckSeek = new float[2];
    private readonly bool[] studioDeckSeeking = new bool[2];

    private void DrawStudioDeck(int deck, StudioDeckSnapshot state)
    {
        using var id = ImRaii.PushId(deck);
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        var letter = deck == 0 ? "A" : "B";
        StudioHeading("Deck " + letter, FontAwesomeIcon.Music);
        StudioPill(origin + UiVec(100, 0), state.Playing ? "Playing" : state.TrackId == null ? "Empty" : "Ready", state.Playing);
        ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(26), 0));
        if (GameLayoutButton("", FontAwesomeIcon.EllipsisH, Ui(26), height: 23)) ImGui.OpenPopup("##deckMenu");
        // Custom menu rows draw their labels directly, so ImGui cannot infer
        // a popup width from the text. Keep height automatic for the UI scale.
        if (ImGui.IsPopupOpen("##deckMenu"))
            ImGui.SetNextWindowSize(new Vector2(Ui(220), 0), ImGuiCond.Always);
        using (var popupColours = ImRaii.PushColor(ImGuiCol.PopupBg, CardBg).Push(ImGuiCol.Border, BorderSubtle))
        using (var popup = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Popup("##deckMenu")))
        {
            if (popup)
            {
                if (DrawYouTubeMenuRow("Load song", FontAwesomeIcon.FolderOpen))
                { OpenMusicLibrary(true, deck); ImGui.CloseCurrentPopup(); }
                using (UiDisabled.Scope(state.TrackId == null))
                {
                    if (DrawYouTubeMenuRow("Restart song", FontAwesomeIcon.Redo))
                    { StudioDeckAction("seek-deck", deck, position: 0); ImGui.CloseCurrentPopup(); }
                    if (DrawYouTubeMenuRow("Unload song", FontAwesomeIcon.Times))
                    { StudioDeckAction("unload-deck", deck); ImGui.CloseCurrentPopup(); }
                }
                ImGui.Separator();
                if (DrawYouTubeMenuRow("Edit scene", FontAwesomeIcon.SlidersH))
                { musicTab = MusicTab.Scene; ImGui.CloseCurrentPopup(); }
            }
        }
        StudioMusicTile(origin + UiVec(0, 32), UiVec(48, 66), true);
        var titleWidth = width - Ui(192);
        var track = state.Track ?? Plugin.Cfg.StudioQueue.FirstOrDefault(t => t.Id == state.TrackId);
        StudioClippedText(origin + UiVec(60, 34), titleWidth, track?.Name ?? "Load a song", false, 1.05f);
        var titleRect = origin + UiVec(60, 34);
        if (track != null && ImGui.IsMouseHoveringRect(titleRect, titleRect + new Vector2(titleWidth, Ui(22))))
            UiFonts.SetTooltip(track.Name);
        StudioAt(origin, 60, 58);
        ImGui.TextColored(MutedText, $"{StudioTime(state.Position)} / {StudioTime(state.Duration)}");
        StudioAt(origin, 60, 78);
        if (!studioDeckSeeking[deck]) studioDeckSeek[deck] = (float)state.Position;
        using (UiDisabled.Scope(state.Duration <= 0))
            if (StudioRail("deckSeek", ref studioDeckSeek[deck], Math.Max(1, (float)state.Duration), titleWidth)) studioDeckSeeking[deck] = true;
        if (ImGui.IsItemDeactivatedAfterEdit())
        { StudioDeckAction("seek-deck", deck, position: studioDeckSeek[deck]); studioDeckSeeking[deck] = false; }
        else if (!ImGui.IsItemActive()) studioDeckSeeking[deck] = false;
        var controlX = width - Ui(122);
        ImGui.SetCursorScreenPos(origin + new Vector2(controlX, Ui(30)));
        using (UiDisabled.Scope(!StudioState.Ready || state.TrackId == null && Plugin.Cfg.StudioQueue.Count == 0))
            if (StudioTransport(state.Playing ? "Pause deck " + letter : "Play deck " + letter,
                state.Playing ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play, 38, true))
                StudioDeckAction(state.Playing ? "pause-deck" : "play-deck", deck);
        ImGui.SameLine(0, Ui(12));
        using (UiDisabled.Scope(state.TrackId == null))
            if (StudioTransport("Stop deck " + letter, FontAwesomeIcon.Stop, 32)) StudioDeckAction("stop-deck", deck);
        var gain = deck == 0 ? studioSettings.DeckAVolume : studioSettings.DeckBVolume;
        ImGui.SetCursorScreenPos(origin + new Vector2(controlX, Ui(79)));
        StudioGainRail("deckGain", gain, Ui(82), value => studioSettings = deck == 0
            ? studioSettings with { DeckAVolume = value } : studioSettings with { DeckBVolume = value });
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Deck " + letter + " volume");
        ImGui.SameLine(0, Ui(3)); ImGui.TextColored(MutedText, $"{gain * 100:0}%");
    }

    private void DrawStudioCrossfader(StudioSnapshot state, bool wide)
    {
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        StudioHeading("Crossfader", FontAwesomeIcon.SlidersH);
        var y = Ui(24);
        var mixerWidth = wide ? width - Ui(242) : width;
        ImGui.SetCursorScreenPos(origin + new Vector2(0, y));
        using (UiDisabled.Scope(!state.Ready || Plugin.Cfg.StudioQueue.Count == 0 && state.DeckA.TrackId == null))
            if (WheelSecondaryButton("Fade to A", new Vector2(Ui(80), Ui(28)))) StudioDeckAction("fade-deck", 0);
        ImGui.SetCursorScreenPos(origin + new Vector2(Ui(89), y + Ui(4))); ImGui.TextUnformatted("A");
        ImGui.SetCursorScreenPos(origin + new Vector2(Ui(107), y));
        var cross = state.Crossfader;
        if (StudioRail("studioCrossfader", ref cross, 1, mixerWidth - Ui(216)))
            studioClient?.Send(new StudioCommand("crossfader") { Position = cross });
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Blend Deck A and Deck B. Moving this cancels an automatic fade.");
        var deckBPercent = Math.Clamp((int)MathF.Round(cross * 100), 0, 100);
        var balance = $"{100 - deckBPercent}/{deckBPercent}";
        var balanceSize = ImGui.CalcTextSize(balance);
        var resetSize = ImGui.CalcTextSize("Reset");
        var readoutWidth = balanceSize.X + Ui(10) + resetSize.X;
        var readout = origin + new Vector2(Ui(107) + (mixerWidth - Ui(216) - readoutWidth) / 2, 0);
        var resetAt = readout + new Vector2(balanceSize.X + Ui(10), 0);
        ImGui.SetCursorScreenPos(resetAt);
        if (ImGui.InvisibleButton("##resetStudioCrossfader", new Vector2(resetSize.X, Math.Max(Ui(18), resetSize.Y))))
        {
            cross = .5f;
            balance = "50/50";
            studioClient?.Send(new StudioCommand("crossfader") { Position = .5 });
        }
        var resetHovered = ImGui.IsItemHovered();
        if (resetHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            UiFonts.SetTooltip("Reset to 50% A / 50% B and cancel the automatic fade.");
        }
        var readoutDraw = ImGui.GetWindowDrawList();
        readoutDraw.AddText(readout, ImGui.GetColorU32(MutedText), balance);
        readoutDraw.AddText(resetAt, ImGui.GetColorU32(resetHovered ? AccentHover : Accent), "Reset");
        ImGui.SetCursorScreenPos(origin + new Vector2(mixerWidth - Ui(100), y + Ui(4))); ImGui.TextUnformatted("B");
        ImGui.SetCursorScreenPos(origin + new Vector2(mixerWidth - Ui(80), y));
        using (UiDisabled.Scope(!state.Ready || Plugin.Cfg.StudioQueue.Count == 0 && state.DeckB.TrackId == null))
            if (WheelSecondaryButton("Fade to B", new Vector2(Ui(80), Ui(28)))) StudioDeckAction("fade-deck", 1);
        ImGui.SetCursorScreenPos(origin + new Vector2(wide ? width - Ui(222) : 0, wide ? y : Ui(60)));
        ImGui.SetNextItemWidth(Ui(108));
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##studioFadeDuration", $"{studioSettings.FadeSeconds:0} seconds")))
        {
            foreach (var seconds in new[] { 1, 3, 5, 10, 15 })
                if (ImGui.Selectable($"{seconds} seconds", studioSettings.FadeSeconds == seconds))
                { studioSettings = studioSettings with { FadeSeconds = seconds }; StudioSettingsChanged(); }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Duration of automatic transitions");
        ImGui.SameLine(0, Ui(12)); var auto = studioSettings.AutoNext;
        if (ImGui.Checkbox("Auto next", ref auto))
        { studioSettings = studioSettings with { AutoNext = auto }; StudioSettingsChanged(); }
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Fade to the prepared deck near the end of the current song, or load the next queued song automatically.");
    }
}

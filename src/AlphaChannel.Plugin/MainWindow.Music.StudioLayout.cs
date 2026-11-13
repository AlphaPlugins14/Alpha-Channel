using AlphaChannel.BroadcastStudio;
using AlphaChannel.Plugin.BroadcastAllowance;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawBroadcastStudio(float width, float gap)
    {
        DrawAccountBroadcastRestriction("Studio");
        if (spotifyOwnsStudio)
        {
            ImGui.TextWrapped("Spotify is using the broadcast audio connection. Stop its broadcast before changing Studio sources.");
            if (GameLayoutButton("Open Spotify", FontAwesomeIcon.Headphones, Ui(170))) musicTab = MusicTab.Spotify;
            ImGui.SameLine();
            if (GameLayoutButton("Stop Spotify broadcast", FontAwesomeIcon.Stop, Ui(210))) StopStudioBroadcast();
            return;
        }
        var canUseStudio = CanUseBroadcastStudio;
        if (!canUseStudio)
        {
            using var noticeColour = ImRaii.PushColor(ImGuiCol.Text, MutedText);
            ImGui.TextWrapped(StudioUnavailableMessage);
            ImGui.Spacing();
        }
        else
        {
            EnsureStudio(); EnsureDjCredentialsForMusic();
        }
        var state = StudioState;
        var wide = width >= Ui(820);
        var panelGap = gap;
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8, 5));
        using var frames = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(7, 3)).Push(ImGuiStyleVar.FrameBorderSize, Ui(1));
        using var contrast = ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Lerp(FrameBg, Vector4.One, .04f))
            .Push(ImGuiCol.Border, Vector4.Lerp(BorderSubtle, Vector4.One, .12f));
        using var disabled = UiDisabled.Scope(!canUseStudio);
        StudioPanel("##studioBroadcast", width, Ui((wide ? 104 : 206) + (ConfiguredPatreonTier() == 0 ? 18 : 0)), () => DrawStudioBroadcastStrip(state, wide));
        SetGameLayoutRowGap(panelGap);
        using var inactive = UiDisabled.Scope(!studioSessionActive || studioPreviewStarting);
        if (StudioUsesApplication)
            StudioPanel("##studioPlaying", width, Ui(wide ? 118 : 178), () => DrawStudioPlayer(state, wide));
        else
        {
            var deckTop = ImGui.GetCursorScreenPos();
            var deckWidth = (width - panelGap) / 2;
            StudioPanel("##studioDeckA", deckWidth, Ui(132), () => DrawStudioDeck(0, state.DeckA));
            ImGui.SetCursorScreenPos(deckTop + new Vector2(deckWidth + panelGap, 0));
            StudioPanel("##studioDeckB", deckWidth, Ui(132), () => DrawStudioDeck(1, state.DeckB));
            // This cursor is explicit, unlike EndChild's automatic advancement.
            // Add the full panel gap instead of subtracting ImGui item spacing.
            ImGui.SetCursorScreenPos(deckTop + new Vector2(0, Ui(132) + panelGap));
            StudioPanel("##studioCrossfader", width, Ui(wide ? 80 : 112), () => DrawStudioCrossfader(state, wide));
        }
        SetGameLayoutRowGap(panelGap);
        var monitoringHeight = Ui(studioMonitoringExpanded ? 124 : 44);
        var minimumHeight = Ui(166 + 154) + monitoringHeight + panelGap * 2;
        if (StudioUsesApplication) minimumHeight = MathF.Max(minimumHeight, Ui(500));
        var middleHeight = Math.Clamp(ImGui.GetContentRegionAvail().Y - Ui(12), minimumHeight, Ui(700));
        var left = wide ? (width - panelGap) * .62f : width;
        var top = ImGui.GetCursorScreenPos();
        DrawStudioQueue(left, middleHeight);
        if (wide) ImGui.SetCursorScreenPos(top + new Vector2(left + panelGap, 0)); else SetGameLayoutRowGap(panelGap);
        var right = wide ? width - left - panelGap : width;
        StudioPanel("##studioMic", right, Ui(166), () => DrawStudioMicrophone(state));
        if (wide) ImGui.SetCursorScreenPos(top + new Vector2(left + panelGap, Ui(166) + panelGap));
        else SetGameLayoutRowGap(panelGap);
        StudioPanel("##studioOutput", right, Ui(154), () => DrawStudioOutput(state));
        if (wide) ImGui.SetCursorScreenPos(top + new Vector2(left + panelGap, Ui(320) + panelGap * 2));
        else SetGameLayoutRowGap(panelGap);
        StudioPanel("##studioSettings", right, monitoringHeight, () => DrawStudioMonitoring(state));
        if (wide) ImGui.SetCursorScreenPos(top + new Vector2(0, middleHeight + panelGap));
        else SetGameLayoutRowGap(panelGap);
        if (state.Error is { } error) ImGui.TextColored(Danger, error);
        if (state.DeviceError is { } deviceError) ImGui.TextColored(Danger, deviceError);
        if (radioError is { } radioIssue) ImGui.TextColored(Danger, radioIssue);
    }
    private void StudioPanel(string id, float width, float height, Action draw)
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(12, 12));
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(9));
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(id, new Vector2(width, height), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse));
        if (panel) draw();
    }
    private void StudioAt(Vector2 origin, float x, float y) => ImGui.SetCursorScreenPos(origin + UiVec(x, y));
    private void StudioHeading(string label, FontAwesomeIcon icon)
    {
        using (UiFonts.PushIcon()) ImGui.TextColored(AccentHover, icon.ToIconString());
        ImGui.SameLine(0, Ui(10));
        using var uiFontScale5046=UiFonts.PushRelative(1.1f, emphasis: true);
        ImGui.TextColored(Hex(0xE2E7EF), label); uiFontScale5046.Dispose();
    }
    private void StudioPill(Vector2 at, string label, bool active, Vector4? textColour = null)
    {
        var size = ImGui.CalcTextSize(label) + UiVec(18, 8);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(at, at + size, ImGui.GetColorU32(active ? Vector4.Lerp(CardBg, Accent, .35f) : FrameBgHover), Ui(12));
        draw.AddText(at + UiVec(9, 4), ImGui.GetColorU32(textColour ?? (active ? AccentHover : MutedText)), label);
    }
    private void DrawStudioBroadcastStrip(StudioSnapshot state, bool wide)
    {
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        var headingWidth = Ui(175); var actionWidth = Ui(125); var allowanceWidth = Ui(260);
        var status = studioPublished ? "ON AIR" : studioBroadcastRequested ? "CONNECTING" : studioPreviewStarting ? "STARTING" : studioSessionActive ? "Ready" : "OFF AIR";
        var readyColour = new Vector4(1f, .65f, .2f, 1f);
        var statusWidth = ImGui.CalcTextSize(status).X + Ui(18);
        StudioAt(origin, 2, wide ? 19 : 0); StudioHeading("Your broadcast", FontAwesomeIcon.BroadcastTower);
        var listeners = stream.Mode == StreamMode.Hosting ? stream.Roster.Length : 0;
        StudioAt(origin, 28, wide ? 45 : 22);
        ImGui.TextColored(MutedText, $"{listeners} {(listeners == 1 ? "Listener" : "Listeners")}");
        if (!wide) origin.Y += Ui(22);
        var nameX = wide ? headingWidth : 0;
        var nameWidth = wide ? Math.Max(Ui(100), width - headingWidth - allowanceWidth - actionWidth - statusWidth - Ui(44)) : width - actionWidth - Ui(12);
        using var setupDisabled = UiDisabled.Scope(!studioSessionActive || studioPreviewStarting);
        ImGui.SetCursorScreenPos(origin + new Vector2(nameX, Ui(wide ? 4 : 30)));
        var name = Plugin.Cfg.DjBroadcastName; ImGui.SetNextItemWidth(nameWidth);
        if (ImGui.InputTextWithHint("##studioName", "Your broadcast name", ref name, 100)) Plugin.Cfg.DjBroadcastName = name;
        if (ImGui.IsItemDeactivatedAfterEdit()) Plugin.Cfg.Save();
        if (wide)
        {
            var at = origin + new Vector2(nameX + nameWidth + Ui(10), Ui(5));
            StudioPill(at, status, studioPublished, status == "Ready" ? readyColour : null);
        }
        if (!wide)
        {
            var at = origin + new Vector2(headingWidth, -Ui(22));
            StudioPill(at, status, studioPublished, status == "Ready" ? readyColour : null);
        }
        StudioAt(origin, wide ? nameX / Ui(1) : 0, wide ? 37 : 62);
        var hint = studioPublished ? "Broadcasting to your Watch Party" : studioBroadcastRequested ? "Connecting to your Watch Party…" :
            CurrentSession == null ? "Preview locally · Sign in to broadcast" : djCredentialsLoading ? "Loading your broadcast connection…" : studioSessionActive ? "Ready to broadcast to your Watch Party" : "Start the studio to prepare your mix";
        using (UiFonts.PushRelative(0.90f))
            ImGui.TextColored(MutedText, FitPartyTextToWidth(hint, wide ? nameWidth + statusWidth + Ui(10) : nameWidth));
        var allowance = BroadcastAllowanceDisplayFor(BroadcastAllowanceMethod.DjLive);
        var allowanceX = wide ? width - actionWidth - allowanceWidth - Ui(24) : 0;
        ImGui.SetCursorScreenPos(origin + new Vector2(allowanceX, Ui(wide ? 9 : 88)));
        ImGui.TextColored(MutedText, allowance.RemainingLabel + " remaining");
        var bar = origin + new Vector2(allowanceX, Ui(wide ? 32 : 109));
        var barWidth = wide ? allowanceWidth : width;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(bar, bar + new Vector2(barWidth, Ui(7)), ImGui.GetColorU32(FrameBgHover), Ui(4));
        draw.AddRectFilled(bar, bar + new Vector2(barWidth * Math.Clamp(allowance.Remaining, 0, 1), Ui(7)), ImGui.GetColorU32(Accent), Ui(4));
        ImGui.SetCursorScreenPos(origin + new Vector2(allowanceX, Ui(wide ? 51 : 132)));
        ImGui.TextColored(MutedText, "Quality");
        ImGui.SetCursorScreenPos(origin + new Vector2(allowanceX + Ui(62), Ui(wide ? 46 : 127)));
        using (UiDisabled.Scope(studioBroadcastRequested))
        {
            ImGui.SetNextItemWidth(barWidth - Ui(62));
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##studioQuality", StudioQualityLabel(studioSettings.Bitrate))))
            {
                var tier = CurrentVerifiedPatreonTier();
                foreach (var quality in new[] { 96, 128, 192, 256, 320 })
                {
                    var requiredTier = StudioQualityRequiredTier(quality);
                    var locked = tier < requiredTier;
                    var label = StudioQualityLabel(quality) + (requiredTier > 0 ? $" (Patreon Tier {requiredTier})" : "");
                    using (UiDisabled.Scope(locked))
                        if (ImGui.Selectable(label, studioSettings.Bitrate == quality))
                        { studioSettings = studioSettings with { Bitrate = quality }; StudioSettingsChanged(); }
                    if (locked && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        UiFonts.SetTooltip($"Available with Patreon Tier {requiredTier} or higher.");
                }
                ImGui.EndCombo();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiFonts.SetTooltip(studioBroadcastRequested ? "Stop broadcasting to change broadcast quality." : "Audio quality sent to your listeners.");
        setupDisabled.Dispose();
        if (ConfiguredPatreonTier() == 0)
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(allowanceX, Ui(wide ? 76 : 154)));
            using var uiFontScale10802=UiFonts.PushRelative(0.68f);
            DrawPatreonUpgradeLink();
            uiFontScale10802.Dispose();
        }
        ImGui.SetCursorScreenPos(origin + new Vector2(width - actionWidth, 0));
        if (GameLayoutButton(studioSessionActive ? "End" : "Start",
            studioSessionActive ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play,
            actionWidth, true, danger: studioSessionActive, height: 30))
        {
            if (studioSessionActive) EndPlaybackAndDespawnTv();
            else StartStudioActivity();
        }
        ImGui.SetCursorScreenPos(origin + new Vector2(width - actionWidth, Ui(36)));
        using (UiDisabled.Scope(!studioSessionActive || studioPreviewStarting || !studioBroadcastRequested && (!state.Ready || radioCredentials?.SourcePassword is not { Length: > 0 })))
            if (GameLayoutButton(studioBroadcastRequested ? "Stop" : "Go Live", studioBroadcastRequested ? FontAwesomeIcon.Stop : FontAwesomeIcon.BroadcastTower,
                actionWidth, true, danger: studioBroadcastRequested, height: 42))
            { if (studioBroadcastRequested) ReturnStudioToReady(); else BeginStudioBroadcast(); }
    }
    private void StudioMusicTile(Vector2 at, Vector2 size, bool prominent)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(at, at + size, ImGui.GetColorU32(prominent ? Vector4.Lerp(CardBg, Accent, .55f) : FrameBgHover), Ui(6));
        if (prominent)
            for (var i = 0; i < 5; i++)
            {
                var y = at.Y + size.Y * (i + 1) / 6;
                draw.AddBezierCubic(new Vector2(at.X, y), new Vector2(at.X + size.X * .35f, y - Ui(15)),
                    new Vector2(at.X + size.X * .7f, y + Ui(15)), new Vector2(at.X + size.X, y),
                    ImGui.GetColorU32(new Vector4(AccentHover.X, AccentHover.Y, AccentHover.Z, .3f)), Ui(3));
            }
        using var font = UiFonts.PushIcon();
        var text = FontAwesomeIcon.Music.ToIconString(); var fontSize = Ui(prominent ? 30 : 15);
        var textSize = ImGui.CalcTextSize(text) * (fontSize / ImGui.GetFontSize());
        UiFonts.DrawText(draw, fontSize, at + (size - textSize) / 2, ImGui.GetColorU32(ImGuiCol.Text), text);
    }
    private bool StudioTransport(string id, FontAwesomeIcon icon, float diameter, bool primary = false)
    {
        var at = ImGui.GetCursorScreenPos(); var size = UiVec(diameter, diameter);
        using var colours = ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero).Push(ImGuiCol.ButtonHovered, Vector4.Zero).Push(ImGuiCol.ButtonActive, Vector4.Zero);
        using var border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0);
        var clicked = ImGui.Button("##" + id, size); var draw = ImGui.GetWindowDrawList(); var center = at + size / 2;
        draw.AddCircleFilled(center, size.X / 2, ImGui.GetColorU32(primary ? ImGui.IsItemHovered() ? AccentHover : Accent : FrameBg));
        if (!primary) draw.AddCircle(center, size.X / 2 - Ui(1), ImGui.GetColorU32(BorderSubtle), 32, Ui(1));
        using (UiFonts.PushIcon())
        { var text = icon.ToIconString(); draw.AddText(center - ImGui.CalcTextSize(text) / 2, ImGui.GetColorU32(ImGuiCol.Text), text); }
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip(id);
        return clicked;
    }
    private bool StudioRail(string id, ref float value, float maximum, float width)
    {
        var origin = ImGui.GetCursorScreenPos();
        using var border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0);
        using var colours = ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero).Push(ImGuiCol.FrameBgHovered, Vector4.Zero)
            .Push(ImGuiCol.FrameBgActive, Vector4.Zero).Push(ImGuiCol.SliderGrab, Vector4.Zero).Push(ImGuiCol.SliderGrabActive, Vector4.Zero);
        ImGui.SetNextItemWidth(Math.Max(Ui(30), width));
        var changed = ImGui.SliderFloat("##" + id, ref value, 0, maximum, "");
        var size = ImGui.GetItemRectSize(); var centerY = origin.Y + size.Y / 2;
        var start = new Vector2(origin.X + Ui(5), centerY - Ui(3)); var end = new Vector2(origin.X + size.X - Ui(5), centerY + Ui(3));
        var amount = Math.Clamp(value / maximum, 0, 1); var x = start.X + (end.X - start.X) * amount;
        var draw = ImGui.GetWindowDrawList(); draw.AddRectFilled(start, end, ImGui.GetColorU32(FrameBgHover), Ui(3));
        draw.AddRectFilled(start, new Vector2(x, end.Y), ImGui.GetColorU32(Accent), Ui(3));
        draw.AddCircleFilled(new Vector2(x, centerY), Ui(6), ImGui.GetColorU32(AccentHover));
        return changed;
    }
    private void StudioGainRail(string id, float value, float width, Action<float> change, float maximum = 100)
    {
        var percent = value * 100;
        if (StudioRail(id, ref percent, maximum, width)) { change(percent / 100); StudioSettingsChanged(false); }
        if (ImGui.IsItemDeactivatedAfterEdit()) StudioSettingsChanged();
    }
    private void StudioSegments(Vector2 at, float width, float value, float height = 10)
    {
        var draw = ImGui.GetWindowDrawList(); var count = Math.Max(8, (int)(width / Ui(8))); var step = width / count;
        for (var i = 0; i < count; i++)
        {
            var lit = i < (int)Math.Ceiling(Math.Clamp(value, 0, 1) * count);
            var colour = !lit ? FrameBgHover : i >= count * .9f ? Danger : i >= count * .72f ? new Vector4(1, .78f, .12f, 1) : new Vector4(.2f, .9f, .32f, 1);
            var start = at + new Vector2(step * i, 0);
            draw.AddRectFilled(start, start + new Vector2(Math.Max(1, step - Ui(2)), Ui(height)), ImGui.GetColorU32(colour), Ui(1));
        }
    }
    private void DrawStudioPlayer(StudioSnapshot state, bool wide)
    {
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        if (StudioUsesApplication) StudioApplicationTile(origin, UiVec(58, 90));
        else StudioMusicTile(origin, UiVec(58, 90), true);
        StudioAt(origin, 72, 0); ImGui.TextColored(MutedText, StudioUsesApplication ? "Application audio" : "Now playing");
        var track = state.DeckA.TrackId == state.TrackId ? state.DeckA.Track : state.DeckB.Track;
        track ??= Plugin.Cfg.StudioQueue.FirstOrDefault(t => t.Id == state.TrackId);
        var textWidth = wide ? width - Ui(557) : width - Ui(72);
        StudioAt(origin, 72, 22);
        StudioClippedText(ImGui.GetCursorScreenPos(), textWidth, StudioUsesApplication ?
            StudioSelectedApplication is { } app ? "Audio from " + app.Name : "Choose an application" :
            track?.Name ?? "Add songs to start your mix", false, 1.12f);
        StudioClippedText(origin + UiVec(72, 46), textWidth, StudioUsesApplication ?
            StudioApplicationStatus(state) + (studioPublished ? " · Broadcasting live" : " · Previewing locally") :
            studioPublished ? "Your local music · Broadcasting live" : "Your local music · Previewing locally", true);
        if (!StudioUsesApplication)
        {
            StudioAt(origin, 72, 68);
            if (!studioSeeking) studioSeekPosition = (float)state.Position;
            using (UiDisabled.Scope(state.Duration <= 0))
                if (StudioRail("studioSeek", ref studioSeekPosition, Math.Max(1, (float)state.Duration), Math.Max(Ui(70), textWidth - Ui(96)))) studioSeeking = true;
            if (ImGui.IsItemDeactivatedAfterEdit()) { studioClient?.Send(new StudioCommand("seek") { Position = studioSeekPosition }); studioSeeking = false; }
            else if (!ImGui.IsItemActive()) studioSeeking = false;
            ImGui.SameLine(0, Ui(8)); ImGui.TextColored(MutedText, $"{StudioTime(state.Position)} / {StudioTime(state.Duration)}");
        }
        var controlsX = wide ? width - Ui(464) : Ui(72);
        var controlsY = Ui(wide ? 29 : 105);
        ImGui.SetCursorScreenPos(origin + new Vector2(controlsX, controlsY));
        if (StudioUsesApplication) DrawStudioCaptureToggle("studioPlayerCapture");
        else
        {
            if (StudioTransport("Previous", FontAwesomeIcon.StepBackward, 36)) StudioAction("previous");
            var pause = state.Playing && (studioSelectedTrackId is null || studioSelectedTrackId == state.TrackId);
            ImGui.SameLine(0, Ui(8)); if (StudioTransport(pause ? "Pause" : "Play", pause ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play, 44, true)) ToggleStudioSongPlayback();
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip(pause ? "Pause current song" : "Play selected song");
            ImGui.SameLine(0, Ui(8)); if (StudioTransport("Next", FontAwesomeIcon.StepForward, 36)) StudioAction("next");
            ImGui.SameLine(0, Ui(6)); if (StudioTransport("Stop studio", FontAwesomeIcon.Stop, 30))
            { if (studioBroadcastRequested) EndPlaybackAndDespawnTv(); else StopStudioBroadcast(); }
        }
        var gainX = wide ? width - Ui(275) : Ui(267);
        ImGui.SetCursorScreenPos(origin + new Vector2(gainX, Ui(wide ? 17 : 100))); ImGui.TextColored(MutedText, "Music volume");
        ImGui.SetCursorScreenPos(origin + new Vector2(gainX, Ui(wide ? 38 : 122)));
        StudioGainRail("studioMusicGain", studioSettings.MusicVolume, Ui(130), v => studioSettings = studioSettings with { MusicVolume = v });
        ImGui.SameLine(); ImGui.TextUnformatted($"{studioSettings.MusicVolume * 100:0}%");
        StudioSegments(origin + new Vector2(gainX + Ui(5), Ui(wide ? 68 : 151)), Ui(125), state.MusicLevel);
        ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(108), Ui(wide ? 35 : 115)));
        if (GameLayoutButton("Edit scene", FontAwesomeIcon.SlidersH, Ui(108), height: 32)) musicTab = MusicTab.Scene;
    }
    private void StudioClippedText(Vector2 at, float width, string text, bool muted = false, float scale = 1)
    {
        using var secondaryFont = muted ? UiFonts.PushRelative(0.90f) : UiFonts.Empty;
        var draw = ImGui.GetWindowDrawList(); draw.PushClipRect(at, at + new Vector2(Math.Max(1, width), Ui(24)), true);
        UiFonts.DrawText(draw, ImGui.GetFontSize() * scale, at, ImGui.GetColorU32(muted ? MutedText : Hex(0xE2E7EF)), text);
        draw.PopClipRect();
    }
    private void DrawStudioDevice(string id, StudioDevice[] devices, string selected, Action<string> change, float width)
    {
        ImGui.SetNextItemWidth(width);
        var name = devices.FirstOrDefault(d => d.Id == selected)?.Name ?? (selected.Length == 0 ? "Windows default" : "Device unavailable");
        if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##" + id, name))) return;
        if (ImGui.Selectable("Windows default", selected.Length == 0)) { change(""); StudioSettingsChanged(); }
        foreach (var device in devices) if (ImGui.Selectable(device.Name + "##" + device.Id, selected == device.Id)) { change(device.Id); StudioSettingsChanged(); }
        ImGui.EndCombo();
    }
    private void DrawStudioMicrophone(StudioSnapshot state)
    {
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        StudioHeading("Microphone", FontAwesomeIcon.Microphone);
        StudioAt(origin, 0, 31); ImGui.TextColored(MutedText, "Device"); StudioAt(origin, 52, 27);
        DrawStudioDevice("studioMicDevice", state.Microphones, studioSettings.MicDevice, id => studioSettings = studioSettings with { MicDevice = id }, width - Ui(133));
        ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(72), Ui(29)));
        var on = studioSettings.MicEnabled;
        if (DrawSettingsToggle("##studioMicOn", "", ref on)) { studioSettings = studioSettings with { MicEnabled = on }; StudioSettingsChanged(); }
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip(state.MicActive ? "Microphone active" : on ? "Microphone enabled · push-to-talk released" : "Microphone off");
        ImGui.SameLine(0, Ui(5)); ImGui.TextColored(on ? AccentHover : MutedText, on ? "On" : "Off");
        StudioMixerRow(origin, "Input volume", 58, studioSettings.MicVolume, width, v => studioSettings = studioSettings with { MicVolume = v });
        StudioAt(origin, 0, 86); var ptt = studioSettings.PushToTalk;
        if (ImGui.Checkbox("Push to talk", ref ptt)) { studioSettings = studioSettings with { PushToTalk = ptt }; StudioSettingsChanged(); }
        if (ptt)
        {
            ImGui.SameLine(0, Ui(4)); ImGui.SetNextItemWidth(Ui(48));
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##studioTalkKey", ((VirtualKey)Plugin.Cfg.StudioTalkKey).ToString())))
            { for (var key = 112; key <= 123; key++) if (ImGui.Selectable(((VirtualKey)key).ToString(), key == Plugin.Cfg.StudioTalkKey)) { Plugin.Cfg.StudioTalkKey = key; Plugin.Cfg.Save(); } ImGui.EndCombo(); }
        }
        ImGui.SetCursorScreenPos(origin + new Vector2(width * (ptt ? .57f : .40f), Ui(86)));
        var duck = studioSettings.DuckMusic;
        if (ImGui.Checkbox(width >= Ui(345) && !ptt ? "Lower music while speaking" : "Duck music", ref duck)) { studioSettings = studioSettings with { DuckMusic = duck }; StudioSettingsChanged(); }
        StudioMixerRow(origin, "Ducking amount", 116, studioSettings.DuckAmount, width, v => studioSettings = studioSettings with { DuckAmount = v });
    }
    private void StudioMixerRow(Vector2 origin, string label, float y, float value, float width, Action<float> change)
    {
        StudioAt(origin, 0, y + 3); ImGui.TextColored(MutedText, label);
        var labelWidth = StudioMixerLabelWidth;
        ImGui.SetCursorScreenPos(origin + new Vector2(labelWidth, Ui(y)));
        StudioGainRail("studio" + label, value, width - labelWidth - Ui(39), change, label == "Local listening" ? 130 : 100);
        ImGui.SameLine(0, Ui(5)); ImGui.TextUnformatted($"{value * 100:0}%");
    }
    private float StudioMixerLabelWidth => MathF.Max(Ui(116), ImGui.CalcTextSize("Ducking amount").X + Ui(12));
    private void DrawStudioOutput(StudioSnapshot state)
    {
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        StudioHeading("Broadcast output", FontAwesomeIcon.BroadcastTower);
        StudioAt(origin, 0, 32); ImGui.TextColored(MutedText, "Output (stereo)");
        StudioSegments(origin + new Vector2(StudioMixerLabelWidth, Ui(34)), width - StudioMixerLabelWidth - Ui(4), state.OutputLeft, 6);
        StudioSegments(origin + new Vector2(StudioMixerLabelWidth, Ui(42)), width - StudioMixerLabelWidth - Ui(4), state.OutputRight, 6);
        StudioMixerRow(origin, "Master volume", 57, studioSettings.MasterVolume, width, v => studioSettings = studioSettings with { MasterVolume = v });
        StudioMixerRow(origin, "Local listening", 86, studioSettings.MonitorVolume, width, SetStudioListeningVolume);
        StudioAt(origin, 0, 115);
        using (UiFonts.PushRelative(0.90f))
            ImGui.TextColored(state.Clipping ? Danger : MutedText, state.Clipping ? "Clipping · lower the volume" : "Local volume does not affect listeners.");
    }
    private void DrawStudioMonitoring(StudioSnapshot state)
    {
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        if (GameLayoutButton("Monitoring & settings", studioMonitoringExpanded ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.ChevronDown,
            width - Ui(34), height: 23)) studioMonitoringExpanded = !studioMonitoringExpanded;
        ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(25), 0));
        if (GameLayoutButton("", FontAwesomeIcon.EllipsisH, Ui(25), height: 23)) OpenMusicDialog("studioTools");
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Refresh devices or restart audio");
        if (!studioMonitoringExpanded) return;
        StudioAt(origin, 0, 39); ImGui.TextColored(MutedText, "Output device"); StudioAt(origin, 87, 34);
        var deviceWidth = width - Ui(87);
        DrawStudioDevice("studioOutputDevice", state.Outputs, studioSettings.OutputDevice, id => studioSettings = studioSettings with { OutputDevice = id }, deviceWidth);
        StudioAt(origin, 0, 71);
        var monitorMic = studioSettings.MonitorMic;
        if (ImGui.Checkbox("Hear microphone", ref monitorMic)) { studioSettings = studioSettings with { MonitorMic = monitorMic }; StudioSettingsChanged(); }
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Use headphones when monitoring your microphone.");
        ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(105), Ui(71)));
        if (GameLayoutButton("Edit scene", FontAwesomeIcon.SlidersH, Ui(105), height: 26)) musicTab = MusicTab.Scene;
    }
    private static string StudioTime(double seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:00}";
    private void DrawStudioQueue(float width, float height)
    {
        StudioPanel("##studioQueue", width, height, () =>
        {
            var origin = ImGui.GetCursorScreenPos(); var contentWidth = ImGui.GetContentRegionAvail().X;
            DrawStudioSourceSwitch(origin, contentWidth);
            if (StudioUsesApplication) { DrawStudioApplicationSource(origin, contentWidth, height); return; }
            StudioAt(origin, 0, 81);
            using (UiFonts.PushRelative(1f, emphasis: true)) ImGui.TextColored(Hex(0xE2E7EF), "Music queue");
            ImGui.SetCursorScreenPos(origin + new Vector2(contentWidth - Ui(216), Ui(76)));
            if (GameLayoutButton("Add songs", FontAwesomeIcon.Music, Ui(103), height: 28))
                OpenMusicLibrary(true);
            ImGui.SameLine(0, Ui(8)); if (GameLayoutButton("Library", FontAwesomeIcon.FolderOpen, Ui(105), height: 28)) OpenMusicLibrary(false);
            StudioAt(origin, 0, 112);
            var shortLabels = contentWidth < Ui(500);
            var saveLabel = shortLabels ? "Save" : "Save playlist";
            var loadLabel = shortLabels ? "Load" : "Load playlist";
            float playlistIconWidth;
            using (UiFonts.PushIcon())
                playlistIconWidth = MathF.Max(ImGui.CalcTextSize(FontAwesomeIcon.Save.ToIconString()).X,
                    ImGui.CalcTextSize(FontAwesomeIcon.FolderOpen.ToIconString()).X);
            var buttonWidth = MathF.Max(Ui(shortLabels ? 80 : 108),
                MathF.Max(ImGui.CalcTextSize(saveLabel).X, ImGui.CalcTextSize(loadLabel).X) + playlistIconWidth + Ui(32));
            var optionWidth = ImGui.CalcTextSize("Shuffle").X + ImGui.CalcTextSize("Repeat").X + ImGui.CalcTextSize("Remove on play").X +
                3 * (ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X) + 2 * ImGui.GetStyle().ItemSpacing.X;
            var stackedFooter = contentWidth < optionWidth + buttonWidth * 2 + Ui(20);
            var listHeight = height - Ui(stackedFooter ? 214 : 184);
            using (var list = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##studioSongs", new Vector2(contentWidth, listHeight))))
            {
                var snapshot = StudioState;
                if (Plugin.Cfg.StudioQueue.Count == 0)
                {
                    var center = ImGui.GetCursorScreenPos() + new Vector2(contentWidth / 2, listHeight / 2);
                    StudioMusicTile(center - UiVec(20, 44), UiVec(40, 40), false);
                    var message = "Build your music queue"; var hint = "Add songs from your music library to start your mix.";
                    var draw = ImGui.GetWindowDrawList(); draw.AddText(center + new Vector2(-ImGui.CalcTextSize(message).X / 2, Ui(9)), ImGui.GetColorU32(Hex(0xE2E7EF)), message);
                    using (UiFonts.PushRelative(0.90f))
                        draw.AddText(center + new Vector2(-ImGui.CalcTextSize(hint).X / 2, Ui(31)), ImGui.GetColorU32(MutedText), hint);
                }
                for (var i = 0; i < Plugin.Cfg.StudioQueue.Count; i++)
                {
                    var track = Plugin.Cfg.StudioQueue[i]; using var id = ImRaii.PushId(track.Id);
                    var row = ImGui.GetCursorScreenPos(); var available = ImGui.GetContentRegionAvail().X;
                    var current = track.Id == snapshot.DeckA.TrackId || track.Id == snapshot.DeckB.TrackId; var selected = track.Id == (studioSelectedTrackId ?? snapshot.TrackId); var draw = ImGui.GetWindowDrawList();
                    var rowEnd = row + new Vector2(available, Ui(44));
                    var hovered = ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(row, rowEnd);
                    // Keep the outline inside the list's clip rectangle. Native
                    // Selectable highlights otherwise paint over this custom card.
                    var cardMin = row + UiVec(1, 1);
                    var cardMax = rowEnd - UiVec(1, 1);
                    draw.AddRectFilled(cardMin, cardMax, hovered ? ImGui.GetColorU32(ImGuiCol.HeaderHovered) :
                        ImGui.GetColorU32(current ? Vector4.Lerp(CardBg, Accent, .12f) : FrameBg), Ui(5));
                    draw.AddRect(cardMin, cardMax, ImGui.GetColorU32(selected ? Accent : BorderSubtle), Ui(5));
                    // Selecting a row does not interrupt the current song.
                    if (ImGui.InvisibleButton("##song", new Vector2(available - Ui(172), Ui(44)))) SelectStudioSong(track.Id);
                    draw.AddText(row + UiVec(9, 14), ImGui.GetColorU32(MutedText), (i + 1).ToString());
                    StudioMusicTile(row + UiVec(29, 7), UiVec(30, 30), false);
                    StudioClippedText(row + UiVec(70, 6), available - Ui(248), track.Name);
                    var loaded = track.Id == snapshot.DeckA.TrackId ? "Loaded in A" : track.Id == snapshot.DeckB.TrackId ? "Loaded in B" : "Local file";
                    StudioClippedText(row + UiVec(70, 25), available - Ui(248), loaded, true);
                    ImGui.SetCursorScreenPos(row + new Vector2(available - Ui(172), Ui(7)));
                    if (WheelSecondaryButton("Load A", UiVec(64, 28))) StudioDeckAction("load-deck", 0, track.Id);
                    if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Load into Deck A without starting playback");
                    ImGui.SetCursorScreenPos(row + new Vector2(available - Ui(102), Ui(7)));
                    if (WheelSecondaryButton("Load B", UiVec(64, 28))) StudioDeckAction("load-deck", 1, track.Id);
                    if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Load into Deck B without starting playback");
                    ImGui.SetCursorScreenPos(row + new Vector2(available - Ui(30), Ui(7)));
                    var menuAnchor = ImGui.GetCursorScreenPos();
                    if (GameLayoutButton("", FontAwesomeIcon.EllipsisH, Ui(26), height: 28))
                    { studioMenuTrackId = track.Id; studioMenuAnchor = menuAnchor; openStudioTrackMenu = true; }
                    if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Song options");
                    ImGui.SetCursorScreenPos(row + new Vector2(0, Ui(46)));
                }
            }
            var footer = origin + new Vector2(0, height - Ui(stackedFooter ? 87 : 57));
            var drawFooter = ImGui.GetWindowDrawList(); drawFooter.AddLine(footer - UiVec(0, 7), footer + new Vector2(contentWidth, -Ui(7)), ImGui.GetColorU32(BorderSubtle));
            ImGui.SetCursorScreenPos(footer);
            var shuffle = studioSettings.Shuffle; var repeat = studioSettings.Repeat; var remove = studioSettings.RemoveOnPlay;
            var changed = ImGui.Checkbox("Shuffle", ref shuffle); ImGui.SameLine(); changed |= ImGui.Checkbox("Repeat", ref repeat);
            ImGui.SameLine(); changed |= ImGui.Checkbox("Remove on play", ref remove);
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Remove songs from the queue when playback starts. Loaded decks keep playing.");
            if (changed) { studioSettings = studioSettings with { Shuffle = shuffle, Repeat = repeat, RemoveOnPlay = remove }; StudioSettingsChanged(); }
            ImGui.SetCursorScreenPos(footer + new Vector2(contentWidth - buttonWidth * 2 - Ui(8), Ui(stackedFooter ? 30 : -2)));
            if (GameLayoutButton(shortLabels ? "Save" : "Save playlist", FontAwesomeIcon.Save, buttonWidth, height: 28)) { studioPlaylistName = ""; OpenMusicDialog("studioSave"); }
            ImGui.SameLine(0, Ui(8)); if (GameLayoutButton(shortLabels ? "Load" : "Load playlist", FontAwesomeIcon.FolderOpen, buttonWidth, height: 28)) OpenMusicDialog("studioLoad");
        });
    }
}

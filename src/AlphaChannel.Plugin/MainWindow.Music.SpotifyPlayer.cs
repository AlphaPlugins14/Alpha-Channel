using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private SpotifyClient? spotifyClient;
    private readonly SpotifyArtwork spotifyArtwork = new();
    private static readonly Vector4 SpotifyGreen = new(.114f, .725f, .329f, 1);
    private bool spotifySeeking;
    private float spotifySeek;

    private void DrawMusicSpotifyPage()
    {
        spotifyClient ??= new(Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName!, "Helpers", "Spotify", "AlphaChannel.SpotifyProbe.exe"));
        spotifyClient.Tick();
        var sample = spotifyClient.Snapshot;
        var player = new SpotifyPlayback(sample);
        spotifyArtwork.Update(sample.ArtworkBase64);
        var width = ImGui.GetContentRegionAvail().X;
        var top = ImGui.GetCursorScreenPos();
        DrawSpotifyMark(top + UiVec(16, 16), Ui(16));
        ImGui.SetCursorScreenPos(top + UiVec(43, 0));
        using (UiFonts.PushRelative(1.7f, emphasis: true)) ImGui.TextUnformatted("Spotify");
        ImGui.SameLine(0, Ui(18));
        var statusAt = ImGui.GetCursorScreenPos() + UiVec(0, 10);
        ImGui.GetWindowDrawList().AddCircleFilled(statusAt + UiVec(4, 4), Ui(4), ImGui.GetColorU32(player.Connected ? SpotifyGreen : MutedText));
        ImGui.SetCursorScreenPos(statusAt + UiVec(15, -3));
        using (UiFonts.PushRegularRelative(.95f)) ImGui.TextColored(player.Connected ? SpotifyGreen : MutedText,
            player.Connected ? "Connected" : spotifyClient.Loading ? "Connecting…" : "Not connected");
        ImGui.SetCursorScreenPos(top + new Vector2(Math.Max(Ui(190), width - Ui(140)), 0));
        if (GameLayoutButton("Open Spotify", FontAwesomeIcon.ExternalLinkAlt, Ui(140), height: 30))
            try { Dalamud.Utility.Util.OpenLink("spotify:"); } catch { musicMessage = "Could not open Spotify. Start the desktop app and play a song."; }
        ImGui.SetCursorScreenPos(top + UiVec(0, 46));
        if (musicMessage is { Length: > 0 } message)
        {
            using var wrap = ImRaii.TextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextWrapped(message);
            ImGui.Spacing();
        }
        using var green = ImRaii.PushColor(ImGuiCol.Button, SpotifyGreen).Push(ImGuiCol.ButtonHovered, new Vector4(.16f, .84f, .4f, 1))
            .Push(ImGuiCol.ButtonActive, new Vector4(.1f, .6f, .28f, 1)).Push(ImGuiCol.SliderGrab, SpotifyGreen)
            .Push(ImGuiCol.SliderGrabActive, SpotifyGreen);
        var wide = width >= Ui(540);
        var panelGap = Ui(10);
        var bodyStart = ImGui.GetCursorScreenPos();
        using (ImRaii.PushStyle(ImGuiStyleVar.DisabledAlpha, player.Connected ? ImGui.GetStyle().DisabledAlpha : 1f))
        using (UiDisabled.Scope(!player.Connected))
        {
        SpotifyPanel("player", width, Ui(wide ? 342 : 378), () =>
        {
            var origin = ImGui.GetCursorScreenPos();
            var art = Ui(wide ? 196 : 132);
            var draw = ImGui.GetWindowDrawList();
            if (spotifyArtwork.Texture is { } texture)
            {
                var imageSize = new Vector2(texture.Width, texture.Height);
                imageSize *= art / Math.Max(texture.Width, texture.Height);
                draw.AddRectFilled(origin, origin + new Vector2(art), ImGui.GetColorU32(Hex(0x151A18)), Ui(7));
                draw.AddImage(texture.Handle, origin + (new Vector2(art) - imageSize) / 2,
                    origin + (new Vector2(art) + imageSize) / 2);
                ImGui.Dummy(new Vector2(art));
            }
            else
            {
                draw.AddRectFilled(origin, origin + new Vector2(art), ImGui.GetColorU32(Hex(0x17251B)), Ui(6));
                UiFonts.DrawIcon(draw, Ui(42), origin + new Vector2(art / 2 - Ui(21)), ImGui.GetColorU32(SpotifyGreen), FontAwesomeIcon.Music.ToIconString());
                ImGui.Dummy(new Vector2(art));
            }
            var textAt = origin + new Vector2(art + Ui(20), 0);
            var textWidth = Math.Max(Ui(60), ImGui.GetContentRegionAvail().X - art - Ui(20));
            ImGui.SetCursorScreenPos(textAt);
            using (UiFonts.PushRegularRelative(.78f)) ImGui.TextColored(MutedText, "N O W  P L A Y I N G");
            ImGui.SetCursorScreenPos(textAt + UiVec(0, 23));
            using (UiFonts.PushRelative(1.9f, emphasis: true)) ImGui.TextUnformatted(TruncateToWidth(player.Text("Track / Title", "Unknown track"), textWidth));
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip(player.Text("Track / Title"));
            ImGui.SetCursorScreenPos(textAt + UiVec(0, 67));
            using (UiFonts.PushRegularRelative(1.1f)) ImGui.TextUnformatted(TruncateToWidth(player.Text("Track / Artist", "Unknown artist"), textWidth));
            using (UiFonts.PushRegularRelative(.96f))
            {
                DrawWrappedLines(draw, textAt + UiVec(0, 96), textWidth, ImGui.GetTextLineHeight(), 2,
                    ImGui.GetColorU32(MutedText), player.Text("Track / Album"), out _);
            }
            var controlsAt = wide ? textAt : origin;
            var controlsWidth = wide ? textWidth : ImGui.GetContentRegionAvail().X;
            var controlsY = origin.Y + Ui(wide ? 172 : 213);
            Vector2 ControlCenter(float fraction) => new(controlsAt.X + controlsWidth * fraction, controlsY);
            SpotifyControl(player, "Shuffle", FontAwesomeIcon.Random, "shuffle", ControlCenter(.08f), player.Flag("Playback / Shuffle active"));
            SpotifyControl(player, "Previous", FontAwesomeIcon.StepBackward, "previous", ControlCenter(.28f));
            SpotifyControl(player, "Play-pause toggle", spotifyOwnsStudio && player.Playing ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play, "toggle", ControlCenter(.5f), true);
            SpotifyControl(player, "Next", FontAwesomeIcon.StepForward, "next", ControlCenter(.72f));
            SpotifyControl(player, "Repeat", FontAwesomeIcon.Redo, "repeat", ControlCenter(.92f), player.Text("Playback / Repeat mode") is "Track" or "List");
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiFonts.SetTooltip("Repeat: " + player.Text("Playback / Repeat mode", "None"));
            DrawSpotifySeek(player, origin + UiVec(0, wide ? 218 : 254), ImGui.GetWindowWidth() - Ui(32));
            ImGui.SetCursorScreenPos(origin + UiVec(0, wide ? 254 : 290));
            DrawSpotifyCoverToggle();
        });
        SetGameLayoutRowGap(panelGap);
        DrawSpotifyUrlBar(width);
        SetGameLayoutRowGap(panelGap);
        var half = wide ? (width - panelGap) / 2 : width;
        var broadcastHeight = Ui(125);
        if (AccountBroadcastRestricted)
        {
            var contentWidth = MathF.Max(1, half - Ui(32));
            var messageHeight = ImGui.CalcTextSize(
                "Spotify broadcasting requires verification via XIVAuth or joining our Patreon.", false, contentWidth).Y;
            var buttonsWidth = ImGui.CalcTextSize("Verify with XIV Auth").X + ImGui.CalcTextSize("Join on Patreon").X +
                ImGui.GetStyle().FramePadding.X * 4 + Ui(8);
            var buttonRows = buttonsWidth <= contentWidth ? 1 : 2;
            broadcastHeight = Ui(32 + 28 + 16) + messageHeight +
                buttonRows * (ImGui.GetFrameHeight() + Ui(4));
        }
        using (ImRaii.Group()) SpotifyPanel("broadcast", half, broadcastHeight, () => DrawSpotifyBroadcast(player));
        if (wide) ImGui.SameLine(0, panelGap); else SetGameLayoutRowGap(panelGap);
        using (ImRaii.Group()) SpotifyPanel("scene", half, Ui(125), () =>
        {
            DrawSpotifyActionHeading("Your music scene", FontAwesomeIcon.Palette, false);
            using (UiFonts.PushRegularRelative(.9f)) ImGui.TextColored(MutedText, "Set the mood for your party.");
            ImGui.Dummy(UiVec(0, 3));
            if (SpotifyPrimaryButton("Customise scene", FontAwesomeIcon.Palette, ImGui.GetContentRegionAvail().X, outlined: true)) musicTab = MusicTab.Scene;
        });
        SetGameLayoutRowGap(panelGap);
        // Register the bottom gap as content so the page's scroll extent includes it.
        ImGui.Dummy(Vector2.Zero);
        }
        if (!player.Connected)
        {
            var afterBody = ImGui.GetCursorScreenPos();
            DrawSpotifyUnavailableOverlay(bodyStart, new Vector2(width, Math.Max(Ui(240), afterBody.Y - bodyStart.Y)));
            ImGui.SetCursorScreenPos(afterBody);
        }
    }

    private void DrawSpotifyUnavailableOverlay(Vector2 origin, Vector2 size)
    {
        ImGui.SetCursorScreenPos(origin);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using var overlay = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##spotifyUnavailableOverlay", size, false,
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse));
        if (!overlay) return;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(new Vector4(.025f, .04f, .03f, .82f)), Ui(9));
        var visibleStart = Vector2.Max(origin, draw.GetClipRectMin());
        var visibleEnd = Vector2.Min(origin + size, draw.GetClipRectMax());
        var available = visibleEnd - visibleStart;
        if (available.X < Ui(80) || available.Y < Ui(80)) return;
        var cardSize = new Vector2(Math.Min(Ui(500), available.X - Ui(24)), Math.Min(Ui(230), available.Y));
        var card = visibleStart + (available - cardSize) / 2;
        draw.AddRectFilled(card, card + cardSize, ImGui.GetColorU32(Hex(0x111D16)), Ui(14));
        draw.AddRect(card, card + cardSize, ImGui.GetColorU32(new Vector4(SpotifyGreen.X, SpotifyGreen.Y, SpotifyGreen.Z, .65f)), Ui(14));
        DrawSpotifyMark(card + new Vector2(cardSize.X / 2, Ui(38)), Ui(22));
        ImGui.SetCursorScreenPos(card + UiVec(24, 78));
        using (UiFonts.PushRelative(1.1f, emphasis: true))
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + cardSize.X - Ui(48));
            ImGui.TextWrapped("Spotify Desktop App must be running to access Spotify on Alpha Channel");
            ImGui.PopTextWrapPos();
        }
        var buttonWidth = Math.Min(Ui(245), cardSize.X - Ui(48));
        ImGui.SetCursorScreenPos(card + new Vector2((cardSize.X - buttonWidth) / 2, cardSize.Y - Ui(56)));
        using (UiDisabled.Scope(spotifyClient!.Loading))
            if (SpotifyPrimaryButton(spotifyClient.Loading ? "Checking for Spotify…" : "Check for Spotify",
                FontAwesomeIcon.SyncAlt, buttonWidth)) spotifyClient.Tick(force: true);
    }

    private void DrawSpotifyCoverToggle()
    {
        using var colors = SpotifyLibraryColors();
        var showCover = Plugin.Cfg.SpotifyCoverOnTv;
        if (ImGui.Checkbox("Show cover art on TV", ref showCover))
        {
            Plugin.Cfg.SpotifyCoverOnTv = showCover;
            Plugin.Cfg.Save();
        }
        using (UiFonts.PushRegularRelative(.86f))
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextColored(MutedText, "Replaces the scene's middle-row text while Spotify is active.");
            ImGui.PopTextWrapPos();
        }
    }

    private void DrawSpotifyBroadcast(SpotifyPlayback player)
    {
        if (AccountBroadcastRestricted)
        {
            DrawSpotifyActionHeading("Watch Party broadcast", FontAwesomeIcon.BroadcastTower, true);
            ImGui.Spacing();
            DrawAccountBroadcastRestriction("Spotify");
            return;
        }
        EnsureDjCredentialsForMusic();
        var live = spotifyOwnsStudio && studioBroadcastRequested;
        var starting = spotifyOwnsStudio && (spotifyShareAfterStart || live && !studioPublished);
        DrawSpotifyActionHeading("Watch Party broadcast", FontAwesomeIcon.BroadcastTower, true);
        using (UiFonts.PushRegularRelative(.9f))
            ImGui.TextColored(live && studioPublished ? SpotifyGreen : MutedText,
                live && studioPublished ? "Live · " + (StudioState.MusicLevel > .002f ? "Receiving audio" : "Waiting for Spotify audio") :
                starting ? "Connecting Spotify audio…" : "Share Spotify audio with your party.");
        var reason = stream.Mode == StreamMode.Viewing ? "Leave this Watch Party before broadcasting." :
            CurrentSession is null ? "Sign in before broadcasting." : !player.Connected ? "Open Spotify and play a song first." :
            studioSessionActive && !spotifyOwnsStudio ? "Stop Broadcast Studio before broadcasting Spotify." :
            !CanUseBroadcastStudio ? StudioUnavailableMessage :
            radioCredentials is null ? "Loading your broadcast connection settings…" :
            HalloweenActivityActive || hostTransferPreparing || stream.IsJoiningWatchParty ? "Finish the current activity first." : null;
        ImGui.Dummy(UiVec(0, 3));
        using (UiDisabled.Scope(!live && !starting && reason is not null))
            if (SpotifyPrimaryButton(spotifyOwnsStudio ? live ? "Stop broadcast" : starting ? "Cancel" : "Broadcast to party" : "Broadcast to party",
                spotifyOwnsStudio && (live || starting) ? FontAwesomeIcon.Stop : FontAwesomeIcon.BroadcastTower,
                ImGui.GetContentRegionAvail().X))
            {
                if (spotifyOwnsStudio && (live || starting)) StopStudioBroadcast();
                else StartSpotifyBroadcast();
            }
        if (reason is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiFonts.SetTooltip(reason);
        else if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Captures Spotify only. Use Spotify's own volume for local listening.");
    }

    private void SpotifyControl(SpotifyPlayback player, string capability, FontAwesomeIcon icon, string command, Vector2 center, bool active = false)
    {
        var primary = command == "toggle";
        var enabled = primary ? (spotifyOwnsStudio || CanStartSpotifyTv) &&
            (player.Enabled(capability) || player.Enabled(player.Playing ? "Pause" : "Play") || !spotifyOwnsStudio && player.Playing) : player.Enabled(capability);
        using var disabled = UiDisabled.Scope(!enabled || spotifyClient!.CommandPending);
        using var id = ImRaii.PushId("spotify" + command);
        var size = Ui(primary ? 56 : 40);
        ImGui.SetCursorScreenPos(center - new Vector2(size / 2));
        if (ImGui.InvisibleButton("control", new Vector2(size)))
        {
            if (primary) ToggleSpotifyTvPlayback(player);
            else spotifyClient?.SendCommand(command);
        }
        var draw = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsItemHovered();
        if (primary) draw.AddCircleFilled(center, size / 2, ImGui.GetColorU32(hovered ? new Vector4(.15f, .84f, .39f, 1) : SpotifyGreen));
        else if (hovered) draw.AddCircleFilled(center, size / 2, ImGui.GetColorU32(Hex(0x27332E)));
        UiFonts.DrawCenteredIcon(draw, Ui(primary ? 24 : 22), center + (primary && (!spotifyOwnsStudio || !player.Playing) ? UiVec(2, 0) : Vector2.Zero),
            ImGui.GetColorU32(primary ? Hex(0x07110A) : active ? SpotifyGreen : Hex(0xDEE5E1)), icon.ToIconString());
        if (command == "repeat" && player.Text("Playback / Repeat mode") == "Track")
            using (UiFonts.PushRegularRelative(.7f)) draw.AddText(center + UiVec(12, -15), ImGui.GetColorU32(SpotifyGreen), "1");
        if (active && !primary) draw.AddCircleFilled(center + UiVec(0, 18), Ui(2), ImGui.GetColorU32(SpotifyGreen));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiFonts.SetTooltip(primary ?
            !spotifyOwnsStudio ? CanStartSpotifyTv ? "Play on TV" : "Stop your current activity or leave the Watch Party before playing Spotify on TV." :
            player.Playing ? "Pause" : "Play" : capability);
    }

    private void DrawSpotifySeek(SpotifyPlayback player, Vector2 origin, float width)
    {
        var duration = (float)player.Duration;
        var canSeek = player.Enabled("Seek") && duration > 0 && !spotifyClient!.CommandPending;
        if (!canSeek) spotifySeeking = false;
        if (!spotifySeeking) spotifySeek = (float)player.Position(DateTime.UtcNow);
        var barAt = origin + UiVec(51, 0);
        var barWidth = Math.Max(Ui(20), width - Ui(102));
        ImGui.SetCursorScreenPos(barAt);
        using var disabled = UiDisabled.Scope(!canSeek);
        ImGui.InvisibleButton("##spotifySeek", new Vector2(barWidth, Ui(24)));
        if (canSeek && ImGui.IsItemActive())
        {
            spotifySeeking = true;
            spotifySeek = Math.Clamp((ImGui.GetIO().MousePos.X - barAt.X) / barWidth, 0, 1) * duration;
        }
        if (canSeek && spotifySeeking && ImGui.IsItemDeactivated())
        { spotifyClient!.SendCommand("seek", spotifySeek + player.Start); spotifySeeking = false; }
        if (canSeek && ImGui.IsItemFocused())
        {
            var keyboardPosition = ImGui.IsKeyPressed(ImGuiKey.LeftArrow) ? Math.Max(0, spotifySeek - 5) :
                ImGui.IsKeyPressed(ImGuiKey.RightArrow) ? Math.Min(duration, spotifySeek + 5) :
                ImGui.IsKeyPressed(ImGuiKey.Home) ? 0 : ImGui.IsKeyPressed(ImGuiKey.End) ? duration : spotifySeek;
            if (keyboardPosition != spotifySeek)
            { spotifySeek = keyboardPosition; spotifyClient!.SendCommand("seek", spotifySeek + player.Start); }
        }
        var draw = ImGui.GetWindowDrawList();
        var progress = duration <= 0 ? 0 : Math.Clamp(spotifySeek / duration, 0, 1);
        var center = barAt + UiVec(0, 12);
        draw.AddRectFilled(center - UiVec(0, 3), center + new Vector2(barWidth, Ui(3)), ImGui.GetColorU32(Hex(0x40514D)), Ui(3));
        draw.AddRectFilled(center - UiVec(0, 3), center + new Vector2(barWidth * progress, Ui(3)), ImGui.GetColorU32(SpotifyGreen), Ui(3));
        draw.AddCircleFilled(center + new Vector2(barWidth * progress, 0), Ui(6), ImGui.GetColorU32(SpotifyGreen));
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Seek to " + SpotifyPlayback.Clock(Math.Clamp((ImGui.GetIO().MousePos.X - barAt.X) / barWidth, 0, 1) * duration));
        using var font = UiFonts.PushRegularRelative(.85f);
        var total = SpotifyPlayback.Clock(duration);
        draw.AddText(origin + UiVec(0, 5), ImGui.GetColorU32(MutedText), SpotifyPlayback.Clock(spotifySeek));
        draw.AddText(origin + new Vector2(width - ImGui.CalcTextSize(total).X, Ui(5)), ImGui.GetColorU32(MutedText), total);
    }

    private void DrawSpotifyMark(Vector2 center, float radius)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.AddCircleFilled(center, radius, ImGui.GetColorU32(SpotifyGreen));
        for (var row = 0; row < 3; row++)
            for (var segment = 0; segment < 12; segment++)
            {
                Vector2 Point(int n)
                {
                    var x = -0.64f + n / 12f * (1.28f - row * .09f);
                    return center + radius * new Vector2(x, -.36f + row * .34f + x * x * .34f);
                }
                draw.AddLine(Point(segment), Point(segment + 1), ImGui.GetColorU32(Hex(0x08150D)), radius * .13f);
            }
    }

    private void DrawSpotifyActionHeading(string label, FontAwesomeIcon icon, bool broadcast)
    {
        var at = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddCircleFilled(at + UiVec(14, 14), Ui(14), ImGui.GetColorU32(Hex(0x23312C)));
        UiFonts.DrawCenteredIcon(draw, Ui(17), at + UiVec(14, 14), ImGui.GetColorU32(broadcast ? SpotifyGreen : MutedText), icon.ToIconString());
        using (UiFonts.PushRelative(1.08f, emphasis: true))
            draw.AddText(at + UiVec(39, 5), ImGui.GetColorU32(Hex(0xE8EDEA)), TruncateToWidth(label, ImGui.GetContentRegionAvail().X - Ui(39)));
        ImGui.Dummy(UiVec(0, 28));
    }

    private bool SpotifyPrimaryButton(string label, FontAwesomeIcon icon, float width, bool outlined = false)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, Ui(36));
        using var colors = ImRaii.PushColor(ImGuiCol.Text, outlined ? Hex(0xE0E6E3) : Hex(0x07110A))
            .Push(ImGuiCol.Button, outlined ? Hex(0x141C19) : SpotifyGreen)
            .Push(ImGuiCol.ButtonHovered, outlined ? Hex(0x26312C) : new Vector4(.15f, .84f, .39f, 1));
        var clicked = ImGui.Button("##spotifyPrimary", size);
        var labelSize = ImGui.CalcTextSize(label);
        var x = origin.X + Math.Max(Ui(4), (width - labelSize.X - Ui(24)) / 2);
        var draw = ImGui.GetWindowDrawList();
        if (outlined) draw.AddRect(origin, origin + size, ImGui.GetColorU32(MutedText), Ui(7));
        draw.PushClipRect(origin, origin + size, true);
        UiFonts.DrawCenteredIcon(draw, ImGui.GetFontSize(), new Vector2(x + Ui(8), origin.Y + size.Y / 2), ImGui.GetColorU32(ImGuiCol.Text), icon.ToIconString());
        draw.AddText(new Vector2(x + Ui(24), origin.Y + (size.Y - labelSize.Y) / 2), ImGui.GetColorU32(ImGuiCol.Text), label);
        draw.PopClipRect();
        return clicked;
    }

    private void SpotifyPanel(string id, float width, float height, Action content)
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(16, 16));
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8, 4));
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(9));
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, Hex(0x111917)).Push(ImGuiCol.Border, Hex(0x2D4038));
        using var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##spotify" + id, new Vector2(width, height), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse));
        if (panel) content();
    }

    private void DisposeSpotifyPage() { spotifyCoverPublisher.Dispose(); spotifyClient?.Dispose(); spotifyArtwork.Dispose(); }
}

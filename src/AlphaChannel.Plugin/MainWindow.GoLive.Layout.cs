using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.BroadcastAllowance;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawGoLive()
    {
        if (CurrentSession is not { } session)
        {
            DrawPlainEmpty("Sign in to connect your streaming app and broadcast to your TV.", "Open Settings", () => currentPage = HomePage.Settings);
            return;
        }
        if (liveStatusDirty && !liveStatusLoading) RefreshLiveStatus(session);
        using var content = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##goLiveContent", new(-1, -1), false));
        if (!content) return;
        if (resetGoLiveScroll)
        {
            ImGui.SetScrollY(0);
            resetGoLiveScroll = false;
        }
        using (UiFonts.PushRegularRelative(.78f)) ImGui.TextColored(AccentHover, "LIVE STREAMING");
        using (UiFonts.PushRelative(1.6f, emphasis: true))
        using (ImRaii.TextWrapPos(0)) ImGui.TextUnformatted("Your computer. Your broadcast.");
        using (UiFonts.PushRegularRelative(.88f))
        using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText, "Stream from OBS Studio or another RTMP-compatible app.");
        SetGameLayoutRowGap(Ui(16));
        var broadcasting = stream.Mode == StreamMode.Hosting && queue.Current is { IsTransient: true, Source: "Live Stream" };
        if (!AccountBroadcastRestricted)
            DrawLiveHubWorkflow(!string.IsNullOrWhiteSpace(Plugin.Cfg.StreamKeys.GetValueOrDefault(session.AccountId)), broadcasting);
        SetGameLayoutRowGap(Ui(14));
        if (!AccountBroadcastRestricted) DrawLiveHubStatus(session, broadcasting);
        SetGameLayoutRowGap(Ui(14));
        if (!AccountBroadcastRestricted) DrawLiveHubAllowance();
        SetGameLayoutRowGap(Ui(14));
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var stacked = width < Ui(640);
        var gap = Ui(14);
        var leftWidth = stacked ? width : (width - gap) * .56f;
        var rightWidth = stacked ? width : width - leftWidth - gap;
        var height = Ui(304);
        DrawPlayerHubPanel("##liveHubConnection", leftWidth, height, false, () => DrawLiveHubConnection(session));
        ImGui.SetCursorScreenPos(origin + new Vector2(stacked ? 0 : leftWidth + gap, stacked ? height + gap : 0));
        DrawPlayerHubPanel("##liveHubSoftware", rightWidth, height, false, DrawLiveHubSoftware);
        ImGui.SetCursorScreenPos(origin + new Vector2(0, stacked ? 2 * height + gap : height));
        ImGui.Dummy(new(width, Ui(10)));
    }

    private void DrawLiveHubWorkflow(bool hasKey, bool broadcasting)
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var current = !hasKey ? 1 : obsConnectionOnline || broadcasting ? 3 : 2;
        var draw = ImGui.GetWindowDrawList();
        ImGui.Dummy(new(width, Ui(58)));
        for (var i = 0; i < 3; i++)
        {
            var center = origin + new Vector2(width * (1 + i * 2) / 6, Ui(15));
            if (i < 2)
                draw.AddLine(center + UiVec(16, 0), center + new Vector2(width / 3 - Ui(16), 0),
                    ImGui.GetColorU32(i + 1 < current ? Accent : BorderSubtle), Ui(2));
            var completed = i + 1 < current || broadcasting;
            draw.AddCircleFilled(center, Ui(14), ImGui.GetColorU32(i + 1 <= current ? Accent : HubSurface(0x14223A)), 32);
            if (completed) UiFonts.DrawCenteredIcon(draw, Ui(13), center, ImGui.GetColorU32(Vector4.One), FontAwesomeIcon.Check.ToIconString());
            else
            {
                var number = (i + 1).ToString();
                draw.AddText(center - ImGui.CalcTextSize(number) / 2, ImGui.GetColorU32(Vector4.One), number);
            }
            var label = i == 0 ? "Connect app" : i == 1 ? "Start streaming" : "Broadcast to TV";
            using (UiFonts.PushRegularRelative(.86f))
                draw.AddText(center + new Vector2(-ImGui.CalcTextSize(label).X / 2, Ui(23)),
                    ImGui.GetColorU32(i + 1 == current ? AccentHover : MutedText), label);
        }
    }

    private void DrawLiveHubStatus(CharacterSession session, bool broadcasting)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var stacked = width < Ui(620);
        var helper = broadcasting ? "Your broadcast is active. Use the player controls to manage playback."
            : obsConnectionChecking ? "This normally takes only a few seconds."
            : !string.IsNullOrWhiteSpace(obsConnectionError) ? obsConnectionError
            : obsConnectionOnline ? "Your incoming stream is ready to broadcast."
            : "Start streaming in your app, then check the connection.";
        float helperHeight;
        using (UiFonts.PushRegularRelative(.82f)) helperHeight = ImGui.CalcTextSize(helper, false, MathF.Max(1, width - Ui(36))).Y;
        var height = Ui(stacked ? 164 : 112) + helperHeight;
        DrawPlayerHubPanel("##liveHubStatus", width, height, true, () =>
        {
            var origin = ImGui.GetCursorScreenPos();
            var innerWidth = ImGui.GetContentRegionAvail().X;
            var textWidth = stacked ? innerWidth : MathF.Max(1, innerWidth - Ui(326));
            var draw = ImGui.GetWindowDrawList();
            draw.AddCircleFilled(origin + UiVec(7, 9), Ui(6), ImGui.GetColorU32(broadcasting || obsConnectionOnline ? Good : MutedText), 24);
            var heading = broadcasting ? "Broadcasting to TV" : obsConnectionChecking ? "Checking connection" : obsConnectionOnline ? "Stream detected" : "Waiting for stream";
            using (UiFonts.PushRelative(1.08f, emphasis: true))
                DrawWrappedLines(draw, origin + UiVec(22, 0), MathF.Max(1, textWidth - Ui(22)), ImGui.GetTextLineHeight(), 2, ImGui.GetColorU32(Vector4.One), heading);
            using (UiFonts.PushRegularRelative(.9f))
                DrawWrappedLines(draw, origin + UiVec(0, 34), textWidth, ImGui.GetTextLineHeight(), 2, ImGui.GetColorU32(MutedText),
                    broadcasting ? "Your stream is playing on the TV." : obsConnectionChecking ? "Looking for an incoming stream…"
                    : obsConnectionOnline ? "Incoming stream detected" : "No incoming stream detected");
            ImGui.SetCursorScreenPos(origin + UiVec(0, 76));
            using (UiFonts.PushRegularRelative(.82f))
            using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText, helper);
            ImGui.SetCursorScreenPos(origin + new Vector2(stacked ? 0 : innerWidth - Ui(316), stacked ? Ui(90) + helperHeight : Ui(26)));
            var buttonWidth = stacked ? (innerWidth - Ui(8)) / 2 : Ui(154);
            using (UiDisabled.Scope(obsConnectionChecking))
                if (GameLayoutButton(obsConnectionChecking ? "Checking…" : "Check connection", FontAwesomeIcon.SyncAlt, buttonWidth, primary: !obsConnectionOnline && !broadcasting, height: 38)) CheckObsConnection(session);
            ImGui.SameLine(0, Ui(8));
            using (UiDisabled.Scope(!obsConnectionOnline || stream.Mode == StreamMode.Viewing || broadcasting))
                if (GameLayoutButton(broadcasting ? "Broadcasting" : "Broadcast to TV", FontAwesomeIcon.BroadcastTower, buttonWidth, primary: obsConnectionOnline && !broadcasting, height: 38)) BeginLiveWatchPartyBroadcast(session);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                if (stream.Mode == StreamMode.Viewing) UiFonts.SetTooltip("Leave your current Watch Party before broadcasting.");
                else if (!obsConnectionOnline && !broadcasting) UiFonts.SetTooltip("Start streaming from your broadcasting app first.");
            }
        });
    }

    private void DrawLiveHubAllowance()
    {
        DrawAccountBroadcastRestriction("OBS");
        var allowance = BroadcastAllowanceDisplayFor(BroadcastAllowanceMethod.Obs,
            Math.Max(0, Interlocked.CompareExchange(ref obsMeasuredBitrateMbps, 0, 0)));
        var width = ImGui.GetContentRegionAvail().X;
        DrawPlayerHubPanel("##liveHubAllowance", width, Ui((width < Ui(540) ? 166 : 136) + (ConfiguredPatreonTier() == 0 ? 28 : 0)), false, () =>
        {
            var innerWidth = ImGui.GetContentRegionAvail().X;
            using (UiFonts.PushRelative(1.02f, emphasis: true)) ImGui.TextUnformatted("Broadcast allowance");
            using (UiFonts.PushRegularRelative(.84f))
            {
                if (width >= Ui(540))
                {
                    ImGui.SameLine();
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(allowance.RemainingLabel).X));
                }
                ImGui.TextColored(MutedText, allowance.RemainingLabel);
            }
            SetGameLayoutRowGap(Ui(8));
            using (ImRaii.PushColor(ImGuiCol.PlotHistogram, Accent).Push(ImGuiCol.FrameBg, AccentPanelBg))
                ImGui.ProgressBar(allowance.Remaining, new(innerWidth, Ui(10)), string.Empty);
            SetGameLayoutRowGap(Ui(8));
            using (UiFonts.PushRegularRelative(.76f))
            using (ImRaii.TextWrapPos(0))
            {
                var tier = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(allowance.Heading.ToLowerInvariant());
                ImGui.TextColored(allowance.Warning ? PatreonOrange : MutedText, $"{tier} · {allowance.Detail}");
                ImGui.TextColored(MutedText, "Lower video bitrates use less allowance.");
                DrawPatreonUpgradeLink();
            }
        });
    }

    private void DrawLiveHubConnection(CharacterSession session)
    {
        using (UiFonts.PushRelative(1.08f, emphasis: true)) ImGui.TextUnformatted("Connect your streaming app");
        if (AccountBroadcastRestricted)
        {
            ImGui.Spacing();
            DrawAccountBroadcastRestriction("OBS");
            return;
        }
        using (UiFonts.PushRegularRelative(.82f))
        using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText, "Use Custom RTMP in your broadcasting software.");
        SetGameLayoutRowGap(Ui(16));
        DrawLiveHubCopyField("Server URL", BuildRtmpServer(), "server", false);
        SetGameLayoutRowGap(Ui(12));
        var key = Plugin.Cfg.StreamKeys.GetValueOrDefault(session.AccountId);
        if (!string.IsNullOrWhiteSpace(key)) DrawLiveHubCopyField("Stream key", key, "key", true);
        else
        {
            using (UiFonts.PushRegularRelative(.86f))
            using (ImRaii.TextWrapPos(0)) ImGui.TextColored(Gold, "No stream key is saved on this installation. Generate one from Settings > Account.");
        }
        SetGameLayoutRowGap(Ui(14));
        using (UiFonts.PushIcon()) ImGui.TextColored(Gold, FontAwesomeIcon.Lock.ToIconString());
        ImGui.SameLine(0, Ui(7));
        using (UiFonts.PushRegularRelative(.8f)) ImGui.TextColored(Gold, "Keep your stream key private.");
        SetGameLayoutRowGap(Ui(14));
        using (UiFonts.PushRegularRelative(.8f))
        using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText, "Copy these details into your app, then start streaming.");
    }

    private void DrawLiveHubCopyField(string label, string value, string id, bool secret)
    {
        using var scope = ImRaii.PushId(id);
        using (UiFonts.PushRegularRelative(.82f)) ImGui.TextColored(MutedText, label);
        var display = secret && !streamKeyRevealed ? new string('•', Math.Min(value.Length, 24)) : value;
        var available = ImGui.GetContentRegionAvail().X;
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7))
                   .Push(ImGuiStyleVar.FramePadding, new Vector2(Ui(10), MathF.Max(1, (Ui(36) - ImGui.GetFontSize()) / 2))))
        {
            ImGui.SetNextItemWidth(MathF.Max(1, available - Ui(secret ? 86 : 44)));
            ImGui.InputText("##value", ref display, Math.Max(512, value.Length + 1), ImGuiInputTextFlags.ReadOnly);
        }
        if (!secret && ImGui.IsItemHovered()) UiFonts.SetTooltip(value);
        if (secret)
        {
            ImGui.SameLine(0, Ui(6));
            using (ImRaii.PushId("reveal"))
                if (GameLayoutButton(string.Empty, streamKeyRevealed ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye, Ui(36), height: 36)) streamKeyRevealed = !streamKeyRevealed;
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip(streamKeyRevealed ? "Hide stream key" : "Reveal stream key");
        }
        ImGui.SameLine(0, Ui(6));
        using (ImRaii.PushId("copy"))
            if (GameLayoutButton(string.Empty, FontAwesomeIcon.Copy, Ui(38), height: 36)) ImGui.SetClipboardText(value);
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip(secret ? "Copy stream key" : "Copy server URL");
    }

    private void DrawLiveHubSoftware()
    {
        using (UiFonts.PushRelative(1.08f, emphasis: true)) ImGui.TextUnformatted("Streaming software");
        if (AccountBroadcastRestricted)
        {
            ImGui.Spacing();
            using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText,
                "Use OBS Studio or another RTMP-compatible app after unlocking broadcasting. Connection details and setup guides will appear here.");
            return;
        }
        SetGameLayoutRowGap(Ui(14));
        using (UiFonts.PushIcon()) ImGui.TextColored(Accent, FontAwesomeIcon.Desktop.ToIconString());
        ImGui.SameLine(0, Ui(8));
        ImGui.TextUnformatted("OBS Studio");
        using (UiFonts.PushRegularRelative(.7f)) ImGui.TextColored(AccentHover, "RECOMMENDED");
        SetGameLayoutRowGap(Ui(8));
        using (UiFonts.PushRegularRelative(.82f))
        using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText, "Free, widely supported and easy to configure.");
        SetGameLayoutRowGap(Ui(12));
        if (GameLayoutButton("OBS setup guide", FontAwesomeIcon.BookOpen, ImGui.GetContentRegionAvail().X, primary: true, height: 38))
        {
            obsSetupGuideStep = 0;
            liveStreamGuideKind = LiveStreamGuideKind.ObsStudio;
        }
        SetGameLayoutRowGap(Ui(10));
        using (UiFonts.PushRegularRelative(.88f))
        {
            const string label = "Other streaming apps →";
            var origin = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton("##otherLiveApps", new(ImGui.CalcTextSize(label).X + Ui(8), Ui(26))))
            {
                genericStreamGuideStep = 0;
                liveStreamGuideKind = LiveStreamGuideKind.GenericRtmp;
            }
            ImGui.GetWindowDrawList().AddText(origin + UiVec(0, 4), ImGui.GetColorU32(ImGui.IsItemHovered() ? AccentHover : Accent), label);
            if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        SetGameLayoutRowGap(Ui(14));
        using (UiFonts.PushRegularRelative(.8f))
        using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText, "Works with apps that support a custom RTMP server and stream key.");
    }
}

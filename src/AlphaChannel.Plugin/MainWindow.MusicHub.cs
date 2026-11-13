using AlphaChannel.Plugin.BroadcastAllowance;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly (MusicTab Tab, string Label, FontAwesomeIcon Icon)[] MusicHubTabs =
    [
        (MusicTab.Dj, "DJ Live", FontAwesomeIcon.BroadcastTower),
        (MusicTab.Radio, "Radio", FontAwesomeIcon.Music),
        (MusicTab.Spotify, "Spotify", FontAwesomeIcon.Headphones),
        (MusicTab.Scene, "Scene", FontAwesomeIcon.Palette),
        (MusicTab.Studio, "Studio", FontAwesomeIcon.Microphone)
    ];

    private void DrawMusicHubPage()
    {
        EnsureMusicScene();
        UpdateDjConnectionStatus();
        if (stream.Mode != StreamMode.Hosting) djBroadcastingToWatchParty = false;
        using var id = ImRaii.PushId("musicHub");
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8, 8));
        using var frames = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(10, 6)).Push(ImGuiStyleVar.FrameRounding, Ui(7));
        using var colors = ImRaii.PushColor(ImGuiCol.FrameBg, FrameBg).Push(ImGuiCol.FrameBgHovered, FrameBgHover)
            .Push(ImGuiCol.FrameBgActive, AccentFieldActive).Push(ImGuiCol.CheckMark, Accent);
        DrawMusicHubBanner();
        SetGameLayoutRowGap(Ui(12));
        DrawMusicHubTabs();
        SetGameLayoutRowGap(Ui(14));
        var width = ImGui.GetContentRegionAvail().X;
        switch (musicTab)
        {
            case MusicTab.Dj: DrawMusicHubDj(width); break;
            case MusicTab.Radio: DrawMusicRadioPage(width); break;
            case MusicTab.Spotify: DrawMusicSpotifyPage(); break;
            case MusicTab.Scene: DrawMusicScenePage(width, Ui(14)); break;
            case MusicTab.Studio: DrawBroadcastStudio(width, Ui(14)); DrawStudioTrackMenu(); break;
        }
        if (musicTab != MusicTab.Spotify && musicMessage is { } message) ImGui.TextWrapped(message);
    }

    private void DrawMusicHubBanner()
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var artwork = width >= Ui(620);
        var size = new Vector2(width, Ui(148));
        ImGui.Dummy(size);
        var draw = ImGui.GetWindowDrawList();
        DrawArcadeGradientPanel(draw, origin, size, HubSurface(0x0D203B), HubSurface(0x050B17), Accent, 10);
        draw.PushClipRect(origin + UiVec(1, 1), origin + size - UiVec(1, 1), true);
        var textWidth = width - Ui(artwork ? 285 : 40);
        using (UiFonts.PushRegularRelative(.8f))
            draw.AddText(origin + UiVec(20, 20), ImGui.GetColorU32(AccentHover), "F I N D  Y O U R  F R E Q U E N C Y");
        using (UiFonts.PushRelative(1.95f, emphasis: true))
            DrawWrappedLines(draw, origin + UiVec(20, 45), textWidth, ImGui.GetTextLineHeight(), 2,
                ImGui.GetColorU32(Vector4.One), "Your sound. Your scene.", out _);
        using (UiFonts.PushRegularRelative(1f))
            DrawWrappedLines(draw, origin + UiVec(20, 105), textWidth, ImGui.GetTextLineHeight(), 2,
                ImGui.GetColorU32(MutedText), "Listen to radio, broadcast your mix or build your music stage.", out _);
        if (artwork) DrawMusicHubDecks(draw, origin + new Vector2(width - Ui(263), Ui(17)), Ui(1));
        draw.PopClipRect();
    }

    private void DrawMusicHubTabs()
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var tabWidth = width / MusicHubTabs.Length;
        var draw = ImGui.GetWindowDrawList();
        for (var i = 0; i < MusicHubTabs.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, 0);
            var tab = MusicHubTabs[i];
            var position = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton("tab" + tab.Tab, new Vector2(tabWidth, Ui(42)))) musicTab = tab.Tab;
            var selected = musicTab == tab.Tab;
            var color = ImGui.GetColorU32(selected ? AccentHover : MutedText);
            using (UiFonts.PushRelative(.96f, emphasis: selected))
            {
                var showIcon = tabWidth >= ImGui.CalcTextSize(tab.Label).X + Ui(40);
                var label = TruncateToWidth(tab.Label, MathF.Max(1, tabWidth - Ui(showIcon ? 40 : 12)));
                var textSize = ImGui.CalcTextSize(label);
                var x = position.X + (tabWidth - textSize.X - Ui(showIcon ? 25 : 0)) / 2;
                if (showIcon) UiFonts.DrawIcon(draw, ImGui.GetFontSize(), new Vector2(x, position.Y + Ui(12)), color, tab.Icon.ToIconString());
                draw.AddText(new Vector2(x + Ui(showIcon ? 25 : 0), position.Y + Ui(12)), color, label);
            }
            if (selected) draw.AddRectFilled(position + UiVec(0, 39), position + new Vector2(tabWidth, Ui(42)), ImGui.GetColorU32(Accent), Ui(1));
        }
        draw.AddLine(origin + UiVec(0, 42), origin + new Vector2(width, Ui(42)), ImGui.GetColorU32(BorderSubtle));
    }

    private void DrawMusicHubDj(float width)
    {
        EnsureDjCredentialsForMusic();
        var sideBySide = width >= Ui(650);
        var left = sideBySide ? (width - Ui(12)) * .57f : width;
        var right = sideBySide ? width - left - Ui(12) : width;
        var height = Ui(left < Ui(350) ? 430 : 370);
        using (ImRaii.Group()) MusicHubPanel("broadcast", left, height, DrawMusicHubBroadcast);
        if (sideBySide) ImGui.SameLine(0, Ui(12)); else SetGameLayoutRowGap(Ui(12));
        using (ImRaii.Group()) MusicHubPanel("stage", right, sideBySide ? height : Ui(300), () =>
        {
            DrawMusicDjHeading("Your music stage", FontAwesomeIcon.Palette);
            using (UiFonts.PushRegularRelative(.9f))
            using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText, "What your party sees while you're live.");
            ImGui.Dummy(UiVec(0, 4));
            var preview = ImGui.GetCursorScreenPos();
            var previewHeight = sideBySide ? height - Ui(145) : Ui(165);
            DrawDjStagePreview(ImGui.GetContentRegionAvail().X, previewHeight);
            using (UiFonts.PushRegularRelative(.76f))
                ImGui.GetWindowDrawList().AddText(preview + UiVec(8, 7), ImGui.GetColorU32(MutedText), "Scene preview");
            if (GameLayoutButton("Customise scene", FontAwesomeIcon.Palette, ImGui.GetContentRegionAvail().X, height: 36)) musicTab = MusicTab.Scene;
        });
        SetGameLayoutRowGap(Ui(12));
        DrawMusicHubSupport(width);
    }

    private void DrawMusicHubBroadcast()
    {
        if (AccountBroadcastRestricted)
        {
            DrawMusicDjHeading("Your broadcast", FontAwesomeIcon.BroadcastTower);
            ImGui.Spacing();
            DrawAccountBroadcastRestriction("DJ", "Broadcasting requires XIVAuth verification or Patreon");
            return;
        }
        var broadcasting = djBroadcastingToWatchParty && IsPlayingOwnIcecastDjStream;
        var checking = djConnectionCheckInProgress || djConnectionSearchActive;
        DrawMusicDjHeading("Your broadcast", FontAwesomeIcon.BroadcastTower,
            broadcasting ? "Live" : checking ? "Checking…" : djIsOnAir ? "Connected" : "Not connected", djIsOnAir ? Good : MutedText);
        using (UiFonts.PushRegularRelative(.9f))
        using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText, broadcasting ? "Your DJ stream is playing in your Watch Party." :
            djIsOnAir ? "Your DJ software is connected and ready to broadcast." : "Connect your DJ software to get started.");
        ImGui.Dummy(UiVec(0, 4));
        ImGui.TextColored(MutedText, "Stream name");
        var name = Plugin.Cfg.DjBroadcastName ?? "Live DJ broadcast";
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##streamName", ref name, 101)) Plugin.Cfg.DjBroadcastName = name;
        if (ImGui.IsItemDeactivatedAfterEdit()) Plugin.Cfg.Save();
        ImGui.TextColored(MutedText, "Local playback level");
        DrawMusicHubMeter();
        var signedIn = CurrentSession is not null;
        var viewing = stream.Mode == StreamMode.Viewing;
        // Connection details remain a separate, fixed action. The primary
        // button owns the check -> ready -> broadcasting progression.
        using (UiDisabled.Scope(djCredentialsLoading))
            if (GameLayoutButton("Connect DJ software", FontAwesomeIcon.Plug, ImGui.GetContentRegionAvail().X, height: 38))
            {
                if (!signedIn) currentPage = HomePage.Settings;
                else if (radioCredentials is not null) OpenMusicDialog("connection");
                else { musicDjNextCredentialAttemptUtc = DateTime.MinValue; EnsureDjCredentialsForMusic(); }
            }
        if (djCredentialsLoading && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiFonts.SetTooltip("Loading your connection settings…");
        var primary = broadcasting ? "Stop Broadcasting" : djIsOnAir ? "Start Broadcasting" : checking ? "Checking connection…" : "Check connection";
        using (UiDisabled.Scope(!broadcasting && (viewing || !signedIn || djCredentialsLoading || radioCredentials is null || !djIsOnAir && checking)))
            if (GameLayoutButton(primary, broadcasting ? FontAwesomeIcon.Stop : djIsOnAir ? FontAwesomeIcon.Play : FontAwesomeIcon.BroadcastTower,
                    ImGui.GetContentRegionAvail().X, true, broadcasting, 38))
            {
                if (broadcasting) { EndPlaybackAndDespawnTv(); if (!studioBroadcastRequested) djBroadcastingToWatchParty = false; }
                else if (djIsOnAir && radioCredentials is { } radio) BeginDjWatchPartyBroadcast(radio);
                else RequestDjConnectionCheck();
            }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            if (viewing) UiFonts.SetTooltip("Leave your current Watch Party before broadcasting.");
            else if (!signedIn) UiFonts.SetTooltip("Sign in to check your DJ connection.");
            else if (!djIsOnAir && checking) UiFonts.SetTooltip("Checking for your DJ stream. This button becomes Start Broadcasting once connected.");
        }
        DrawMusicConnectionFlash();
        using (UiFonts.PushRegularRelative(.82f))
        using (ImRaii.TextWrapPos(0))
        {
            if (radioError is { Length: > 0 }) ImGui.TextColored(Danger, radioError);
            else ImGui.TextColored(MutedText, broadcasting && Plugin.Cfg.Muted
                ? "Your local monitor is muted to avoid the delayed return feed."
                : "After connecting you'll be able to start a broadcast");
        }
    }

    private void DrawMusicHubMeter()
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var size = new Vector2(width, Ui(78));
        ImGui.Dummy(size);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(HubSurface(0x080F1D)), Ui(6));
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(HubTint(0x263E5D)), Ui(6));
        var monitoring = IsPlayingOwnIcecastDjStream && video.IsAudioOnly;
        var raw = monitoring ? screenController.Engine.ClassicBarsAudioLevel : 0;
        var level = float.IsFinite(raw) ? Math.Clamp(raw, 0, 1) : 0;
        const int segments = 28;
        var step = (width - Ui(22)) / segments;
        for (var i = 0; i < segments; i++)
        {
            var lit = i < (int)MathF.Ceiling(level * segments);
            var tint = lit ? i > 24 ? Hex(0xD97B83) : i > 21 ? Gold : AccentHover : HubSurface(0x243044);
            draw.AddRectFilled(origin + new Vector2(Ui(11) + i * step, Ui(14)),
                origin + new Vector2(Ui(11) + (i + .66f) * step, Ui(42)), ImGui.GetColorU32(tint), Ui(1));
        }
        var label = monitoring ? level > .005f ? "Local playback" : "No audio detected" : djIsOnAir ? "Meter available during playback" : "No audio detected";
        using (UiFonts.PushRegularRelative(.8f))
            draw.AddText(origin + new Vector2((width - ImGui.CalcTextSize(label).X) / 2, Ui(54)), ImGui.GetColorU32(MutedText), label);
    }

    private void DrawMusicHubSupport(float width)
    {
        var sideBySide = width >= Ui(600);
        MusicHubPanel("support", width, Ui(sideBySide ? 124 : 250), () =>
        {
            var origin = ImGui.GetCursorScreenPos();
            var available = ImGui.GetContentRegionAvail().X;
            var gap = Ui(28);
            var leftWidth = sideBySide ? (available - gap) * .45f : available;
            var rightWidth = sideBySide ? available - leftWidth - gap : available;
            var sectionHeight = Ui(96);
            using (var help = ImRaii.Child("help", new Vector2(leftWidth, sectionHeight), false))
            {
                if (help)
                {
                    using (UiFonts.PushRelative(1f, emphasis: true)) ImGui.TextUnformatted("Need help getting connected?");
                    using (UiFonts.PushRegularRelative(.86f))
                    using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText, "Set up your DJ software in a few steps.");
                    if (GameLayoutButton("Open setup guide", FontAwesomeIcon.BookOpen,
                            MathF.Min(Ui(185), ImGui.GetContentRegionAvail().X), height: 30)) OpenMusicDialog("guide");
                }
            }
            var draw = ImGui.GetWindowDrawList();
            if (sideBySide)
            {
                var divider = origin + new Vector2(leftWidth + gap / 2, 0);
                draw.AddLine(divider, divider + new Vector2(0, sectionHeight), ImGui.GetColorU32(HubTint(0x24456B)), Ui(1));
                ImGui.SameLine(0, gap);
            }
            else
            {
                SetGameLayoutRowGap(Ui(14));
                ImGui.Separator();
                ImGui.Spacing();
            }
            using (var credits = ImRaii.Child("credits", new Vector2(rightWidth, sectionHeight), false))
            {
                if (credits)
                {
                    var allowance = BroadcastAllowanceDisplayFor(BroadcastAllowanceMethod.DjLive);
                    using (UiFonts.PushRelative(1f, emphasis: true)) ImGui.TextUnformatted("Broadcast allowance");
                    ImGui.SameLine(0, Ui(8));
                    using (UiFonts.PushIcon()) ImGui.TextColored(MutedText, FontAwesomeIcon.InfoCircle.ToIconString());
                    if (ImGui.IsItemHovered()) UiFonts.SetTooltip(allowance.Heading + "\n" + allowance.Detail);
                    var membership = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                        allowance.Heading.Replace(" WEEKLY BROADCAST ALLOWANCE", string.Empty).ToLowerInvariant());
                    using (UiFonts.PushRegularRelative(.84f)) ImGui.TextColored(MutedText, membership);
                    using (ImRaii.PushColor(ImGuiCol.PlotHistogram, Accent))
                        ImGui.ProgressBar(allowance.Remaining, new Vector2(-1, Ui(10)), string.Empty);
                    if (ImGui.IsItemHovered()) UiFonts.SetTooltip(allowance.Detail);
                    using (UiFonts.PushRegularRelative(.84f)) ImGui.TextColored(MutedText, allowance.RemainingLabel);
                    if (ImGui.IsItemHovered()) UiFonts.SetTooltip(allowance.Detail);
                }
            }
        });
    }

    private void MusicHubPanel(string id, float width, float height, Action content)
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(14, 14));
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10));
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, HubSurface(0x0D1729)).Push(ImGuiCol.Border, HubTint(0x24456B));
        using var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(id, new Vector2(width, height), true));
        if (panel) content();
    }
}

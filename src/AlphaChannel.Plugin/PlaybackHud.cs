using System.Numerics;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// A small, always-present control surface. It only observes the existing
// player/screen state; it never creates a second playback session.
internal sealed partial class PlaybackHud
{
    private const float Width = 340f;
    private const float Height = 44f;
    private const float CornerRadius = 10f;
    private static ThemeColors DockColors => ThemeCatalog.Get(Plugin.Cfg.UiTheme, Plugin.Cfg.UiBackground);
    private static Vector4 DockBackground => WithAlpha(DockColors.WindowBg, 0.96f);
    private static Vector4 DockButtonBackground => WithAlpha(DockColors.CardBg, 0.96f);

    private static Vector4 WithAlpha(Vector4 color, float alpha) => new(color.X, color.Y, color.Z, alpha);

    private readonly VideoPlayer video;
    private readonly VideoEngine engine;
    private readonly StreamClient stream;
    private readonly Func<bool> viewerTvEnabled;
    private readonly Func<bool> miniPlayerOpen;
    private readonly Func<bool> mainWindowOpen;
    private readonly Func<int> notificationCount;
    private readonly Action toggleTv;
    private readonly Action toggleMiniPlayer;
    private readonly Action toggleMainWindow;
    private readonly Func<Version?> availableUpdate;
    private readonly Action openUpdates;
    private float updateTabWidth = 26f;
    private float controllerTabHeight;

    private Vector2? position;
    private bool positionDirty;
    private bool dragging;
    private bool volumeDirty;

    internal PlaybackHud(
        VideoPlayer video,
        VideoEngine engine,
        StreamClient stream,
        Func<bool> viewerTvEnabled,
        Func<bool> miniPlayerOpen,
        Func<bool> mainWindowOpen,
        Func<int> notificationCount,
        Action toggleTv,
        Action toggleMiniPlayer,
        Action toggleMainWindow,
        Func<Version?> availableUpdate,
        Action openUpdates)
    {
        this.video = video;
        this.engine = engine;
        this.stream = stream;
        this.viewerTvEnabled = viewerTvEnabled;
        this.miniPlayerOpen = miniPlayerOpen;
        this.mainWindowOpen = mainWindowOpen;
        this.notificationCount = notificationCount;
        this.toggleTv = toggleTv;
        this.toggleMiniPlayer = toggleMiniPlayer;
        this.toggleMainWindow = toggleMainWindow;
        this.availableUpdate = availableUpdate;
        this.openUpdates = openUpdates;
        position = Plugin.Cfg.PlaybackHudPosition;
    }

    internal Action? OnInteraction { get; set; }
    internal Action? ApplyListeningVolume { get; set; }
    internal Action? DrawVolumeContents { get; set; }
    internal Func<Vector2>? VolumePanelSize { get; set; }

    internal void Draw()
    {
        var activePlayback = video.State is VideoPlaybackState.Playing or VideoPlaybackState.Paused or VideoPlaybackState.Loading ||
                             engine.HasActiveNonVideoPlayback || video.IsPlayingLocalVideo;
        if (!Plugin.ClientState.IsLoggedIn || !Plugin.Cfg.HasCompletedInterfaceSizingSetup ||
            !MiniDockVisibilityPolicy.ShouldShow(Plugin.Cfg.MiniDockDisplay,activePlayback,
                stream.Mode is StreamMode.Hosting or StreamMode.Viewing))
        {
            controllerTabHeight = 0f;
            SaveVolumeIfDirty();
            return;
        }

        var mainViewport=ImGui.GetMainViewport();
        if (mainViewport.WorkSize.X < Width || mainViewport.WorkSize.Y < Height)
            return;

        position ??= mainViewport.WorkPos+mainViewport.WorkSize*.40f;
        var monitor=DesktopMonitors.ForWindow(position.Value,new Vector2(Width,Height));
        var gameRunning = engine.IsPlayingGame;
        var targetControllerHeight = gameRunning ? MathF.Min(40f, monitor.Size.Y - Height) : 0f;
        controllerTabHeight += (targetControllerHeight - controllerTabHeight) * MathF.Min(1f, ImGui.GetIO().DeltaTime * 14f);
        if (MathF.Abs(controllerTabHeight - targetControllerHeight) < 0.1f) controllerTabHeight = targetControllerHeight;
        if(!dragging)
        {
            var constrained=monitor.Clamp(position.Value-new Vector2(0,controllerTabHeight),new Vector2(Width,Height+controllerTabHeight))+new Vector2(0,controllerTabHeight);
            if(constrained!=position.Value){position=constrained;positionDirty=true;}
        }

        var update = availableUpdate();
        var expandedWidth = 46f + ImGui.CalcTextSize("Update available").X;
        var rightSpace = MathF.Max(0,monitor.Origin.X+monitor.Size.X-position.Value.X-Width);
        var leftSpace = MathF.Max(0,position.Value.X-monitor.Origin.X);
        var tabOnLeft = rightSpace < expandedWidth && leftSpace > rightSpace;
        var availableSpace = tabOnLeft ? leftSpace : rightSpace;
        var tabWidth = update is null ? 0f : MathF.Min(updateTabWidth, availableSpace);
        var tabOrigin = position.Value + new Vector2(tabOnLeft ? -tabWidth : Width, 8f);
        var mouse = ImGui.GetIO().MousePos;
        var tabHovered = update is not null && tabWidth > 0f &&
            mouse.X >= tabOrigin.X && mouse.X <= tabOrigin.X + tabWidth &&
            mouse.Y >= tabOrigin.Y && mouse.Y <= tabOrigin.Y + 28f;
        var targetWidth = tabHovered ? expandedWidth : 26f;
        updateTabWidth += (targetWidth - updateTabWidth) * MathF.Min(1f, ImGui.GetIO().DeltaTime * 14f);
        if (update is null) updateTabWidth = 26f;

        ImGui.SetNextWindowPos(position.Value - new Vector2(tabOnLeft ? tabWidth : 0f, controllerTabHeight), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(Width + tabWidth, Height + controllerTabHeight), ImGuiCond.Always);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                    ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoCollapse |
                    ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoFocusOnAppearing;
        if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.Begin("Alpha Channel playback HUD###AlphaChannelPlaybackHud", flags)))
        {
            ImGui.End();
            return;
        }

        using (new NativeUiFontScaleScope(Plugin.Cfg.NativeUiSizing, 1f))
        using (UiFonts.Begin())
        {
        OnInteraction?.Invoke();
        var origin = ImGui.GetWindowPos() + new Vector2(tabOnLeft ? tabWidth : 0f, controllerTabHeight);
        var draw = ImGui.GetWindowDrawList();
        var accent = DockColors.Accent;
        var muted = new Vector4(0.47f, 0.49f, 0.57f, 1f);
        if (update is not null && tabWidth > 0f)
            DrawUpdateTab(draw, origin, tabWidth, tabOnLeft, update, accent);
        if (controllerTabHeight > 0f)
            DrawControllerTab(draw, origin, gameRunning, accent, muted);
        draw.AddRectFilled(origin, origin + new Vector2(Width, Height),
            Color(DockBackground), CornerRadius);
        draw.AddRect(origin + Vector2.One, origin + new Vector2(Width - 1f, Height - 1f),
            Color(accent), CornerRadius, ImDrawFlags.None, 1.2f);

        var partyContentAvailable = stream.Mode == StreamMode.Viewing &&
                                    !string.IsNullOrWhiteSpace(stream.CurrentRoomState?.Url) &&
                                    !engine.IsActive && !miniPlayerOpen() && !engine.AudioOnlyMode;
        var onAir = activePlayback && (stream.Mode == StreamMode.Hosting || video.BroadcastDiagnostics.Active);
        var status = partyContentAvailable ? "PARTY CONTENT AVAILABLE — NOT WATCHING" : onAir ? "ON AIR" : stream.Mode == StreamMode.Viewing && activePlayback
            ? "VIEWING" : activePlayback ? "PLAYING" : "OFF AIR";
        var statusColor = partyContentAvailable ? new Vector4(1f, 0.80f, 0.25f, 1f)
            : activePlayback ? new Vector4(0.13f, 0.94f, 0.52f, 1f) : muted;
        draw.AddCircleFilled(origin + new Vector2(18f, 22f), 4.5f, Color(statusColor));

        var animateWaveform = !engine.AudioOnlyMode && !partyContentAvailable && activePlayback && video.State is not (VideoPlaybackState.Paused or VideoPlaybackState.Loading);
        DrawWaveform(draw, origin + new Vector2(74f, 22f), animateWaveform, accent, muted,
            highlightStationary: partyContentAvailable);

        // The status and waveform are the drag target; controls remain clickable.
        ImGui.SetCursorScreenPos(origin + new Vector2(7f, 5f));
        ImGui.InvisibleButton("##playbackHudDrag", new Vector2(111f, Height - 10f));
        dragging=ImGui.IsItemActive()&&ImGui.IsMouseDragging(ImGuiMouseButton.Left);
        if (dragging)
        {
            // Do not clamp during a drag: it must be able to cross monitor
            // seams, desktop gaps and negative desktop coordinates.
            position += ImGui.GetIO().MouseDelta;
            positionDirty = true;
        }
        if (positionDirty && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            var destination=DesktopMonitors.ForWindow(position.Value,new Vector2(Width,Height));
            position=destination.Clamp(position.Value-new Vector2(0,controllerTabHeight),new Vector2(Width,Height+controllerTabHeight))+new Vector2(0,controllerTabHeight);
            Plugin.Cfg.PlaybackHudPosition = position;
            Plugin.Cfg.Save();
            positionDirty = false;
        }
        if (ImGui.IsItemHovered())
            UiFonts.SetTooltip($"{(!partyContentAvailable && video.State == VideoPlaybackState.Paused ? "PAUSED" : status)}\nDrag to move Alpha Channel controls");

        var tvActive = stream.Mode == StreamMode.Viewing ? viewerTvEnabled() : engine.IsActive;
        if (DrawSquareButton(draw, origin + new Vector2(126f, 7f), "tv", FontAwesomeIcon.Tv,
                tvActive, accent, muted, tvActive ? "Despawn TV" : "Spawn TV"))
            toggleTv();

        if (DrawSquareButton(draw, origin + new Vector2(166f, 7f), "mini", engine.AudioOnlyMode ? FontAwesomeIcon.Headphones : FontAwesomeIcon.PlayCircle,
                miniPlayerOpen() || engine.AudioOnlyMode, accent, muted, engine.AudioOnlyMode ? "Audio Only — Open Mini Player" : miniPlayerOpen() ? "Close Mini Player" : "Open Mini Player", miniPlayer: !engine.AudioOnlyMode))
            toggleMiniPlayer();

        var volumeButtonOrigin = origin + new Vector2(206f, 7f);
        var soundMuted = Plugin.Cfg.Muted || Plugin.Cfg.Volume == 0;
        if (DrawSquareButton(draw, volumeButtonOrigin, "volume",
                soundMuted ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp,
                ImGui.IsPopupOpen("##playbackHudVolume"), accent, muted, "TV volume"))
            ImGui.OpenPopup("##playbackHudVolume");

        var lightingButtonOrigin = origin + new Vector2(246f, 7f);
        if(DrawSquareButton(draw,lightingButtonOrigin,"lighting",FontAwesomeIcon.Lightbulb,
            Plugin.LightingControls.HasCustomAdjustments,
            accent,muted,"Lighting",highlightOnHover:false))
        {
            if(ImGui.IsPopupOpen("##playbackHudLighting")) closeLightingPopup=true;
            else { lightingIndoorTab=Lighting.WorldLightingController.CanUseIndoor; ImGui.OpenPopup("##playbackHudLighting"); }
        }
        draw.AddLine(origin + new Vector2(288f, 11f), origin + new Vector2(288f, 33f),
            Color(new Vector4(0.40f, 0.39f, 0.52f, 0.55f)));
        ImGui.SetCursorScreenPos(origin + new Vector2(296f, 7f));
        var mainOpen = mainWindowOpen();
        if (ImGui.InvisibleButton("##playbackHudOpenMain", new Vector2(36f, 30f)))
            toggleMainWindow();
        var openHovered = ImGui.IsItemHovered();
        if (openHovered)
            UiFonts.SetTooltip(mainOpen ? "Close Alpha Channel" : "Open Alpha Channel");
        UiFonts.DrawCenteredIcon(draw,18f,origin+new Vector2(314f,22f),Color(openHovered ? Vector4.One : accent),FontAwesomeIcon.ExternalLinkAlt.ToIconString());

        var unread = notificationCount();
        if (unread > 0)
        {
            var badge = unread > 99 ? "99+" : unread.ToString();
            var badgeCenter = origin + new Vector2(327f, 10f);
            draw.AddCircleFilled(badgeCenter, badge.Length > 2 ? 11f : 8f,
                Color(new Vector4(0.91f, 0.17f, 0.23f, 1f)));
            var badgeFontSize = ImGui.GetFontSize() * 0.78f;
            var badgeSize = ImGui.CalcTextSize(badge) * 0.78f;
            UiFonts.DrawText(draw, badgeFontSize, badgeCenter - badgeSize * 0.5f, Color(Vector4.One), badge);
        }

        DrawVolumePopup(origin, volumeButtonOrigin, monitor, accent, muted);
        DrawLightingPopup(origin,lightingButtonOrigin,monitor,accent,muted);
        }
        ImGui.End();
    }

    private void DrawControllerTab(ImDrawListPtr draw, Vector2 dock, bool gameRunning, Vector4 accent, Vector4 muted)
    {
        const float width = 132f;
        // Like the update tab, this extends from behind the dock with an
        // overlap at the join. Keep controls inside the animated visible area.
        var start = dock + new Vector2((Width - width) * 0.5f, -controllerTabHeight);
        var end = new Vector2(start.X + width, dock.Y + 8f);
        draw.AddRectFilled(start, end, Color(DockBackground),
            8f, ImDrawFlags.RoundCornersTop);
        draw.AddRect(start, end, Color(accent), 8f, ImDrawFlags.RoundCornersTop, 1.2f);
        draw.PushClipRect(start, new Vector2(end.X, dock.Y), true);
        draw.AddLine(start + new Vector2(46f, 8f), start + new Vector2(46f, 32f),
            Color(new Vector4(0.40f, 0.39f, 0.52f, 0.55f)));
        draw.AddLine(start + new Vector2(86f, 8f), start + new Vector2(86f, 32f),
            Color(new Vector4(0.40f, 0.39f, 0.52f, 0.55f)));
        var controllingGame = engine.IsPlayingSnes ? engine.SnesControlsEnabled : engine.GameBoyControlsEnabled;
        var controllerOnly = controllingGame && engine.ControllerModeEnabled;
        // Do not allow clicks until the buttons have fully emerged, or during
        // the closing animation after the game has stopped.
        using (UiDisabled.Scope(!gameRunning || controllerTabHeight < 39f))
        {
            if (DrawSquareButton(draw, start + new Vector2(11f, 5f), "controlEmulator", FontAwesomeIcon.Keyboard,
                    controllingGame && !controllerOnly, accent, muted, "Control retro game with keyboard and controller"))
            {
                engine.SetRetroControlMode(AlphaChannel.Plugin.Video.RetroControlMode.Game);
            }
            if (DrawSquareButton(draw, start + new Vector2(51f, 5f), "controllerMode", FontAwesomeIcon.Gamepad,
                    controllerOnly, accent, muted, "Controller input goes to game, keyboard inputs to ffxiv"))
            {
                engine.SetRetroControlMode(RetroControlMode.Controller);
            }
            if (DrawSquareButton(draw, start + new Vector2(91f, 5f), "controlFfxiv", FontAwesomeIcon.Desktop,
                    !controllingGame, accent, muted, "Control FFXIV\nCtrl + F12 returns control to FFXIV"))
            {
                engine.SetRetroControlMode(AlphaChannel.Plugin.Video.RetroControlMode.Ffxiv);
            }
        }
        draw.PopClipRect();
    }

    private void DrawUpdateTab(ImDrawListPtr draw, Vector2 dock, float width,
        bool onLeft, Version version, Vector4 accent)
    {
        // Draw behind the dock with an eight-pixel overlap: only the outer
        // rounded end is exposed, rather than a detached pill/button.
        var start = dock + new Vector2(onLeft ? -width : Width - 8f, 8f);
        var end = dock + new Vector2(onLeft ? 8f : Width + width, 36f);
        var corners = onLeft ? ImDrawFlags.RoundCornersLeft : ImDrawFlags.RoundCornersRight;
        draw.AddRectFilled(start, end, Color(DockBackground), 6f, corners);
        draw.AddRect(start, end, Color(new Vector4(accent.X, accent.Y, accent.Z, 0.75f)), 6f, corners, 1f);

        var visibleStart = dock + new Vector2(onLeft ? -width : Width, 8f);
        ImGui.SetCursorScreenPos(visibleStart);
        if (ImGui.InvisibleButton("##playbackHudUpdate", new Vector2(width, 28f))) openUpdates();
        if (ImGui.IsItemHovered())
            UiFonts.SetTooltip($"Alpha Channel {version} is available.\nClick to open Dalamud's Can be updated page.");

        draw.PushClipRect(visibleStart, visibleStart + new Vector2(width, 28f), true);
        var arrow = dock + new Vector2(onLeft ? -13f : Width + 13f, 22f);
        var ink = Color(DockColors.AccentHover);
        draw.AddLine(arrow + new Vector2(0f, 5f), arrow - new Vector2(0f, 5f), ink, 1.7f);
        draw.AddLine(arrow - new Vector2(0f, 5f), arrow + new Vector2(-4f, -1f), ink, 1.7f);
        draw.AddLine(arrow - new Vector2(0f, 5f), arrow + new Vector2(4f, -1f), ink, 1.7f);
        var textSize = ImGui.CalcTextSize("Update available");
        var text = dock + new Vector2(onLeft ? -32f - textSize.X : Width + 32f, (Height - textSize.Y) * 0.5f);
        draw.AddText(text, Color(Vector4.Lerp(new Vector4(0.70f, 0.72f, 0.78f, 1f), DockColors.AccentHover, 0.4f)), "Update available");
        draw.PopClipRect();
    }

    private void DrawVolumePopup(Vector2 dockOrigin, Vector2 buttonOrigin, AlphaChannel.Plugin.Core.MonitorWorkArea monitor,
        Vector4 accent, Vector4 muted)
    {
        var popupSize = VolumePanelSize?.Invoke()??new Vector2(316f,170f);
        var popupY = dockOrigin.Y - popupSize.Y - 8f;
        if (popupY < monitor.Origin.Y) popupY = dockOrigin.Y + Height + 8f;
        var popupPosition = monitor.Clamp(new Vector2(buttonOrigin.X + 15f - popupSize.X * 0.5f, popupY),popupSize);
        ImGui.SetNextWindowPos(popupPosition, ImGuiCond.Always);
        ImGui.SetNextWindowSize(popupSize, ImGuiCond.Always);
        using var colors = ImRaii.PushColor(ImGuiCol.PopupBg, WithAlpha(DockColors.WindowBg, 0.98f))
            .Push(ImGuiCol.Border, accent)
            .Push(ImGuiCol.FrameBg, DockColors.FrameBg)
            .Push(ImGuiCol.SliderGrab, accent)
            .Push(ImGuiCol.SliderGrabActive, DockColors.AccentHover);
        using var styles = ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, CornerRadius)
            .Push(ImGuiStyleVar.PopupBorderSize, 1f)
            .Push(ImGuiStyleVar.WindowPadding, new Vector2(12f, 12f))
            .Push(ImGuiStyleVar.FrameRounding, 6f)
            .Push(ImGuiStyleVar.GrabRounding, 6f)
            .Push(ImGuiStyleVar.GrabMinSize, 12f);
        if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup("##playbackHudVolume", ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar)))
        {
            SaveVolumeIfDirty();
            return;
        }

        DrawVolumeContents?.Invoke();
        ImGui.EndPopup();
    }

    private void SaveVolumeIfDirty()
    {
        if (!volumeDirty) return;
        Plugin.Cfg.Save();
        volumeDirty = false;
    }

    private static void DrawWaveform(ImDrawListPtr draw, Vector2 center, bool active,
        Vector4 accent, Vector4 muted, bool highlightStationary = false)
    {
        const int bars = 14;
        var time = (float)ImGui.GetTime();
        for (var index = 0; index < bars; index++)
        {
            var envelope = 1f - MathF.Abs(index - 6.5f) / 8f;
            var pulse = active ? 0.55f + 0.45f * MathF.Sin(time * 5.3f + index * 0.72f) : 0.42f;
            var halfHeight = 2f + envelope * pulse * 9f;
            var x = center.X + (index - 6.5f) * 3.8f;
            draw.AddLine(new Vector2(x, center.Y - halfHeight),
                new Vector2(x, center.Y + halfHeight),
                Color(active || highlightStationary ? accent : muted), 1.7f);
        }
    }

    private static bool DrawSquareButton(ImDrawListPtr draw, Vector2 origin, string id,
        FontAwesomeIcon icon, bool active, Vector4 accent, Vector4 muted, string tooltip, bool miniPlayer = false,
        bool highlightOnHover = true)
    {
        const float size = 30f;
        ImGui.SetCursorScreenPos(origin);
        var clicked = ImGui.InvisibleButton($"##playbackHud_{id}", new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        if (hovered)
            UiFonts.SetTooltip(tooltip);
        var highlighted=active || (hovered && highlightOnHover);

        draw.AddRectFilled(origin, origin + new Vector2(size),
            Color(highlighted
                ? Vector4.Lerp(DockButtonBackground, accent, 0.24f)
                : DockButtonBackground), 6f);
        draw.AddRect(origin, origin + new Vector2(size), Color(highlighted ? accent : muted), 6f);
        var iconColor = highlighted ? DockColors.AccentHover : muted;
        if (miniPlayer)
        {
            MiniPlayerIcon.Draw(draw, origin, size, Color(iconColor));
        }
        else
        {
            UiFonts.DrawCenteredIcon(draw,18f,origin+new Vector2(size*.5f),Color(iconColor),icon.ToIconString());
        }
        return clicked;
    }

    internal static void DrawSettingsPreview(float width)
    {
        var origin = ImGui.GetCursorScreenPos();
        var scale = width / Width;
        var size = new Vector2(width, Height * scale);
        ImGui.Dummy(size);
        var draw = ImGui.GetWindowDrawList();
        var accent = DockColors.Accent;
        var muted = new Vector4(0.47f, 0.49f, 0.57f, 1f);
        Vector2 At(float x, float y) => origin + new Vector2(x, y) * scale;
        draw.AddRectFilled(origin, origin + size, Color(DockColors.WindowBg), CornerRadius * scale);
        draw.AddRect(origin, origin + size, Color(accent), CornerRadius * scale, ImDrawFlags.None, scale);
        draw.AddCircleFilled(At(18f, 22f), 4.5f * scale, Color(new Vector4(0.13f, 0.94f, 0.52f, 1f)));
        ReadOnlySpan<float> bars = [4f, 7f, 5f, 9f, 13f, 20f, 14f, 9f, 5f, 7f, 4f];
        for (var index = 0; index < bars.Length; index++)
        {
            var x = 49f + index * 4f;
            draw.AddLine(At(x, 22f - bars[index] * 0.5f), At(x, 22f + bars[index] * 0.5f), Color(accent), 1.5f * scale);
        }
        for (var index = 0; index < 4; index++)
        {
            var x = 126f + index * 40f;
            draw.AddRectFilled(At(x, 7f), At(x + 30f, 37f), Color(DockColors.CardBg), 6f * scale);
            draw.AddRect(At(x, 7f), At(x + 30f, 37f), Color(muted), 6f * scale);
            if (index == 1)
            {
                MiniPlayerIcon.Draw(draw, At(x, 7f), 30f * scale, Color(muted));
            }
            else
            {
                var icon = index == 0 ? FontAwesomeIcon.Tv : index == 2 ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.Lightbulb;
                UiFonts.DrawCenteredIcon(draw,18f*scale,At(x+15f,22f),Color(muted),icon.ToIconString());
            }
        }
        draw.AddLine(At(288f, 11f), At(288f, 33f), Color(muted), scale);
        UiFonts.DrawCenteredIcon(draw,18f*scale,At(314f,22f),Color(accent),FontAwesomeIcon.ExternalLinkAlt.ToIconString());
    }

    private static void DrawIcon(ImDrawListPtr draw, FontAwesomeIcon icon,
        Vector2 origin, float size, Vector4 color) =>
        UiFonts.DrawIcon(draw, size, origin, Color(color), icon.ToIconString());

    private static uint Color(Vector4 color) => ImGui.GetColorU32(color);
}

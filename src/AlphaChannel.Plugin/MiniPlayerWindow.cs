using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AlphaChannel.Plugin;

// Draws the engine's existing shared texture. Mini Mode never starts a
// second decoder, browser host, emulator or broadcast process.
internal sealed partial class MiniPlayerWindow : Window, IDisposable
{
    private readonly VideoPlayer video;
    private readonly VideoEngine engine;
    private readonly StreamClient stream;
    private readonly Func<string> titleProvider;
    private readonly Func<bool> arcadeGameActive;
    private readonly Action openMiniChat;
    private readonly Action restoreFullWindow;

    private float seekPreview;
    private bool seekDragging;
    private bool saveLayoutPending;
    private DateTime dutyNoticeUntilUtc;
    private bool dutyNoticeWaitingForArea;
    private float classicBarsPhase;
    private float classicBarsSpeed = 0.65f;
    internal void ShowDutyNotice() => dutyNoticeWaitingForArea = true;

    internal MiniPlayerWindow(
        VideoPlayer video,
        VideoEngine engine,
        StreamClient stream,
        Func<string> titleProvider,
        Func<bool> arcadeGameActive,
        Action openMiniChat,
        Action restoreFullWindow)
        : base("Alpha Channel Mini Player###AlphaChannelMiniPlayer")
    {
        this.video = video;
        this.engine = engine;
        this.stream = stream;
        this.titleProvider = titleProvider;
        this.arcadeGameActive = arcadeGameActive;
        this.openMiniChat = openMiniChat;
        this.restoreFullWindow = restoreFullWindow;

        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse |
                ImGuiWindowFlags.NoSavedSettings;

        Size = new Vector2(
            Math.Clamp(Plugin.Cfg.MiniPlayerWidth, 380f, 1100f),
            Math.Clamp(Plugin.Cfg.MiniPlayerHeight, 285f, 760f));
        SizeCondition = ImGuiCond.FirstUseEver;
        Position = Plugin.Cfg.MiniPlayerPosition;
        PositionCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380f, 285f),
            MaximumSize = new Vector2(1100f, 760f),
        };
    }

    internal Action? OnInteraction { get; set; }
    internal Action? EnterAudioOnly { get; set; }
    internal Func<bool>? CanEnterAudioOnly { get; set; }
    internal Func<string>? AudioOnlyUnavailableReason { get; set; }

    public override void Draw()
    {
        using var nativeFont = new NativeUiFontScaleScope(Plugin.Cfg.NativeUiSizing, 1f);
        using var managedFonts = UiFonts.Begin();
        OnInteraction?.Invoke();
        // Loading screens may hide plugin windows. Start the visible countdown
        // once the new area is ready, rather than consuming it while zoning.
        if (dutyNoticeWaitingForArea && WatchPartyTvLocation.IsLocalPlayerReady())
        {
            dutyNoticeWaitingForArea = false;
            dutyNoticeUntilUtc = DateTime.UtcNow.AddSeconds(10);
        }
        var colors = ThemeCatalog.Get(Plugin.Cfg.UiTheme, Plugin.Cfg.UiBackground);
        using var theme=new MiniWindowStyle(colors);
        DrawHeader(colors);
        if (!IsOpen) return;
        ImGui.Separator();
        ImGui.Spacing();

        var available = ImGui.GetContentRegionAvail();
        var previewAvailable = new Vector2(
            available.X,
            MathF.Max(120f, available.Y - 72f));
        var previewSize = FitAspect(previewAvailable, 16f / 9f);
        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() + MathF.Max(0f, (available.X - previewSize.X) * 0.5f));

        var previewOrigin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##miniVideo", previewSize);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            previewOrigin,
            previewOrigin + previewSize,
            ImGui.GetColorU32(new Vector4(0.01f, 0.015f, 0.03f, 1f)),
            7f);

        var hasPreview = video.State is not (VideoPlaybackState.Idle or VideoPlaybackState.Failed) ||
                         engine.IsPlayingGame ||
                         engine.IsPlayingBrowser ||
                         video.IsPlayingImage ||
                         video.IsPlayingLocalVideo;

        if (arcadeGameActive())
        {
            const string notice = "Arcade games can not be played via the mini player. You can play directly on the spawned tv screen or via the arcade page in the main plugin window.";
            ImGui.SetCursorScreenPos(previewOrigin + new Vector2(18f, previewSize.Y * 0.28f));
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + previewSize.X - 36f);
            ImGui.TextColored(colors.MutedText, notice);
            ImGui.PopTextWrapPos();
            ImGui.SetCursorScreenPos(previewOrigin + previewSize);
        }
        else if (engine.PartyCountdown is { } countdown)
        {
            DrawCountdownPreview(drawList, previewOrigin, previewSize, countdown);
        }
        else if (engine.IsShowingScreensaver)
        {
            DrawScreensaver(drawList, previewOrigin, previewSize);
        }
        else if (engine.IsShowingWaitingScreen || video.State == VideoPlaybackState.Loading ||
                 stream.Mode == StreamMode.Viewing && string.IsNullOrWhiteSpace(stream.CurrentRoomState?.Url))
        {
            DrawLoadingWheel(drawList, previewOrigin, previewSize);
        }
        else if (hasPreview && (engine.ClassicBarsActive || engine.PreviewTextureHandle != nint.Zero))
        {
            if (engine.ClassicBarsActive)
                DrawClassicBars(drawList, previewOrigin, previewSize);
            else drawList.AddImageRounded(
                new ImTextureID(unchecked((ulong)engine.PreviewTextureHandle)),
                previewOrigin,
                previewOrigin + previewSize,
                Vector2.Zero,
                Vector2.One,
                uint.MaxValue,
                7f);

            if (video.IsAudioOnly && engine.AudioScreenTextTextureHandle != nint.Zero)
                DrawAudioScreenText(drawList, previewOrigin, previewSize);
        }
        else
        {
            const string idle = "Nothing is playing";
            var idleSize = ImGui.CalcTextSize(idle);
            drawList.AddText(
                previewOrigin + (previewSize - idleSize) * 0.5f,
                ImGui.GetColorU32(colors.MutedText),
                idle);
        }

        DrawPreviewReactions(drawList, previewOrigin, previewSize);

        if (dutyNoticeWaitingForArea || DateTime.UtcNow < dutyNoticeUntilUtc)
        {
            const string notice = "Entering Instanced Content. Switching to mini video player";
            var wrapWidth = MathF.Max(1f, previewSize.X - 28f);
            var noticeHeight = ImGui.CalcTextSize(notice, false, wrapWidth).Y + 20f;
            drawList.AddRectFilled(previewOrigin + new Vector2(4f),
                previewOrigin + new Vector2(previewSize.X - 4f, noticeHeight),
                ImGui.GetColorU32(new Vector4(0.05f, 0.06f, 0.1f, 0.95f)), 5f);
            var cursor = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(previewOrigin + new Vector2(14f, 12f));
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapWidth);
            ImGui.TextUnformatted(notice);
            ImGui.PopTextWrapPos();
            ImGui.SetCursorScreenPos(cursor);
        }

        ImGui.Spacing();
        DrawControls(colors);
        CaptureLayout();
    }

    private void DrawClassicBars(ImDrawListPtr drawList, Vector2 origin, Vector2 size)
    {
        // Classic Bars are a world-screen shader, not decoded video frames.
        // Reuse the cached volume sample and the shader's bar shapes here;
        // no spectrum filter, extra decoder or MPV polling is needed.
        var level = Math.Clamp(engine.ClassicBarsAudioLevel, 0f, 1f);
        var delta = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);
        classicBarsSpeed += (0.65f + 2.75f * level - classicBarsSpeed) *
            (1f - MathF.Exp(-delta * 7f));
        classicBarsPhase = (classicBarsPhase + delta * classicBarsSpeed) % (MathF.PI * 200f);
        var boostedLevel = Math.Clamp(MathF.Pow(level, 0.65f) * 1.45f, 0f, 1f);

        drawList.PushClipRect(origin, origin + size, true);
        drawList.AddRectFilledMultiColor(origin, origin + size,
            ImGui.GetColorU32(new Vector4(0.030f, 0.018f, 0.085f, 1f)),
            ImGui.GetColorU32(new Vector4(0.030f, 0.018f, 0.085f, 1f)),
            ImGui.GetColorU32(new Vector4(0.006f, 0.008f, 0.025f, 1f)),
            ImGui.GetColorU32(new Vector4(0.006f, 0.008f, 0.025f, 1f)));
        for (var bar = 0; bar < 13; bar++)
        {
            var x = 0.32f + bar / 12f * 0.36f;
            var shape = (0.5f + 0.5f * MathF.Sin(classicBarsPhase + bar * 1.37f)) * 0.7f +
                (0.5f + 0.5f * MathF.Sin(classicBarsPhase * 0.62f + bar * 0.73f)) * 0.3f;
            var sensitivity = 0.70f + 0.30f * (0.5f + 0.5f * MathF.Sin(bar * 1.19f));
            var height = (0.012f + shape * 0.012f +
                boostedLevel * (0.060f + shape * 0.090f) * sensitivity) * size.Y;
            var center = origin + new Vector2(x * size.X, 0.63f * size.Y);
            var halfWidth = MathF.Max(1f, size.X * 0.007f);
            var color = Vector4.Lerp(new Vector4(0.35f, 0.10f, 0.70f, 1f),
                new Vector4(0.82f, 0.35f, 1f, 1f), x);
            drawList.AddRectFilled(center - new Vector2(halfWidth, height),
                center + new Vector2(halfWidth, height), ImGui.GetColorU32(color), 1f);
        }
        drawList.PopClipRect();
    }

    private void DrawAudioScreenText(
        ImDrawListPtr drawList, Vector2 origin, Vector2 size)
    {
        var selection = engine.AudioScreenTextSelection;
        if (!selection.HasText) return;

        var texture = new ImTextureID(unchecked((ulong)engine.AudioScreenTextTextureHandle));
        var time = engine.AudioScreenTextAnimationTime;
        if (!string.IsNullOrWhiteSpace(selection.TopText))
            DrawAudioTextBand(drawList, texture, origin, size,
                0f, 0.5f, selection.TopGradient, time);
        if (!string.IsNullOrWhiteSpace(selection.BottomText))
            DrawAudioTextBand(drawList, texture, origin, size,
                0.5f, 1f, selection.BottomGradient, time);
    }

    private void DrawPreviewReactions(ImDrawListPtr drawList, Vector2 origin, Vector2 size)
    {
        var reactions = video.CurrentReactions;
        if (reactions.Count == 0) return;

        drawList.PushClipRect(origin, origin + size, true);
        foreach (var reaction in reactions.Take(16))
        {
            if (reaction.Alpha <= 0f) continue;
            var center = origin + new Vector2(reaction.X * size.X, reaction.Y * size.Y);
            var radius = reaction.Radius * size.Y;
            var alpha = Math.Clamp(reaction.Alpha, 0f, 1f);
            drawList.AddCircleFilled(center + new Vector2(radius * 0.10f, radius * 0.12f),
                radius * 1.08f, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, alpha * 0.50f)), 32);
            drawList.AddCircleFilled(center, radius,
                ImGui.GetColorU32(new Vector4(0.018f, 0.025f, 0.055f, alpha)), 32);
            drawList.AddCircleFilled(center, radius * 0.84f,
                ImGui.GetColorU32(new Vector4(reaction.R, reaction.G, reaction.B, alpha * 0.85f)), 32);

            var glyph = ReactionGlyphs.Get(reaction.Glyph);
            var glyphSize = radius * 1.10f;
            using (UiFonts.PushIcon())
            {
                var textSize = ImGui.CalcTextSize(glyph) * (glyphSize / ImGui.GetFontSize());
                UiFonts.DrawIcon(drawList, glyphSize, center - textSize * 0.5f, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, alpha)), glyph);
            }
        }
        drawList.PopClipRect();
    }

    private static void DrawAudioTextBand(
        ImDrawListPtr drawList, ImTextureID texture, Vector2 origin, Vector2 size,
        float bandStart, float bandEnd, AudioTextGradient gradient, float time)
    {
        var top = origin.Y + size.Y * bandStart;
        var bottom = origin.Y + size.Y * bandEnd;
        if (gradient == AudioTextGradient.Solid)
        {
            drawList.AddImage(texture,
                new Vector2(origin.X, top), new Vector2(origin.X + size.X, bottom),
                new Vector2(0f, bandStart), new Vector2(1f, bandEnd), uint.MaxValue);
            return;
        }

        // The shared text texture contains white glyphs and a dark shadow.
        // Tint narrow vertical slices to match the world TV's animated shader.
        const int slices = 64;
        for (var index = 0; index < slices; index++)
        {
            var left = (float)index / slices;
            var right = (float)(index + 1) / slices;
            var colour = SampleAudioTextGradient(gradient, (left + right) * 0.5f, time);
            drawList.AddImage(texture,
                new Vector2(origin.X + size.X * left, top),
                new Vector2(origin.X + size.X * right, bottom),
                new Vector2(left, bandStart), new Vector2(right, bandEnd),
                ImGui.GetColorU32(new Vector4(colour, 1f)));
        }
    }

    private static Vector3 SampleAudioTextGradient(
        AudioTextGradient gradient, float x, float time)
    {
        if (gradient == AudioTextGradient.Rainbow)
        {
            var phase = Fraction(x * 0.9f - time * 0.08f);
            return new Vector3(
                RainbowChannel(phase),
                RainbowChannel(phase + 2f / 3f),
                RainbowChannel(phase + 1f / 3f));
        }

        var wave = 0.5f + 0.5f * MathF.Sin(
            2f * MathF.PI * (x * 0.85f - time * 0.12f));
        return gradient switch
        {
            AudioTextGradient.Aurora => Vector3.Lerp(
                new Vector3(0.20f, 0.96f, 0.68f),
                new Vector3(0.72f, 0.40f, 1f), wave),
            AudioTextGradient.Sunset => Vector3.Lerp(
                new Vector3(1f, 0.68f, 0.22f),
                new Vector3(1f, 0.28f, 0.66f), wave),
            _ => Vector3.Lerp(
                new Vector3(0.18f, 0.82f, 1f),
                new Vector3(0.24f, 0.38f, 1f), wave),
        };
    }

    private static float Fraction(float value) => value - MathF.Floor(value);

    private static float RainbowChannel(float phase) =>
        Math.Clamp(MathF.Abs(Fraction(phase) * 6f - 3f) - 1f, 0f, 1f);

    private void DrawHeader(ThemeColors colors)
    {
        using (UiFonts.PushIcon())
        {
            ImGui.TextColored(colors.Accent, FontAwesomeIcon.PlayCircle.ToIconString());
        }
        ImGui.SameLine();

        var reserved = 184f;
        ImGui.TextUnformatted(
            Truncate(titleProvider(), MathF.Max(100f, ImGui.GetContentRegionAvail().X - reserved)));

        var closeX = ImGui.GetWindowContentRegionMax().X - 27f;
        ImGui.SameLine(closeX - 102f);
        using (UiDisabled.Scope(CanEnterAudioOnly?.Invoke() != true))
            if (IconButton("miniAudioOnly", FontAwesomeIcon.Headphones,
                CanEnterAudioOnly?.Invoke() == true ? "Continue with audio only" : AudioOnlyUnavailableReason?.Invoke() ?? "Audio only unavailable"))
                EnterAudioOnly?.Invoke();

        ImGui.SameLine(closeX - 68f);
        if (IconButton("miniOpenChat", FontAwesomeIcon.Comments, "Show Mini Chat"))
        {
            openMiniChat();
        }

        ImGui.SameLine(closeX - 34f);
        if (IconButton("miniRestoreFull", FontAwesomeIcon.Expand, "Open full Alpha Channel"))
        {
            IsOpen = false;
            restoreFullWindow();
        }

        ImGui.SameLine(closeX);
        if (IconButton("miniClose", FontAwesomeIcon.Times, "Close Mini Player"))
        {
            IsOpen = false;
        }
    }

    private void DrawControls(ThemeColors colors)
    {
        var (position, duration, paused) = video.GetProgress();
        var canTransport = !video.ManualTransportLocked && stream.Mode != StreamMode.Viewing &&
                           video.State is VideoPlaybackState.Playing or VideoPlaybackState.Paused;
        var seekable = canTransport && duration > 0.5f && !video.IsAudioOnly;

        if (!seekDragging)
        {
            seekPreview = position;
        }

        if (!canTransport)
        {
            UiDisabled.Begin();
        }

        if (IconButton(
                "miniPlayPause",
                paused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause,
                canTransport ? (paused ? "Play" : "Pause") : "Playback is controlled by the host") &&
            canTransport)
        {
            video.Pause(!paused);
        }

        if (!canTransport)
        {
            UiDisabled.End();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(MathF.Max(90f, ImGui.GetContentRegionAvail().X - 168f));
        if (!seekable)
        {
            UiDisabled.Begin();
        }

        ImGui.SliderFloat("##miniSeek", ref seekPreview, 0f, MathF.Max(duration, 0.01f), "");
        seekDragging = seekable && ImGui.IsItemActive();
        if (seekable && ImGui.IsItemDeactivatedAfterEdit())
        {
            video.Seek(seekPreview);
        }

        if (!seekable)
        {
            UiDisabled.End();
        }

        ImGui.SameLine();
        ImGui.TextColored(colors.MutedText, $"{FormatTime(position)} / {FormatTime(duration)}");

        var volume = Plugin.Cfg.Volume;
        if (IconButton("miniMute",
                Plugin.Cfg.Muted || volume == 0 ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp,
                Plugin.Cfg.Muted || volume == 0 ? "Unmute" : "Mute"))
        {
            if (Plugin.Cfg.Muted || volume == 0)
            {
                if (volume == 0) Plugin.Cfg.Volume = 50;
                Plugin.Cfg.Muted = false;
            }
            else
            {
                Plugin.Cfg.Muted = true;
            }
            video.SetVolume(Plugin.Cfg.Muted ? 0 : Plugin.Cfg.Volume);
            Plugin.Cfg.Save();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        using (ImRaii.PushColor(ImGuiCol.Text, volume > 100 ? colors.Gold : Vector4.One))
        if (ImGui.SliderInt("##miniVolume", ref volume, 0, 130, "%d%%"))
        {
            Plugin.Cfg.Volume = volume;
            Plugin.Cfg.Muted = volume == 0;
            video.SetVolume(volume);
            Plugin.Cfg.Save();
        }
    }

    private static bool IconButton(string id, FontAwesomeIcon icon, string tooltip)
    {
        bool clicked;
        using (UiFonts.PushIcon())
        {
            clicked = ImGui.Button($"{icon.ToIconString()}##{id}", new Vector2(27f, 25f));
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            UiFonts.SetTooltip(tooltip);
        }

        return clicked;
    }

    private void CaptureLayout()
    {
        var position = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var changed = Plugin.Cfg.MiniPlayerPosition != position ||
                      MathF.Abs(Plugin.Cfg.MiniPlayerWidth - size.X) > 1f ||
                      MathF.Abs(Plugin.Cfg.MiniPlayerHeight - size.Y) > 1f;

        if (changed)
        {
            Plugin.Cfg.MiniPlayerPosition = position;
            Plugin.Cfg.MiniPlayerWidth = size.X;
            Plugin.Cfg.MiniPlayerHeight = size.Y;
            saveLayoutPending = true;
        }

        if (saveLayoutPending && !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            Plugin.Cfg.Save();
            saveLayoutPending = false;
        }
    }

    public override void OnClose()
    {
        if (saveLayoutPending)
        {
            Plugin.Cfg.Save();
            saveLayoutPending = false;
        }
    }

    private static Vector2 FitAspect(Vector2 available, float aspect)
    {
        var width = available.X;
        var height = width / aspect;
        if (height > available.Y)
        {
            height = available.Y;
            width = height * aspect;
        }

        return new Vector2(MathF.Max(1f, width), MathF.Max(1f, height));
    }

    private static string Truncate(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width)
        {
            return text;
        }

        while (text.Length > 1 && ImGui.CalcTextSize(text + "…").X > width)
        {
            text = text[..^1];
        }

        return text + "…";
    }

    private static string FormatTime(float seconds) =>
        TimeSpan.FromSeconds(Math.Max(0f, seconds))
            .ToString(seconds >= 3600f ? @"h\:mm\:ss" : @"m\:ss");

    public void Dispose() => OnClose();
}

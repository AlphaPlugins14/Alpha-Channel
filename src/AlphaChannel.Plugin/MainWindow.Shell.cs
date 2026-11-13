using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace AlphaChannel.Plugin;

// Mockup chrome: Home right rail + always-on bottom media bar + optional custom window background.
internal sealed partial class MainWindow
{
    private IDalamudTextureWrap? homeHero;
    private string? homeHeroLoadedPath;
    private bool homeHeroLoadStarted;
    private int homeHeroLoadGeneration;
    private DateTime homeHeroNextRetryUtc;
    private readonly object windowTexturePublicationGate = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> pendingWindowTextureChanges = new();
    private bool playerFocusJoin;
    private string? subtitleChoiceUrl;

    private IDalamudTextureWrap? customBackground;
    private string? customBackgroundLoadedPath;
    private bool customBackgroundLoadStarted;
    private int customBackgroundLoadGeneration;
    private DateTime customBackgroundNextRetryUtc;
    private string customBackgroundPathInput = string.Empty;
    private bool customBackgroundPathSynced;
    private string? customBackgroundError;

    private string customHomeHeroPathInput = string.Empty;
    private bool customHomeHeroPathSynced;
    private string? customHomeHeroError;

    private double playbackStartedAt;
    private bool lastPlaybackState;
    private double playbackStoppedAt;
    private bool playbackWasActive;

    private void QueueWindowTextureChange(Action update)
    {
        lock (windowTexturePublicationGate)
        {
            // After shutdown the update's guard releases its new texture.
            if (Volatile.Read(ref disposeStarted) != 0) update();
            else pendingWindowTextureChanges.Enqueue(update);
        }
    }

    private void ApplyPendingWindowTextures()
    {
        while (pendingWindowTextureChanges.TryDequeue(out var update)) update();
    }

    private void EnsureHomeHeroLoaded(bool bundledOnly = false)
    {
        ApplyPendingWindowTextures();
        var assemblyDirectory = Plugin.PluginInterface.AssemblyLocation.DirectoryName;
        var path = bundledOnly
            ? assemblyDirectory is null ? null : Path.Combine(assemblyDirectory, "Assets", "home-hero.png")
            : ResolveHomeHeroPath();
        if (path is null)
        {
            return;
        }

        if (homeHero is not null &&
            string.Equals(homeHeroLoadedPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if ((homeHeroLoadStarted || DateTime.UtcNow < homeHeroNextRetryUtc) &&
            string.Equals(homeHeroLoadedPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        homeHeroLoadStarted = true;
        homeHeroLoadedPath = path;
        _ = LoadHomeHeroAsync(path, Interlocked.Increment(ref homeHeroLoadGeneration));
    }

    private static string? ResolveHomeHeroPath()
    {
        var custom = Plugin.Cfg.CustomHomeHeroPath;
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
        {
            return custom;
        }

        var dir = Plugin.PluginInterface.AssemblyLocation.DirectoryName;
        if (dir is null)
        {
            return null;
        }

        var bundled = Path.Combine(dir, "Assets", "home-hero.png");
        return File.Exists(bundled) ? bundled : null;
    }

    private async Task LoadHomeHeroAsync(string path, int generation)
    {
        try
        {
            var pngBytes = await Video.BoundedImageDecode.LoadLocalPngAsync(path, youtubeFeedToken).ConfigureAwait(false);
            youtubeFeedToken.ThrowIfCancellationRequested();
            var wrap = await Plugin.TextureProvider.CreateFromImageAsync(pngBytes)
                .ConfigureAwait(false);

            QueueWindowTextureChange(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0 || generation != Volatile.Read(ref homeHeroLoadGeneration))
                { wrap.Dispose(); return; }
                var old = homeHero;
                homeHero = wrap;
                homeHeroLoadedPath = path;
                customHomeHeroError = null;
                old?.Dispose();
            });
        }
        catch (OperationCanceledException) when (youtubeFeedToken.IsCancellationRequested) { }
        catch (Exception)
        {
            if (generation == Volatile.Read(ref homeHeroLoadGeneration))
            {
                customHomeHeroError = "Couldn't load that Home illustration.";
                homeHeroNextRetryUtc = DateTime.UtcNow.AddSeconds(5);
            }
            AepLog.Warning("[Home] Failed to load hero [redacted].");
        }
        finally
        {
            if (generation == Volatile.Read(ref homeHeroLoadGeneration)) homeHeroLoadStarted = false;
        }
    }

    private bool TryApplyCustomHomeHeroFromPath(string rawPath)
    {
        customHomeHeroError = null;
        var path = rawPath.Trim().Trim('"');
        if (path.Length == 0 || !File.Exists(path))
        {
            customHomeHeroError = "Pick an existing png, jpg, or webp file.";
            return false;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp"))
        {
            customHomeHeroError = "Use a png, jpg, webp, or bmp image.";
            return false;
        }

        try
        {
            var destDir = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "Backgrounds");
            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, "home-hero" + ext);
            File.Copy(path, dest, overwrite: true);

            Plugin.Cfg.CustomHomeHeroPath = dest;
            Plugin.Cfg.ShowHomeHeroImage = true;
            Plugin.Cfg.Save();

            customHomeHeroPathInput = dest;
            Interlocked.Increment(ref homeHeroLoadGeneration);
            homeHero?.Dispose();
            homeHero = null;
            homeHeroLoadedPath = null;
            homeHeroLoadStarted = false;
            EnsureHomeHeroLoaded();
            return true;
        }
        catch (Exception)
        {
            customHomeHeroError = "Couldn't copy that file into the plugin folder.";
            AepLog.Warning("[Home] Hero copy failed.");
            return false;
        }
    }

    private void ClearCustomHomeHero()
    {
        Interlocked.Increment(ref homeHeroLoadGeneration);
        Plugin.Cfg.CustomHomeHeroPath = null;
        Plugin.Cfg.Save();
        homeHero?.Dispose();
        homeHero = null;
        homeHeroLoadedPath = null;
        homeHeroLoadStarted = false;
        customHomeHeroPathInput = string.Empty;
        customHomeHeroPathSynced = false;
        customHomeHeroError = null;
        if (Plugin.Cfg.ShowHomeHeroImage)
        {
            EnsureHomeHeroLoaded();
        }
    }

    private void EnsureCustomBackgroundLoaded()
    {
        ApplyPendingWindowTextures();
        var path = Plugin.Cfg.CustomBackgroundPath;
        if (Plugin.Cfg.UiBackground != UiBackground.Custom || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (customBackground is not null &&
            string.Equals(customBackgroundLoadedPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if ((customBackgroundLoadStarted || DateTime.UtcNow < customBackgroundNextRetryUtc) &&
            string.Equals(customBackgroundLoadedPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        customBackgroundLoadStarted = true;
        customBackgroundLoadedPath = path;
        _ = LoadCustomBackgroundAsync(path, Interlocked.Increment(ref customBackgroundLoadGeneration));
    }

    private async Task LoadCustomBackgroundAsync(string path, int generation)
    {
        try
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Background file not found.");
            }

            var pngBytes = await Video.BoundedImageDecode.LoadLocalPngAsync(path, youtubeFeedToken).ConfigureAwait(false);
            youtubeFeedToken.ThrowIfCancellationRequested();
            var wrap = await Plugin.TextureProvider.CreateFromImageAsync(pngBytes)
                .ConfigureAwait(false);

            QueueWindowTextureChange(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0 || generation != Volatile.Read(ref customBackgroundLoadGeneration))
                { wrap.Dispose(); return; }
                var old = customBackground;
                customBackground = wrap;
                customBackgroundLoadedPath = path;
                customBackgroundError = null;
                old?.Dispose();
            });
        }
        catch (OperationCanceledException) when (youtubeFeedToken.IsCancellationRequested) { }
        catch (Exception)
        {
            if (generation == Volatile.Read(ref customBackgroundLoadGeneration))
            {
                customBackgroundError = "Couldn't load that image.";
                customBackgroundNextRetryUtc = DateTime.UtcNow.AddSeconds(5);
            }
            AepLog.Warning("[Background] Failed to load [redacted].");
        }
        finally
        {
            if (generation == Volatile.Read(ref customBackgroundLoadGeneration)) customBackgroundLoadStarted = false;
        }
    }

    private void DrawCustomBackgroundLayer()
    {
        ApplyPendingWindowTextures();
        if (Plugin.Cfg.UiBackground != UiBackground.Custom || customBackground is null)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        var boxW = max.X - min.X;
        var boxH = max.Y - min.Y;

        // Fit the whole image inside the window (contain) and center it — no stretch, no crop.
        var (imgMin, imgMax) = ContainRect(
            customBackground.Width, customBackground.Height, min, boxW, boxH);
        drawList.AddImage(customBackground.Handle, imgMin, imgMax);

        var dim = Math.Clamp(Plugin.Cfg.CustomBackgroundDim, 0f, 0.9f);
        if (dim > 0.01f)
        {
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, dim)));
        }
    }

    // Scale the image to fit inside the box, then center the resulting rect.
    private static (Vector2 Min, Vector2 Max) ContainRect(
        float texW, float texH, Vector2 boxOrigin, float boxW, float boxH)
    {
        if (texW <= 0 || texH <= 0 || boxW <= 0 || boxH <= 0)
        {
            return (boxOrigin, boxOrigin + new Vector2(boxW, boxH));
        }

        var scale = MathF.Min(boxW / texW, boxH / texH);
        var drawW = texW * scale;
        var drawH = texH * scale;
        var origin = boxOrigin + new Vector2((boxW - drawW) * 0.5f, (boxH - drawH) * 0.5f);
        return (origin, origin + new Vector2(drawW, drawH));
    }

    private bool TryApplyCustomBackgroundFromPath(string rawPath)
    {
        customBackgroundError = null;
        var path = rawPath.Trim().Trim('"');
        if (path.Length == 0 || !File.Exists(path))
        {
            customBackgroundError = "Pick an existing png, jpg, or webp file.";
            return false;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp"))
        {
            customBackgroundError = "Use a png, jpg, webp, or bmp image.";
            return false;
        }

        try
        {
            var destDir = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "Backgrounds");
            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, "custom" + ext);
            File.Copy(path, dest, overwrite: true);

            Plugin.Cfg.CustomBackgroundPath = dest;
            Plugin.Cfg.UiBackground = UiBackground.Custom;
            Plugin.Cfg.Save();

            customBackgroundPathInput = dest;
            Interlocked.Increment(ref customBackgroundLoadGeneration);
            customBackground?.Dispose();
            customBackground = null;
            customBackgroundLoadedPath = null;
            customBackgroundLoadStarted = false;
            EnsureCustomBackgroundLoaded();
            Colors = ThemeCatalog.Get(Plugin.Cfg.UiTheme, UiBackground.Custom);
            return true;
        }
        catch (Exception)
        {
            customBackgroundError = "Couldn't copy that file into the plugin folder.";
            AepLog.Warning("[Background] Copy failed.");
            return false;
        }
    }

    private void ClearCustomBackground()
    {
        Interlocked.Increment(ref customBackgroundLoadGeneration);
        Plugin.Cfg.CustomBackgroundPath = null;
        if (Plugin.Cfg.UiBackground == UiBackground.Custom)
        {
            Plugin.Cfg.UiBackground = UiBackground.Theme;
        }

        Plugin.Cfg.Save();
        customBackground?.Dispose();
        customBackground = null;
        customBackgroundLoadedPath = null;
        customBackgroundLoadStarted = false;
        customBackgroundPathInput = string.Empty;
        customBackgroundPathSynced = false;
        customBackgroundError = null;
        Colors = ThemeCatalog.Get(Plugin.Cfg.UiTheme, Plugin.Cfg.UiBackground);
    }

    private static string? FindImageInDownloads()
    {
        try
        {
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            if (!Directory.Exists(downloads))
            {
                return null;
            }

            string[] patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"];
            return patterns
                .SelectMany(pattern => Directory.GetFiles(downloads, pattern))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static void DrawDashedRect(
    ImDrawListPtr drawList,
    Vector2 min,
    Vector2 max,
    uint color,
    float rounding)
    {
        const float dash = 6f;
        var gap = Ui(5f);

        // Top + bottom edges
        for (float x = min.X + rounding; x < max.X - rounding; x += dash + gap)
        {
            drawList.AddLine(
                new Vector2(x, min.Y),
                new Vector2(MathF.Min(x + dash, max.X - rounding), min.Y),
                color,
                1f);

            drawList.AddLine(
                new Vector2(x, max.Y),
                new Vector2(MathF.Min(x + dash, max.X - rounding), max.Y),
                color,
                1f);
        }

        // Left + right edges
        for (float y = min.Y + rounding; y < max.Y - rounding; y += dash + gap)
        {
            drawList.AddLine(
                new Vector2(min.X, y),
                new Vector2(min.X, MathF.Min(y + dash, max.Y - rounding)),
                color,
                1f);

            drawList.AddLine(
                new Vector2(max.X, y),
                new Vector2(max.X, MathF.Min(y + dash, max.Y - rounding)),
                color,
                1f);
        }

        const int segments = 8;

        // Top-left corner
        drawList.PathArcTo(
            new Vector2(min.X + rounding, min.Y + rounding),
            rounding,
            MathF.PI,
            MathF.PI * 1.5f,
            segments);

        drawList.PathStroke(color, ImDrawFlags.None, 1f);

        // Top-right corner
        drawList.PathArcTo(
            new Vector2(max.X - rounding, min.Y + rounding),
            rounding,
            MathF.PI * 1.5f,
            MathF.PI * 2f,
            segments);

        drawList.PathStroke(color, ImDrawFlags.None, 1f);

        // Bottom-right corner
        drawList.PathArcTo(
            new Vector2(max.X - rounding, max.Y - rounding),
            rounding,
            0f,
            MathF.PI * 0.5f,
            segments);

        drawList.PathStroke(color, ImDrawFlags.None, 1f);

        // Bottom-left corner
        drawList.PathArcTo(
            new Vector2(min.X + rounding, max.Y - rounding),
            rounding,
            MathF.PI * 0.5f,
            MathF.PI,
            segments);

        drawList.PathStroke(color, ImDrawFlags.None, 1f);
    }

    private void DrawBottomBar(
    bool playbackActive,
    float sidebarEdge)
    {
        var windowPos =
            ImGui.GetWindowPos();

        var windowSize =
            ImGui.GetWindowSize();

        var height =
            BottomBarHeight;

        //
        // Leave the outer window's semi-transparent bottom border visible.
        //
        var borderClearance =
            Ui(6f);

        var targetY =
            windowPos.Y +
            windowSize.Y -
            height -
            borderClearance;

        //
        // Keep the animation starting below the visible window. Only its final
        // resting position needs the border clearance.
        //
        var hiddenY =
            windowPos.Y +
            windowSize.Y;

        var slidingIn =
            playbackActive;

        var elapsed =
            (float)(
                ImGui.GetTime() -
                (slidingIn
                    ? playbackStartedAt
                    : playbackStoppedAt));

        var slide =
            Math.Clamp(
                elapsed / 0.35f,
                0f,
                1f);

        if (!slidingIn)
        {
            slide =
                1f -
                slide;
        }

        slide =
            slide *
            slide *
            (3f - (2f * slide));

        var y =
            hiddenY +
            ((targetY - hiddenY) * slide);

        //
        // Start one pixel beyond the sidebar so the playback bar cannot
        // cover the sidebar divider.
        //
        var leftEdge =
            sidebarEdge +
            Ui(1f);

        //
        // Preserve the same outer-border clearance on the right side as on
        // the bottom edge.
        //
        var rightEdge =
            windowPos.X +
            windowSize.X -
            borderClearance;

        var width =
            MathF.Max(
                0f,
                rightEdge -
                leftEdge);

        ImGui.SetCursorScreenPos(
            new Vector2(
                leftEdge,
                y));

        using var window =
            UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                "##bottomTransportOverlay",
                new Vector2(
                    width,
                    height),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse |
                ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoSavedSettings));

        if (window)
        {
            DrawBottomTransport(
                width,
                height);
        }
    }

   

    private void DrawBottomTransport(float width, float height)
    {
        using var transport = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##bottomTransport", new Vector2(width, height), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse));
        if (!transport) return;
        var current =
     queue.Current;

        if (HalloweenActivityActive)
        {
            DrawBottomTransportPlaying("Halloween 2026", null, false, width, height);
            return;
        }
        if (current is null &&
            video.IsPlayingLocalVideo)
        {
            var localTitle =
                string.IsNullOrWhiteSpace(
                    localVideoSelectedPath)
                    ? "Local Video"
                    : Path.GetFileNameWithoutExtension(
                        localVideoSelectedPath);

            DrawBottomTransportPlaying(
                localTitle,
                null,
                true,
                width,
                height);

            return;
        }

        if (current is null)
        {
            // Center the idle block inside the transport column
            var idleBlock = Ui(210f);
            var pad = MathF.Max(0f, (width - idleBlock) * 0.5f);
            var top = MathF.Max(0f, (height - ImGui.GetTextLineHeight() * 2f - Ui(4f)) * 0.5f);
            ImGui.SetCursorPos(new Vector2(pad, top));
            using (UiFonts.PushIcon())
            {
                ImGui.TextColored(MutedText, FontAwesomeIcon.Tv.ToIconString());
            }

            ImGui.SameLine(0, 12);

            ImGui.BeginGroup();

            ImGui.TextUnformatted("Nothing Playing");

            var subtitle = "Pick something to watch.";

            var titleWidth = ImGui.CalcTextSize("Nothing Playing").X;
            var subtitleWidth = ImGui.CalcTextSize(subtitle).X;

            ImGui.SetCursorPosX(
                ImGui.GetCursorPosX() + ((titleWidth - subtitleWidth) / 2f));

            ImGui.TextColored(
                MutedText,
                subtitle);

            ImGui.EndGroup();

            return;
        }

        DrawBottomTransportPlaying(
    stream.Mode == StreamMode.Viewing
        ? WatchPartyMediaDisplay.Title(current.Url, current.Title, video.IsAudioOnly)
        : current.Title,
    current.ThumbnailUrl,
    false,
    width,
    height);
    }

    // Spotify-style island: centered prev/play/next, seek with times underneath, volume on the right.
    private void DrawBottomTransportPlaying(
        string mediaTitle,
        string? thumbnailUrl,
        bool localVideo,
        float width,
        float height)
    {
        var barDrawList = ImGui.GetWindowDrawList();
        var childPos = ImGui.GetWindowPos();

        barDrawList.AddLine(
childPos + UiVec(0, 2),
childPos + new Vector2(width, Ui(2)),
            ImGui.GetColorU32(new Vector4(
        Accent.X,
        Accent.Y,
        Accent.Z,
        0.14f)),
           2f);

        var (position, duration, isPaused) = video.GetProgress();
        var liveAudio = video.IsAudioOnly && Uri.TryCreate(video.CurrentMediaUrl, UriKind.Absolute, out var liveAudioUrl) &&
            liveAudioUrl.Scheme is "http" or "https";
        if (!seekDragging)
        {
            seekPreview = position;
        }
        var playSize = Ui(32f);
        var skipSize = Ui(20f);
        var stopSize = Ui(30f);
        var gap = Ui(14f);
        var volSliderW = Ui(90f);
        var volIconW = Ui(26f);

        // Viewers follow the host, so only solo playback and the host
        // receive the permanent end-playback control.
        var showEndPlaybackButton =
            stream.Mode != StreamMode.Viewing;

        var volClusterW =
            volIconW + Ui(6f) + volSliderW;

        var controlsW =
            skipSize + gap +
            playSize + gap +
            skipSize +
            (showEndPlaybackButton
                ? gap + stopSize
                : 0f);

        var lineH = ImGui.GetTextLineHeight();

        // Title sits left; controls + volume share the rest.
        var title =
            Truncate(
                PrepareTwitchDisplayText(
                    mediaTitle),
                80);



        var thumbnailWidth = Ui(64f);
        var titleX = thumbnailWidth + Ui(24f);
        var titleWidth = MathF.Max(1f, (width - controlsW) * 0.5f - Ui(18f) - titleX);



            using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(gap, Ui(4f))))
            {
                // --- Top row: title | centered transport | volume ---
                var topY = Ui(14f);

                // --- Left title area ---
                ImGui.SetCursorPos(new Vector2(
                    thumbnailWidth + Ui(24f),
                    topY + Ui(8f)));

                ImGui.TextColored(Hex(0xE2E7EF),
                    TruncateToWidth(title, MathF.Max(1f, titleWidth - (liveAudio ? ImGui.CalcTextSize("LIVE").X + Ui(8f) : 0f))));
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip(mediaTitle);
                if (liveAudio)
                {
                    ImGui.SameLine(0, Ui(8f));
                    ImGui.TextColored(Accent, "LIVE");
                }

                if (screenController.Engine.AudioOnlyMode)
                {
                    ImGui.SetCursorPos(new Vector2(titleX, topY + Ui(30)));
                    using (UiFonts.PushRegularRelative(.85f)) DrawAudioOnlyStatus();
                }

                var thumbX = Ui(8f);
                var thumbY = topY;

                var drawList = ImGui.GetWindowDrawList();

                var windowPos = ImGui.GetWindowPos();

                var thumbMin = windowPos + new Vector2(thumbX, thumbY);
                var thumbMax = windowPos + new Vector2(
                    thumbX + thumbnailWidth,
                    thumbY + Ui(56f));

            var thumbnail =
    string.IsNullOrWhiteSpace(
        thumbnailUrl)
        ? null
        : thumbnails.Get(
            thumbnailUrl);

            if (thumbnail is not null)
            {
                drawList.AddImageRounded(
                    thumbnail.Handle,
                    thumbMin,
                    thumbMax,
                    Vector2.Zero,
                    Vector2.One,
                    uint.MaxValue,
                    6f);
                drawList.AddRect(thumbMin, thumbMax, ImGui.GetColorU32(Accent), 6f);
            }
            else
            {
                var entry = localVideo ? null : queue.Current;
                var icon = CompactMediaPlaceholderIcon(entry, GetPlayerStatusMedia(entry).Icon);
                DrawPartyThumbnailFallback(entry ?? new VideoQueueEntry(string.Empty, mediaTitle, "LocalVideo", null, null),
                    thumbMin, thumbMax - thumbMin, usePlaybackState: false, compactIcon: icon);
            }

            // Reset cursor after thumbnail drawing
            ImGui.SetCursorPos(new Vector2(0f, topY));



                var volumeGap = Ui(36f);

                var fullClusterW = controlsW + volumeGap + volClusterW;

                var clusterX = (width - fullClusterW) * 0.5f;
                var controlsX =
     (width - controlsW) * 0.5f;

                var volX = width - volClusterW - Ui(70f);


                // Center transport
                ImGui.SetCursorPos(new Vector2(
                    controlsX,
                    topY + (playSize - skipSize) * 0.5f));

            using (UiDisabled.Scope(video.ManualTransportLocked || StudioTvTransportActive && !CanSkipStudioSong))
            if (DrawTransportGhostButton(
                    FontAwesomeIcon.StepBackward,
                    skipSize))
            {
                if (!TryStudioSongTransport(next: false)) video.Seek(
                    localVideo
                        ? MathF.Max(
                            0f,
                            position - 10f)
                        : 0f);
            }

            ImGui.SameLine(0, gap);

                ImGui.SetCursorPosY(topY);

            using (UiDisabled.Scope(video.ManualTransportLocked))
            if (DrawTransportPlayButton(isPaused, playSize))
            {
                video.Pause(!isPaused);
                queue.SaveCurrentProgress();
            }

            ImGui.SameLine(0, gap);
            ImGui.SetCursorPosY(
               topY +
               (playSize - skipSize) * 0.5f);

            using (UiDisabled.Scope(video.ManualTransportLocked || StudioTvTransportActive && !CanSkipStudioSong))
            if (DrawTransportGhostButton(
                    FontAwesomeIcon.StepForward,
                    skipSize))
            {
                if (TryStudioSongTransport(next: true)) { }
                else if (localVideo || HalloweenActivityActive)
                {
                    video.Seek(
                        MathF.Min(
                            duration,
                            position + 10f));
                }
                else
                {
                    queue.Advance();
                }
            }

            if (showEndPlaybackButton)
            {
                ImGui.SameLine(0, gap);

                ImGui.SetCursorPosY(
                    topY +
                    (playSize - stopSize) * 0.5f);

                var endPlaybackClicked =
                    DrawTransportStopButton(stopSize);

                if (ImGui.IsItemHovered())
                {
                    UiFonts.SetTooltip(
                        "End playback and despawn TV");
                }

                if (endPlaybackClicked)
                {
                    EndPlaybackAndDespawnTv();
                }
            }

            ImGui.SetCursorPos(
                new Vector2(
                    volX,
                    topY +
                    (playSize - skipSize) * 0.5f));

            DrawBottomVolume(
                volIconW,
                volSliderW,
                Ui(18f));

            ImGui.SetCursorPos(new Vector2(
                volX - Ui(76f),
                topY + (playSize - Ui(26f)) * 0.5f));
            DrawSubtitleButton("bottomSubtitles", Ui(26f), openAbove: true);

            ImGui.SetCursorPos(new Vector2(
                volX - Ui(38f),
                topY + (playSize - Ui(26f)) * 0.5f));
            DrawBottomVisualizerButton(mediaTitle, Ui(26f));

            if (liveAudio)
            {
                seekDragging = false;
                var elapsed = FormatTime(position);
                const string endLabel = "Listening";
                ImGui.SetCursorPos(new Vector2(Ui(84f), topY + playSize + Ui(20f)));
                ImGui.TextColored(MutedText, elapsed);
                ImGui.SameLine(0, 8);
                var trackWidth = MathF.Max(Ui(20f), width - ImGui.CalcTextSize(elapsed).X - ImGui.CalcTextSize(endLabel).X - Ui(120f));
                var track = ImGui.GetCursorScreenPos();
                // No slider/drag target or seek handle: the fixed end marker
                // represents the live edge, not a position within a known song.
                ImGui.Dummy(new Vector2(trackWidth, lineH));
                var trackTop = track + new Vector2(0, (lineH - Ui(6f)) * .5f);
                barDrawList.AddRectFilled(trackTop, trackTop + new Vector2(trackWidth, Ui(6f)),
                    ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, .3f)), Ui(3f));
                barDrawList.AddRectFilled(trackTop + new Vector2(trackWidth - Ui(3f), 0), trackTop + new Vector2(trackWidth, Ui(6f)),
                    ImGui.GetColorU32(Accent), Ui(1f));
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Live audio · seeking unavailable");
                ImGui.SameLine(0, 8);
                ImGui.TextColored(MutedText, endLabel);
            }
            else
            {
            // --- Timestamp under title ---
            var timeLeft = FormatTime(position);
                var timeRight = FormatTime(duration);



                // --- Seek row ---
                var seekY = topY + playSize + Ui(18f);

                var timeLeftWidth = ImGui.CalcTextSize(timeLeft).X;

                ImGui.SetCursorPos(new Vector2(Ui(84f), seekY + Ui(2f)));

                ImGui.TextColored(MutedText, timeLeft);

                ImGui.SameLine(0, 8);

                ImGui.SetNextItemWidth(
                   width - timeLeftWidth - ImGui.CalcTextSize(timeRight).X - Ui(120f));

                using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(6f))
                           .Push(ImGuiStyleVar.GrabRounding, Ui(6f))
                           .Push(ImGuiStyleVar.FramePadding, new Vector2(0, 0)))
                {
                    using var seekLocked = UiDisabled.Scope(video.ManualTransportLocked);
                    ImGui.SliderFloat("##bottomSeek", ref seekPreview, 0f, MathF.Max(duration, 0.01f), "");
                }

                seekDragging = ImGui.IsItemActive();

                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                video.Seek(
seekPreview);

                queue.SaveCurrentProgress();
            }

                ImGui.SameLine(0, 8);
                ImGui.TextColored(MutedText, timeRight);
            }
             
            }
        }
    

    private void DrawBottomVisualizerButton(string mediaTitle, float size)
    {
        var audioOnly = video.IsAudioOnly;
        var url = video.CurrentMediaUrl;
        var canChange = audioOnly && !string.IsNullOrWhiteSpace(url);
        var origin = ImGui.GetCursorScreenPos();
        bool clicked;
        using (UiDisabled.Scope(!canChange))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(5f))
            .Push(ImGuiStyleVar.FrameBorderSize, Ui(1f)))
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.10f))
            .Push(ImGuiCol.ButtonHovered, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.24f))
            .Push(ImGuiCol.ButtonActive, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.36f))
            .Push(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, canChange ? 0.4f : 0.15f)))
        {
            clicked = ImGui.Button("##bottomVisualizer", new Vector2(size, size));
        }
        var color = ImGui.GetColorU32(canChange ? AccentHover : new Vector4(MutedText.X, MutedText.Y, MutedText.Z, 0.45f));
        var draw = ImGui.GetWindowDrawList();
        ReadOnlySpan<float> heights = [0.28f, 0.48f, 0.68f, 0.48f, 0.28f];
        for (var index = 0; index < heights.Length; index++)
        {
            var height = size * heights[index];
            var min = origin + new Vector2(size * (0.19f + index * 0.13f), (size - height) * 0.5f);
            draw.AddRectFilled(min, min + new Vector2(size * 0.075f, height), color, Ui(0.6f));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiFonts.SetTooltip(!audioOnly ? "Available for audio-only streams" :
                stream.Mode == StreamMode.Viewing ? "View the host’s music scene" :
                "Adjust Scene");
        if (clicked && canChange)
        {
            var current = queue.Current;
            if (current is null || !AudioVisualizerSelection.IsSameMedia(current.Url, url!))
                current = new VideoQueueEntry(url!, mediaTitle, "Audio", null, null);
            OpenAudioPresentationSettings(current);
        }
    }

    private void DrawSubtitleButton(string id, float size, bool openAbove = false, bool boundToPlugin = false)
    {
        using var subtitleId = ImRaii.PushId(id);
        var subtitleUrl = video.CurrentMediaUrl;
        var isYouTubeVideo = subtitleUrl is not null &&
                             VideoUrlResolver.IsYouTubeUrl(subtitleUrl);
        var isDailymotionVideo = subtitleUrl is not null &&
                                 VideoUrlResolver.IsDailymotionUrl(subtitleUrl);
        var isArchiveVideo = InternetArchiveSubtitles.IsArchiveVideoUrl(subtitleUrl);
        var isLocalVideo = video.IsPlayingLocalVideo;
        var remoteLocalVideo = stream.Mode == StreamMode.Viewing &&
            stream.CurrentRoomState?.MediaTitle?.StartsWith("Local Video", StringComparison.OrdinalIgnoreCase) == true &&
            subtitleUrl is not null && Plugin.IsLiveBroadcastUrl(subtitleUrl);
        var hasSelectableSubtitles = isYouTubeVideo || isDailymotionVideo ||
                                     isArchiveVideo || isLocalVideo || remoteLocalVideo;
        if (stream.Mode != StreamMode.Viewing &&
            !string.Equals(subtitleChoiceUrl, subtitleUrl, StringComparison.Ordinal))
        {
            subtitleChoiceUrl = subtitleUrl;
            stream.SubtitleLanguage = null;
            stream.SubtitleName = null;
        }
        var canChange = hasSelectableSubtitles && stream.Mode != StreamMode.Viewing;
        using (UiDisabled.Scope(!canChange))
        {
            if (DrawTransportGhostButton(FontAwesomeIcon.ClosedCaptioning, size))
                ImGui.OpenPopup("##subtitles");
        }
        var buttonMin = ImGui.GetItemRectMin();
        var buttonMax = ImGui.GetItemRectMax();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiFonts.SetTooltip(hasSelectableSubtitles ? "Manage Subtitles" : "No subtitles available");
        if (!boundToPlugin && openAbove && ImGui.IsPopupOpen("##subtitles"))
            ImGui.SetNextWindowPos(new Vector2(buttonMax.X, buttonMin.Y - Ui(6f)),
                ImGuiCond.Always, new Vector2(1f, 1f));
        var popupHeight = Ui(84);
        if (boundToPlugin && ImGui.IsPopupOpen("##subtitles"))
        {
            if (isLocalVideo) popupHeight = Ui(32) + (video.GetEmbeddedSubtitleTracks().Count + 1) * (ImGui.GetTextLineHeight() + Ui(8));
            else if (isArchiveVideo && InternetArchiveSubtitles.GetTracks(subtitleUrl!) is { } archiveTracks)
                popupHeight = Ui(32) + (archiveTracks.Count(t => t.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)) + 1) *
                    (ImGui.GetTextLineHeight() + Ui(8)) + (archiveTracks.Any(t => !t.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)) ? Ui(270) : 0);
            else
            {
                var tracks = video.GetSubtitleTracks();
                popupHeight = Ui(32) + (tracks.Count(IsEnglishSubtitleTrack) + 1) * (ImGui.GetTextLineHeight() + Ui(8)) +
                    (tracks.Any(t => !IsEnglishSubtitleTrack(t)) ? Ui(270) : 0);
            }
        }
        if (boundToPlugin ? BeginPartyPopup("##subtitles", new(Ui(320), Math.Clamp(popupHeight, Ui(84), Ui(460)))) :
            UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup("##subtitles")))
        {
            if (!canChange)
            {
                ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
                return;
            }
            var subtitlesOff = isLocalVideo
                ? video.GetSelectedSubtitleId() is null or "no"
                : stream.SubtitleLanguage is null;
            if (ImGui.Selectable("Off", subtitlesOff))
            {
                video.SelectSubtitle(null, null);
                if (!isLocalVideo)
                {
                    stream.SubtitleLanguage = null;
                    stream.SubtitleName = null;
                }
            }
            if (subtitlesOff)
                DrawSubtitleSelectionTick();
            if (isLocalVideo)
            {
                var embeddedTracks = video.GetEmbeddedSubtitleTracks();
                if (embeddedTracks.Count == 0)
                    ImGui.TextDisabled("No embedded subtitles found");
                foreach (var track in embeddedTracks)
                    DrawLocalSubtitleTrack(track);
            }
            else if (isArchiveVideo)
            {
                var archiveTracks = InternetArchiveSubtitles.GetTracks(subtitleUrl!);
                if (archiveTracks is null)
                    ImGui.TextDisabled("Finding subtitles...");
                else if (archiveTracks.Count == 0)
                    ImGui.TextDisabled("No subtitles available");
                else
                {
                    foreach (var track in archiveTracks.Where(track =>
                                 track.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)))
                        DrawArchiveSubtitleTrack(track);
                    var otherTracks = archiveTracks.Where(track =>
                        !track.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (otherTracks.Length > 0)
                    {
                        ImGui.Separator();
                        UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##otherArchiveSubtitles", new Vector2(Ui(270f), Ui(250f)), false));
                        foreach (var track in otherTracks)
                            DrawArchiveSubtitleTrack(track);
                        ImGui.EndChild();
                    }
                }
            }
            else
            {
                var tracks = video.GetSubtitleTracks();
                if (tracks.Count == 0)
                    ImGui.TextDisabled("No subtitles available yet");
                if (isDailymotionVideo)
                {
                    for (var i = 0; i < tracks.Count; i++)
                        DrawSubtitleTrack(tracks[i], $"Subtitles (source {i + 1})");
                }
                else
                {
                    var englishTracks = tracks.Where(IsEnglishSubtitleTrack).ToArray();
                    var otherTracks = tracks.Where(track => !IsEnglishSubtitleTrack(track))
                        .OrderBy(track => track.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(track => track.Language, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    foreach (var track in englishTracks)
                        DrawSubtitleTrack(track);
                    if (otherTracks.Length > 0)
                    {
                        ImGui.Separator();
                        UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##otherYouTubeSubtitles",
                            new Vector2(Ui(270f), Ui(250f)), false));
                        foreach (var track in otherTracks)
                            DrawSubtitleTrack(track);
                        ImGui.EndChild();
                    }
                }
            }
            ImGui.EndPopup();
        }

    }

    private static bool IsEnglishSubtitleTrack(SubtitleTrack track) =>
        string.Equals(track.Language, "en", StringComparison.OrdinalIgnoreCase) ||
        track.Language.StartsWith("en-", StringComparison.OrdinalIgnoreCase) ||
        track.Name.StartsWith("English", StringComparison.OrdinalIgnoreCase);

    private void DrawSubtitleTrack(SubtitleTrack track, string? displayLabel = null)
    {
        var label = displayLabel ?? (string.IsNullOrWhiteSpace(track.Name)
            ? track.Language
            : track.Name);
        if (string.IsNullOrWhiteSpace(label)) label = $"Track {track.Id}";
        var selected = string.Equals(video.GetSelectedSubtitleId(),
            track.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        var clicked = ImGui.Selectable($"{label}##sub{track.Id}", selected);
        if (selected)
            DrawSubtitleSelectionTick();
        if (clicked &&
            video.SelectSubtitle(track.Language, track.Name))
        {
            stream.SubtitleLanguage = track.Language;
            stream.SubtitleName = track.Name;
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawArchiveSubtitleTrack(InternetArchiveSubtitle track)
    {
        var selectedId = video.GetSelectedSubtitleId();
        var selected = video.GetSubtitleTracks().Any(candidate =>
            candidate.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == selectedId &&
            string.Equals(candidate.Language, track.Language, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.Name, track.Name, StringComparison.OrdinalIgnoreCase));
        var clicked = ImGui.Selectable($"{track.Name}##archiveSub{track.Language}", selected);
        if (selected)
            DrawSubtitleSelectionTick();
        if (clicked && video.SelectSubtitle(track.Language, track.Name))
        {
            stream.SubtitleLanguage = track.Language;
            stream.SubtitleName = track.Name;
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawLocalSubtitleTrack(SubtitleTrack track)
    {
        var label = !string.IsNullOrWhiteSpace(track.Name)
            ? track.Name
            : !string.IsNullOrWhiteSpace(track.Language) ? track.Language : $"Track {track.Id}";
        if (stream.Mode == StreamMode.Hosting &&
            (!track.FfmpegStreamIndex.HasValue || !IsBroadcastableTextSubtitle(track.Codec)))
            label += " (local only)";
        var selected = string.Equals(video.GetSelectedSubtitleId(),
            track.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        var clicked = ImGui.Selectable($"{label}##localSub{track.Id}", selected);
        if (selected)
            DrawSubtitleSelectionTick();
        if (clicked && video.SelectSubtitleTrack(track.Id))
            ImGui.CloseCurrentPopup();
    }

    private static void DrawSubtitleSelectionTick()
    {
        var right = ImGui.GetItemRectMax();
        var top = ImGui.GetItemRectMin();
        using (UiFonts.PushIcon())
        {
            var glyph = FontAwesomeIcon.Check.ToIconString();
            var glyphSize = ImGui.CalcTextSize(glyph);
            UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), new Vector2(right.X - glyphSize.X - Ui(8f),
                    top.Y + (right.Y - top.Y - glyphSize.Y) * 0.5f), ImGui.GetColorU32(Accent), glyph);
        }
    }

    private void DrawBottomVolume(float iconSize, float sliderW, float rowH)
    {
        var muted = Plugin.Cfg.Muted;
        if (DrawTransportGhostButton(
                muted || Plugin.Cfg.Volume == 0 ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp,
                iconSize))
        {
            Plugin.Cfg.Muted = !Plugin.Cfg.Muted;
            ApplyListeningVolume();
            Plugin.Cfg.Save();
        }

        ImGui.SameLine(0, Ui(6f));
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Ui(5f));
        ImGui.SetNextItemWidth(sliderW);

        var volume = Plugin.Cfg.Volume;

        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(4f))
                   .Push(ImGuiStyleVar.GrabRounding, Ui(4f))
                   .Push(ImGuiStyleVar.FramePadding, UiVec(0, -2f)))
        {
            if (ImGui.SliderInt("##bottomVol", ref volume, 0, 130, ""))
            {
                Plugin.Cfg.Volume = volume;
                if (volume > 0 && Plugin.Cfg.Muted)
                {
                    Plugin.Cfg.Muted = false;
                }

                ApplyListeningVolume();
            }
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            Plugin.Cfg.Save();
        }
        var sliderMin = ImGui.GetItemRectMin();
        var sliderMax = ImGui.GetItemRectMax();
        ImGui.SetCursorScreenPos(new Vector2(sliderMax.X + Ui(8f),
            (sliderMin.Y + sliderMax.Y - ImGui.GetTextLineHeight()) * 0.5f));
        ImGui.TextColored(volume > 100 ? Gold : Vector4.One, $"{volume}%");
    }

    private static bool DrawTransportGhostButton(FontAwesomeIcon icon, float size)
    {
        var origin = ImGui.GetCursorScreenPos();
        ImGui.PushID((int)icon + (int)(size * 10));
        var clicked = ImGui.InvisibleButton("##transportGhost", new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        ImGui.PopID();

        using (UiFonts.PushIcon())
        {
            var glyph = icon.ToIconString();
            var textSize = ImGui.CalcTextSize(glyph);
            UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), origin + new Vector2(size, size) / 2f - textSize / 2f, ImGui.GetColorU32(hovered ? Vector4.One : MutedText), glyph);
        }

        return clicked;
    }

    private static bool DrawTransportStopButton(
     float size)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        ImGui.PushID("##transportStop");

        var clicked =
            ImGui.InvisibleButton(
                "##hit",
                new Vector2(size, size));

        var hovered =
            ImGui.IsItemHovered();

        var held =
            ImGui.IsItemActive();

        ImGui.PopID();

        var drawList =
            ImGui.GetWindowDrawList();

        var buttonMin =
            origin;

        var buttonMax =
            origin +
            new Vector2(size, size);

        var background =
            held
                ? new Vector4(
                    0.32f,
                    0.08f,
                    0.11f,
                    1f)
                : hovered
                    ? new Vector4(
                        0.24f,
                        0.07f,
                        0.10f,
                        1f)
                    : new Vector4(
                        0.10f,
                        0.06f,
                        0.08f,
                        1f);

        var border =
            hovered
                ? new Vector4(
                    1f,
                    0.34f,
                    0.40f,
                    1f)
                : new Vector4(
                    0.86f,
                    0.20f,
                    0.28f,
                    0.85f);

        drawList.AddRectFilled(
            buttonMin,
            buttonMax,
            ImGui.GetColorU32(background),
            7f);

        drawList.AddRect(
            buttonMin,
            buttonMax,
            ImGui.GetColorU32(border),
            7f,
            ImDrawFlags.None,
            1f);

        using (UiFonts.PushIcon())
        {
            var glyph =
                FontAwesomeIcon.Stop
                    .ToIconString();

            var textSize =
                ImGui.CalcTextSize(glyph);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), origin +
                new Vector2(size, size) / 2f -
                textSize / 2f, ImGui.GetColorU32(
                    hovered
                        ? new Vector4(
                            1f,
                            0.58f,
                            0.62f,
                            1f)
                        : new Vector4(
                            1f,
                            0.32f,
                            0.38f,
                            1f)), glyph);
        }

        return clicked;
    }

    private void EndPlaybackAndDespawnTv()
    {
        if (TryRequestStudioStop()) return;
        EndPlaybackAndDespawnTvConfirmed();
    }

    private void EndPlaybackAndDespawnTvConfirmed()
    {
        if (HalloweenActivityActive) { EndHalloween(); return; }
        if (StudioOwnsPlaybackControls) StopStudioBroadcast();
        var engine =
            screenController.Engine;

        // Game broadcasting has its own lifecycle and must not be
        // interrupted by the normal video stop control.
        if (engine.IsPlayingGame || engine.IsPlayingBrowser)
        {
            return;
        }

        var wasHosting =
            stream.Mode ==
            StreamMode.Hosting;

        // Save the current timestamp, stop MPV, and remove the virtual
        // screen without deleting anything from the active saved queue.
        queue.StopPlayback();

        if (wasHosting)
        {
            // Keep the room open while immediately telling viewers
            // that the host is no longer sharing any content.
            _ = stream.PublishStateAsync(
                null,
                0d,
                true,
                null,
                null,
                null,
                null,
                null);
        }
    }

    private static bool DrawTransportPlayButton(bool isPaused, float size)
    {
        var origin = ImGui.GetCursorScreenPos();
        ImGui.PushID("##transportPlay");
        var clicked = ImGui.InvisibleButton("##hit", new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        ImGui.PopID();

        var center = origin + new Vector2(size, size) / 2f;
        var fill = hovered ? AccentHover : Accent;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddCircleFilled(center, size * 0.5f, ImGui.GetColorU32(fill));

        using (UiFonts.PushIcon())
        {
            var glyph = (isPaused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause).ToIconString();
            var textSize = ImGui.CalcTextSize(glyph);
            // Play triangle sits optically left in FA — nudge so it reads centered.
            var nudge = isPaused ? UiVec(1.2f, 0f) : Vector2.Zero;
            UiFonts.DrawText(drawList, ImGui.GetFontSize(), center - textSize / 2f + nudge, ImGui.GetColorU32(Vector4.One), glyph);
        }

        return clicked;
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)] + "…";
}

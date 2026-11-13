using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static Vector4 ScreenControlBg => FrameBg;

    private static Vector4 ScreenControlBgHover => FrameBgHover;

    private readonly bool[] tvGizmoKeysDown = new bool[256];
    private bool capturingTvGizmoKey;
    internal bool IsCapturingTvGizmoKey => capturingTvGizmoKey;

    private enum ScreenTab { Placement, Settings, Frame }
    private ScreenTab screenTab;

    private void DrawScreenControls()
    {
        if (HalloweenActivityActive)
        {
            ImGui.TextWrapped("The Halloween show is using its fixed TV placement and synchronised frame. End the show to change the TV setup.");
            if (ActivePartyButton("End Halloween show", new(-1, Ui(38)), danger: true)) EndHalloween();
            return;
        }
        var engine = screenController.Engine;
        var width = ImGui.GetContentRegionAvail().X;
        var gap = Ui(10f);
        var toolbar = ImGui.GetCursorScreenPos();
        using (UiFonts.PushRelative(.90f))
            ImGui.TextColored(engine.IsActive ? Good : MutedText, engine.IsActive ? "●  TV visible" : "○  TV hidden");
        var spawnWidth = MathF.Min(Ui(180f), width * .45f);
        ImGui.SetCursorScreenPos(toolbar + new Vector2(width - spawnWidth, 0));
        DrawPlayerTvButton(spawnWidth, height: 40f, recenterOnSpawn: true);
        ImGui.SetCursorScreenPos(toolbar + UiVec(0, 52f));
        var tabWidth = (width - gap * 2) / 3;
        if (GameLayoutButton("Placement", FontAwesomeIcon.Crosshairs, tabWidth, screenTab == ScreenTab.Placement, height: 42)) screenTab = ScreenTab.Placement;
        ImGui.SameLine(0, gap);
        if (GameLayoutButton("Settings", FontAwesomeIcon.Cog, tabWidth, screenTab == ScreenTab.Settings, height: 42)) screenTab = ScreenTab.Settings;
        ImGui.SameLine(0, gap);
        if (GameLayoutButton("Frame", FontAwesomeIcon.Image, tabWidth, screenTab == ScreenTab.Frame, height: 42)) screenTab = ScreenTab.Frame;
        AddScreenCardGap();

        switch (screenTab)
        {
            case ScreenTab.Placement:
                DrawCompactScreenWatchPartySyncControl(width);
                AddScreenCardGap();
                using (UiDisabled.Scope(stream.Mode == StreamMode.Viewing && PartySyncTvPlacement))
                    DrawScreenPlacementCard(engine, width, Ui(390f));
                if (!string.IsNullOrWhiteSpace(screenClipboardStatus))
                    ImGui.TextColored(screenClipboardStatusIsError ? Danger : Good, screenClipboardStatus);
                AddScreenCardGap();
                GameLayoutPanel("##screenEditHint", width, Ui(82f), () =>
                {
                    var hintOrigin = ImGui.GetCursorScreenPos();
                    DrawScreenGizmoIllustration(hintOrigin, UiVec(56f, 48f));
                    ImGui.SetCursorScreenPos(hintOrigin + UiVec(72f, 0f));
                    ImGui.TextUnformatted("Edit TV in world");
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Ui(72f));
                    using (ImRaii.TextWrapPos(0))
                        ImGui.TextColored(MutedText, $"Hold {GetTvGizmoKeyName(Plugin.Cfg.TvGizmoKey)} to move, rotate and resize the TV.");
                });
                AddScreenCardGap();
                DrawScreenPresetsCard(engine);
                break;
            case ScreenTab.Settings:
                ImGui.TextColored(MutedText, "Applies only to your client.");
                ImGui.Spacing();
                DrawScreenVisibilityCard(engine, width, Ui(374f));
                AddScreenCardGap();
                DrawScreenGizmoShortcutCard();
                break;
            case ScreenTab.Frame:
                DrawTvFramePage(width);
                break;
        }
        ImGui.Dummy(UiVec(0f, 24f));
    }

    private static void AddScreenCardGap()
    {
        // The previous item has already added ImGui's vertical item spacing.
        // Match the 12px gap between the side-by-side screen cards exactly.
        ImGui.SetCursorPosY(
            ImGui.GetCursorPosY() + Ui(12f) - ImGui.GetStyle().ItemSpacing.Y);
    }

    private void DrawScreenGizmoShortcutCard()
    {
        var syncingFromHost =
            stream.Mode == StreamMode.Viewing && PartySyncTvPlacement;
        var width = ImGui.GetContentRegionAvail().X;
        var compact = width < Ui(650f);
        var height = Ui(compact ? 184f : 112f);
        var padding = Ui(16f);
        var tileSize = UiVec(84f, 72f);
        var controlWidth = Ui(158f);

        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
                   .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
                   .Push(ImGuiStyleVar.WindowPadding, UiVec(16f, 14f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
                   .Push(ImGuiCol.Border, BorderSubtle))
        using (var card = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                   "##screenGizmoShortcutCard",
                   new Vector2(width, height),
                   true,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (!card)
            {
                return;
            }

            var tileTopLeft = ImGui.GetCursorScreenPos();
            DrawScreenGizmoIllustration(tileTopLeft, tileSize);

            var descriptionX = padding + tileSize.X + Ui(16f);
            ImGui.SetCursorPos(new Vector2(descriptionX, Ui(19f)));
            using var uiFontScale4590=UiFonts.PushRelative(1f, emphasis: true);
            ImGui.TextColored(Hex(0xE2E7EF), "Edit TV in world");
            uiFontScale4590.Dispose();

            ImGui.SetCursorPos(new Vector2(descriptionX, Ui(45f)));
            var descriptionWidth = compact
                ? width - descriptionX - padding
                : width - descriptionX - controlWidth - Ui(28f);
            ImGui.PushTextWrapPos(
                ImGui.GetCursorPosX() + descriptionWidth);
            using var uiFontScale5058=UiFonts.PushRelative(.90f);
            ImGui.TextColored(
                syncingFromHost ? Gold : MutedText,
                syncingFromHost
                    ? "Turn off host TV-position sync to use the gizmo."
                    : "Hold your shortcut to move, rotate, and resize the TV directly.");
            uiFontScale5058.Dispose();
            ImGui.PopTextWrapPos();

            var controlX = compact ? padding : width - padding - controlWidth;
            var controlY = compact ? Ui(99f) : Ui(19f);
            ImGui.SetCursorPos(new Vector2(controlX, controlY));
            using var uiFontScale5653=UiFonts.PushRelative(.90f);
            ImGui.TextColored(MutedText, "Gizmo shortcut");
            uiFontScale5653.Dispose();

            ImGui.SetCursorPos(new Vector2(controlX, controlY + Ui(20f)));
            var keyLabel = capturingTvGizmoKey
                ? "Press a key..."
                : GetTvGizmoKeyName(Plugin.Cfg.TvGizmoKey);
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7f))
                       .Push(ImGuiStyleVar.FrameBorderSize, Ui(1f)))
            using (ImRaii.PushColor(ImGuiCol.Button, ScreenControlBg)
                       .Push(ImGuiCol.ButtonHovered, ScreenControlBgHover)
                       .Push(ImGuiCol.ButtonActive, ScreenControlBgHover)
                       .Push(ImGuiCol.Border, Accent))
            {
                if (ImGui.Button($"{keyLabel}##tvGizmoKey", new Vector2(controlWidth, Ui(31f))))
                {
                    capturingTvGizmoKey = true;
                    SnapshotTvGizmoKeys();
                }
            }
            if (ImGui.IsItemHovered())
            {
                UiFonts.SetTooltip("Click, then press the key to hold while editing the TV. Esc cancels.");
            }

            if (capturingTvGizmoKey)
            {
                CaptureTvGizmoKey();
            }

            ImGui.SetCursorPos(new Vector2(controlX, controlY + Ui(56f)));
            using var uiFontScale7050=UiFonts.PushRelative(.90f);
            using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero)
                       .Push(ImGuiCol.ButtonHovered, CardBgHover)
                       .Push(ImGuiCol.ButtonActive, CardBgHover)
                       .Push(ImGuiCol.Text, Accent))
            {
                if (ImGui.Button("Reset to Tab##tvGizmoKeyReset"))
                {
                    Plugin.Cfg.TvGizmoKey = (int)VirtualKey.TAB;
                    Plugin.Cfg.Save();
                    capturingTvGizmoKey = false;
                }
            }
            uiFontScale7050.Dispose();
        }
    }

    private static void DrawScreenGizmoIllustration(Vector2 origin, Vector2 size)
    {
        var drawList = ImGui.GetWindowDrawList();
        var background = ImGui.GetColorU32(ScreenControlBg);
        var border = ImGui.GetColorU32(BorderSubtle);
        var purple = ImGui.GetColorU32(Accent);
        var red = ImGui.GetColorU32(new Vector4(1f, 0.34f, 0.38f, 1f));
        var green = ImGui.GetColorU32(new Vector4(0.32f, 0.9f, 0.58f, 1f));
        var blue = ImGui.GetColorU32(new Vector4(0.36f, 0.64f, 1f, 1f));
        var center = origin + size * 0.5f;
        drawList.AddRectFilled(origin, origin + size, background, Ui(8f));
        drawList.AddRect(origin, origin + size, border, Ui(8f));
        drawList.AddCircle(center, Ui(22f), purple, 32, Ui(2f));
        drawList.AddLine(center, center + UiVec(23f, 11f), red, Ui(2.5f));
        drawList.AddLine(center, center - UiVec(0f, 24f), green, Ui(2.5f));
        drawList.AddLine(center, center + UiVec(-20f, 12f), blue, Ui(2.5f));
        drawList.AddCircleFilled(center + UiVec(23f, 11f), Ui(3f), red);
        drawList.AddCircleFilled(center - UiVec(0f, 24f), Ui(3f), green);
        drawList.AddCircleFilled(center + UiVec(-20f, 12f), Ui(3f), blue);
        drawList.AddCircleFilled(center, Ui(4f), purple);
    }

    private void SnapshotTvGizmoKeys()
    {
        for (var key = 8; key < tvGizmoKeysDown.Length; key++)
        {
            tvGizmoKeysDown[key] = (GetAsyncKeyState(key) & 0x8000) != 0;
        }
    }

    private void CaptureTvGizmoKey()
    {
        for (var key = 8; key < tvGizmoKeysDown.Length; key++)
        {
            var down = (GetAsyncKeyState(key) & 0x8000) != 0;
            var pressed = down && !tvGizmoKeysDown[key];
            tvGizmoKeysDown[key] = down;
            if (!pressed)
            {
                continue;
            }

            if (key == (int)VirtualKey.ESCAPE)
            {
                capturingTvGizmoKey = false;
                return;
            }

            if (!Enum.IsDefined(typeof(VirtualKey), (VirtualKey)key) ||
                key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C)
            {
                continue;
            }

            Plugin.Cfg.TvGizmoKey = key;
            Plugin.Cfg.Save();
            capturingTvGizmoKey = false;
            return;
        }
    }

    private static string GetTvGizmoKeyName(int key) => key switch
    {
        0x09 => "Tab",
        0x0D => "Enter",
        0x20 => "Space",
        >= 0x30 and <= 0x39 => ((char)key).ToString(),
        >= 0x41 and <= 0x5A => ((char)key).ToString(),
        >= 0x70 and <= 0x87 => $"F{key - 0x6F}",
        _ => Enum.GetName(typeof(VirtualKey), (VirtualKey)key) ?? $"Key {key}",
    };

    private void DrawCompactScreenWatchPartySyncControl(float width)
    {
        var isViewer = stream.Mode == StreamMode.Viewing;
        var isHost = stream.Mode == StreamMode.Hosting;
        var inWatchParty = isViewer || isHost;
        var syncEnabled = isHost || partySyncTvPlacement;
        var wasSyncEnabled = partySyncTvPlacement;

        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
                   .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
                   .Push(ImGuiStyleVar.WindowPadding, UiVec(16f, 12f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
                   .Push(ImGuiCol.Border, BorderSubtle))
        using (var card = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                   "##compactScreenWatchPartySync",
                   new Vector2(width, Ui(76f)),
                   true,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (!card)
            {
                return;
            }

            DrawScreenSyncHeading(
                "Match TV position & size with host",
                !inWatchParty
                    ? "Join a Watch Party to use host screen syncing."
                    : isHost
                        ? "Viewers can match the position and size of your TV."
                        : partySyncTvPlacement
                            ? "Following the host's TV position and size."
                            : "Using your own local TV placement.");

            ImGui.SetCursorPos(
                new Vector2(
                    ImGui.GetWindowWidth() - Ui(56f),
                    Ui(22f)));

            DrawPartyPlacementToggle(
                ref syncEnabled,
                disabled: !isViewer);

            var disabledToggleHovered =
                !isViewer &&
                ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);

            if (isViewer)
            {
                if (partySyncTvPlacement != syncEnabled) SetPartySyncTvPlacement(syncEnabled);
                if (wasSyncEnabled && !partySyncTvPlacement)
                {
                    ApplySavedViewerScreenPlacement();
                }
            }

            if (disabledToggleHovered)
            {
                UiFonts.SetTooltip(
                    isHost
                        ? "The host provides the shared TV placement."
                        : "Join a Watch Party to change this setting.");
            }
        }
    }

    private void DrawScreenPlacementCard(
        VideoEngine engine,
        float width,
        float height)
    {
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
                   .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
                   .Push(ImGuiStyleVar.WindowPadding, UiVec(16f, 14f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
                   .Push(ImGuiCol.Border, BorderSubtle))
        using (var card = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                   "##screenPlacementCard",
                   new Vector2(width, height),
                   true,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (!card)
            {
                return;
            }

            DrawScreenCardHeading(
                FontAwesomeIcon.Crosshairs,
                "TV placement",
                "Drag the in-world TV, or fine-tune it below.");

            ImGui.Dummy(UiVec(0f, 10f));

            var position = engine.ScreenPosition;
            var yaw = engine.ScreenYaw;
            var disableFixedScaleRatio =
                VideoEngine.IndependentScreenScalingEnabled &&
                engine.DisableFixedScreenScaleRatio;
            var widthScale = engine.ScreenWidthScale;
            var heightScale = engine.ScreenHeightScale;
            var changed = false;

            DrawCompactScreenLabel("Position");
            ImGui.Dummy(UiVec(0f, 3f));

            var axisGap = Ui(8f);
            var axisWidth =
                (ImGui.GetContentRegionAvail().X - axisGap * 2f) /
                3f;

            changed |= DrawScreenAxisField(
                "X",
                "##compactScreenX",
                ref position.X,
                axisWidth);
            ImGui.SameLine(0f, axisGap);
            changed |= DrawScreenAxisField(
                "Y",
                "##compactScreenY",
                ref position.Y,
                axisWidth);
            ImGui.SameLine(0f, axisGap);
            changed |= DrawScreenAxisField(
                "Z",
                "##compactScreenZ",
                ref position.Z,
                axisWidth);

            ImGui.Dummy(UiVec(0f, 0f));

            DrawCompactScreenValueLabel(
                "Rotation",
                $"{NormalizeScreenDegrees(yaw):0}°");

            using (ScreenSliderStyle())
            {
                var sliderWidth = ImGui.GetContentRegionAvail().X;
                DrawScreenSliderTrack(
                    Math.Clamp(
                        (yaw + MathF.PI * 2f) /
                        (MathF.PI * 4f),
                        0f,
                        1f),
                    sliderWidth);
                ImGui.SetNextItemWidth(sliderWidth);
                changed |= ImGui.SliderAngle(
                    "##compactScreenYaw",
                    ref yaw,
                    -360f,
                    360f,
                    "");
            }

            ImGui.Dummy(UiVec(0f, 0f));

            if (disableFixedScaleRatio)
            {
                DrawCompactScreenValueLabel(
                    "Width",
                    $"{widthScale:0.00}×");
                using (ScreenSliderStyle())
                {
                    var sliderWidth = ImGui.GetContentRegionAvail().X;
                    DrawScreenSliderTrack(
                        NormalizeScreenSliderValue(
                            widthScale,
                            VideoEngine.MinScreenScale,
                            VideoEngine.MaxScreenScale),
                        sliderWidth);
                    ImGui.SetNextItemWidth(sliderWidth);
                    changed |= ImGui.SliderFloat(
                        "##compactScreenWidth",
                        ref widthScale,
                        VideoEngine.MinScreenScale,
                        VideoEngine.MaxScreenScale,
                        "");
                }

                DrawCompactScreenValueLabel(
                    "Height",
                    $"{heightScale:0.00}×");
                using (ScreenSliderStyle())
                {
                    var sliderWidth = ImGui.GetContentRegionAvail().X;
                    DrawScreenSliderTrack(
                        NormalizeScreenSliderValue(
                            heightScale,
                            VideoEngine.MinScreenScale,
                            VideoEngine.MaxScreenScale),
                        sliderWidth);
                    ImGui.SetNextItemWidth(sliderWidth);
                    changed |= ImGui.SliderFloat(
                        "##compactScreenHeight",
                        ref heightScale,
                        VideoEngine.MinScreenScale,
                        VideoEngine.MaxScreenScale,
                        "");
                }
            }
            else
            {
                DrawCompactScreenValueLabel(
                    "Size",
                    $"{widthScale:0.00}×");
                using (ScreenSliderStyle())
                {
                    var sliderWidth = ImGui.GetContentRegionAvail().X;
                    DrawScreenSliderTrack(
                        NormalizeScreenSliderValue(
                            widthScale,
                            VideoEngine.MinScreenScale,
                            VideoEngine.MaxScreenScale),
                        sliderWidth);
                    ImGui.SetNextItemWidth(sliderWidth);
                    changed |= ImGui.SliderFloat(
                        "##compactScreenScale",
                        ref widthScale,
                        VideoEngine.MinScreenScale,
                        VideoEngine.MaxScreenScale,
                        "");
                }

                heightScale = widthScale;
            }

            if (changed)
            {
                SaveScreenTransform(
                    engine,
                    position,
                    yaw,
                    disableFixedScaleRatio,
                    widthScale,
                    heightScale);
            }

            var buttonGap = Ui(8f);
            var buttonHeight = Ui(44f);
            var available = ImGui.GetContentRegionAvail().X;
            var secondaryWidth =
                MathF.Floor(
                    (available - buttonGap * 2f) * 0.27f);
            var recenterWidth =
                MathF.Max(
                    Ui(120f),
                    available - secondaryWidth * 2f - buttonGap * 2f);

            ImGui.SetCursorPosY(
                ImGui.GetWindowHeight() -
                Ui(14f) -
                buttonHeight);

            if (GameLayoutButton(
                    "Recenter TV",
                    FontAwesomeIcon.Crosshairs,
                    recenterWidth,
                    primary: true,
                    height: 44f))
            {
                engine.RecenterScreen();
            }

            ImGui.SameLine(0f, buttonGap);
            if (GameLayoutButton(
                    "Export",
                    FontAwesomeIcon.Upload,
                    secondaryWidth,
                    height: 44f))
            {
                ExportScreenSettingsToClipboard(engine);
            }
            if (ImGui.IsItemHovered())
            {
                UiFonts.SetTooltip("Export TV placement to the clipboard");
            }

            ImGui.SameLine(0f, buttonGap);
            if (GameLayoutButton(
                    "Import",
                    FontAwesomeIcon.Download,
                    secondaryWidth,
                    height: 44f))
            {
                ImportScreenSettingsFromClipboard(engine);
            }
            if (ImGui.IsItemHovered())
            {
                UiFonts.SetTooltip("Import TV placement from the clipboard");
            }
        }
    }

    private void DrawScreenVisibilityCard(
        VideoEngine engine,
        float width,
        float height)
    {
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
                   .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
                   .Push(ImGuiStyleVar.WindowPadding, UiVec(16f, 14f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
                   .Push(ImGuiCol.Border, BorderSubtle))
        using (var card = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                   "##screenVisibilityCard",
                   new Vector2(width, height),
                   true,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (!card)
            {
                return;
            }

            DrawScreenCardHeading(
                FontAwesomeIcon.Eye,
                "Visibility around TV",
                "Local display preferences for this client.");

            ImGui.Dummy(UiVec(0f, 12f));

            var radius = Math.Clamp(
                Plugin.Cfg.ScreenNameplateHideRadiusYalms,
                0f,
                5f);

            DrawCompactScreenValueLabel(
                "Hide nearby nameplates",
                radius <= 0.01f
                    ? "Off"
                    : $"{radius:0.0} yalms");

            var rightLabelWidth = ImGui.CalcTextSize("5").X;
            ImGui.TextColored(MutedText, "Off");
            ImGui.SameLine(0f, Ui(8f));

            using (ScreenSliderStyle())
            {
                var sliderWidth =
                    MathF.Max(
                        Ui(80f),
                        ImGui.GetContentRegionAvail().X -
                        rightLabelWidth -
                        Ui(12f));

                DrawScreenSliderTrack(radius / 5f, sliderWidth);
                ImGui.SetNextItemWidth(sliderWidth);

                if (ImGui.SliderFloat(
                        "##screenNameplateRadius",
                        ref radius,
                        0f,
                        5f,
                        ""))
                {
                    Plugin.Cfg.ScreenNameplateHideRadiusYalms =
                        Math.Clamp(radius, 0f, 5f);
                    Plugin.Cfg.Save();
                }
            }

            ImGui.SameLine(0f, Ui(8f));
            ImGui.TextColored(MutedText, "5");

            using var uiFontScale23693=UiFonts.PushRelative(.90f);
            using (ImRaii.TextWrapPos(0f))
            ImGui.TextColored(
                MutedText,
                "Distance from the TV in yalms. Hides nearby names, titles and markers.");
            uiFontScale23693.Dispose();

            ImGui.Dummy(UiVec(0f, 13f));
            DrawScreenDivider();
            ImGui.Dummy(UiVec(0f, 13f));

            var keepUiVisible = Plugin.Cfg.KeepGameUiVisibleOverTv;

            using var uiFontScale24096=UiFonts.PushRelative(0.82f);
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(5f))
                       .Push(ImGuiStyleVar.FrameBorderSize, Ui(1f)))
            using (ImRaii.PushColor(
                       ImGuiCol.FrameBg,
                       keepUiVisible ? Accent : ScreenControlBg)
                   .Push(ImGuiCol.FrameBgHovered, AccentHover)
                   .Push(ImGuiCol.FrameBgActive, AccentActive)
                   .Push(ImGuiCol.CheckMark, Vector4.One)
                   .Push(ImGuiCol.Border, BorderSubtle))
            {
                if (ImGui.Checkbox(
                        "##keepGameUiVisibleOverTv",
                        ref keepUiVisible))
                {
                    Plugin.Cfg.KeepGameUiVisibleOverTv = keepUiVisible;
                    engine.KeepGameUiVisibleOverTv = keepUiVisible;
                    Plugin.Cfg.Save();
                }
            }
            uiFontScale24096.Dispose();

            ImGui.SameLine(0f, Ui(8f));
            ImGui.TextColored(
                Vector4.One,
                "Keep game UI visible over the TV");

            ImGui.SetCursorPosX(
                ImGui.GetCursorPosX() + Ui(26f));
            using var uiFontScale25332=UiFonts.PushRelative(.90f);
            using (ImRaii.TextWrapPos(0f))
            ImGui.TextColored(
                MutedText,
                "Menus and HUD windows remain readable when they overlap the TV.");
            uiFontScale25332.Dispose();

            ImGui.SetCursorPosY(
                MathF.Max(ImGui.GetCursorPosY() + Ui(8f), ImGui.GetWindowHeight() - Ui(48f)));

            using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(6f)))
            using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero)
                       .Push(ImGuiCol.ButtonHovered, CardBgHover)
                       .Push(ImGuiCol.ButtonActive, CardBgHover)
                       .Push(ImGuiCol.Text, Accent))
            {
                if (ImGui.Button("Restore visibility defaults"))
                {
                    Plugin.Cfg.ScreenNameplateHideRadiusYalms = 3f;
                    Plugin.Cfg.KeepGameUiVisibleOverTv = true;
                    engine.KeepGameUiVisibleOverTv = true;
                    Plugin.Cfg.Save();
                }
            }
        }
    }

    private void DrawScreenPresetsCard(VideoEngine engine)
    {
        using var presetSpacing=ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing,UiVec(8f,6f));
        var presetCount = Plugin.Cfg.ScreenPresets.Count;
        // Each gap includes spacing after the row and after the spacer item.
        var rowGap = Ui(6f) + ImGui.GetStyle().ItemSpacing.Y * 2f;
        var cardHeight =
            Ui(260f + Math.Max(1, presetCount) * 120f) +
            Math.Max(0, presetCount - 1) * rowGap;

        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
                   .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
                   .Push(ImGuiStyleVar.WindowPadding, UiVec(16f, 14f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
                   .Push(ImGuiCol.Border, BorderSubtle))
        using (var card = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                   "##screenPresetsCard",
                   new Vector2(-1f, cardHeight),
                   true,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (!card)
            {
                return;
            }

            DrawScreenCardHeading(
                FontAwesomeIcon.Bookmark,
                "Presets",
                "Save screen positions for places you return to.");

            ImGui.Dummy(UiVec(0f, 6f));

            var saveButtonWidth = Ui(180f);
            var inputButtonGap = Ui(10f);

            using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(8f))
                       .Push(ImGuiStyleVar.FramePadding, UiVec(12f, 10f))
                       .Push(ImGuiStyleVar.FrameBorderSize, Ui(1f)))
            using (ImRaii.PushColor(ImGuiCol.FrameBg, ScreenControlBg)
                       .Push(ImGuiCol.FrameBgHovered, ScreenControlBgHover)
                       .Push(ImGuiCol.FrameBgActive, ScreenControlBgHover)
                       .Push(ImGuiCol.Border, BorderSubtle))
            {
                ImGui.SetNextItemWidth(
                    ImGui.GetContentRegionAvail().X -
                    saveButtonWidth -
                    inputButtonGap);

                ImGui.InputTextWithHint(
                    "##compactPresetName",
                    "Name this location...",
                    ref presetNameInput,
                    48);
            }

            ImGui.SameLine(0f, inputButtonGap);

            using (UiDisabled.Scope(
                       presetNameInput.Trim().Length == 0))
            {
                if (GameLayoutButton(
                        "Save preset",
                        FontAwesomeIcon.Save,
                        saveButtonWidth,
                        primary: true,
                        height: 40f))
                {
                    SaveCompactScreenPreset(engine);
                }
            }

            ImGui.Dummy(UiVec(0f, 6f));
            using var uiFontScale29280=UiFonts.PushRelative(.90f);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(MutedText, GetScreenPresetLocationName());
            ImGui.PopTextWrapPos();
            uiFontScale29280.Dispose();
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, Ui(1f)))
            using (ImRaii.PushColor(ImGuiCol.FrameBg, FrameBg)
                       .Push(ImGuiCol.FrameBgHovered, AccentHover)
                       .Push(ImGuiCol.FrameBgActive, AccentActive)
                       .Push(ImGuiCol.Border, Accent)
                       .Push(ImGuiCol.CheckMark, Vector4.One))
            using (UiDisabled.Scope(!WatchPartyTvLocation.TryReadCurrent(out _)))
                ImGui.Checkbox("Auto-apply this preset in this location", ref presetAutoApplyInput);
            ImGui.Dummy(UiVec(0f, 2f));
            using var uiFontScale30121=UiFonts.PushRelative(.90f);
            using (ImRaii.TextWrapPos(0f))
                ImGui.TextColored(MutedText, "Manually adjusting the TV disables auto-apply for the current preset.");
            uiFontScale30121.Dispose();
            ImGui.Dummy(UiVec(0f, 6f));

            if (presetCount == 0)
            {
                using var uiFontScale30461=UiFonts.PushRelative(.90f);
                ImGui.TextColored(
                    MutedText,
                    "No presets yet. Place the TV, then save its position above.");
                uiFontScale30461.Dispose();
                return;
            }

            for (var index = 0;
                 index < Plugin.Cfg.ScreenPresets.Count;
                 index++)
            {
                var preset = Plugin.Cfg.ScreenPresets[index];
                ImGui.PushID(index);

                var deleted = DrawCompactScreenPresetRow(
                    engine,
                    preset,
                    index);

                ImGui.PopID();

                if (deleted)
                {
                    break;
                }

                if (index < Plugin.Cfg.ScreenPresets.Count - 1)
                {
                    ImGui.Dummy(UiVec(0f, 6f));
                }
            }
        }
    }

    private bool DrawCompactScreenPresetRow(
        VideoEngine engine,
        ScreenPositionPreset preset,
        int index)
    {
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(8f))
                   .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
                   .Push(ImGuiStyleVar.WindowPadding, UiVec(16f, 12f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   CardBg)
                   .Push(ImGuiCol.Border, ReferenceEquals(engine.AppliedScreenPreset, preset) ? Accent : BorderSubtle))
        using (var row = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                   "##compactPresetRow",
                   new Vector2(-1f, Ui(120f)),
                   true,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (!row)
            {
                return false;
            }

            var rowOrigin = ImGui.GetCursorScreenPos();
            var rowWidth = ImGui.GetContentRegionAvail().X;
            var widthScale = preset.WidthScale ?? preset.Scale;
            var heightScale = preset.HeightScale ?? preset.Scale;
            var scaleText = preset.DisableFixedScaleRatio
                ? $"W {widthScale:0.00}× · H {heightScale:0.00}×"
                : $"{widthScale:0.00}×";

            ImGui.SetCursorScreenPos(rowOrigin);
            ImGui.TextColored(ReferenceEquals(engine.AppliedScreenPreset, preset) ? Accent : Vector4.One,
                preset.Name + (ReferenceEquals(engine.AppliedScreenPreset, preset) ? " · Applied" : ""));

            ImGui.SetCursorScreenPos(rowOrigin + UiVec(0f, 29f));
            using var uiFontScale33115=UiFonts.PushRelative(.90f);
            ImGui.TextColored(
                MutedText,
                $"X {preset.X:0.0} · Y {preset.Y:0.0} · Z {preset.Z:0.0} · {NormalizeScreenDegrees(preset.Yaw):0}° · {scaleText}");
            uiFontScale33115.Dispose();

            ImGui.SetCursorScreenPos(rowOrigin + UiVec(0f, 55f));
            using var uiFontScale33446=UiFonts.PushRelative(.90f);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + MathF.Max(Ui(100f), rowWidth - Ui(145f)));
            ImGui.TextColored(MutedText, preset.LocationName ?? "Location not saved — overwrite to enable auto-apply.");
            ImGui.PopTextWrapPos();
            uiFontScale33446.Dispose();
            ImGui.SetCursorScreenPos(rowOrigin + new Vector2(rowWidth - Ui(132f), Ui(66f)));
            var autoApply = preset.AutoApply;
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, Ui(1f)))
            using (ImRaii.PushColor(ImGuiCol.FrameBg, FrameBg)
                       .Push(ImGuiCol.FrameBgHovered, AccentHover)
                       .Push(ImGuiCol.FrameBgActive, AccentActive)
                       .Push(ImGuiCol.Border, Accent)
                       .Push(ImGuiCol.CheckMark, Vector4.One))
            using (UiDisabled.Scope(string.IsNullOrEmpty(preset.LocationKey)))
            {
                if (ImGui.Checkbox("Auto-apply", ref autoApply))
                {
                    preset.AutoApply = autoApply;
                    engine.InvalidateScreenPresetSpawnNotice(preset);
                    Plugin.Cfg.Save();
                }
            }

            var gap = Ui(7f);
            var loadWidth = Ui(96f);
            var overwriteWidth = Ui(112f);
            var deleteWidth = Ui(88f);
            var actionsWidth =
                loadWidth + overwriteWidth + deleteWidth + gap * 2f;
            var actionsX =
                rowOrigin.X + rowWidth - actionsWidth;
            var actionsY = rowOrigin.Y + Ui(17f);

            ImGui.SetCursorScreenPos(new Vector2(actionsX, actionsY));

            if (GameLayoutButton(
                    "Load",
                    FontAwesomeIcon.FolderOpen,
                    loadWidth,
                    primary: true,
                    height: 38f))
            {
                LoadScreenPreset(engine, preset);
            }

            ImGui.SameLine(0f, gap);
            if (GameLayoutButton(
                    "Overwrite",
                    FontAwesomeIcon.Pen,
                    overwriteWidth,
                    height: 38f))
            {
                OverwriteCompactScreenPreset(engine, preset);
            }

            ImGui.SameLine(0f, gap);
            if (GameLayoutButton(
                    "Delete",
                    FontAwesomeIcon.Trash,
                    deleteWidth,
                    danger: true,
                    height: 38f))
            {
                Plugin.Cfg.ScreenPresets.RemoveAt(index);
                engine.InvalidateScreenPresetSpawnNotice(preset);
                Plugin.Cfg.Save();
                return true;
            }
        }

        return false;
    }

    private static void DrawScreenCardHeading(
        FontAwesomeIcon icon,
        string title,
        string helper)
    {
        using (UiFonts.PushIcon())
        {
            ImGui.TextColored(
                Accent,
                icon.ToIconString());
        }

        ImGui.SameLine(0f, Ui(9f));
        using var uiFontScale36629=UiFonts.PushRelative(1.12f);
        ImGui.TextColored(Hex(0xE2E7EF), title);
        uiFontScale36629.Dispose();

        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() + Ui(25f));
        using var uiFontScale36817=UiFonts.PushRelative(.90f);
        using (ImRaii.TextWrapPos(0f))
        ImGui.TextColored(MutedText, helper);
        uiFontScale36817.Dispose();
    }

    private static void DrawScreenSyncHeading(
        string title,
        string helper)
    {
        var origin = ImGui.GetCursorScreenPos();
        var iconBoxSize = Ui(40f);
        var iconCenter =
            origin +
            new Vector2(iconBoxSize * 0.5f, iconBoxSize * 0.5f);
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddCircleFilled(
            iconCenter,
            iconBoxSize * 0.5f,
            ImGui.GetColorU32(
                AccentIconBg));

        var glyph = FontAwesomeIcon.Link.ToIconString();
        Vector2 glyphSize;
        using (UiFonts.PushIcon())
        {
            glyphSize = ImGui.CalcTextSize(glyph);
            UiFonts.DrawText(drawList, ImGui.GetFontSize(), iconCenter - glyphSize * 0.5f, ImGui.GetColorU32(Vector4.One), glyph);
        }

        var textX = origin.X + iconBoxSize + Ui(14f);
        ImGui.SetCursorScreenPos(
            new Vector2(textX, origin.Y + Ui(2f)));
        using var uiFontScale38032=UiFonts.PushRelative(1.10f);
        ImGui.TextColored(Hex(0xE2E7EF), title);
        uiFontScale38032.Dispose();

        ImGui.SetCursorScreenPos(
            new Vector2(textX, origin.Y + Ui(24f)));
        using var uiFontScale38232=UiFonts.PushRelative(.90f);
        ImGui.TextColored(MutedText, helper);
        uiFontScale38232.Dispose();

        ImGui.SetCursorScreenPos(
            new Vector2(origin.X, origin.Y + iconBoxSize));
        ImGui.Dummy(new Vector2(iconBoxSize, 0f));
    }

    private static void DrawCompactScreenLabel(string label)
    {
        using var uiFontScale38568=UiFonts.PushRelative(.90f);
        ImGui.TextColored(MutedText, label);
        uiFontScale38568.Dispose();
    }

    private static void DrawCompactScreenValueLabel(
        string label,
        string value)
    {
        DrawCompactScreenLabel(label);
        ImGui.SameLine();
        var valueWidth = ImGui.CalcTextSize(value).X;
        ImGui.SetCursorPosX(
            ImGui.GetWindowWidth() -
            valueWidth -
            Ui(16f));
        using var uiFontScale39030=UiFonts.PushRelative(0.82f);
        ImGui.TextColored(Vector4.One, value);
        uiFontScale39030.Dispose();
    }

    private static bool DrawScreenAxisField(
        string axis,
        string id,
        ref float value,
        float width)
    {
        var changed = false;
        using var framePadding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(8f, 11f));
        ImGui.BeginGroup();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(MutedText, axis);
        ImGui.SameLine(0f, Ui(6f));

        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7f))
                   .Push(ImGuiStyleVar.FrameBorderSize, Ui(1f)))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, ScreenControlBg)
                   .Push(ImGuiCol.FrameBgHovered, ScreenControlBgHover)
                   .Push(ImGuiCol.FrameBgActive, ScreenControlBgHover)
                   .Push(ImGuiCol.Border, BorderSubtle))
        {
            ImGui.SetNextItemWidth(
                width -
                ImGui.CalcTextSize(axis).X -
                Ui(8f));
            changed = ImGui.DragFloat(
                id,
                ref value,
                0.05f,
                0f,
                0f,
                "%.3f");
        }

        ImGui.EndGroup();
        return changed;
    }

    private static IDisposable ScreenSliderStyle() =>
        new ScreenSliderStyleScope();

    private static float NormalizeScreenSliderValue(
        float value,
        float minimum,
        float maximum) =>
        Math.Clamp(
            (value - minimum) /
            MathF.Max(0.0001f, maximum - minimum),
            0f,
            1f);

    private static void DrawScreenSliderTrack(
        float normalizedValue,
        float width)
    {
        var origin = ImGui.GetCursorScreenPos();
        var centerY = origin.Y + ImGui.GetFrameHeight() * 0.5f;
        var trackHeight = Ui(7f);
        var radius = trackHeight * 0.5f;
        var start = new Vector2(origin.X, centerY - radius);
        var end = new Vector2(origin.X + width, centerY + radius);
        var fillEnd =
            new Vector2(
                origin.X + width * Math.Clamp(normalizedValue, 0f, 1f),
                centerY + radius);
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            start,
            end,
            ImGui.GetColorU32(FrameBgHover),
            radius);

        if (fillEnd.X > start.X)
        {
            drawList.AddRectFilled(
                start,
                fillEnd,
                ImGui.GetColorU32(Accent),
                radius);
        }
    }

    private sealed class ScreenSliderStyleScope : IDisposable
    {
        public ScreenSliderStyleScope()
        {
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Ui(7f));
            ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, Ui(7f));
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, UiVec(6f, 5f));
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.SliderGrab, Accent);
            ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, AccentHover);
        }

        public void Dispose()
        {
            ImGui.PopStyleColor(5);
            ImGui.PopStyleVar(3);
        }
    }

    private static void DrawScreenDivider()
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.GetWindowDrawList().AddRectFilled(
            origin,
            origin + new Vector2(width, Ui(1f)),
            ImGui.GetColorU32(BorderSubtle));
        ImGui.Dummy(new Vector2(width, Ui(1f)));
    }

    private static float NormalizeScreenDegrees(float yaw)
    {
        var degrees = yaw * 180f / MathF.PI;
        degrees %= 360f;
        return degrees < 0f
            ? degrees + 360f
            : degrees;
    }

    private static void SaveScreenTransform(
        VideoEngine engine,
        Vector3 position,
        float yaw,
        bool disableFixedScaleRatio,
        float widthScale,
        float heightScale)
    {
        engine.SetScreenTransform(
            position,
            yaw,
            disableFixedScaleRatio,
            widthScale,
            heightScale, manualAdjustment: true);

        Plugin.Cfg.ScreenPosition = position;
        Plugin.Cfg.ScreenYaw = yaw;
        Plugin.Cfg.ScreenScale = widthScale;
        Plugin.Cfg.DisableFixedScreenScaleRatio =
            disableFixedScaleRatio;
        Plugin.Cfg.ScreenWidthScale = widthScale;
        Plugin.Cfg.ScreenHeightScale = heightScale;
        Plugin.Cfg.Save();
    }

    private void LoadScreenPreset(VideoEngine engine, ScreenPositionPreset preset)
    {
        if (engine.DutyScreen.InDuty || !WatchPartyTvLocation.IsLocalPlayerReady()) return;
        var viewerSpawn = stream.Mode == StreamMode.Viewing && !ViewerTvEnabled;
        if (stream.Mode == StreamMode.Viewing)
            SpawnViewerTvWithPlacement(localPlacement: true, recenterLocalPlacement: false);
        var viewerRestored = viewerSpawn && engine.IsActive;
        engine.ApplyScreenPreset(preset, spawnScreen: true);
        if (viewerRestored && engine.IsActive)
            engine.AnnounceScreenPresetSpawn(preset);
    }

    private void SaveCompactScreenPreset(VideoEngine engine)
    {
        var position = engine.ScreenPosition;
        var preset = new ScreenPositionPreset
            {
                Name = presetNameInput.Trim(),
                X = position.X,
                Y = position.Y,
                Z = position.Z,
                Yaw = engine.ScreenYaw,
                Scale = engine.ScreenScale,
                DisableFixedScaleRatio =
                    engine.DisableFixedScreenScaleRatio,
                WidthScale = engine.ScreenWidthScale,
                HeightScale = engine.ScreenHeightScale,
                LocationKey = WatchPartyTvLocation.TryReadCurrent(out var location) ? location.Encode() : null,
                LocationName = GetScreenPresetLocationName(),
                AutoApply = presetAutoApplyInput && WatchPartyTvLocation.IsLocalPlayerReady(),
                SavedUtc = DateTime.UtcNow,
            };
        Plugin.Cfg.ScreenPresets.Add(preset);
        engine.ApplyScreenPreset(preset);
        Plugin.Cfg.Save();
        presetNameInput = string.Empty;
    }

    private void OverwriteCompactScreenPreset(
        VideoEngine engine,
        ScreenPositionPreset preset)
    {
        var position = engine.ScreenPosition;
        preset.X = position.X;
        preset.Y = position.Y;
        preset.Z = position.Z;
        preset.Yaw = engine.ScreenYaw;
        preset.Scale = engine.ScreenScale;
        preset.DisableFixedScaleRatio =
            engine.DisableFixedScreenScaleRatio;
        preset.WidthScale = engine.ScreenWidthScale;
        preset.HeightScale = engine.ScreenHeightScale;
        preset.LocationKey = WatchPartyTvLocation.TryReadCurrent(out var location) ? location.Encode() : null;
        preset.LocationName = GetScreenPresetLocationName();
        preset.SavedUtc = DateTime.UtcNow;
        engine.ApplyScreenPreset(preset);
        Plugin.Cfg.Save();
    }

    private string GetScreenPresetLocationName()
    {
        if (!WatchPartyTvLocation.TryReadCurrent(out var location)) return "Location unavailable while travelling.";
        var name = GetCurrentWatchPartyLocation();
        var world = Plugin.ObjectTable.LocalPlayer?.CurrentWorld.Value.Name.ToString();
        if (location.Plot >= 0 && location.Room > 0) name += $", Room {location.Room}";
        return string.IsNullOrWhiteSpace(world) ? name : $"{world} · {name}";
    }
}

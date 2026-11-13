using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.BroadcastAllowance;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool gameKeyboardControlsOpenRequested;
    private bool gameControllerControlsOpenRequested;
    private string? controllerBindingCapture;
    private Action<int>? controllerBindingSetter;
    private bool controllerCaptureWaitingForNeutral;

    private static void SetGameLayoutRowGap(float gap)
    {
        // ImGui has already advanced by ItemSpacing.Y after the previous row.
        // Replace that default spacing so the vertical panel gap exactly matches
        // the explicit horizontal gap used by SameLine above.
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + gap - ImGui.GetStyle().ItemSpacing.Y);
    }

    private bool GameLayoutButton(string label, FontAwesomeIcon icon, float width,
        bool primary = false, bool danger = false, float height = 36f)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(MathF.Max(1f, width), Ui(height));
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(8f));
        using var border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, primary || danger ? 0f : Ui(1f));
        using var colors = ImRaii.PushColor(ImGuiCol.Button,
            danger ? Danger : primary ? Accent : FrameBg)
            .Push(ImGuiCol.ButtonHovered, danger ? Danger : primary ? AccentHover : CardBgHover)
            .Push(ImGuiCol.ButtonActive, danger ? Danger : AccentActive)
            .Push(ImGuiCol.Border, BorderSubtle);
        var clicked = ImGui.Button("##game_" + label, size);
        var textSize = ImGui.CalcTextSize(label);
        Vector2 iconSize;
        using (UiFonts.PushIcon()) iconSize = ImGui.CalcTextSize(icon.ToIconString());
        var iconGap = string.IsNullOrEmpty(label) ? 0f : Ui(8f);
        var x = origin.X + (size.X - textSize.X - iconSize.X - iconGap) / 2f;
        var color = ImGui.GetColorU32(ImGuiCol.Text);
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(origin, origin + size, true);
        using (UiFonts.PushIcon())
            UiFonts.DrawText(draw, ImGui.GetFontSize(), new Vector2(x, origin.Y + (size.Y - iconSize.Y) / 2f), color, icon.ToIconString());
        UiFonts.DrawText(draw, ImGui.GetFontSize(), new Vector2(x + iconSize.X + iconGap, origin.Y + (size.Y - textSize.Y) / 2f), color, label);
        draw.PopClipRect();
        return clicked;
    }

    private void GameLayoutPanel(string id, float width, float height, Action draw, float verticalPadding = 16f)
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(16f, verticalPadding));
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(12f));
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        // Keep overflow scrollable for large fonts and long error messages.
        using var child = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(id, new Vector2(width, height), true));
        if (child) draw();
    }

    private void GameLayoutHeading(string title, FontAwesomeIcon icon, string? status = null, Vector4? statusColor = null,
        bool patreonFeature = false, string? warning = null)
    {
        using (UiFonts.PushIcon()) ImGui.TextColored(Accent, icon.ToIconString());
        ImGui.SameLine(0f, Ui(10f));
        using (currentPage is HomePage.PlaySnes or HomePage.LuckyWheel ? UiFonts.PushRelative(1f, emphasis: true) : UiFonts.Empty)
            ImGui.TextColored(currentPage is HomePage.PlaySnes or HomePage.LuckyWheel ? Hex(0xE2E7EF) : ImGui.GetStyle().Colors[(int)ImGuiCol.Text], title);
        if (status is not null)
        {
            ImGui.SameLine(0f, Ui(16f));
            ImGui.TextColored(statusColor ?? MutedText, status);
        }
        if (warning is not null)
        {
            ImGui.SameLine(0f, Ui(24f));
            ImGui.TextColored(Danger, warning);
        }
        if (patreonFeature)
            DrawPatreonFeatureTag();
        ImGui.Dummy(UiVec(0f, 10f));
    }

    private void DrawGameBroadcastQualitySelector(bool broadcastArmed)
    {
        var hasTier2 = HasConfiguredPatreonTier(2);
        if (Plugin.Cfg.GameBroadcastQuality == GameBroadcastQuality.LowCpu ||
            (!hasTier2 && Plugin.Cfg.GameBroadcastQuality == GameBroadcastQuality.StandardNative))
        {
            Plugin.Cfg.GameBroadcastQuality = GameBroadcastQuality.DataSaver;
            Plugin.Cfg.Save();
        }

        using var uiFontScale19351=UiFonts.PushRelative(0.72f);
        ImGui.TextColored(MutedText, "BROADCAST QUALITY");
        uiFontScale19351.Dispose();

        var quality = Plugin.Cfg.GameBroadcastQuality;
        var preview = quality == GameBroadcastQuality.StandardNative
            ? "Standard Native Quality"
            : "Data Saver";

        using (UiDisabled.Scope(broadcastArmed))
        {
            ImGui.SetNextItemWidth(-1f);
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##gameBroadcastQuality", preview)))
            {
                if (ImGui.Selectable("Data Saver", quality == GameBroadcastQuality.DataSaver))
                {
                    Plugin.Cfg.GameBroadcastQuality = GameBroadcastQuality.DataSaver;
                    Plugin.Cfg.Save();
                }
                if (quality == GameBroadcastQuality.DataSaver)
                    ImGui.SetItemDefaultFocus();

                using (UiDisabled.Scope(!hasTier2))
                {
                    if (ImGui.Selectable(
                            "Standard Native Quality  (Patreon Tier 2)",
                            quality == GameBroadcastQuality.StandardNative))
                    {
                        Plugin.Cfg.GameBroadcastQuality = GameBroadcastQuality.StandardNative;
                        Plugin.Cfg.Save();
                    }
                }
                if (!hasTier2 &&
                    ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    UiFonts.SetTooltip("Available with patreon tier 2");
                }

                ImGui.EndCombo();
            }
        }

        if (broadcastArmed && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiFonts.SetTooltip("Stop broadcasting to change quality.");

        using var uiFontScale21157=UiFonts.PushRelative(0.75f);
        ImGui.TextColored(MutedText,
            quality == GameBroadcastQuality.StandardNative
                ? "~1,200 Kbps · Higher video quality"
                : "~500 Kbps · Reduced bandwidth and CPU use");
        using (ImRaii.TextWrapPos(0f))
            ImGui.TextColored(MutedText, "Higher quality consumes more allowance credits.");
        uiFontScale21157.Dispose();

        if (!hasTier2)
            DrawGameStandardQualityPatreonLink();
    }

    private void DrawGameStandardQualityPatreonLink()
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, Ui(18f));
        ImGui.InvisibleButton("##gameStandardQualityPatreon", size);
        var color = ImGui.IsItemHovered()
            ? new Vector4(1f, 0.67f, 0.27f, 1f)
            : PatreonOrange;
        var draw = ImGui.GetWindowDrawList();
        using (UiFonts.PushIcon())
            UiFonts.DrawText(draw, ImGui.GetFontSize(), origin + UiVec(1f, 1f), ImGui.GetColorU32(color), FontAwesomeIcon.Lock.ToIconString());
        UiFonts.DrawText(draw, ImGui.GetFontSize() * 0.76f, origin + UiVec(20f, 2f), ImGui.GetColorU32(color), "Standard Native Quality available with Patreon Tier 2.");
        if (ImGui.IsItemClicked()) OpenPatreonMembershipPage();
    }

    private void DrawGameBroadcastAllowancePlaceholder()
    {
        if (DrawAccountBroadcastRestriction("Retro game")) return;
        var allowance = BroadcastAllowanceDisplayFor(
            BroadcastAllowanceMethod.RetroGame);
        var remaining = allowance.Remaining;
        var remainingLabel = allowance.RemainingLabel;

        using (UiFonts.PushRelative(.76f))
        {
            var available = ImGui.GetContentRegionAvail().X;
            var headingWidth = ImGui.CalcTextSize(allowance.Heading).X;
            var remainingWidth = ImGui.CalcTextSize(remainingLabel).X;
            var startX = ImGui.GetCursorPosX();
            ImGui.TextColored(MutedText, allowance.Heading);
            if (headingWidth + Ui(12f) + remainingWidth <= available)
                ImGui.SameLine(startX + available - remainingWidth);
            ImGui.TextColored(MutedText, remainingLabel);
        }
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, Ui(14f));
        var fillSize = new Vector2(size.X * remaining, size.Y);
        var draw = ImGui.GetWindowDrawList();
        ImGui.Dummy(size);

        draw.AddRectFilled(origin, origin + size,
            ImGui.GetColorU32(AccentPanelBg), Ui(7f));
        draw.AddRectFilled(origin, origin + fillSize,
            ImGui.GetColorU32(Accent), Ui(7f));
        draw.PushClipRect(origin, origin + fillSize, true);
        var stripeStep = Ui(13f);
        for (var x = origin.X - size.Y; x < origin.X + fillSize.X + size.Y; x += stripeStep)
        {
            draw.AddLine(
                new Vector2(x, origin.Y + size.Y),
                new Vector2(x + size.Y, origin.Y),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.16f)),
                Ui(4f));
        }
        draw.PopClipRect();
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(BorderSubtle), Ui(7f));

        using var uiFontScale24255=UiFonts.PushRelative(0.68f);
        ImGui.TextColored(MutedText, allowance.Detail);
        DrawPatreonUpgradeLink(ImGui.GetWindowWidth() - Ui(16f));
        uiFontScale24255.Dispose();
    }

    private static void DrawRetroGameHelper(string text)
    {
        using var font = UiFonts.PushRelative(.90f);
        using var colour = ImRaii.PushColor(ImGuiCol.Text, MutedText);
        ImGui.TextWrapped(text);
    }

    private static void DrawRetroGameSubheading(string text)
    {
        using var font = UiFonts.PushRelative(1f, emphasis: true);
        ImGui.TextColored(Hex(0xE2E7EF), text);
    }

    private void DrawCompactGameAudio(GameSystem system)
    {
        DrawRetroGameSubheading("Audio & display");
        var volume = Plugin.Cfg.Volume;
        ImGui.TextColored(MutedText, "TV volume");
        ImGui.SetNextItemWidth(MathF.Max(Ui(50f), ImGui.GetContentRegionAvail().X - Ui(52f)));
        if (ImGui.SliderInt("##compactGameVolume", ref volume, 0, 130, ""))
        {
            Plugin.Cfg.Volume = volume;
            if (volume > 0) Plugin.Cfg.Muted = false;
            ApplyListeningVolume();
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) Plugin.Cfg.Save();
        ImGui.SameLine(0f, Ui(8f));
        ImGui.TextColored(volume > 100 ? Gold : Vector4.One, $"{volume}%");
        ImGui.Spacing();
        var half = (ImGui.GetContentRegionAvail().X - Ui(8f)) / 2f;
        if (GameLayoutButton(Plugin.Cfg.Muted ? "Unmute TV" : "Mute TV", Plugin.Cfg.Muted ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute, half))
        {
            Plugin.Cfg.Muted = !Plugin.Cfg.Muted;
            ApplyListeningVolume();
            Plugin.Cfg.Save();
        }
        ImGui.SameLine(0f, Ui(8f));
        var muted = IsFfxivSoundMuted();
        if (GameLayoutButton(muted ? "Restore FFXIV" : "Mute FFXIV", muted ? FontAwesomeIcon.VolumeUp : FontAwesomeIcon.VolumeMute, half)) SetFfxivSoundMuted(!muted);
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Unmuting restores your previous FFXIV audio levels.");
        ImGui.Dummy(UiVec(0f, 6f));
        ImGui.Separator();
        ImGui.Spacing();
        var crt = system == GameSystem.Snes ? screenController.Engine.SnesCrtFilterEnabled : system == GameSystem.Nes ? screenController.Engine.NesCrtFilterEnabled : system == GameSystem.GameBoyAdvance ? screenController.Engine.GameBoyAdvanceCrtFilterEnabled : system == GameSystem.MasterSystem ? screenController.Engine.MasterSystemCrtFilterEnabled : system == GameSystem.GameGear ? screenController.Engine.GameGearCrtFilterEnabled : screenController.Engine.GameBoyCrtFilterEnabled;
        if (DrawGameCrtToggle(ref crt))
        {
            if (system == GameSystem.Snes) screenController.Engine.SetSnesCrtFilterEnabled(crt);
            else if (system == GameSystem.Nes) screenController.Engine.SetNesCrtFilterEnabled(crt);
            else if (system == GameSystem.GameBoyAdvance) screenController.Engine.SetGameBoyAdvanceCrtFilterEnabled(crt);
            else if (system == GameSystem.MasterSystem) screenController.Engine.SetMasterSystemCrtFilterEnabled(crt);
            else if (system == GameSystem.GameGear) screenController.Engine.SetGameGearCrtFilterEnabled(crt);
            else screenController.Engine.SetGameBoyCrtFilterEnabled(crt);
        }
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Apply a CRT screen style filter to the game.");
    }

    private void DrawCompactGameKeyboardPopup(bool snes)
    {
        var hasShoulderButtons = snes || selectedGameSystem == GameSystem.GameBoyAdvance;
        if (!BeginGameDialog("Keyboard controls", "Keyboard settings", FontAwesomeIcon.Keyboard, 620f, hasShoulderButtons ? 560f : 510f)) return;
        DrawRetroGameSubheading(snes ? "Super Nintendo controls" : selectedGameSystem == GameSystem.Nes ? "Nintendo Entertainment System controls" : selectedGameSystem == GameSystem.GameBoyAdvance ? "Game Boy Advance controls" : selectedGameSystem == GameSystem.MasterSystem ? "Master System / SG-1000 controls" : selectedGameSystem == GameSystem.GameGear ? "Game Gear controls" : "Game Boy / Color controls");
        DrawRetroGameHelper("Configure Player 1 keyboard bindings below.");
        if (!snes) DrawRetroGameHelper($"{(selectedGameSystem == GameSystem.Nes ? "NES" : selectedGameSystem == GameSystem.GameBoyAdvance ? "Game Boy Advance" : selectedGameSystem == GameSystem.MasterSystem ? "Master System / SG-1000" : selectedGameSystem == GameSystem.GameGear ? "Game Gear" : "Game Boy")} shares these bindings with the matching SNES controls.");
        if (ImGui.BeginTable("##compactBindings", 2, ImGuiTableFlags.SizingStretchProp))
        {
            DrawGameBindingCell("D-Pad Up", Plugin.Cfg.SnesKeyUp, v => Plugin.Cfg.SnesKeyUp = v);
            DrawGameBindingCell("D-Pad Down", Plugin.Cfg.SnesKeyDown, v => Plugin.Cfg.SnesKeyDown = v);
            DrawGameBindingCell("D-Pad Left", Plugin.Cfg.SnesKeyLeft, v => Plugin.Cfg.SnesKeyLeft = v);
            DrawGameBindingCell("D-Pad Right", Plugin.Cfg.SnesKeyRight, v => Plugin.Cfg.SnesKeyRight = v);
            DrawGameBindingCell("A Button", Plugin.Cfg.SnesKeyA, v => Plugin.Cfg.SnesKeyA = v);
            DrawGameBindingCell("B Button", Plugin.Cfg.SnesKeyB, v => Plugin.Cfg.SnesKeyB = v);
            if (hasShoulderButtons)
            {
                if (snes)
                {
                    DrawGameBindingCell("X Button", Plugin.Cfg.SnesKeyX, v => Plugin.Cfg.SnesKeyX = v);
                    DrawGameBindingCell("Y Button", Plugin.Cfg.SnesKeyY, v => Plugin.Cfg.SnesKeyY = v);
                }
                DrawGameBindingCell("L Button", Plugin.Cfg.SnesKeyL, v => Plugin.Cfg.SnesKeyL = v);
                DrawGameBindingCell("R Button", Plugin.Cfg.SnesKeyR, v => Plugin.Cfg.SnesKeyR = v);
            }
            DrawGameBindingCell("Start", Plugin.Cfg.SnesKeyStart, v => Plugin.Cfg.SnesKeyStart = v);
            DrawGameBindingCell("Select", Plugin.Cfg.SnesKeySelect, v => Plugin.Cfg.SnesKeySelect = v);
            ImGui.EndTable();
        }
        if (HasDuplicateGameKeyBindings()) ImGui.TextWrapped("Some buttons share the same keyboard key.");
        ImGui.Separator();
        var block = screenController.Engine.BlockAllFfxivKeyboardInput;
        using (UiDisabled.Scope(screenController.Engine.ControllerModeEnabled))
        if (ImGui.Checkbox("Block all FFXIV keyboard input while controlling game", ref block))
            screenController.Engine.SetBlockAllFfxivKeyboardInput(block);
        DrawRetroGameHelper(screenController.Engine.ControllerModeEnabled ? "Controller Mode leaves all keyboard input with FFXIV. Ctrl + F12 releases the controller." : "When disabled, only assigned game keys are blocked. Press Ctrl + F12 to return control to FFXIV.");
        if (GameLayoutButton("Restore defaults", FontAwesomeIcon.Undo, ImGui.GetContentRegionAvail().X))
        {
            ResetSnesKeyboardControls();
            Plugin.Cfg.Save();
        }
        if (GameLayoutButton("Save & close", FontAwesomeIcon.Check, ImGui.GetContentRegionAvail().X, true))
        {
            Plugin.Cfg.Save();
            activeGameDialog = null;
        }
        EndGameDialog();
    }

    private void DrawCompactGameControllerPopup(bool snes)
    {
        var hasShoulderButtons = snes || selectedGameSystem == GameSystem.GameBoyAdvance;
        if (!BeginGameDialog("Controller controls", "Controller settings", FontAwesomeIcon.Gamepad, 660f,
                snes ? 535f : hasShoulderButtons ? 545f : 495f)) return;

        DrawRetroGameSubheading(snes ? "Super Nintendo controls" : selectedGameSystem == GameSystem.Nes
            ? "Nintendo Entertainment System controls" : selectedGameSystem == GameSystem.GameBoyAdvance
                ? "Game Boy Advance controls" : selectedGameSystem == GameSystem.MasterSystem
                    ? "Master System / SG-1000 controls" : selectedGameSystem == GameSystem.GameGear
                        ? "Game Gear controls" : "Game Boy / Color controls");
        DrawRetroGameHelper("Select a control, then press the button on your controller to assign it.");
        if (!snes)
            DrawRetroGameHelper($"{(selectedGameSystem == GameSystem.Nes ? "NES" : selectedGameSystem == GameSystem.GameBoyAdvance ? "Game Boy Advance" : selectedGameSystem == GameSystem.MasterSystem ? "Master System / SG-1000" : selectedGameSystem == GameSystem.GameGear ? "Game Gear" : "Game Boy")} shares this controller layout with the matching SNES controls.");

        ImGui.Spacing();
        if (ImGui.BeginTable("##compactControllerBindings", 2, ImGuiTableFlags.SizingStretchProp))
        {
            DrawControllerBindingCell("D-Pad Up", Plugin.Cfg.GamepadUp, v => Plugin.Cfg.GamepadUp = v);
            DrawControllerBindingCell("D-Pad Down", Plugin.Cfg.GamepadDown, v => Plugin.Cfg.GamepadDown = v);
            DrawControllerBindingCell("D-Pad Left", Plugin.Cfg.GamepadLeft, v => Plugin.Cfg.GamepadLeft = v);
            DrawControllerBindingCell("D-Pad Right", Plugin.Cfg.GamepadRight, v => Plugin.Cfg.GamepadRight = v);
            DrawControllerBindingCell("A Button", Plugin.Cfg.GamepadA, v => Plugin.Cfg.GamepadA = v);
            DrawControllerBindingCell("B Button", Plugin.Cfg.GamepadB, v => Plugin.Cfg.GamepadB = v);
            if (hasShoulderButtons)
            {
                if (snes)
                {
                    DrawControllerBindingCell("X Button", Plugin.Cfg.GamepadX, v => Plugin.Cfg.GamepadX = v);
                    DrawControllerBindingCell("Y Button", Plugin.Cfg.GamepadY, v => Plugin.Cfg.GamepadY = v);
                }
                DrawControllerBindingCell("L Button", Plugin.Cfg.GamepadL, v => Plugin.Cfg.GamepadL = v);
                DrawControllerBindingCell("R Button", Plugin.Cfg.GamepadR, v => Plugin.Cfg.GamepadR = v);
            }
            DrawControllerBindingCell("Start", Plugin.Cfg.GamepadStart, v => Plugin.Cfg.GamepadStart = v);
            DrawControllerBindingCell("Select", Plugin.Cfg.GamepadSelect, v => Plugin.Cfg.GamepadSelect = v);
            ImGui.EndTable();
        }

        CaptureControllerBinding();

        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.12f, 0.09f, 0.035f, 0.72f))
            .Push(ImGuiCol.Border, Gold))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(12f, 10f)))
        using (var info = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##controllerCompatibility", UiVec(-1f, 72f), true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (info)
            {
                ImGui.TextColored(Gold, "Controller compatibility");
                DrawRetroGameHelper("The controller must be enabled and recognized by Final Fantasy XIV and Dalamud. Controllers that FFXIV cannot detect may not work in Alpha Channel.");
            }
        }

        if (GameLayoutButton("Restore defaults", FontAwesomeIcon.Undo, ImGui.GetContentRegionAvail().X))
        {
            ResetGamepadControls();
            controllerBindingCapture = null;
            controllerBindingSetter = null;
            Plugin.Cfg.Save();
        }
        if (GameLayoutButton("Save & close", FontAwesomeIcon.Check, ImGui.GetContentRegionAvail().X, true))
        {
            Plugin.Cfg.Save();
            controllerBindingCapture = null;
            controllerBindingSetter = null;
            activeGameDialog = null;
        }
        EndGameDialog();
    }

    private void DrawControllerBindingCell(string label, int value, Action<int> setValue)
    {
        ImGui.TableNextColumn();
        ImGui.PushID("controller_" + label);
        if (ImGui.BeginTable("##controllerBindingPair", 2,
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("Control", ImGuiTableColumnFlags.WidthStretch, 0.38f);
            ImGui.TableSetupColumn("Button", ImGuiTableColumnFlags.WidthStretch, 0.62f);
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);
            ImGui.TableNextColumn();
            var listening = controllerBindingCapture == label;
            using (ImRaii.PushColor(ImGuiCol.Button, listening ? new Vector4(Accent.X, Accent.Y, Accent.Z, 0.55f) : CardBg))
            {
                var caption = listening
                    ? controllerCaptureWaitingForNeutral ? "Release buttons..." : "Press a button..."
                    : GamepadButtonDisplayName((GamepadButtons)value);
                if (ImGui.Button(caption, new Vector2(-1f, 0f)))
                {
                    controllerBindingCapture = label;
                    controllerBindingSetter = setValue;
                    controllerCaptureWaitingForNeutral = true;
                }
            }
            ImGui.EndTable();
        }
        ImGui.PopID();
    }

    private void CaptureControllerBinding()
    {
        if (controllerBindingSetter is null) return;
        if (controllerCaptureWaitingForNeutral)
        {
            if (AssignableGamepadButtons.All(button => Plugin.GamepadState.Raw(button) == 0))
                controllerCaptureWaitingForNeutral = false;
            return;
        }

        foreach (var button in AssignableGamepadButtons)
        {
            if (Plugin.GamepadState.Pressed(button) == 0) continue;
            controllerBindingSetter((int)button);
            controllerBindingCapture = null;
            controllerBindingSetter = null;
            return;
        }
    }

    private static readonly GamepadButtons[] AssignableGamepadButtons =
    [
        GamepadButtons.DpadUp, GamepadButtons.DpadDown, GamepadButtons.DpadLeft, GamepadButtons.DpadRight,
        GamepadButtons.North, GamepadButtons.South, GamepadButtons.West, GamepadButtons.East,
        GamepadButtons.L1, GamepadButtons.L2, GamepadButtons.L3,
        GamepadButtons.R1, GamepadButtons.R2, GamepadButtons.R3,
        GamepadButtons.Start, GamepadButtons.Select
    ];

    private static string GamepadButtonDisplayName(GamepadButtons button) => button switch
    {
        GamepadButtons.North => "Top face button",
        GamepadButtons.South => "Bottom face button",
        GamepadButtons.West => "Left face button",
        GamepadButtons.East => "Right face button",
        GamepadButtons.DpadUp => "D-pad Up",
        GamepadButtons.DpadDown => "D-pad Down",
        GamepadButtons.DpadLeft => "D-pad Left",
        GamepadButtons.DpadRight => "D-pad Right",
        GamepadButtons.L1 => "L1",
        GamepadButtons.L2 => "L2",
        GamepadButtons.L3 => "L3",
        GamepadButtons.R1 => "R1",
        GamepadButtons.R2 => "R2",
        GamepadButtons.R3 => "R3",
        GamepadButtons.Start => "Start",
        GamepadButtons.Select => "Select / Back",
        _ => button.ToString()
    };

    private static void ResetGamepadControls()
    {
        Plugin.Cfg.GamepadUp = (int)GamepadButtons.DpadUp;
        Plugin.Cfg.GamepadDown = (int)GamepadButtons.DpadDown;
        Plugin.Cfg.GamepadLeft = (int)GamepadButtons.DpadLeft;
        Plugin.Cfg.GamepadRight = (int)GamepadButtons.DpadRight;
        Plugin.Cfg.GamepadA = (int)GamepadButtons.East;
        Plugin.Cfg.GamepadB = (int)GamepadButtons.South;
        Plugin.Cfg.GamepadX = (int)GamepadButtons.North;
        Plugin.Cfg.GamepadY = (int)GamepadButtons.West;
        Plugin.Cfg.GamepadL = (int)GamepadButtons.L1;
        Plugin.Cfg.GamepadR = (int)GamepadButtons.R1;
        Plugin.Cfg.GamepadStart = (int)GamepadButtons.Start;
        Plugin.Cfg.GamepadSelect = (int)GamepadButtons.Select;
    }

}

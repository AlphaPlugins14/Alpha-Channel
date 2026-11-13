using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string? activeGameDialog;

    private void DrawGamePageDialogs()
    {
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8f, 8f));
        using var frames = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(10f, 6f))
            .Push(ImGuiStyleVar.FrameRounding, Ui(7f));
        using var colors = ImRaii.PushColor(ImGuiCol.FrameBg, FrameBg)
            .Push(ImGuiCol.FrameBgHovered, FrameBgHover)
            .Push(ImGuiCol.CheckMark, Accent);
        if (gameKeyboardControlsOpenRequested)
        {
            activeGameDialog = "Keyboard controls";
            gameKeyboardControlsOpenRequested = false;
        }
        if (gameControllerControlsOpenRequested)
        {
            activeGameDialog = "Controller controls";
            gameControllerControlsOpenRequested = false;
            controllerBindingCapture = null;
            controllerBindingSetter = null;
        }
        if (snesRomSourcesPopupRequested)
        {
            activeGameDialog = "Game ROM information";
            snesRomSourcesPopupRequested = false;
        }
        DrawCompactGameKeyboardPopup(selectedGameSystem == GameSystem.Snes);
        DrawCompactGameControllerPopup(selectedGameSystem == GameSystem.Snes);
        DrawGameLibraryDialog();
        DrawGameRomHelp();
        DrawGameExitConfirmation();
        DrawGameBroadcastDialog();
    }

    private void DrawGameBroadcastDialog()
    {
        if (!BeginGameDialog("Game broadcast", "Game Broadcast", FontAwesomeIcon.BroadcastTower, 540f, 460f)) return;
        ImGui.TextWrapped("Choose the quality and frame rate for your gameplay broadcast.");
        ImGui.Spacing();
        DrawGameBroadcastAllowancePlaceholder();
        ImGui.Spacing();
        DrawGameBroadcastQualitySelector(gameBroadcastArmed);
        ImGui.Spacing();
        using var uiFontScale2286=UiFonts.PushRelative(0.72f);
        ImGui.TextColored(MutedText, "BROADCAST FPS");
        uiFontScale2286.Dispose();
        var fps = Video.GameBroadcastQualityProfiles.NormalizeFrameRate(Plugin.Cfg.GameBroadcastFramesPerSecond);
        using (UiDisabled.Scope(gameBroadcastArmed))
        {
            ImGui.SetNextItemWidth(-1f);
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##gameBroadcastFps", fps == 0 ? "Native game FPS" : $"{fps} FPS")))
            {
                foreach (var option in new[] { (15, "15 FPS (Lowest CPU usage)"), (24, "24 FPS"), (0, "Native game FPS") })
                {
                    if (ImGui.Selectable(option.Item2, fps == option.Item1))
                    {
                        Plugin.Cfg.GameBroadcastFramesPerSecond = option.Item1;
                        Plugin.Cfg.Save();
                    }
                    if (fps == option.Item1) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                UiFonts.SetTooltip("If you experience lag or viewers report broadcast stream issues then lower FPS");
        }
        ImGui.TextWrapped("Lower FPS reduces broadcast processing demand. Native game FPS preserves the game's own frame rate. Your local game speed is unchanged.");
        ImGui.Spacing();
        var buttonWidth = (ImGui.GetContentRegionAvail().X - Ui(10f)) / 2f;
        if (GameLayoutButton("Cancel", FontAwesomeIcon.Times, buttonWidth)) activeGameDialog = null;
        ImGui.SameLine(0f, Ui(10f));
        using (UiDisabled.Scope(gameBroadcastArmed || !screenController.Engine.IsPlayingGame ||
            stream.Mode != StreamMode.Hosting || CurrentSession is not { } session ||
            string.IsNullOrWhiteSpace(Plugin.Cfg.StreamKeys.GetValueOrDefault(session.AccountId))))
            if (GameLayoutButton("Start Broadcast", FontAwesomeIcon.BroadcastTower, buttonWidth, true))
                if (StartGameWatchPartyBroadcast()) activeGameDialog = null;
        DrawMissingBroadcastStreamKeyTooltip(!gameBroadcastArmed);
        EndGameDialog();
    }

    private void RequestGameExitConfirmation()
    {
        if (screenController.Engine.IsPlayingGame)
            activeGameDialog = "Exit game";
    }

    private void DrawGameExitConfirmation()
    {
        if (activeGameDialog != "Exit game") return;
        if (!IsLocalActivityHost || !screenController.Engine.IsPlayingGame)
        {
            activeGameDialog = null;
            return;
        }
        if (!BeginGameDialog("Exit game", "Exit game?", FontAwesomeIcon.ExclamationTriangle, 510f, 230f))
            return;

        ImGui.TextWrapped("Exiting the game will lose any unsaved progress.");
        ImGui.Dummy(UiVec(0f, 30f));
        var buttonWidth = (ImGui.GetContentRegionAvail().X - Ui(10f)) * 0.5f;
        if (GameLayoutButton("Cancel", FontAwesomeIcon.Times, buttonWidth))
            activeGameDialog = null;
        ImGui.SameLine(0f, Ui(10f));
        if (GameLayoutButton("Exit Game", FontAwesomeIcon.SignOutAlt, buttonWidth, danger: true))
        {
            activeGameDialog = null;
            StopGameWatchPartyBroadcast();
            screenController.Engine.StopVideo();
            snesLaunchError = gameBoyLaunchError = nesLaunchError = gameBoyAdvanceLaunchError = masterSystemLaunchError = gameGearLaunchError = null;
        }
        EndGameDialog();
    }

    // Match the Add Media overlays: intercept input and shade only Alpha Channel.
    private bool BeginGameDialog(string id, string title, FontAwesomeIcon icon, float width, float height)
    {
        if (activeGameDialog != id) return false;
        var parentPosition = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var size = new Vector2(MathF.Min(Ui(width), MathF.Max(1f, parentSize.X - Ui(40f))),
            MathF.Min(Ui(height), MathF.Max(1f, parentSize.Y - Ui(40f))));
        ImGui.SetNextWindowPos(parentPosition, ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        if (!BeginWindowOverlay("##gameDialogOverlay", ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground))
        {
            ImGui.End();
            return false;
        }
        ImGui.GetWindowDrawList().AddRectFilled(parentPosition, parentPosition + parentSize,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.54f)));
        ImGui.SetCursorScreenPos(parentPosition + (parentSize - size) / 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, UiVec(24f, 20f));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Ui(14f));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, Ui(1f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, CardBg);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.82f));
        UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##gameDialogCard", size, true, id is "Arcade scoreboard" or "Internet Archive episodes" or "Browser settings" or "Browser history" ||
            (id == MusicDialogId && musicDialog == "guide")
            ? ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            : ImGuiWindowFlags.None));
        var headingStart = ImGui.GetCursorScreenPos();
        var headingWidth = ImGui.GetContentRegionAvail().X;
        if (id is "Arcade scoreboard" or "Internet Archive episodes" or "Keyboard controls" or "Controller controls" or "Game library" or "Game ROM information" or "Browser settings" or "Browser history" or "Browser favourites" or "Browser broadcast" || id == MusicDialogId && (musicDialog is "connection" or "guide" or "station" or "stationActions" or "stationDelete"
            or "text" or "variables" or "save" or "saved" or "load" or "delete" or "reset" ||
            musicDialog?.StartsWith("studio", StringComparison.Ordinal) == true))
        {
            using var titleFont = UiFonts.PushRelative(1f, emphasis: true);
            using var titleColour = ImRaii.PushColor(ImGuiCol.Text, Hex(0xE2E7EF));
            GameLayoutHeading(title, icon);
        }
        else GameLayoutHeading(title, icon);
        var afterHeading = ImGui.GetCursorScreenPos();
        if (id is "Game library" or "Browser broadcast" or "Game broadcast" or "Local video broadcast" or "Arcade scoreboard")
        {
            ImGui.SetCursorScreenPos(headingStart + new Vector2(headingWidth - Ui(28f), -Ui(4f)));
            using var closeColors = ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero);
            if (id == "Arcade scoreboard")
                closeColors.Push(ImGuiCol.ButtonHovered, Vector4.Zero)
                    .Push(ImGuiCol.ButtonActive, Vector4.Zero);
            if (ImGui.Button("X##closeLibrary", UiVec(28f, 28f))) activeGameDialog = null;
            ImGui.SetCursorScreenPos(afterHeading);
        }
        ImGui.Separator();
        ImGui.Spacing();
        return true;
    }

    private static void EndGameDialog()
    {
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
        ImGui.End();
    }

    private void DrawGameBindingCell(string label, int value, Action<int> setValue)
    {
        ImGui.TableNextColumn();
        ImGui.PushID(label);
        if (ImGui.BeginTable("##bindingPair", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Control", ImGuiTableColumnFlags.WidthStretch, 0.42f);
            ImGui.TableSetupColumn("Key", ImGuiTableColumnFlags.WidthStretch, 0.58f);
            DrawSnesConfigRow(label, value, setValue);
            ImGui.EndTable();
        }
        ImGui.PopID();
    }

    private void DrawGameRomHelp()
    {
        var snes = selectedGameSystem == GameSystem.Snes;
        var nes = selectedGameSystem == GameSystem.Nes;
        var gameBoyAdvance = selectedGameSystem == GameSystem.GameBoyAdvance;
        var masterSystem = selectedGameSystem == GameSystem.MasterSystem;
        var gameGear = selectedGameSystem == GameSystem.GameGear;
        if (!BeginGameDialog("Game ROM information", "ROM information", FontAwesomeIcon.InfoCircle, 650f, masterSystem || !(snes || nes || gameBoyAdvance || gameGear) ? 600f : 490f)) return;
        var systemName = snes ? "SNES" : nes ? "NES" : gameBoyAdvance ? "Game Boy Advance" : masterSystem ? "Master System and SG-1000" : gameGear ? "Game Gear" : "Game Boy and Game Boy Color";
        DrawGameRomParagraph($"It is possible to obtain {systemName} ROMs from many websites, which can be found using your preferred search engine.");
        ImGui.Spacing();
        ImGui.TextColored(Accent, "What is a ROM?");
        DrawGameRomParagraph("A rom is a digital file containing an exact copy of the data extracted from a video game cartridge. They can be found for free online from many sources.");
        ImGui.Spacing();
        if (snes)
        {
            DrawGameRomFileGuidance("SNES", ".sfc or .smc");
        }
        else if (nes)
        {
            DrawGameRomFileGuidance("NES", ".nes");
        }
        else if (gameBoyAdvance)
        {
            DrawGameRomFileGuidance("Game Boy Advance", ".gba");
        }
        else if (masterSystem)
        {
            DrawGameRomFileGuidance("Master System", ".sms");
            DrawGameRomFileGuidance("SG-1000", ".sg");
        }
        else if (gameGear)
        {
            DrawGameRomFileGuidance("Game Gear", ".gg");
        }
        else
        {
            DrawGameRomFileGuidance("Game Boy", ".gb or .dmg");
            ImGui.Spacing();
            DrawGameRomFileGuidance("Game Boy Color", ".gbc");
        }
        ImGui.Spacing();
        ImGui.TextColored(Gold, "Third-party download notice");
        DrawGameRomParagraph("Alpha Channel does not host these files and is not responsible for the content, safety, or legality of downloads from third-party websites. Only download ROMs you are legally permitted to use and exercise normal internet safety when downloading files from unfamiliar sources.");
        ImGui.Spacing();
        if (GameLayoutButton("Close", FontAwesomeIcon.Check, ImGui.GetContentRegionAvail().X, true)) activeGameDialog = null;
        EndGameDialog();
    }

    private static void DrawGameRomFileGuidance(string console, string extensions)
    {
        ImGui.TextColored(Accent, $"{console} file type");
        DrawGameRomParagraph($"A {console} ROM should normally use the {extensions} file extension. Checking the extension can help you identify whether a downloaded file is likely to be for the correct console.");
        ImGui.Spacing();
        DrawGameRomParagraph("ROM downloads may be supplied inside a .zip file. If so, extract the ZIP file first, then import the ROM file contained inside it.");
    }

    private static void DrawGameRomParagraph(string text)
    {
        using var font = UiFonts.PushRelative(.90f);
        using var colour = ImRaii.PushColor(ImGuiCol.Text, Hex(0xE2E7EF));
        ImGui.TextWrapped(text);
    }
    private bool DrawGameCrtToggle(ref bool enabled)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("CRT filter");
        ImGui.SameLine(0f, Ui(14f));
        var origin = ImGui.GetCursorScreenPos();
        var size = UiVec(42f, 24f);
        var clicked = ImGui.InvisibleButton("##gameCrtToggle", size);
        if (clicked) enabled = !enabled;
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(enabled ? (hovered ? AccentHover : Accent) : CardBgHover), size.Y / 2f);
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(hovered ? AccentHover : MutedText), size.Y / 2f, ImDrawFlags.None, Ui(1f));
        draw.AddCircleFilled(origin + new Vector2(enabled ? size.X - size.Y / 2f : size.Y / 2f, size.Y / 2f), size.Y / 2f - Ui(3f), ImGui.GetColorU32(Vector4.One));
        if (hovered) UiFonts.SetTooltip("Apply a CRT screen style filter to the game.");
        ImGui.SameLine(0f, Ui(10f));
        using (UiFonts.PushIcon()) ImGui.TextColored(MutedText, FontAwesomeIcon.InfoCircle.ToIconString());
        return clicked;
    }
}

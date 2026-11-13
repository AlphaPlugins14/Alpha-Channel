using AlphaChannel.Plugin.BroadcastAllowance;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string retroHubSearch = string.Empty;
    private GameLibraryEntry[] retroHubGames = [];
    private readonly int[] retroHubCounts = new int[6];
    private DateTime retroHubRefreshAfter;
    private GameSystem? retroHubListedSystem;
    private int retroHubLibraryCount = -1;
    private static readonly GameSystem[] RetroHubSystems =
        [GameSystem.Snes, GameSystem.GameBoy, GameSystem.MasterSystem, GameSystem.Nes, GameSystem.GameBoyAdvance, GameSystem.GameGear];

    private void DrawRetroHubPage()
    {
        using var id = ImRaii.PushId("retroHub");
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8, 8));
        using var frames = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(10, 6))
            .Push(ImGuiStyleVar.FrameRounding, Ui(7));
        using var colors = ImRaii.PushColor(ImGuiCol.FrameBg, FrameBg)
            .Push(ImGuiCol.FrameBgHovered, FrameBgHover).Push(ImGuiCol.FrameBgActive, AccentFieldActive)
            .Push(ImGuiCol.SliderGrab, Accent).Push(ImGuiCol.SliderGrabActive, AccentHover)
            .Push(ImGuiCol.PlotHistogram, Accent);
        RestoreLibrarySelection();
        var engine = screenController.Engine;
        var playing = engine.IsPlayingGame;
        if (engine.IsPlayingSnes) selectedGameSystem = GameSystem.Snes;
        else if (engine.IsPlayingNes) selectedGameSystem = GameSystem.Nes;
        else if (engine.IsPlayingGameBoyAdvance) selectedGameSystem = GameSystem.GameBoyAdvance;
        else if (engine.IsPlayingMasterSystem) selectedGameSystem = GameSystem.MasterSystem;
        else if (engine.IsPlayingGameGear) selectedGameSystem = GameSystem.GameGear;
        else if (engine.IsPlayingGameBoy) selectedGameSystem = GameSystem.GameBoy;
        RefreshRetroHubLibrary();

        DrawRetroHubBanner();
        SetGameLayoutRowGap(Ui(12));
        DrawRetroHubSystems(playing);
        SetGameLayoutRowGap(Ui(12));
        var width = ImGui.GetContentRegionAvail().X;
        if (width >= Ui(660))
        {
            var gap = Ui(12);
            var partyWidth = Math.Clamp(width * .35f, Ui(240), Ui(290));
            var height = RetroHubLibraryHeight();
            using (ImRaii.Group()) DrawRetroHubLibrary(playing, width - partyWidth - gap);
            ImGui.SameLine(0, gap);
            using (ImRaii.Group()) DrawRetroHubBroadcast(playing, partyWidth, height);
        }
        else
        {
            DrawRetroHubLibrary(playing);
            SetGameLayoutRowGap(Ui(12));
            DrawRetroHubBroadcast(playing);
        }
        SetGameLayoutRowGap(Ui(12));
        DrawRetroHubControls(playing);
    }

    private void RefreshRetroHubLibrary()
    {
        if (retroHubListedSystem == selectedGameSystem && retroHubLibraryCount == Plugin.Cfg.GameLibrary.Count &&
            DateTime.UtcNow < retroHubRefreshAfter) return;
        retroHubRefreshAfter = DateTime.UtcNow.AddMilliseconds(500);
        retroHubListedSystem = selectedGameSystem;
        retroHubLibraryCount = Plugin.Cfg.GameLibrary.Count;
        Array.Clear(retroHubCounts);
        foreach (var entry in Plugin.Cfg.GameLibrary) retroHubCounts[(int)LibraryEntrySystem(entry)]++;
        retroHubGames = Plugin.Cfg.GameLibrary.Where(entry => LibraryEntryMatchesSystem(entry, selectedGameSystem) &&
                entry.Name.Contains(retroHubSearch, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void DrawRetroHubBanner()
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var artwork = width >= Ui(610);
        var height = Ui(154);
        ImGui.Dummy(new Vector2(width, height));
        var draw = ImGui.GetWindowDrawList();
        DrawArcadeGradientPanel(draw, origin, new Vector2(width, height), HubSurface(0x0C1B33), HubSurface(0x040A15), Accent, 10);
        draw.PushClipRect(origin + UiVec(1, 1), origin + new Vector2(width - Ui(1), height - Ui(1)), true);
        DrawRetroHubPixelLabel(draw, origin + UiVec(20, 17));
        var textWidth = width - Ui(artwork ? 235 : 40);
        using (UiFonts.PushRelative(1.85f, emphasis: true))
            DrawWrappedLines(draw, origin + UiVec(20, 40), textWidth, ImGui.GetTextLineHeight(), 2,
                ImGui.GetColorU32(Vector4.One), "Your classics. Ready to play.", out _);
        using (UiFonts.PushRegularRelative(1f))
            DrawWrappedLines(draw, origin + UiVec(20, 112), textWidth, ImGui.GetTextLineHeight(), 2,
                ImGui.GetColorU32(MutedText), "Pick a console, load a game and make yourself at home.", out _);
        if (artwork) DrawRetroHubSetup(draw, origin + new Vector2(width - Ui(210), height - Ui(142)), Ui(1));
        draw.PopClipRect();
    }

    private void DrawRetroHubSystems(bool playing)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var columns = width >= Ui(600) ? 3 : 2;
        var gap = Ui(10);
        var tileWidth = (width - gap * (columns - 1)) / columns;
        for (var i = 0; i < RetroHubSystems.Length; i++)
        {
            if (i % columns != 0) ImGui.SameLine(0, gap);
            var system = RetroHubSystems[i];
            using var id = ImRaii.PushId((int)system);
            var origin = ImGui.GetCursorScreenPos();
            var size = new Vector2(tileWidth, Ui(66));
            var selected = selectedGameSystem == system;
            bool clicked;
            using (UiDisabled.Scope(playing)) clicked = ImGui.InvisibleButton("console", size);
            if (clicked) { selectedGameSystem = system; RefreshRetroHubLibrary(); }
            var draw = ImGui.GetWindowDrawList();
            DrawArcadeGradientPanel(draw, origin, size, selected ? HubSelected(0x163765) : HubSurface(0x101C30),
                HubSurface(0x080F1D), selected ? Accent : HubTint(0x24456B), 8);
            if (selected) draw.AddRectFilled(origin + new Vector2(Ui(9), size.Y - Ui(3)),
                origin + size - UiVec(9, 0), ImGui.GetColorU32(Accent), Ui(1));
            DrawRetroHubConsole(draw, origin + UiVec(10, 14), Ui(.7f), system);
            var label = RetroHubSystemName(system);
            var labelWidth = MathF.Max(1, tileWidth - Ui(76));
            draw.PushClipRect(origin + UiVec(68, 0), origin + size - UiVec(5, 0), true);
            using (UiFonts.PushRelative(.93f, emphasis: true))
                draw.AddText(origin + UiVec(68, 14), ImGui.GetColorU32(Vector4.One), TruncateToWidth(label, labelWidth));
            using (UiFonts.PushRegularRelative(.84f))
            {
                var count = retroHubCounts[(int)system];
                draw.AddText(origin + UiVec(68, 38), ImGui.GetColorU32(MutedText), $"{count} {(count == 1 ? "game" : "games")}");
            }
            draw.PopClipRect();
        }
    }

    private static string RetroHubSystemName(GameSystem system) => system switch
    {
        GameSystem.Nes => "NES",
        _ => LibraryFilterName(system)
    };

    private float RetroHubLibraryHeight() => Ui(Math.Clamp(retroHubGames.Length, 2, 4) * 66 + 160);

    private void DrawRetroHubLibrary(bool playing, float? panelWidth = null)
    {
        var rowHeight = Ui(60);
        var rowGap = Ui(6);
        var rowsHeight = Math.Clamp(retroHubGames.Length, 2, 4) * (rowHeight + rowGap);
        RetroHubPanel("library", panelWidth ?? ImGui.GetContentRegionAvail().X, RetroHubLibraryHeight(), () =>
        {
            var width = ImGui.GetContentRegionAvail().X;
            ImGui.BeginGroup();
            using (UiFonts.PushRelative(1.3f, emphasis: true)) ImGui.TextUnformatted("Your library");
            using (UiFonts.PushRegularRelative(.85f)) ImGui.TextColored(MutedText,
                $"{RetroHubSystemName(selectedGameSystem)} · {retroHubCounts[(int)selectedGameSystem]} games");
            ImGui.EndGroup();
            ImGui.SameLine(MathF.Max(0, width - Ui(124)) + ImGui.GetStyle().WindowPadding.X);
            using (UiDisabled.Scope(playing))
                if (GameLayoutButton("Add games...", FontAwesomeIcon.FolderOpen, Ui(124), height: 34)) AddLibraryGame(selectedGameSystem);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextWithHint("##librarySearch", "Search your library...", ref retroHubSearch, 150))
            { retroHubRefreshAfter = DateTime.MinValue; RefreshRetroHubLibrary(); }
            using (var rows = ImRaii.Child("games", new Vector2(0, rowsHeight), false))
            {
                if (rows)
                {
                    if (retroHubGames.Length == 0)
                    {
                        ImGui.Dummy(UiVec(0, 16));
                        ImGui.TextColored(MutedText, string.IsNullOrWhiteSpace(retroHubSearch)
                            ? "No games added for this console yet." : "No games match your search.");
                        if (string.IsNullOrWhiteSpace(retroHubSearch))
                        {
                            using (UiDisabled.Scope(playing))
                                if (GameLayoutButton("Add your first game", FontAwesomeIcon.Plus, MathF.Min(Ui(220), width)))
                                    AddLibraryGame(selectedGameSystem);
                        }
                    }
                    else
                    {
                        // Submit only visible rows, while retaining the full scroll extent.
                        using var tightRows = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
                        var stride = rowHeight + rowGap;
                        var first = Math.Max(0, (int)(ImGui.GetScrollY() / stride) - 1);
                        var last = Math.Min(retroHubGames.Length, first + (int)MathF.Ceiling(rowsHeight / stride) + 3);
                        var start = ImGui.GetCursorPosY();
                        if (first > 0) ImGui.Dummy(new Vector2(1, first * stride));
                        for (var i = first; i < last; i++) DrawRetroHubGameRow(retroHubGames[i], playing, rowHeight, rowGap);
                        ImGui.SetCursorPosY(start + retroHubGames.Length * stride);
                        ImGui.Dummy(new Vector2(1, 0));
                    }
                }
            }
            var half = (ImGui.GetContentRegionAvail().X - Ui(10)) / 2;
            if (GameLayoutButton("Manage library", FontAwesomeIcon.List, half, height: 30))
            {
                libraryAction = null; libraryActionEntry = null; libraryFilter = selectedGameSystem;
                librarySearch = string.Empty; libraryPendingSelection = null; activeGameDialog = "Game library";
            }
            ImGui.SameLine(0, Ui(10));
            if (GameLayoutButton("ROM information", FontAwesomeIcon.InfoCircle, half, height: 30)) snesRomSourcesPopupRequested = true;
        }, 12);
        if (!string.IsNullOrWhiteSpace(libraryError)) ImGui.TextColored(Danger, libraryError);
        var error = selectedGameSystem switch
        {
            GameSystem.Snes => snesLaunchError, GameSystem.Nes => nesLaunchError,
            GameSystem.GameBoyAdvance => gameBoyAdvanceLaunchError, GameSystem.MasterSystem => masterSystemLaunchError,
            GameSystem.GameGear => gameGearLaunchError, _ => gameBoyLaunchError
        };
        if (!string.IsNullOrWhiteSpace(error)) { using var color = ImRaii.PushColor(ImGuiCol.Text, Danger); ImGui.TextWrapped(error); }
    }

    private void DrawRetroHubGameRow(GameLibraryEntry entry, bool playing, float height, float gap)
    {
        using var id = ImRaii.PushId(entry.Hash);
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var selected = SelectedLibraryHash(selectedGameSystem) == entry.Hash;
        var buttonWidth = Ui(96);
        using (UiDisabled.Scope(playing))
            if (ImGui.InvisibleButton("select", new Vector2(MathF.Max(1, width - buttonWidth - Ui(20)), height)))
            { SelectLibraryGame(entry); Plugin.Cfg.Save(); }
        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        var draw = ImGui.GetWindowDrawList();
        DrawArcadeGradientPanel(draw, origin, new Vector2(width, height), selected ? HubSelected(0x17345D) : hovered ? HubSelected(0x14243C) : HubSurface(0x0C1728),
            HubSurface(0x09111F), selected ? Accent : HubTint(0x234368), 7);
        DrawRetroHubCartridge(draw, origin + UiVec(12, 12), Ui(.65f));
        var textWidth = MathF.Max(1, width - buttonWidth - Ui(90));
        draw.PushClipRect(origin + UiVec(63, 0), origin + new Vector2(width - buttonWidth - Ui(20), height), true);
        using (UiFonts.PushRelative(1f, emphasis: true))
            draw.AddText(origin + UiVec(63, 10), ImGui.GetColorU32(Vector4.One), TruncateToWidth(entry.Name, textWidth));
        using (UiFonts.PushRegularRelative(.82f))
            draw.AddText(origin + UiVec(63, 34), ImGui.GetColorU32(MutedText), LibrarySystemName(entry));
        draw.PopClipRect();
        if (hovered) UiFonts.SetTooltip(entry.Name);
        ImGui.SetCursorScreenPos(origin + new Vector2(width - buttonWidth - Ui(8), (height - Ui(34)) / 2));
        var blocked = playing || screenController.Engine.IsPlayingLocalVideo || screenController.Engine.IsPlayingWhiteboard || stream.Mode == StreamMode.Viewing;
        using (UiDisabled.Scope(blocked))
            if (GameLayoutButton(playing && selected ? "Playing" : "Play", FontAwesomeIcon.Play, buttonWidth, selected, height: 34))
                StartRetroHubGame(entry);
        if (blocked && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiFonts.SetTooltip(playing ? "Exit the current game before starting another." : "Leave the Watch Party or stop the current local activity first.");
        ImGui.SetCursorScreenPos(origin + new Vector2(0, height));
        ImGui.Dummy(new Vector2(width, gap));
    }

    private void StartRetroHubGame(GameLibraryEntry entry)
    {
        var engine = screenController.Engine;
        if (engine.IsPlayingGame || engine.IsPlayingLocalVideo || engine.IsPlayingWhiteboard || stream.Mode == StreamMode.Viewing) return;
        try
        {
            SelectLibraryGame(entry);
            Plugin.Cfg.Save();
            var path = LibraryStorage.RomPath(entry);
            StopGameWatchPartyBroadcast();
            snesLaunchError = gameBoyLaunchError = nesLaunchError = gameBoyAdvanceLaunchError = masterSystemLaunchError = gameGearLaunchError = null;
            var started = selectedGameSystem switch
            {
                GameSystem.Snes => engine.PlaySnes(path), GameSystem.Nes => engine.PlayNes(path),
                GameSystem.GameBoyAdvance => engine.PlayGameBoyAdvance(path), GameSystem.MasterSystem => engine.PlayMasterSystem(path),
                GameSystem.GameGear => engine.PlayGameGear(path), _ => engine.PlayGameBoy(path)
            };
            if (!started) libraryError = engine.LastError ?? "The game could not be started.";
            else libraryError = null;
        }
        catch (Exception) { libraryError = "The game could not be opened. Check its entry in Manage library."; }
    }

    private void DrawRetroHubBroadcast(bool playing, float? panelWidth = null, float? panelHeight = null)
    {
        var width = panelWidth ?? ImGui.GetContentRegionAvail().X;
        if (AccountBroadcastRestricted)
        {
            RetroHubPanel("broadcast", width, MathF.Max(panelHeight ?? 0, Ui(245)), () =>
            {
                using (UiFonts.PushRelative(1.06f, emphasis: true)) ImGui.TextUnformatted("Bring your party along");
                ImGui.Spacing();
                DrawAccountBroadcastRestriction("Retro game");
            }, 12);
            return;
        }
        var vertical = panelWidth.HasValue || width < Ui(480);
        var wide = width >= Ui(720);
        RetroHubPanel("broadcast", width, panelHeight ?? Ui(vertical ? 290 : wide ? 130 : 185), () =>
        {
            var origin = ImGui.GetCursorScreenPos();
            var available = ImGui.GetContentRegionAvail().X;
            var actionsWidth = vertical ? available : Ui(185);
            var descriptionWidth = vertical ? available : MathF.Max(Ui(120), available - actionsWidth - Ui(wide ? 214 : 16));
            var armed = gameBroadcastArmed && gameBroadcastSystem == selectedGameSystem;
            using (UiFonts.PushRelative(1.06f, emphasis: true)) ImGui.TextUnformatted("Bring your party along");
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + descriptionWidth);
            ImGui.TextColored(MutedText, "Share your gameplay with your Watch Party.");
            ImGui.PopTextWrapPos();
            ImGui.TextColored(armed ? gameBroadcastStartFailed ? Danger : Good : MutedText, armed ? gameBroadcastStartFailed ? "Broadcast failed" :
                gameBroadcastPausedForInactivity ? "Paused for inactivity" : GameBroadcastEncoderRunning ? "Live" : "Waiting for viewers" : "Offline");
            if (vertical) ImGui.Spacing();
            else if (wide) ImGui.SetCursorScreenPos(origin + new Vector2(descriptionWidth + Ui(12), 0));
            else ImGui.SetCursorScreenPos(origin + UiVec(0, 86));
            using (var allowance = ImRaii.Child("allowance", new Vector2(wide && !vertical ? Ui(185) : available, Ui(70)), false))
                if (allowance) DrawRetroHubAllowance();
            if (!vertical) ImGui.SetCursorScreenPos(origin + new Vector2(available - actionsWidth, 0));
            using var actionGroup = ImRaii.Group();
            var signedIn = CurrentSession is not null;
            var viewing = stream.Mode == StreamMode.Viewing;
            var hosting = stream.Mode == StreamMode.Hosting;
            var hasKey = CurrentSession is { } session && !string.IsNullOrWhiteSpace(Plugin.Cfg.StreamKeys.GetValueOrDefault(session.AccountId));
            if (!signedIn || !hasKey)
            {
                if (GameLayoutButton("Open Settings", FontAwesomeIcon.Cog, actionsWidth, height: 32)) currentPage = HomePage.Settings;
            }
            else
            {
                using var disabled = UiDisabled.Scope(viewing || hosting);
                if (GameLayoutButton(hosting ? "Watch Party ready" : "Create Watch Party", FontAwesomeIcon.Users, actionsWidth, height: 32))
                {
                    pendingWatchPartyMediaKind = PendingWatchPartyMediaKind.GameRoom;
                    watchPartyCreationPopupOpen = true; createRoomPassword = string.Empty; createLockedRoomPasswordError = null;
                }
            }
            using (UiDisabled.Scope(!armed && (!playing || !hosting || !signedIn || !hasKey)))
                if (GameLayoutButton(armed ? "Stop broadcast" : "Start broadcast", armed ? FontAwesomeIcon.Stop : FontAwesomeIcon.BroadcastTower,
                    actionsWidth, !armed, armed, 32))
                { if (armed) StopGameWatchPartyBroadcast(); else activeGameDialog = "Game broadcast"; }
            DrawMissingBroadcastStreamKeyTooltip(!armed);
            if (armed && ImGui.IsItemHovered()) UiFonts.SetTooltip("Stop sharing gameplay.");
        }, 12);
    }

    private void DrawRetroHubControls(bool playing)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var columns = width >= Ui(570);
        RetroHubPanel("controls", width, Ui((columns ? 290 : 542) + (playing ? 38 : 0)), () =>
        {
            using (UiFonts.PushRelative(1.12f, emphasis: true)) ImGui.TextUnformatted("Controls & display");
            if (playing)
                if (GameLayoutButton("Exit game & despawn TV", FontAwesomeIcon.Stop, ImGui.GetContentRegionAvail().X, danger: true, height: 30))
                    RequestGameExitConfirmation();
            var available = ImGui.GetContentRegionAvail().X;
            var columnWidth = columns ? (available - Ui(28)) / 2 : available;
            using (var input = ImRaii.Child("input", new Vector2(columnWidth, Ui(226)), false))
                if (input) DrawRetroHubInput(playing);
            if (columns) ImGui.SameLine(0, Ui(28));
            using (var audio = ImRaii.Child("audio", new Vector2(columnWidth, Ui(220)), false))
                if (audio) DrawCompactGameAudio(selectedGameSystem);
        }, 12);
    }

    private void RetroHubPanel(string id, float width, float height, Action content, float verticalPadding = 12)
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(14, verticalPadding));
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10));
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, HubSurface(0x0C1526)).Push(ImGuiCol.Border, HubTint(0x24456B));
        using var child = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(id, new Vector2(width, height), true));
        if (child) content();
    }

    private void DrawRetroHubAllowance()
    {
        var allowance = BroadcastAllowanceDisplayFor(BroadcastAllowanceMethod.RetroGame);
        using var font = UiFonts.PushRegularRelative(.82f);
        ImGui.TextColored(MutedText, "Broadcast allowance");
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.ProgressBar(allowance.Remaining, new Vector2(width, Ui(9)), string.Empty);
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip(allowance.Heading + "\n" + allowance.Detail);
        ImGui.TextColored(MutedText, TruncateToWidth(allowance.RemainingLabel, width));
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip(allowance.RemainingLabel + "\n" + allowance.Detail);
    }

    private void DrawRetroHubInput(bool playing)
    {
        using (UiFonts.PushRelative(1f, emphasis: true)) ImGui.TextUnformatted("Input control");
        using (UiFonts.PushRegularRelative(.84f)) ImGui.TextColored(MutedText, "Choose where your controls go.");
        var engine = screenController.Engine;
        var enabled = playing && (selectedGameSystem == GameSystem.Snes ? engine.SnesControlsEnabled : engine.GameBoyControlsEnabled);
        var half = (ImGui.GetContentRegionAvail().X - Ui(8)) / 2;
        using (UiDisabled.Scope(!playing))
            if (GameLayoutButton("Control game", FontAwesomeIcon.Gamepad, half, enabled && !engine.ControllerModeEnabled, height: 32))
            { engine.SetRetroControlMode(AlphaChannel.Plugin.Video.RetroControlMode.Game); }
        ImGui.SameLine(0, Ui(8));
        if (GameLayoutButton("Control FFXIV", FontAwesomeIcon.Desktop, half, !enabled, height: 32))
        { engine.SetRetroControlMode(AlphaChannel.Plugin.Video.RetroControlMode.Ffxiv); }
        using (UiDisabled.Scope(!playing))
            if (GameLayoutButton("Controller Mode", FontAwesomeIcon.Gamepad, ImGui.GetContentRegionAvail().X,
                    enabled && engine.ControllerModeEnabled, height: 32))
                engine.SetRetroControlMode(AlphaChannel.Plugin.Video.RetroControlMode.Controller);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiFonts.SetTooltip("Controller input goes to game, keyboard inputs to ffxiv");
        ImGui.Dummy(UiVec(0, 6));
        if (GameLayoutButton("Keyboard settings", FontAwesomeIcon.Keyboard, ImGui.GetContentRegionAvail().X, height: 30)) gameKeyboardControlsOpenRequested = true;
        if (GameLayoutButton("Controller settings", FontAwesomeIcon.Gamepad, ImGui.GetContentRegionAvail().X, height: 30)) gameControllerControlsOpenRequested = true;
    }
}

using System.Diagnostics;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private GameLibraryStorage LibraryStorage => new(Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "GameLibrary"));
    private string librarySearch = "";
    private string? libraryError;
    private GameLibraryEntry? libraryActionEntry;
    private string? libraryAction;
    private string libraryRename = "";
    private bool libraryRestored;
    // Null represents the shared "All systems" library view. Console metadata
    // remains derived from each ROM extension rather than stored in configuration.
    private GameSystem? libraryFilter;
    // A library-dialog choice is only committed to an emulator when Play Game
    // is pressed. This keeps browsing and closing the library side-effect free.
    private GameLibraryEntry? libraryPendingSelection;

    private void InitializeLocalLibraryRecovery()
    {
        EnsureBrowserLibraryLoaded();
        try
        {
            Plugin.Cfg.GameLibrary ??= new();
            var repaired = LibraryStorage.Repair(Plugin.Cfg.GameLibrary);
            var changed = repaired.Entries.Count != Plugin.Cfg.GameLibrary.Count ||
                repaired.Entries.Where((entry, index) => index >= Plugin.Cfg.GameLibrary.Count ||
                    !LibraryEntriesEqual(entry, Plugin.Cfg.GameLibrary[index])).Any();
            Plugin.Cfg.GameLibrary = repaired.Entries;
            if (changed) Plugin.Cfg.Save();
            if (!string.IsNullOrWhiteSpace(repaired.RecoveryMessage))
                Plugin.ChatGui.Print("[AlphaChannel] " + repaired.RecoveryMessage);
        }
        catch (Exception)
        {
            AepLog.Warning("[GAME LIBRARY] Startup repair failed.");
            Plugin.ChatGui.Print("[AlphaChannel] The game library could not be checked. Existing files were left untouched.");
        }
    }

    private static bool LibraryEntriesEqual(GameLibraryEntry left, GameLibraryEntry right) =>
        left.Hash == right.Hash && left.Extension == right.Extension && left.Name == right.Name && left.Icon == right.Icon;

    private void SaveGameLibraryMetadata()
    {
        Plugin.Cfg.Save();
        LibraryStorage.SaveIndex(Plugin.Cfg.GameLibrary);
    }

    private string LibraryGameName(string path)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(path));
        return Plugin.Cfg.GameLibrary.FirstOrDefault(e => e.Hash == folder)?.Name ?? Path.GetFileNameWithoutExtension(path);
    }

    private void SelectLibraryGame(GameLibraryEntry entry, bool switchSystem = true)
    {
        var path = LibraryStorage.RomPath(entry);
        var system = LibraryEntrySystem(entry);

        // Selecting from the shared All view also takes the main games page to
        // the emulator that can run this ROM.
        if (switchSystem)
        {
            selectedGameSystem = system;
        }

        if (system == GameSystem.Snes)
        {
            snesSelectedRomPath = path;
            Plugin.Cfg.SelectedSnesLibraryHash = entry.Hash;
        }
        else if (system == GameSystem.Nes)
        {
            nesSelectedRomPath = path;
            Plugin.Cfg.SelectedNesLibraryHash = entry.Hash;
        }
        else if (system == GameSystem.GameBoyAdvance)
        {
            gameBoyAdvanceSelectedRomPath = path;
            Plugin.Cfg.SelectedGameBoyAdvanceLibraryHash = entry.Hash;
        }
        else if (system == GameSystem.MasterSystem)
        {
            masterSystemSelectedRomPath = path;
            Plugin.Cfg.SelectedMasterSystemLibraryHash = entry.Hash;
        }
        else if (system == GameSystem.GameGear)
        {
            gameGearSelectedRomPath = path;
            Plugin.Cfg.SelectedGameGearLibraryHash = entry.Hash;
        }
        else
        {
            gameBoySelectedRomPath = path;
            Plugin.Cfg.SelectedGameBoyLibraryHash = entry.Hash;
        }
    }

    private void RestoreLibrarySelection()
    {
        if (libraryRestored) return;
        libraryRestored = true;
        Plugin.Cfg.GameLibrary ??= new();
        foreach (var entry in Plugin.Cfg.GameLibrary)
        {
            if (entry.Hash != Plugin.Cfg.SelectedSnesLibraryHash && entry.Hash != Plugin.Cfg.SelectedGameBoyLibraryHash && entry.Hash != Plugin.Cfg.SelectedNesLibraryHash && entry.Hash != Plugin.Cfg.SelectedGameBoyAdvanceLibraryHash && entry.Hash != Plugin.Cfg.SelectedMasterSystemLibraryHash && entry.Hash != Plugin.Cfg.SelectedGameGearLibraryHash) continue;
            try { SelectLibraryGame(entry, switchSystem: false); }
            catch (Exception e) { libraryError = e.Message; }
        }
    }

    private static bool LibraryEntryMatchesSystem(GameLibraryEntry entry, GameSystem system) => system switch
    {
        GameSystem.Snes => GameLibraryStorage.IsSnes(entry),
        GameSystem.Nes => GameLibraryStorage.IsNes(entry),
        GameSystem.GameBoyAdvance => GameLibraryStorage.IsGameBoyAdvance(entry),
        GameSystem.MasterSystem => GameLibraryStorage.IsMasterSystem(entry),
        GameSystem.GameGear => GameLibraryStorage.IsGameGear(entry),
        GameSystem.GameBoy => !GameLibraryStorage.IsSnes(entry) && !GameLibraryStorage.IsNes(entry) && !GameLibraryStorage.IsGameBoyAdvance(entry) && !GameLibraryStorage.IsMasterSystem(entry) && !GameLibraryStorage.IsGameGear(entry),
        _ => false
    };

    private static GameSystem LibraryEntrySystem(GameLibraryEntry entry) =>
        GameLibraryStorage.IsSnes(entry)
            ? GameSystem.Snes
            : GameLibraryStorage.IsNes(entry)
                ? GameSystem.Nes
                : GameLibraryStorage.IsGameBoyAdvance(entry)
                    ? GameSystem.GameBoyAdvance
                    : GameLibraryStorage.IsMasterSystem(entry)
                        ? GameSystem.MasterSystem
                        : GameLibraryStorage.IsGameGear(entry)
                            ? GameSystem.GameGear
                        : GameSystem.GameBoy;

    private static string LibrarySystemName(GameLibraryEntry entry) =>
        GameLibraryStorage.IsSnes(entry)
            ? "Super Nintendo"
            : GameLibraryStorage.IsNes(entry)
                ? "Nintendo Entertainment System"
                : GameLibraryStorage.IsGameBoyAdvance(entry)
                    ? "Game Boy Advance"
                    : GameLibraryStorage.IsMasterSystem(entry)
                        ? entry.Extension == ".sg" ? "SG-1000" : "Sega Master System"
                    : GameLibraryStorage.IsGameGear(entry)
                        ? "Game Gear"
                    : entry.Extension == ".gbc"
                        ? "Game Boy Color"
                        : "Game Boy";

    private static string LibraryFilterName(GameSystem? system) => system switch
    {
        GameSystem.Snes => "Super Nintendo",
        GameSystem.Nes => "Nintendo Entertainment System",
        GameSystem.GameBoyAdvance => "Game Boy Advance",
        GameSystem.MasterSystem => "Master System / SG-1000",
        GameSystem.GameGear => "Game Gear",
        GameSystem.GameBoy => "Game Boy / Color",
        _ => "All systems"
    };

    private static bool LibraryEntryMatchesFilter(GameLibraryEntry entry, GameSystem? system) =>
        system is null || LibraryEntryMatchesSystem(entry, system.Value);

    private string? SelectedLibraryHash(GameSystem system) => system switch
    {
        GameSystem.Snes => Plugin.Cfg.SelectedSnesLibraryHash,
        GameSystem.Nes => Plugin.Cfg.SelectedNesLibraryHash,
        GameSystem.GameBoyAdvance => Plugin.Cfg.SelectedGameBoyAdvanceLibraryHash,
        GameSystem.MasterSystem => Plugin.Cfg.SelectedMasterSystemLibraryHash,
        GameSystem.GameGear => Plugin.Cfg.SelectedGameGearLibraryHash,
        _ => Plugin.Cfg.SelectedGameBoyLibraryHash
    };

    private void AddLibraryGame(GameSystem? requestedSystem)
    {
        var dialog = requestedSystem == GameSystem.Snes ? snesFileDialog : requestedSystem == GameSystem.Nes ? nesFileDialog : requestedSystem == GameSystem.GameBoyAdvance ? gameBoyAdvanceFileDialog : requestedSystem == GameSystem.MasterSystem ? masterSystemFileDialog : requestedSystem == GameSystem.GameGear ? gameGearFileDialog : gameBoyFileDialog;
        // Show all files regardless of the current tab. Detect the emulator
        // from the chosen ROM, rather than requiring another browse attempt.
        dialog.OpenFileDialog("Add game to library", ".*", (success, path) =>
        {
            if (!success || string.IsNullOrWhiteSpace(path)) return;
            try
            {
                // Do not replace selection while an emulator owns the screen.
                if (screenController.Engine.IsPlayingGame) return;
                var extension = Path.GetExtension(path).ToLowerInvariant();
                var detectedSystem = extension switch
                {
                    ".sfc" or ".smc" => GameSystem.Snes,
                    ".nes" => GameSystem.Nes,
                    ".gba" => GameSystem.GameBoyAdvance,
                    ".sms" or ".sg" => GameSystem.MasterSystem,
                    ".gg" => GameSystem.GameGear,
                    ".gb" or ".gbc" or ".dmg" => GameSystem.GameBoy,
                    _ => throw new InvalidOperationException("That file is not a supported game ROM.")
                };
                var entry = detectedSystem == GameSystem.Nes
                    ? LibraryStorage.ImportNes(path, Plugin.Cfg.GameLibrary)
                    : detectedSystem == GameSystem.GameBoyAdvance
                        ? LibraryStorage.ImportGameBoyAdvance(path, Plugin.Cfg.GameLibrary)
                        : detectedSystem == GameSystem.MasterSystem
                            ? LibraryStorage.ImportMasterSystem(path, Plugin.Cfg.GameLibrary)
                        : detectedSystem == GameSystem.GameGear
                            ? LibraryStorage.ImportGameGear(path, Plugin.Cfg.GameLibrary)
                        : LibraryStorage.Import(path, detectedSystem == GameSystem.Snes, Plugin.Cfg.GameLibrary);
                if (!Plugin.Cfg.GameLibrary.Contains(entry)) Plugin.Cfg.GameLibrary.Add(entry);
                SelectLibraryGame(entry);
                if (libraryFilter is not null) libraryFilter = detectedSystem;
                SaveGameLibraryMetadata();
                libraryError = null;
            }
            catch (Exception e) { libraryError = "Could not import game: " + e.Message; }
        });
    }

    private void DrawGameLibraryDialog()
    {
        if (!BeginGameDialog("Game library", "Manage library", FontAwesomeIcon.Gamepad, 660f, 520f)) return;
        using var popupStyle = ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, Ui(9f));
        using var popupColors = ImRaii.PushColor(ImGuiCol.PopupBg, CardBg)
            .Push(ImGuiCol.Border, BorderSubtle).Push(ImGuiCol.HeaderHovered, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.25f));
        var system = libraryFilter;
        var playing = screenController.Engine.IsPlayingGame;
        if (libraryActionEntry is { } iconTarget && libraryAction == "Change Icon")
        {
            DrawLibraryIconGallery(iconTarget);
        }
        else if (libraryActionEntry is { } target && libraryAction is { } action)
        {
            ImGui.TextUnformatted(target.Name);
            if (action == "Rename") ImGui.InputText("Name", ref libraryRename, 120);
            else ImGui.TextWrapped(action == "Remove from Library"
                ? "Remove this game's imported ROM and save data from the library? Your original files will not be changed. This cannot be undone."
                : "Permanently clear this game's library save data? Your progress will be lost. The ROM and original files will not be changed.");
            if (playing) ImGui.TextWrapped("Exit the running game before changing library data.");
            using (UiDisabled.Scope(playing || (action == "Rename" && string.IsNullOrWhiteSpace(libraryRename))))
            {
                if (GameLayoutButton(action == "Rename" ? "Save name" : "Confirm " + action, FontAwesomeIcon.Check,
                    ImGui.GetContentRegionAvail().X, action == "Rename", action != "Rename"))
                {
                    try
                    {
                        if (action == "Rename") target.Name = libraryRename.Trim();
                        else if (action == "Clear save data") LibraryStorage.ClearSave(target);
                        else
                        {
                            LibraryStorage.Remove(target);
                            Plugin.Cfg.GameLibrary.Remove(target);
                            if (libraryPendingSelection?.Hash == target.Hash) libraryPendingSelection = null;
                            if (Plugin.Cfg.SelectedSnesLibraryHash == target.Hash) { Plugin.Cfg.SelectedSnesLibraryHash = null; snesSelectedRomPath = ""; }
                            if (Plugin.Cfg.SelectedGameBoyLibraryHash == target.Hash) { Plugin.Cfg.SelectedGameBoyLibraryHash = null; gameBoySelectedRomPath = ""; }
                            if (Plugin.Cfg.SelectedNesLibraryHash == target.Hash) { Plugin.Cfg.SelectedNesLibraryHash = null; nesSelectedRomPath = ""; }
                            if (Plugin.Cfg.SelectedGameBoyAdvanceLibraryHash == target.Hash) { Plugin.Cfg.SelectedGameBoyAdvanceLibraryHash = null; gameBoyAdvanceSelectedRomPath = ""; }
                            if (Plugin.Cfg.SelectedMasterSystemLibraryHash == target.Hash) { Plugin.Cfg.SelectedMasterSystemLibraryHash = null; masterSystemSelectedRomPath = ""; }
                            if (Plugin.Cfg.SelectedGameGearLibraryHash == target.Hash) { Plugin.Cfg.SelectedGameGearLibraryHash = null; gameGearSelectedRomPath = ""; }
                        }
                        SaveGameLibraryMetadata();
                        libraryAction = null;
                        libraryError = null;
                    }
                    catch (Exception e) { libraryError = e.Message; }
                }
            }
            if (GameLayoutButton("Cancel", FontAwesomeIcon.Times, ImGui.GetContentRegionAvail().X)) libraryAction = null;
        }
        else
        {
            DrawRetroGameHelper("Browse every imported game or filter by console.");
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##librarySystemFilter", LibraryFilterName(system))))
            {
                GameSystem?[] filters = [null, GameSystem.Snes, GameSystem.Nes, GameSystem.GameBoy, GameSystem.GameBoyAdvance, GameSystem.MasterSystem, GameSystem.GameGear];
                foreach (var filter in filters)
                {
                    var selectedFilter = filter == system;
                    if (ImGui.Selectable(LibraryFilterName(filter), selectedFilter))
                    {
                        system = filter;
                        libraryFilter = filter;
                    }
                    if (selectedFilter) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            ImGui.Spacing();
            ImGui.SetNextItemWidth(MathF.Max(Ui(60f), ImGui.GetContentRegionAvail().X - Ui(140f)));
            ImGui.InputTextWithHint("##librarySearch", "Search games...", ref librarySearch, 120);
            ImGui.SameLine(0f, Ui(8f));
            using (UiDisabled.Scope(playing))
                if (GameLayoutButton("Add games...", FontAwesomeIcon.FolderOpen, Ui(132f), true)) AddLibraryGame(system);
            using (var list = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##libraryList", new Vector2(0f, MathF.Max(Ui(80f), ImGui.GetContentRegionAvail().Y - Ui(105f))), false)))
            {
                if (list)
                {
                    var entries = Plugin.Cfg.GameLibrary.Where(e => LibraryEntryMatchesFilter(e, system) && e.Name.Contains(librarySearch, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (entries.Count == 0) ImGui.TextWrapped("No games found. Add a ROM to build your library.");
                    foreach (var entry in entries)
                    {
                        ImGui.PushID(entry.Hash);
                        using (UiDisabled.Scope(playing))
                            if (DrawLibraryGameRow(entry, entry.Hash == libraryPendingSelection?.Hash, true))
                            {
                                libraryPendingSelection = entry;
                            }
                        ImGui.SameLine(0f, Ui(8f));
                        using (ImRaii.PushColor(ImGuiCol.Button, FrameBg)
                            .Push(ImGuiCol.ButtonHovered, CardBgHover))
                        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, Ui(1f)))
                            if (ImGui.Button("...", UiVec(36f, 56f))) ImGui.OpenPopup("Actions");
                        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup("Actions")))
                        {
                            if (LibraryMenuItem("Open Folder", FontAwesomeIcon.FolderOpen))
                            {
                                try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(LibraryStorage.RomPath(entry))!) { UseShellExecute = true }); }
                                catch (Exception e) { libraryError = e.Message; }
                            }
                            if (LibraryMenuItem("Change Icon", FontAwesomeIcon.ThLarge))
                            {
                                libraryActionEntry = entry;
                                libraryAction = "Change Icon";
                            }
                            using (UiDisabled.Scope(playing))
                                foreach (var option in new[] { "Rename", "Clear save data", "Remove from Library" })
                                {
                                    using var actionColor = ImRaii.PushColor(ImGuiCol.Text, option == "Rename" ? Vector4.One : Danger);
                                    var optionIcon = option == "Rename" ? FontAwesomeIcon.Pen : option == "Clear save data" ? FontAwesomeIcon.Eraser : FontAwesomeIcon.Trash;
                                    if (LibraryMenuItem(option, optionIcon)) { libraryActionEntry = entry; libraryAction = option; libraryRename = entry.Name; }
                                }
                            ImGui.EndPopup();
                        }
                        ImGui.PopID();
                    }
                }
            }
            using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
                DrawRetroGameHelper("Imported games are copied into your library. Originals stay untouched.\nROMs and save data are kept together.");
            ImGui.Separator();
            var footerWidth = ImGui.GetContentRegionAvail().X;
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(MutedText, $"{Plugin.Cfg.GameLibrary.Count(e => LibraryEntryMatchesFilter(e, system))} games");
            var footerGap = Ui(8f);
            var closeWidth = Ui(132f);
            var playWidth = Ui(120f);
            ImGui.SameLine(footerWidth - closeWidth - playWidth - footerGap + ImGui.GetStyle().WindowPadding.X);
            if (GameLayoutButton("Close library", FontAwesomeIcon.Times, closeWidth))
            {
                libraryPendingSelection = null;
                activeGameDialog = null;
            }
            ImGui.SameLine(0f, footerGap);
            using (UiDisabled.Scope(playing || libraryPendingSelection is null))
            {
                if (GameLayoutButton("Play game", FontAwesomeIcon.Play, playWidth, true) &&
                    libraryPendingSelection is { } selectedGame)
                {
                    SelectLibraryGame(selectedGame);
                    Plugin.Cfg.Save();
                    libraryPendingSelection = null;
                    activeGameDialog = null;
                }
            }
        }
        if (libraryError is not null) ImGui.TextWrapped(libraryError);
        EndGameDialog();
    }

    private bool DrawLibraryGameRow(GameLibraryEntry entry, bool selected, bool showDetails)
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X - (showDetails ? Ui(44f) : 0f));
        var size = new Vector2(width, Ui(showDetails ? 56f : 38f));
        var clicked = ImGui.InvisibleButton("##libraryRow_" + entry.Hash, size);
        var hovered = ImGui.IsItemHovered();
        if (hovered) UiFonts.SetTooltip(entry.Name);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(hovered ? CardBgHover : CardBg), Ui(8f));
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(selected ? Accent : BorderSubtle), Ui(8f));
        var tile = Ui(showDetails ? 40f : 26f);
        var tileOrigin = origin + UiVec(7f, 7f);
        draw.AddRectFilled(tileOrigin, tileOrigin + new Vector2(tile), ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.30f)), Ui(7f));
        using (UiFonts.PushIcon())
        {
            var glyph = GetLibraryIcon(entry).ToIconString();
            var glyphSize = ImGui.CalcTextSize(glyph);
            UiFonts.DrawText(draw, ImGui.GetFontSize(), tileOrigin + (new Vector2(tile) - glyphSize) / 2f, ImGui.GetColorU32(Accent), glyph);
        }
        var textX = tileOrigin.X + tile + Ui(10f);
        var badgeWidth = selected && width > Ui(270f) ? Ui(70f) : 0f;
        draw.PushClipRect(new Vector2(textX, origin.Y), new Vector2(origin.X + width - badgeWidth - Ui(10f), origin.Y + size.Y), true);
        UiFonts.DrawText(draw, ImGui.GetFontSize(), new Vector2(textX, origin.Y + Ui(showDetails ? 9f : 10f)), ImGui.GetColorU32(Hex(0xE2E7EF)), TruncateToWidth(entry.Name, MathF.Max(1f, origin.X + width - badgeWidth - Ui(10f) - textX)));
        if (showDetails)
        {
            using var detailFont = UiFonts.PushRelative(.90f);
            var systemName = LibrarySystemName(entry);
            UiFonts.DrawText(draw, ImGui.GetFontSize(), new Vector2(textX, origin.Y + Ui(31f)), ImGui.GetColorU32(MutedText), systemName + "  •  " + entry.Extension);
        }
        draw.PopClipRect();
        if (badgeWidth > 0f)
        {
            var badgeOrigin = origin + new Vector2(width - badgeWidth - Ui(7f), (size.Y - Ui(22f)) / 2f);
            draw.AddRectFilled(badgeOrigin, badgeOrigin + new Vector2(badgeWidth, Ui(22f)), ImGui.GetColorU32(Accent), Ui(11f));
            var labelSize = ImGui.CalcTextSize("Selected");
            UiFonts.DrawText(draw, ImGui.GetFontSize(), badgeOrigin + (new Vector2(badgeWidth, Ui(22f)) - labelSize) / 2f, ImGui.GetColorU32(ImGuiCol.Text), "Selected");
        }
        return clicked;
    }

    private static readonly FontAwesomeIcon[] LibraryIconChoices =
    [
        FontAwesomeIcon.Gamepad, FontAwesomeIcon.Star, FontAwesomeIcon.Heart,
        FontAwesomeIcon.Bolt, FontAwesomeIcon.Fire, FontAwesomeIcon.Moon,
        FontAwesomeIcon.Sun, FontAwesomeIcon.Ghost, FontAwesomeIcon.Dragon,
        FontAwesomeIcon.HatWizard, FontAwesomeIcon.ShieldAlt, FontAwesomeIcon.Crown,
        FontAwesomeIcon.Gem, FontAwesomeIcon.Rocket, FontAwesomeIcon.Car,
        FontAwesomeIcon.Futbol, FontAwesomeIcon.ChessKnight, FontAwesomeIcon.Dice,
        FontAwesomeIcon.PuzzlePiece, FontAwesomeIcon.Music, FontAwesomeIcon.Leaf,
        FontAwesomeIcon.Paw, FontAwesomeIcon.Fish, FontAwesomeIcon.Skull
    ];

    private static FontAwesomeIcon GetLibraryIcon(GameLibraryEntry entry) =>
        Enum.TryParse<FontAwesomeIcon>(entry.Icon, out var icon) && LibraryIconChoices.Contains(icon)
            ? icon : FontAwesomeIcon.Gamepad;

    private bool LibraryMenuItem(string label, FontAwesomeIcon icon)
    {
        var clicked = ImGui.MenuItem("      " + label);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        using (UiFonts.PushIcon())
        {
            var glyph = icon.ToIconString();
            var size = ImGui.CalcTextSize(glyph);
            UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), new Vector2(min.X + Ui(5f), min.Y + (max.Y - min.Y - size.Y) / 2f), ImGui.GetColorU32(ImGuiCol.Text), glyph);
        }
        return clicked;
    }

    private void DrawLibraryIconGallery(GameLibraryEntry entry)
    {
        ImGui.TextUnformatted(entry.Name);
        ImGui.TextColored(MutedText, "Choose an icon for this game.");
        ImGui.Spacing();
        var gap = Ui(10f);
        var columns = Math.Clamp((int)(ImGui.GetContentRegionAvail().X / Ui(66f)), 1, 8);
        var tile = MathF.Min(Ui(56f), (ImGui.GetContentRegionAvail().X - gap * (columns - 1)) / columns);
        for (var index = 0; index < LibraryIconChoices.Length; index++)
        {
            if (index % columns != 0) ImGui.SameLine(0f, gap);
            var icon = LibraryIconChoices[index];
            using var color = ImRaii.PushColor(ImGuiCol.Button, GetLibraryIcon(entry) == icon ? Accent : CardBg)
                .Push(ImGuiCol.ButtonHovered, AccentHover);
            bool clicked;
            using (UiFonts.PushIcon())
                clicked = ImGui.Button(icon.ToIconString() + "##libraryIcon" + index, new Vector2(tile));
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip(icon.ToString());
            if (clicked)
            {
                entry.Icon = icon.ToString();
                SaveGameLibraryMetadata();
                libraryAction = null;
            }
        }
        ImGui.Spacing();
        if (GameLayoutButton("Back to Library", FontAwesomeIcon.ArrowLeft, ImGui.GetContentRegionAvail().X)) libraryAction = null;
    }
}

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly record struct RadioStationChoice(
        string Name, string Description, string Url, FontAwesomeIcon Icon, bool Custom = false,
        string[]? Genres = null);

    private static readonly RadioStationChoice[] SuggestedRadioStations =
    [
        new("Truckers FM", "Chart hits, pop, rock and EDM",
            "https://radio.truckers.fm/", FontAwesomeIcon.Music, Genres: ["Pop", "Rock", "EDM"]),
        new("181 Power", "Current pop, Top 40 and dance hits",
            "https://listen.181fm.com/181-power_128k.mp3", FontAwesomeIcon.Music, Genres: ["Pop", "Dance"]),
        new("181 The Mix", "Pop, chart hits and adult contemporary",
            "https://listen.181fm.com/181-themix_128k.mp3", FontAwesomeIcon.Music, Genres: ["Pop"]),
        new("Pinguin Pop", "Non-stop modern and alternative pop",
            "https://13683.live.streamtheworld.com/SP_R2591862_SC", FontAwesomeIcon.Music, Genres: ["Pop", "Indie"]),
        new("WRTI Classical", "Classical music throughout the day",
            "https://wrti-live.streamguys1.com/classical-mp3", FontAwesomeIcon.Music, Genres: ["Classical"]),
        new("WRTI Jazz", "Classic and contemporary jazz",
            "https://wrti-live.streamguys1.com/jazz-mp3", FontAwesomeIcon.Music, Genres: ["Jazz"]),
        new("Chillsynth FM", "Chilled synthwave, chillwave and instrumental music",
            "https://stream.nightride.fm/chillsynth.mp3", FontAwesomeIcon.Music,
            Genres: ["Synthwave", "Chillout"]),
        new("Radio Paradise (Rock Mix)", "A curated mix of classic and modern rock",
            "http://stream.radioparadise.com/rock-128", FontAwesomeIcon.Music,
            Genres: ["Rock"]),
    ];

    private bool homeRadioSettingsOpen;
    private string homeRadioFirstDraft = string.Empty;
    private string homeRadioSecondDraft = string.Empty;

    private List<RadioStationChoice> GetHomeRadioChoices()
    {
        var choices = new List<RadioStationChoice>(SuggestedRadioStations);
        foreach (var station in djSavedStations)
        {
            if (string.IsNullOrWhiteSpace(station.Name) ||
                string.IsNullOrWhiteSpace(station.Url) ||
                choices.Any(choice => string.Equals(choice.Url, station.Url,
                    StringComparison.OrdinalIgnoreCase)))
                continue;

            choices.Add(new RadioStationChoice(station.Name, "Your saved station",
                station.Url, FontAwesomeIcon.Music, true));
        }
        return choices;
    }

    private static RadioStationChoice ResolveHomeRadioChoice(string? url, int fallbackIndex,
        IReadOnlyList<RadioStationChoice> choices)
    {
        foreach (var choice in choices)
        {
            if (string.Equals(choice.Url, url, StringComparison.OrdinalIgnoreCase))
                return choice;
        }
        return SuggestedRadioStations[fallbackIndex];
    }

    private void OpenHomeRadioSettings()
    {
        homeRadioFirstDraft = Plugin.Cfg.HomeRadioStation1Url;
        homeRadioSecondDraft = Plugin.Cfg.HomeRadioStation2Url;
        homeRadioSettingsOpen = true;
    }

    private void DrawHomeRadioSettingsOverlay()
    {
        if (!homeRadioSettingsOpen) return;

        var parentPosition = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var panelWidth = MathF.Min(Ui(620f), parentSize.X - Ui(24f));
        var panelHeight = MathF.Min(Ui(400f), parentSize.Y - Ui(24f));
        var panelPosition = parentPosition + new Vector2(
            (parentSize.X - panelWidth) * 0.5f,
            (parentSize.Y - panelHeight) * 0.5f);

        ImGui.SetNextWindowPos(parentPosition, ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        const ImGuiWindowFlags overlayFlags = ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##homeRadioSettingsOverlay", overlayFlags))
        {
            ImGui.End();
            return;
        }

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(parentPosition, parentPosition + parentSize,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.54f)));
        ImGui.SetCursorScreenPos(panelPosition);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(24f, 20f)).Push(ImGuiStyleVar.ItemSpacing,UiVec(8f,6f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg,
                   CardBg)
            .Push(ImGuiCol.Border,
                new Vector4(Accent.X, Accent.Y, Accent.Z, 0.82f)))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##homeRadioSettingsPanel",
                   new Vector2(panelWidth, panelHeight), true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (panel)
                DrawHomeRadioSettingsContent(panelPosition, panelWidth, panelHeight);
        }
        ImGui.End();
    }

    private void DrawHomeRadioSettingsContent(Vector2 origin, float width, float height)
    {
        var draw = ImGui.GetWindowDrawList();
        ImGui.SetCursorScreenPos(origin + UiVec(24f, 18f));
        using var uiFontScale5804=UiFonts.PushRelative(1.16f);
        ImGui.TextColored(Hex(0xE2E7EF), "Live Radio settings");
        uiFontScale5804.Dispose();
        ImGui.SetCursorScreenPos(origin + UiVec(24f, 47f));
        DrawSocialHelper("Choose the two stations shown on your homepage.");

        var closeOrigin = origin + new Vector2(width - Ui(43f), Ui(19f));
        ImGui.SetCursorScreenPos(closeOrigin);
        if (ImGui.InvisibleButton("##closeHomeRadioSettings", UiVec(24f, 24f)))
            homeRadioSettingsOpen = false;
        UiFonts.DrawIcon(draw, ImGui.GetFontSize(), closeOrigin + UiVec(5f, 4f), ImGui.GetColorU32(ImGui.IsItemHovered() ? AccentHover : MutedText), FontAwesomeIcon.Times.ToIconString());
        draw.AddLine(origin + UiVec(18f, 78f),
            origin + new Vector2(width - Ui(18f), Ui(78f)),
            ImGui.GetColorU32(BorderSubtle));

        var choices = GetHomeRadioChoices();
        ImGui.SetCursorScreenPos(origin + UiVec(24f, 96f));
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
        using (var body = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##homeRadioSettingsBody",
                   new Vector2(width - Ui(48f), height - Ui(164f)), false)))
        {
            if (body)
            {
                DrawHomeRadioSettingsSlot("First station", 0, choices,
                    ref homeRadioFirstDraft);
                ImGui.Dummy(UiVec(0f, 8f));
                DrawHomeRadioSettingsSlot("Second station", 1, choices,
                    ref homeRadioSecondDraft);
            }
        }

        var buttonWidth = (width - Ui(58f)) * 0.5f;
        ImGui.SetCursorScreenPos(origin + new Vector2(Ui(24f), height - Ui(59f)));
        DrawDjActionButton("##cancelHomeRadioSettings", FontAwesomeIcon.Times,
            "Cancel", new Vector2(buttonWidth, Ui(38f)), false,
            () => homeRadioSettingsOpen = false);
        ImGui.SameLine(0f, Ui(10f));
        DrawDjActionButton("##saveHomeRadioSettings", FontAwesomeIcon.Check,
            "Save stations", new Vector2(buttonWidth, Ui(38f)), false, () =>
            {
                Plugin.Cfg.HomeRadioStation1Url =
                    ResolveHomeRadioChoice(homeRadioFirstDraft, 0, choices).Url;
                Plugin.Cfg.HomeRadioStation2Url =
                    ResolveHomeRadioChoice(homeRadioSecondDraft, 1, choices).Url;
                Plugin.Cfg.Save();
                homeRadioSettingsOpen = false;
            }, true);
    }

    private void DrawHomeRadioSettingsSlot(string label, int index,
        IReadOnlyList<RadioStationChoice> choices, ref string draftUrl)
    {
        ImGui.PushID(index);
        var selected = ResolveHomeRadioChoice(draftUrl, index, choices);
        ImGui.TextColored(Vector4.One, label);
        ImGui.Dummy(UiVec(0f, 5f));
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7f))
            .Push(ImGuiStyleVar.FramePadding, UiVec(10f, 8f)))
        using (ImRaii.PushColor(ImGuiCol.FrameBg,
                   FrameBg))
        {
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##station", selected.Name)))
            {
                ImGui.TextColored(AccentHover, "SUGGESTED STATIONS");
                for (var i = 0; i < choices.Count; i++)
                {
                    if (i == SuggestedRadioStations.Length)
                    {
                        ImGui.Separator();
                        ImGui.TextColored(AccentHover, "YOUR STATIONS");
                    }
                    var choice = choices[i];
                    if (ImGui.Selectable($"{choice.Name}##{i}",
                            string.Equals(choice.Url, selected.Url,
                                StringComparison.OrdinalIgnoreCase)))
                        draftUrl = choice.Url;
                }
                ImGui.EndCombo();
            }
        }
        ImGui.Dummy(UiVec(0f, 4f));
        DrawSocialHelper(selected.Description);
        ImGui.PopID();
    }
}

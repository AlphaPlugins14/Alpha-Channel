using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawChocoRaceSetupPage()
    {
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(10f, 12f));
        DrawChocoRaceSetupHero();

        var available = ImGui.GetContentRegionAvail().X;
        var gap = Ui(12f);
        var stacked = available < Ui(760f);
        var standardWidth = stacked ? available : (available - gap) * 0.40f;
        var customWidth = stacked ? available : available - gap - standardWidth;
        DrawChocoRaceStandardCard(standardWidth);
        if (!stacked) ImGui.SameLine(0f, gap);
        DrawChocoRaceCustomCard(customWidth);
        DrawChocoRaceSetupInfo();
    }

    private void DrawChocoRaceSetupHero() => DrawArcadeReviewHero("choco-race", "Choco RaceBets", "Ready, set, Kweh!", "Choose standard rules or customise the race before spawning it on every TV.", "Automatic races", false, ActivityPreviewKind.RacePreview);


    private void DrawChocoRaceStandardCard(float width)
    {
        const float height = 410f;
        DrawQuizCardBegin("##chocoStandard", width, Ui(height));
        var origin = ImGui.GetWindowPos();
        DrawPictionarySetupBadge("Standard play", true);
        ImGui.Dummy(UiVec(0f, 8f));
        DrawPictionarySetupCardHeading(FontAwesomeIcon.Circle, "Standard Choco RaceBets",
            "A classic race with automatic rounds.");
        ImGui.Dummy(UiVec(0f, 17f));
        DrawActivityFeature("12 second bet timer");
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Time from first bet to start of race");
        DrawActivityFeature("1,000 starting chips");
        DrawActivityFeature("Refill chips when empty");
        DrawActivityFeature("Payout: 1st Place only");
        ImGui.SetCursorScreenPos(new Vector2(origin.X + Ui(18f), origin.Y + Ui(height - 54f)));
        DrawActivityStartButtons("chocoStandard", () => RequestChocoRaceStart(true),
            () => RequestChocoRaceStart(true, true), CanRequestChocoRaceStart());
        ImGui.EndChild();
    }

    private void DrawChocoRaceCustomCard(float width)
    {
        const float height = 446f;
        DrawQuizCardBegin("##chocoCustom", width, Ui(height), accentBorder: true);
        var origin = ImGui.GetWindowPos();
        DrawPictionarySetupBadge("Custom play", false);
        ImGui.Dummy(UiVec(0f, 8f));
        DrawPictionarySetupCardHeading(FontAwesomeIcon.Cog, "Custom Choco RaceBets",
            "Set the betting pace, balance, and payout rules.");
        ImGui.Dummy(UiVec(0f, 12f));

        var formOrigin = ImGui.GetCursorScreenPos();
        var formWidth = ImGui.GetContentRegionAvail().X;
        var columnGap = Ui(18f);
        var columnWidth = (formWidth - columnGap) * 0.5f;
        using (UiDisabled.Scope(stream.Mode == StreamMode.Viewing))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7f)))
        {
            DrawQuizGridStepper(formOrigin, columnWidth, "Bet timer", "chocoTimer",
                ref chocoRaceCustomBettingSeconds, 5, 60, 1, value => $"{value} seconds");
            DrawQuizGridStepper(new Vector2(formOrigin.X + columnWidth + columnGap, formOrigin.Y), columnWidth,
                "Starting chips", "chocoChips", ref chocoRaceCustomStartingChips, 100, 10000, 100,
                value => value.ToString());

            ImGui.SetCursorScreenPos(formOrigin + UiVec(0f, 76f));
            ImGui.Checkbox("Refill chips when a player reaches zero##chocoRefill", ref chocoRaceCustomRefillEnabled);
            ImGui.SetCursorScreenPos(formOrigin + UiVec(0f, 106f));
            ImGui.TextColored(MutedText, chocoRaceCustomRefillEnabled
                ? $"Players refill to {chocoRaceCustomStartingChips:N0} chips when they cannot bet."
                : "Players keep their remaining chips, even if they cannot bet.");

            ImGui.SetCursorScreenPos(formOrigin + UiVec(0f, 144f));
            ImGui.TextColored(MutedText, "Payout mode");
            ImGui.SetCursorScreenPos(formOrigin + UiVec(0f, 168f));
            using (ImRaii.PushColor(ImGuiCol.Button,
                       chocoRaceCustomTopThreePays ? CardBg : Accent))
                if (ImGui.Button("1st Place Only##chocoFirstOnly", new Vector2(columnWidth, Ui(34f))))
                    chocoRaceCustomTopThreePays = false;
            ImGui.SameLine(0f, columnGap);
            using (ImRaii.PushColor(ImGuiCol.Button,
                       chocoRaceCustomTopThreePays ? Accent : CardBg))
                if (ImGui.Button("Top 3 Pays##chocoTopThree", new Vector2(columnWidth, Ui(34f))))
                    chocoRaceCustomTopThreePays = true;
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Reduced payout for 2nd and 3rd place");
            ImGui.SetCursorScreenPos(formOrigin + UiVec(0f, 211f));
            ImGui.TextColored(MutedText, chocoRaceCustomTopThreePays
                ? "1st: full return   2nd: 30%   3rd: 15%"
                : "Only the first-place choco pays out.");
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X + Ui(18f), origin.Y + Ui(height - 54f)));
        DrawActivityStartButtons("chocoCustom", () => RequestChocoRaceStart(false),
            () => RequestChocoRaceStart(false, true), CanRequestChocoRaceStart());
        ImGui.EndChild();
    }

    private void DrawChocoRaceSetupInfo()
    {
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(9f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f)).Push(ImGuiStyleVar.WindowPadding, UiVec(18f, 13f));
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##chocoSetupInfo", new Vector2(-1f, Ui(60f)), true,
                ImGuiWindowFlags.NoScrollbar)))
        {
            var origin = ImGui.GetCursorScreenPos();
            UiFonts.DrawIcon(ImGui.GetWindowDrawList(), ImGui.GetFontSize() * 1.2f, origin + UiVec(0f, 2f), ImGui.GetColorU32(new Vector4(0.72f, 0.76f, 0.90f, 1f)), FontAwesomeIcon.Tv.ToIconString());
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Ui(40f));
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Ui(4f));
            ImGui.TextColored(MutedText,
                "The first placed bet starts the timer. Everyone in the party sees the same race and results.");
        }
        ImGui.EndChild();
    }

    private bool CanRequestChocoRaceStart() =>
        stream.Mode != StreamMode.Viewing && ActiveActivity == ActiveActivityKind.None;

    private void RequestChocoRaceStart(bool standardPlay, bool inParty = false)
    {
        if (!CanRequestChocoRaceStart()) return;
        pendingChocoRaceBettingSeconds = standardPlay ? ChocoRaceBettingSeconds :
            Math.Clamp(chocoRaceCustomBettingSeconds, 5, 60);
        pendingChocoRaceStartingChips = standardPlay ? ChocoRaceStartingChips :
            Math.Clamp(chocoRaceCustomStartingChips, 100, 10000);
        pendingChocoRaceRefillEnabled = standardPlay || chocoRaceCustomRefillEnabled;
        pendingChocoRaceTopThreePays = !standardPlay && chocoRaceCustomTopThreePays;

        if (!inParty || stream.Mode == StreamMode.Hosting)
            StartChocoRace();
        else
        {
            pendingWatchPartyMediaKind = PendingWatchPartyMediaKind.ChocoRace;
            OpenPendingWatchPartyCreation();
        }
    }
}

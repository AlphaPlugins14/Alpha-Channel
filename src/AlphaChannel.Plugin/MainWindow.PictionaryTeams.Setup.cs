using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool CanStartPictionaryTeamsFromSetup => stream.Mode == StreamMode.Hosting &&
        CurrentSession is not null && CanLaunchActivity(ActiveActivityKind.PictionaryTeams) &&
        stream.Roster.Length + 1 >= PictionaryTeamsPreviewRules.MinimumPlayers;

    private string PictionaryTeamsStartNotice => stream.Mode == StreamMode.None
        ? "Host a Watch Party and invite at least three friends to play."
        : stream.Mode == StreamMode.Viewing
            ? "The Watch Party host chooses the rules and starts the team lobby."
            : CurrentSession is null
                ? "Sign in before starting Pictionary Teams."
                : !CanLaunchActivity(ActiveActivityKind.PictionaryTeams)
                    ? "End the current activity before starting Pictionary Teams."
                    : stream.Roster.Length + 1 < PictionaryTeamsPreviewRules.MinimumPlayers
                        ? $"At least {PictionaryTeamsPreviewRules.MinimumPlayers} players are needed. Your Watch Party currently has {stream.Roster.Length + 1}."
                        : "Ready to start the team lobby. Everyone picks a team, then the host confirms the teams.";

    private void DrawPictionaryTeamsSetupPage()
    {
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(10f, 12f));
        DrawPictionaryTeamsSetupHero();
        var available = ImGui.GetContentRegionAvail().X;
        var gap = Ui(12f);
        var stacked = available < Ui(760f);
        var cardWidth = stacked ? available : (available - gap) * .5f;
        DrawPictionaryTeamsRulesCard(cardWidth, custom: false);
        if (!stacked) ImGui.SameLine(0f, gap);
        DrawPictionaryTeamsRulesCard(cardWidth, custom: true);
        DrawPictionaryTeamsSetupGuide();
        ImGui.Dummy(UiVec(0f, 12f));
    }

    private void DrawPictionaryTeamsSetupHero()
    {
        var width = ImGui.GetContentRegionAvail().X;
        var showArt = width >= Ui(850f);
        DrawQuizCardBegin("##teamsSetupHero", width, Ui(160f));
        var origin = ImGui.GetWindowPos();
        var innerWidth = ImGui.GetContentRegionAvail().X;
        if (showArt) ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + innerWidth * .62f);
        else ImGui.PushTextWrapPos();
        DrawArcadeSetupHeading("Two teams. One drawing race.", showArt ? 1.65f : 1.35f, "pictionary-teams", "Pictionary Teams", true,
            origin.X + width - (showArt ? Math.Min(Ui(280f), innerWidth * .30f) + Ui(32f) : Ui(18f)));
        ImGui.TextColored(MutedText,
            "Red and blue draw at the same time. Guess your team's word first to win the round.");
        ImGui.PopTextWrapPos();
        ImGui.SetCursorScreenPos(origin + UiVec(18f, 112f));
        DrawPictionarySetupBadge($"4+ PLAYERS • WATCH PARTY REQUIRED", true);
        if (showArt)
        {
            var artWidth = Math.Min(Ui(280f), innerWidth * .30f);
            ImGui.SetCursorScreenPos(origin + new Vector2(width - artWidth - Ui(20f), Ui(16f)));
            DrawActivityPreview("teamsSetupHeroArt", ActivityPreviewKind.PictionaryTeams,
                artWidth, 126f, 14f, 8f);
        }
        ImGui.EndChild();
    }

    private void DrawPictionaryTeamsRulesCard(float width, bool custom)
    {
        var height = custom ? 350f : 390f;
        DrawQuizCardBegin(custom ? "##teamsCustomRules" : "##teamsStandardRules", width, Ui(height),
            accentBorder: custom);
        var origin = ImGui.GetWindowPos();
        DrawPictionarySetupBadge(custom ? "Custom play" : "Recommended", !custom);
        ImGui.Dummy(UiVec(0f, 6f));
        DrawPictionarySetupCardHeading(custom ? FontAwesomeIcon.Cog : FontAwesomeIcon.Users,
            custom ? "Custom rules" : "Standard rules",
            custom ? "Choose the game length, timer and team reward." : "A balanced race for red and blue teams.");
        ImGui.Dummy(UiVec(0f, 12f));
        if (custom)
        {
            using (UiDisabled.Scope(stream.Mode == StreamMode.Viewing ||
                !CanLaunchActivity(ActiveActivityKind.PictionaryTeams)))
            {
                DrawPictionaryRuleStepper("Rounds", "teamsRounds", ref teamsCustomRounds,
                    3, 20, 1, value => value.ToString());
                DrawPictionaryRuleStepper("Round timer", "teamsTimer", ref teamsCustomSeconds,
                    30, 120, 15, value => $"{value} seconds");
                DrawPictionaryRuleStepper("Team reward", "teamsPoints", ref teamsCustomPoints,
                    1, 10, 1, PictionaryPointsLabel);
            }
            ImGui.PushStyleColor(ImGuiCol.Text, MutedText);
            DrawArcadeSetupParagraph("The first correct answer wins points for that team.");
            ImGui.PopStyleColor();
        }
        else
        {
            DrawActivityFeature($"{PictionaryStandardRoundCount} rounds");
            DrawActivityFeature($"{PictionaryStandardDrawingSeconds} second drawing timer");
            DrawActivityFeature($"{PictionaryStandardFirstGuessPoints} points for the winning team");
            DrawActivityFeature("First correct answer ends the round");
        }
        ImGui.SetCursorScreenPos(origin + UiVec(18f, height - 60f));
        if (DrawPictionarySetupStartButton(custom ? "teamsCustom" : "teamsStandard",
                custom ? "Start Custom Game" : "Start Standard Game", CanStartPictionaryTeamsFromSetup))
        {
            teamsCustomRules = custom;
            StartPictionaryTeams();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiFonts.SetTooltip(PictionaryTeamsStartNotice);
        ImGui.EndChild();
    }

    private void DrawPictionaryTeamsSetupGuide()
    {
        var width = ImGui.GetContentRegionAvail().X;
        string[] steps =
        [
            "1. Pick red or blue in the lobby. Each team needs at least two players, and everyone must pick a team before the host confirms.",
            "2. Each team gets a drawer. Drawers choose one of three secret words and draw on their own half of the TV or on this page.",
            "3. Teammates guess on the TV or in the plugin. The first correct answer ends the round and earns the team reward; a timeout awards no points.",
            "4. New drawers are selected for the next round. The highest team score wins; equal scores finish as a tie.",
        ];
        var wrapWidth = Math.Max(Ui(100f), width - Ui(36f));
        var guideHeight = Ui(40f) + ImGui.GetTextLineHeight() +
            steps.Sum(text => ImGui.CalcTextSize(text, false, wrapWidth).Y) +
            ImGui.GetStyle().ItemSpacing.Y * (steps.Length + 1);
        DrawQuizCardBegin("##teamsSetupGuide", width, guideHeight);
        ImGui.TextColored(AccentHover, "How to play");
        foreach (var text in steps) DrawArcadeSetupParagraph(text);
        ImGui.EndChild();
        const string lobbyNote = "Teams can have different sizes. Starting a game opens the team lobby before drawing begins.";
        var notice = PictionaryTeamsStartNotice;
        var noticeHeight = Ui(40f) + ImGui.CalcTextSize(notice, false, wrapWidth).Y +
            ImGui.CalcTextSize(lobbyNote, false, wrapWidth).Y + ImGui.GetStyle().ItemSpacing.Y * 2;
        DrawQuizCardBegin("##teamsSetupRequirements", width, noticeHeight);
        ImGui.PushStyleColor(ImGuiCol.Text, CanStartPictionaryTeamsFromSetup ? Good : MutedText);
        DrawArcadeSetupParagraph(notice);
        ImGui.PopStyleColor();
        DrawArcadeSetupParagraph(lobbyNote);
        ImGui.EndChild();
    }
}

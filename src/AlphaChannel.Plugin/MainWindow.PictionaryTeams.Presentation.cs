using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawTeamsRoundStatus()
    {
        var width = ImGui.GetContentRegionAvail().X;
        DrawQuizCardBegin("##teamsRoundStatus", width, Ui(78f));
        var origin = ImGui.GetWindowPos();
        var draw = ImGui.GetWindowDrawList();
        var label = teamsPhase == TeamsPhase.Finished ? "FINAL SCORES" :
            teamsPhase == TeamsPhase.Lobby ? "CHOOSE YOUR TEAM" : $"ROUND {teamsRound} OF {teamsRounds}";
        DrawPictionaryPill(draw, origin + UiVec(16f, 19f), label, Accent, Ui(160f));
        var scores = $"RED  {teamsRedScore}    :    {teamsBlueScore}  BLUE";
        draw.AddText(origin + UiVec(192f, 29f), ImGui.GetColorU32(Vector4.One), scores);
        var timerMin = origin + new Vector2(width - Ui(190f), Ui(17f));
        draw.AddRectFilled(timerMin, timerMin + UiVec(120f, 44f), ImGui.GetColorU32(Hex(0x1C193B)), Ui(22f));
        draw.AddRect(timerMin, timerMin + UiVec(120f, 44f), ImGui.GetColorU32(AccentHover), Ui(22f));
        DrawPictionaryIcon(draw, FontAwesomeIcon.Clock, timerMin + UiVec(12f, 12f), 1.05f, AccentHover);
        draw.AddText(timerMin + UiVec(41f, 11f), ImGui.GetColorU32(Vector4.One),
            teamsPhase is TeamsPhase.Lobby or TeamsPhase.Finished ? "--:--" : FormatPictionaryTimer(TeamsSecondsLeft));
        ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(58f), Ui(17f)));
        if (ImGui.InvisibleButton("##teamsGameControls", UiVec(42f, 44f)))
            ImGui.OpenPopup("##teamsGameMenu");
        DrawPictionaryIcon(draw, FontAwesomeIcon.EllipsisH, origin + new Vector2(width - Ui(47f), Ui(30f)), 1f, MutedText);
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup("##teamsGameMenu")))
        {
            if (stream.Mode == StreamMode.Hosting)
            {
                if (teamsPhase == TeamsPhase.Drawing && ImGui.MenuItem("Skip round")) EndTeamsRound(-1, string.Empty);
                if (ImGui.MenuItem("End game")) StopPictionaryTeams();
            }
            else ImGui.TextDisabled("Host controls appear here");
            ImGui.EndPopup();
        }
        ImGui.EndChild();
    }

    private void DrawTeamsSidePanel(float width, float height)
    {
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f)).Push(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##teamsSidePanel", new Vector2(width, height), true, ImGuiWindowFlags.NoScrollbar)))
        {
            var origin = ImGui.GetWindowPos();
            var draw = ImGui.GetWindowDrawList();
            DrawPictionaryIcon(draw, FontAwesomeIcon.Trophy, origin + UiVec(14f, 17f), .92f, AccentHover);
            draw.AddText(origin + UiVec(38f, 15f), ImGui.GetColorU32(Vector4.One), "SCOREBOARD");
            draw.AddLine(origin + UiVec(12f, 43f), origin + new Vector2(width - Ui(12f), Ui(43f)), ImGui.GetColorU32(BorderSubtle));
            ImGui.SetCursorScreenPos(origin + UiVec(12f, 54f));
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##teamsScores", new Vector2(width - Ui(24f), height - Ui(218f)), false)))
            {
                for (var team = 0; team < 2; team++)
                {
                    var accent = team == 0 ? Hex(0xFF8A91) : Hex(0x81C7FF);
                    ImGui.TextColored(accent, $"{(team == 0 ? "RED TEAM" : "BLUE TEAM")}   {(team == 0 ? teamsRedScore : teamsBlueScore)} points");
                    var rank = 0;
                    foreach (var member in teamsMembers.Values.Where(m => m.Team == team).OrderByDescending(m => m.Correct).ThenBy(m => m.Name))
                    {
                        var at = ImGui.GetCursorScreenPos();
                        DrawPictionaryScoreRow(ImGui.GetWindowDrawList(), at, width - Ui(36f),
                            ++rank, string.IsNullOrEmpty(member.Name) ? "Player" : member.Name, member.Correct.ToString(), PictionaryAvatarColour(member.Id),
                            teamsPhase == TeamsPhase.Drawing && member.Id == teamsDrawers[team],
                            teamsPhase == TeamsPhase.Summary && member.Id == teamsWinningGuesser);
                        ImGui.SetCursorScreenPos(at);
                        ImGui.Dummy(new Vector2(width - Ui(36f), Ui(42f)));
                    }
                    ImGui.Separator();
                }
            }
            ImGui.EndChild();
            DrawPictionaryAnswerComposer(draw, origin + new Vector2(Ui(12f), height - Ui(140f)),
                origin + new Vector2(width - Ui(12f), height - Ui(12f)), teams: true);
            ImGui.SetCursorScreenPos(origin);
            ImGui.Dummy(new Vector2(width, height));
        }
        ImGui.EndChild();
    }
}

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool CanStartBingoFromSetup => stream.Mode != StreamMode.Viewing &&
        CurrentSession is not null && CanLaunchActivity(ActiveActivityKind.Bingo);

    private void RestartBingoGame()
    {
        if (!IsLocalActivityHost || bingoPhase != BingoPhase.Finished) return;
        // Keep the selected rules, but reset cards, scores and the TV session together.
        StopBingoGame();
        StartBingoGame();
    }

    private void DrawBingoSetupPage()
    {
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(10f, 12f));
        var width = ImGui.GetContentRegionAvail().X;
        DrawQuizCardBegin("##bingoSetupHero", width, Ui(150f));
        var origin = ImGui.GetWindowPos();
        var showArt = width >= Ui(850f);
        ImGui.PushTextWrapPos(showArt ? ImGui.GetCursorPosX() + width * .62f : 0f);
        using var uiFontScale1055=UiFonts.PushRelative(1.5f);
        ImGui.TextColored(Vector4.One, "New round. New pattern. Bingo!");
        uiFontScale1055.Dispose();
        ImGui.TextColored(MutedText, "Build your card and race to complete the pattern. Play solo or share the TV with friends.");
        ImGui.PopTextWrapPos();
        ImGui.SetCursorScreenPos(origin + UiVec(18f, 108f));
        DrawPictionarySetupBadge("1+ PLAYERS • SOLO OR WATCH PARTY", true);
        if (showArt)
        {
            var artWidth = Math.Min(Ui(270f), width * .30f);
            ImGui.SetCursorScreenPos(origin + new Vector2(width - artWidth - Ui(20f), Ui(16f)));
            DrawActivityPreview("bingoSetupArt", ActivityPreviewKind.Bingo, artWidth, 118f, 14f, 8f);
        }
        ImGui.EndChild();
        var stacked = width < Ui(760f);
        var cardWidth = stacked ? width : (width - Ui(12f)) * .5f;
        DrawBingoSetupRulesCard(cardWidth, false);
        if (!stacked) ImGui.SameLine(0f, Ui(12f));
        DrawBingoSetupRulesCard(cardWidth, true);

        string[] steps =
        [
            "1. Randomise your card or edit its numbers, then lock it. The first locked card starts a 30-second countdown. Once everyone locks a card, it drops to five seconds.",
            "2. Balls are called automatically. Automatic mode marks matching numbers; in manual mode, click called numbers to mark them. The centre is free. The pattern preview shows the current round's target; line patterns allow any row, column or diagonal.",
            "3. In automatic mode, completing the pattern wins the round; matching winners share the point. In manual mode, complete the marked pattern and press Bingo to claim one point.",
            "4. Each new round gives you a fresh card to lock. After the final round, the highest score wins; equal scores share the result. The host can play again or end the game.",
        ];
        var wrapWidth = Math.Max(Ui(100f), width - Ui(36f));
        var height = Ui(40f) + ImGui.GetTextLineHeight() + steps.Sum(t => ImGui.CalcTextSize(t, false, wrapWidth).Y)
            + ImGui.GetStyle().ItemSpacing.Y * (steps.Length + 1);
        DrawQuizCardBegin("##bingoSetupGuide", width, height);
        ImGui.TextColored(AccentHover, "How to play");
        foreach (var step in steps) DrawArcadeSetupParagraph(step);
        ImGui.EndChild();

        var patternsHeight = Ui(40f) + ImGui.GetTextLineHeight() + BingoRules.Rotation.Sum(p =>
            ImGui.CalcTextSize(BingoRules.Name(p) + ": " + BingoRules.Description(p), false, wrapWidth).Y)
            + ImGui.GetStyle().ItemSpacing.Y * 8;
        DrawQuizCardBegin("##bingoPatternsGuide", width, patternsHeight);
        ImGui.TextColored(AccentHover, "Winning patterns — standard round order");
        foreach (var pattern in BingoRules.Rotation)
            DrawArcadeSetupParagraph(BingoRules.Name(pattern) + ": " + BingoRules.Description(pattern));
        ImGui.EndChild();
        if (!CanStartBingoFromSetup)
            DrawArcadeSetupParagraph(stream.Mode == StreamMode.Viewing ? "The Watch Party host chooses the rules and starts Bingo."
                : CurrentSession is null ? "Sign in to start Bingo." : "End the current activity before starting Bingo.");
    }

    private void DrawBingoSetupRulesCard(float width, bool custom)
    {
        var height = custom ? 420f : 390f;
        DrawQuizCardBegin(custom ? "##bingoCustomSetup" : "##bingoStandardSetup", width, Ui(height), custom);
        var origin = ImGui.GetWindowPos();
        DrawPictionarySetupBadge(custom ? "Custom play" : "Recommended", !custom);
        ImGui.Dummy(UiVec(0f, 6f));
        DrawPictionarySetupCardHeading(custom ? FontAwesomeIcon.Cog : FontAwesomeIcon.Th,
            custom ? "Custom rules" : "Standard rules",
            custom ? "Choose patterns, round count and calling speed." : "Seven patterns, from one line to a full house.");
        ImGui.Dummy(UiVec(0f, 8f));
        if (custom)
        {
            using (UiDisabled.Scope(!CanStartBingoFromSetup))
            {
                DrawPictionaryRuleStepper("Rounds", "bingoRounds", ref bingoCustomRounds, 1, 14, 1, n => n.ToString());
                DrawPictionaryRuleStepper("Call interval", "bingoSpeed", ref bingoCustomDrawSeconds, 2, 10, 1, n => $"{n} seconds");
                ImGui.TextColored(MutedText, "Winning pattern");
                ImGui.SetNextItemWidth(-1);
                string[] options = ["Mixed patterns (standard rotation)", .. BingoRules.Rotation.Select(BingoRules.Name)];
                ImGui.Combo("##bingoPattern", ref bingoCustomPattern, options, options.Length);
                ImGui.TextColored(MutedText, "Play mode");
                var mode = bingoDraftManual ? 1 : 0;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.Combo("##bingoPlayMode", ref mode, new[] { "Automatic", "Manual" }, 2))
                    bingoDraftManual = mode == 1;
            }
        }
        else
        {
            DrawActivityFeature("7 rounds • a different pattern each round");
            DrawActivityFeature("A new ball every 5 seconds");
            DrawActivityFeature("30 seconds to lock cards");
            DrawActivityFeature("1 point for each round win");
        }
        ImGui.SetCursorScreenPos(origin + UiVec(18f, height - 62f));
        DrawActivityStartButtons(custom ? "bingoCustom" : "bingoStandard", () =>
        {
            bingoCustomRules = custom;
            StartBingoGame();
        }, () =>
        {
            bingoCustomRules = custom;
            if (stream.Mode == StreamMode.Hosting) StartBingoGame();
            else
            {
                pendingWatchPartyMediaKind = PendingWatchPartyMediaKind.Bingo;
                OpenPendingWatchPartyCreation();
            }
        }, CanStartBingoFromSetup, 44f, ImGui.GetContentRegionAvail().X);
        ImGui.EndChild();
    }

    private void DrawBingoFinalResults(byte[] pixels, int width, int height, int hover)
    {
        var standings = bingoStandings.Values.OrderByDescending(s => s.Wins)
            .ThenBy(s => s.Name, StringComparer.Ordinal).ToArray();
        var best = standings.FirstOrDefault()?.Wins ?? 0;
        var leaders = standings.Where(s => s.Wins == best).ToArray();
        var title = best == 0 ? "GAME COMPLETE" : leaders.Length == 1
            ? BingoShortName(leaders[0].Name) + " WINS!" : "SHARED VICTORY!";
        FillQuizTvPanel(pixels, width, height, 300, 155, 680, 420, 0x171325, 0xFFCB54);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 180, title, 4, 0xFFCB54);
        DrawPictionaryPixelTextCentered(pixels, width, height, 640, 226,
            $"{bingoRoundCount} ROUNDS - FINAL SCORES", 2, 0xBAC1D5);
        // Rotate leaderboard pages so every player remains visible on the TV.
        var pages = Math.Max(1, (standings.Length + 5) / 6);
        var offset = ((int)(DateTime.UtcNow.TimeOfDay.TotalSeconds / 8) % pages) * 6;
        foreach (var (standing, index) in standings.Skip(offset).Take(6).Select((s, i) => (s, i)))
        {
            var rank = 1 + standings.Count(s => s.Wins > standing.Wins);
            DrawUsernamePixelText(pixels, width, height, 340, 270 + index * 33,
                $"{rank}. {BingoShortName(standing.Name)}", 3, standing.Wins == best && best > 0 ? 0xFFCB54u : 0xF5F5FCu,standing.PlayerId);
            DrawPictionaryPixelText(pixels, width, height, 823, 270 + index * 33,
                standing.Wins.ToString(), 3, 0xF5F5FC);
        }
        if (IsLocalActivityHost)
            BingoButton(pixels, width, height, 492, 507, 296, 52, "PLAY AGAIN", hover == 300, false, true);
        else
            DrawPictionaryPixelTextCentered(pixels, width, height, 640, 525, "WAITING FOR THE HOST", 2, 0xBAC1D5);
    }
}

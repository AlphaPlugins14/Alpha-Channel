using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawPokerSetupPage()
    {
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(10f, 12f));
        var width = ImGui.GetContentRegionAvail().X;
        DrawQuizCardBegin("##pokerSetupHero", width, Ui(150f));
        var origin = ImGui.GetWindowPos();
        var showArt = width >= Ui(850f);
        ImGui.PushTextWrapPos(showArt ? ImGui.GetCursorPosX() + width * .62f : 0f);
        DrawArcadeSetupHeading("Take a seat. Play your hand.", 1.5f, "poker", "Poker", false,
            origin.X + width - (showArt ? Math.Min(Ui(270f), width * .30f) + Ui(32f) : Ui(18f)));
        ImGui.TextColored(MutedText, "Texas Hold'em with private cards, a shared table and an automated dealer. Play friends or practise against the house bot.");
        ImGui.PopTextWrapPos();
        ImGui.SetCursorScreenPos(origin + UiVec(18f, 108f));
        DrawPictionarySetupBadge("1–16 PLAYERS • PLAY CHIPS ONLY", true);
        if (showArt)
        {
            var artWidth = Math.Min(Ui(270f), width * .30f);
            ImGui.SetCursorScreenPos(origin + new Vector2(width - artWidth - Ui(20f), Ui(16f)));
            DrawActivityPreview("pokerSetupArt", ActivityPreviewKind.Poker, artWidth, 118f, 14f, 8f);
        }
        ImGui.EndChild();
        var stacked = width < Ui(760f);
        var cardWidth = stacked ? width : (width - Ui(12f)) * .5f;
        DrawPokerRulesCard(cardWidth, false);
        if (!stacked) ImGui.SameLine(0f, Ui(12f));
        DrawPokerRulesCard(cardWidth, true);
        string[] steps =
        [
            "1. Click an open seat to join. The host waits for friends to sit, then selects Deal hand. One seated player faces a house bot when enabled; two or more human players play each other without a bot.",
            "2. Each player receives two private cards. Combine them with the five shared board cards to make your best five-card hand. Friends' hole cards stay hidden until showdown; folded hands remain hidden.",
            "3. Small and big blinds rotate each hand. Betting runs preflop, then after the three-card flop, one-card turn and one-card river. Choose Fold, Check/Call, Bet/Raise or All in when it is your turn. The raise button uses the current minimum raise.",
            "4. The strongest hand wins the pot. Equal hands split it; all-ins can create side pots. On a timeout you check if nothing is owed, otherwise fold. A disconnected player folds; arrivals during a hand wait for the next deal.",
            "5. Results show for seven seconds, then another hand deals after five seconds. The host can pause between hands or end the table. The session ledger tracks hands, pot wins, chip gain and free refills; leaving and rejoining retains your stack.",
            "6. A stack below the big blind is refilled before the next hand. Refills do not count as profit. Play chips are temporary and never spend or award real gil, MGP or broadcast allowance.",
        ];
        DrawPokerGuide("##pokerHowToPlay", "How to play", steps);
        DrawPokerGuide("##pokerHandRanks", "Hand ranks — strongest first",
        ["Royal flush / straight flush → four of a kind → full house → flush → straight → three of a kind → two pair → one pair → high card.",
         "Aces can be high or low in a straight. Kickers break ties; equal best five-card hands share the pot. Hover any visible card to see its rank and suit."]);
        if (stream.Mode == StreamMode.Viewing) DrawArcadeSetupParagraph("The Watch Party host chooses the rules and opens the table.");
        else if (CurrentSession is null) DrawArcadeSetupParagraph("Sign in to start Poker.");
        else if (!CanLaunchActivity(ActiveActivityKind.Poker)) DrawArcadeSetupParagraph("End the current activity before starting Poker.");
    }

    private void DrawPokerRulesCard(float width, bool custom)
    {
        var height = custom ? 370f : 390f;
        DrawQuizCardBegin(custom ? "##pokerCustomRules" : "##pokerStandardRules", width, Ui(height), custom);
        var origin = ImGui.GetWindowPos();
        DrawPictionarySetupBadge(custom ? "Custom table" : "Recommended", !custom);
        ImGui.Dummy(UiVec(0f, 6f));
        DrawPictionarySetupCardHeading(custom ? FontAwesomeIcon.Cog : FontAwesomeIcon.Users,
            custom ? "Custom rules" : "Standard rules",
            custom ? "Set starting chips, blinds and turn time." : "An open-ended table for friends or solo practice.");
        ImGui.Dummy(UiVec(0f, 8f));
        var enabled = IsLocalActivityHost && CurrentSession is not null && CanLaunchActivity(ActiveActivityKind.Poker);
        if (custom)
        {
            using (UiDisabled.Scope(!enabled))
            {
                DrawPictionaryRuleStepper("Starting chips", "pokerChips", ref pokerCustomChips, 250, 5000, 250, n => n.ToString());
                DrawPictionaryRuleStepper("Big blind", "pokerBlind", ref pokerCustomBlind, 10, 100, 10, n => $"{n / 2} / {n}");
                DrawPictionaryRuleStepper("Turn timer", "pokerTimer", ref pokerCustomTurnSeconds, 15, 60, 5, n => $"{n} seconds");
                ImGui.Checkbox("House bot for one seated player##pokerBot", ref pokerCustomBot);
            }
        }
        else
        {
            DrawActivityFeature("1,000 starting chips • blinds 10 / 20");
            DrawActivityFeature("25 seconds per turn");
            DrawActivityFeature("House bot when only one player sits");
            DrawActivityFeature("Private hands, shared results and ledger");
        }
        ImGui.SetCursorScreenPos(origin + UiVec(18f, height - 62f));
        DrawActivityStartButtons(custom ? "pokerCustom" : "pokerStandard", () =>
        {
            pokerCustomRules = custom;
            StartPokerTable();
        }, () =>
        {
            pokerCustomRules = custom;
            if (stream.Mode == StreamMode.Hosting) StartPokerTable();
            else
            {
                pendingWatchPartyMediaKind = PendingWatchPartyMediaKind.Poker;
                OpenPendingWatchPartyCreation();
            }
        }, enabled, 44f, ImGui.GetContentRegionAvail().X);
        ImGui.EndChild();
    }

    private void DrawPokerGuide(string id, string title, string[] paragraphs)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var wrap = Math.Max(Ui(100f), width - Ui(36f));
        var height = Ui(40f) + ImGui.GetTextLineHeight() + paragraphs.Sum(t => ImGui.CalcTextSize(t, false, wrap).Y)
            + ImGui.GetStyle().ItemSpacing.Y * (paragraphs.Length + 1);
        DrawQuizCardBegin(id, width, height);
        ImGui.TextColored(AccentHover, title);
        foreach (var text in paragraphs) DrawArcadeSetupParagraph(text);
        ImGui.EndChild();
    }

    private void DrawPokerSessionLedger()
    {
        if (!ImGui.CollapsingHeader("Session ledger##pokerLedger")) return;
        ImGui.TextColored(MutedText, "Pot wins include side pots. Net excludes free refills; records reset when the table ends.");
        foreach (var standing in pokerLedger.Values.OrderByDescending(s => s.Net).ThenBy(s => s.Name, StringComparer.Ordinal))
            DrawArcadeSetupParagraph($"{standing.Name} — {standing.Stack} chips · {standing.Hands} hands · {standing.Wins} wins · net {standing.Net:+0;-0;0} · {standing.Refills} refills");
    }
}

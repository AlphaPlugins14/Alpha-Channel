using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawSlotSetupPage()
    {
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(10f, 12f));
        var width = ImGui.GetContentRegionAvail().X;
        DrawQuizCardBegin("##slotSetupHero", width, Ui(150f));
        var origin = ImGui.GetWindowPos();
        var showArt = width >= Ui(850f);
        ImGui.PushTextWrapPos(showArt ? ImGui.GetCursorPosX() + width * .62f : 0f);
        DrawArcadeSetupHeading("Spin together. Celebrate the jackpot.", 1.5f, "slot-machine", "Slot Machine", false,
            origin.X + width - (showArt ? Math.Min(Ui(270f), width * .30f) + Ui(32f) : Ui(18f)));
        ImGui.TextColored(MutedText, "A relaxed cabinet for solo play or a Watch Party. Take turns, share the excitement and compare your luck.");
        ImGui.PopTextWrapPos();
        ImGui.SetCursorScreenPos(origin + UiVec(18f, 108f));
        DrawPictionarySetupBadge("OPEN-ENDED PLAY • PLAY COINS ONLY", true);
        if (showArt)
        {
            var artWidth = Math.Min(Ui(270f), width * .30f);
            ImGui.SetCursorScreenPos(origin + new Vector2(width - artWidth - Ui(20f), Ui(16f)));
            DrawActivityPreview("slotSetupArt", ActivityPreviewKind.SlotMachine, artWidth, 118f, 14f, 8f);
        }
        ImGui.EndChild();
        var stacked = width < Ui(760f);
        var cardWidth = stacked ? width : (width - Ui(12f)) * .5f;
        DrawSlotModeCard(cardWidth, true);
        if (!stacked) ImGui.SameLine(0f, Ui(12f));
        DrawSlotModeCard(cardWidth, false);
        string[] rules =
        [
            "1. Insert a coin, then pull the handle. In shared play, clicking the coin slot joins a queue; each player gets their own turn and balance while everyone watches the same cabinet.",
            "2. Each player starts with 100 play coins. A spin costs 5. Only the illuminated middle row pays: matching pairs and triples pay different amounts for each symbol. Open Help on the machine for the payout table. Three Gil symbols return the 250-coin jackpot. Payouts are total returns, including your stake.",
            "3. Wins appear on everyone's TV and are saved in the recent wins feed. Triples and jackpots trigger a shower of coins. The ledger shows balances, spins, wins, net gain, best payout and free refills.",
            "4. If you run out, your next turn refills your balance to 100. Refills do not count as winnings. There are no fixed rounds; play until the host ends the activity. These coins never spend or award real gil, MGP or broadcast allowance.",
            "5. Pull the handle within 20 seconds of your turn becoming ready. Otherwise your 5 coins are refunded and the queue continues. You can leave the queue before your turn. Players who disconnect before pulling are refunded too.",
        ];
        var wrap = Math.Max(Ui(100f), width - Ui(36f));
        var height = Ui(40f) + ImGui.GetTextLineHeight() + rules.Sum(t => ImGui.CalcTextSize(t, false, wrap).Y)
            + ImGui.GetStyle().ItemSpacing.Y * (rules.Length + 1);
        DrawQuizCardBegin("##slotRulesGuide", width, height);
        ImGui.TextColored(AccentHover, "How to play & payouts");
        foreach (var rule in rules) DrawArcadeSetupParagraph(rule);
        ImGui.EndChild();
        if (stream.Mode == StreamMode.Viewing) DrawArcadeSetupParagraph("The Watch Party host chooses the mode and starts the cabinet.");
        else if (CurrentSession is null) DrawArcadeSetupParagraph("Sign in to start Slot Machine.");
        else if (!CanLaunchActivity(ActiveActivityKind.SlotMachine)) DrawArcadeSetupParagraph("End the current activity before starting Slot Machine.");
    }

    private void DrawSlotModeCard(float width, bool shared)
    {
        const float height = 355f;
        DrawQuizCardBegin(shared ? "##slotSharedSetup" : "##slotHostSetup", width, Ui(height), shared);
        var origin = ImGui.GetWindowPos();
        DrawPictionarySetupBadge(shared ? "Play together" : "Watch together", shared);
        ImGui.Dummy(UiVec(0f, 6f));
        DrawPictionarySetupCardHeading(shared ? FontAwesomeIcon.Users : FontAwesomeIcon.User,
            shared ? "Shared cabinet" : "Host-only cabinet",
            shared ? "Everyone can queue for their own spin." : "Only the host plays; friends watch the results.");
        ImGui.Dummy(UiVec(0f, 8f));
        DrawActivityFeature(shared ? "Individual balances and a fair turn queue" : "Host controls the coin slot and handle");
        DrawActivityFeature("Shared wins and big-win coin showers");
        DrawActivityFeature("Player ledger and recent wins");
        ImGui.SetCursorScreenPos(origin + UiVec(18f, height - 62f));
        var enabled = stream.Mode != StreamMode.Viewing && CurrentSession is not null && CanLaunchActivity(ActiveActivityKind.SlotMachine);
        DrawActivityStartButtons(shared ? "slotShared" : "slotHost", () =>
        {
            slotMachineSetupShared = shared;
            StartSlotMachineGame();
        }, () =>
        {
            slotMachineSetupShared = shared;
            if (stream.Mode == StreamMode.Hosting) StartSlotMachineGame();
            else
            {
                pendingWatchPartyMediaKind = PendingWatchPartyMediaKind.SlotMachine;
                OpenPendingWatchPartyCreation();
            }
        }, enabled, 44f, ImGui.GetContentRegionAvail().X);
        ImGui.EndChild();
    }
}

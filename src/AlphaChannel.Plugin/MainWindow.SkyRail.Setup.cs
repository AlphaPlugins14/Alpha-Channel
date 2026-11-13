using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool CanStartSkyRailFromSetup => stream.Mode != StreamMode.Viewing && CurrentSession is not null &&
        CanLaunchActivity(ActiveActivityKind.SkyRail);

    private void DrawSkyRailSetupPage()
    {
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(10f, 12f));
        var width = ImGui.GetContentRegionAvail().X;
        DrawQuizCardBegin("##skyRailSetupHero", width, Ui(150f));
        var origin = ImGui.GetWindowPos();
        var showArt = width >= Ui(850f);
        ImGui.PushTextWrapPos(showArt ? ImGui.GetCursorPosX() + width * .62f : 0f);
        DrawArcadeSetupHeading("Aim high. Dodge red. Fly to victory.", 1.5f, "air-force", "Air Force EX", true,
            origin.X + width - (showArt ? Math.Min(Ui(270f), width * .30f) + Ui(32f) : Ui(18f)));
        ImGui.TextColored(MutedText, "Shoot moving targets on the TV or in this window. Chase your best score solo or compete with friends.");
        ImGui.PopTextWrapPos();
        ImGui.SetCursorScreenPos(origin + UiVec(18f, 108f));
        DrawPictionarySetupBadge("1+ PLAYERS • SOLO OR WATCH PARTY", true);
        if (showArt)
        {
            var artWidth = Math.Min(Ui(270f), width * .30f);
            ImGui.SetCursorScreenPos(origin + new Vector2(width - artWidth - Ui(20f), Ui(16f)));
            DrawActivityPreview("skyRailSetupArt", ActivityPreviewKind.SkyRail, artWidth, 118f, 14f, 8f);
        }
        ImGui.EndChild();
        var stacked = width < Ui(760f);
        var cardWidth = stacked ? width : (width - Ui(12f)) * .5f;
        DrawSkyRailSetupRulesCard(cardWidth, false);
        if (!stacked) ImGui.SameLine(0f, Ui(12f));
        DrawSkyRailSetupRulesCard(cardWidth, true);

        DrawSkyRailSetupGuide("##skyRailHowToPlay", "How to play",
        [
            "1. Join the ready lobby. Your route is already chosen; the host starts the flight when the pilots are ready. You can unready before the host locks the roster.",
            "2. After the six-second countdown, aim with your mouse and click to fire on the TV or in this window. Shots have a short cooldown. Targets move towards you and disappear when they pass.",
            "3. Blue targets earn 10 points, silver 25 and gold 50. Red targets lose 100 points. Each pilot can hit each normal target once; friends cannot steal your targets.",
            "4. Consecutive positive hits build a combo: the third hit gives a 2× bonus, the sixth gives 3×, and the ninth gives 5×. A miss or red hit resets it. Your score cannot fall below zero.",
            "5. A boss may appear once during the flight. Work together to defeat it and shoot its rockets. The boss has 70 health plus 80 per additional active pilot. Hits on its face deal 2 damage; other hits deal 1. Each escaping rocket costs every active pilot 200 points and resets their combo. Six escaping rockets temporarily damage your local display and disable your weapons for 3 seconds while repairs complete. Boss and rocket shots preserve your combo but award no points individually. Defeating the boss awards every active pilot 400 points.",
            "6. A boss can extend its wave by up to 30 seconds, then retreats. After the last wave, the highest score wins; equal scores share the rank. Results remain until the host replays or ends the game. Late arrivals can join the next flight.",
        ]);
        if (!CanStartSkyRailFromSetup)
            DrawArcadeSetupParagraph(stream.Mode == StreamMode.Viewing ? "The Watch Party host chooses the rules and starts Air Force EX."
                : CurrentSession is null ? "Sign in to start Air Force EX." : "End the current activity before starting Air Force EX.");
    }

    private void DrawSkyRailSetupRulesCard(float width, bool custom)
    {
        var height = custom ? 350f : 390f;
        DrawQuizCardBegin(custom ? "##skyRailCustomSetup" : "##skyRailStandardSetup", width, Ui(height), custom);
        var origin = ImGui.GetWindowPos();
        DrawPictionarySetupBadge(custom ? "Custom flight" : "Recommended", !custom);
        ImGui.Dummy(UiVec(0f, 6f));
        DrawPictionarySetupCardHeading(custom ? FontAwesomeIcon.Cog : FontAwesomeIcon.Bullseye,
            custom ? "Custom rules" : "Standard rules",
            custom ? "Choose flight length and boss encounters. Pick your map in the lobby." : "Eight waves on a random route with a shared boss encounter.");
        ImGui.Dummy(UiVec(0f, 8f));
        if (custom)
        {
            using (UiDisabled.Scope(!CanStartSkyRailFromSetup))
            {
                DrawPictionaryRuleStepper("Waves", "skyRailWaves", ref skyRailCustomWaves, 4, 12, 1, n => n.ToString());
                ImGui.TextColored(MutedText, "Boss encounter");
                ImGui.SetNextItemWidth(-1);
                string[] modes = ["Random chance", "Always — one encounter", "Off — targets only"];
                ImGui.Combo("##skyRailSetupBoss", ref skyRailCustomBossMode, modes, modes.Length);
            }
        }
        else
        {
            DrawActivityFeature("8 waves");
            DrawActivityFeature("Random map");
            DrawActivityFeature("Guaranteed shared boss encounter");
            DrawActivityFeature("Set personal highscores");
        }
        ImGui.SetCursorScreenPos(origin + UiVec(18f, height - 62f));
        DrawActivityStartButtons(custom ? "skyRailCustom" : "skyRailStandard", () =>
        {
            skyRailCustomRules = custom;
            StartSkyRailGame();
        }, () =>
        {
            skyRailCustomRules = custom;
            if (stream.Mode == StreamMode.Hosting) StartSkyRailGame();
            else
            {
                pendingWatchPartyMediaKind = PendingWatchPartyMediaKind.SkyRail;
                OpenPendingWatchPartyCreation();
            }
        }, CanStartSkyRailFromSetup, 44f, ImGui.GetContentRegionAvail().X);
        ImGui.EndChild();
    }

    private void DrawSkyRailSetupGuide(string id, string title, string[] steps)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var wrap = Math.Max(Ui(100f), width - Ui(36f));
        var height = Ui(40f) + ImGui.GetTextLineHeight() + steps.Sum(t => ImGui.CalcTextSize(t, false, wrap).Y)
            + ImGui.GetStyle().ItemSpacing.Y * (steps.Length + 1);
        DrawQuizCardBegin(id, width, height);
        ImGui.TextColored(AccentHover, title);
        foreach (var step in steps) DrawArcadeSetupParagraph(step);
        ImGui.EndChild();
    }

    private void PruneSkyRailParticipants()
    {
        bool Connected(string id) => id == LocalSkyRailId || stream.Mode == StreamMode.Hosting &&
            stream.Roster.Any(p => p.UserId == id);
        var departed = skyRailReady.Where(id => !Connected(id)).ToArray();
        if (departed.Length == 0) return;
        foreach (var id in departed)
        {
            skyRailReady.Remove(id);
            skyRailLocked.Remove(id);
            if (skyRailPhase == SkyRailPhase.Lobby) skyRailPlayers.Remove(id);
        }
        if (skyRailPhase == SkyRailPhase.Countdown && skyRailLocked.Count == 0)
        {
            skyRailPhase = SkyRailPhase.Lobby;
            skyRailPhaseEndsUtc = DateTime.MaxValue;
        }
        skyRailRevision++;
        SendSkyRailState();
    }
}

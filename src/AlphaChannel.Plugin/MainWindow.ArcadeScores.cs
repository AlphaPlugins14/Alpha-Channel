using AlphaChannel.Plugin.ArcadeScores;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Security.Cryptography;
using System.Text;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private ArcadeScoreService? arcadeScores;
    private string? arcadeScoreboardGame;
    private string arcadeScoreboardTitle = "";
    private string arcadeScoreboardPeriod = "all";
    private bool arcadeScoreboardCumulative;
    private Task<ArcadeLeaderboard>? arcadeScoreboardFetch;
    private ArcadeScoreService ArcadeScores => arcadeScores ??= new(Plugin.PluginInterface.ConfigDirectory.FullName);

    internal void TickArcadeScores()
    {
        if (Volatile.Read(ref disposeStarted) != 0) return;
        ObserveArcadeScores();
    }

    private void ObserveArcadeScores()
    {
        var account = CurrentSession;
        if (account is null)
        {
            arcadeScores?.SetSession(null, null);
            return;
        }
        var scores = ArcadeScores;
        scores.SetSession(account.AccountId, account.Token);
        var id = account.AccountId;
        var name = LocalPictionaryDisplayName();
        if (skyRailPhase == SkyRailPhase.Results && skyRailSessionId is not null &&
            skyRailWave >= skyRailWaveCount && skyRailLocked.Contains(id) && skyRailPlayers.TryGetValue(id, out var pilot))
            scores.RecordResult(id, name, "air-force", skyRailSessionId, skyRailRound, pilot.Score);
        if (quizPhase == QuizPhase.Finished && quizSessionId is not null && quizScores.TryGetValue(id, out var quizScore))
            scores.RecordResult(id, name, "quiz", quizSessionId, 1, quizScore);
        if (pictionaryPhase == PictionaryPhase.GameOver && pictionarySessionId is not null && pictionaryScores.TryGetValue(id, out var drawingScore))
            scores.RecordResult(id, name, "pictionary", pictionarySessionId, 1, drawingScore);
        if (teamsPhase == TeamsPhase.Finished && teamsSession is not null && teamsMembers.TryGetValue(id, out var member) && member.Team is 0 or 1)
            scores.RecordResult(id, name, "pictionary-teams", teamsSession, 1, member.Team == 0 ? teamsRedScore : teamsBlueScore);
        var choco = chocoRacePlayers.GetValueOrDefault(id);
        scores.ObservePeak(id, name, "choco-race", chocoRaceSessionId is not null && choco is not null ? chocoRaceSessionId : null,
            choco?.Chips ?? 0, chocoRaceRefilledPlayerIds.Contains(id) ? chocoRaceRound.ToString() : "", chocoRaceStartingChips);
        var slot = slotMachinePlayers.GetValueOrDefault(id);
        scores.ObservePeak(id, name, "slot-machine", slotMachineSessionId is not null && slot is not null ? slotMachineSessionId : null,
            slot?.Coins ?? 0, slot?.Refills.ToString() ?? "", SlotMachineRules.StartingCoins);
        var poker = pokerPlayers.FirstOrDefault(player => player.Id == id && player.State != "LEFT");
        scores.ObservePeak(id, name, "poker", pokerSessionId is not null && poker is not null ? pokerSessionId : null,
            poker?.Stack ?? 0, pokerLedger.GetValueOrDefault(id)?.Refills.ToString() ?? "", pokerStartingChips);
        var roulette = roulettePlayers.GetValueOrDefault(id);
        scores.ObservePeak(id, name, "roulette", rouletteSessionId is not null && roulette is not null ? rouletteSessionId : null,
            roulette?.Chips ?? 0, rouletteRefilledPlayerIds.Contains(id) ? rouletteRoundNumber.ToString() : "", rouletteStartingChips);
    }

    private void CaptureArcadeScoresBeforeReset(string? peakGame = null)
    {
        if (Volatile.Read(ref disposeStarted) != 0) return;
        ObserveArcadeScores();
        if (peakGame is not null && CurrentSession is { } account)
            arcadeScores?.ObservePeak(account.AccountId, LocalPictionaryDisplayName(), peakGame, null, 0);
    }

    private void DrawArcadeSetupHeading(string heading, float fontScale, string game,
        string title, bool cumulative, float buttonRightX)
    {
        var origin = ImGui.GetCursorScreenPos();
        using var uiFontScale4140=UiFonts.PushRelative(fontScale);
        var headingSize = ImGui.CalcTextSize(heading);
        var available = buttonRightX - origin.X;
        var compact = headingSize.X + Ui(190f) > available;
        var buttonWidth = Ui(compact ? 100f : 174f);
        var displayedHeading = TruncateToWidth(heading, MathF.Max(Ui(50f), available - buttonWidth - Ui(16f)));
        ImGui.TextColored(Hex(0xE2E7EF), displayedHeading);
        if (displayedHeading != heading && ImGui.IsItemHovered()) UiFonts.SetTooltip(heading);
        uiFontScale4140.Dispose();
        var afterHeading = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(buttonRightX - buttonWidth, origin.Y + (headingSize.Y - Ui(32f)) * 0.5f));
        if (GameLayoutButton(compact ? "Scores" : "View scoreboard", FontAwesomeIcon.Trophy, buttonWidth, true, height: 32f))
            OpenArcadeScoreboard(game, title, cumulative);
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip($"View {title} scoreboard");
        ImGui.SetCursorScreenPos(afterHeading);
    }

    private void OpenArcadeScoreboard(string game, string title, bool cumulative)
    {
        arcadeScoreboardGame = game;
        arcadeScoreboardTitle = title;
        arcadeScoreboardCumulative = cumulative;
        arcadeScoreboardPeriod = "all";
        arcadeScoreboardFetch = ArcadeScores.GetAsync(game, "all");
        activeGameDialog = "Arcade scoreboard";
    }

    private void DrawArcadeScoreboardPopup()
    {
        if (!BeginGameDialog("Arcade scoreboard", arcadeScoreboardTitle + " — Top 20",
                FontAwesomeIcon.Trophy, 570f, 480f)) return;
        try
        {
            if (activeGameDialog is null || ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                activeGameDialog = null;
                return;
            }
            using (UiFonts.PushRelative(.90f))
            using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
            ImGui.TextWrapped(arcadeScoreboardCumulative
                ? "Total points earned across all completed games and rule types."
                : "Highest chip or coin balance held at any point during play.");
            ImGui.Spacing();
            if (arcadeScoreboardCumulative)
                foreach (var (period, label) in new[] { ("all", "All time"), ("month", "Monthly"), ("week", "Weekly") })
                {
                    if (period != "all") ImGui.SameLine();
                    using var colour = ImRaii.PushColor(ImGuiCol.Button, arcadeScoreboardPeriod == period ? Accent : CardBg);
                    if (ImGui.Button(label, UiVec(140f, 32f)))
                    {
                        arcadeScoreboardPeriod = period;
                        arcadeScoreboardFetch = ArcadeScores.GetAsync(arcadeScoreboardGame!, period);
                    }
                }
            ImGui.Separator();
            // Only the score list scrolls; keep the tabs, footer and Close button fixed.
            var footerHeight = ImGui.GetTextLineHeightWithSpacing() * (arcadeScoreboardCumulative ? 2 : 1)
                + Ui(42f) + ImGui.GetStyle().ItemSpacing.Y * 3f + Ui(8f);
            var listHeight = MathF.Max(Ui(60f), ImGui.GetContentRegionAvail().Y - footerHeight);
            using (var child = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##arcadeScoreboardRows", new Vector2(0, listHeight), false)))
            {
                if (child)
                {
                    if (arcadeScoreboardFetch is null || !arcadeScoreboardFetch.IsCompleted) ImGui.Text("Loading scoreboard…");
                    else if (!arcadeScoreboardFetch.IsCompletedSuccessfully)
                    {
                        _ = arcadeScoreboardFetch.Exception;
                        ImGui.TextWrapped("The scoreboard could not be loaded. Try opening it again after 15 minutes.");
                    }
                    else
                    {
                        var board = arcadeScoreboardFetch.Result;
                        if (board.Period != "all") ImGui.TextColored(MutedText, "Period starts " + board.PeriodKey + " (UTC)");
                        if (board.Entries.Length == 0) ImGui.TextWrapped("No scores recorded for this period yet.");
                        var own = CurrentSession?.AccountId;
                        var ownId = own is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(own.ToLowerInvariant()))).ToLowerInvariant();
                        if (ImGui.BeginTable("##arcadeScoreTable", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
                        {
                            try
                            {
                                ImGui.TableSetupColumn("Rank", ImGuiTableColumnFlags.WidthFixed, Ui(72f));
                                ImGui.TableSetupColumn("Player");
                                ImGui.TableSetupColumn(arcadeScoreboardCumulative ? "Points" : "Peak balance", ImGuiTableColumnFlags.WidthFixed, Ui(125f));
                                ImGui.TableHeadersRow();
                                foreach (var row in board.Entries)
                                {
                                    ImGui.TableNextRow(0, Ui(24f)); ImGui.TableNextColumn();
                                    if (row.Rank <= 3)
                                    {
                                        var medal = row.Rank == 1 ? new Vector4(1f, .78f, .25f, 1f) : row.Rank == 2
                                            ? new Vector4(.76f, .8f, .86f, 1f) : new Vector4(.82f, .55f, .32f, 1f);
                                        using (UiFonts.PushIcon()) ImGui.TextColored(medal, FontAwesomeIcon.Star.ToIconString());
                                        ImGui.SameLine();
                                    }
                                    ImGui.Text(row.Rank.ToString()); ImGui.TableNextColumn();
                                    DrawUsername(row.PlayerId,TruncateToWidth(row.Name + (row.PlayerId == ownId ? " (you)" : ""), ImGui.GetContentRegionAvail().X),row.PlayerId==ownId ? Accent : Vector4.One);
                                    ImGui.TableNextColumn(); ImGui.Text(row.Score.ToString("N0"));
                                }
                            }
                            finally { ImGui.EndTable(); }
                        }
                    }
                }
            }
            ImGui.Separator();
            using (UiFonts.PushRelative(.90f)) ImGui.TextColored(MutedText, "Each scoreboard is cached for 15 minutes after opening.");
            if (arcadeScoreboardCumulative) ImGui.TextColored(MutedText, "Weeks begin Monday; months begin on the 1st. Times use UTC.");
            if (GameLayoutButton("Close", FontAwesomeIcon.Check, ImGui.GetContentRegionAvail().X, true))
                activeGameDialog = null;
        }
        finally { EndGameDialog(); }
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private enum ViewerActivityPrompt
    {
        None,
        TeamWhiteboard,
        Pictionary,
        Arcade,
    }

    private ViewerActivityPrompt viewerActivityPrompt;
    private DateTime viewerActivityPromptReadyUtc = DateTime.MinValue;
    private string? promptedWhiteboardSessionId;
    private string? promptedPictionarySessionId;
    private readonly Dictionary<ActiveActivityKind, string> promptedArcadeSessions = new();
    private ActiveActivityKind promptedArcadeActivity;
    private string? promptedArcadeSession;

    // Wait for each game's real snapshot, rather than its canvas announcement.
    // This covers both server commands and relay snapshots without duplicate prompts.
    private string? ViewerArcadeSession(ActiveActivityKind activity) => activity switch
    {
        ActiveActivityKind.PictionaryTeams when PictionaryTeamsActive => teamsSession,
        ActiveActivityKind.Quiz when QuizActive => quizSessionId,
        ActiveActivityKind.Roulette when RouletteActive => rouletteSessionId,
        ActiveActivityKind.Bingo when BingoActive => bingoSessionId,
        ActiveActivityKind.BlastParty when blastState is not null => blastCanvas,
        ActiveActivityKind.SkyRail when SkyRailActive => skyRailSessionId,
        ActiveActivityKind.SlotMachine when SlotMachineActive => slotMachineSessionId,
        ActiveActivityKind.Poker when PokerActive => pokerSessionId,
        ActiveActivityKind.ChocoRaceBets when ChocoRaceActive => chocoRaceSessionId,
        ActiveActivityKind.Pixels when pixelsActive && pixelsBoard.Ready => pixelsCanvas,
        ActiveActivityKind.LuckyWheel when LuckyWheelActive => luckyWheelState?.SessionId,
        _ => null,
    };

    private static HomePage ViewerArcadePage(ActiveActivityKind activity) => activity switch
    {
        ActiveActivityKind.PictionaryTeams => HomePage.PictionaryTeams,
        ActiveActivityKind.Quiz => HomePage.Quiz,
        ActiveActivityKind.Roulette => HomePage.Roulette,
        ActiveActivityKind.Bingo => HomePage.Bingo,
        ActiveActivityKind.BlastParty => HomePage.BlastParty,
        ActiveActivityKind.SkyRail => HomePage.SkyRail,
        ActiveActivityKind.SlotMachine => HomePage.SlotMachine,
        ActiveActivityKind.Poker => HomePage.Poker,
        ActiveActivityKind.ChocoRaceBets => HomePage.RacePreview,
        ActiveActivityKind.Pixels => HomePage.Pixels,
        ActiveActivityKind.LuckyWheel => HomePage.LuckyWheel,
        _ => HomePage.Arcade,
    };

    private void UpdateViewerArcadePrompt()
    {
        if (stream.Mode != StreamMode.Viewing)
        {
            viewerActivityPrompt = ViewerActivityPrompt.None;
            promptedArcadeSessions.Clear();
            return;
        }

        var activity = ActiveActivity;
        var session = ViewerArcadeSession(activity);
        if (viewerActivityPrompt == ViewerActivityPrompt.Arcade &&
            (activity != promptedArcadeActivity || session != promptedArcadeSession))
            viewerActivityPrompt = ViewerActivityPrompt.None;
        if (string.IsNullOrWhiteSpace(session) ||
            promptedArcadeSessions.TryGetValue(activity, out var previous) && previous == session)
            return;

        promptedArcadeSessions[activity] = session;
        viewerActivityPrompt = ViewerActivityPrompt.None;
        // Match Pictionary's invitation: respect a closed plugin window, and
        // do not interrupt someone already on the game page (including drawers).
        if (!IsOpen || currentPage == ViewerArcadePage(activity))
            return;
        promptedArcadeActivity = activity;
        promptedArcadeSession = session;
        viewerActivityPrompt = ViewerActivityPrompt.Arcade;
        viewerActivityPromptReadyUtc = DateTime.MinValue;
    }

    private void RequestTeamWhiteboardPrompt(string sessionId)
    {
        if (stream.Mode != StreamMode.Viewing ||
            string.IsNullOrWhiteSpace(sessionId) ||
            string.Equals(promptedWhiteboardSessionId, sessionId, StringComparison.Ordinal))
            return;

        promptedWhiteboardSessionId = sessionId;
        viewerActivityPrompt = ViewerActivityPrompt.TeamWhiteboard;
        // Pictionary owns a team whiteboard and its state follows immediately.
        // Wait briefly so that invitation can replace this one before it is drawn.
        viewerActivityPromptReadyUtc = DateTime.UtcNow.AddSeconds(1);
    }

    internal void UpdateViewerActivityPrompt()
    {
        UpdateViewerArcadePrompt();
        // A standalone team whiteboard still opens its invitation after the short grace
        // period. Pictionary replaces this pending prompt before then, allowing its TV
        // invitation to respect whether the Alpha Channel window was already open.
        if (viewerActivityPrompt == ViewerActivityPrompt.TeamWhiteboard && !IsOpen &&
            DateTime.UtcNow >= viewerActivityPromptReadyUtc && stream.Mode == StreamMode.Viewing &&
            whiteboardActive && whiteboardMode == WhiteboardMode.Team && ActiveActivity == ActiveActivityKind.Whiteboard)
            OpenUi();
    }

    private void RequestPictionaryPrompt(string sessionId)
    {
        if (stream.Mode != StreamMode.Viewing ||
            string.IsNullOrWhiteSpace(sessionId) ||
            string.Equals(promptedPictionarySessionId, sessionId, StringComparison.Ordinal))
            return;

        promptedPictionarySessionId = sessionId;
        viewerActivityPrompt = ViewerActivityPrompt.None;
        if (!IsOpen)
            return;
        viewerActivityPrompt = ViewerActivityPrompt.Pictionary;
        viewerActivityPromptReadyUtc = DateTime.MinValue;
    }

    private void OpenPictionaryForDrawingTurn(string sessionId, int round)
    {
        var alertKey = $"{sessionId}:{round}";
        if (string.Equals(lastPictionaryDrawingTurnAlert, alertKey, StringComparison.Ordinal))
            return;

        lastPictionaryDrawingTurnAlert = alertKey;
        viewerActivityPrompt = ViewerActivityPrompt.None;
        pictionaryShowSetup = false;
        lastActivitiesPage = HomePage.Pictionary;
        currentPage = HomePage.Pictionary;
        OpenUi();
    }

    private void DrawViewerActivityPrompt()
    {
        if (viewerActivityPrompt == ViewerActivityPrompt.None)
            return;

        // Do not stack two modal decisions. Let the existing TV prompt finish,
        // then offer the activity page on the following frame.
        if (viewerTvSpawnPromptRequested)
            return;

        if (stream.Mode != StreamMode.Viewing)
        {
            viewerActivityPrompt = ViewerActivityPrompt.None;
            return;
        }

        if (viewerActivityPrompt == ViewerActivityPrompt.Pictionary && !PictionaryActive ||
            viewerActivityPrompt == ViewerActivityPrompt.TeamWhiteboard &&
            (!whiteboardActive || whiteboardMode != WhiteboardMode.Team || ActiveActivity != ActiveActivityKind.Whiteboard) ||
            viewerActivityPrompt == ViewerActivityPrompt.Arcade &&
            (ActiveActivity != promptedArcadeActivity || ViewerArcadeSession(promptedArcadeActivity) != promptedArcadeSession))
        {
            viewerActivityPrompt = ViewerActivityPrompt.None;
            return;
        }

        if (DateTime.UtcNow < viewerActivityPromptReadyUtc)
            return;

        var isPictionary = viewerActivityPrompt == ViewerActivityPrompt.Pictionary;
        var isArcade = viewerActivityPrompt == ViewerActivityPrompt.Arcade;
        var title = isArcade ? ActivityTitle(promptedArcadeActivity) : isPictionary ? "Pictionary" : "Team Canvas";
        var destination = isArcade ? ViewerArcadePage(promptedArcadeActivity) : isPictionary ? HomePage.Pictionary : HomePage.Whiteboard;
        var parentPos = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var panelSize = new Vector2(
            MathF.Min(Ui(530f), parentSize.X - Ui(40f)),
            MathF.Min(Ui(isPictionary || isArcade ? 310f : 285f), parentSize.Y - Ui(40f)));

        ImGui.SetNextWindowPos(parentPos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;

        if (!BeginWindowOverlay("##viewerActivityOverlay", flags))
        {
            ImGui.End();
            return;
        }

        ImGui.GetWindowDrawList().AddRectFilled(
            parentPos,
            parentPos + parentSize,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.56f)));
        ImGui.SetCursorScreenPos(parentPos + (parentSize - panelSize) * 0.5f);

        var openRequested = false;
        var dismissRequested = false;
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14f))
                   .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
                   .Push(ImGuiStyleVar.WindowPadding, UiVec(24f, 20f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.025f, 0.03f, 0.06f, 0.995f))
                   .Push(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.82f)))
        using (var card = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##viewerActivityCard", panelSize, true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (card)
            {
                DrawSectionTitle(
                    isPictionary ? FontAwesomeIcon.Palette : FontAwesomeIcon.Users,
                    $"{title} has started");
                ImGui.Separator();
                ImGui.Dummy(UiVec(0f, 10f));

                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
                ImGui.TextColored(
                    MutedText,
                    isArcade
                        ? promptedArcadeActivity == ActiveActivityKind.PictionaryTeams
                            ? "Your host has started Pictionary Teams. Open the game page to choose your team and play. When it is your drawing turn, the drawing canvas opens automatically."
                            : $"Your host has started {title}. Open the game page to see the game and its controls, or keep watching on the TV."
                        : isPictionary
                        ? "Play and submit guesses directly on the TV. When it is your drawing turn, the Pictionary page opens so you can use the drawing canvas."
                        : "Everyone in the Watch Party can draw from the Canvas page. Open it to add your own strokes to the shared canvas.");
                ImGui.PopTextWrapPos();
                ImGui.Dummy(UiVec(0f, 18f));

                var gap = Ui(10f);
                var buttonWidth = (ImGui.GetContentRegionAvail().X - gap) * 0.5f;
                dismissRequested = ImGui.Button("Not now", UiVec(buttonWidth, 40f));
                ImGui.SameLine(0f, gap);
                using (ImRaii.PushColor(ImGuiCol.Button, Accent)
                           .Push(ImGuiCol.ButtonHovered, AccentHover)
                           .Push(ImGuiCol.ButtonActive, AccentActive))
                {
                    openRequested = ImGui.Button(
                        isArcade ? $"Open {title}" : isPictionary ? "Open Pictionary" : "Open Canvas",
                        UiVec(buttonWidth, 40f));
                }
            }
        }

        if (dismissRequested || openRequested)
            viewerActivityPrompt = ViewerActivityPrompt.None;
        if (openRequested)
        {
            if (destination == HomePage.LuckyWheel) luckyWheelEditorOpen = false;
            lastActivitiesPage = destination;
            currentPage = lastActivitiesPage;
        }

        ImGui.End();
    }
}

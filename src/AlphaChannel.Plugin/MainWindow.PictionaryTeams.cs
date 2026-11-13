using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private const int TeamsTvJoinRedTarget = 500;
    private const int TeamsTvJoinBlueTarget = 501;
    private const int TeamsTvConfirmTarget = 502;
    private const int TeamsTvWordChoiceTargetStart = 600;
    private const int TeamsTvPlayAgainTarget = 603;
    private enum TeamsPhase { Inactive, Lobby, ChoosingDrawers, ChoosingWords, Drawing, Summary, Finished }
    private sealed record TeamsMember(string Id, string Name, int Team, int Correct);
    private sealed record TeamsSnapshot(string Session, TeamsPhase Phase, int Round, int Rounds,
        long Ends, int Seconds, int GuessPoints, int RedScore, int BlueScore,
        string RedDrawer, string BlueDrawer, string RedMask, string BlueMask,
        string RedReveal, string BlueReveal, TeamsMember[] Members, int WinningTeam = -1, string GuesserId = "");

    private TeamsPhase teamsPhase;
    private string? teamsSession;
    private readonly Dictionary<string, TeamsMember> teamsMembers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> teamsGuessRate = new(StringComparer.Ordinal);
    private string teamsRosterSignature = string.Empty;
    private DateTime teamsPhaseEndsUtc;
    private int teamsRound;
    private int teamsRounds = PictionaryStandardRoundCount;
    private int teamsDrawingSeconds = PictionaryStandardDrawingSeconds;
    private int teamsGuessPoints = PictionaryStandardFirstGuessPoints;
    private int teamsCustomRounds = PictionaryStandardRoundCount;
    private int teamsCustomSeconds = PictionaryStandardDrawingSeconds;
    private int teamsCustomPoints = PictionaryStandardFirstGuessPoints;
    private int teamsRedScore;
    private int teamsBlueScore;
    private int teamsWinningTeam = -1;
    private string teamsWinningGuesser = string.Empty;
    private readonly string[] teamsDrawers = [string.Empty, string.Empty];
    private readonly string[] teamsWords = [string.Empty, string.Empty];
    private readonly string[] teamsMasks = [string.Empty, string.Empty];
    private readonly string[][] teamsChoices = [[], []];
    private readonly bool[] teamsSelected = [false, false];
    private readonly string[] teamsReveals = [string.Empty, string.Empty];
    private string teamsGuessInput = string.Empty;
    private string teamsFeedback = string.Empty;
    private bool teamsCustomRules;
    private long teamsLastTvTick = -1;
    private int teamsLastTvSecond = -1;
    private bool teamsHostAuthority;

    private bool PictionaryTeamsActive => teamsPhase != TeamsPhase.Inactive;
    private string TeamsLocalId => CurrentSession?.AccountId ?? string.Empty;
    private int TeamsLocalColour => teamsMembers.TryGetValue(TeamsLocalId, out var member) ? member.Team : -1;
    private int TeamsLocalDrawerColour
    {
        get
        {
            if (teamsPhase is not (TeamsPhase.ChoosingWords or TeamsPhase.Drawing)) return -1;
            if (string.Equals(teamsDrawers[0], TeamsLocalId, StringComparison.Ordinal)) return 0;
            return string.Equals(teamsDrawers[1], TeamsLocalId, StringComparison.Ordinal) ? 1 : -1;
        }
    }
    private bool TeamsCanGuess => teamsPhase == TeamsPhase.Drawing &&
        (TeamsLocalColour is 0 or 1) && (TeamsLocalDrawerColour < 0 ||
            PictionaryTeamsPreviewRules.DrawerCanGuess(TeamCount(TeamsLocalColour)));
    private int TeamsSecondsLeft => teamsPhaseEndsUtc == DateTime.MaxValue ? 0 :
        Math.Max(0, (int)Math.Ceiling((teamsPhaseEndsUtc - DateTime.UtcNow).TotalSeconds));

    private void OpenPictionaryTeams()
    {
        lastActivitiesPage = HomePage.PictionaryTeams;
        currentPage = HomePage.PictionaryTeams;
    }

    internal void UpdatePictionaryTeams()
    {
        if (stream.Mode == StreamMode.None)
        {
            if (!stream.IncomingPictionaryTeamsCommands.IsEmpty)
                stream.IncomingPictionaryTeamsCommands.Clear();
            if (teamsPhase != TeamsPhase.Inactive || teamsSession is not null)
                ResetPictionaryTeams();
            return;
        }
        while (stream.IncomingPictionaryTeamsCommands.TryDequeue(out var incoming))
        {
#if DEBUG
            // The production game relay correctly rejects drawer guesses. For
            // singleton Debug teams use the existing authenticated party path;
            // the host still validates session, round, team, size and rate limit.
            if (stream.Mode == StreamMode.Hosting && incoming.Command.StartsWith("G|", StringComparison.Ordinal) &&
                teamsMembers.TryGetValue(incoming.UserId, out var previewMember) &&
                PictionaryTeamsPreviewRules.DrawerCanGuess(TeamCount(previewMember.Team)))
            {
                HostHandleTeamsContribution(incoming.UserId, incoming.Command);
                continue;
            }
#endif
            if (stream.Mode == StreamMode.Hosting)
                ReceiveArcadeServerCommand("pictionary-teams", incoming.UserId, incoming.Command);
            else
                ReceiveArcadeServerCommand("pictionary-teams", incoming.UserId, incoming.Command);
        }
        UpdateActivityRelay("pictionary-teams");
        UpdateTeamsDrawerNotifications();
        if (PictionaryTeamsActive && pictionaryTvGuessInputActive && !TeamsCanGuess)
            CancelPictionaryTvGuess();

        var animateDrawers = teamsPhase == TeamsPhase.ChoosingDrawers;
        var tick = animateDrawers ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 100 : -1;
        var second = teamsPhase is TeamsPhase.ChoosingDrawers or TeamsPhase.ChoosingWords or
            TeamsPhase.Drawing or TeamsPhase.Summary ? TeamsSecondsLeft : -1;
        if (PictionaryTeamsActive && (tick != teamsLastTvTick || second != teamsLastTvSecond))
        {
            teamsLastTvTick = tick;
            teamsLastTvSecond = second;
            whiteboardTvDirty = true;
        }
        if (stream.Mode != StreamMode.Hosting || !PictionaryTeamsActive)
            return;
        if (!teamsHostAuthority || !screenController.Engine.IsPlayingWhiteboard)
        {
            StopPictionaryTeams();
            return;
        }

        var roster = stream.Roster.Select(p => p.UserId).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (TeamsLocalId.Length > 0) roster.Add(TeamsLocalId);
        var signature = string.Join(';', roster.Order(StringComparer.Ordinal));
        if (signature != teamsRosterSignature)
        {
            teamsRosterSignature = signature;
            foreach (var id in teamsMembers.Keys.Where(id => !roster.Contains(id)).ToArray())
                teamsMembers.Remove(id);
            foreach (var player in stream.Roster)
                if (roster.Contains(player.UserId) && !teamsMembers.ContainsKey(player.UserId))
                    teamsMembers[player.UserId] = new TeamsMember(player.UserId, player.DisplayName, -1, 0);
            if (TeamsLocalId.Length > 0 && !teamsMembers.ContainsKey(TeamsLocalId))
                teamsMembers[TeamsLocalId] = new TeamsMember(TeamsLocalId, LocalPictionaryDisplayName(), -1, 0);
            if (teamsPhase != TeamsPhase.Lobby &&
                (!PictionaryTeamsPreviewRules.TeamsHaveEnoughPlayers(TeamCount(0), TeamCount(1)) ||
                 (teamsPhase is TeamsPhase.ChoosingDrawers or TeamsPhase.ChoosingWords or TeamsPhase.Drawing &&
                  (!TeamsDrawerPresent(0) || !TeamsDrawerPresent(1)))))
                ReturnTeamsToLobby();
            else
                SendTeamsState();
            if (teamsPhase is TeamsPhase.ChoosingWords or TeamsPhase.Drawing)
                SendTeamsSecrets();
        }

        if (DateTime.UtcNow < teamsPhaseEndsUtc)
            return;
        switch (teamsPhase)
        {
            case TeamsPhase.ChoosingDrawers: BeginTeamsWordChoice(); break;
            case TeamsPhase.ChoosingWords: BeginTeamsDrawing(); break;
            case TeamsPhase.Drawing: EndTeamsRound(-1, string.Empty); break;
            case TeamsPhase.Summary:
                if (teamsRound >= teamsRounds)
                {
                    teamsPhase = TeamsPhase.Finished;
                    teamsPhaseEndsUtc = DateTime.MaxValue;
                    ObserveArcadeScores();
                    SendTeamsState();
                }
                else
                {
                    teamsRound++;
                    BeginTeamsDrawers();
                }
                break;
        }
    }

    private int TeamCount(int team) => teamsMembers.Values.Count(m => m.Team == team);
    private bool TeamsDrawerPresent(int team) => teamsMembers.ContainsKey(teamsDrawers[team]) ||
        teamsDrawers[team].Length == 0 && PictionaryTeamsPreviewRules.IsInactiveTeam(TeamCount(team), TeamCount(1 - team));
    private bool TeamsReady => PictionaryTeamsPreviewRules.TeamsHaveEnoughPlayers(TeamCount(0), TeamCount(1)) &&
        teamsMembers.Values.All(member => member.Team is 0 or 1);

    private void StartPictionaryTeams()
    {
        if (stream.Mode != StreamMode.Hosting || CurrentSession is null ||
            !CanLaunchActivity(ActiveActivityKind.PictionaryTeams) || stream.Roster.Length + 1 < PictionaryTeamsPreviewRules.MinimumPlayers)
            return;
        teamsSession = Guid.NewGuid().ToString("N");
        teamsPhase = TeamsPhase.Lobby;
        teamsHostAuthority = true;
        teamsPhaseEndsUtc = DateTime.MaxValue;
        teamsRound = 0;
        teamsRedScore = teamsBlueScore = 0;
        teamsMembers.Clear();
        teamsGuessRate.Clear();
        teamsMembers[TeamsLocalId] = new TeamsMember(TeamsLocalId, LocalPictionaryDisplayName(), -1, 0);
        foreach (var player in stream.Roster)
            if (player.UserId.Length > 0 && !teamsMembers.ContainsKey(player.UserId))
                teamsMembers[player.UserId] = new TeamsMember(player.UserId, player.DisplayName, -1, 0);
        teamsRosterSignature = string.Join(';', teamsMembers.Keys.Order(StringComparer.Ordinal));
        teamsRounds = teamsCustomRules ? Math.Clamp(teamsCustomRounds, 3, 20) : PictionaryStandardRoundCount;
        teamsDrawingSeconds = teamsCustomRules ? Math.Clamp(teamsCustomSeconds, 30, 120) : PictionaryStandardDrawingSeconds;
        teamsGuessPoints = teamsCustomRules ? Math.Clamp(teamsCustomPoints, 1, 10) : PictionaryStandardFirstGuessPoints;
        whiteboardBackgroundWhite = false;
        StartHostWhiteboard(WhiteboardMode.Team, ActiveActivityKind.PictionaryTeams);
        SendTeamsState();
    }

    private void SetTeamsColour(string id, int team)
    {
        if (teamsPhase != TeamsPhase.Lobby || team is < 0 or > 1 || !teamsMembers.TryGetValue(id, out var member))
            return;
        teamsMembers[id] = member with { Team = team };
        SendTeamsState();
    }

    private void ConfirmTeams()
    {
        if (stream.Mode != StreamMode.Hosting || teamsPhase != TeamsPhase.Lobby || !TeamsReady)
            return;
        teamsRound = 1;
        BeginTeamsDrawers();
    }

    private void BeginTeamsDrawers()
    {
        for (var team = 0; team < 2; team++)
        {
#if DEBUG
            if (PictionaryTeamsPreviewRules.IsInactiveTeam(TeamCount(team), TeamCount(1 - team)))
            {
                teamsDrawers[team] = teamsWords[team] = teamsReveals[team] = teamsMasks[team] = string.Empty;
                teamsChoices[team] = []; teamsSelected[team] = true;
                continue;
            }
#endif
            var candidates = teamsMembers.Values.Where(m => m.Team == team &&
                (m.Id != teamsDrawers[team] || PictionaryTeamsPreviewRules.DrawerCanGuess(TeamCount(team)))).ToArray();
            if (candidates.Length == 0) { ReturnTeamsToLobby(); return; }
            teamsDrawers[team] = candidates[Random.Shared.Next(candidates.Length)].Id;
            teamsWords[team] = teamsReveals[team] = string.Empty;
            teamsMasks[team] = string.Empty;
            teamsChoices[team] = [];
            teamsSelected[team] = false;
        }
        teamsWinningTeam = -1;
        teamsWinningGuesser = string.Empty;
        teamsPhase = TeamsPhase.ChoosingDrawers;
        teamsPhaseEndsUtc = DateTime.UtcNow + PictionaryDrawerSelectionTime;
        SendTeamsState();
    }

    private void BeginTeamsWordChoice()
    {
        var choices = PictionaryWords.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(_ => Random.Shared.Next()).Take(6).ToArray();
        teamsChoices[0] = choices[..3];
        teamsChoices[1] = choices[3..];
        teamsSelected[0] = teamsSelected[1] = false;
#if DEBUG
        for (var team = 0; team < 2; team++)
            if (PictionaryTeamsPreviewRules.IsInactiveTeam(TeamCount(team), TeamCount(1 - team)))
            { teamsChoices[team] = []; teamsSelected[team] = true; }
#endif
        teamsPhase = TeamsPhase.ChoosingWords;
        teamsPhaseEndsUtc = DateTime.UtcNow + PictionaryWordChoiceTime;
        SendTeamsState();
        SendTeamsSecrets();
        if (TeamsLocalDrawerColour >= 0)
        {
            currentPage = HomePage.PictionaryTeams;
            OpenUi();
        }
    }

    private void SendTeamsSecrets()
    {
        if (GameRelay("pictionary-teams") is not null) { PublishRemainingState("pictionary-teams"); return; }
        if (stream.Mode != StreamMode.Hosting || teamsSession is null) return;
        UpdateAdditionalArcadePoints();
        for (var team = 0; team < 2; team++)
        {
            if (string.Equals(teamsDrawers[team], TeamsLocalId, StringComparison.Ordinal)) continue;
            if (teamsPhase == TeamsPhase.ChoosingWords && teamsChoices[team].Length == 3)
                _ = stream.SendPictionaryTeamsCommandAsync($"O|{teamsSession}|{teamsRound}|{teamsDrawers[team]}|{team}|{EncodePictionaryJson(teamsChoices[team])}", teamsDrawers[team]);
            else if (teamsPhase == TeamsPhase.Drawing && teamsWords[team].Length > 0)
                _ = stream.SendPictionaryTeamsCommandAsync($"D|{teamsSession}|{teamsRound}|{teamsDrawers[team]}|{team}|{EncodePictionaryText(teamsWords[team])}", teamsDrawers[team]);
        }
    }

    private void SelectTeamsWord(int team, int index)
    {
        if (team is < 0 or > 1 || teamsPhase != TeamsPhase.ChoosingWords || teamsSelected[team] ||
            index < 0 || index >= teamsChoices[team].Length)
            return;
        teamsWords[team] = teamsChoices[team][index];
        teamsMasks[team] = BuildPictionaryMask(teamsWords[team]);
        teamsSelected[team] = true;
        SendTeamsState();
        if (teamsSelected[0] && teamsSelected[1]) BeginTeamsDrawing();
    }

    private void BeginTeamsDrawing()
    {
        if (teamsPhase != TeamsPhase.ChoosingWords) return;
        for (var team = 0; team < 2; team++)
        {
#if DEBUG
            if (PictionaryTeamsPreviewRules.IsInactiveTeam(TeamCount(team), TeamCount(1 - team))) continue;
#endif
            if (!teamsSelected[team])
                teamsWords[team] = teamsChoices[team][Random.Shared.Next(teamsChoices[team].Length)];
            teamsMasks[team] = BuildPictionaryMask(teamsWords[team]);
        }
        if (whiteboardSessionId is not null)
            SendAndApplyWhiteboardCommand($"C|{whiteboardSessionId}");
        teamsPhase = TeamsPhase.Drawing;
        teamsPhaseEndsUtc = DateTime.UtcNow.AddSeconds(teamsDrawingSeconds);
        teamsFeedback = string.Empty;
        SendTeamsState();
        SendTeamsSecrets();
    }

    private void EndTeamsRound(int winningTeam, string guesserId)
    {
        if (teamsPhase != TeamsPhase.Drawing) return;
        FinishWhiteboardStroke();
        teamsWinningTeam = winningTeam;
        teamsWinningGuesser = guesserId;
        if (winningTeam == 0) teamsRedScore += teamsGuessPoints;
        if (winningTeam == 1) teamsBlueScore += teamsGuessPoints;
        if (winningTeam is 0 or 1 && teamsMembers.TryGetValue(guesserId, out var guesser))
            teamsMembers[guesserId] = guesser with { Correct = guesser.Correct + 1 };
        teamsReveals[0] = teamsWords[0];
        teamsReveals[1] = teamsWords[1];
        teamsPhase = TeamsPhase.Summary;
        teamsPhaseEndsUtc = DateTime.UtcNow + PictionarySummaryTime;
        SendTeamsState();
    }

    private void ReturnTeamsToLobby()
    {
        if (whiteboardSessionId is not null && stream.Mode == StreamMode.Hosting)
            SendAndApplyWhiteboardCommand($"C|{whiteboardSessionId}");
        teamsPhase = TeamsPhase.Lobby;
        teamsWinningTeam = -1;
        teamsWinningGuesser = string.Empty;
        teamsPhaseEndsUtc = DateTime.MaxValue;
        teamsRound = 0;
        teamsDrawers[0] = teamsDrawers[1] = string.Empty;
        teamsWords[0] = teamsWords[1] = string.Empty;
        teamsMasks[0] = teamsMasks[1] = string.Empty;
        SendTeamsState();
    }

    private void StopPictionaryTeams()
    {
        if (stream.Mode == StreamMode.Hosting && teamsSession is not null)
            _ = stream.SendPictionaryTeamsCommandAsync($"X|{teamsSession}");
        if (stream.Mode == StreamMode.Hosting && whiteboardSessionId is not null)
            SendAndApplyWhiteboardCommand($"X|{whiteboardSessionId}");
        ResetPictionaryTeams();
    }

    private void ResetPictionaryTeams()
    {
        CaptureArcadeScoresBeforeReset();
        ResetRemainingRelay("pictionary-teams");
        teamsPhase = TeamsPhase.Inactive;
        teamsSession = null;
        teamsWinningTeam = -1;
        teamsWinningGuesser = string.Empty;
        teamsMembers.Clear();
        teamsGuessRate.Clear();
        teamsDrawerNotifications.Clear();
        teamsHostAuthority = false;
        teamsRosterSignature = string.Empty;
        teamsGuessInput = teamsFeedback = string.Empty;
        teamsChoices[0] = teamsChoices[1] = [];
        teamsSelected[0] = teamsSelected[1] = false;
        teamsWords[0] = teamsWords[1] = string.Empty;
        teamsMasks[0] = teamsMasks[1] = string.Empty;
        teamsDrawers[0] = teamsDrawers[1] = string.Empty;
        pictionaryTvGuessInputActive = false;
        pictionaryTvGuessInput = string.Empty;
        teamsLastTvTick = -1;
        teamsLastTvSecond = -1;
    }

    private void SendTeamsState()
    {
        if (stream.Mode != StreamMode.Hosting || teamsSession is null) return;
        UpdateAdditionalArcadePoints();
        var snapshot = new TeamsSnapshot(teamsSession, teamsPhase, teamsRound, teamsRounds,
            teamsPhaseEndsUtc == DateTime.MaxValue ? 0 : new DateTimeOffset(teamsPhaseEndsUtc).ToUnixTimeMilliseconds(),
            teamsDrawingSeconds, teamsGuessPoints, teamsRedScore, teamsBlueScore,
            teamsDrawers[0], teamsDrawers[1], teamsMasks[0], teamsMasks[1],
            teamsReveals[0], teamsReveals[1], teamsMembers.Values.ToArray(), teamsWinningTeam, teamsWinningGuesser);
        if (!TrySendRemainingState("pictionary-teams", "S|" + EncodePictionaryJson(snapshot))) _ = stream.SendPictionaryTeamsCommandAsync("S|" + EncodePictionaryJson(snapshot));
        whiteboardTvDirty = true;
    }

    private void ApplyTeamsCommand(string command)
    {
        var parts = command.Split('|', 7);
        if (parts.Length == 2 && parts[0] == "S")
        {
            var state = DecodePictionaryJson<TeamsSnapshot>(parts[1]);
            if (state is null) return;
            var previousPhase = teamsPhase;
            var changedRound = teamsSession != state.Session || teamsRound != state.Round;
            teamsSession = state.Session;
            teamsPhase = state.Phase;
            if (teamsPhase != TeamsPhase.Drawing)
            {
                CancelPictionaryTvDrawing();
                pictionaryTvGuessInputActive = false;
                pictionaryTvGuessInput = string.Empty;
            }
            teamsRound = state.Round;
            teamsRounds = state.Rounds;
            teamsDrawingSeconds = state.Seconds;
            teamsGuessPoints = state.GuessPoints;
            teamsPhaseEndsUtc = state.Ends == 0 ? DateTime.MaxValue : DateTimeOffset.FromUnixTimeMilliseconds(state.Ends).UtcDateTime;
            teamsRedScore = state.RedScore;
            teamsBlueScore = state.BlueScore;
            teamsWinningTeam = state.WinningTeam is 0 or 1 ? state.WinningTeam : -1;
            teamsWinningGuesser = state.GuesserId ?? string.Empty;
            teamsDrawers[0] = state.RedDrawer;
            teamsDrawers[1] = state.BlueDrawer;
            teamsMasks[0] = state.RedMask;
            teamsMasks[1] = state.BlueMask;
            teamsReveals[0] = state.RedReveal;
            teamsReveals[1] = state.BlueReveal;
            teamsMembers.Clear();
            foreach (var member in state.Members) teamsMembers[member.Id] = member;
            UpdateAdditionalArcadePoints();
            ObserveArcadeScores();
            if (changedRound)
            {
                teamsGuessInput = teamsFeedback = string.Empty;
                teamsWords[0] = teamsWords[1] = string.Empty;
                teamsChoices[0] = teamsChoices[1] = [];
                teamsSelected[0] = teamsSelected[1] = false;
                pictionaryTvGuessInputActive = false;
                pictionaryTvGuessInput = string.Empty;
            }
            if (TeamsLocalDrawerColour < 0)
                teamsWords[0] = teamsWords[1] = string.Empty;
            if (teamsPhase == TeamsPhase.ChoosingWords)
            { teamsSelected[0] = teamsMasks[0].Length > 0; teamsSelected[1] = teamsMasks[1].Length > 0; }
            if (stream.Mode == StreamMode.Viewing && TeamsLocalDrawerColour >= 0 &&
                     teamsPhase == TeamsPhase.ChoosingWords && previousPhase != TeamsPhase.ChoosingWords)
            {
                viewerActivityPrompt = ViewerActivityPrompt.None;
                lastActivitiesPage = HomePage.PictionaryTeams;
                currentPage = HomePage.PictionaryTeams;
                OpenUi();
            }
            whiteboardTvDirty = true;
            return;
        }
        if (parts.Length == 6 && parts[0] == "O" && parts[1] == teamsSession &&
            int.TryParse(parts[2], out var round) && round == teamsRound && parts[3] == TeamsLocalId &&
            int.TryParse(parts[4], out var team) && team is 0 or 1)
        {
            teamsChoices[team] = DecodePictionaryJson<string[]>(parts[5]) ?? [];
            whiteboardTvDirty = true;
            return;
        }
        if (parts.Length == 6 && parts[0] == "D" && parts[1] == teamsSession &&
            int.TryParse(parts[2], out var round2) && round2 == teamsRound && parts[3] == TeamsLocalId &&
            int.TryParse(parts[4], out var team2) && team2 is 0 or 1)
        {
            teamsWords[team2] = DecodePictionaryText(parts[5]);
            whiteboardTvDirty = true;
            return;
        }
        if (parts.Length == 7 && parts[0] == "N" && parts[1] == teamsSession &&
            int.TryParse(parts[2], out var noticeRound) && noticeRound == teamsRound &&
            parts[3] == TeamsLocalId && Guid.TryParse(parts[4], out var noticeId) &&
            parts[5] is "0" or "1")
        {
            UpdateTeamsDrawerNotifications();
            if (teamsDrawerNotifications.Enqueue(parts[1], noticeRound, parts[3], noticeId,
                new(DecodePictionaryText(parts[6]), parts[5] == "1"), DateTime.UtcNow)) whiteboardTvDirty = true;
            return;
        }
        if (parts.Length == 3 && parts[0] == "F" && parts[1] == TeamsLocalId)
        {
            teamsFeedback = DecodePictionaryText(parts[2]);
            whiteboardTvDirty = true;
            return;
        }
        if (parts.Length == 2 && parts[0] == "X" && parts[1] == teamsSession)
            ResetPictionaryTeams();
    }

    private void HostHandleTeamsContribution(string senderId, string command)
    {
        var parts = command.Split('|', 5);
        if (teamsSession is null || parts.Length < 3 || parts[1] != teamsSession ||
            !teamsMembers.ContainsKey(senderId)) return;
        if (parts[0] == "T" && parts.Length == 3 && int.TryParse(parts[2], out var team))
        {
            SetTeamsColour(senderId, team);
            return;
        }
        if (parts.Length != 5 || !int.TryParse(parts[2], out var round) || round != teamsRound ||
            !int.TryParse(parts[3], out var colour) || colour is < 0 or > 1) return;
        if (parts[0] == "W" && senderId == teamsDrawers[colour] && int.TryParse(parts[4], out var index))
            SelectTeamsWord(colour, index);
        else if (parts[0] == "G")
            HostHandleTeamsGuess(senderId, colour, DecodePictionaryText(parts[4]));
    }

    private void HostHandleTeamsGuess(string senderId, int team, string guess)
    {
        if (teamsPhase != TeamsPhase.Drawing || !teamsMembers.TryGetValue(senderId, out var member) ||
            member.Team != team || (senderId == teamsDrawers[team] &&
            !PictionaryTeamsPreviewRules.DrawerCanGuess(TeamCount(team))) || guess.Length is < 1 or > 80) return;
        var now = DateTime.UtcNow;
        if (teamsGuessRate.TryGetValue(senderId, out var last) && now - last < TimeSpan.FromMilliseconds(500)) return;
        teamsGuessRate[senderId] = now;
        var correct = NormalizePictionaryAnswer(guess) == NormalizePictionaryAnswer(teamsWords[team]);
        var noticeText = correct ? $"{member.Name} guessed correctly!" : $"{member.Name} made an incorrect guess";
        var notice = $"N|{teamsSession}|{teamsRound}|{teamsDrawers[team]}|{Guid.NewGuid():N}|{(correct ? 1 : 0)}|{EncodePictionaryText(noticeText)}";
        ApplyTeamsCommand(notice);
        if (!SendRemainingEvent("pictionary-teams", notice)) _ = stream.SendPictionaryTeamsCommandAsync(notice);
        if (correct)
        {
            EndTeamsRound(team, senderId);
        }
        else if (senderId != TeamsLocalId)
            SendTeamsFeedback(senderId);
        else
        {
            teamsFeedback = "Try again!";
            whiteboardTvDirty = true;
        }
    }

    private void DrawPictionaryTeamsPage()
    {
        lastActivitiesPage = HomePage.PictionaryTeams;
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(10f, 9f));
        if (!PictionaryTeamsActive)
        {
            DrawPictionaryTeamsSetupPage();
            return;
        }

        DrawTeamsRoundStatus();

        if (teamsPhase == TeamsPhase.Lobby)
        {
            ImGui.TextWrapped("Everyone picks red or blue. Teams may be different sizes, but each needs at least two players.");
            var rosterWidth = (ImGui.GetContentRegionAvail().X - Ui(10f)) * 0.5f;
            DrawTeamsRoster(0, rosterWidth);
            ImGui.SameLine();
            DrawTeamsRoster(1, rosterWidth);
            var waiting = teamsMembers.Values.Where(member => member.Team < 0).Select(member => member.Name).ToArray();
            if (waiting.Length > 0)
                ImGui.TextDisabled($"Waiting to pick a team: {string.Join(", ", waiting)}");
            var localTeam = TeamsLocalColour;
            if (localTeam != 0 && ImGui.Button("Join red team", UiVec(160f, 38f))) SubmitTeamsJoin(0);
            if (localTeam != 1)
            {
                if (localTeam != 0) ImGui.SameLine();
                if (ImGui.Button("Join blue team", UiVec(160f, 38f))) SubmitTeamsJoin(1);
            }
            ImGui.TextDisabled($"Red: {TeamCount(0)} players  •  Blue: {TeamCount(1)} players");
            if (stream.Mode == StreamMode.Hosting)
            {
                var ready = TeamsReady;
                if (!ready) UiDisabled.Begin();
                if (ImGui.Button("Confirm teams", UiVec(220f, 42f))) ConfirmTeams();
                if (!ready) UiDisabled.End();
            }
            return;
        }

        var available = ImGui.GetContentRegionAvail().X;
        var stack = available < Ui(900f);
        var gap = Ui(12f);
        var sideWidth = stack ? available : Math.Clamp(available * .30f, Ui(250f), Ui(310f));
        DrawTeamsCanvas(stack ? available : available - sideWidth - gap, Ui(550f));
        if (!stack) ImGui.SameLine(0f, gap);
        DrawTeamsSidePanel(sideWidth, stack ? Ui(400f) : Ui(550f));
        DrawPictionaryFooter(teams: true);
        if (teamsPhase == TeamsPhase.Finished)
        {
            var winner = teamsRedScore == teamsBlueScore ? "It's a tie!" :
                teamsRedScore > teamsBlueScore ? "Red team wins!" : "Blue team wins!";
            ImGui.TextColored(AccentHover, winner);
            for (var team = 0; team < 2; team++)
            {
                var best = BestTeamsGuessers(team);
                ImGui.Text($"{(team == 0 ? "Red" : "Blue")} best guesser: {best}");
            }
            if (stream.Mode == StreamMode.Hosting && ImGui.Button("Play again##teams", UiVec(180f, 38f)))
                StartTeamsNewCycle();
        }
    }

    private void DrawTeamsRoster(int team, float width)
    {
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild($"##teamRoster{team}", new Vector2(width, Ui(170f)), true)))
        {
            ImGui.TextColored(team == 0 ? Hex(0xFF6575) : Hex(0x72B9FF), team == 0 ? "RED TEAM" : "BLUE TEAM");
            foreach (var member in teamsMembers.Values.Where(m => m.Team == team).OrderBy(m => m.Name))
                DrawUsername(member.Id,member.Name);
        }
        ImGui.EndChild();
    }

    private void SubmitTeamsJoin(int team)
    {
        if (stream.Mode == StreamMode.Hosting) SetTeamsColour(TeamsLocalId, team);
        else if (teamsSession is not null)
            SendArcadePlayerAction("pictionary-teams", $"T|{teamsSession}|{team}");
    }

    private void SubmitTeamsGuess()
    {
        var answer = teamsGuessInput.Trim();
        teamsGuessInput = string.Empty;
        SubmitTeamsGuess(answer);
    }

    private void SubmitTeamsGuess(string answer)
    {
        var team = TeamsLocalColour;
        teamsFeedback = string.Empty;
        if (!TeamsCanGuess || team is < 0 or > 1 || teamsSession is null || string.IsNullOrWhiteSpace(answer) || answer.Length > 80) return;
        if (stream.Mode == StreamMode.Hosting) HostHandleTeamsGuess(TeamsLocalId, team, answer);
        else
        {
            var command = $"G|{teamsSession}|{teamsRound}|{team}|{EncodePictionaryText(answer)}";
#if DEBUG
            if (PictionaryTeamsPreviewRules.DrawerCanGuess(TeamCount(team)))
            {
                _ = stream.SendPictionaryTeamsContributionAsync(command);
                return;
            }
#endif
            SendArcadePlayerAction("pictionary-teams", command);
        }
    }

    private void SubmitTeamsWord(int team, int index)
    {
        if (teamsSession is null || team is < 0 or > 1 || TeamsLocalDrawerColour != team ||
            teamsPhase != TeamsPhase.ChoosingWords || teamsSelected[team] || index < 0 || index >= teamsChoices[team].Length) return;
        if (stream.Mode == StreamMode.Hosting) SelectTeamsWord(team, index);
        else SendArcadePlayerAction("pictionary-teams", $"W|{teamsSession}|{teamsRound}|{team}|{index}");
        teamsSelected[team] = true;
        whiteboardTvDirty = true;
    }

    private void StartTeamsNewCycle()
    {
        if (stream.Mode != StreamMode.Hosting || teamsPhase != TeamsPhase.Finished) return;
        teamsRedScore = teamsBlueScore = 0;
        teamsRound = 0;
        foreach (var member in teamsMembers.Values.ToArray())
            teamsMembers[member.Id] = member with { Team = -1, Correct = 0 };
        ReturnTeamsToLobby();
    }

    private void DrawTeamsCanvas(float width, float height)
    {
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f)).Push(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##teamsCanvas", new Vector2(width, height), true, ImGuiWindowFlags.NoScrollbar)))
        {
            ImGui.EndChild();
            return;
        }
        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetWindowPos();
        var canDraw = teamsPhase == TeamsPhase.Drawing && TeamsLocalDrawerColour is 0 or 1;
        DrawPictionaryPill(draw, origin + UiVec(14f, 13f), canDraw ? "DRAW THE WORD" : "GUESS THE DRAWING", Accent, Ui(172f));
        var instruction = canDraw ? $"Your word: {teamsWords[TeamsLocalDrawerColour]}" : "Guess your team's word first to win the round.";
        draw.PushClipRect(origin + UiVec(196f, 12f), origin + new Vector2(width - Ui(14f), Ui(48f)), true);
        draw.AddText(origin + UiVec(198f, 23f), ImGui.GetColorU32(AccentHover), instruction);
        draw.PopClipRect();
        DrawPictionaryDrawingControls(draw, origin, width, canDraw, teams: true);
        var min = origin + UiVec(14f, 169f);
        var max = origin + new Vector2(width - Ui(14f), height - Ui(14f));
        var size = max - min;
        draw.AddRectFilled(min, max, ImGui.GetColorU32(Hex(0x090E1B)));
        var mid = (min.X + max.X) * 0.5f;
        draw.AddRectFilled(min, new Vector2(mid, min.Y + Ui(37f)), ImGui.GetColorU32(Hex(0x512631)));
        draw.AddRectFilled(new Vector2(mid, min.Y), new Vector2(max.X, min.Y + Ui(37f)), ImGui.GetColorU32(Hex(0x263F65)));
        for (var team = 0; team < 2; team++)
        {
            var left = team == 0 ? min.X : mid;
            var right = team == 0 ? mid : max.X;
            draw.PushClipRect(new Vector2(left, min.Y), new Vector2(right, min.Y + Ui(37f)), true);
            var word = teamsPhase == TeamsPhase.Drawing ? TeamsLocalDrawerColour == team ? teamsWords[team] : teamsMasks[team] : string.Empty;
            DrawUsernameAt(draw, new Vector2(left + Ui(10f), min.Y + Ui(10f)),
                $"{(team == 0 ? "RED" : "BLUE")} � {TeamDrawerName(team)}  {word}", Vector4.One);
            draw.PopClipRect();
        }

        draw.PushClipRect(min + UiVec(0f, 37f), max - UiVec(0f, 52f), true);
        foreach (var stroke in VisibleWhiteboardStrokes)
        {
            var colour = ImGui.GetColorU32(WhiteboardColours[stroke.ColourIndex].Value);
            for (var i = 1; i < stroke.Points.Count; i++)
                draw.AddLine(min + stroke.Points[i - 1] * size, min + stroke.Points[i] * size,
                    colour, Math.Max(1f, Ui(stroke.Width) * size.Y / WhiteboardRenderer.Height));
        }
        draw.PopClipRect();
        draw.AddLine(new Vector2(mid, min.Y), new Vector2(mid, max.Y), ImGui.GetColorU32(AccentHover), Ui(3f));

        for (var team = 0; team < 2; team++)
        {
            var halfMin = new Vector2(team == 0 ? min.X : mid, min.Y + Ui(44f));
            var halfMax = new Vector2(team == 0 ? mid : max.X, max.Y - Ui(52f));
            var center = (halfMin + halfMax) * 0.5f;
            string? message = teamsPhase switch
            {
                TeamsPhase.ChoosingDrawers => AnimatedTeamDrawer(team),
                TeamsPhase.ChoosingWords when teamsSelected[team] => "Waiting for the other drawer",
                TeamsPhase.ChoosingWords when TeamsLocalDrawerColour != team => $"{TeamDrawerName(team)} is choosing a word",
                TeamsPhase.Summary => $"WORD: {teamsReveals[team]}",
                TeamsPhase.Finished => teamsRedScore == teamsBlueScore ? "DRAW" :
                    (teamsRedScore > teamsBlueScore) == (team == 0) ? "WINNER" : string.Empty,
                _ => null,
            };
            ImGui.PushID(team);
            draw.PushClipRect(halfMin, halfMax, true);
            if (!string.IsNullOrEmpty(message))
            {
                var heading = teamsPhase == TeamsPhase.ChoosingDrawers ? "CHOOSING THE NEXT DRAWER..." :
                    teamsPhase == TeamsPhase.Summary ? "THE ANSWER WAS" :
                    teamsPhase == TeamsPhase.Finished ? "FINAL RESULTS" : "CHOOSING WORD";
                DrawPictionaryCenteredMessage(draw, halfMin, halfMax, heading,
                    teamsPhase == TeamsPhase.Summary ? teamsReveals[team].ToUpperInvariant() : message);
            }
            if (teamsPhase == TeamsPhase.ChoosingWords && TeamsLocalDrawerColour == team && !teamsSelected[team])
            {
                var choices = teamsChoices[team];
                for (var i = 0; i < choices.Length; i++)
                {
                    var buttonSize = new Vector2(Math.Min(Ui(210f), halfMax.X - halfMin.X - Ui(24f)), Ui(40f));
                    var at = new Vector2(center.X - buttonSize.X * 0.5f, center.Y - Ui(65f) + i * Ui(48f));
                    ImGui.SetCursorScreenPos(at);
                    if (ImGui.Button($"{choices[i]}##teamWord{team}_{i}", buttonSize)) SubmitTeamsWord(team, i);
                }
            }
            draw.PopClipRect();
            ImGui.PopID();
            var footer = teamsPhase == TeamsPhase.Drawing
                ? TeamsLocalColour == team && TeamsCanGuess ? string.Empty :
                    TeamsLocalDrawerColour == team ? $"DRAW: {teamsWords[team]}" : "WATCH THE DRAWING"
                : teamsPhase == TeamsPhase.Summary ? teamsWinningTeam == team ? $"+{teamsGuessPoints} points" : "No points this round" : string.Empty;
            var footMin = new Vector2(halfMin.X, max.Y - Ui(50f));
            if (footer.Length > 0)
            {
                draw.AddRectFilled(footMin, new Vector2(halfMax.X, max.Y), ImGui.GetColorU32(Hex(0x151B2C)));
                var measure = ImGui.CalcTextSize(footer);
                draw.AddText(new Vector2(center.X - measure.X * 0.5f, footMin.Y + Ui(17f)), ImGui.GetColorU32(AccentHover), footer);
            }
        }

        if (canDraw)
        {
            var team = TeamsLocalDrawerColour;
            var left = team == 0 ? min.X : mid;
            var right = team == 0 ? mid : max.X;
            var drawMin = new Vector2(left + Ui(2f), min.Y + Ui(39f));
            var drawMax = new Vector2(right - Ui(2f), min.Y + size.Y * (530f / 720f));
            ImGui.SetCursorScreenPos(drawMin);
            ImGui.InvisibleButton("##teamDrawingTarget", drawMax - drawMin, ImGuiButtonFlags.MouseButtonLeft);
            var hovered = ImGui.IsItemHovered();
            if (hovered && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var mouse = ImGui.GetMousePos();
                var point = new Vector2(Math.Clamp((mouse.X - min.X) / size.X, team == 0 ? 0.02f : 0.52f,
                    team == 0 ? 0.48f : 0.98f), Math.Clamp((mouse.Y - min.Y) / size.Y, 0.085f, 530f / 720f));
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    BeginWhiteboardStroke(point);
                }
                else if (whiteboardDrawingStrokeId is not null)
                {
                    ContinueWhiteboardStroke(point);
                }
            }
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) FinishWhiteboardStroke();
        }
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, height));
        ImGui.EndChild();
    }

    private string TeamDrawerName(int team)
    {
        return teamsMembers.TryGetValue(teamsDrawers[team], out var member) ? member.Name : "Waiting for drawer";
    }

    private string BestTeamsGuessers(int team)
    {
        var members = teamsMembers.Values.Where(member => member.Team == team).ToArray();
        var max = members.Select(member => member.Correct).DefaultIfEmpty(0).Max();
        return max == 0 ? "No correct guesses" :
            $"{string.Join(", ", members.Where(member => member.Correct == max).Select(member => member.Name))} ({max} correct)";
    }

    private string AnimatedTeamDrawer(int team)
    {
        var candidates = teamsMembers.Values.Where(m => m.Team == team).OrderBy(m => m.Id).ToArray();
        if (candidates.Length == 0) return "Waiting for players";
        if (TeamsSecondsLeft <= 1) return TeamDrawerName(team);
        var frame = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 100);
        return candidates[Math.Abs(frame % candidates.Length)].Name;
    }

    private void ApplyPictionaryTeamsTvOverlay(byte[] pixels, int width, int height)
    {
        FillPictionaryPixels(pixels, width, height, 0, 0, width / 2, 48, 0x512631);
        FillPictionaryPixels(pixels, width, height, width / 2, 0, width / 2, 48, 0x263F65);
        DrawPictionaryPixelTextCentered(pixels, width, height, width / 4, 9,
            $"RED TEAM  {teamsRedScore}", 3, 0xFFFFFF);
        DrawPictionaryPixelTextCentered(pixels, width, height, width * 3 / 4, 9,
            $"BLUE TEAM  {teamsBlueScore}", 3, 0xFFFFFF);
        FillPictionaryPixels(pixels, width, height, width / 2 - 2, 0, 4, height, 0xD5B764);
        FillPictionaryPixels(pixels, width, height, 0, height - 106, width, 106, 0x151B2C);
        FillPictionaryPixels(pixels, width, height, width / 2 - 2, height - 106, 4, 106, 0xD5B764);

        if (teamsPhase == TeamsPhase.Lobby)
        {
            DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 58,
                "CHOOSE YOUR TEAM", 4, 0xF4DC9A);
            for (var team = 0; team < 2; team++)
            {
                var members = teamsMembers.Values.Where(m => m.Team == team).OrderBy(m => m.Name).ToArray();
                var centerX = team == 0 ? width / 4 : width * 3 / 4;
                for (var index = 0; index < Math.Min(12, members.Length); index++)
                    DrawPictionaryPixelTextCentered(pixels, width, height, centerX,
                        125 + index * 31, members[index].Name.ToUpperInvariant(), 2, 0xFFFFFF);
                var joinX = team == 0 ? 100 : 740;
                FillQuizTvPanel(pixels, width, height, joinX, height - 96, 440, 62,
                    0x242337u, pictionaryTvHoveredTarget == (team == 0 ? TeamsTvJoinRedTarget : TeamsTvJoinBlueTarget)
                        ? (team == 0 ? 0x8B3F4Bu : 0x315F8Bu)
                        : (team == 0 ? 0x512631u : 0x263F65u));
                DrawPictionaryPixelTextCentered(pixels, width, height, centerX, height - 77,
                    team == 0 ? "JOIN RED" : "JOIN BLUE", 3, team == 0 ? 0xFF8A91u : 0x81C7FFu);
            }
            if (stream.Mode == StreamMode.Hosting && TeamsReady)
            {
                FillPictionaryPixels(pixels, width, height, 470, 500, 340, 52, 0x563D78);
                DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 514,
                    "CONFIRM TEAMS", 3, 0xFFFFFF);
            }
            DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, height - 30,
                $"RED {TeamCount(0)}  BLUE {TeamCount(1)}  WAITING {teamsMembers.Values.Count(m => m.Team < 0)}", 2, 0xD5B764);
            return;
        }

        var phaseText = teamsPhase == TeamsPhase.Finished ? "FINAL SCORE" :
            $"ROUND {teamsRound} / {teamsRounds}   {TeamsSecondsLeft:00} SEC";
        DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 55,
            phaseText, 2, 0xF4DC9A);
        if (TeamsCanPlayAgainOnTv)
        {
            FillQuizTvPanel(pixels, width, height, 480, 540, 320, 56, 0x38235Fu,
                pictionaryTvHoveredTarget == TeamsTvPlayAgainTarget ? 0x512E8Fu : 0x8C55F6u);
            DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 558, "PLAY AGAIN", 3, 0xF5F7FC);
        }
        for (var team = 0; team < 2; team++)
        {
            var centerX = team == 0 ? width / 4 : width * 3 / 4;
            var accent = team == 0 ? 0xFF8A91u : 0x81C7FFu;
            if (teamsPhase == TeamsPhase.ChoosingDrawers)
            {
                DrawPictionaryPixelTextCentered(pixels, width, height, centerX, 245,
                    "CHOOSING DRAWER", 3, accent);
                DrawPictionaryPixelTextCentered(pixels, width, height, centerX, 297,
                    AnimatedTeamDrawer(team).ToUpperInvariant(), 3, 0xFFFFFF);
            }
            else if (teamsPhase == TeamsPhase.ChoosingWords)
            {
                if (TeamsLocalDrawerColour == team && TeamsCanChooseTvWord)
                {
                    DrawPictionaryPixelTextCentered(pixels, width, height, centerX, 188, "CHOOSE A WORD", 3, 0xF5F7FC);
                    for (var i = 0; i < 3; i++)
                    {
                        var left = team == 0 ? 120 : 760;
                        var top = 240 + i * 80;
                        FillQuizTvPanel(pixels, width, height, left, top, 400, 60, 0x252D48u,
                            pictionaryTvHoveredTarget == TeamsTvWordChoiceTargetStart + i ? 0x512E8Fu : 0x38235Fu);
                        var choice = teamsChoices[team][i].ToUpperInvariant();
                        DrawPictionaryPixelTextCentered(pixels, width, height, centerX, top + 20,
                            choice, PictionaryTvTextScaleToFit(choice, 360, 3), 0xF5F7FC);
                    }
                }
                else
                {
                    var title = teamsSelected[team] ? "WORD CHOSEN" : $"{TeamDrawerName(team).ToUpperInvariant()} CHOOSES";
                    DrawPictionaryPixelTextCentered(pixels, width, height, centerX, 245,
                        title, PictionaryTvTextScaleToFit(title, width / 2 - 40, 3), accent);
                    DrawPictionaryPixelTextCentered(pixels, width, height, centerX, 283,
                        teamsSelected[team] ? "WAITING FOR THE OTHER DRAWER" : "THREE WORDS TO CHOOSE FROM", 2, 0xFFFFFF);
                }
            }
            else if (teamsPhase == TeamsPhase.Drawing)
            {
                var visibleWord = TeamsLocalDrawerColour == team ? teamsWords[team] : teamsMasks[team];
                if (visibleWord.Length > 0)
                    DrawPictionaryPixelTextCentered(pixels, width, height, centerX, 84,
                        visibleWord.ToUpperInvariant(), PictionaryTvTextScaleToFit(visibleWord, width / 2 - 40, 3), accent);
                if (TeamsCanGuess && TeamsLocalColour == team)
                {
                    if (pictionaryTvGuessInputActive)
                    {
                        FillPictionaryPixels(pixels, width, height, centerX - 280, 116, 560, 62, 0x11172A);
                        var letters = BuildPictionaryTvGuessLetters(teamsMasks[team], pictionaryTvGuessInput);
                        DrawPictionaryPixelTextCentered(pixels, width, height, centerX, 130,
                            letters, PictionaryTvTextScaleToFit(letters, 520, 3), 0xF5F7FC);
                        FillQuizTvPanel(pixels, width, height, centerX - 160, 540, 145, 56, 0x38235Fu,
                            pictionaryTvHoveredTarget == PictionaryTvGuessSubmitTarget ? 0x512E8Fu : 0x8C55F6u);
                        FillQuizTvPanel(pixels, width, height, centerX + 15, 540, 145, 56, 0x252D48u, 0x343D5Du);
                        DrawPictionaryPixelTextCentered(pixels, width, height, centerX - 88, 558, "SUBMIT", 2, 0xF5F7FC);
                        DrawPictionaryPixelTextCentered(pixels, width, height, centerX + 87, 558, "CANCEL", 2, 0xF5F7FC);
                    }
                    else
                    {
                        FillQuizTvPanel(pixels, width, height, centerX - 130, 540, 260, 56, 0x38235Fu,
                            pictionaryTvHoveredTarget == PictionaryTvGuessStartTarget ? 0x512E8Fu : 0x8C55F6u);
                        DrawPictionaryPixelTextCentered(pixels, width, height, centerX, 558, "CLICK TO GUESS", 2, 0xF5F7FC);
                    }
                    if (teamsFeedback.Length > 0)
                        DrawPictionaryPixelTextCentered(pixels, width, height, centerX, 504, "INCORRECT - TRY AGAIN", 2, 0xFF5964);
                }
            }
            else if (teamsPhase == TeamsPhase.Summary)
            {
                DrawPictionaryPixelTextCentered(pixels, width, height, centerX, 220, "THE ANSWER WAS", 3, 0xAAB2CA);
                var answer = teamsReveals[team].ToUpperInvariant();
                DrawPictionaryPixelTextCentered(pixels, width, height, centerX, 270,
                    answer, PictionaryTvTextScaleToFit(answer, width / 2 - 40, 4), accent);
                DrawPictionaryPixelTextCentered(pixels, width, height, centerX, 480,
                    teamsRound >= teamsRounds ? $"FINAL RESULTS IN {TeamsSecondsLeft} SECONDS" : $"NEXT ROUND IN {TeamsSecondsLeft} SECONDS", 2, 0xAAB2CA);
                var result = teamsWinningTeam < 0 ? "NO CORRECT GUESSES" : teamsWinningTeam == team ? $"+{teamsGuessPoints} POINTS" : "NO POINTS THIS ROUND";
                DrawPictionaryPixelTextCentered(pixels, width, height, centerX, 340, result, 3, teamsWinningTeam == team ? 0x62E6A7u : 0xAAB2CAu);
                if (teamsWinningTeam == team && teamsMembers.TryGetValue(teamsWinningGuesser, out var winner))
                {
                    var name = winner.Name.ToUpperInvariant();
                    DrawPictionaryPixelTextCentered(pixels, width, height, centerX, 390, name,
                        PictionaryTvTextScaleToFit(name, width / 2 - 40, 3), 0xFFFFFF);
                }
            }
            else if (teamsPhase == TeamsPhase.Finished)
            {
                var winning = teamsRedScore == teamsBlueScore ||
                    (teamsRedScore > teamsBlueScore) == (team == 0);
                DrawPictionaryPixelTextCentered(pixels, width, height, centerX, 260,
                    teamsRedScore == teamsBlueScore ? "TIE GAME" : winning ? "WINNER" : "GOOD GAME", 4, accent);
                var bestLabel = $"BEST: {BestTeamsGuessers(team).ToUpperInvariant()}";
                if (bestLabel.Length > 32) bestLabel = bestLabel[..29] + "...";
                DrawPictionaryPixelTextCentered(pixels, width, height, centerX, 325,
                    bestLabel, 2, 0xFFFFFF);
            }
        }

    }
}

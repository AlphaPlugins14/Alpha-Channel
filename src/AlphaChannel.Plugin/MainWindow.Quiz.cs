using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlphaChannel.Plugin.Net;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private enum QuizPhase { Inactive, Countdown, Question, Reveal, Finished }

    private sealed record QuizQuestion(string Category, string Text, string[] Answers, int CorrectIndex);
    private sealed record QuizPlayer(string Id, string Name, int Score, bool? AnswerCorrect = null);
    private sealed record QuizSnapshot(
        string SessionId, QuizPhase Phase, int QuestionNumber, int QuestionCount,
        long PhaseEndsUnixMs, string Category, string Question, string[] Answers,
        int CorrectIndex, string WinnerName, string[] JoinedIds, string[] AnsweredIds,
        QuizPlayer[] Players, bool FirstCorrectEnds, bool Paused, bool TimedOut,
        int QuestionSeconds, int Revision);

    private sealed class OpenTriviaResponse
    {
        [JsonPropertyName("response_code")] public int ResponseCode { get; set; }
        [JsonPropertyName("results")] public OpenTriviaQuestion[] Results { get; set; } = [];
    }

    private sealed class OpenTriviaQuestion
    {
        [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;
        [JsonPropertyName("question")] public string Question { get; set; } = string.Empty;
        [JsonPropertyName("correct_answer")] public string CorrectAnswer { get; set; } = string.Empty;
        [JsonPropertyName("incorrect_answers")] public string[] IncorrectAnswers { get; set; } = [];
    }

    private static readonly (string Name, int Id)[] QuizCategories =
    [
        ("Mixed", 0), ("General Knowledge", 9), ("Books", 10), ("Film", 11),
        ("Music", 12), ("Television", 14), ("Video Games", 15),
        ("Science & Nature", 17), ("Computers", 18), ("Mythology", 20),
        ("Sports", 21), ("Geography", 22), ("History", 23), ("Animals", 27),
        ("Anime & Manga", 31),
    ];
    private static readonly string[] QuizDifficulties = ["Any", "Easy", "Medium", "Hard"];
    private static readonly string[] QuizQuestionTypes = ["Multiple choice", "True / False"];
    private static readonly string[] QuizEndRules = ["Everyone answered or timer ends", "First correct answer"];
    private static readonly string[] QuizScoringOptions = ["Speed bonus", "No speed bonus"];

    private readonly List<QuizQuestion> quizQuestions = [];
    private readonly Dictionary<string, string> quizPlayerNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> quizScores = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> quizAnswers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> quizRevealedAnswerResults = new(StringComparer.Ordinal);
    private readonly HashSet<string> quizJoinedPlayers = new(StringComparer.Ordinal);
    private string? quizSessionId;
    private QuizPhase quizPhase;
    private int quizQuestionIndex;
    private DateTime quizPhaseEndsUtc;
    private string quizWinnerName = string.Empty;
    private int quizRevision;
    private bool quizPaused;
    private int quizPausedSeconds;
    private int quizRemoteAnsweredCount;
    private bool quizRevealTimedOut;
    private string quizRosterSignature = string.Empty;
    private bool quizHostAuthority;
    private bool quizLocalJoined;
    private int quizLocalAnswer = -1;
    private int quizQuestionCount = 10;
    private int quizTimerSeconds = 20;
    private int quizCategoryIndex;
    private int quizDifficultyIndex;
    private int quizQuestionTypeIndex;
    private int quizEndRuleIndex;
    private int quizScoringIndex;
    private bool quizLoading;
    private string? quizError;
    private Task<List<QuizQuestion>>? quizLoadTask;
    private bool pendingQuizQuickPlay;
    private bool quizLastQuickPlay;
    private string? quizWhiteboardSessionId;
    private int quizLastTvSecond = -1;
    private int quizHoveredTvTarget = -1;

    private bool QuizActive => quizPhase != QuizPhase.Inactive;
    private string LocalQuizUserId => CurrentSession?.AccountId ?? string.Empty;
    private QuizQuestion? CurrentQuizQuestion =>
        quizQuestionIndex >= 0 && quizQuestionIndex < quizQuestions.Count ? quizQuestions[quizQuestionIndex] : null;
    private int QuizSecondsRemaining => quizPaused ? quizPausedSeconds :
        Math.Max(0, (int)Math.Ceiling((quizPhaseEndsUtc - DateTime.UtcNow).TotalSeconds));

    internal void UpdateQuiz()
    {
        if (stream.Mode == StreamMode.None)
        {
            stream.IncomingQuizCommands.Clear();
        }

        while (stream.IncomingQuizCommands.TryDequeue(out var incoming))
        {
            ReceiveArcadeServerCommand("quiz", incoming.UserId, incoming.Command);
        }
        UpdateQuizRelay();

        if (QuizActive)
        {
            var tvSecond = QuizSecondsRemaining;
            if (tvSecond != quizLastTvSecond)
            {
                quizLastTvSecond = tvSecond;
                whiteboardTvDirty = true;
            }
        }

        if (quizLoadTask is { IsCompleted: true } load)
        {
            quizLoadTask = null;
            quizLoading = false;
            if (load.IsCompletedSuccessfully)
            {
                var loaded = load.Result;
                if (loaded.Count == 0)
                    quizError = "Open Trivia DB did not return any usable questions.";
                else
                    StartLoadedQuiz(loaded);
            }
            else
            {
                quizError = "Questions could not be downloaded. Please try again shortly.";
            }
        }

        if (IsLocalActivityHost && QuizActive && !quizHostAuthority)
        {
            StopQuiz();
            return;
        }
        if (IsLocalActivityHost && QuizActive && !screenController.Engine.IsPlayingWhiteboard)
        {
            StopQuiz();
            return;
        }
        if (!IsLocalActivityHost || !QuizActive || !quizHostAuthority)
            return;

        var rosterSignature = string.Join(';', stream.Roster.Select(player => player.UserId).Order(StringComparer.Ordinal));
        if (!string.Equals(rosterSignature, quizRosterSignature, StringComparison.Ordinal))
        {
            quizRosterSignature = rosterSignature;
            var connected = stream.Roster.Select(player => player.UserId).ToHashSet(StringComparer.Ordinal);
            if (CurrentSession is { AccountId.Length: > 0 } host) connected.Add(host.AccountId);
            quizJoinedPlayers.RemoveWhere(id => !connected.Contains(id));
            foreach (var departed in quizAnswers.Keys.Where(id => !connected.Contains(id)).ToArray())
                quizAnswers.Remove(departed);
            quizRemoteAnsweredCount = quizAnswers.Count;
            quizRevision++;
            whiteboardTvDirty = true;
            SendQuizState();
        }
        if (quizPaused) return;

        if (quizPhase == QuizPhase.Countdown && DateTime.UtcNow >= quizPhaseEndsUtc)
            BeginQuizQuestion();
        else if (quizPhase == QuizPhase.Question && DateTime.UtcNow >= quizPhaseEndsUtc)
            RevealQuizAnswer(timedOut: true);
        else if (quizPhase == QuizPhase.Reveal && DateTime.UtcNow >= quizPhaseEndsUtc)
            BeginNextQuizQuestion();
    }

    private void StartQuizLoad(bool quickPlay)
    {
        if (!IsLocalActivityHost || CurrentSession is null || quizLoading ||
            !CanLaunchActivity(ActiveActivityKind.Quiz))
            return;

        if (quickPlay)
        {
            quizQuestionCount = 10;
            quizTimerSeconds = 20;
            quizCategoryIndex = 0;
            quizDifficultyIndex = 0;
            quizQuestionTypeIndex = 0;
            quizEndRuleIndex = 0;
            quizScoringIndex = 0;
        }
        quizLastQuickPlay = quickPlay;
        quizError = null;
        quizLoading = true;
        quizLoadTask = FetchQuizQuestionsAsync(
            Math.Clamp(quizQuestionCount, 3, 25),
            QuizCategories[Math.Clamp(quizCategoryIndex, 0, QuizCategories.Length - 1)].Id,
            Math.Clamp(quizDifficultyIndex, 0, QuizDifficulties.Length - 1),
            Math.Clamp(quizQuestionTypeIndex, 0, QuizQuestionTypes.Length - 1));
    }

    private static async Task<List<QuizQuestion>> FetchQuizQuestionsAsync(
        int count, int category, int difficultyIndex, int questionTypeIndex)
    {
        var questionType = questionTypeIndex == 1 ? "boolean" : "multiple";
        var query = $"https://opentdb.com/api.php?amount={count}&type={questionType}&encode=base64";
        if (category > 0) query += $"&category={category}";
        if (difficultyIndex > 0)
            query += $"&difficulty={QuizDifficulties[difficultyIndex].ToLowerInvariant()}";

        using var http = PluginHttpClients.CreateMetadataClient();
        var response = await http.GetFromJsonAsync<OpenTriviaResponse>(query).ConfigureAwait(false);
        if (response is null || response.ResponseCode != 0)
            return [];

        var result = new List<QuizQuestion>();
        foreach (var source in response.Results)
        {
            var correct = DecodeQuizBase64(source.CorrectAnswer);
            var answers = source.IncorrectAnswers.Select(DecodeQuizBase64).Append(correct)
                .OrderBy(_ => Random.Shared.Next()).ToArray();
            var correctIndex = Array.IndexOf(answers, correct);
            var question = DecodeQuizBase64(source.Question);
            if (question.Length > 0 && correctIndex >= 0)
                result.Add(new QuizQuestion(DecodeQuizBase64(source.Category), question, answers, correctIndex));
        }
        return result;
    }

    private static string DecodeQuizBase64(string value)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch { return value; }
    }

    private void StartLoadedQuiz(List<QuizQuestion> questions)
    {
        if (!IsLocalActivityHost || CurrentSession is null ||
            !CanLaunchActivity(ActiveActivityKind.Quiz))
            return;
        quizQuestions.Clear();
        quizQuestions.AddRange(questions);
        quizSessionId = Guid.NewGuid().ToString("N");
        quizHostAuthority = true;
        quizQuestionIndex = 0;
        quizScores.Clear();
        quizPlayerNames.Clear();
        quizJoinedPlayers.Clear();
        quizAnswers.Clear();
        quizRemoteAnsweredCount = 0;
        quizWinnerName = string.Empty;
        quizRevealTimedOut = false;
        quizPaused = false;
        quizPausedSeconds = 0;
        quizRevision = 0;
        quizRosterSignature = string.Empty;
        quizLocalJoined = true;
        quizLocalAnswer = -1;
        StartHostWhiteboard(WhiteboardMode.Solo, ActiveActivityKind.Quiz);
        quizWhiteboardSessionId = whiteboardSessionId;
        whiteboardTvDirty = true;
        var hostName = LocalQuizDisplayName();
        quizJoinedPlayers.Add(LocalQuizUserId);
        quizPlayerNames[LocalQuizUserId] = hostName;
        quizScores[LocalQuizUserId] = 0;
        BeginQuizCountdown();
        currentPage = HomePage.Quiz;
    }

    private void BeginQuizCountdown()
    {
        quizPhase = QuizPhase.Countdown;
        quizQuestionIndex = 0;
        quizAnswers.Clear();
        quizWinnerName = string.Empty;
        quizPaused = false;
        quizPausedSeconds = 0;
        quizLocalAnswer = -1;
        quizPhaseEndsUtc = DateTime.UtcNow.AddSeconds(20);
        quizRevision++;
        whiteboardTvDirty = true;
        SendQuizState();
    }

    private void BeginQuizQuestion()
    {
        if (CurrentQuizQuestion is null)
        {
            quizPhase = QuizPhase.Finished;
            quizPhaseEndsUtc = DateTime.MaxValue;
            AwardQuizActivityPoints();
            quizRevision++;
            whiteboardTvDirty = true;
            SendQuizState();
            return;
        }
        quizPhase = QuizPhase.Question;
        quizAnswers.Clear();
        quizWinnerName = string.Empty;
        quizPaused = false;
        quizPausedSeconds = 0;
        quizLocalAnswer = -1;
        quizPhaseEndsUtc = DateTime.UtcNow.AddSeconds(Math.Clamp(quizTimerSeconds, 5, 120));
        quizRevision++;
        whiteboardTvDirty = true;
        SendQuizState();
    }

    private void BeginNextQuizQuestion()
    {
        quizQuestionIndex++;
        BeginQuizQuestion();
    }

    private void SubmitQuizJoin()
    {
        if (!QuizActive || quizSessionId is null || quizLocalJoined)
            return;
        quizLocalJoined = true;
        if (IsLocalActivityHost)
            HostJoinQuiz(LocalQuizUserId);
        else if (!SendArcadePlayerAction("quiz", $"J|{quizSessionId}"))
            quizLocalJoined = false;
    }

    private void SubmitQuizAnswer(int answerIndex)
    {
        if (quizPhase != QuizPhase.Question || quizPaused || !quizLocalJoined || quizLocalAnswer >= 0 ||
            quizSessionId is null || CurrentQuizQuestion is not { } question ||
            answerIndex < 0 || answerIndex >= question.Answers.Length)
            return;
        quizLocalAnswer = answerIndex;
        whiteboardTvDirty = true;
        if (IsLocalActivityHost)
            HostAcceptQuizAnswer(LocalQuizUserId, answerIndex);
        else if (!SendArcadePlayerAction("quiz", $"A|{quizSessionId}|{quizQuestionIndex + 1}|{answerIndex}"))
            quizLocalAnswer = -1;
    }

    internal bool QuizTvAnswersInteractive =>
        quizPhase == QuizPhase.Question && !quizPaused && quizLocalJoined &&
        quizLocalAnswer < 0 && CurrentQuizQuestion is not null;

    internal bool QuizTvControlsInteractive =>
        QuizTvAnswersInteractive ||
        IsLocalActivityHost && quizPhase == QuizPhase.Finished;

    internal bool IsQuizTvControlAt(Vector2 uv) =>
        TryGetQuizTvTarget(uv, out _);

    internal void SetQuizTvHover(Vector2? uv)
    {
        var target = uv is { } position && TryGetQuizTvTarget(position, out var hitTarget)
            ? hitTarget
            : -1;
        if (target == quizHoveredTvTarget)
            return;
        quizHoveredTvTarget = target;
        whiteboardTvDirty = true;
    }

    internal void SubmitQuizTvControlAt(Vector2 uv)
    {
        if (!TryGetQuizTvTarget(uv, out var target))
            return;
        if (target is >= 0 and <= 3)
        {
            SubmitQuizAnswer(target);
            return;
        }
        if (target == 4)
            StopQuiz();
        else if (target == 5)
            PlayQuizAgain();
    }

    private bool TryGetQuizTvTarget(Vector2 uv, out int target)
    {
        if (TryGetQuizTvAnswerIndex(uv, out target))
            return true;

        target = -1;
        if (!IsLocalActivityHost || quizPhase != QuizPhase.Finished)
            return false;

        var pixelX = uv.X * WhiteboardRenderer.Width;
        var pixelY = uv.Y * WhiteboardRenderer.Height;
        if (pixelY < 340f || pixelY > 426f)
            return false;
        if (pixelX >= 195f && pixelX <= 445f)
            target = 4;
        else if (!quizLoading && pixelX >= 473f && pixelX <= 723f)
            target = 5;
        return target >= 0;
    }

    private bool TryGetQuizTvAnswerIndex(Vector2 uv, out int answerIndex)
    {
        answerIndex = -1;
        if (quizPhase != QuizPhase.Question || quizPaused || !quizLocalJoined ||
            quizLocalAnswer >= 0 || CurrentQuizQuestion is not { } question)
        {
            return false;
        }

        var pixelX = uv.X * WhiteboardRenderer.Width;
        var pixelY = uv.Y * WhiteboardRenderer.Height;
        const float firstX = 36f;
        const float secondX = 471f;
        const float answerWidth = 411f;
        const float firstY = 316f;
        const float secondY = 482f;
        const float answerHeight = 146f;
        var column = pixelX >= firstX && pixelX <= firstX + answerWidth
            ? 0
            : pixelX >= secondX && pixelX <= secondX + answerWidth
                ? 1
                : -1;
        var row = pixelY >= firstY && pixelY <= firstY + answerHeight
            ? 0
            : pixelY >= secondY && pixelY <= secondY + answerHeight
                ? 1
                : -1;
        if (column < 0 || row < 0)
        {
            return false;
        }

        answerIndex = row * 2 + column;
        return answerIndex < question.Answers.Length;
    }

    private void ApplyQuizContribution(string senderId, string command)
    {
        var parts = command.Split('|');
        if (quizSessionId is null || parts.Length < 2 || !string.Equals(parts[1], quizSessionId, StringComparison.Ordinal))
            return;
        if (parts[0] == "J")
        {
            HostJoinQuiz(senderId);
            return;
        }
        if (parts[0] == "A" && parts.Length == 4 &&
            int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var questionNumber) &&
            questionNumber == quizQuestionIndex + 1 && int.TryParse(parts[3], out var answer))
            HostAcceptQuizAnswer(senderId, answer);
    }

    private void HostJoinQuiz(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || quizJoinedPlayers.Contains(userId)) return;
        quizJoinedPlayers.Add(userId);
        var rosterName = stream.Roster.FirstOrDefault(player => player.UserId == userId)?.DisplayName;
        quizPlayerNames[userId] = string.IsNullOrWhiteSpace(rosterName) ? "Player" : rosterName;
        quizScores.TryAdd(userId, 0);
        quizRevision++;
        whiteboardTvDirty = true;
        SendQuizState();
    }

    private void HostAcceptQuizAnswer(string userId, int answerIndex)
    {
        var question = CurrentQuizQuestion;
        if (quizPhase != QuizPhase.Question || quizPaused || question is null || !quizJoinedPlayers.Contains(userId) ||
            quizAnswers.ContainsKey(userId) || answerIndex < 0 || answerIndex >= question.Answers.Length)
            return;
        quizAnswers[userId] = answerIndex;
        quizRemoteAnsweredCount = quizAnswers.Count;
        whiteboardTvDirty = true;
        if (answerIndex == question.CorrectIndex)
        {
            if (quizWinnerName.Length == 0)
                quizWinnerName = quizPlayerNames.GetValueOrDefault(userId, "Player");
            var fraction = Math.Clamp(QuizSecondsRemaining / (double)Math.Max(1, quizTimerSeconds), 0d, 1d);
            var points = quizScoringIndex == 0
                ? 1 + (int)Math.Floor(fraction * 2d)
                : 1;
            quizScores[userId] = quizScores.GetValueOrDefault(userId) + points;
            if (quizEndRuleIndex == 1)
            {
                RevealQuizAnswer();
                return;
            }
        }
        quizRevision++;
        SendQuizState();
        if (quizJoinedPlayers.Count > 0 && quizJoinedPlayers.All(quizAnswers.ContainsKey))
            RevealQuizAnswer();
    }

    private void RevealQuizAnswer(bool timedOut = false)
    {
        if (quizPhase != QuizPhase.Question) return;
        quizRevealTimedOut = timedOut;
        quizPaused = false;
        quizPausedSeconds = 0;
        quizPhase = QuizPhase.Reveal;
        quizPhaseEndsUtc = DateTime.UtcNow.AddSeconds(5);
        quizRevision++;
        whiteboardTvDirty = true;
        SendQuizState();
    }

    private void SendQuizState()
    {
        if (!IsLocalActivityHost || quizSessionId is null) return;
        var question = CurrentQuizQuestion;
        var reveal = quizPhase is QuizPhase.Reveal or QuizPhase.Finished;
        var snapshot = new QuizSnapshot(
            quizSessionId, quizPhase, quizQuestionIndex + 1, quizQuestions.Count,
            quizPhaseEndsUtc == DateTime.MaxValue ? long.MaxValue :
                new DateTimeOffset(quizPaused ? DateTime.UtcNow.AddSeconds(quizPausedSeconds) : quizPhaseEndsUtc).ToUnixTimeMilliseconds(),
            question?.Category ?? string.Empty, question?.Text ?? string.Empty,
            question?.Answers ?? [], reveal ? question?.CorrectIndex ?? -1 : -1,
            quizWinnerName, quizJoinedPlayers.ToArray(), quizAnswers.Keys.ToArray(),
            quizScores.Select(pair => new QuizPlayer(pair.Key,
                quizPlayerNames.GetValueOrDefault(pair.Key, "Player"), pair.Value,
                QuizPlayerAnswerResult(pair.Key))).ToArray(),
            quizEndRuleIndex == 1, quizPaused, quizRevealTimedOut, quizTimerSeconds, quizRevision);
        var command = "S|" + EncodePictionaryJson(snapshot);
        if (TrySendQuizThroughCloudflare(command)) return;
        _ = stream.SendArcadeSnapshotAsync(StreamClient.QuizCommandPrefix, command,
            $"{quizSessionId}|{quizQuestionIndex}|{quizPhase}|{quizPaused}");
    }

    private void ApplyQuizCommand(string command)
    {
        var parts = command.Split('|', 2);
        if (parts.Length != 2) return;
        if (parts[0] == "X")
        {
            if (quizSessionId is null || parts[1] == quizSessionId) ResetQuizLocal();
            return;
        }
        if (parts[0] != "S") return;
        var snapshot = DecodePictionaryJson<QuizSnapshot>(parts[1]);
        if (snapshot is null || QuizRelayRules.Revision(command, snapshot.SessionId) is null ||
            snapshot.SessionId == quizSessionId && snapshot.Revision < quizRevision) return;
        var newQuizSession = !string.Equals(quizSessionId, snapshot.SessionId, StringComparison.Ordinal);
        quizSessionId = snapshot.SessionId;
        quizPhase = snapshot.Phase;
        quizQuestionIndex = Math.Max(0, snapshot.QuestionNumber - 1);
        quizPhaseEndsUtc = snapshot.PhaseEndsUnixMs == long.MaxValue ? DateTime.MaxValue :
            DateTimeOffset.FromUnixTimeMilliseconds(snapshot.PhaseEndsUnixMs).UtcDateTime;
        quizWinnerName = snapshot.WinnerName;
        quizRevision = snapshot.Revision;
        quizEndRuleIndex = snapshot.FirstCorrectEnds ? 1 : 0;
        quizPaused = snapshot.Paused;
        quizRevealTimedOut = snapshot.TimedOut;
        quizTimerSeconds = Math.Clamp(snapshot.QuestionSeconds, 5, 120);
        if (quizPaused) quizPausedSeconds = Math.Max(0,
            (int)Math.Ceiling((quizPhaseEndsUtc - DateTime.UtcNow).TotalSeconds));
        quizQuestions.Clear();
        for (var index = 0; index < snapshot.QuestionCount; index++)
            quizQuestions.Add(index == quizQuestionIndex
                ? new QuizQuestion(snapshot.Category, snapshot.Question, snapshot.Answers, snapshot.CorrectIndex)
                : new QuizQuestion(string.Empty, string.Empty, [], -1));
        quizJoinedPlayers.Clear();
        quizJoinedPlayers.UnionWith(snapshot.JoinedIds);
        quizRemoteAnsweredCount = snapshot.AnsweredIds.Length;
        quizScores.Clear();
        quizPlayerNames.Clear();
        quizRevealedAnswerResults.Clear();
        foreach (var player in snapshot.Players)
        {
            quizScores[player.Id] = player.Score;
            quizPlayerNames[player.Id] = player.Name;
            if (player.AnswerCorrect is { } correct)
                quizRevealedAnswerResults[player.Id] = correct;
        }
        AwardQuizActivityPoints();
        quizLocalJoined = quizJoinedPlayers.Contains(LocalQuizUserId);
        if (!snapshot.AnsweredIds.Contains(LocalQuizUserId, StringComparer.Ordinal))
            quizLocalAnswer = -1;
        else if (quizLocalAnswer < 0)
            quizLocalAnswer = -2;
        whiteboardTvDirty = true;
        if (newQuizSession && stream.Mode == StreamMode.Viewing && !ViewerTvEnabled)
            NotifyViewerQuizStartedWithoutTv();
        if (stream.Mode == StreamMode.Viewing && !quizLocalJoined)
            SubmitQuizJoin();
    }

    private void StopQuiz()
    {
        if (IsLocalActivityHost && quizSessionId is not null)
            _ = stream.SendQuizCommandAsync("X|" + quizSessionId);
        if (IsLocalActivityHost && whiteboardSessionId is not null &&
            string.Equals(whiteboardSessionId, quizWhiteboardSessionId, StringComparison.Ordinal))
            SendAndApplyWhiteboardCommand($"X|{whiteboardSessionId}");
        ResetQuizLocal();
    }

    private void PlayQuizAgain()
    {
        if (!IsLocalActivityHost || quizPhase != QuizPhase.Finished || quizLoading)
            return;
        StartQuizLoad(quizLastQuickPlay);
        whiteboardTvDirty = true;
    }

    private void ResetQuizLocal()
    {
        CaptureArcadeScoresBeforeReset();
        DisposeQuizRelay();
        quizSessionId = null;
        quizPhase = QuizPhase.Inactive;
        quizQuestions.Clear();
        quizAnswers.Clear();
        quizRevealedAnswerResults.Clear();
        quizRemoteAnsweredCount = 0;
        quizJoinedPlayers.Clear();
        quizPlayerNames.Clear();
        quizScores.Clear();
        quizLocalJoined = false;
        quizLocalAnswer = -1;
        quizHostAuthority = false;
        quizWinnerName = string.Empty;
        quizPaused = false;
        quizPausedSeconds = 0;
        quizRevealTimedOut = false;
        quizRosterSignature = string.Empty;
        quizLoading = false;
        quizLoadTask = null;
        quizWhiteboardSessionId = null;
        quizLastTvSecond = -1;
        quizHoveredTvTarget = -1;
    }

    private void ApplyQuizTvOverlay(byte[] pixels, int width, int height)
    {
        const uint background = 0x090C18;
        const uint panel = 0x12182A;
        const uint panelSoft = 0x181E33;
        const uint border = 0x343D5D;
        const uint white = 0xF5F7FC;
        const uint muted = 0xAAB2CA;
        const uint purple = 0x8C55F6;
        const uint purpleDark = 0x38235F;
        const uint green = 0x27D887;
        const uint greenDark = 0x123F32;
        const uint red = 0xFF5C73;
        const uint redDark = 0x481D2A;
        const uint hoverBorder = 0x20263B;

        FillPictionaryPixels(pixels, width, height, 0, 0, width, height, background);
        FillPictionaryPixels(pixels, width, height, 0, 0, width, 70, 0x171126);
        FillPictionaryPixels(pixels, width, height, 0, 68, width, 2, purple);
        DrawPictionaryPixelText(pixels, width, height, 30, 22, "ALPHA CHANNEL QUIZ", 4, white);

        var phaseLabel = quizPhase switch
        {
            QuizPhase.Countdown => "QUIZ STARTING",
            QuizPhase.Finished => "FINAL SCORES",
            _ => $"QUESTION {Math.Max(1, quizQuestionIndex + 1)} OF {Math.Max(1, quizQuestions.Count)}",
        };
        DrawPictionaryPixelText(pixels, width, height, 655, 25, phaseLabel, 3, muted);

        const int contentX = 36;
        const int contentWidth = 846;
        const int sideX = 914;
        const int sideWidth = 330;

        if (quizPhase == QuizPhase.Countdown)
        {
            FillQuizTvPanel(pixels, width, height, contentX, 100, contentWidth, 570, panel, border);
            DrawPictionaryPixelTextCentered(pixels, width, height, contentX + contentWidth / 2, 170,
                "QUIZ STARTS IN", 4, muted);
            DrawPictionaryPixelTextCentered(pixels, width, height, contentX + contentWidth / 2, 260,
                QuizSecondsRemaining.ToString(CultureInfo.InvariantCulture), 12, purple);
            DrawPictionaryPixelTextCentered(pixels, width, height, contentX + contentWidth / 2, 410,
                "GET READY TO ANSWER ON THE TV", 3, white);
        }
        else if (quizPhase == QuizPhase.Finished)
        {
            FillQuizTvPanel(pixels, width, height, contentX, 100, contentWidth, 570, panel, border);
            DrawPictionaryPixelTextCentered(pixels, width, height, contentX + contentWidth / 2, 142,
                "QUIZ COMPLETE", 6, purple);
            DrawPictionaryPixelTextCentered(pixels, width, height, contentX + contentWidth / 2, 214,
                "THANKS FOR PLAYING", 3, muted);
            if (IsLocalActivityHost)
            {
                const int buttonY = 340;
                const int buttonWidth = 250;
                const int buttonHeight = 86;
                FillQuizTvPanel(pixels, width, height, 195, buttonY, buttonWidth, buttonHeight,
                    redDark, quizHoveredTvTarget == 4 ? 0xB83E52 : red);
                DrawPictionaryPixelTextCentered(pixels, width, height, 320, buttonY + 31,
                    "END QUIZ", 3, white);
                FillQuizTvPanel(pixels, width, height, 473, buttonY, buttonWidth, buttonHeight,
                    quizLoading ? 0x282F49 : purpleDark,
                    quizLoading ? border : quizHoveredTvTarget == 5 ? 0x512E8F : purple);
                DrawPictionaryPixelTextCentered(pixels, width, height, 598, buttonY + 31,
                    quizLoading ? "LOADING..." : "PLAY AGAIN", 3, white);
            }
        }
        else if (CurrentQuizQuestion is { } question)
        {
            FillQuizTvPanel(pixels, width, height, contentX, 100, contentWidth, 196, panel, border);
            DrawPictionaryPixelText(pixels, width, height, contentX + 22, 120,
                TruncateQuizTvText(question.Category.ToUpperInvariant(), 45), 2, purple);
            var questionScale = question.Text.Length > 190 ? 2 : 3;
            var questionLines = WrapQuizTvText(question.Text,
                questionScale == 2 ? 66 : 43, questionScale == 2 ? 6 : 5);
            var questionLineHeight = questionScale == 2 ? 21 : 29;
            for (var index = 0; index < questionLines.Count; index++)
                DrawPictionaryPixelText(pixels, width, height, contentX + 22,
                    158 + index * questionLineHeight, questionLines[index], questionScale, white);

            var answerWidth = 411;
            var answerHeight = 146;
            for (var index = 0; index < question.Answers.Length && index < 4; index++)
            {
                var column = index % 2;
                var row = index / 2;
                var answerX = contentX + column * (answerWidth + 24);
                var answerY = 316 + row * (answerHeight + 20);
                var selected = quizLocalAnswer == index;
                var reveal = quizPhase == QuizPhase.Reveal;
                var correct = reveal && index == question.CorrectIndex;
                var incorrectSelection = reveal && selected && !correct;
                var fill = correct ? greenDark : incorrectSelection ? redDark : selected ? purpleDark : panelSoft;
                var outline = correct ? green : incorrectSelection ? red : selected ? purple :
                    quizHoveredTvTarget == index ? hoverBorder : border;
                FillQuizTvPanel(pixels, width, height, answerX, answerY, answerWidth, answerHeight, fill, outline);
                FillPictionaryPixels(pixels, width, height, answerX + 18, answerY + 17, 42, 42,
                    correct ? green : incorrectSelection ? red : selected ? purple : 0x252D48);
                DrawPictionaryPixelTextCentered(pixels, width, height, answerX + 39, answerY + 28,
                    ((char)('A' + index)).ToString(), 2, white);
                var answerLines = WrapQuizTvText(question.Answers[index], 25, 3);
                for (var line = 0; line < answerLines.Count; line++)
                    DrawPictionaryPixelText(pixels, width, height, answerX + 76, answerY + 25 + line * 23,
                        answerLines[line], 2, white);
                if (selected)
                    DrawPictionaryPixelText(pixels, width, height, answerX + 76, answerY + 105,
                        reveal ? correct ? "YOUR ANSWER - CORRECT" : "YOUR ANSWER - INCORRECT" : "YOUR ANSWER - LOCKED IN",
                        2, correct ? green : incorrectSelection ? red : purple);
            }
        }

        FillQuizTvPanel(pixels, width, height, sideX, 100, sideWidth, 170, panel, border);
        DrawPictionaryPixelTextCentered(pixels, width, height, sideX + sideWidth / 2, 120,
            quizPaused ? "PAUSED" : quizPhase == QuizPhase.Countdown ? "STARTING" :
            quizPhase == QuizPhase.Reveal ? "ANSWER" : "TIME LEFT", 3, muted);
        var timer = quizPhase == QuizPhase.Finished ? "--" : $"{QuizSecondsRemaining / 60:00}:{QuizSecondsRemaining % 60:00}";
        DrawPictionaryPixelTextCentered(pixels, width, height, sideX + sideWidth / 2, 165, timer, 7,
            quizPhase == QuizPhase.Reveal ? green : purple);
        var timerProgress = quizPhase is QuizPhase.Question or QuizPhase.Countdown
            ? Math.Clamp(QuizSecondsRemaining /
                (float)(quizPhase == QuizPhase.Countdown ? 20 : Math.Max(1, quizTimerSeconds)), 0f, 1f)
            : 0f;
        FillPictionaryPixels(pixels, width, height, sideX + 24, 238, sideWidth - 48, 10, 0x282F49);
        FillPictionaryPixels(pixels, width, height, sideX + 24, 238,
            (int)((sideWidth - 48) * timerProgress), 10, purple);

        FillQuizTvPanel(pixels, width, height, sideX, 290, sideWidth, 380, panel, border);
        DrawPictionaryPixelText(pixels, width, height, sideX + 22, 312, "SCOREBOARD", 3, white);
        var players = quizScores
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => quizPlayerNames.GetValueOrDefault(entry.Key, "Player"), StringComparer.OrdinalIgnoreCase)
            .Take(7)
            .ToArray();
        for (var index = 0; index < players.Length; index++)
        {
            var player = players[index];
            var y = 362 + index * 40;
            if (string.Equals(player.Key, LocalQuizUserId, StringComparison.Ordinal))
                FillPictionaryPixels(pixels, width, height, sideX + 12, y - 9, sideWidth - 24, 34, purpleDark);
            DrawUsernamePixelText(pixels, width, height, sideX + 22, y,
                $"{index + 1}. {TruncateQuizTvText(quizPlayerNames.GetValueOrDefault(player.Key, "Player"), 17)}", 2,
                QuizPlayerAnswerResult(player.Key) is { } answeredCorrectly ? answeredCorrectly ? green : red : white,player.Key);
            DrawPictionaryPixelText(pixels, width, height, sideX + sideWidth - 76, y,
                $"{player.Value} PT", 2, purple);
        }
        DrawPictionaryPixelText(pixels, width, height, sideX + 22, 642,
            $"{quizRemoteAnsweredCount} OF {quizJoinedPlayers.Count} ANSWERED", 2, muted);
        if (quizPhase == QuizPhase.Question && quizLocalJoined && quizLocalAnswer < 0)
            DrawPictionaryPixelText(pixels, width, height, contentX, 673,
                "CLICK AN ANSWER DIRECTLY ON THE TV", 2, muted);
    }

    private static void FillQuizTvPanel(byte[] pixels, int width, int height,
        int x, int y, int panelWidth, int panelHeight, uint fill, uint border)
    {
        FillPictionaryPixels(pixels, width, height, x, y, panelWidth, panelHeight, border);
        FillPictionaryPixels(pixels, width, height, x + 2, y + 2, panelWidth - 4, panelHeight - 4, fill);
    }

    private static List<string> WrapQuizTvText(string value, int maxCharacters, int maxLines)
    {
        var normalized = value.Replace('’', '\'').Replace('‘', '\'').Replace('“', '"').Replace('”', '"')
            .Replace('–', '-').Replace('—', '-').Trim();
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (candidate.Length <= maxCharacters)
            {
                current = candidate;
                continue;
            }
            if (current.Length > 0)
                lines.Add(current);
            current = word.Length <= maxCharacters ? word : word[..maxCharacters];
            if (lines.Count == maxLines - 1)
                break;
        }
        if (lines.Count < maxLines && current.Length > 0)
            lines.Add(current);
        if (lines.Count == 0)
            lines.Add(string.Empty);
        return lines;
    }

    private static string TruncateQuizTvText(string value, int maxCharacters) =>
        value.Length <= maxCharacters ? value : value[..Math.Max(1, maxCharacters - 1)] + ".";

    private string LocalQuizDisplayName() =>
        string.IsNullOrWhiteSpace(CurrentSession?.DisplayName) ? CurrentDisplayName ?? "Player" : CurrentSession!.DisplayName;

    private void DrawQuizPage()
    {
        if (!QuizActive)
            DrawQuizSetupPage();
        else
            DrawQuizStatusPage();
    }

    private void DrawQuizSetupPage()
    {
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(10f, 12f));
        DrawQuizHero();
        var available = ImGui.GetContentRegionAvail().X;
        var gap = Ui(12f);
        var stacked = available < Ui(760f);
        var quickWidth = stacked ? available : (available - gap) * 0.40f;
        var customWidth = stacked ? available : available - gap - quickWidth;
        DrawQuizQuickCard(quickWidth);
        if (!stacked) ImGui.SameLine(0f, gap);
        DrawQuizCustomCard(customWidth);
        DrawQuizHowItWorks();
        ImGui.TextColored(MutedText, "Questions supplied by Open Trivia DB (CC BY-SA 4.0). An internet connection is required when the host starts a quiz.");
        if (!string.IsNullOrWhiteSpace(quizError)) ImGui.TextColored(Danger, quizError);
    }

    private void DrawQuizHero() => DrawArcadeReviewHero("quiz", "Quiz", "Test your knowledge together", "Choose a quiz mode or customise your own. Answer together on the Watch Party TV.", "One locked answer per player", true, ActivityPreviewKind.Quiz);


    private void DrawQuizQuickCard(float width)
    {
        const float designHeight = 390f;
        DrawQuizCardBegin("##quizQuick", width, Ui(designHeight));
        var origin = ImGui.GetWindowPos();
        DrawPictionarySetupBadge("Quick play", true);
        ImGui.Dummy(UiVec(0f, 8f));
        DrawPictionarySetupCardHeading(FontAwesomeIcon.GraduationCap, "Quick quiz",
            "Jump straight into a balanced ten-question game.");
        ImGui.Dummy(UiVec(0f, 18f));
        DrawActivityFeature("10 random questions");
        DrawActivityFeature("Mixed categories and difficulty");
        DrawActivityFeature("20 seconds per question");
        DrawActivityFeature("Everyone answers or the timer ends");
        ImGui.SetCursorScreenPos(new Vector2(origin.X + Ui(18f), origin.Y + Ui(designHeight - 54f)));
        DrawActivityStartButtons("quizQuick", () => RequestQuizStart(true),
            () => RequestQuizStart(true, true), CanRequestQuizStart());
        ImGui.EndChild();
    }

    private void DrawQuizCustomCard(float width)
    {
        const float designHeight = 450f;
        DrawQuizCardBegin("##quizCustom", width, Ui(designHeight), accentBorder: true);
        var origin = ImGui.GetWindowPos();
        DrawPictionarySetupBadge("Custom quiz", false);
        ImGui.Dummy(UiVec(0f, 8f));
        DrawPictionarySetupCardHeading(FontAwesomeIcon.Cog, "Custom quiz", "Set up your own quiz experience.");
        ImGui.Dummy(UiVec(0f, 7f));
        var formOrigin = ImGui.GetCursorScreenPos();
        var formWidth = ImGui.GetContentRegionAvail().X;
        var columnGap = Ui(18f);
        var columnWidth = (formWidth - columnGap) * 0.5f;
        var rightX = formOrigin.X + columnWidth + columnGap;
        var rowStep = Ui(60f);
        using (UiDisabled.Scope(stream.Mode == StreamMode.Viewing || quizLoading))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7f)))
        {
            DrawQuizGridStepper(formOrigin, columnWidth, "Questions", "count",
                ref quizQuestionCount, 3, 25, 1, value => value.ToString());
            DrawQuizGridCombo(formOrigin + new Vector2(0f, rowStep), columnWidth, "Category", "category",
                ref quizCategoryIndex, QuizCategories.Select(x => x.Name).ToArray());
            DrawQuizGridCombo(formOrigin + new Vector2(0f, rowStep * 2f), columnWidth, "Difficulty", "difficulty",
                ref quizDifficultyIndex, QuizDifficulties);
            DrawQuizGridCombo(formOrigin + new Vector2(0f, rowStep * 3f), columnWidth, "Question type", "type",
                ref quizQuestionTypeIndex, QuizQuestionTypes);

            DrawQuizGridStepper(new Vector2(rightX, formOrigin.Y), columnWidth, "Time per question", "timer",
                ref quizTimerSeconds, 10, 60, 5, value => $"{value} seconds");
            DrawQuizGridCombo(new Vector2(rightX, formOrigin.Y + rowStep), columnWidth, "Round ends", "end",
                ref quizEndRuleIndex, QuizEndRules);
            DrawQuizGridCombo(new Vector2(rightX, formOrigin.Y + rowStep * 2f), columnWidth, "Scoring", "scoring",
                ref quizScoringIndex, QuizScoringOptions);
        }
        ImGui.SetCursorScreenPos(new Vector2(origin.X + Ui(18f), origin.Y + Ui(designHeight - 54f)));
        DrawActivityStartButtons("quizCustom", () => RequestQuizStart(false),
            () => RequestQuizStart(false, true), CanRequestQuizStart());
        ImGui.EndChild();
    }

    private void DrawQuizCardBegin(string id, float width, float height, bool accentBorder = false)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, CardBg);
        ImGui.PushStyleColor(ImGuiCol.Border, accentBorder
            ? new Vector4(AccentHover.X, AccentHover.Y, AccentHover.Z, 0.75f)
            : BorderSubtle);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Ui(10f));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, Ui(1f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, UiVec(18f, 16f));
        UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild(id, new Vector2(width, height), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse));
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);
    }

    private static void DrawQuizGridStepper(
        Vector2 origin, float width, string label, string id,
        ref int value, int min, int max, int step,
        Func<int, string> format)
    {
        var buttonWidth = Ui(31f);
        var gap = Ui(4f);
        var valueWidth = width - buttonWidth * 2f - gap * 2f;
        var draw = ImGui.GetWindowDrawList();

        draw.AddText(origin, ImGui.GetColorU32(MutedText), label);
        var controlsY = origin.Y + Ui(22f);
        ImGui.SetCursorScreenPos(new Vector2(origin.X, controlsY));
        if (ImGui.Button($"-##quiz{id}Minus", new Vector2(buttonWidth, Ui(30f))))
            value = Math.Max(min, value - step);

        ImGui.SameLine(0f, gap);
        var valueMin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##quiz{id}Value", new Vector2(valueWidth, Ui(30f)));
        draw.AddRectFilled(valueMin, valueMin + new Vector2(valueWidth, Ui(30f)),
            ImGui.GetColorU32(FrameBg), Ui(7f));
        draw.AddRect(valueMin, valueMin + new Vector2(valueWidth, Ui(30f)), ImGui.GetColorU32(BorderSubtle), Ui(7f));
        var text = format(value);
        draw.AddText(valueMin + (new Vector2(valueWidth, Ui(30f)) - ImGui.CalcTextSize(text)) * 0.5f,
            ImGui.GetColorU32(Vector4.One), text);

        ImGui.SameLine(0f, gap);
        if (ImGui.Button($"+##quiz{id}Plus", new Vector2(buttonWidth, Ui(30f))))
            value = Math.Min(max, value + step);
    }

    private static void DrawQuizGridCombo(
        Vector2 origin, float width, string label, string id,
        ref int value, string[] options)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.AddText(origin, ImGui.GetColorU32(MutedText), label);
        ImGui.SetCursorScreenPos(origin + UiVec(0f, 22f));
        ImGui.SetNextItemWidth(width);
        using var colours = ImRaii.PushColor(ImGuiCol.FrameBg, FrameBg)
            .Push(ImGuiCol.FrameBgHovered, FrameBgHover)
            .Push(ImGuiCol.Button, FrameBg)
            .Push(ImGuiCol.ButtonHovered, FrameBgHover)
            .Push(ImGuiCol.ButtonActive, AccentFieldActive);
        ImGui.Combo($"##quiz{id}", ref value, options, options.Length);
    }

    private bool CanRequestQuizStart() =>
        stream.Mode != StreamMode.Viewing && !quizLoading &&
        CanLaunchActivity(ActiveActivityKind.Quiz);

    private void RequestQuizStart(bool quickPlay, bool inParty = false)
    {
        if (!CanRequestQuizStart())
            return;

        if (!inParty || stream.Mode == StreamMode.Hosting)
        {
            StartQuizLoad(quickPlay);
            return;
        }

        pendingQuizQuickPlay = quickPlay;
        pendingWatchPartyMediaKind = PendingWatchPartyMediaKind.Quiz;
        OpenPendingWatchPartyCreation();
    }

    private void DrawQuizHowItWorks() => DrawArcadeSetupSteps("##quizSteps", ["Choose rules", "Spawn the TV", "Click one answer", "See the scores"]);


    private void DrawQuizStatusPage()
    {
        // Share the TV texture and hit targets; refresh only when quiz state changes.
        RenderWhiteboardTvIfNeeded();
        var width = Math.Max(1f, ImGui.GetContentRegionAvail().X);
        var previewSize = new Vector2(width, width * 9f / 16f);
        ImGui.Image(new ImTextureID(screenController.Engine.WhiteboardPreviewTextureHandle), previewSize);
        var hovered = ImGui.IsItemHovered();
        var uv = (ImGui.GetIO().MousePos - ImGui.GetItemRectMin()) / previewSize;
        SetQuizTvHover(hovered ? uv : null);
        if (hovered && IsQuizTvControlAt(uv))
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                SubmitQuizTvControlAt(uv);
        }
        ImGui.Spacing();
        if (IsLocalActivityHost && quizPhase == QuizPhase.Question)
        {
            if (ImGui.Button(quizPaused ? "Resume" : "Pause", UiVec(88f, 38f)))
            {
                if (quizPaused)
                {
                    quizPaused = false;
                    quizPhaseEndsUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, quizPausedSeconds));
                }
                else
                {
                    quizPausedSeconds = QuizSecondsRemaining;
                    quizPaused = true;
                }
                quizRevision++;
                whiteboardTvDirty = true;
                SendQuizState();
            }
            ImGui.SameLine();
            if (ImGui.Button("Skip", UiVec(78f, 38f))) RevealQuizAnswer();
            ImGui.SameLine();
            if (ImGui.Button("+10 sec", UiVec(88f, 38f)))
            {
                if (quizPaused) quizPausedSeconds += 10;
                else quizPhaseEndsUtc = quizPhaseEndsUtc.AddSeconds(10);
                quizRevision++;
                whiteboardTvDirty = true;
                SendQuizState();
            }
            ImGui.SameLine();
        }
        else if (IsLocalActivityHost && quizPhase == QuizPhase.Reveal)
        {
            if (ImGui.Button("Next question", UiVec(130f, 38f))) BeginNextQuizQuestion();
            ImGui.SameLine();
        }

        if (IsLocalActivityHost && QuizActive && quizPhase != QuizPhase.Finished)
        {
            using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.20f, 0.04f, 0.07f, 1f))
                       .Push(ImGuiCol.ButtonHovered, new Vector4(0.36f, 0.07f, 0.11f, 1f)))
            {
                if (ImGui.Button("End quiz", UiVec(96f, 38f))) StopQuiz();
            }
            ImGui.SameLine();
        }
        if (ImGui.Button("Open TV Controls", UiVec(156f, 38f)))
            currentPage = HomePage.Screen;
    }

    // Results are only exposed during the five-second reveal, never while answering.
    private bool? QuizPlayerAnswerResult(string userId)
    {
        if (quizPhase != QuizPhase.Reveal)
            return null;
        if (IsLocalActivityHost)
            return CurrentQuizQuestion is { } question && quizAnswers.TryGetValue(userId, out var answer)
                ? answer == question.CorrectIndex : null;
        return quizRevealedAnswerResults.TryGetValue(userId, out var result) ? result : null;
    }

}

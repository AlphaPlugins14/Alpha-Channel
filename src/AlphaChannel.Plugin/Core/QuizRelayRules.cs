using System.Text.Json;

namespace AlphaChannel.Plugin;

internal static class QuizRelayRules
{
    internal static int? Revision(string command, string session)
    {
        if (command.Length > 48000 || !command.StartsWith("S|", StringComparison.Ordinal)) return null;
        try
        {
            using var document = JsonDocument.Parse(Convert.FromBase64String(command[2..]));
            var s = document.RootElement;
            bool Text(JsonElement v, int max) => v.ValueKind == JsonValueKind.String && v.GetString()!.Length <= max;
            bool Int(string key, int min, int max) => s.GetProperty(key).TryGetInt32(out var v) && v >= min && v <= max;
            bool Ids(JsonElement a) => a.ValueKind == JsonValueKind.Array && a.GetArrayLength() <= 65 &&
                a.EnumerateArray().All(v => Text(v, 128) && v.GetString()!.Length > 0) &&
                a.EnumerateArray().Select(v => v.GetString()).Distinct(StringComparer.Ordinal).Count() == a.GetArrayLength();
            var answers = s.GetProperty("Answers");
            var joined = s.GetProperty("JoinedIds");
            var answered = s.GetProperty("AnsweredIds");
            var players = s.GetProperty("Players");
            var phase = s.GetProperty("Phase").GetInt32();
            var ends = s.GetProperty("PhaseEndsUnixMs").GetInt64();
            if (s.GetProperty("SessionId").GetString() != session || !Int("Revision", 1, int.MaxValue) ||
                !Int("Phase", 1, 4) || !Int("QuestionCount", 1, 25) || !Int("QuestionNumber", 1, s.GetProperty("QuestionCount").GetInt32() + (phase == 4 ? 1 : 0)) ||
                !(ends is >= 0 and <= 253402300799999 || ends == long.MaxValue) ||
                !Text(s.GetProperty("Category"), 512) || !Text(s.GetProperty("Question"), 8192) || !Text(s.GetProperty("WinnerName"), 256) ||
                answers.ValueKind != JsonValueKind.Array || !(answers.GetArrayLength() is 2 or 4 || phase == 4 && answers.GetArrayLength() == 0) || !answers.EnumerateArray().All(v => Text(v, 4096)) ||
                !Int("CorrectIndex", -1, answers.GetArrayLength() - 1) || (phase < 3 && s.GetProperty("CorrectIndex").GetInt32() != -1) ||
                !Ids(joined) || !Ids(answered) || answered.EnumerateArray().Any(v => !joined.EnumerateArray().Any(j => j.GetString() == v.GetString())) ||
                players.ValueKind != JsonValueKind.Array || players.GetArrayLength() > 65 ||
                players.EnumerateArray().Select(p => p.GetProperty("Id").GetString()).Distinct(StringComparer.Ordinal).Count() != players.GetArrayLength() ||
                players.EnumerateArray().Any(p => !Text(p.GetProperty("Id"), 128) || p.GetProperty("Id").GetString()!.Length == 0 ||
                    !Text(p.GetProperty("Name"), 256) || !p.GetProperty("Score").TryGetInt32(out var score) || score < 0 ||
                    p.GetProperty("AnswerCorrect").ValueKind is not (JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False) ||
                    (phase < 3 && p.GetProperty("AnswerCorrect").ValueKind != JsonValueKind.Null)) ||
                new[] { "FirstCorrectEnds", "Paused", "TimedOut" }.Any(k => s.GetProperty(k).ValueKind is not (JsonValueKind.True or JsonValueKind.False)) ||
                !Int("QuestionSeconds", 5, 120)) return null;
            return s.GetProperty("Revision").GetInt32();
        }
        catch { return null; }
    }
}

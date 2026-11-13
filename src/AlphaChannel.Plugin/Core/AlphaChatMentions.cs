namespace AlphaChannel.Plugin;

internal static class AlphaChatMentions
{
    private static bool NameCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';

    internal static bool Contains(string text, string? localName, IEnumerable<string> roomNames)
    {
        if (string.IsNullOrWhiteSpace(localName)) return false;
        var names = roomNames.Append(localName).Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length).ToArray();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '@' || i > 0 && (NameCharacter(text[i - 1]) || text[i - 1] == '@')) continue;
            foreach (var name in names)
            {
                var end = i + 1 + name.Length;
                if (end > text.Length || !text.AsSpan(i + 1, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    end < text.Length && NameCharacter(text[end])) continue;
                if (string.Equals(name, localName, StringComparison.OrdinalIgnoreCase)) return true;
                break; // A longer roster name owns this mention, rather than its shorter prefix.
            }
        }
        return false;
    }

    internal static bool TryCompletion(string text, int caret, out int start, out int end, out string query)
    {
        start = end = 0; query = "";
        caret = Math.Clamp(caret, 0, text.Length);
        if (caret == 0) return false;
        start = text.LastIndexOf('@', caret - 1);
        if (start < 0 || start > 0 && (NameCharacter(text[start - 1]) || text[start - 1] == '@')) return false;
        query = text[(start + 1)..caret];
        if (query.Any(c => !NameCharacter(c) && c != ' ' && c != '-' && c != '\'')) return false;
        end = caret;
        while (end < text.Length && NameCharacter(text[end])) end++;
        return true;
    }

    internal static string? Complete(string text, int start, int end, string name, out int caret)
    {
        var insertion = "@" + name + " ";
        var result = text[..start] + insertion + text[end..];
        caret = start + insertion.Length;
        return result.Length <= 1000 ? result : null;
    }
}

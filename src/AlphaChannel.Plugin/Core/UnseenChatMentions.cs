namespace AlphaChannel.Plugin;

// Session-only state. Seeing or jumping to a mention consumes it permanently
// until the room is left; rendering it again must not re-register it.
internal sealed class UnseenChatMentions
{
    private readonly HashSet<Guid> unseen = [];
    private readonly HashSet<Guid> seen = [];
    internal int Count => unseen.Count;
    internal void Add(Guid id)
    {
        if (!seen.Contains(id)) unseen.Add(id);
    }
    internal bool Remove(Guid id)
    {
        seen.Add(id);
        return unseen.Remove(id);
    }
    internal void Clear()
    {
        unseen.Clear();
        seen.Clear();
    }
    internal void IntersectWith(IEnumerable<Guid> retained)
    {
        var ids = retained.ToHashSet();
        unseen.IntersectWith(ids);
        seen.IntersectWith(ids);
    }

    internal Guid? TakeNewest(IEnumerable<Guid> chronologicalIds)
    {
        Guid? latest = null;
        foreach (var id in chronologicalIds)
            if (unseen.Contains(id)) latest = id;
        if (latest is { } selected) Remove(selected);
        return latest;
    }

    internal static bool Matches(string text, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var mention = "@" + name;
        for (var start = 0; start < text.Length;)
        {
            var index = text.IndexOf(mention, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            var end = index + mention.Length;
            if ((index == 0 || !IsNameCharacter(text[index - 1])) &&
                (end == text.Length || !IsNameCharacter(text[end]))) return true;
            start = end;
        }
        return false;
    }

    private static bool IsNameCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';
}

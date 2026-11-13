using System.Collections;

namespace AlphaChannel.Plugin;

// Plaintext is transient; encrypted persistent history is kept separately.
internal sealed class BoundedPlaintextCache(int capacity) : IReadOnlyDictionary<string, string>
{
    private readonly object gate = new();
    private readonly Dictionary<string, (string Value, LinkedListNode<string> Node)> entries = new();
    private readonly LinkedList<string> recent = new();

    public string this[string key]
    {
        get => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();
        set
        {
            lock (gate)
            {
                if (entries.Remove(key, out var old)) recent.Remove(old.Node);
                entries[key] = (value, recent.AddLast(key));
                while (entries.Count > capacity)
                {
                    var oldest = recent.First!;
                    recent.RemoveFirst();
                    entries.Remove(oldest.Value);
                }
            }
        }
    }

    public bool TryGetValue(string key, out string value)
    {
        lock (gate)
        {
            if (entries.TryGetValue(key, out var item))
            {
                recent.Remove(item.Node);
                recent.AddLast(item.Node);
                value = item.Value;
                return true;
            }
            value = "";
            return false;
        }
    }

    public bool TryRemove(string key, out string value)
    {
        lock (gate)
        {
            if (entries.Remove(key, out var item))
            {
                recent.Remove(item.Node);
                value = item.Value;
                return true;
            }
            value = "";
            return false;
        }
    }

    public bool ContainsKey(string key) { lock (gate) return entries.ContainsKey(key); }
    public int Count { get { lock (gate) return entries.Count; } }
    public IEnumerable<string> Keys { get { lock (gate) return entries.Keys.ToArray(); } }
    public IEnumerable<string> Values { get { lock (gate) return entries.Values.Select(e => e.Value).ToArray(); } }
    public void Clear() { lock (gate) { entries.Clear(); recent.Clear(); } }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        lock (gate)
            return entries.Select(e => new KeyValuePair<string, string>(e.Key, e.Value.Value))
                .ToArray().AsEnumerable().GetEnumerator();
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

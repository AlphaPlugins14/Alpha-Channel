using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AlphaChannel.Plugin.Auth;

// The entire on-disk cache is encrypted with the fetched room key. No key is persisted.
internal sealed class AlphaChatHistoryCache(string path, Action<string>? warning = null)
{
    internal const int Limit = 500;
    private readonly SortedDictionary<long, JsonElement> messages = new();
    internal IEnumerable<JsonElement> Messages => messages.Values;
    private sealed record Envelope(int Version, string Nonce, string Ciphertext);
    private static byte[] Aad(string accountId) => Encoding.UTF8.GetBytes(accountId + "\nalpha-chat-local-history-v1");

    internal void Load(byte[] key, string accountId)
    {
        messages.Clear();
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 12 * 1024 * 1024) throw new InvalidDataException();
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path))!;
            if (envelope.Version != 1) throw new InvalidDataException();
            var bytes = Convert.FromBase64String(envelope.Ciphertext);
            if (bytes.Length < 16 || bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException();
            var plain = new byte[bytes.Length - 16];
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(Convert.FromBase64String(envelope.Nonce), bytes.AsSpan(0, plain.Length), bytes.AsSpan(plain.Length), plain, Aad(accountId));
                Merge(JsonSerializer.Deserialize<JsonElement[]>(plain) ?? []);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception) { messages.Clear(); warning?.Invoke("[Alpha Chat] Local encrypted history could not be read; rebuilding from server history."); }
    }

    internal void Merge(IEnumerable<JsonElement> incoming)
    {
        foreach (var message in incoming)
        {
            try
            {
                var sequence = message.GetProperty("sequence").GetInt64();
                var request = message.GetProperty("requestId").GetString();
                var sender = message.GetProperty("sender").GetProperty("accountId").GetString();
                if (sequence < 1 || !Guid.TryParse(request, out _) || !Guid.TryParse(sender, out _) ||
                    message.GetProperty("kind").GetString() is not ("text" or "room") ||
                    message.GetProperty("keyVersion").GetInt32() != 1 ||
                    Convert.FromBase64String(message.GetProperty("nonce").GetString()!).Length != 12 ||
                    Convert.FromBase64String(message.GetProperty("ciphertext").GetString()!).Length is < 17 or > 16000) continue;
                foreach (var duplicate in messages.Where(x => x.Key != sequence &&
                    x.Value.GetProperty("requestId").GetString() == request &&
                    x.Value.GetProperty("sender").GetProperty("accountId").GetString() == sender).Select(x => x.Key).ToArray()) messages.Remove(duplicate);
                messages[sequence] = message.Clone();
            }
            catch (Exception) { /* Ignore invalid cache records. */ }
        }
        while (messages.Count > Limit) messages.Remove(messages.Keys.First());
    }

    internal void Reconcile(JsonElement[] serverHistory, IEnumerable<long> deleted)
    {
        foreach (var sequence in deleted) messages.Remove(sequence);
        var sequences = serverHistory.Select(m => m.GetProperty("sequence").GetInt64()).ToHashSet();
        if (sequences.Count == 0) return;
        // Missing entries inside the authoritative server window have been deleted.
        foreach (var sequence in messages.Keys.Where(id => id >= sequences.Min() && id <= sequences.Max() && !sequences.Contains(id)).ToArray()) messages.Remove(sequence);
    }

    internal void Remove(long sequence) => messages.Remove(sequence);

    internal void Save(byte[] key, string accountId)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(messages.Values);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[plain.Length]; var tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plain, cipher, tag, Aad(accountId));
            var envelope = new Envelope(1, Convert.ToBase64String(nonce), Convert.ToBase64String(cipher.Concat(tag).ToArray()));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(envelope));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception) { warning?.Invoke("[Alpha Chat] Local encrypted history could not be saved."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}

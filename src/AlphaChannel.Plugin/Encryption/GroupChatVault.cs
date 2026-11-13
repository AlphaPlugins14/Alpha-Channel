using System.Security.Cryptography;
using System.Text.Json;
using AlphaChannel.Plugin.Auth;

namespace AlphaChannel.Plugin.Crypto;

// The encrypted blob NEVER leaves this installation. Cloudflare knows the wrapping
// key, so uploading this blob there would destroy the separation that protects E2EE.
internal sealed class GroupChatVault : IDisposable
{
    private sealed class Contents
    {
        public string EncryptionPrivate { get; set; } = "";
        public string SigningPrivate { get; set; } = "";
        public Dictionary<string, string> Groups { get; set; } = [];
        public Dictionary<string, string> PinnedKeys { get; set; } = [];
        public Dictionary<string,string> DirectMediaMessages { get; set; } = [];
        public Dictionary<string,PendingDirectImage> PendingDirectImages { get; set; } = [];
    }
    private readonly string path, context;
    private readonly byte[] wrappingKey;
    private Contents contents;
    internal DmIdentity Identity { get; }
    internal byte[] SigningKey { get; }
    internal GroupChatVault(string directory, string account, string device, byte[] wrappingKey)
    {
        Directory.CreateDirectory(directory);
        this.wrappingKey = wrappingKey.ToArray();
        path = Path.Combine(directory, $"{account}.{device}.vault"); context = $"AC-GROUP-VAULT-1\n{account}\n{device}";
        if (File.Exists(path)) {
            var packet = JsonSerializer.Deserialize<GroupChatCrypto.Sealed>(File.ReadAllText(path)) ?? throw new CryptographicException("Invalid group vault");
            var opened = GroupChatCrypto.Open(this.wrappingKey, packet, context);
            try { contents = JsonSerializer.Deserialize<Contents>(opened) ?? throw new CryptographicException("Invalid group vault"); }
            finally { CryptographicOperations.ZeroMemory(opened); }
        } else {
            var identity = DmCipher.GenerateIdentity(); var signing = GroupChatCrypto.SigningPrivateKey();
            contents = new() { EncryptionPrivate = Convert.ToBase64String(DmCipher.ExportPrivateKey(identity)), SigningPrivate = Convert.ToBase64String(signing) };
            CryptographicOperations.ZeroMemory(signing); Save();
        }
        Identity = DmCipher.ImportPrivateKey(Convert.FromBase64String(contents.EncryptionPrivate));
        SigningKey = Convert.FromBase64String(contents.SigningPrivate);
    }
    internal byte[]? GroupKey(string group) => contents.Groups.TryGetValue(group, out var key) ? Convert.FromBase64String(key) : null;
    private string MediaPath(string id) => Guid.TryParseExact(id, "D", out _) ? path + "." + id + ".media" : throw new CryptographicException("Invalid media identifier");
    internal void StoreMedia(string id, byte[] ciphertext) => WriteEncrypted(MediaPath(id), GroupChatCrypto.Seal(wrappingKey, ciphertext, context + "\nmedia:" + id));
    internal byte[]? ReadMedia(string id)
    {
        var target = MediaPath(id);
        if (!File.Exists(target)) return null;
        if (new FileInfo(target).Length > 6 * 1024 * 1024) throw new CryptographicException("Invalid image cache size");
        var packet = JsonSerializer.Deserialize<GroupChatCrypto.Sealed>(File.ReadAllText(target)) ?? throw new CryptographicException("Invalid image cache");
        return GroupChatCrypto.Open(wrappingKey, packet, context + "\nmedia:" + id);
    }
    internal void DeleteMedia(string id) { var target = MediaPath(id); if (File.Exists(target)) File.Delete(target); }
    internal PendingDirectImage? PendingDirect(string fingerprint)=>contents.PendingDirectImages.GetValueOrDefault(fingerprint);
    internal void StorePendingDirect(string fingerprint,PendingDirectImage pending) {
        foreach(var key in contents.PendingDirectImages.Where(p=>p.Value.ExpiresAt<=DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Select(p=>p.Key).ToArray()) contents.PendingDirectImages.Remove(key);
        contents.PendingDirectImages[fingerprint]=pending;Save();
    }
    internal void ForgetPendingDirect(string fingerprint){contents.PendingDirectImages.Remove(fingerprint);Save();}
    internal void RememberDirectMedia(string conversation,string message,string image)
    {
        var key=conversation+":"+message;
        if(contents.DirectMediaMessages.GetValueOrDefault(key)==image)return;
        contents.DirectMediaMessages[key]=image;Save();
    }
    internal void ForgetDirectMessageMedia(string conversation,string message)
    {
        var key=conversation+":"+message;
        if(!contents.DirectMediaMessages.TryGetValue(key,out var image))return;
        DeleteMedia(image);contents.DirectMediaMessages.Remove(key);Save();
    }
    internal void StoreGroupKey(string group, byte[] key) { contents.Groups[group] = Convert.ToBase64String(key); Save(); }
    internal void StoreHistory(string group, GroupMessage[] messages)
    {
        if (!Guid.TryParse(group, out _)) throw new CryptographicException("Invalid group identifier");
        var historyPath = path + "." + group + ".history";
        GroupMessage[] previous = [];
        if (File.Exists(historyPath)) {
            try {
                if (new FileInfo(historyPath).Length > 32 * 1024 * 1024) throw new CryptographicException("Invalid cache size");
                var packet = JsonSerializer.Deserialize<GroupChatCrypto.Sealed>(File.ReadAllText(historyPath))!;
                var opened = GroupChatCrypto.Open(wrappingKey, packet, context + "\nhistory:" + group);
                try { previous = JsonSerializer.Deserialize<GroupMessage[]>(opened) ?? []; }
                finally { CryptographicOperations.ZeroMemory(opened); }
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or CryptographicException or Org.BouncyCastle.Crypto.InvalidCipherTextException) {
                previous = []; // Canonical history remains on the group service.
            }
        }
        var combined = previous.Concat(messages).GroupBy(m => m.Id).Select(items => items.Last())
            .OrderByDescending(m => m.Sequence).Take(500).ToArray();
        var plain = JsonSerializer.SerializeToUtf8Bytes(combined);
        try { WriteEncrypted(historyPath, GroupChatCrypto.Seal(wrappingKey, plain, context + "\nhistory:" + group)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal void Pin(string identity, string publicKey)
    {
        if (contents.PinnedKeys.TryGetValue(identity, out var previous) && previous != publicKey)
            throw new CryptographicException("A group encryption identity changed. Restore the original vault or verify the member before continuing.");
        if (!contents.PinnedKeys.ContainsKey(identity)) { contents.PinnedKeys[identity] = publicKey; Save(); }
    }
    private void Save()
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(contents);
        try {
            WriteEncrypted(path, GroupChatCrypto.Seal(wrappingKey, plain, context));
        } finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static void WriteEncrypted(string target, GroupChatCrypto.Sealed packet)
    {
        var temporary = target + ".writing";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(packet); file.Write(bytes); file.Flush(true);
        }
        File.Move(temporary, target, true);
    }
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(wrappingKey); CryptographicOperations.ZeroMemory(SigningKey);
        contents.Groups.Clear(); contents.PinnedKeys.Clear(); contents.DirectMediaMessages.Clear(); contents.PendingDirectImages.Clear(); contents.EncryptionPrivate = contents.SigningPrivate = "";
        // Bouncy Castle scalar objects and managed strings are GC-owned; zeroization
        // of every historical copy cannot be guaranteed. Never claim otherwise.
    }
}

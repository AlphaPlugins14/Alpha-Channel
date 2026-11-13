using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AlphaChannel.Plugin.Auth;

internal sealed record AlphaChatUser(string AccountId, string DisplayName, string? AvatarIcon,
    string AvatarColorHex, string? AvatarImageUrl, int PatreonTier, bool ChatActive = true, string? NameColourHex = null,
    string NameEffect = "solid", long NameEffectUntil = 0, bool IsGuest = false);
internal sealed record AlphaChatMessage(long Sequence, string RequestId, AlphaChatUser Sender,
    long Timestamp, string Kind, string Content, bool IsHistory = false, long? ReplyTo = null, string[]? MentionAccountIds = null, string? Evidence = null);
internal sealed record AlphaChatReactionUser(string AccountId, string DisplayName);
internal sealed record AlphaChatReaction(long Sequence, int Emoji, int Count, bool ReactedByMe, AlphaChatReactionUser[]? Users = null);

internal static class AlphaChatCrypto
{
    internal static (string Nonce, string Ciphertext) Encrypt(byte[] key, string text, string accountId, string requestId, string kind)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(text);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes($"{accountId}\n{requestId}\n{kind}"));
        CryptographicOperations.ZeroMemory(plain);
        return (Convert.ToBase64String(nonce), Convert.ToBase64String(cipher.Concat(tag).ToArray()));
    }
    internal static string Decrypt(byte[] key, string nonce, string ciphertext, string accountId, string requestId, string kind)
    {
        var bytes = Convert.FromBase64String(ciphertext);
        if (bytes.Length < 16 || bytes.Length > 16000) throw new CryptographicException("Invalid chat payload.");
        var plain = new byte[bytes.Length - 16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(nonce), bytes.AsSpan(0, plain.Length), bytes.AsSpan(plain.Length), plain,
                Encoding.UTF8.GetBytes($"{accountId}\n{requestId}\n{kind}"));
            return Encoding.UTF8.GetString(plain);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}

internal sealed class AlphaChatClient : IDisposable
{
    internal const string WorkerUrl = "https://alpha-chat.example.invalid";
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal readonly ConcurrentQueue<JsonElement> Events = new();
    internal readonly ConcurrentQueue<AlphaChatMessage> Messages = new();
    internal string Status { get; private set; } = "";
    internal bool Connected { get; private set; }
    internal bool Joining { get; private set; }
    internal bool ChatSubscribed { get; private set; }
    internal int MemberCount { get; private set; }
    internal bool BackgroundUnread { get; private set; }
    internal bool Stopped => terminal;
    private volatile bool wantChat;
    private bool rosterWatching;
    private bool modernConnection;
    private DateTime nextSubscribeUtc;
    private volatile bool encryptionVerified;
    private volatile bool encryptionError;
    private int encryptionRepairRequested;
    internal bool EncryptionHealthy => Connected && encryptionVerified && !encryptionError;
    internal bool Active => runner is { IsCompleted: false };
    internal string AccountId { get; private set; } = "";
    internal string Token { get; private set; } = "";
    private CancellationTokenSource? lifetime;
    private Task? runner;
    private ClientWebSocket? socket;
    private byte[]? key;
    private AlphaChatHistoryCache? historyCache;
    private bool historyCacheLoaded;
    private string? sessionId;
    private bool terminal;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, string> pending = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> receipts = new();
    private readonly string endpoint;
    internal AlphaChatClient(string? endpoint = null) => this.endpoint = endpoint ?? WorkerUrl;
    private DateTime lastPost = DateTime.UtcNow;
    private volatile string? usernameColourHex;
    internal string RequestedUsernameEffect { get; private set; } = "solid";
    internal string ApprovedUsernameEffect { get; private set; } = "solid";
    internal long ApprovedUsernameEffectUntil { get; private set; }
    internal static string? NormalizeUsernameColour(string? colour)
    {
        if (colour is null) return null;
        if (colour.Length != 7 || colour[0] != '#' || colour.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF"))
            throw new ArgumentException("Invalid username colour.");
        return colour.ToUpperInvariant();
    }
    internal void SetUsernameColour(string? colour)
        => SetUsernameStyle(colour, RequestedUsernameEffect);
    private bool usernameStyleManaged;
    internal void SetUsernameStyle(string? colour, string effect, bool publish = true)
    {
        usernameColourHex = NormalizeUsernameColour(colour);
        RequestedUsernameEffect = AlphaChatNameEffects.Normalize(effect);
        if (Connected && publish) _ = CommandAsync(new { type = "profile", nameColourHex = usernameColourHex, nameEffect = RequestedUsernameEffect, accountToken = Token });
    }

    internal void Join(string token, string accountId, string? cacheDirectory = null, Action<string>? cacheWarning = null, bool presenceOnly = false, string? nameColourHex = null, string nameEffect = "solid", bool styleManaged = false)
    {
        if (Active) { if (!presenceOnly) OpenChat(); return; }
        if (!AesGcm.IsSupported) { Status = "AES-GCM encryption isn't supported in this environment."; return; }
        lifetime?.Dispose(); lifetime = new();
        AccountId = Guid.Parse(accountId).ToString("D"); Token = token;
        usernameColourHex = NormalizeUsernameColour(nameColourHex);
        usernameStyleManaged = styleManaged;
        RequestedUsernameEffect = AlphaChatNameEffects.Normalize(nameEffect); ApprovedUsernameEffect = "solid"; ApprovedUsernameEffectUntil = 0;
        historyCache = cacheDirectory is null ? null : new AlphaChatHistoryCache(Path.Combine(cacheDirectory, Guid.Parse(AccountId).ToString("N") + ".json"), cacheWarning);
        historyCacheLoaded = false;
        sessionId = null; terminal = false; modernConnection = presenceOnly; wantChat = !presenceOnly; ChatSubscribed = false; MemberCount = 0; BackgroundUnread = false; rosterWatching = false; Joining = true; Status = "Connecting…"; lastPost = DateTime.UtcNow;
        pending.Clear(); ClearKey();
        runner = RunAsync(lifetime.Token);
    }
    internal void Leave(string reason = "")
    {
        terminal = true; Connected = false; ChatSubscribed = false; Joining = false; Status = reason;
        lifetime?.Cancel(); socket?.Abort(); pending.Clear();
        foreach (var receipt in receipts.Values) receipt.TrySetResult(false);
        receipts.Clear(); ClearKey();
    }
    internal void OpenChat()
    {
        wantChat = true;
        if (!Connected || ChatSubscribed || DateTime.UtcNow < nextSubscribeUtc) return;
        nextSubscribeUtc = DateTime.UtcNow.AddSeconds(1);
        _ = CommandAsync(new { type = "subscribe" });
    }
    internal void Background()
    {
        wantChat = false;
        if (Connected && ChatSubscribed) _ = CommandAsync(new { type = "presence" });
        ChatSubscribed = false;
        SetRosterWatching(false);
    }
    internal void SetRosterWatching(bool watching)
    {
        if (rosterWatching == watching) return;
        rosterWatching = watching;
        if (Connected) _ = CommandAsync(new { type = "roster", watching });
    }
    internal void MarkChatRead() => BackgroundUnread = false;

    internal void RepairEncryption()
    {
        if (!Active || terminal || Joining || EncryptionHealthy) return;
        encryptionVerified = false; encryptionError = true;
        Interlocked.Exchange(ref encryptionRepairRequested, 1);
        Connected = false; Joining = true; Status = "Checking chat encryption…";
        // Resume the existing authenticated session: preserve pending IDs and the server idle deadline.
        socket?.Abort();
    }
    private bool VerifyEncryptionProof(JsonElement welcome)
    {
        try
        {
            var proof = welcome.GetProperty("encryptionProof");
            return AlphaChatCrypto.Decrypt(key!, proof.GetProperty("nonce").GetString()!,
                proof.GetProperty("ciphertext").GetString()!, AccountId, sessionId!, "proof") == "Alpha Chat Encryption Check v1";
        }
        catch (Exception) { return false; }
    }
    private async Task RunAsync(CancellationToken ct)
    {
        var retry = 0;
        try
        {
            while (!ct.IsCancellationRequested && !terminal)
            {
                if (modernConnection && wantChat && DateTime.UtcNow - lastPost >= TimeSpan.FromMinutes(30)) wantChat = false;
                using var ws = new ClientWebSocket(); socket = ws;
                ws.Options.SetRequestHeader("Authorization", "Bearer " + Token);
                ws.Options.CollectHttpResponseDetails = true;
                try
                {
                    var query = new List<string> { "reactionNames=1" };
                    query.Add("version=" + Uri.EscapeDataString(typeof(AlphaChatClient).Assembly.GetName().Version?.ToString() ?? "Unknown"));
                    if (modernConnection) query.Add("mode=" + (wantChat ? "chat" : "presence"));
                    if (sessionId is not null) query.Add("resume=" + Uri.EscapeDataString(sessionId));
                    if (usernameColourHex is { } colour) query.Add("nameColourHex=" + Uri.EscapeDataString(colour));
                    if (RequestedUsernameEffect != "solid") query.Add("nameEffect=" + Uri.EscapeDataString(RequestedUsernameEffect));
                    await ws.ConnectAsync(new Uri(endpoint.Replace("https:", "wss:").Replace("http:", "ws:") + "/connect" +
                        (query.Count == 0 ? "" : "?" + string.Join("&", query))), ct).ConfigureAwait(false);
                    using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var heartbeat = HeartbeatAsync(connection.Token);
                    try { await ReceiveAsync(ws, connection.Token).ConfigureAwait(false); }
                    finally { connection.Cancel(); try { await heartbeat.ConfigureAwait(false); } catch (OperationCanceledException) { } }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (WebSocketException) when (ws.HttpStatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.Conflict)
                {
                    terminal = true;
                    Status = ws.HttpStatusCode == HttpStatusCode.Conflict ? "Disconnected due to inactivity. Join again to continue."
                        : ws.HttpStatusCode == HttpStatusCode.Unauthorized ? "Please sign in again before joining Alpha Chat."
                        : "Alpha Chat access is blocked or temporarily suspended.";
                }
                catch (Exception) { Status = "Connection lost. Reconnecting…"; }
                Connected = false;
                if (terminal || ct.IsCancellationRequested) break;
                if (!modernConnection && DateTime.UtcNow - lastPost >= TimeSpan.FromMinutes(30))
                { terminal = true; Status = "Disconnected due to inactivity. Join again to continue."; break; }
                Joining = true;
                var repairing = Interlocked.Exchange(ref encryptionRepairRequested, 0) != 0;
                if (repairing) retry = 0;
                await Task.Delay(repairing ? TimeSpan.FromMilliseconds(100) :
                    TimeSpan.FromSeconds(Math.Min(20, 1 << Math.Min(retry++, 4))), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally { Connected = false; ChatSubscribed = false; Joining = false; socket = null; ClearKey(); }
    }
    private async Task HeartbeatAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (ChatSubscribed && DateTime.UtcNow - lastPost >= TimeSpan.FromMinutes(30))
            {
                if (modernConnection) Background();
                else { Leave("Disconnected due to inactivity. Join again to continue."); return; }
            }
            try { await SendRawAsync("ping", ct).ConfigureAwait(false); } catch { socket?.Abort(); return; }
        }
    }
    private async Task ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            using var data = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                data.Write(buffer, 0, result.Count);
                if (data.Length > 1024 * 1024) throw new InvalidDataException("Chat response too large.");
            } while (!result.EndOfMessage);
            var text = Encoding.UTF8.GetString(data.ToArray());
            if (text == "pong") continue;
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "welcome")
            {
                var user = root.GetProperty("user").Deserialize<AlphaChatUser>(JsonOptions)!;
                if (user.AccountId != AccountId) throw new InvalidDataException("Chat identity mismatch.");
                ApprovedUsernameEffect = user.NameEffect; ApprovedUsernameEffectUntil = user.NameEffectUntil;
                ClearKey(); key = Convert.FromBase64String(root.GetProperty("key").GetString()!);
                if (key.Length != 32 || root.GetProperty("keyVersion").GetInt32() != 1)
                { encryptionError = true; throw new InvalidDataException("Unsupported chat encryption."); }
                sessionId = root.GetProperty("sessionId").GetString();
                lastPost = DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("lastPost").GetInt64()).UtcDateTime;
                encryptionVerified = VerifyEncryptionProof(root); encryptionError = !encryptionVerified;
                ChatSubscribed = !root.TryGetProperty("mode", out var mode) || mode.GetString() == "chat";
                MemberCount = root.TryGetProperty("count", out var count) ? count.GetInt32() : root.GetProperty("roster").GetArrayLength();
                if (ChatSubscribed) BackgroundUnread = false;
                else if (modernConnection && DateTime.UtcNow - lastPost >= TimeSpan.FromMinutes(30)) wantChat = false;
                Connected = true; Joining = false; Status = encryptionVerified ? (ChatSubscribed ? "Connected" : "Background presence") : "Encryption error. Press the padlock to attempt repair.";
                if (!usernameStyleManaged && user.NameColourHex != usernameColourHex) await CommandAsync(new { type = "profile", nameColourHex = usernameColourHex, nameEffect = RequestedUsernameEffect, accountToken = Token });
                // Navigation can change while the subscription welcome is in flight.
                if (modernConnection && ChatSubscribed && !wantChat) Background();
                if (ChatSubscribed) while (Messages.TryDequeue(out _)) { }
                Events.Enqueue(root.Clone());
                var serverHistory = root.GetProperty("history").EnumerateArray().Select(m => m.Clone()).ToArray();
                if (ChatSubscribed && historyCache is not null && encryptionVerified)
                {
                    if (!historyCacheLoaded) { historyCache.Load(key, AccountId); historyCacheLoaded = true; }
                    historyCache.Merge(serverHistory);
                    var deleted = root.TryGetProperty("deleted", out var tombstones) ? tombstones.EnumerateArray().Select(x => x.GetInt64()).ToArray() : [];
                    historyCache.Reconcile(serverHistory, deleted);
                    foreach (var message in historyCache.Messages.ToArray())
                        if (!DecodeMessage(message, isHistory: true)) historyCache.Remove(message.GetProperty("sequence").GetInt64());
                    historyCache.Save(key, AccountId);
                }
                else if (ChatSubscribed) foreach (var message in serverHistory) DecodeMessage(message, isHistory: true);
                if (Connected && modernConnection && wantChat && !ChatSubscribed) OpenChat();
                if (rosterWatching) await CommandAsync(new { type = "roster", watching = true });
                if (EncryptionHealthy && ChatSubscribed) foreach (var post in pending.Values) await SendRawAsync(post, ct).ConfigureAwait(false);
            }
            else if (type is "message" or "mention")
            {
                var message = root.GetProperty("message");
                if (DecodeMessage(message) && historyCache is not null && key is not null)
                { if (!historyCacheLoaded) { historyCache.Load(key, AccountId); historyCacheLoaded = true; } historyCache.Merge([message]); historyCache.Save(key, AccountId); }
            }
            else if (type == "deleted")
            {
                if (historyCache is not null && key is not null && encryptionVerified && !historyCacheLoaded) { historyCache.Load(key, AccountId); historyCacheLoaded = true; }
                historyCache?.Remove(root.GetProperty("sequence").GetInt64());
                if (historyCache is not null && key is not null && encryptionVerified) historyCache.Save(key, AccountId);
                Events.Enqueue(root.Clone());
            }
            else if (type == "ack")
            { var accepted = root.GetProperty("requestId").GetString()!; pending.TryRemove(accepted, out _);
                if (receipts.TryRemove(accepted, out var receipt)) receipt.TrySetResult(true); lastPost = DateTime.UtcNow; Status = EncryptionHealthy ? "Connected" : "Encryption error. Press the padlock to attempt repair."; }
            else if (type == "presence")
            { MemberCount = root.GetProperty("count").GetInt32(); Events.Enqueue(root.Clone()); }
            else if (type == "roster")
            { MemberCount = root.GetProperty("users").GetArrayLength(); Events.Enqueue(root.Clone()); }
            else if (type == "activity")
            { BackgroundUnread = true; Events.Enqueue(root.Clone()); }
            else if (type == "user")
            {
                var changed = root.GetProperty("user").Deserialize<AlphaChatUser>(JsonOptions)!;
                if (changed.AccountId == AccountId) { ApprovedUsernameEffect = changed.NameEffect; ApprovedUsernameEffectUntil = changed.NameEffectUntil; }
                Events.Enqueue(root.Clone());
            }
            else if (type == "mode")
            {
                ChatSubscribed = root.GetProperty("mode").GetString() == "chat";
                if (!ChatSubscribed) { wantChat = false; Status = root.TryGetProperty("reason", out var reason) && reason.GetString() == "inactive" ? "Chat paused after 30 minutes without posting. Join again to continue." : "Background presence"; }
                Events.Enqueue(root.Clone());
            }
            else if (type == "closed")
            {
                var reason = root.GetProperty("reason").GetString();
                if (reason is "reauthenticate" or "connection_error") { Status = "Refreshing chat connection…"; return; }
                var until = root.GetProperty("until").GetInt64();
                if (reason == "inactive" && modernConnection) { Background(); return; }
                Leave(reason == "inactive" ? "Disconnected due to inactivity. Join again to continue."
                    : reason == "kicked" ? "Chat suspended until " + DateTimeOffset.FromUnixTimeMilliseconds(until).LocalDateTime.ToString("g")
                    : "You are banned from Alpha Chat."); return;
            }
            else
            {
                if (type == "error") Status = root.GetProperty("error").GetString() switch
                { "slow_down" => "Please wait a moment before posting again.", "share_cooldown" => "You can share a room once per minute.", "locked_room" => "Locked parties can not be shared in chat", "muted" => "You are temporarily muted in Alpha Chat.", "posting_paused" => "An admin has paused chat posting.", "sharing_disabled" => "Room sharing is currently disabled.", "slow_mode" => "Slow mode is active. Wait before posting again.", _ => "The chat action could not be completed." };
                if (type == "error" && root.TryGetProperty("requestId", out var rejected) && rejected.ValueKind == JsonValueKind.String) { var id = rejected.GetString()!; pending.TryRemove(id, out _); if (receipts.TryRemove(id, out var receipt)) receipt.TrySetResult(false); }
                Events.Enqueue(root.Clone());
            }
        }
    }
    private bool DecodeMessage(JsonElement message, bool isHistory = false)
    {
        try
        {
            var sender = message.GetProperty("sender").Deserialize<AlphaChatUser>(JsonOptions)!;
            var requestId = message.GetProperty("requestId").GetString()!;
            var kind = message.GetProperty("kind").GetString()!;
            var decoded = AlphaChatCrypto.Decrypt(key!, message.GetProperty("nonce").GetString()!,
                message.GetProperty("ciphertext").GetString()!, sender.AccountId, requestId, kind);
            if (kind == "text" && decoded.Length > 1000) return false;
            Messages.Enqueue(new(message.GetProperty("sequence").GetInt64(), requestId, sender,
                message.GetProperty("timestamp").GetInt64(), kind, decoded, isHistory, message.TryGetProperty("replyTo", out var reply) && reply.ValueKind == JsonValueKind.Number ? reply.GetInt64() : null,
                message.TryGetProperty("mentions", out var mentions) ? mentions.Deserialize<string[]>(JsonOptions) : null,
                message.TryGetProperty("evidence", out var evidence) ? evidence.GetString() : null));
            while (Messages.Count > AlphaChatHistoryCache.Limit) Messages.TryDequeue(out _);
            return true;
        }
        catch (Exception) { encryptionError = true; Status = "A message couldn't be decrypted. Press the padlock to attempt repair."; return false; }
    }
    internal async Task<bool> PostTextAsync(string text, long? replyTo = null, string[]? mentions = null, string? requestId = null)
    {
        if (!ChatSubscribed || !EncryptionHealthy || key is null || string.IsNullOrWhiteSpace(text) || text.Length > 1000) return false;
        var id = Guid.TryParse(requestId, out var requested) ? requested.ToString("D") : Guid.NewGuid().ToString("D");
        if (receipts.TryGetValue(id, out var previous))
        {
            if (previous.Task.IsCompletedSuccessfully && previous.Task.Result) return true;
            if (pending.TryGetValue(id, out var retry))
            {
                try { await SendRawAsync(retry, lifetime!.Token); } catch { socket?.Abort(); }
                return await WaitForReceiptAsync(previous);
            }
        }
        try
        {
            var encrypted = AlphaChatCrypto.Encrypt(key, text, AccountId, id, "text");
            return await PostAsync(id, new { type = "post", requestId = id, kind = "text", keyVersion = 1, nonce = encrypted.Nonce, ciphertext = encrypted.Ciphertext, replyTo, mentions = mentions ?? Array.Empty<string>() });
        }
        catch (CryptographicException) { encryptionError = true; Status = "Encryption error. Your draft was kept."; return false; }
        catch { Status = "Couldn't send the message. Your draft was kept."; return false; }
    }
    internal Task<bool> ShareAsync(string hostId)
    {
        if (!ChatSubscribed || !EncryptionHealthy) return Task.FromResult(false);
        var id = Guid.NewGuid().ToString("D");
        return PostAsync(id, new { type = "post", requestId = id, kind = "room", hostAccountId = hostId, accountToken = Token });
    }
    private async Task<bool> PostAsync(string id, object body)
    {
        if (!Connected || pending.Count >= 8) return false;
        var raw = JsonSerializer.Serialize(body, JsonOptions); pending[id] = raw;
        var receipt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); receipts[id] = receipt;
        try { await SendRawAsync(raw, lifetime!.Token); }
        catch { Status = "Connection lost. Your post will retry after reconnecting."; socket?.Abort(); }
        // A missing acknowledgement stays queued and is replayed with the same deduplication ID.
        return await WaitForReceiptAsync(receipt);
    }
    private async Task<bool> WaitForReceiptAsync(TaskCompletionSource<bool> receipt)
    {
        var completed = await Task.WhenAny(receipt.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (completed == receipt.Task) return await receipt.Task;
        Status = "Still awaiting confirmation. Your draft is kept; retry uses the same message ID.";
        return false;
    }
    internal async Task CommandAsync(object command)
    {
        try { if (Connected) await SendRawAsync(JsonSerializer.Serialize(command, JsonOptions), lifetime!.Token); }
        catch { Status = "Couldn't complete the chat action."; }
    }
    private async Task SendRawAsync(string raw, CancellationToken ct)
    {
        await sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ws = socket;
            if (ws?.State != WebSocketState.Open) throw new WebSocketException();
            await ws.SendAsync(Encoding.UTF8.GetBytes(raw), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally { sendLock.Release(); }
    }
    private void ClearKey() { encryptionVerified = false; var old = key; key = null; if (old is not null) CryptographicOperations.ZeroMemory(old); }
    public void Dispose() => Leave();
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Crypto;

namespace AlphaChannel.Plugin.Auth;

internal sealed partial class GroupChatClient(string? serviceOrigin = null, string? localDirectory = null, HttpMessageHandler? transport = null,
    TimeSpan? renewalInterval = null) : IDisposable
{
    internal const string Origin = "https://group-chats.example.invalid";
    internal const string Prefix = "cg:";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<string> revokedMedia = new();
    private readonly SemaphoreSlim mediaTransfers = new(2, 2);
    private readonly AsyncLocal<(string Token, CancellationToken Cancellation)?> operation = new();
    private readonly object lifecycle = new();
    private static readonly SocketsHttpHandler Handler = new() { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) };
    private readonly HttpClient http = new(transport ?? Handler, disposeHandler: transport is not null) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string origin = serviceOrigin ?? Origin;
    private CancellationTokenSource? lifetime;
    private string? accountToken;
    private GroupSession? session;
    private GroupChatVault? vault;
    private string? deviceId;
    private string? stateAccountId;
    private bool disposed;
    private Task runner = Task.CompletedTask;
    private volatile bool ready;
    private GroupState state = new([], []);
    internal bool Ready => ready;
    internal string Status { get; private set; } = "Connecting to group chats…";
    internal string? LastOperationWarning { get; private set; }
    internal string? VerificationCode { get; private set; }
    internal GroupState State => ready ? Volatile.Read(ref state) : new([], []);
    internal bool IsCurrentAccount(string account, string token) => ready && accountToken == token && stateAccountId == account;
    internal event Action<string?, string?, string?>? Changed;
    internal event Action<string>? Locked;
    internal event Action<string>? ConnectionInterrupted;
    private sealed class RequestFailure(string message, System.Net.HttpStatusCode status, string path) : InvalidOperationException(message)
    {
        internal System.Net.HttpStatusCode Status { get; } = status;
        internal string Path { get; } = path;
    }
    internal static bool IsGroup(string? id) => id?.StartsWith(Prefix, StringComparison.Ordinal) == true;
    internal static string Raw(string id) => IsGroup(id) ? id[Prefix.Length..] : id;
    internal static bool IsMessage(MessageDto message) => IsGroup(message.Id);
    private string DirectoryPath => localDirectory ?? Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "PrivateGroups");

    internal void Start(string token)
    {
        lock (lifecycle) {
            if (disposed || accountToken == token) return;
            lifetime?.Cancel(); lifetime = new(); accountToken = token;
            ready = false; state = new([], []); stateAccountId = null;
            var cancellation = lifetime.Token; var previous = runner;
            runner = Task.Run(async () => {
                try { await previous.ConfigureAwait(false); } catch { }
                if (!cancellation.IsCancellationRequested) await RunAsync(token, cancellation).ConfigureAwait(false);
            });
        }
    }
    internal void Stop()
    {
        string? previousToken;
        lock (lifecycle) { previousToken = accountToken; accountToken = null; lifetime?.Cancel(); ready = false; state = new([], []); }
        if (previousToken is not null) Locked?.Invoke(previousToken);
    }
    private async Task<T> Request<T>(string path, HttpMethod method, object? data = null, string? bearer = null, string? sourceToken = null, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++) {
            try { return await RequestOnce<T>(path,method,data,bearer,sourceToken,ct).ConfigureAwait(false); }
            catch (HttpRequestException) when (method == HttpMethod.Get && attempt == 0 && !ct.IsCancellationRequested) {
                await Task.Delay(150,ct).ConfigureAwait(false);
            }
        }
    }
    private async Task<T> RequestOnce<T>(string path, HttpMethod method, object? data, string? bearer, string? sourceToken, CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, operation.Value?.Cancellation ?? CancellationToken.None);
        ct = cancellation.Token;
        using var req = new HttpRequestMessage(method, origin + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer ?? session?.Token ?? throw new InvalidOperationException("Group chats are disconnected."));
        if (sourceToken is not null) req.Headers.Add("X-Account-Token", sourceToken);
        if (data is not null) req.Content = JsonContent.Create(data, options: Json);
        using var response = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) {
            var error = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct).ConfigureAwait(false);
            throw new RequestFailure(error.TryGetProperty("error", out var message) ? message.GetString() ?? "Group service request failed." : "Group service request failed.", response.StatusCode, path);
        }
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false))!;
    }
    private async Task<T> Use<T>(string token, Func<Task<T>> action)
    {
        CancellationToken cancellation;
        lock (lifecycle) {
            if (disposed || accountToken != token || lifetime is null) throw new InvalidOperationException("Group chats are disconnected.");
            cancellation = lifetime.Token;
        }
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        var previous = operation.Value;
        operation.Value = (token,cancellation);
        try {
            if (accountToken != token || !ready || vault is null || session is null || session.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                throw new InvalidOperationException(Status);
            return await action().ConfigureAwait(false);
        } finally { operation.Value = previous; gate.Release(); }
    }
    // Only ciphertext transfers leave the state lock. Capture all routing/authentication
    // before releasing it, and revalidate ownership before touching the vault again.
    private async Task<T> Transfer<T>(Func<CancellationToken,Task<T>> transfer)
    {
        var context = operation.Value ?? throw new InvalidOperationException("No active chat operation.");
        var owner = vault;
        T? result=default;
        gate.Release();
        try {
            await mediaTransfers.WaitAsync(context.Cancellation).ConfigureAwait(false);
            try { result=await transfer(context.Cancellation).ConfigureAwait(false); return result; }
            finally { mediaTransfers.Release(); }
        } finally {
            await gate.WaitAsync().ConfigureAwait(false);
            if (context.Cancellation.IsCancellationRequested || accountToken != context.Token || !ReferenceEquals(owner,vault)) {
                if(result is IDisposable resource)resource.Dispose();
                if(result is byte[] bytes)CryptographicOperations.ZeroMemory(bytes);
                throw new OperationCanceledException("Chat account changed.",context.Cancellation);
            }
        }
    }
    private async Task RunAsync(string token, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested) {
            using var socket = new ClientWebSocket();
            try {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try {
                    ct.ThrowIfCancellationRequested();
                    System.IO.Directory.CreateDirectory(DirectoryPath);
                    var installationPath = Path.Combine(DirectoryPath, "installation.txt");
                    if (deviceId is null) {
                        deviceId = File.Exists(installationPath) ? File.ReadAllText(installationPath).Trim() : Guid.NewGuid().ToString();
                        if (!Guid.TryParse(deviceId, out _)) throw new InvalidOperationException("Invalid group installation identifier. Restore your PrivateGroups folder.");
                        if (!File.Exists(installationPath)) File.WriteAllText(installationPath, deviceId);
                    }
                    var authenticated = await Request<GroupSession>("/session", HttpMethod.Post, new { deviceId, renewable = true }, token, ct: ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (session?.AccountId != authenticated.AccountId) { vault?.Dispose(); vault = null; revokedMedia.Clear(); }
                    session = authenticated;
                    stateAccountId = authenticated.AccountId;
                    var wrapping = Convert.FromBase64String(authenticated.VaultKey);
                    try { vault ??= new GroupChatVault(DirectoryPath, authenticated.AccountId, deviceId, wrapping); }
                    finally { CryptographicOperations.ZeroMemory(wrapping); }
                    // Don't keep an extra copy of the wrapping key inside the session record.
                    session = authenticated with { VaultKey = "" };
                    await Request<JsonElement>("/identity", HttpMethod.Post, new {
                        encryptionKey = Convert.ToBase64String(DmCipher.ExportPublicKey(vault.Identity)),
                        signingKey = GroupChatCrypto.SigningPublicKey(vault.SigningKey)
                    }, ct: ct).ConfigureAwait(false);
                    VerificationCode = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(GroupChatCrypto.SigningPublicKey(vault.SigningKey))))[..12];
                    socket.Options.SetRequestHeader("Authorization", "Bearer " + session.Token);
                    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(60);
                    await socket.ConnectAsync(new Uri(origin.Replace("https:", "wss:").Replace("http:", "ws:") + "/socket"), ct).ConfigureAwait(false);
                    var previousState = state;
                    state = await Request<GroupState>("/state", HttpMethod.Get, ct: ct).ConfigureAwait(false);
                    ready = true; Status = "Connected"; attempt = 0;
                    foreach (var group in state.Groups) {
                        var cachedKey = vault.GroupKey(group.Id); var hasKey = cachedKey is not null;
                        if (cachedKey is not null) CryptographicOperations.ZeroMemory(cachedKey);
                        // A session renewal must not re-query every unchanged group's
                        // key directory. Membership/key events already keep it current.
                        if (hasKey && previousState.Groups.Any(old => old.Id == group.Id && old.Version == group.Version)) continue;
                        try { await Info(group.Id).ConfigureAwait(false); } catch { /* Missing keys require another authorised member. */ }
                    }
                } finally { gate.Release(); }
                Changed?.Invoke(null, null, null);
                using var renewal = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (session?.RenewInPlace != true) renewal.CancelAfter(TimeSpan.FromSeconds(270));
                async Task ReceiveUpdatesAsync()
                {
                var buffer = new byte[65536];
                while (!renewal.IsCancellationRequested) {
                    using var bytes = new MemoryStream(); WebSocketReceiveResult received;
                    do {
                        received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), renewal.Token).ConfigureAwait(false);
                        if (received.MessageType == WebSocketMessageType.Close) throw new IOException("Group connection closed");
                        bytes.Write(buffer, 0, received.Count); if (bytes.Length > 131072) throw new IOException("Oversized group event");
                    } while (!received.EndOfMessage);
                    if (Encoding.UTF8.GetString(bytes.ToArray()) == "pong") continue;
                    var e = JsonSerializer.Deserialize<GroupEvent>(bytes.ToArray(), Json)!;
                    await gate.WaitAsync(renewal.Token).ConfigureAwait(false);
                    try {
                        if (e.Revision <= state.Revision) continue;
                        var previousGroup = state.Groups.FirstOrDefault(g => g.Id == e.GroupId);
                        var groups = state.Groups.Where(g => g.Id != e.RemoveGroup && g.Id != e.Group?.Id).ToList();
                        if (e.Group is not null) groups.Add(e.Group);
                        var invites = state.Invitations.Where(i => i.Id != e.RemoveInvite && i.Id != e.Invitation?.Id).ToList();
                        if (e.Invitation is not null) invites.Add(e.Invitation);
                        state = new(groups.ToArray(), invites.ToArray(), e.Revision);
                        // Only membership/key events need key-directory lookups. Ordinary
                        // messages and read updates carry their own account projection.
                        if (e.GroupId is not null && e.Group is not null &&
                            (e.KeysChanged || previousGroup is null || previousGroup.Version != e.Group.Version)) {
                            try { await Info(e.GroupId).ConfigureAwait(false); } catch { }
                        }
                    }
                    finally { gate.Release(); }
                    Changed?.Invoke(e.GroupId, e.MessageId, e.SenderAccountId);
                }
                }
                var receiving = ReceiveUpdatesAsync();
                var renewing = session?.RenewInPlace == true
                    ? RenewSessionAsync(token, renewal.Token)
                    : Task.Delay(Timeout.Infinite, renewal.Token);
                await Task.WhenAny(receiving, renewing).ConfigureAwait(false);
                renewal.Cancel();
                // Observe both tasks before the next connection can touch session/vault state.
                try { await Task.WhenAll(receiving, renewing).ConfigureAwait(false); }
                catch when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
                if (receiving.IsFaulted) await receiving.ConfigureAwait(false);
                if (renewing.IsFaulted) await renewing.ConfigureAwait(false);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break;
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                // Expected short-session renewal. Keep the vault in memory while reauthenticating.
                ready = false;
            } catch (Exception) when (ct.IsCancellationRequested) { break;
            } catch (Exception e) {
                ready = false; Status = e is InvalidOperationException ? e.Message : "Group chats disconnected. Reconnecting…";
                // Release encryption material while offline, but keep already displayed
                // plaintext on ordinary transport failures. Authentication loss is a lock.
                var authenticationLost = e is RequestFailure { Path: "/session" or "/session/renew", Status: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden };
                await LockAsync(token, notify: authenticationLost).ConfigureAwait(false);
                if (!authenticationLost) ConnectionInterrupted?.Invoke(token);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 1 << Math.Min(++attempt, 6))), ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
            } finally { socket.Abort(); }
        }
        await LockAsync(token).ConfigureAwait(false);
    }
    private sealed record RenewedSession(string Token, long Expires);
    private async Task RenewSessionAsync(string token, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(renewalInterval ?? TimeSpan.FromSeconds(240), ct).ConfigureAwait(false);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (accountToken != token || session is null || !ready) throw new OperationCanceledException(ct);
                var renewed = await Request<RenewedSession>("/session/renew", HttpMethod.Post,
                    sourceToken: token, ct: ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(renewed.Token) || renewed.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    throw new InvalidDataException("Invalid renewed group session.");
                session = session with { Token = renewed.Token, Expires = renewed.Expires };
            }
            finally { gate.Release(); }
        }
    }
    private async Task LockAsync(string token, bool notify = true)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try {
            // A replacement account can start only after this gate; never clear its vault.
            vault?.Dispose(); vault = null; session = null; ready = false;
            if (notify) { state = new([], []); stateAccountId = null; }
        } finally { gate.Release(); }
        if (notify) Locked?.Invoke(token);
    }
    // Conversation metadata may stay visible for the same signed-in account while
    // reconnecting. Sending, decryption and authoritative State still require Ready.
    internal ConversationSummaryDto[] Conversations(string? token = null) => accountToken is null || token is not null && accountToken != token ? [] : Volatile.Read(ref state).Groups.Select(g => new ConversationSummaryDto(Prefix + g.Id, true, g.Name,
        g.Members.Where(m => m.AccountId != stateAccountId).Select(m => new ConversationMemberDto(m.AccountId, m.Handle, m.DisplayName)).ToArray(), g.LastMessageAtUnix, g.UnreadCount)).ToArray();
    internal Task<bool> HasIdentityAsync(string token, string account) => Use(token, async () =>
        (await Request<GroupDevices>($"/identities/{account}", HttpMethod.Get).ConfigureAwait(false)).Devices.Length > 0);
    private async Task<GroupInfo> Info(string group)
    {
        var info = await Request<GroupInfo>($"/groups/{group}", HttpMethod.Get).ConfigureAwait(false);
        foreach (var device in info.Devices) {
            vault!.Pin($"sign:{device.AccountId}:{device.DeviceId}", device.SigningKey);
            vault.Pin($"enc:{device.AccountId}:{device.DeviceId}", device.EncryptionKey);
        }
        var key = vault!.GroupKey(group);
        if (info.IsPublic) {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            key = Convert.FromBase64String(info.PublicKey ?? throw new CryptographicException("Public group key unavailable"));
            if (key.Length != 32) throw new CryptographicException("Invalid public group key");
            vault.StoreGroupKey(group, key); CryptographicOperations.ZeroMemory(key);
            return info;
        }
        if (key is null) {
            var packet = info.Packages.FirstOrDefault();
            if (packet is null) throw new InvalidOperationException("Waiting for a group key. Ask an existing member to open Key access and approve this installation. Your verification code is in the padlock menu.");
            vault.Pin($"enc:{packet.SenderAccountId}:{packet.SenderDeviceId}", packet.SenderEncryptionKey!);
            key = GroupChatCrypto.Unwrap(group, vault.Identity, packet);
            if (key.Length != 32) throw new CryptographicException("Invalid group key");
            vault.StoreGroupKey(group, key);
        }
        // Never silently distribute the secret merely because the directory lists a
        // new installation. Initial invitations provision explicitly; recovery needs
        // a member's explicit Key access approval. The relay must not be able to
        // obtain group secrets by silently inserting a device into its directory.
        CryptographicOperations.ZeroMemory(key);
        return info;
    }
    internal Task<bool> EnsureKeyAsync(string token, string conversation) => Use(token, async () => { await Info(Raw(conversation)).ConfigureAwait(false); return true; });
    internal bool IsPublic(string conversation) => accountToken is not null && Volatile.Read(ref state).Groups.Any(g => g.Id == Raw(conversation) && g.IsPublic);
    internal Task<PublicGroupListing[]> DirectoryAsync(string token) => Use(token, async () => {
        var result = new List<PublicGroupListing>(); var cursors = new HashSet<string>(); string? cursor = null;
        do {
            var page = await Request<PublicGroupDirectory>("/directory" + (cursor is null ? "" : "?after=" + Uri.EscapeDataString(cursor)), HttpMethod.Get).ConfigureAwait(false);
            result.AddRange(page.Groups); cursor = page.NextCursor;
        } while (cursor is not null && cursors.Add(cursor));
        return result.DistinctBy(g => g.Id).ToArray();
    });
    internal Task<PublicGroupListing> PreviewAsync(string token, string group) => Use(token, () => Request<PublicGroupListing>($"/groups/{Raw(group)}/preview", HttpMethod.Get));
    internal Task<string> CreatePublicAsync(string token, string name, string description, string category, string avatarIcon = "Users", string avatarColorHex = "#4D96FF") => Use(token, async () => {
        var group = Guid.NewGuid().ToString();
        var created = await Request<GroupSummary>("/groups", HttpMethod.Post, new { id = group, name, description, category, avatarIcon, avatarColorHex, isPublic = true, packages = Array.Empty<object>() }).ConfigureAwait(false);
        state = new([.. state.Groups, created], state.Invitations, state.Revision);
        await Info(group).ConfigureAwait(false); Changed?.Invoke(null, null, null); return Prefix + group;
    });
    internal Task<GroupMember[]> BansAsync(string token, string conversation) => Use(token, async () =>
        (await Request<GroupBans>($"/groups/{Raw(conversation)}/bans", HttpMethod.Get).ConfigureAwait(false)).Members);
    internal Task<string?> CreateAsync(string token, string[] friends, string name, string avatarIcon = "Users", string avatarColorHex = "#4D96FF") => Use<string?>(token, async () => {
        LastOperationWarning = null;
        var group = Guid.NewGuid().ToString(); var key = GroupChatCrypto.RandomKey();
        try {
            vault!.StoreGroupKey(group, key);
            var devices = (await Request<GroupDevices>($"/identities/{session!.AccountId}", HttpMethod.Get).ConfigureAwait(false)).Devices;
            var created = await Request<GroupSummary>("/groups", HttpMethod.Post, new { id = group, name, avatarIcon, avatarColorHex,
                packages = devices.Select(d => GroupChatCrypto.Wrap(group, vault.Identity, d, key)).ToArray() }).ConfigureAwait(false);
            foreach (var friend in friends) {
                try { await Invite(group, friend, token, key).ConfigureAwait(false); }
                catch { LastOperationWarning = "Group created, but some invitations could not be sent. Use Invite people to retry them."; }
            }
            state = new([.. state.Groups.Where(g => g.Id != group), created], state.Invitations, state.Revision);
            Changed?.Invoke(null, null, null);
            return Prefix + group;
        } finally { CryptographicOperations.ZeroMemory(key); }
    });
    private async Task Invite(string group, string account, string token, byte[] key)
    {
        var devices = (await Request<GroupDevices>($"/identities/{account}", HttpMethod.Get).ConfigureAwait(false)).Devices;
        await Request<JsonElement>($"/groups/{group}/invite", HttpMethod.Post, new { accountId = account,
            packages = devices.Select(d => GroupChatCrypto.Wrap(group, vault!.Identity, d, key)).ToArray() }, sourceToken: token).ConfigureAwait(false);
    }
    internal Task<bool> InviteAsync(string token, string conversation, string account) => Use(token, async () => {
        var group = Raw(conversation); var info = await Info(group).ConfigureAwait(false);
        if (info.IsPublic) {
            await Request<JsonElement>($"/groups/{group}/invite", HttpMethod.Post,
                new { accountId = account, packages = Array.Empty<object>() }, sourceToken: token).ConfigureAwait(false);
            return true;
        }
        var key = vault!.GroupKey(group)!;
        try { await Invite(group, account, token, key).ConfigureAwait(false); return true; } finally { CryptographicOperations.ZeroMemory(key); }
    });
    internal Task<bool> MembershipAsync(string token, string conversation, string operation, string? target = null) => Use(token, async () => {
        await Request<JsonElement>($"/groups/{Raw(conversation)}/{operation}", HttpMethod.Post, new { accountId = target }).ConfigureAwait(false);
        state = await Request<GroupState>("/state", HttpMethod.Get).ConfigureAwait(false); Changed?.Invoke(null, null, null); return true;
    });
    internal Task<GroupInfo> InfoAsync(string token, string conversation) => Use(token, () => Info(Raw(conversation)));
    internal Task<bool> UpdateAppearanceAsync(string token, string conversation, string avatarIcon, string avatarColorHex) => Use(token, async () => {
        var group = Raw(conversation);
        var updated = await Request<GroupSummary>($"/groups/{group}/appearance", HttpMethod.Post, new { avatarIcon, avatarColorHex }).ConfigureAwait(false);
        state = new([.. state.Groups.Where(g => g.Id != group), updated], state.Invitations, state.Revision);
        Changed?.Invoke(group, null, null); return true;
    });
    internal Task<bool> ProvisionAsync(string token, string conversation, string deviceId) => Use(token, async () => {
        var group = Raw(conversation); var info = await Info(group).ConfigureAwait(false);
        var device = info.Missing.SingleOrDefault(d => d.DeviceId == deviceId && info.Members.Any(m => m.AccountId == d.AccountId))
            ?? throw new InvalidOperationException("This installation no longer needs a key.");
        var key = vault!.GroupKey(group)!;
        try {
            await Request<JsonElement>($"/groups/{group}/package", HttpMethod.Post,
                new { packages = new[] { GroupChatCrypto.Wrap(group, vault.Identity, device, key) } }).ConfigureAwait(false);
            return true;
        } finally { CryptographicOperations.ZeroMemory(key); }
    });
    internal Task<MessagePage> MessagesAsync(string token, string conversation, long? before) => Use(token, async () => {
        var group = Raw(conversation);
        var existing = vault!.GroupKey(group);
        if (existing is null) await Info(group).ConfigureAwait(false); else CryptographicOperations.ZeroMemory(existing);
        var page = await Request<GroupMessagePage>($"/groups/{group}/messages" + (before is null ? "" : $"?before={before}"), HttpMethod.Get).ConfigureAwait(false);
        foreach (var deleted in page.Items.Where(m => m.Deleted && m.MediaId is not null)) { revokedMedia.Add(deleted.MediaId!); vault!.DeleteMedia(deleted.MediaId!); }
        try { vault!.StoreHistory(group, page.Items); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* An optional cache write must not hide valid server history. */ }
        return new MessagePage(page.Items.Where(m => !m.Deleted).Select(m => ToDto(group, m)).ToArray(), page.NextCursor);
    });
    private MessageDto ToDto(string group, GroupMessage m) => WithReadReceipt(new($"{Prefix}{group}:{m.Id}", m.Id, m.SenderAccountId,
        session!.AccountId, JsonSerializer.Serialize(m, Json), "group-v1", "", m.SentAtUnix, null));
    internal MessageDto WithReadReceipt(MessageDto message)
    {
        if (!IsMessage(message) || message.ReadAtUnix.HasValue || message.SenderAccountId != stateAccountId) return message;
        var parts = message.Id.Split(':');
        if (parts.Length != 3) return message;
        var group = Volatile.Read(ref state).Groups.FirstOrDefault(g => g.Id == parts[1]);
        if (group is not { SeenRanges.Length: > 0, SeenAtUnix: > 0 }) return message;
        var packet = JsonSerializer.Deserialize<GroupMessage>(message.Ciphertext, Json);
        return packet is { Sequence: > 0 } && group.SeenRanges.Any(range => packet.Sequence >= range.StartSequence && packet.Sequence <= range.EndSequence)
            ? message with { ReadAtUnix = group.SeenAtUnix } : message;
    }
    private static (string Group, GroupMessage Message) Packet(MessageDto m) => (m.Id.Split(':')[1], JsonSerializer.Deserialize<GroupMessage>(m.Ciphertext, Json)!);
    private GroupChatCrypto.Payload Decrypt(MessageDto dto)
    {
        var (group, m) = Packet(dto);
        vault!.Pin($"sign:{m.SenderAccountId}:{m.DeviceId}", m.SigningKey);
        if (!GroupChatCrypto.Verify(m.SigningKey, GroupChatCrypto.SignedText(group, m), m.Signature)) throw new CryptographicException("Invalid group sender signature");
        var key = vault.GroupKey(group) ?? throw new CryptographicException("Group key is unavailable");
        try {
            var plain = GroupChatCrypto.Open(key, new(m.Ciphertext, m.Nonce, m.Tag), $"AC-GROUP-MESSAGE-1\n{group}\n{m.Id}\n{m.SenderAccountId}\n{m.DeviceId}");
            try { return JsonSerializer.Deserialize<GroupChatCrypto.Payload>(plain)!; } finally { CryptographicOperations.ZeroMemory(plain); }
        } finally { CryptographicOperations.ZeroMemory(key); }
    }
    internal Task<string> DecryptAsync(string token, MessageDto message) => Use(token, () => {
        var (group, _) = Packet(message);
        if (!state.Groups.Any(g => g.Id == group)) throw new InvalidOperationException("You no longer have access to this group.");
        return Task.FromResult(DisplayPayload(Decrypt(message)));
    });
    internal Task<MessageDto> SendAsync(string token, string conversation, string text) => Use(token, () => SendCore(conversation, text));
    private async Task<MessageDto> SendCore(string conversation, string text, string? fixedMessageId = null, string? mediaId = null, string? mediaDigest = null) {
        var group = Raw(conversation);
        var current = State.Groups.FirstOrDefault(g => g.Id == group) ?? throw new InvalidOperationException("This group is no longer available.");
        var cached = vault!.GroupKey(group);
        if (cached is null) await Info(group).ConfigureAwait(false); else CryptographicOperations.ZeroMemory(cached);
        var key = vault!.GroupKey(group)!; var franking = GroupChatCrypto.RandomKey();
        try {
            var messageId = fixedMessageId ?? Guid.NewGuid().ToString();
            var attachment = mediaId is null ? null : ImageContent(text);
            // Older plugin builds see a caption, never attachment keys in the text field.
            var payload = JsonSerializer.SerializeToUtf8Bytes(new GroupChatCrypto.Payload(attachment is null ? text : string.IsNullOrEmpty(attachment.Text) ? "Image" : attachment.Text, Convert.ToBase64String(franking), attachment));
            GroupChatCrypto.Sealed encrypted;
            try { encrypted = GroupChatCrypto.Seal(key, payload, $"AC-GROUP-MESSAGE-1\n{group}\n{messageId}\n{session!.AccountId}\n{deviceId}"); }
            finally { CryptographicOperations.ZeroMemory(payload); }
            var m = new GroupMessage(messageId, session!.AccountId, deviceId!, encrypted.Ciphertext, encrypted.Nonce, encrypted.Tag,
                Convert.ToBase64String(HMACSHA256.HashData(franking, Encoding.UTF8.GetBytes(text))), "", GroupChatCrypto.SigningPublicKey(vault.SigningKey), MediaId: mediaId, MediaDigest: mediaDigest);
            m = m with { Signature = GroupChatCrypto.Sign(vault.SigningKey, GroupChatCrypto.SignedText(group, m)) };
            GroupMessage sent;
            try { sent = await Request<GroupMessage>($"/groups/{group}/send", HttpMethod.Post, new { version = current.Version, message = m }).ConfigureAwait(false); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) {
                // The relay deduplicates the signed ID. Reuse the exact same packet after
                // an uncertain transport result; generating another ID could double-post.
                await Task.Delay(150).ConfigureAwait(false);
                sent = await Request<GroupMessage>($"/groups/{group}/send", HttpMethod.Post, new { version = current.Version, message = m }).ConfigureAwait(false);
            }
            return ToDto(group, sent);
        } finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(franking); }
    }
    internal Task<bool> MarkReadAsync(string token, string conversation) => Use(token, async () => {
        var group = State.Groups.FirstOrDefault(x => x.Id == Raw(conversation)); if (group is null) return false;
        await Request<JsonElement>($"/groups/{group.Id}/read", HttpMethod.Post, new { sequence = group.LastSequence }).ConfigureAwait(false); return true;
    });
    internal Task<bool> DeleteAsync(string token, string conversation, string message) => Use(token, async () => {
        await Request<JsonElement>($"/groups/{Raw(conversation)}/delete", HttpMethod.Post, new { messageId = message }).ConfigureAwait(false); return true;
    });
    internal Task<bool> ReportAsync(string token, MessageDto message) => Use(token, async () => {
        var (group, m) = Packet(message); var payload = Decrypt(message);
        if (m.MediaId is not null) return await ReportMediaCore(group, m, payload).ConfigureAwait(false);
        await Request<JsonElement>($"/groups/{group}/report", HttpMethod.Post, new { messageId = m.Id, plaintext = payload.Text, frankingKey = payload.FrankingKey }).ConfigureAwait(false); return true;
    });
    internal Task<GroupReport[]> ReportsAsync(string token) => Use(token, async () => {
        var reports = new Dictionary<string,GroupReport>(); string? after = null;
        do {
            var page = await Request<GroupReports>("/moderation" + (after is null ? "" : "?after=" + Uri.EscapeDataString(after)), HttpMethod.Get).ConfigureAwait(false);
            foreach (var report in page.Reports) reports[report.Id] = report;
            if (page.NextCursor == after && after is not null) throw new InvalidOperationException("Invalid report cursor.");
            after = page.NextCursor;
        } while (after is not null);
        return reports.Values.ToArray();
    });
    internal Task<PublicGroupListing[]> AdminPublicGroupsAsync(string token) => Use(token, async () => {
        var groups = new List<PublicGroupListing>(); string? after = null;
        do {
            var page = await Request<PublicGroupDirectory>("/admin/public-groups" + (after is null ? "" : "?after=" + Uri.EscapeDataString(after)), HttpMethod.Get).ConfigureAwait(false);
            groups.AddRange(page.Groups);
            if (page.NextCursor == after && after is not null) throw new InvalidOperationException("Invalid group directory cursor.");
            after = page.NextCursor;
        } while (after is not null);
        return groups.ToArray();
    });
    internal Task<bool> AdminPublicGroupActionAsync(string token, string group, object action) => Use(token, async () => {
        await Request<JsonElement>($"/admin/public-groups/{Raw(group)}", HttpMethod.Post, action).ConfigureAwait(false); return true;
    });
    internal Task<bool> ResolveReportAsync(string token, string id) => Use(token, async () => { await Request<JsonElement>("/moderation/resolve", HttpMethod.Post, new { id }).ConfigureAwait(false); return true; });
    internal Task<GroupReport[]> ModeratorReportsAsync(string token) => Use(token, async () => {
        var reports = new List<GroupReport>(); string? after = null;
        do {
            var page=await Request<GroupReports>("/moderation/cases"+(after is null?"":"?after="+Uri.EscapeDataString(after)),HttpMethod.Get).ConfigureAwait(false);
            reports.AddRange(page.Reports);
            if(page.NextCursor==after&&after is not null)throw new InvalidOperationException("Invalid report cursor.");
            after=page.NextCursor;
        }while(after is not null);
        return reports.ToArray();
    });
    internal Task<bool> ModeratorReviewAsync(string token,string id,string status,string note) => Use(token,async()=>{
        await Request<JsonElement>("/moderation/review",HttpMethod.Post,new{id,status,note}).ConfigureAwait(false);return true;
    });
    internal Task<PublicGroupListing[]> ModeratorPublicGroupsAsync(string token) => Use(token, async () => {
        var groups = new List<PublicGroupListing>(); string? after = null;
        do {
            var page = await Request<PublicGroupDirectory>("/moderator/public-groups" + (after is null ? "" : "?after=" + Uri.EscapeDataString(after)), HttpMethod.Get).ConfigureAwait(false);
            groups.AddRange(page.Groups);
            if(page.NextCursor == after && after is not null) throw new InvalidOperationException("Invalid directory cursor.");
            after = page.NextCursor;
        } while(after is not null);
        return groups.ToArray();
    });
    internal Task<bool> ModeratorDeleteMessageAsync(string token,string group,string messageId,string reason) => Use(token,async()=>{
        await Request<JsonElement>($"/moderator/public-groups/{Raw(group)}/message",HttpMethod.Post,new{messageId,reason}).ConfigureAwait(false);return true;
    });
    internal Task<bool> ModeratorPublicGroupActionAsync(string token, string group, object action) => Use(token, async () => {
        await Request<JsonElement>($"/moderator/public-groups/{Raw(group)}", HttpMethod.Post, action).ConfigureAwait(false); return true;
    });
    public void Dispose() {
        lock (lifecycle) {
            disposed = true; Stop();
            http.Dispose();
        }
    }
}

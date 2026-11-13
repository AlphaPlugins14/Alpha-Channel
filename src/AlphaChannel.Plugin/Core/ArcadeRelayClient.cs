using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AlphaChannel.Plugin;

internal sealed record ArcadeRelayRoom(string RoomId, string Game, string HostKey, string ViewerKey,
    string SessionId, string CanvasId, long ExpiresUnixMs);

// Bounded public snapshots; Bingo also supports account-bound delivery and
// an independent acknowledged card-action path. Only snapshots coalesce.
internal sealed class ArcadeRelayClient : IDisposable
{
    internal const string DefaultUrl = "https://arcade-games.example.invalid";
    private const int MaxFrameBytes = 50_000;
    private int FrameLimit => RemainingArcadeRelayRules.Game(game) ? 300000 : MaxFrameBytes;
    private int CommandLimit => RemainingArcadeRelayRules.Game(game) ? 280000 : 48000;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim changed = new(0, 1);
    private readonly object gate = new();
    private readonly HttpClient http;
    private readonly Func<IArcadeRelaySocket> socketFactory;
    private readonly Func<string, int?> validateSnapshot;
    private readonly Action<Exception>? failureObserver;
    private readonly string baseUrl, game, session, canvas;
    private readonly string? viewerAccount;
    private readonly Func<string, bool>? validateAction;
    private string[] members = [];
    private (string Id, string Command)? pendingAction;
    private readonly Queue<string> bingoActionQueue = new();
    private sealed class PendingAction(string command) { internal readonly string Id = Guid.NewGuid().ToString("N"), Command = command; internal int Sequence; internal bool? Result; }
    private readonly Queue<PendingAction> pendingActions = new();
    private int actionSequence;
    private bool SequencedActions => game == "lucky-wheel" && validateAction is not null || game is "quiz" or "slot-machine" or "roulette" or "choco-race" or "poker" || RemainingArcadeRelayRules.Game(game);
    private bool DrawingActions => game is "canvas" or "pictionary" or "pictionary-teams";
    private bool HasActions => game == "bingo" || SequencedActions;
    private readonly Dictionary<string, bool?> hostActions = new(StringComparer.Ordinal);
    private string? actionError;
    private ArcadeRelayRoom? room;
    private (string Command, int Revision)? latest;
    private bool disposed;
    private volatile bool connected;
    private volatile bool snapshotRejected;
    private string status = "Connecting to Cloudflare…";
    internal bool IsHost { get; }
    internal bool Connected => connected;
    internal Task Completion { get; }
    internal string Status => snapshotRejected ? $"{GameName} state could not be sent: it is invalid or exceeds the relay size limit." : Volatile.Read(ref status);
    internal bool SnapshotRejected => snapshotRejected;
    internal ArcadeRelayRoom? Room => Volatile.Read(ref room);
    internal ConcurrentQueue<string> Incoming { get; } = new();
    internal ConcurrentQueue<(string UserId, string Command, string ActionId)> IncomingActions { get; } = new();
    internal ConcurrentQueue<(string Command, bool Accepted)> ActionResults { get; } = new();
    internal string? ActionError => Volatile.Read(ref actionError);
    private string GameName => game switch { "lucky-wheel" => "Lucky Wheel", "quiz" => "Quiz", "bingo" => "Bingo", "slot-machine" => "Slot Machine", "roulette" => "Roulette", "choco-race" => "Choco Race", "poker" => "Poker",
        "pictionary" => "Pictionary", "pictionary-teams" => "Pictionary Teams", "canvas" => "Canvas", "sky-rail" => "Air Force EX", _ => "Arcade" };

    internal ArcadeRelayClient(string url, string game, string session, string canvas,
        Func<string, int?> validateSnapshot, ArcadeRelayRoom? viewerRoom = null,
        HttpClient? client = null, Func<IArcadeRelaySocket>? socketFactory = null,
        Action<Exception>? failureObserver = null, string? accountToken = null, string? accountId = null,
        Func<string, bool>? validateAction = null)
    {
        if (!ValidUrl(url, out baseUrl) || !Hex(session, 32) || !Hex(canvas, 32))
            throw new ArgumentException("Invalid arcade relay address or activity identity.");
        this.game = game; this.session = session; this.canvas = canvas;
        this.validateSnapshot = validateSnapshot;
        this.validateAction = validateAction;
        viewerAccount = accountId;
        this.socketFactory = socketFactory ?? (() => new ArcadeRelaySocket(accountToken));
        this.failureObserver = failureObserver;
        IsHost = viewerRoom is null;
        if (viewerRoom is not null && (!ValidRoom(viewerRoom) || viewerRoom.HostKey.Length != 0))
            throw new ArgumentException("Invalid viewer invitation.");
        http = client ?? new(new HttpClientHandler { AllowAutoRedirect = false });
        http.Timeout = TimeSpan.FromSeconds(8);
        room = viewerRoom;
        Completion = Task.Run(RunAsync);
    }

    internal static bool ValidUrl(string? url, out string normalized)
    {
        normalized = "";
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/" || !uri.Host.EndsWith(".workers.dev", StringComparison.OrdinalIgnoreCase)) return false;
        normalized = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        return true;
    }
    internal static bool Hex(string? value, int length) => value is not null && value.Length == length &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private bool ValidRoom(ArcadeRelayRoom value) => Hex(value.RoomId, 32) && Hex(value.ViewerKey, 64) &&
        value.Game == game && value.SessionId == session && value.CanvasId == canvas &&
        value.ExpiresUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() &&
        value.ExpiresUnixMs <= DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeMilliseconds();

    internal void Publish(string command)
    {
        if (!IsHost) return;
        var revision = command.Length <= CommandLimit ? validateSnapshot(command) : null;
        snapshotRejected = revision is null;
        if (revision is null) return;
        lock (gate)
        {
            if (disposed || latest is { } previous && revision <= previous.Revision) return;
            latest = (command, revision.Value);
            if (changed.CurrentCount == 0) changed.Release();
        }
    }
    internal void SetMembers(IEnumerable<string> ids)
    {
        if (!IsHost || !HasActions) return;
        var next = ids.Select(id => id.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (next.Any(id => !Guid.TryParseExact(id, "D", out _))) return;
        // The room supports at most 64 viewers. Always refresh membership even
        // when a larger Watch Party roster exceeds this activity's capacity.
        Volatile.Write(ref members, next.Take(64).ToArray());
    }
    internal bool SubmitAction(string command)
    {
        if (IsHost || !HasActions || validateAction?.Invoke(command) != true) return false;
        lock (gate)
        {
            if (SequencedActions)
            {
                if (disposed || pendingActions.Count >= 16) { actionError = "Waiting for previous actions to finish. Please try again."; return false; }
                pendingActions.Enqueue(new(command)); actionError = null; return true;
            }
            if (disposed) return false;
            if (pendingAction is not null)
            {
                if (game != "bingo" || command.StartsWith("L|", StringComparison.Ordinal) || bingoActionQueue.Count >= 32) return false;
                bingoActionQueue.Enqueue(command);
                return true;
            }
            pendingAction = (Guid.NewGuid().ToString("N"), command);
            actionError = null;
            return true;
        }
    }
    internal void AcknowledgeAction(string id, bool accepted)
    {
        lock (gate) if (hostActions.ContainsKey(id)) hostActions[id] = accepted;
    }

    private async Task RunAsync()
    {
        var token = lifetime.Token;
        var attempt = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (room is null)
                    {
                        var body = await ExchangeAsync(HttpMethod.Post, "/rooms", null,
                            JsonSerializer.Serialize(new { Game = game, SessionId = session, CanvasId = canvas }), token).ConfigureAwait(false);
                        var created = JsonSerializer.Deserialize<ArcadeRelayRoom>(body);
                        if (created is null || !ValidRoom(created) || !Hex(created.HostKey, 64) || created.HostKey == created.ViewerKey)
                            throw new IOException("Invalid arcade room response.");
                        Volatile.Write(ref room, created);
                    }
                    if (room.ExpiresUnixMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) throw new ArcadeSessionEndedException();
                    await RunConnectionAsync(room, token).ConfigureAwait(false);
                }
                catch (ArcadeSessionEndedException) { SessionEnded(); break; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    connected = false;
                    failureObserver?.Invoke(exception);
                    Volatile.Write(ref status, $"Cloudflare unavailable. {GameName} is reconnecting…");
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(15_000, ++attempt * 2_000) +
                        Random.Shared.Next(0, 500)), token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            lock (gate) { disposed = true; connected = false; }
            if (IsHost && room is { } owned)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ExchangeAsync(HttpMethod.Delete, "/rooms/" + owned.RoomId, owned.HostKey, null, timeout.Token).ConfigureAwait(false);
                }
                catch { /* The Worker alarm also expires abandoned sessions. */ }
            }
            http.Dispose(); changed.Dispose(); lifetime.Dispose();
        }
    }

    private async Task RunConnectionAsync(ArcadeRelayRoom current, CancellationToken token)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var socket = socketFactory();
        using var sendGate = new SemaphoreSlim(1, 1);
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var uri = new Uri("wss://" + new Uri(baseUrl).Authority + "/rooms/" + current.RoomId + "/socket");
            await socket.ConnectAsync(uri, IsHost ? current.HostKey : current.ViewerKey, timeout.Token).ConfigureAwait(false);
            var raw = await socket.ReceiveAsync(MaxFrameBytes, timeout.Token).ConfigureAwait(false);
            var ready = JsonSerializer.Deserialize<RelayFrame>(raw);
            if (ready is not { Type: "Ready", Version: 1 } || ready.Game != game || ready.SessionId != session ||
                ready.CanvasId != canvas || ready.ExpiresUnixMs != current.ExpiresUnixMs || ready.Revision < 0 ||
                ready.ActionSequence < 0 || (!IsHost && HasActions && ready.PlayerId != viewerAccount))
                throw new IOException("Invalid arcade handshake.");
            lock (gate) actionSequence = Math.Max(actionSequence, ready.ActionSequence);
        }
        connected = true;
        Volatile.Write(ref status, $"{GameName} connected through Cloudflare WebSockets");
        var connectionToken = connection.Token;
        TaskCompletionSource<int>? acknowledgement = null;
        var receivedRevision = 0;
        string? mergedState = null;

        async Task SendAsync(string frame)
        {
            await sendGate.WaitAsync(connectionToken).ConfigureAwait(false);
            try { await socket.SendAsync(frame, connectionToken).ConfigureAwait(false); }
            finally { sendGate.Release(); }
        }
        async Task ReceiveAsync()
        {
            while (!connectionToken.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                var raw = await socket.ReceiveAsync(FrameLimit, timeout.Token).ConfigureAwait(false);
                if (raw == "pong") continue;
                var frame = JsonSerializer.Deserialize<RelayFrame>(raw) ?? throw new IOException("Invalid arcade frame.");
                if (frame.Type == "Ended")
                {
                    if (frame.SessionId != session) throw new IOException("Wrong arcade session.");
                    throw new ArcadeSessionEndedException();
                }
                if (IsHost)
                {
                    if (HasActions && frame.Type == "ActionAcknowledged" && Hex(frame.ActionId, 32))
                    { lock (gate) hostActions.Remove(frame.ActionId!); continue; }
                    if (HasActions && frame.Type == "Action" && Hex(frame.ActionId, 32) &&
                        Guid.TryParseExact(frame.UserId, "D", out _) && frame.Command is not null && validateAction?.Invoke(frame.Command) == true)
                    {
                        lock (gate)
                        {
                            if (disposed) return;
                            if (!hostActions.ContainsKey(frame.ActionId!))
                            {
                                if (hostActions.Count >= 64) throw new IOException("Too many pending player actions.");
                                hostActions.Add(frame.ActionId!, null);
                                IncomingActions.Enqueue((frame.UserId!, frame.Command, frame.ActionId!));
                            }
                        }
                        continue;
                    }
                    if (frame.Type != "Published" || frame.Revision < 1) throw new IOException("Invalid publish acknowledgement.");
                    Volatile.Read(ref acknowledgement)?.TrySetResult(frame.Revision);
                    continue;
                }
                if (HasActions && frame.Type == "ActionResult" && Hex(frame.ActionId, 32) && frame.Accepted is not null)
                {
                    if (SequencedActions)
                    {
                        lock (gate)
                        {
                            var received = pendingActions.FirstOrDefault(a => a.Id == frame.ActionId);
                            if (received is not null) received.Result = frame.Accepted.Value;
                            // Receipts may arrive out of order; retire only the completed prefix.
                            while (pendingActions.TryPeek(out var pending) && pending.Result is { } accepted)
                            {
                                pendingActions.Dequeue();
                                if (ActionResults.Count >= 16) ActionResults.TryDequeue(out _);
                                ActionResults.Enqueue((pending.Command, accepted));
                                actionError = accepted ? null : "The action could not be applied. Check the current game state and try again.";
                            }
                        }
                        continue;
                    }
                    lock (gate) if (pendingAction?.Id == frame.ActionId)
                    {
                        pendingAction = bingoActionQueue.TryDequeue(out var nextBingo) ? (Guid.NewGuid().ToString("N"), nextBingo) : null;
                        actionError = frame.Accepted.Value ? null : "Bingo action could not be applied. Check the current round and try again.";
                    }
                    continue;
                }
                if (RemainingArcadeRelayRules.Game(game) && frame.Command is not null && frame.Type == "Snapshot")
                    frame = frame with { Command = RemainingArcadeRelayRules.Merge(frame.Command, mergedState) };
                if (frame.Type != "Snapshot" || frame.Command is null || frame.Command.Length > CommandLimit ||
                    validateSnapshot(frame.Command) != frame.Revision)
                    throw new IOException("Invalid arcade snapshot.");
                if (frame.Revision > receivedRevision)
                {
                    receivedRevision = frame.Revision;
                    mergedState = frame.Command;
                    // Only snapshots may replace pending state. Future actions
                    // and events must have their own reliable queue.
                    lock (gate)
                    {
                        if (disposed) return;
                        Incoming.Clear(); Incoming.Enqueue(frame.Command);
                    }
                }
                await SendAsync(JsonSerializer.Serialize(new { Type = "Ack", Revision = frame.Revision })).ConfigureAwait(false);
            }
        }
        async Task PublishAsync()
        {
            string? sent = null;
            string? sentMembers = null;
            while (!connectionToken.IsCancellationRequested)
            {
                if (HasActions)
                {
                    var body = JsonSerializer.Serialize(Volatile.Read(ref members));
                    if (body != sentMembers)
                    {
                        await ExchangeAsync(HttpMethod.Put, "/rooms/" + current.RoomId + "/members", current.HostKey, body, connectionToken).ConfigureAwait(false);
                        sentMembers = body;
                    }
                }
                (string Command, int Revision)? value;
                lock (gate) value = latest;
                if (value is null || value.Value.Command == sent)
                {
                    await changed.WaitAsync(TimeSpan.FromSeconds(1), connectionToken).ConfigureAwait(false);
                    continue;
                }
                var started = Stopwatch.GetTimestamp();
                var ack = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref acknowledgement, ack);
                await SendAsync(JsonSerializer.Serialize(new { Type = "Snapshot", value.Value.Command })).ConfigureAwait(false);
                var revision = await ack.Task.WaitAsync(TimeSpan.FromSeconds(8), connectionToken).ConfigureAwait(false);
                if (revision != value.Value.Revision) throw new IOException("Host state is out of sync with the relay.");
                sent = value.Value.Command;
                Volatile.Write(ref acknowledgement, null);
                // Drawing publishes small pictures promptly; dense pictures adapt to
                // the existing byte budget. Other games keep their usual cadence.
                var interval = RemainingArcadeRelayRules.Game(game) && game != "sky-rail"
                    ? Math.Max(100, Encoding.UTF8.GetByteCount(value.Value.Command) / 700.0) : 400;
                var delay = TimeSpan.FromMilliseconds(interval) - Stopwatch.GetElapsedTime(started);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, connectionToken).ConfigureAwait(false);
            }
        }
        async Task ActionsAsync()
        {
            var sentAt = new Dictionary<string, long>(StringComparer.Ordinal);
            while (!connectionToken.IsCancellationRequested)
            {
                string? packet = null;
                lock (gate)
                {
                    var now = Stopwatch.GetTimestamp();
                    bool Due(string id) => !sentAt.TryGetValue(id, out var at) || Stopwatch.GetElapsedTime(at, now).TotalSeconds >= 2;
                    if (IsHost)
                    {
                        foreach (var id in sentAt.Keys.Where(id => !hostActions.ContainsKey(id)).ToArray()) sentAt.Remove(id);
                        var batch = hostActions.Where(a => a.Value is not null && Due(a.Key)).Take(SequencedActions ? 8 : 1).ToArray();
                        if (SequencedActions && batch.Length > 0)
                        {
                            packet = JsonSerializer.Serialize(new { Type = "ActionAcks", Actions = batch.Select(a => new { ActionId = a.Key, Accepted = a.Value!.Value }).ToArray() });
                            foreach (var entry in batch) sentAt[entry.Key] = now;
                        }
                        else if (batch.Length > 0)
                        { var next = batch[0]; packet = JsonSerializer.Serialize(new { Type = "ActionAck", ActionId = next.Key, Accepted = next.Value!.Value }); sentAt[next.Key] = now; }
                    }
                    else if (SequencedActions)
                    {
                        foreach (var id in sentAt.Keys.Where(id => !pendingActions.Any(a => a.Id == id)).ToArray()) sentAt.Remove(id);
                        // Keep a bounded drawing window moving during a slow host round trip.
                        var next = pendingActions.Take(DrawingActions ? 8 : 1).FirstOrDefault(a => a.Result is null && Due(a.Id));
                        if (next is not null)
                        {
                            if (next.Sequence == 0) next.Sequence = checked(++actionSequence);
                            sentAt[next.Id] = now;
                            packet = JsonSerializer.Serialize(new { Type = "Action", ActionId = next.Id, next.Command, next.Sequence });
                        }
                    }
                    else if (pendingAction is { } action && Due(action.Id))
                    {
                        sentAt.Clear(); sentAt[action.Id] = now;
                        packet = JsonSerializer.Serialize(new { Type = "Action", ActionId = action.Id, action.Command });
                    }
                }
                if (packet is not null) await SendAsync(packet).ConfigureAwait(false);
                // Independent reliable path: 20 drawing, 40 other-game or 10 general iterations/s. Together with
                // snapshots and pings this stays below Bingo's host budget.
                await Task.Delay(DrawingActions ? 50 : RemainingArcadeRelayRules.Game(game) ? 25 : 100, connectionToken).ConfigureAwait(false);
            }
        }
        async Task HeartbeatAsync()
        {
            while (!connectionToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), connectionToken).ConfigureAwait(false);
                await SendAsync("ping").ConfigureAwait(false);
            }
        }
        var tasks = new List<Task> { ReceiveAsync(), HeartbeatAsync() };
        if (IsHost) tasks.Add(PublishAsync());
        if (HasActions) tasks.Add(ActionsAsync());
        try { await await Task.WhenAny(tasks).ConfigureAwait(false); }
        finally
        {
            connected = false;
            connection.Cancel(); socket.Abort();
            // Drain all tasks before disposing the socket and send semaphore.
            foreach (var task in tasks) try { await task.ConfigureAwait(false); } catch { }
        }
    }

    private void SessionEnded()
    {
        connected = false;
        Volatile.Write(ref status, "Cloudflare session ended. End the activity and start again.");
        if (!IsHost) lock (gate) { if (!disposed) { Incoming.Clear(); Incoming.Enqueue("X|" + session); } }
    }
    private async Task<byte[]> ExchangeAsync(HttpMethod method, string path, string? key, string? body, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(method, baseUrl + path);
        if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Gone) throw new ArcadeSessionEndedException();
        response.EnsureSuccessStatusCode();
        using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var data = new MemoryStream();
        var buffer = new byte[1024];
        int count;
        while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (data.Length + count > 4096) throw new IOException("Arcade response too large.");
            data.Write(buffer, 0, count);
        }
        return data.ToArray();
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; lifetime.Cancel(); Incoming.Clear(); IncomingActions.Clear(); ActionResults.Clear(); hostActions.Clear(); pendingAction = null; bingoActionQueue.Clear(); pendingActions.Clear();
        }
    }
    private sealed record RelayFrame(string Type, int Version, string? Game, string? SessionId,
        string? CanvasId, int Revision, long ExpiresUnixMs, string? Command, string? PlayerId, string? UserId, string? ActionId, bool? Accepted, int ActionSequence);
}

internal sealed class ArcadeSessionEndedException : IOException;

internal interface IArcadeRelaySocket : IDisposable
{
    Task ConnectAsync(Uri uri, string key, CancellationToken token);
    Task SendAsync(string text, CancellationToken token);
    Task<string> ReceiveAsync(int limit, CancellationToken token);
    void Abort();
}

internal sealed class ArcadeRelaySocket(string? accountToken = null) : IArcadeRelaySocket
{
    private readonly ClientWebSocket socket = new();
    private readonly HttpMessageInvoker invoker = new(new SocketsHttpHandler { AllowAutoRedirect = false });
    public async Task ConnectAsync(Uri uri, string key, CancellationToken token)
    {
        socket.Options.SetRequestHeader("Authorization", "Bearer " + key);
        if (!string.IsNullOrEmpty(accountToken)) socket.Options.SetRequestHeader("X-Arcade-Account", "Bearer " + accountToken);
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        try { await socket.ConnectAsync(uri, invoker, token).ConfigureAwait(false); }
        catch when (socket.HttpStatusCode == HttpStatusCode.Gone) { throw new ArcadeSessionEndedException(); }
    }
    public Task SendAsync(string text, CancellationToken token) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, token);
    public async Task<string> ReceiveAsync(int limit, CancellationToken token)
    {
        using var data = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
            if (result.MessageType != WebSocketMessageType.Text) throw new IOException("Arcade connection closed or sent binary data.");
            if (data.Length + result.Count > limit) throw new IOException("Arcade frame too large.");
            data.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return new UTF8Encoding(false, true).GetString(data.GetBuffer(), 0, (int)data.Length);
    }
    public void Abort() => socket.Abort();
    public void Dispose() { socket.Dispose(); invoker.Dispose(); }
}

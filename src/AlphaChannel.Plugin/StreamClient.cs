using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Core;
using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

// Connects to the Alpha Channel server's /rt WebSocket endpoint using AlphaChannel.Contracts.
// Authenticates with the bearer token from the current character's CharacterSession.
internal enum StreamMode
{
    None,
    Hosting,
    Viewing,
}

internal enum RealtimeConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    AuthenticationRequired,
    ServerUnavailable,
    AccountInUse,
}

internal sealed record PartyNextQueueSnapshot(
    int Count,
    string? Title,
    string? Source,
    double? DurationSeconds,
    string? ThumbnailUrl);

internal sealed partial class StreamClient : IDisposable
{
    internal HalloweenSession? HostHalloweenSession { get; set; }
    internal Guid? HostHalloweenCompletedSession { get; set; }
    internal string? HostPartyCountdownUrl { get; set; }
    private TvFrameSelection hostFrameSelection = new();
    private bool hostHalloweenFrameEnabled;
    internal bool HostHalloweenFrameEnabled
    {
        get=>Volatile.Read(ref hostHalloweenFrameEnabled);
        set=>Volatile.Write(ref hostHalloweenFrameEnabled,value);
    }
    internal TvFrameSelection HostFrameSelection
    {
        get => Volatile.Read(ref hostFrameSelection);
        set => Volatile.Write(ref hostFrameSelection, value.Normalized());
    }
    private const string RichChatPrefix = "[[AC_MSG_1]]";
    private const string ChatReactionPrefix = "[[AC_MSG_REACT_1]]";
    private const string ChatDeletePrefix = "[[AC_MSG_DELETE_1]]";
    internal const string WhiteboardCommandPrefix = "[[AC_WHITEBOARD_1]]";
    internal const string BroadcastAllowanceRefreshCommand = "[[AC_ALLOWANCE_REFRESH_1]]";
    private int incomingBroadcastAllowanceRefresh;
    internal bool TakeBroadcastAllowanceRefresh() =>
        Interlocked.Exchange(ref incomingBroadcastAllowanceRefresh, 0) != 0;
    internal const string PictionaryCommandPrefix = "[[AC_PICTIONARY_1]]";
    internal const string PictionaryTeamsCommandPrefix = "[[AC_PICTIONARY_TEAMS_1]]";
    internal const string QuizCommandPrefix = "[[AC_QUIZ_1]]";
    internal const string RouletteCommandPrefix = "[[AC_ROULETTE_1]]";
    internal const string ChocoRaceCommandPrefix = "[[AC_CHOCO_RACE_1]]";
    internal const string BingoCommandPrefix = "[[AC_BINGO_1]]";
    internal const string SkyRailCommandPrefix = "[[AC_SKY_RAIL_1]]";
    internal const string LuckyWheelCommandPrefix = "[[AC_LUCKY_WHEEL_1]]";
    internal const string SlotMachineCommandPrefix = "[[AC_SLOTS_1]]";
    internal const string PokerCommandPrefix = "[[AC_POKER_1]]";
    private static readonly TimeSpan SignedOutPollDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly Configuration configuration;
    private readonly Func<string?> displayNameProvider;
    private readonly Func<CharacterSession?> sessionProvider;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly PendingSnapshotCoalescer pendingSnapshots = new();
    private readonly RealtimeSendPacer sendPacer = new();
    private readonly LatestRealtimeBatch nextQueueBatches = new();
    private readonly RealtimeOutgoingPolicy outgoingPolicy = new();
    private int confirmedGeneration;
    private readonly object lifecycleGate = new();
    private readonly ManualResetEventSlim sendsDrained = new(true);
    private ClientWebSocket? socket;
    private Task? runTask;
    private bool disposed;
    private int activeSends;
    private volatile RealtimeConnectionState connectionState =
        RealtimeConnectionState.Disconnected;
    private int connectionGeneration;
    private string? rejectedSessionToken;

    // Snapshot of sessionProvider().AccountId taken at the moment a connection is established -
    // used for the rest of that connection's lifetime so a mid-session character switch can't
    // desync self-identity checks (e.g. the host-transfer comparison in Dispatch) from what the
    // server actually authenticated this socket as.
    private string? myAccountId;
    private StreamControl? lastHostState;
    private string? subtitleMediaUrl;
    private DateTime nextHostStateHeartbeatUtc;
    private string? pendingJoinHost;
    private string? pendingJoinPassword;

    //
    // Prevent periodic stream.state publication from recreating a room
    // while its host's stream.leave message is being sent.
    //
    private volatile bool leavingRoom;
    private readonly RoomCloseReconnectState roomCloseReconnect = new();
    internal bool IsClosingPreviousRoomConnection => roomCloseReconnect.Pending;

    private int streamMode;
    private readonly AlphaChannel.Plugin.Core.WatchPartyHostIdentity hostIdentity = new();
    internal string? CurrentHostDisplayName => hostIdentity.NameFor(HostId);
    internal string HostingSessionId { get; private set; } = Guid.NewGuid().ToString("D");
    internal StreamMode Mode
    {
        get => (StreamMode)Volatile.Read(ref streamMode);
        private set
        {
            if(value==StreamMode.Hosting&&Mode!=StreamMode.Hosting)HostingSessionId=Guid.NewGuid().ToString("D");
            Volatile.Write(ref streamMode,(int)value);
        }
    }
    internal string? HostId { get; private set; }
    internal string? WatchPartyJoinBlockReason
    {
        get
        {
            if (IsClosingPreviousRoomConnection) return RoomCloseReconnectState.Message;
            if (RecoveryBlocked) return "Resolve your previous Watch Party before starting or joining another.";
            var role=Mode;
            return AlphaChannel.Plugin.Core.WatchPartyJoinPolicy.BlockReason(
                role==StreamMode.Hosting,role==StreamMode.Viewing,
                !string.IsNullOrEmpty(Volatile.Read(ref pendingJoinHost)),leavingRoom);
        }
    }
    internal string? LocalUserId => myAccountId;
    internal ParticipantInfo[] Roster { get; private set; } = [];
    internal bool IsConnected => socket?.State == WebSocketState.Open;
    internal RealtimeConnectionState ConnectionState => connectionState;
    internal string ConnectionStatusText => connectionState switch
    {
        RealtimeConnectionState.Connecting => "Connecting",
        RealtimeConnectionState.Connected => "Connected",
        RealtimeConnectionState.Reconnecting => "Reconnecting",
        RealtimeConnectionState.AuthenticationRequired => "Authentication required",
        RealtimeConnectionState.ServerUnavailable => "Server unavailable",
        RealtimeConnectionState.AccountInUse => "Account connection is held by another plugin instance. Close other instances or restart the game after a failed plugin reload.",
        _ => "Disconnected",
    };

    // Filled from Dispatch (a background thread) - drained by MainWindow's Draw (main thread) each
    // frame, so this needs to be thread-safe for that handoff, same reasoning as everywhere else
    // in this plugin that crosses from the receive loop back to the main thread.
    internal ConcurrentQueue<(
        string UserId,
        string AccountDisplayName,
        string? CharacterName,
        string? WorldName,
        bool ShowInChat,
        string Glyph)> IncomingReactions
    { get; } = new();

    internal ConcurrentQueue<(
        string UserId,
        string DisplayName,
        string Text,
        Guid MessageId,
        Guid? ReplyToId,
        bool HasSharedMessageId)> IncomingChat
    { get; } = new();

    internal ConcurrentQueue<(string UserId, Guid MessageId, int Emoji)> IncomingChatMessageReactions
    { get; } = new();
    internal ConcurrentQueue<(string UserId, Guid MessageId)> IncomingChatMessageDeletes
    { get; } = new();
    internal ConcurrentQueue<(int ConnectionGeneration, StreamMode Mode, ParticipantInfo[] Participants)>
        IncomingRosterSnapshots { get; } = new();

    internal ConcurrentQueue<(string UserId, string Command)> IncomingWhiteboardCommands { get; } = new();
    internal ConcurrentQueue<(string UserId, string Command)> IncomingPictionaryCommands { get; } = new();
    internal ConcurrentQueue<(string UserId, string Command)> IncomingPictionaryTeamsCommands { get; } = new();
    internal ConcurrentQueue<(string UserId, string Command)> IncomingQuizCommands { get; } = new();
    internal ConcurrentQueue<(string UserId, string Command)> IncomingRouletteCommands { get; } = new();
    internal ConcurrentQueue<(string UserId, string Command)> IncomingChocoRaceCommands { get; } = new();
    internal ConcurrentQueue<(string UserId, string Command)> IncomingBingoCommands { get; } = new();
    internal ConcurrentQueue<(string UserId, string Command)> IncomingSkyRailCommands { get; } = new();
    internal ConcurrentQueue<(string UserId, string Command)> IncomingLuckyWheelCommands { get; } = new();
    internal ConcurrentQueue<(string UserId, string Command)> IncomingSlotMachineCommands { get; } = new();
    internal ConcurrentQueue<(string UserId, string Command)> IncomingPokerCommands { get; } = new();

    internal ConcurrentQueue<PartyNextQueueSnapshot> IncomingNextQueueSnapshots { get; } = new();
    private readonly Dictionary<string, string?[]> incomingNextQueueChunks = new(StringComparer.Ordinal);

    internal event Action<StreamControl>? OnState;
    internal ConcurrentQueue<(StreamMode PreviousMode, StreamControl? State, DateTime ReceivedUtc, int Generation)> IncomingHostTransfers { get; } = new();
    internal int ConnectionGeneration => Volatile.Read(ref connectionGeneration);
    private DateTime currentRoomStateReceivedUtc;
    private volatile bool hostPlaybackAdoptionPending;
    private long hostTransferUntilTicks;
    private StreamControl? inheritedHostTransferScreen;
    private volatile StreamControl? dutyScreenSnapshot;
    internal bool HasPreservedDutyScreen => dutyScreenSnapshot is not null;
    internal void PreserveDutyScreen() => dutyScreenSnapshot = CurrentRoomState;
    internal void ReleaseDutyScreen()
    {
        dutyScreenSnapshot = null;
        inheritedHostTransferScreen = null;
    }
    private (Vector3 Position, float Yaw, float Width, float Height, bool FixedRatioDisabled) inheritedScreenBaseline;
    internal void PreserveHostTransferScreen(StreamControl state, Vector3 position, float yaw,
        float width, float height, bool fixedRatioDisabled)
    {
        inheritedHostTransferScreen = state;
        inheritedScreenBaseline = (position, yaw, width, height, fixedRatioDisabled);
    }
    internal bool HostPlaybackAdoptionPending => hostPlaybackAdoptionPending;
    internal void CompleteHostPlaybackAdoption() => hostPlaybackAdoptionPending = false;
    internal event Action? OnJoined;
    internal event Action<string?>? OnDeclined;
    internal event Action? OnEnded;
    internal event Action? OnViewerPartyEnded;

    // Fired when an admin reset this user's name server-side - the plugin should prompt for a new
    // one again, same as the first-connect flow.
    internal event Action? OnRenameRequired;

    // friend.*/presence.*/dm.*/activity.* pushes - see AlphaChannel.Contracts.SocialControl. These
    // are push-only (the server never expects a SocialControl back), so unlike the stream.* events
    // above there's no corresponding SendXAsync method for most of them - mutations go over REST
    // (Social/DmService/etc. clients, added alongside the features that use them).
    internal event Action<SocialControl>? OnFriendRequestReceived;
    internal event Action<SocialControl>? OnFriendAccepted;
    internal event Action<SocialControl>? OnFriendRemoved;
    internal event Action<SocialControl>? OnPresenceUpdate;
    internal event Action<int>? OnOnlineCount;
    internal event Action<SocialControl>? OnDmMessage;
    internal event Action<SocialControl>? OnDmDeleted;
    internal event Action<SocialControl>? OnActivityNew;

    internal StreamClient(Configuration configuration, Func<string?> displayNameProvider, Func<CharacterSession?> sessionProvider)
    {
        this.configuration = configuration;
        this.displayNameProvider = displayNameProvider;
        this.sessionProvider = sessionProvider;
    }

    internal void Start()
    {
        lock (lifecycleGate)
        {
            if (disposed || runTask is { IsCompleted: false }) return;
            var token = lifetime.Token;
            runTask = Task.Run(() => RunAsync(token));
            capacityTask = Task.Run(() => RunCapacityAsync(token));
            moderationTask = Task.Run(() => RunPartyModeration(token));
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        var reconnect = new RealtimeReconnectPolicy();
        string? busyAccount = null;
        var hasConnected = false;

        while (!token.IsCancellationRequested)
        {
            var session = sessionProvider();
            if (session is null)
            {
                SetConnectionState(RealtimeConnectionState.AuthenticationRequired);
                ClearRoomForAuthenticationLoss();
                rejectedSessionToken = null;
                reconnect.Reset();
                await DelaySafelyAsync(SignedOutPollDelay, token).ConfigureAwait(false);
                continue;
            }

            if (string.Equals(
                    rejectedSessionToken,
                    session.Token,
                    StringComparison.Ordinal))
            {
                SetConnectionState(RealtimeConnectionState.AuthenticationRequired);
                await DelaySafelyAsync(SignedOutPollDelay, token).ConfigureAwait(false);
                continue;
            }

            SetConnectionState(
                hasConnected || reconnect.Attempts > 0
                    ? RealtimeConnectionState.Reconnecting
                    : RealtimeConnectionState.Connecting);

            var openedSocket = false;
            var connectedAt = 0L;
            var generation = 0;
            try
            {
                using var accountLease = RealtimeAccountLease.Acquire(configuration.RelayServerUrl, session.AccountId);
                busyAccount = null;
                if (accountLease.Cooldown > TimeSpan.Zero)
                    await DelaySafelyAsync(accountLease.Cooldown, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var sessionAfterCooldown = sessionProvider();
                if (sessionAfterCooldown is null || sessionAfterCooldown.AccountId != session.AccountId || sessionAfterCooldown.Token != session.Token)
                    continue;
                accountLease.StartConnecting();
                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Authorization", $"Bearer {session.Token}");

                using var connection =
                    CancellationTokenSource.CreateLinkedTokenSource(token);
                connection.CancelAfter(ConnectTimeout);

                await ws.ConnectAsync(
                        BuildUri(configuration.RelayServerUrl),
                        connection.Token)
                    .ConfigureAwait(false);

                generation = Interlocked.Increment(ref connectionGeneration);
                capacityAuthorizedVersion=-1;
                openedSocket = true;
                connectedAt = Environment.TickCount64;
                recoveryCheckPending = true;
                socket = ws;
                myAccountId = session.AccountId;
                hasConnected = true;
                rejectedSessionToken = null;
                // An HTTP upgrade alone does not mean the server completed its
                // presence/database setup. Confirm after a valid incoming frame.

                if (displayNameProvider() is { Length: > 0 } name)
                {
                    await SendHelloAsync(name).ConfigureAwait(false);
                }

                await CheckRoomRecoveryAsync(session, token).ConfigureAwait(false);
                if (!RecoveryBlocked) await FlushPendingAsync(token).ConfigureAwait(false);

                using var activeConnection =
                    CancellationTokenSource.CreateLinkedTokenSource(token);

                var receiveTask =
                    ReceiveLoopAsync(ws, generation, activeConnection.Token);
                var sessionMonitorTask =
                    MonitorSessionAsync(session, generation, accountLease, activeConnection.Token);

                await Task.WhenAny(receiveTask, sessionMonitorTask)
                    .ConfigureAwait(false);

                // Keep the receiver alive to finish the close handshake. Cancelling
                // it first aborts ClientWebSocket and gives the server less time to
                // finish the old connection before our next connection starts.
                if (!await RealtimeSocketSafety.CloseAndDrainAsync(ws, sendLock, receiveTask,
                        "Realtime connection ending", RealtimeSocketSafety.CloseTimeout).ConfigureAwait(false))
                    { }
                activeConnection.Cancel();

                try
                {
                    await Task.WhenAll(receiveTask, sessionMonitorTask)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (activeConnection.IsCancellationRequested)
                {
                    // The companion task is intentionally cancelled after the
                    // socket closes or the active character/session changes.
                }

                var currentSession = sessionProvider();
                if (currentSession is null ||
                    !string.Equals(
                        currentSession.AccountId,
                        session.AccountId,
                        StringComparison.Ordinal))
                {
                    ClearRoomForAuthenticationLoss();
                }
            }
            catch (RealtimeAccountInUseException)
            {
                roomCloseReconnect.Reset();
                SetConnectionState(RealtimeConnectionState.AccountInUse);
                if (busyAccount != session.AccountId)
                {
                    busyAccount = session.AccountId;
                    AepLog.Warning("[Realtime] Another plugin instance holds the account connection. Close duplicate instances or restart the game after a failed plugin reload.");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                roomCloseReconnect.ConnectionFailed(generation, openedSocket);
                {}
                SetConnectionState(RealtimeConnectionState.ServerUnavailable);
            }
            catch (Exception exception)
            {
                if (IsAuthenticationRejected(exception))
                {
                    roomCloseReconnect.Reset();
                    rejectedSessionToken = session.Token;
                    SetConnectionState(RealtimeConnectionState.AuthenticationRequired);
                    AepLog.Warning(
                        "[Realtime] Authentication rejected. Reconnection paused until the session changes.");
                    continue;
                }

                roomCloseReconnect.ConnectionFailed(generation, openedSocket);
                SetConnectionState(RealtimeConnectionState.ServerUnavailable);
                {}
            }
            finally
            {
                socket = null;
                if (openedSocket)
                    reconnect.ObserveConnection(TimeSpan.FromMilliseconds(Environment.TickCount64 - connectedAt),
                        Volatile.Read(ref confirmedGeneration) == generation);
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            var delay = reconnect.NextDelay(openedSocket, 0.8d + Random.Shared.NextDouble() * 0.4d);
            if (connectionState != RealtimeConnectionState.AccountInUse)
                SetConnectionState(RealtimeConnectionState.ServerUnavailable);
            await DelaySafelyAsync(delay, token).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket ws,
        int generation,
        CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        while (!token.IsCancellationRequested && (ws.State is WebSocketState.Open or WebSocketState.CloseSent))
        {
            using var stream = new MemoryStream();
            ValueWebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                try
                {
                    RealtimeSocketSafety.AppendFragment(stream, buffer.AsSpan(0, result.Count));
                }
                catch (RealtimeMessageTooLargeException)
                {
                    AepLog.Warning("[Realtime] Rejected oversized incoming message.");
                    await RealtimeSocketSafety.CloseOutputAsync(ws, sendLock, "Incoming message too large",
                        RealtimeSocketSafety.CloseTimeout, WebSocketCloseStatus.MessageTooBig).ConfigureAwait(false);
                    return;
                }
            } while (!result.EndOfMessage);

            var bytes = stream.ToArray();
            string? type;
            try
            {
                using var peek = JsonDocument.Parse(bytes);
                type = peek.RootElement.TryGetProperty("Type", out var typeEl) ? typeEl.GetString() : null;
            }
            catch (JsonException)
            {
                AepLog.Warning("[Stream] malformed message.");
                continue;
            }

            // friend./presence./dm./activity. share the socket with stream. but are a distinct
            // envelope shape (SocialControl, not StreamControl) - see AlphaChannel.Contracts.
            var isSocial = type is not null && (type.StartsWith("friend.", StringComparison.Ordinal) ||
                type.StartsWith("presence.", StringComparison.Ordinal) ||
                type.StartsWith("dm.", StringComparison.Ordinal) ||
                type.StartsWith("activity.", StringComparison.Ordinal));

            try
            {
                if (isSocial)
                {
                    if (JsonSerializer.Deserialize<SocialControl>(bytes) is { } social)
                    {
                        if (generation == Volatile.Read(ref connectionGeneration))
                        {
                            DispatchSocial(social);
                            ConfirmConnection(generation);
                        }
                    }
                }
                else if (JsonSerializer.Deserialize<StreamControl>(bytes) is { } message)
                {
                    if (generation == Volatile.Read(ref connectionGeneration))
                    {
                        Dispatch(message);
                        ConfirmConnection(generation);
                    }
                }
            }
            catch (JsonException)
            {
                AepLog.Warning("[Stream] malformed message.");
            }
        }
    }

    private void ConfirmConnection(int generation)
    {
        if (generation != Volatile.Read(ref connectionGeneration)) return;
        if (Interlocked.Exchange(ref confirmedGeneration, generation) == generation) return;
        roomCloseReconnect.Confirm(generation);
        SetConnectionState(RealtimeConnectionState.Connected);
        
    }

    private void Dispatch(StreamControl message)
    {
        switch (message.Type)
        {
            case SignalType.StreamHostWaiting:
                if (Mode == StreamMode.Viewing && message.HostId == HostId)
                {
                    Interlocked.Exchange(ref waitingForHostUntil, message.RecoveryExpiresAtUnix ?? 1);
                    Interlocked.Exchange(ref recoveryNoticePending, 1);
                }
                break;
            case SignalType.StreamResumed:
                ReceiveRoomResumed(message);
                break;
            case SignalType.StreamLeft:
                AcknowledgePartyLeave();
                recoveryCheckPending = false;
                break;
            case SignalType.StreamRecoveryUnavailable:
                ClearRoomRecovery();
                recoveryCheckPending = false;
                ClearRoomForAuthenticationLoss();
                break;
            case SignalType.StreamState:
                if (Mode == StreamMode.Viewing && string.Equals(message.HostId, HostId, StringComparison.OrdinalIgnoreCase))
                    hostIdentity.SelectHost(HostId, [], message.DisplayName);

                //
                // Retain the complete current room state so the Watch
                // Party details tab can render the same information for
                // hosts and viewers.
                //
                CurrentRoomState =
                    message;
                currentRoomStateReceivedUtc = DateTime.UtcNow;

                RoomDescription =
                    message.Description ?? "";

                RoomLocation =
                    message.Location ?? "";

                if (message.Kind is { } roomKind)
                {
                    RoomKind =
                        roomKind;
                }

                if (message.IsPrivate is { } isPrivate)
                {
                    IsPrivate =
                        isPrivate;
                }

                //
                // Password is deliberately not assigned here. The server
                // sanitizes it before broadcasting stream.state.
                //
                OnState?.Invoke(
                    message);

                break;
            case SignalType.StreamJoined:
                Mode = StreamMode.Viewing;
                _=ConfirmCapacitySeat();
                // Echoed back as the host's real UserId (JoinAsync sent their display name) - keep
                // it, in case anything ever needs the resolved identity rather than the typed name.
                if (message.HostId is { Length: > 0 } resolvedHostId)
                {
                    HostId = resolvedHostId;
                    hostIdentity.SelectHost(resolvedHostId, Roster, message.DisplayName);
                }

                OnJoined?.Invoke();
                break;
            case SignalType.StreamDeclined:
                ReleaseCapacitySeat();CancelCapacityRetry();
                Mode = StreamMode.None;
                pendingJoinHost = null;
                pendingJoinPassword = null;
                OnDeclined?.Invoke(message.Reason);
                break;
            case SignalType.StreamReaction when message.Reaction?.StartsWith(CapacityDisplayPrefix,StringComparison.Ordinal)==true:
                ReceiveCapacityDisplay(message);
                break;
            case SignalType.StreamRoster:
                hostIdentity.ObserveRoster(message.Participants ?? []);
                Roster = message.Participants ?? [];
                IncomingRosterSnapshots.Enqueue((Volatile.Read(ref connectionGeneration), Mode, Roster));
                break;
            case SignalType.StreamEnded:
                if (HasPendingPartyLeave) AcknowledgePartyLeave();
                ClearRoomRecovery();
                hostIdentity.Reset();
                ReleaseCapacitySeat();CancelCapacityRetry();
                var wasViewingEndedParty = Mode == StreamMode.Viewing;
                dutyScreenSnapshot = null;
                inheritedHostTransferScreen = null;
                IncomingHostTransfers.Clear();
                hostPlaybackAdoptionPending = false;
                Interlocked.Exchange(ref hostTransferUntilTicks, 0);
                Mode = StreamMode.None;
                HostId = null;
                lastHostState = null;
                pendingJoinHost = null;
                pendingJoinPassword = null;

                IncomingChat.Clear();
                IncomingChatMessageReactions.Clear();
                IncomingChatMessageDeletes.Clear();

                IncomingRosterSnapshots.Clear();
                IncomingWhiteboardCommands.Clear();
                Interlocked.Exchange(ref incomingBroadcastAllowanceRefresh, 0);
                IncomingPictionaryCommands.Clear();
                IncomingPictionaryTeamsCommands.Clear();
                IncomingQuizCommands.Clear();
                IncomingRouletteCommands.Clear();
                IncomingChocoRaceCommands.Clear();
                IncomingBingoCommands.Clear();
                IncomingSkyRailCommands.Clear();
                IncomingSlotMachineCommands.Clear();
            IncomingLuckyWheelCommands.Clear();
                IncomingPokerCommands.Clear();

                IncomingNextQueueSnapshots.Clear();
                incomingNextQueueChunks.Clear();

                OnEnded?.Invoke();
                if (wasViewingEndedParty) OnViewerPartyEnded?.Invoke();
                break;

            case SignalType.StreamRenameRequired:
                OnRenameRequired?.Invoke();
                break;

            case SignalType.StreamHostTransferred:
                if (Mode is not (StreamMode.Hosting or StreamMode.Viewing) ||
                    string.IsNullOrWhiteSpace(message.HostId) ||
                    (Mode == StreamMode.Hosting && message.HostId == myAccountId) ||
                    (Mode == StreamMode.Viewing && message.HostId == HostId)) break;
                hostIdentity.SelectHost(message.HostId, Roster, message.DisplayName);
                var previousMode = Mode;
                var transferState = CurrentRoomState;
                dutyScreenSnapshot = null;
                inheritedHostTransferScreen = null;
                hostPlaybackAdoptionPending = message.HostId == myAccountId;
                Interlocked.Exchange(ref hostTransferUntilTicks, 0);
                lastHostState = null;
                nextHostStateHeartbeatUtc = DateTime.MinValue;
                if (message.HostId == myAccountId)
                {
                    Mode = StreamMode.Hosting;
                    HostId = null;
                    pendingJoinHost = null;
                    pendingJoinPassword = null;
                    subtitleMediaUrl = transferState?.Url;
                    SubtitleLanguage = transferState?.SubtitleLanguage;
                    SubtitleName = transferState?.SubtitleName;
                }
                else
                {
                    Mode = StreamMode.Viewing;
                    HostId = message.HostId;
                    pendingJoinHost = message.HostId;
                    pendingJoinPassword = RoomPassword;
                }

                IncomingHostTransfers.Enqueue((previousMode, transferState, currentRoomStateReceivedUtc, Volatile.Read(ref connectionGeneration)));

                break;

            case SignalType.StreamReaction
               when message.Reaction is { Length: > 0 } pictionaryReaction &&
                    pictionaryReaction.StartsWith(PictionaryCommandPrefix, StringComparison.Ordinal) &&
                    message.UserId is { Length: > 0 } pictionarySender:
                // Pictionary follows the same host-authoritative direction as the whiteboard:
                // viewers submit proposals to the host, while viewers only accept state from it.
                if ((Mode == StreamMode.Viewing &&
                     string.Equals(pictionarySender, HostId, StringComparison.Ordinal)) ||
                    (Mode == StreamMode.Hosting &&
                     !string.Equals(pictionarySender, myAccountId, StringComparison.Ordinal)))
                {
                    IncomingPictionaryCommands.Enqueue(
                        (pictionarySender, pictionaryReaction[PictionaryCommandPrefix.Length..]));
                }

                break;

            case SignalType.StreamReaction or SignalType.StreamPictionaryTeamsPrivate
               when message.Reaction is { Length: > 0 } teamsReaction &&
                    teamsReaction.StartsWith(PictionaryTeamsCommandPrefix, StringComparison.Ordinal) &&
                    message.UserId is { Length: > 0 } teamsSender:
                if ((Mode == StreamMode.Viewing && string.Equals(teamsSender, HostId, StringComparison.Ordinal)) ||
                    (Mode == StreamMode.Hosting && !string.Equals(teamsSender, myAccountId, StringComparison.Ordinal)))
                    IncomingPictionaryTeamsCommands.Enqueue((teamsSender, teamsReaction[PictionaryTeamsCommandPrefix.Length..]));
                break;

            case SignalType.StreamReaction
               when message.Reaction is { Length: > 0 } quizReaction &&
                    quizReaction.StartsWith(QuizCommandPrefix, StringComparison.Ordinal) &&
                    message.UserId is { Length: > 0 } quizSender:
                if ((Mode == StreamMode.Viewing &&
                     string.Equals(quizSender, HostId, StringComparison.Ordinal)) ||
                    (Mode == StreamMode.Hosting &&
                     !string.Equals(quizSender, myAccountId, StringComparison.Ordinal)))
                {
                    IncomingQuizCommands.Enqueue(
                        (quizSender, quizReaction[QuizCommandPrefix.Length..]));
                }

                break;

            case SignalType.StreamReaction
               when message.Reaction is { Length: > 0 } raceReaction &&
                    raceReaction.StartsWith(ChocoRaceCommandPrefix, StringComparison.Ordinal) &&
                    message.UserId is { Length: > 0 } raceSender:
                if ((Mode == StreamMode.Viewing &&
                     string.Equals(raceSender, HostId, StringComparison.Ordinal)) ||
                    (Mode == StreamMode.Hosting &&
                     !string.Equals(raceSender, myAccountId, StringComparison.Ordinal)))
                {
                    IncomingChocoRaceCommands.Enqueue(
                        (raceSender, raceReaction[ChocoRaceCommandPrefix.Length..]));
                }
                break;

            case SignalType.StreamReaction
               when message.Reaction is { Length: > 0 } rouletteReaction &&
                    rouletteReaction.StartsWith(RouletteCommandPrefix, StringComparison.Ordinal) &&
                    message.UserId is { Length: > 0 } rouletteSender:
                if ((Mode == StreamMode.Viewing &&
                     string.Equals(rouletteSender, HostId, StringComparison.Ordinal)) ||
                    (Mode == StreamMode.Hosting &&
                     !string.Equals(rouletteSender, myAccountId, StringComparison.Ordinal)))
                {
                    IncomingRouletteCommands.Enqueue(
                        (rouletteSender, rouletteReaction[RouletteCommandPrefix.Length..]));
                }

                break;

            case SignalType.StreamReaction
               when message.Reaction is { Length: > 0 } bingoReaction &&
                    bingoReaction.StartsWith(BingoCommandPrefix, StringComparison.Ordinal) &&
                    message.UserId is { Length: > 0 } bingoSender:
                if ((Mode == StreamMode.Viewing &&
                     string.Equals(bingoSender, HostId, StringComparison.Ordinal)) ||
                    (Mode == StreamMode.Hosting &&
                     !string.Equals(bingoSender, myAccountId, StringComparison.Ordinal)))
                    IncomingBingoCommands.Enqueue(
                        (bingoSender, bingoReaction[BingoCommandPrefix.Length..]));
                break;

            case SignalType.StreamReaction
               when message.Reaction is { Length: > 0 } skyRailReaction &&
                    skyRailReaction.StartsWith(SkyRailCommandPrefix, StringComparison.Ordinal) &&
                    message.UserId is { Length: > 0 } skyRailSender:
                if ((Mode == StreamMode.Viewing &&
                     string.Equals(skyRailSender, HostId, StringComparison.Ordinal)) ||
                    (Mode == StreamMode.Hosting &&
                     !string.Equals(skyRailSender, myAccountId, StringComparison.Ordinal)))
                    IncomingSkyRailCommands.Enqueue(
                        (skyRailSender, skyRailReaction[SkyRailCommandPrefix.Length..]));
                break;

            case SignalType.StreamReaction
               when message.Reaction is { Length: > 0 } wheelReaction &&
                    wheelReaction.StartsWith(LuckyWheelCommandPrefix, StringComparison.Ordinal) &&
                    message.UserId is { Length: > 0 } wheelSender:
                if (Mode == StreamMode.Viewing && string.Equals(wheelSender, HostId, StringComparison.Ordinal))
                    IncomingLuckyWheelCommands.Enqueue((wheelSender, wheelReaction[LuckyWheelCommandPrefix.Length..]));
                break;

            case SignalType.StreamReaction
               when message.Reaction is { Length: > 0 } slotReaction &&
                    slotReaction.StartsWith(SlotMachineCommandPrefix, StringComparison.Ordinal) &&
                    message.UserId is { Length: > 0 } slotSender:
                if ((Mode == StreamMode.Viewing &&
                     string.Equals(slotSender, HostId, StringComparison.Ordinal)) ||
                    (Mode == StreamMode.Hosting &&
                     !string.Equals(slotSender, myAccountId, StringComparison.Ordinal)))
                    IncomingSlotMachineCommands.Enqueue(
                        (slotSender, slotReaction[SlotMachineCommandPrefix.Length..]));
                break;

            case SignalType.StreamReaction
               when message.Reaction is { Length: > 0 } pokerReaction &&
                    pokerReaction.StartsWith(PokerCommandPrefix, StringComparison.Ordinal) &&
                    message.UserId is { Length: > 0 } pokerSender:
                if ((Mode == StreamMode.Viewing &&
                     string.Equals(pokerSender, HostId, StringComparison.Ordinal)) ||
                    (Mode == StreamMode.Hosting &&
                     !string.Equals(pokerSender, myAccountId, StringComparison.Ordinal)))
                    IncomingPokerCommands.Enqueue((pokerSender, pokerReaction[PokerCommandPrefix.Length..]));
                break;

            case SignalType.StreamPokerPrivate
                when message.Reaction is { Length: > 0 } privatePokerReaction &&
                     privatePokerReaction.StartsWith(PokerCommandPrefix + "H|", StringComparison.Ordinal) &&
                     message.UserId is { Length: > 0 } privatePokerSender &&
                     Mode == StreamMode.Viewing &&
                     string.Equals(privatePokerSender, HostId, StringComparison.Ordinal):
                IncomingPokerCommands.Enqueue((privatePokerSender,
                    privatePokerReaction[PokerCommandPrefix.Length..]));
                break;

            case SignalType.StreamReaction
               when message.Reaction is { Length: > 0 } whiteboardReaction &&
                    whiteboardReaction.StartsWith(WhiteboardCommandPrefix, StringComparison.Ordinal) &&
                    message.UserId is { Length: > 0 } whiteboardSender:
                // Viewers accept authoritative commands from the host. Hosts also receive
                // contribution proposals from viewers while a team whiteboard is active; the
                // MainWindow validates those proposals before relaying an authoritative stroke.
                if ((Mode == StreamMode.Viewing &&
                     string.Equals(whiteboardSender, HostId, StringComparison.Ordinal)) ||
                    (Mode == StreamMode.Hosting &&
                     !string.Equals(whiteboardSender, myAccountId, StringComparison.Ordinal)))
                {
                    IncomingWhiteboardCommands.Enqueue(
                        (whiteboardSender, whiteboardReaction[WhiteboardCommandPrefix.Length..]));
                }

                break;

            case SignalType.StreamReaction
                when message.Reaction?.StartsWith(BroadcastAllowanceRefreshCommand, StringComparison.Ordinal) == true:
                // Reserved control messages never become visible reactions or chat entries.
                if (message.Reaction == BroadcastAllowanceRefreshCommand && Mode == StreamMode.Viewing &&
                    string.Equals(message.UserId, HostId, StringComparison.OrdinalIgnoreCase))
                    Interlocked.Exchange(ref incomingBroadcastAllowanceRefresh, 1);
                break;

            case SignalType.StreamReaction
               when message.Reaction is { Length: > 0 } glyph &&
                    message.UserId is { Length: > 0 } senderId:
                {
                    string accountDisplayName;

                    // ---------------------------------------------------------
                    // Our own reaction
                    // ---------------------------------------------------------
                    //
                    // The host is not necessarily present in Roster, so resolve
                    // our own account directly from the local display-name
                    // provider first.
                    // ---------------------------------------------------------

                    if (string.Equals(
                            senderId,
                            myAccountId,
                            StringComparison.Ordinal))
                    {
                        accountDisplayName =
                            message.Reason ??
                            displayNameProvider() ??
                            "You";
                    }
                    else
                    {
                        // -----------------------------------------------------
                        // Another party member
                        // -----------------------------------------------------
                        //
                        // The roster already maps the relay UserId to the
                        // friendly Alpha Channel display name.
                        // -----------------------------------------------------

                        var participant =
                            Roster.FirstOrDefault(
                                member =>
                                    string.Equals(
                                        member.UserId,
                                        senderId,
                                        StringComparison.Ordinal));

                        accountDisplayName =
                            message.Reason ??
                            participant?.DisplayName ??
                            "Someone";
                    }

                    IncomingReactions.Enqueue(
                        (
                            senderId,
                            accountDisplayName,
                            message.DisplayName,
                            message.Location,
                            message.IsPrivate != true,
                            glyph
                        ));

                    break;
                }

            case SignalType.StreamChat when message.ChatText is { Length: > 0 } text:
                {
                    var displayName =
                        message.DisplayName ??
                        message.UserId ??
                        "Someone";

                    // Retired clients may still send these controls. Never display
                    // protocol payloads as chat or revive the old request flow.
                    if (text.StartsWith("[[AC_MEDIA_", StringComparison.Ordinal)) break;

                    const string nextQueuePrefix = "[[AC_NEXT_QUEUE_1]]";

                    var sentByHost = Mode == StreamMode.Hosting ||
                        string.Equals(message.UserId, HostId, StringComparison.Ordinal);

                    if (text.StartsWith(nextQueuePrefix, StringComparison.Ordinal))
                    {
                        var parts = text[nextQueuePrefix.Length..].Split('|', 4);
                        if (sentByHost && parts.Length == 4 &&
                            int.TryParse(parts[1], out var chunkIndex) &&
                            int.TryParse(parts[2], out var chunkCount) &&
                            chunkCount is > 0 and <= 32 && chunkIndex >= 0 && chunkIndex < chunkCount)
                        {
                            if (!incomingNextQueueChunks.TryGetValue(parts[0], out var chunks) || chunks.Length != chunkCount)
                            {
                                chunks = new string?[chunkCount];
                                incomingNextQueueChunks[parts[0]] = chunks;
                            }

                            chunks[chunkIndex] = parts[3];
                            if (chunks.All(chunk => chunk is not null))
                            {
                                incomingNextQueueChunks.Remove(parts[0]);
                                try
                                {
                                    var json = Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(chunks)));
                                    var snapshot = JsonSerializer.Deserialize<PartyNextQueueSnapshot>(json);
                                    if (snapshot is not null)
                                        IncomingNextQueueSnapshots.Enqueue(snapshot);
                                }
                                catch (Exception exception) when (exception is FormatException or JsonException)
                                {
                                    AepLog.Warning("[Stream] Ignored malformed next-queue snapshot.");
                                }
                            }
                        }

                        break;
                    }

                    if (text.StartsWith(ChatReactionPrefix, StringComparison.Ordinal))
                    {
                        var parts = text[ChatReactionPrefix.Length..].Split('|');
                        if (parts.Length == 2 &&
                            Guid.TryParseExact(parts[0], "N", out var targetId) &&
                            int.TryParse(parts[1], out var emoji) &&
                            emoji is >= 0 and < 20)
                        {
                            IncomingChatMessageReactions.Enqueue((message.UserId ?? string.Empty, targetId, emoji));
                        }
                        break;
                    }

                    if (text.StartsWith(ChatDeletePrefix, StringComparison.Ordinal))
                    {
                        if (Guid.TryParseExact(text[ChatDeletePrefix.Length..], "N", out var targetId))
                            IncomingChatMessageDeletes.Enqueue((message.UserId ?? string.Empty, targetId));
                        break;
                    }

                    if (text.StartsWith(RichChatPrefix, StringComparison.Ordinal))
                    {
                        var parts = text[RichChatPrefix.Length..].Split('|', 3);
                        if (parts.Length == 3 &&
                            Guid.TryParseExact(parts[0], "N", out var chatId))
                        {
                            var replyId = Guid.TryParseExact(parts[1], "N", out var parsedReply)
                                ? parsedReply : (Guid?)null;
                            IncomingChat.Enqueue((message.UserId ?? string.Empty, displayName, parts[2], chatId, replyId, true));
                        }
                        break;
                    }

                    IncomingChat.Enqueue((message.UserId ?? string.Empty, displayName, text, Guid.NewGuid(), null, false));

                    break;
                }

        }
    }

    private void DispatchSocial(SocialControl message)
    {
        switch (message.Type)
        {
            case SocialSignalType.FriendRequestReceived:
                OnFriendRequestReceived?.Invoke(message);
                break;
            case SocialSignalType.FriendAccepted:
                OnFriendAccepted?.Invoke(message);
                break;
            case SocialSignalType.FriendRemoved:
                OnFriendRemoved?.Invoke(message);
                break;
            case SocialSignalType.PresenceUpdate:
                OnPresenceUpdate?.Invoke(message);
                break;
            case SocialSignalType.OnlineCount when message.OnlineCount is { } count:
                OnOnlineCount?.Invoke(count);
                break;
            case SocialSignalType.DmMessage:
                OnDmMessage?.Invoke(message);
                break;
            case SocialSignalType.DmDeleted:
                OnDmDeleted?.Invoke(message);
                break;
            case SocialSignalType.ActivityNew:
                OnActivityNew?.Invoke(message);
                break;
        }
    }

    internal Task SendHelloAsync(string displayName) =>
        SendAsync(new StreamControl { Type = SignalType.StreamHello, DisplayName = displayName, SupportsRoomRecovery = true });

    //
    // Current room metadata.
    //
    // Hosts populate these before publishing. Viewers receive the same
    // values from the host's stream.state message. Password is never
    // echoed by the server and therefore remains host-only.
    //
    internal bool IsPrivate { get; set; }

    internal string? RoomDescription { get; set; }

    internal string? RoomLocation { get; set; }

    internal RoomKind RoomKind { get; set; } =
        RoomKind.Public;

    internal string? RoomPassword { get; set; }

    internal string? SubtitleLanguage { get; set; }
    internal string? SubtitleName { get; set; }

    internal StreamControl? CurrentRoomState
    {
        get;
        private set;
    }

    //
    // Compatibility overload for existing callers that only provide one
    // fixed-ratio scale value.
    //
    internal Task PublishStateAsync(
        string? url,
        double positionSeconds,
        bool paused,
        Vector3? screenPosition,
        float? screenYaw,
        float? screenScale,
        string? mediaTitle,
        string? mediaThumbnailUrl)
    {
        return PublishStateAsync(
            url,
            positionSeconds,
            paused,
            screenPosition,
            screenYaw,
            screenScale,
            screenDisableFixedScaleRatio: false,
            screenWidthScale: screenScale,
            screenHeightScale: screenScale,
            mediaTitle,
            mediaThumbnailUrl);
    }

    internal Task PublishStateAsync(
    string? url,
    double positionSeconds,
    bool paused,
Vector3? screenPosition,
float? screenYaw,
float? screenScale,
bool? screenDisableFixedScaleRatio,
float? screenWidthScale,
float? screenHeightScale,
string? mediaTitle,
    string? mediaThumbnailUrl)
    {
        //
        // LeaveAsync changes Mode immediately, but a framework update that
        // already began may still reach this method. Do not let that stale
        // update put the client back into Hosting or recreate the room.
        //
        if (RecoveryBlocked || WaitingForHost || leavingRoom || Mode == StreamMode.Viewing || hostPlaybackAdoptionPending ||
            DateTime.UtcNow.Ticks < Interlocked.Read(ref hostTransferUntilTicks))
        {
            return Task.CompletedTask;
        }

        if (HostHalloweenSession is null && HostPartyCountdownUrl is { } countdownUrl)
        { url = countdownUrl; positionSeconds = 0; paused = true; mediaTitle = "Starting soon"; mediaThumbnailUrl = null; }

        if (!string.Equals(subtitleMediaUrl, url, StringComparison.Ordinal))
        {
            if (subtitleMediaUrl is not null)
            {
                SubtitleLanguage = null;
                SubtitleName = null;
            }
            subtitleMediaUrl = url;
        }

        // An update that began as host must not promote this client again
        // if the network thread demoted it during snapshot construction.
        lock(lifecycleGate)
        {
            if(leavingRoom || IsClosingPreviousRoomConnection || !string.IsNullOrEmpty(pendingJoinHost)) return Task.CompletedTask;
            if (Mode == StreamMode.None)
            {
                HostingSessionId=Guid.NewGuid().ToString("D");
                Interlocked.CompareExchange(ref streamMode, (int)StreamMode.Hosting, (int)StreamMode.None);
            }
        }
        if (Mode != StreamMode.Hosting) return Task.CompletedTask;

        var previousHostState = lastHostState;
        var state =
            new StreamControl
            {
                Type =
                    SignalType.StreamState,

                HostId =
                    myAccountId,

                // The server caches and replays this existing field with
                // stream.state; the visible room description stays untouched.
                Reason = screenPosition is null
                    ? null
                    : WatchPartyTvLocation.TryReadCurrent(out var tvLocation)
                        ? tvLocation.Encode()
                        : WatchPartyScreenSyncPause.LocationReason(CurrentRoomState?.Reason) ?? "TV1:?",

                Url =
                    url,

                MediaTitle = InternetArchiveMediaDisplay.IsArchiveUrl(url)
                    ? InternetArchiveMediaDisplay.Title(url, mediaTitle) : mediaTitle,

                MediaThumbnailUrl =
                    mediaThumbnailUrl,

                SubtitleLanguage = SubtitleLanguage,
                SubtitleName = SubtitleName,

                PositionSeconds =
                    positionSeconds,

                Paused =
                    paused,

                ScreenX =
                    screenPosition?.X,

                ScreenY =
                    screenPosition?.Y,

                ScreenZ =
                    screenPosition?.Z,

                ScreenYaw =
                    screenYaw,

                ScreenScale =
    screenScale,

                ScreenDisableFixedScaleRatio =
    screenDisableFixedScaleRatio,

                ScreenWidthScale =
    screenWidthScale,

                ScreenHeightScale =
    screenHeightScale,

                IsPrivate =
                    IsPrivate,

                Description =
                    RoomDescription ?? "",

                Location =
                    RoomLocation ?? "",

                Kind =
                    RoomKind,

                Password =
                    RoomKind ==
                    RoomKind.Locked
                        ? RoomPassword
                        : null,
            };

        // Hiding the host's local TV does not remove the viewers' placement.
        // Keep its last published transform and area until a new placement
        // is supplied, while continuing to publish playback normally.
        if (screenPosition is null && previousHostState is not null)
            state = WatchPartyHostHandover.PreserveScreen(state, previousHostState);

        if (inheritedHostTransferScreen is { } inheritedScreen)
        {
            // The new host may be watching in a different area or using a
            // local TV position. Retain the room's placement until they move it.
            if (screenPosition is null ||
                (screenPosition == inheritedScreenBaseline.Position && screenYaw == inheritedScreenBaseline.Yaw &&
                 screenWidthScale == inheritedScreenBaseline.Width && screenHeightScale == inheritedScreenBaseline.Height &&
                 screenDisableFixedScaleRatio == inheritedScreenBaseline.FixedRatioDisabled))
                state = WatchPartyHostHandover.PreserveScreen(state, inheritedScreen);
            else inheritedHostTransferScreen = null;
        }
        if (dutyScreenSnapshot is { } dutyScreen)
            state = WatchPartyHostHandover.PreserveScreen(state, dutyScreen) with
            {
                Reason = WatchPartyScreenSyncPause.MarkHeld(dutyScreen.Reason),
            };
        state = state with { Reason = HalloweenTvFrame.EncodeMode(HostFrameSelection.EncodeReason(state.Reason),
            HostHalloweenFrameEnabled&&HalloweenTvFrame.IsVideo(state.Url)) };
        if (!string.IsNullOrWhiteSpace(url)) HostHalloweenCompletedSession = null;
        state = state with { Reason = HostHalloweenSession is { } halloween
            ? halloween.Encode(HalloweenSession.MarkCompleted(state.Reason ?? "", null))
            : HalloweenSession.MarkCompleted(state.Reason ?? "", HostHalloweenCompletedSession) };
        state = state with { DisplayName = displayNameProvider() };
        state = state with { Reason = ActivityBadgeTracker.EncodeParty(state.Reason, HostingSessionId) };
        lastHostState = state;
        CurrentRoomState = state;
        currentRoomStateReceivedUtc = DateTime.UtcNow;

        // Position is sampled continuously for local playback, but a network
        // update once a second is enough for viewer sync. Send media, pause,
        // screen and room changes immediately.
        var now = DateTime.UtcNow;
        if (previousHostState is not null &&
            now < nextHostStateHeartbeatUtc &&
            previousHostState with { PositionSeconds = state.PositionSeconds } == state)
        {
            return Task.CompletedTask;
        }

        nextHostStateHeartbeatUtc = now.AddSeconds(1);
        return SendAsync(state);
    }

    //
    // Republishes only the editable room metadata while preserving the
    // current URL, timestamp, paused state, media details and screen
    // transform from the most recently published host state.
    //
    // This updates the existing server Room and does not create a new
    // room, clear the queue or disconnect its roster.
    //
    internal Task PublishRoomDetailsAsync()
    {
        //
        // Do not allow a pending room-details update to recreate a room
        // after its host has begun leaving.
        //
        if (leavingRoom ||
            Mode != StreamMode.Hosting)
        {
            return Task.CompletedTask;
        }

        var currentState =
            lastHostState ??
            CurrentRoomState;

        if (currentState is null)
        {
            return Task.CompletedTask;
        }

        lastHostState =
            currentState with
            {
                DisplayName = displayNameProvider(),
                Type =
                    SignalType.StreamState,

                HostId =
                    myAccountId,

                IsPrivate =
                    IsPrivate,

                Description =
                    RoomDescription ?? "",

                Location =
                    RoomLocation ?? "",

                Kind =
                    RoomKind,

                //
                // An empty password explicitly clears the server's old
                // password hash when changing away from Locked.
                //
                Password =
                    RoomKind ==
                    RoomKind.Locked
                        ? RoomPassword
                        : string.Empty,
            };

        CurrentRoomState =
            lastHostState;

        return SendAsync(
            lastHostState);
    }

    internal Task JoinAsync(
        string hostId,
        string? password = null,bool capacityRetry=false)
    {
        lock(lifecycleGate)
        {
        if(capacityRetry && automaticWatchPartyJoinsSuppressed) return Task.CompletedTask;
        if(WatchPartyJoinBlockReason is not null) return Task.CompletedTask;
        if(!capacityRetry)CancelCapacityRetry();
        Interlocked.Increment(ref capacityJoinVersion);capacityAuthorizedVersion=-1;
        ReleaseDutyScreen();
        // Only idle clients can begin a join. Keep the existing role and playback untouched on rejection.
        HostId =
            hostId;

        pendingJoinHost =
            hostId;

        pendingJoinPassword =
            string.IsNullOrWhiteSpace(
                password)
                ? null
                : password.Trim();

        lastHostState =
            null;

        //
        // Prevent the previous room's details appearing briefly while
        // waiting for the newly joined host's first state message.
        //
        CurrentRoomState =
            null;

        RoomDescription =
            null;

        RoomLocation =
            null;

        RoomKind =
            RoomKind.Public;

        RoomPassword =
            null;

        IsPrivate =
            false;

        return SendAsync(
            new StreamControl
            {
                Type =
                    SignalType.StreamJoin,

                HostId =
                    hostId,

                Password =
                    pendingJoinPassword,
            });
        }
    }

    // targetUserId comes straight from Roster (ParticipantInfo.UserId) - the host already has real
    // UserIds for everyone currently watching, no name lookup needed like JoinAsync's does.
    internal async Task<bool> TransferHostAsync(string targetUserId, StreamControl state)
    {
        if (Mode != StreamMode.Hosting || hostPlaybackAdoptionPending ||
            DateTime.UtcNow.Ticks < Interlocked.Read(ref hostTransferUntilTicks) ||
            !Roster.Any(member => member.UserId == targetUserId) || targetUserId == myAccountId) return false;
        var transferConnection = ConnectionGeneration;
        var transferAccount = myAccountId;
        // Freeze ordinary publication until the acknowledgement, with a timeout
        // if the selected viewer disconnects before the server processes it.
        Interlocked.Exchange(ref hostTransferUntilTicks, DateTime.UtcNow.AddSeconds(10).Ticks);
        state = state with { Type = SignalType.StreamState, HostId = myAccountId, DisplayName = displayNameProvider() };
        CurrentRoomState = state;
        currentRoomStateReceivedUtc = DateTime.UtcNow;
        lastHostState = state;
        await SendAsync(state, allowDuringHostTransfer: true).ConfigureAwait(false);
        if (Mode == StreamMode.Hosting && !leavingRoom && transferConnection == ConnectionGeneration &&
            transferAccount == myAccountId && Roster.Any(member => member.UserId == targetUserId))
        {
            await SendAsync(new StreamControl { Type = SignalType.StreamTransferHost, HostId = targetUserId }).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    internal Task SendReactionAsync(
        string glyph,
        string? accountDisplayName,
        string? characterName,
        string? worldName,
        bool showInChat) =>
        SendAsync(
            new StreamControl
            {
                Type = SignalType.StreamReaction,
                Reaction = glyph,

                // Reaction-specific identity payload. The server already relays these existing
                // StreamControl fields unchanged, so no server update is required.
                Reason = string.IsNullOrWhiteSpace(accountDisplayName)
                    ? null
                    : accountDisplayName.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(characterName)
                    ? null
                    : characterName.Trim(),
                Location = string.IsNullOrWhiteSpace(worldName)
                    ? null
                    : worldName.Trim(),

                // Reactions are relayed as an existing StreamControl object.
                // Reuse its privacy flag so this preference works with the
                // current server: updated receivers interpret true as TV-only.
                IsPrivate = showInChat
                    ? null
                    : true,
            });

    internal Task SendWhiteboardCommandAsync(string command)
    {
        if (Mode != StreamMode.Hosting || string.IsNullOrWhiteSpace(command))
        {
            return Task.CompletedTask;
        }

        return SendAsync(
            new StreamControl
            {
                Type = SignalType.StreamReaction,
                Reaction = WhiteboardCommandPrefix + command,

                // Keep the transport envelope out of reaction chat on clients that know the
                // existing TV-only flag but do not yet understand whiteboard commands.
                IsPrivate = true,
            });
    }

    internal Task SendWhiteboardContributionAsync(string command)
    {
        if (Mode != StreamMode.Viewing || string.IsNullOrWhiteSpace(command))
        {
            return Task.CompletedTask;
        }

        return SendAsync(
            new StreamControl
            {
                Type = SignalType.StreamReaction,
                Reaction = WhiteboardCommandPrefix + command,
                IsPrivate = true,
            });
    }

    internal Task SendPictionaryCommandAsync(string command)
    {
        if (Mode != StreamMode.Hosting || string.IsNullOrWhiteSpace(command))
        {
            return Task.CompletedTask;
        }

        return SendAsync(new StreamControl
        {
            Type = SignalType.StreamReaction,
            Reaction = PictionaryCommandPrefix + command,
            IsPrivate = true,
        });
    }

    internal Task SendPictionaryContributionAsync(string command)
    {
        if (Mode != StreamMode.Viewing || string.IsNullOrWhiteSpace(command))
        {
            return Task.CompletedTask;
        }

        return SendAsync(new StreamControl
        {
            Type = SignalType.StreamReaction,
            Reaction = PictionaryCommandPrefix + command,
            IsPrivate = true,
        });
    }

    internal Task SendPictionaryTeamsCommandAsync(string command, string? targetUserId = null)
    {
        if (Mode != StreamMode.Hosting || string.IsNullOrWhiteSpace(command))
            return Task.CompletedTask;
        return SendAsync(new StreamControl {
            Type = targetUserId is null ? SignalType.StreamReaction : SignalType.StreamPictionaryTeamsPrivate,
            Reaction = PictionaryTeamsCommandPrefix + command, TargetUserId = targetUserId, IsPrivate = true });
    }

    internal Task SendPictionaryTeamsContributionAsync(string command)
    {
        if (Mode != StreamMode.Viewing || string.IsNullOrWhiteSpace(command))
            return Task.CompletedTask;
        return SendAsync(new StreamControl { Type = SignalType.StreamReaction,
            Reaction = PictionaryTeamsCommandPrefix + command, IsPrivate = true });
    }

    internal Task SendQuizCommandAsync(string command)
    {
        if (Mode != StreamMode.Hosting || string.IsNullOrWhiteSpace(command))
            return Task.CompletedTask;

        return SendAsync(new StreamControl
        {
            Type = SignalType.StreamReaction,
            Reaction = QuizCommandPrefix + command,
            IsPrivate = true,
        });
    }

    internal Task SendQuizContributionAsync(string command)
    {
        if (Mode != StreamMode.Viewing || string.IsNullOrWhiteSpace(command))
            return Task.CompletedTask;

        return SendAsync(new StreamControl
        {
            Type = SignalType.StreamReaction,
            Reaction = QuizCommandPrefix + command,
            IsPrivate = true,
        });
    }

    internal Task SendRouletteCommandAsync(string command)
    {
        if (Mode != StreamMode.Hosting || string.IsNullOrWhiteSpace(command))
            return Task.CompletedTask;

        return SendAsync(new StreamControl
        {
            Type = SignalType.StreamReaction,
            Reaction = RouletteCommandPrefix + command,
            IsPrivate = true,
        });
    }

    internal Task SendRouletteContributionAsync(string command)
    {
        if (Mode != StreamMode.Viewing || string.IsNullOrWhiteSpace(command))
            return Task.CompletedTask;

        return SendAsync(new StreamControl
        {
            Type = SignalType.StreamReaction,
            Reaction = RouletteCommandPrefix + command,
            IsPrivate = true,
        });
    }

    internal Task SendChocoRaceCommandAsync(string command)
    {
        if (Mode != StreamMode.Hosting || string.IsNullOrWhiteSpace(command))
            return Task.CompletedTask;
        return SendAsync(new StreamControl
        {
            Type = SignalType.StreamReaction,
            Reaction = ChocoRaceCommandPrefix + command,
            IsPrivate = true,
        });
    }

    internal Task SendChocoRaceContributionAsync(string command)
    {
        if (Mode != StreamMode.Viewing || string.IsNullOrWhiteSpace(command))
            return Task.CompletedTask;
        return SendAsync(new StreamControl
        {
            Type = SignalType.StreamReaction,
            Reaction = ChocoRaceCommandPrefix + command,
            IsPrivate = true,
        });
    }

    internal Task SendPokerCommandAsync(string command, string? targetUserId = null)
    {
        if (Mode != StreamMode.Hosting || string.IsNullOrWhiteSpace(command))
            return Task.CompletedTask;
        return SendAsync(new StreamControl
        {
            Type = targetUserId is null ? SignalType.StreamReaction : SignalType.StreamPokerPrivate,
            Reaction = PokerCommandPrefix + command,
            TargetUserId = targetUserId,
            IsPrivate = true,
        });
    }

    internal Task SendPokerContributionAsync(string command)
    {
        if (Mode != StreamMode.Viewing || string.IsNullOrWhiteSpace(command))
            return Task.CompletedTask;
        return SendAsync(new StreamControl
        {
            Type = SignalType.StreamReaction,
            Reaction = PokerCommandPrefix + command,
            IsPrivate = true,
        });
    }

    internal Task SendBingoCommandAsync(string command)
    {
        if (Mode != StreamMode.Hosting || string.IsNullOrWhiteSpace(command))
            return Task.CompletedTask;
        return SendAsync(new StreamControl
        {
            Type = SignalType.StreamReaction,
            Reaction = BingoCommandPrefix + command,
            IsPrivate = true,
        });
    }

    internal Task SendBingoContributionAsync(string command)
    {
        if (Mode != StreamMode.Viewing || string.IsNullOrWhiteSpace(command))
            return Task.CompletedTask;
        return SendAsync(new StreamControl
        {
            Type = SignalType.StreamReaction,
            Reaction = BingoCommandPrefix + command,
            IsPrivate = true,
        });
    }

    internal Task SendSkyRailCommandAsync(string command)
    {
        if (Mode != StreamMode.Hosting || string.IsNullOrWhiteSpace(command))
            return Task.CompletedTask;
        return SendAsync(new StreamControl
        {
            Type = SignalType.StreamReaction,
            Reaction = SkyRailCommandPrefix + command,
            IsPrivate = true,
        });
    }

    internal Task SendSkyRailContributionAsync(string command)
    {
        if (Mode != StreamMode.Viewing || string.IsNullOrWhiteSpace(command))
            return Task.CompletedTask;
        return SendAsync(new StreamControl
        {
            Type = SignalType.StreamReaction,
            Reaction = SkyRailCommandPrefix + command,
            IsPrivate = true,
        });
    }

    internal Task SendLuckyWheelCommandAsync(string command)
    {
        if (Mode != StreamMode.Hosting || string.IsNullOrWhiteSpace(command)) return Task.CompletedTask;
        return SendAsync(new StreamControl { Type = SignalType.StreamReaction,
            Reaction = LuckyWheelCommandPrefix + command, IsPrivate = true });
    }

    internal Task SendSlotMachineCommandAsync(string command)
    {
        if (Mode != StreamMode.Hosting || string.IsNullOrWhiteSpace(command))
            return Task.CompletedTask;
        return SendAsync(new StreamControl
        {
            Type = SignalType.StreamReaction,
            Reaction = SlotMachineCommandPrefix + command,
            IsPrivate = true,
        });
    }

    internal Task SendSlotMachineContributionAsync(string command)
    {
        if (Mode != StreamMode.Viewing || string.IsNullOrWhiteSpace(command))
            return Task.CompletedTask;
        return SendAsync(new StreamControl
        {
            Type = SignalType.StreamReaction,
            Reaction = SlotMachineCommandPrefix + command,
            IsPrivate = true,
        });
    }

    internal Task SendChatAsync(string text) =>
        SendAsync(new StreamControl
        {
            Type = SignalType.StreamChat,
            ChatText = text
        });






    internal Task SendRichChatAsync(string text, Guid? replyToId)
    {
        // StreamChat is capped at 280 characters by the current server. The
        // final field is raw text, so Unicode and emoji use the same visible
        // character budget and a vertical bar in the body remains literal.
        var id = Guid.NewGuid();
        var header = RichChatPrefix + id.ToString("N") + "|" +
                     (replyToId?.ToString("N") ?? string.Empty) + "|";
        var body = text.Trim();
        if (body.Length > 280 - header.Length)
            body = body[..(280 - header.Length)];
        return SendChatAsync(header + body);
    }


    internal Task SendChatMessageReactionAsync(Guid messageId, int emoji) =>
        SendChatAsync(ChatReactionPrefix + messageId.ToString("N") + "|" + emoji);

    internal Task SendChatMessageDeleteAsync(Guid messageId) =>
        SendChatAsync(ChatDeletePrefix + messageId.ToString("N"));

    internal async Task SendNextQueueSnapshotAsync(PartyNextQueueSnapshot snapshot)
    {
        // Validate before JSON/base64 allocation, including the metadata embedded
        // in the legacy chat chunks. Never truncate a URL or protocol payload.
        if (snapshot.Count < 0 || !RealtimeOutgoingPolicy.Validate(new StreamControl
            { MediaTitle = snapshot.Title, SubtitleName = snapshot.Source, MediaThumbnailUrl = snapshot.ThumbnailUrl }, out _))
        {
            WarnOutgoingLimit("oversized next-queue metadata");
            return;
        }
        using var admission = outgoingPolicy.TryAcquireBatch();
        if (admission is null) { WarnOutgoingLimit("next-queue backlog limit"); return; }
        CancellationToken token;
        int generation;
        lock (lifecycleGate)
        {
            if (disposed) return;
            activeSends++;
            sendsDrained.Reset();
            token = lifetime.Token;
            generation = connectionGeneration;
        }
        try
        {
            await nextQueueBatches.RunAsync(async () =>
            {
                const int chunkSize = 180;
                var encoded = Convert.ToBase64String(RealtimeSocketSafety.Serialize(snapshot));
                var chunkCount = Math.Max(1, (encoded.Length + chunkSize - 1) / chunkSize);
                if (chunkCount > 32)
                {
                    {}
                    return;
                }
                var messageId = Guid.NewGuid().ToString("N");

                for (var index = 0; index < chunkCount; index++)
                {
                    token.ThrowIfCancellationRequested();
                    if (generation != Volatile.Read(ref connectionGeneration) || Mode != StreamMode.Hosting) return;
                    var offset = index * chunkSize;
                    var chunk = encoded.Substring(offset, Math.Min(chunkSize, encoded.Length - offset));
                    await SendAsync(new StreamControl
                    {
                        Type = SignalType.StreamChat,
                        ChatText = $"[[AC_NEXT_QUEUE_1]]{messageId}|{index}|{chunkCount}|{chunk}",
                    }, expectedGeneration: generation).ConfigureAwait(false);
                }
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            lock (lifecycleGate)
            {
                activeSends--;
                if (activeSends == 0) sendsDrained.Set();
            }
        }
    }

    private async Task MonitorSessionAsync(
        CharacterSession connectedSession, int generation, RealtimeAccountLease lease,
        CancellationToken token)
    {
        var started = Environment.TickCount64;
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            lease.Refresh();
            if (Volatile.Read(ref confirmedGeneration) != generation &&
                Environment.TickCount64 - started >= ConnectTimeout.TotalMilliseconds)
            {
                SetConnectionState(RealtimeConnectionState.ServerUnavailable);
                {}
                return;
            }
            var current = sessionProvider();
            if (current is null ||
                !string.Equals(current.AccountId, connectedSession.AccountId, StringComparison.Ordinal) ||
                !string.Equals(current.Token, connectedSession.Token, StringComparison.Ordinal))
            {
                return;
            }
        }
    }

    private static bool IsAuthenticationRejected(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException
                {
                    StatusCode: System.Net.HttpStatusCode.Unauthorized or
                                System.Net.HttpStatusCode.Forbidden,
                })
            {
                return true;
            }
        }

        return false;
    }

    private void SetConnectionState(RealtimeConnectionState state) =>
        connectionState = state;

    private void ClearRoomForAuthenticationLoss()
    {
        ClearRoomRecovery();
        CancelCapacityRetry();ReleaseCapacitySeat();
        roomCloseReconnect.Reset();
        ReleaseDutyScreen();
        if (Mode == StreamMode.None && string.IsNullOrEmpty(pendingJoinHost) && RecoveryOffer is null)
        {
            return;
        }

        Mode = StreamMode.None;
        HostId = null;
        Roster = [];
        lastHostState = null;
        pendingJoinHost = null;
        pendingJoinPassword = null;
        CurrentRoomState = null;
        OnEnded?.Invoke();
    }

    private static async Task DelaySafelyAsync(
        TimeSpan delay,
        CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }




    internal async Task LeaveAsync()
    {
        ClearRoomRecovery();
        hostIdentity.Reset();
        Volatile.Write(ref moderationAdmission, null);
        partyModeration.Watch(null, RemoveModeratedViewer);
        if(Mode==StreamMode.Hosting&&sessionProvider() is { } closingSession)
            _=RoomCapacityClient.Shared.Close(closingSession.Token,closingSession.AccountId);
        Volatile.Write(ref capacityDisplay,null);
        CancelCapacityRetry();ReleaseCapacitySeat();
        dutyScreenSnapshot = null;
        inheritedHostTransferScreen = null;
        IncomingHostTransfers.Clear();
        hostPlaybackAdoptionPending = false;
        Interlocked.Exchange(ref hostTransferUntilTicks, 0);
        if (Mode == StreamMode.None && string.IsNullOrEmpty(pendingJoinHost))
        {
            return;
        }

        //
        // The currently deployed server only guarantees room teardown when
        // the host's socket disconnects. Remember whether this client was
        // hosting before clearing its local mode.
        //
        var wasHosting =
            Mode ==
            StreamMode.Hosting;

        if (wasHosting && IsConnected && ConnectionState == RealtimeConnectionState.Connected)
            roomCloseReconnect.Begin(Volatile.Read(ref connectionGeneration));

        //
        // For viewers this identifies the room being left. A host normally
        // has no HostId because the server identifies their room from their
        // authenticated account.
        //
        var leavingHostId =
            HostId;

        //
        // Prevent any periodic state publication from recreating the room
        // while departure is in progress.
        //
        leavingRoom =
            true;

        Mode =
            StreamMode.None;

        lastHostState =
            null;

        CurrentRoomState =
            null;

        pendingJoinHost =
            null;

        pendingJoinPassword =
            null;

        try
        {
            //
            // Keep sending the explicit leave message. This supports the
            // updated server handler once that version is deployed, and is
            // still the normal departure path for viewers.
            //
            await SendAsync(
                    new StreamControl
                    {
                        Type =
                            SignalType.StreamLeave,

                        HostId =
                            leavingHostId,
                    })
                .ConfigureAwait(false);

            //
            // Temporary compatibility behavior for the currently deployed
            // server: disconnect a departing host's shared /rt socket so the
            // server's existing connection-finally cleanup closes the room
            // and broadcasts stream.ended to every viewer.
            //
            // RunAsync will establish a fresh connection automatically.
            //
            if (wasHosting)
            {
                var currentSocket =
                    socket;

                if (currentSocket is
                    {
                        State:
                            WebSocketState.Open
                    })
                {
                    try
                    {
                        if (!await RealtimeSocketSafety.CloseOutputAsync(currentSocket, sendLock,
                                "Watch Party host left", RealtimeSocketSafety.CloseTimeout).ConfigureAwait(false))
                            {}
                    }
                    catch (Exception)
                    {
                        AepLog.Warning(
                            "[Stream] Graceful host-leave disconnect failed.");

                        //
                        // Abort is the fallback that guarantees the receive
                        // loop exits and the server observes disconnection.
                        //
                        try
                        {
                            currentSocket.Abort();
                        }
                        catch (Exception)
                        {
                            AepLog.Warning(
                                "[Stream] Failed to abort host connection.");
                        }
                    }
                }
            }
        }
        finally
        {
            HostId =
                null;

            Roster =
                [];

            RoomDescription =
                null;

            RoomLocation =
                null;

            RoomKind =
                RoomKind.Public;

            RoomPassword =
                null;

            IsPrivate =
                false;

            IncomingChat.Clear();
            IncomingChatMessageReactions.Clear();
            IncomingChatMessageDeletes.Clear();
            IncomingSkyRailCommands.Clear();
            IncomingSlotMachineCommands.Clear();
            IncomingLuckyWheelCommands.Clear();
            IncomingPokerCommands.Clear();

            IncomingRosterSnapshots.Clear();

            IncomingNextQueueSnapshots.Clear();
            incomingNextQueueChunks.Clear();

            leavingRoom =
                false;
        }
    }
    private async Task FlushPendingAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Mode == StreamMode.Hosting && lastHostState is { } hostState)
        {
            await SendAsync(hostState with { HostId = myAccountId }).ConfigureAwait(false);
            return;
        }

        if (pendingJoinHost is { Length: > 0 } host)
        {
            await SendAsync(new StreamControl
            {
                Type = SignalType.StreamJoin,
                HostId = host,
                Password = pendingJoinPassword,
            }).ConfigureAwait(false);
        }
    }

    internal Task SendArcadeSnapshotAsync(string prefix, string command, string boundary)
    {
        if (Mode != StreamMode.Hosting || !command.StartsWith("S|", StringComparison.Ordinal))
            return Task.CompletedTask;
        return SendAsync(new StreamControl
        {
            Type = SignalType.StreamReaction,
            Reaction = prefix + command,
            IsPrivate = true,
        }, snapshotKey: $"{connectionGeneration}|{HostId}|{prefix}|{boundary}");
    }

    private void WarnOutgoingLimit(string reason)
    {
        if (outgoingPolicy.ShouldWarn())
            AepLog.Warning("[Realtime] Outgoing message skipped: [redacted]. Further safeguard warnings are limited to one per 10 seconds.");
    }

    private async Task SendAsync(StreamControl message, bool allowDuringHostTransfer = false, string? snapshotKey = null, int? expectedGeneration = null)
    {
        if (message.Type == SignalType.StreamState)
            message = message with { HostingSessionId = HostingSessionId };
        // Reject overload immediately rather than allocating an unbounded number
        // of tasks waiting for the serialized socket sender.
        if (socket is not { State: WebSocketState.Open }) return;
        if(message.Type==SignalType.StreamJoin&&!await PrepareCapacityJoin(message).ConfigureAwait(false))return;
        if(message.Type==SignalType.StreamJoin&&!await PrepareLocalViewerJoinAsync(message).ConfigureAwait(false))return;
        var permittedJoinVersion=Volatile.Read(ref capacityJoinVersion);
        if(message.Type==SignalType.StreamJoin)
        {
            if(!ApplyCapacityJoinTarget(ref message,permittedJoinVersion))return;
        }
        using var admission = outgoingPolicy.TryAcquire(message, out var rejection, handoverState: allowDuringHostTransfer);
        if (admission is null) { WarnOutgoingLimit(rejection); return; }
        PendingSnapshotCoalescer.Ticket? snapshotTicket;
        lock (lifecycleGate)
        {
            if (disposed)
            {
                return;
            }

            activeSends++;
            sendsDrained.Reset();
            if (message.Type == SignalType.StreamState && !allowDuringHostTransfer)
                snapshotKey = $"{connectionGeneration}|host-state";
            snapshotTicket = pendingSnapshots.Register(snapshotKey);
        }

        var enteredSendLock =
            false;

        try
        {
            var ws = socket;
            if (ws is not { State: WebSocketState.Open }) return;

            await sendLock.WaitAsync(lifetime.Token).ConfigureAwait(false);
            enteredSendLock = true;

            bool StillValid() => ReferenceEquals(socket, ws) && ws.State == WebSocketState.Open &&
                (message.Type is not (SignalType.StreamResume or SignalType.StreamRecoveryDecline) ||
                    (!recoveryCheckPending && RecoveryOffer?.RecoveryId == message.RecoveryId)) &&
                (message.Type != SignalType.StreamState || !RecoveryBlocked) &&
                (message.Type != SignalType.StreamJoin || (!leavingRoom && Mode != StreamMode.Hosting &&
                    string.Equals(message.HostId,pendingJoinHost,StringComparison.Ordinal)&&
                    capacityAuthorizedVersion==permittedJoinVersion&&permittedJoinVersion==Volatile.Read(ref capacityJoinVersion)&&capacityLease is not null)) &&
                (expectedGeneration is null || (expectedGeneration == Volatile.Read(ref connectionGeneration) && Mode == StreamMode.Hosting && !leavingRoom)) &&
                (snapshotKey is null || (Mode == StreamMode.Hosting && !leavingRoom)) &&
                (message.Type != SignalType.StreamState || (Mode == StreamMode.Hosting && !leavingRoom && !hostPlaybackAdoptionPending &&
                    (allowDuringHostTransfer || DateTime.UtcNow.Ticks >= Interlocked.Read(ref hostTransferUntilTicks))));

            // A disconnect/close may have happened while this send was waiting.
            if (!StillValid()) return;
            if (!pendingSnapshots.TryBegin(snapshotTicket)) return;
            // A queued public game snapshot belongs to the host who queued it.
            if (snapshotKey is not null && (Mode != StreamMode.Hosting || leavingRoom)) return;

            // Recheck after acquiring the send lock: a state queued by the old
            // host must never recreate a room after their role has changed.
            if (message.Type == SignalType.StreamState &&
                (Mode != StreamMode.Hosting || leavingRoom || hostPlaybackAdoptionPending ||
                 (!allowDuringHostTransfer && DateTime.UtcNow.Ticks < Interlocked.Read(ref hostTransferUntilTicks)))) return;

            var json = RealtimeSocketSafety.Serialize(message);
            if (message.Type == SignalType.StreamJoin)
            {
                // Keep a room switch atomic relative to other outgoing messages.
                // The server removes its previous membership before handling
                // this join, including when two joins were queued quickly.
                var leave = RealtimeSocketSafety.Serialize(new StreamControl { Type = SignalType.StreamLeave });
                await sendPacer.SendAsync(ws, leave, lifetime.Token, StillValid).ConfigureAwait(false);
            }
            await sendPacer.SendAsync(ws, json, lifetime.Token, StillValid).ConfigureAwait(false);
        }
        catch (RealtimeMessageTooLargeException)
        {
            AepLog.Warning("[Realtime] Rejected oversized outgoing [redacted] message.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Normal during plugin shutdown.
        }
        catch (Exception)
        {
            {}
        }
        finally
        {
            if (enteredSendLock)
            {
                sendLock.Release();
            }

            lock (lifecycleGate)
            {
                activeSends--;
                if (activeSends == 0)
                {
                    sendsDrained.Set();
                }
            }
        }
    }

    private static Uri BuildUri(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "wss://" + trimmed["https://".Length..];
        }
        else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "ws://" + trimmed["http://".Length..];
        }

        return new Uri(trimmed + "/rt");
    }

    public void Dispose()
    {
        lock (lifecycleGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        lifetime.Cancel();
        partyModeration.Dispose();
        if(Mode==StreamMode.Hosting&&sessionProvider() is { } closingSession)
            _=RoomCapacityClient.Shared.Close(closingSession.Token,closingSession.AccountId);
        CancelCapacityRetry();ReleaseCapacitySeat();

        try { socket?.Abort(); } catch { }
        try { socket?.Dispose(); } catch { }

        try
        {
            if (runTask is { IsCompleted: false } &&
                !runTask.Wait(TimeSpan.FromSeconds(3)))
            {
                AepLog.Warning(
                    "[Realtime] Connection worker did not stop within 3 seconds.");
            }
        }
        catch (Exception)
        {
            
        }

        try { moderationTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }

        var sendsFinished =
            sendsDrained.Wait(TimeSpan.FromSeconds(3));

        if (!sendsFinished)
        {
            // SemaphoreSlim has no native handle unless AvailableWaitHandle is
            // requested. Leaving it for GC is safer than disposing it beneath
            // a send which is still unwinding.
            {}
        }

        runTask = null;
        lifetime.Dispose();

        if (sendsFinished)
        {
            sendLock.Dispose();
            sendsDrained.Dispose();
        }
    }
}

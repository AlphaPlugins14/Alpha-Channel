using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Video;
using System.Text.Json;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private sealed record AdminUsageEvent(string Id, string Type, string ContentType, double Duration, string RoomKind, string? SessionId, double TotalDuration = 0);
    private sealed record AdminUsageState(List<AdminUsageEvent> Events, string? SessionId, DateTime Started, DateTime SegmentStarted, string ContentType, string RoomKind);
    private readonly object adminUsageLock = new();
    private readonly Dictionary<string, AdminUsageState> adminUsageStates = new(StringComparer.OrdinalIgnoreCase);
    private string? adminUsageAccount;
    private bool adminUsageSending;
    private DateTime nextAdminUsageSample, nextAdminUsageSend;
    private StreamMode adminUsageMode;

    private string AdminUsagePath(string account) => Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "AdminUsage", Guid.Parse(account).ToString("D") + ".json");
    private AdminUsageState AdminUsageFor(string account)
    {
        if (adminUsageStates.TryGetValue(account, out var state)) return state;
        try { state = JsonSerializer.Deserialize<AdminUsageState>(File.ReadAllText(AdminUsagePath(account)), AlphaChatClient.JsonOptions); }
        catch (Exception) { }
        state ??= new([], null, default, default, "Waiting", "Public");
        adminUsageStates[account] = state;
        return state;
    }
    private void SaveAdminUsage(string account, AdminUsageState state)
    {
        try
        {
            var path = AdminUsagePath(account); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state, AlphaChatClient.JsonOptions)); File.Move(path + ".tmp", path, true);
        }
        catch (Exception) { }
    }
    private void RecordAdminRegistration(CharacterSession session, string? eventId = null)
    {
        lock (adminUsageLock)
        {
            var state = AdminUsageFor(session.AccountId);
            if (state.Events.Count < 5000 && (eventId is null || !state.Events.Any(e => e.Id == eventId)))
                state.Events.Add(new(eventId ?? Guid.NewGuid().ToString("D"), "Registration", "", 0, "", null));
            SaveAdminUsage(session.AccountId, state);
        }
    }
    private string AdminBroadcastContentType()
    {
        var current = queue.Current;
        var room = stream.CurrentRoomState;
        if (room?.Url is { Length: > 0 } url && (current is null || !AudioVisualizerSelection.IsSameMedia(current.Url,url)))
            current = CreatePartyMediaDisplayEntry(url, room.MediaTitle ?? "", room.MediaThumbnailUrl);
        if (current is null) return "Waiting";
        if (current.Url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(current.Url)) return "Local video";
        if (current.Url.Contains("youtu", StringComparison.OrdinalIgnoreCase)) return "YouTube";
        if (video.IsAudioOnly) return "DJ / Radio";
        var source = current.Source;
        if (source.Equals("Local Video",StringComparison.OrdinalIgnoreCase)) return "Local video";
        if (source is "Canvas" or "Arcade") return "Activities";
        if (source == "Broadcast" && (room?.MediaTitle?.Contains("DJ",StringComparison.OrdinalIgnoreCase) ?? false)) return "DJ / Radio";
        if (source.Contains("game", StringComparison.OrdinalIgnoreCase)) return "Games";
        if (source.Contains("browser", StringComparison.OrdinalIgnoreCase)) return "Browser";
        if (source.Contains("live", StringComparison.OrdinalIgnoreCase) || source.Contains("twitch", StringComparison.OrdinalIgnoreCase)) return "Live stream";
        return "Video / Other";
    }
    private void SaveFinalAdminUsage()
    {
        lock (adminUsageLock) {
            if (adminUsageAccount is null) return;
            var state=AdminUsageFor(adminUsageAccount);
            if (state.SessionId is null) return;
            var now=DateTime.UtcNow;
            state.Events.Add(new(Guid.NewGuid().ToString("D"),"Broadcast ended",state.ContentType,Math.Max(0,(now-state.SegmentStarted).TotalSeconds),state.RoomKind,state.SessionId,Math.Max(0,(now-state.Started).TotalSeconds)));
            state=state with { SessionId=null };adminUsageStates[adminUsageAccount]=state;SaveAdminUsage(adminUsageAccount,state);
        }
    }
    internal void TickAdminUsage()
    {
        if (Volatile.Read(ref disposeStarted) != 0 || DateTime.UtcNow < nextAdminUsageSample) return;
        var now = DateTime.UtcNow; nextAdminUsageSample = now.AddSeconds(1);
        var session = Plugin.ClientState.IsLoggedIn ? CurrentSession : null;
        lock (adminUsageLock)
        {
            if (adminUsageAccount != session?.AccountId)
            {
                // A restored unfinished broadcast stays incomplete; do not manufacture an end timestamp.
                adminUsageAccount = session?.AccountId; adminUsageMode = StreamMode.None;
                if (adminUsageAccount is not null)
                { var old = AdminUsageFor(adminUsageAccount); adminUsageStates[adminUsageAccount] = old with { SessionId = null }; SaveAdminUsage(adminUsageAccount, adminUsageStates[adminUsageAccount]); }
            }
            if (session is null) return;
            var state = AdminUsageFor(session.AccountId);
            var hosting = stream.Mode == StreamMode.Hosting;
            var type = hosting ? AdminBroadcastContentType() : state.ContentType;
            var changed = false;
            if (hosting && state.SessionId is null)
            {
                state = state with { SessionId = Guid.NewGuid().ToString("D"), Started = now, SegmentStarted = now, ContentType = type, RoomKind = stream.RoomKind.ToString() };
                state.Events.Add(new(Guid.NewGuid().ToString("D"), "Broadcast started", type, 0, state.RoomKind, state.SessionId)); changed = true;
            }
            else if (state.SessionId is not null && (!hosting || state.ContentType != type))
            {
                state.Events.Add(new(Guid.NewGuid().ToString("D"), hosting ? "Content changed" : "Broadcast ended", state.ContentType,
                    Math.Max(0, (now - state.SegmentStarted).TotalSeconds), state.RoomKind, state.SessionId, Math.Max(0, (now - state.Started).TotalSeconds)));
                state = state with { SessionId = hosting ? state.SessionId : null, ContentType = type, SegmentStarted = now }; changed = true;
            }
            if (stream.Mode == StreamMode.Viewing && adminUsageMode != StreamMode.Viewing)
            { state.Events.Add(new(Guid.NewGuid().ToString("D"), "Party joined", "", 0, "", null)); changed = true; }
            adminUsageMode = stream.Mode;
            if (state.Events.Count > 5000) state.Events.RemoveRange(0, state.Events.Count - 5000);
            adminUsageStates[session.AccountId] = state;
            if (changed) SaveAdminUsage(session.AccountId, state);
            if (state.Events.Count == 0 || adminUsageSending || now < nextAdminUsageSend) return;
            var batch = state.Events.Take(20).ToArray();
            adminUsageSending = true; nextAdminUsageSend = now.AddMinutes(1);
            _ = Task.Run(async () =>
            {
                var success = false;
                try { await AlphaChatAdminApi.RequestAsync(session.Token, "/telemetry", new { events = batch }); success = true; }
                catch (Exception) { }
                lock (adminUsageLock)
                {
                    var current = AdminUsageFor(session.AccountId);
                    if (success) { var ids = batch.Select(e => e.Id).ToHashSet(); current.Events.RemoveAll(e => ids.Contains(e.Id)); SaveAdminUsage(session.AccountId, current); }
                    else nextAdminUsageSend = DateTime.UtcNow.AddMinutes(5);
                    adminUsageSending = false;
                }
            });
        }
    }
}

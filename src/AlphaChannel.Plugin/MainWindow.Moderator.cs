using AlphaChannel.Plugin.Auth;
using System.Text.Json;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string? moderatorAccount, moderatorToken, moderatorError;
    private bool moderatorAllowed, moderatorChecking, moderatorBusy;
    private DateTime moderatorNextCheck, moderatorVerifiedUntil, moderatorUpdated;
    private long moderatorGeneration;
    private JsonElement moderatorData;
    private int moderatorTab, moderatorDays = 7, moderatorStatus, moderatorMinutes = 30;
    private string moderatorSearch = "", moderatorNote = "", moderatorReason = "";
    private string? moderatorSelectedReport, moderatorSelectedUser, moderatorAction, moderatorTarget;
    private long moderatorSequence;
    private GroupReport? moderatorMessageReport;
    private PublicGroupListing[] moderatorGroups = [];
    private CommunityEvent[] moderatorEvents = [];
    private CommunityEvent? moderatorEvent;
    private PublicGroupListing? moderatorGroup;
    private string moderatorEventTitle = "", moderatorEventDescription = "";
    private bool moderatorPosting, moderatorSharing, moderatorJoinLocked, moderatorFuture;
    private int moderatorSlow;
    private static readonly string[] ModeratorTabs = ["Overview", "Reports", "Users", "Chats", "Events", "Activity log"];
    private bool CanViewModeratorEvidence => ModeratorApproved
#if DEBUG
#endif
        ;
    private bool ModeratorApproved => moderatorAllowed && DateTime.UtcNow < moderatorVerifiedUntil &&
        CurrentSession?.AccountId == moderatorAccount && CurrentSession?.Token == moderatorToken;

    private void CheckModeratorAccess()
    {
        var session = CurrentSession;
        if (moderatorAccount != session?.AccountId || moderatorToken != session?.Token)
        {
            moderatorGeneration++;
            moderatorAccount = session?.AccountId; moderatorToken = session?.Token;
            moderatorAllowed = moderatorChecking = moderatorBusy = false;
            moderatorData = default; moderatorGroups = []; moderatorEvents = []; cloudGroupReports = [];
            moderatorAction = moderatorSelectedReport = moderatorSelectedUser = null;
            moderatorEvent = null; moderatorGroup = null; moderatorError = null;
            moderatorNextCheck = moderatorVerifiedUntil = default;
        }
        if (session is null || moderatorChecking || DateTime.UtcNow < moderatorNextCheck) return;
        moderatorChecking = true; moderatorNextCheck = DateTime.UtcNow.AddSeconds(60);
        var generation = moderatorGeneration;
        _ = Task.Run(async () =>
        {
            var allowed = false;
            try
            {
                var access = await AlphaChatAdminApi.RequestAsync(session.Token, "/moderator/access");
                allowed = access.GetProperty("accountId").GetString() == session.AccountId && access.GetProperty("allowed").GetBoolean();
            }
            catch { }
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (!ModeratorSessionCurrent(session, generation)) return;
                moderatorChecking = false; moderatorAllowed = allowed;
                moderatorVerifiedUntil = allowed ? DateTime.UtcNow.AddSeconds(75) : default;
                if (!allowed)
                {
                    moderatorData = default; moderatorGroups = []; moderatorEvents = []; cloudGroupReports = [];
                    moderatorAction = null; moderatorEvent = null; moderatorGroup = null;
                    foreach(var key in groupImageViews.Keys.Where(k=>k.StartsWith("report:",StringComparison.Ordinal)).ToArray()) {
                        groupImageViews[key].Dispose();groupImageViews.Remove(key);
                        if(groupImageViewer==key)groupImageViewer=null;
                    }
                }
            });
        });
    }

    private bool ModeratorSessionCurrent(CharacterSession session, long generation) => Volatile.Read(ref disposeStarted) == 0 &&
        moderatorGeneration == generation && CurrentSession?.AccountId == session.AccountId && CurrentSession.Token == session.Token;

    private void OpenModeratorPanel()
    {
        CheckModeratorAccess();
        if (!ModeratorApproved) return;
        currentPage = HomePage.Moderator; moderatorTab = 0; moderatorSearch = "";
        RefreshModeratorPanel();
    }

    private void RefreshModeratorPanel()
    {
        if (!ModeratorApproved || moderatorBusy || CurrentSession is not { } session) return;
        var days = moderatorDays; var tab = moderatorTab;
        ModeratorWork(async () =>
        {
            var data = await AlphaChatAdminApi.RequestAsync(session.Token, "/moderator/snapshot?days=" + days);
            GroupReport[]? reports = null; PublicGroupListing[]? groups = null; CommunityEvent[]? listing = null;
            string? extraError = null;
            // Load only the current tool's extra data; never poll all tools in the background.
            try
            {
                if (tab is 0 or 1) reports = await dmClient.Groups.ModeratorReportsAsync(session.Token);
                if (tab == 3) groups = await dmClient.Groups.ModeratorPublicGroupsAsync(session.Token);
                if (tab is 0 or 4)
                {
                    var now = DateTimeOffset.UtcNow;
                    listing = (await eventsApi.Sync(session.Token, now.AddDays(tab == 4 ? -30 : -1).ToUnixTimeMilliseconds(),
                        now.AddDays(60).ToUnixTimeMilliseconds(), false, false, youtubeFeedToken)).Events;
                }
            }
            catch { extraError = "Some results could not be loaded. Refresh to try again."; }
            return () =>
            {
                moderatorData = data; moderatorUpdated = DateTime.Now; moderatorError = extraError;
                if (reports is not null) cloudGroupReports = reports;
                if (groups is not null) moderatorGroups = groups;
                if (listing is not null) moderatorEvents = listing;
            };
        });
    }

    private void ModeratorWork(Func<Task<Action>> work)
    {
        if (!ModeratorApproved || moderatorBusy || CurrentSession is not { } session) return;
        var generation = moderatorGeneration;
        moderatorBusy = true; moderatorError = null;
        _ = Task.Run(async () =>
        {
            Action? apply = null; string? error = null;
            try { apply = await work(); }
            catch { error = "The action could not be completed. Check your access and refresh before trying again."; }
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (!ModeratorSessionCurrent(session, generation)) return;
                moderatorBusy = false;
                if (error is not null)
                {
                    moderatorError = error; moderatorNextCheck = default; CheckModeratorAccess();
                }
                else if (ModeratorApproved) apply?.Invoke();
            });
        });
    }

    private static string ModText(JsonElement row, string key, string fallback = "") =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : fallback;
    private static long ModNumber(JsonElement row, string key) => row.ValueKind == JsonValueKind.Object &&
        row.TryGetProperty(key, out var value) && value.TryGetInt64(out var number) ? number : 0;
    private IEnumerable<JsonElement> ModRows(string key) => moderatorData.ValueKind == JsonValueKind.Object &&
        moderatorData.TryGetProperty(key, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().ToArray() : [];
    private static string ModTime(long milliseconds) => milliseconds > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).LocalDateTime.ToString("dd MMM, HH:mm") : "—";
    private string ModName(string account) => ModRows("users").Where(u => ModText(u, "account") == account).Select(u => ModText(u, "name")).FirstOrDefault() ?? account;
    private int ModeratorOpenReports => ModRows("reports").Count(r => ModText(r, "status") is "Open" or "Under review") + cloudGroupReports.Count(r => r.Status is "Open" or "Under review");

    private void QueueModeratorAction(string action, string? account = null, long sequence = 0)
    {
        moderatorAction = action; moderatorTarget = account; moderatorSequence = sequence;
        moderatorReason = ""; moderatorMinutes = 30; moderatorFuture = false;
    }

    private void SubmitModeratorAction()
    {
        if (CurrentSession is not { } session) return;
        var action = moderatorAction; var reason = moderatorReason; var message = moderatorMessageReport; var e = moderatorEvent; var group = moderatorGroup;
        var future = moderatorFuture; var title = moderatorEventTitle; var description = moderatorEventDescription;
        var slow = moderatorSlow; var locked = moderatorJoinLocked;
        var body = new { action, accountId = moderatorTarget, sequence = moderatorSequence, reason, minutes = moderatorMinutes,
            slowSeconds = slow, posting = moderatorPosting, sharing = moderatorSharing };
        ModeratorWork(async () =>
        {
            if (action is "event-edit" or "event-remove" && e is not null)
                await eventsApi.ModeratorAction(session.Token, e, action == "event-remove", title, description, future, reason, youtubeFeedToken);
            else if (action == "group-controls" && group is not null)
                await dmClient.Groups.ModeratorPublicGroupActionAsync(session.Token, group.Id, new { slowSeconds = slow, joinLocked = locked, reason });
            else if(action=="group-delete"&&message is not null)
                await dmClient.Groups.ModeratorDeleteMessageAsync(session.Token,message.GroupId,message.MessageId,reason);
            else await AlphaChatAdminApi.RequestAsync(session.Token, "/moderator/action", body);
            return () => { moderatorAction = null; moderatorEvent = null; moderatorGroup = null; RefreshModeratorPanel(); };
        });
    }

}

using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Text.Json;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool moderatorRestrictedOnly, moderatorPastEvents, moderatorScrollToUser;
    private sealed record ModeratorReportItem(string Id, string Sender, string Name, string Preview, string Source, string Status, long At,
        AlphaChatUser? User, JsonElement Alpha, GroupReport? Group);

    private AlphaChatUser? ModeratorUser(string account) => ModRows("users").Where(u => ModText(u, "account") == account)
        .Select(u => u.GetProperty("user").Deserialize<AlphaChatUser>(AlphaChatClient.JsonOptions)).FirstOrDefault();

    private IEnumerable<ModeratorReportItem> ModeratorReportItems()
    {
        foreach (var report in ModRows("reports"))
        {
            var message = report.GetProperty("message");
            var sender = message.GetProperty("sender").Deserialize<AlphaChatUser>(AlphaChatClient.JsonOptions)!;
            yield return new(ModText(report, "sequence"), sender.AccountId, sender.DisplayName, ModText(report, "content"), "Alpha Chat",
                ModText(report, "status"), ModNumber(message, "timestamp"), sender, report, null);
        }
        foreach (var report in cloudGroupReports)
            yield return new("group:" + report.Id, report.SenderId, ModName(report.SenderId), report.MediaId is not null ? "Reported image" : report.Plaintext,
                report.Direct ? "Private message" : report.IsPublic ? "Public group" : "Private group", report.Status, report.CreatedAt * 1000,
                ModeratorUser(report.SenderId), default, report);
    }

    private void ModeratorAvatar(AlphaChatUser? user, string? name, float size) =>
        DrawAvatarChip(user?.AvatarIcon ?? "User", user?.AvatarColorHex ?? "#9966FA", size, user?.AvatarImageUrl);

    private void DrawModeratorReportRow(ModeratorReportItem report, bool compact = false)
    {
        var p = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X; var height = Ui(compact ? 64 : 78);
        if (!ImGui.IsRectVisible(new Vector2(width, height))) { ImGui.Dummy(new(width, height)); return; }
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(p, p + new Vector2(width, height), ImGui.GetColorU32(FrameBg), Ui(8));
        draw.AddRect(p, p + new Vector2(width, height), ImGui.GetColorU32(BorderSubtle), Ui(8));
        ImGui.SetCursorScreenPos(p + UiVec(10, 12)); ModeratorAvatar(report.User, report.Name, Ui(36));
        ImGui.SetCursorScreenPos(p + UiVec(58, 10));
        DrawUsername(report.Sender, FitPartyTextToWidth(report.Name, width - Ui(210)), Vector4.One);
        ImGui.SetCursorScreenPos(p + new Vector2(MathF.Max(Ui(100), width - Ui(120)), Ui(10)));
        using (UiFonts.PushRelative(.8f)) ImGui.TextColored(ModeratorStatusColour(report.Status), report.Status);
        ImGui.SetCursorScreenPos(p + UiVec(58, 32));
        using (UiFonts.PushRelative(.88f)) ImGui.TextColored(MutedText, FitPartyTextToWidth(report.Preview.Replace('\n', ' '), width - Ui(100)));
        if (!compact)
        {
            ImGui.SetCursorScreenPos(p + UiVec(58, 54));
            using (UiFonts.PushRelative(.78f)) ImGui.TextColored(MutedText, report.Source + " · " + ModTime(report.At));
        }
        ImGui.SetCursorScreenPos(p + new Vector2(width - Ui(30), (height - Ui(26)) / 2));
        if (ModeratorButton("report" + report.Id, "", FontAwesomeIcon.ChevronRight, Ui(26)))
        {
            moderatorSelectedReport = report.Id; moderatorNote = "";
            if (compact) { moderatorTab = 1; RefreshModeratorPanel(); }
        }
        ImGui.SetCursorScreenPos(p); ImGui.Dummy(new(width, height));
    }

    private void DrawModeratorReports()
    {
        if (moderatorSelectedReport is { } selected)
        {
            var report = ModeratorReportItems().FirstOrDefault(r => r.Id == selected);
            if (report is null) { moderatorSelectedReport = null; return; }
            DrawModeratorReportDetails(report); return;
        }
        ModeratorCard("reportFilters", Ui(108), () =>
        {
            ModeratorSearch("reports", "Search reports…", ref moderatorSearch);
            string[] statuses = ["Open", "Under review", "Resolved", "Dismissed", "Escalated", "All"];
            ImGui.SetNextItemWidth(Ui(170));
            if (ImGui.BeginCombo("##modStatus", statuses[moderatorStatus]))
            {
                for (var i = 0; i < statuses.Length; i++) if (ImGui.Selectable(statuses[i], moderatorStatus == i)) moderatorStatus = i;
                ImGui.EndCombo();
            }
        });
        string[] filters = ["Open", "Under review", "Resolved", "Dismissed", "Escalated", "All"];
        var reports = ModeratorReportItems().Where(r => (moderatorStatus == 5 || r.Status == filters[moderatorStatus]) &&
            (r.Preview + r.Name + r.Source).Contains(moderatorSearch, StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.At).ToArray();
        if (reports.Length == 0)
        {
            ModeratorCard("reportsEmpty", Ui(242), () =>
            { ModeratorHeading("Report queue", FontAwesomeIcon.Flag); ModeratorEmpty(FontAwesomeIcon.CheckCircle, "No reports in this view", "New reports will appear here for review."); });
            return;
        }
        foreach (var report in reports) DrawModeratorReportRow(report);
    }

    private void DrawModeratorReportDetails(ModeratorReportItem report)
    {
        if (ModeratorButton("backReports", "Back to reports", FontAwesomeIcon.ArrowLeft)) { moderatorSelectedReport = null; moderatorNote = ""; return; }
        var contentWidth = ImGui.GetContentRegionAvail().X - Ui(64);
        var quoteHeight = ModeratorWrappedHeight(report.Preview, contentWidth - Ui(32)) + Ui(30);
        var notes = report.Group?.Notes ?? (report.Group is null ? report.Alpha.GetProperty("notes").EnumerateArray().Select(n =>
            ModText(n.GetProperty("admin"), "displayName") + " · " + ModText(n, "status") + ": " + ModText(n, "text")).ToArray() : []);
        var reporters = report.Group is { } group ? new[] { "Reported by " + ModName(group.ReporterId) } : report.Alpha.GetProperty("reports").EnumerateArray().Select(r =>
            ModText(r.GetProperty("user"), "displayName") + " · " + ModText(r, "reason") + (ModText(r, "note").Length > 0 ? "\n" + ModText(r, "note") : "")).ToArray();
        var detailsHeight = Ui(472) + quoteHeight + reporters.Sum(t => ModeratorWrappedHeight(t, contentWidth) + Ui(8)) +
            notes.Sum(t => ModeratorWrappedHeight(t, contentWidth) + Ui(8)) + (report.Group?.MediaId is not null ? Ui(90) : 0);
        ModeratorCard("reportDetail", detailsHeight, () =>
        {
            ModeratorHeading(report.Group is null ? "Report #" + report.Id : "Report details", FontAwesomeIcon.FileAlt);
            ModeratorBadge(report.Status, ModeratorStatusColour(report.Status));
            ImGui.SameLine(); ImGui.TextColored(MutedText, report.Source + " · " + ModTime(report.At));
            var p = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
            ModeratorAvatar(report.User, report.Name, Ui(44));
            ImGui.SameLine(); DrawUsername(report.Sender, report.Name, Vector4.One);
            ImGui.SetCursorScreenPos(p + new Vector2(width - Ui(126), Ui(4)));
            if (ModeratorButton("reportProfile", "View profile", FontAwesomeIcon.User, Ui(126)) && CurrentSession is { } session)
                OpenProfilePage(session, report.Sender, report.Name);
            ImGui.SetCursorScreenPos(p); ImGui.Dummy(new(width, Ui(52)));
            ModeratorCard("reportedMessage", quoteHeight, () => ImGui.TextWrapped(report.Preview));
            foreach (var reporter in reporters) ImGui.TextWrapped(reporter);
            if (report.Group is { } image && image.Status is not ("Resolved" or "Dismissed") && CurrentSession is { } imageSession) DrawReportedGroupImage(imageSession, image);
            ImGui.Separator(); ModeratorHeading("Case history", FontAwesomeIcon.History);
            if (notes.Length == 0) ImGui.TextColored(MutedText, "No previous review notes.");
            foreach (var note in notes) ImGui.TextWrapped(note);
            ImGui.Spacing(); ImGui.TextColored(MutedText, "Internal note");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextMultiline("##modNote", ref moderatorNote, 500, new(-1, Ui(82)));
            ImGui.Spacing();
            var column = (ImGui.GetContentRegionAvail().X - Ui(20)) / 3;
            if (ModeratorButton("resolve", "Resolve", FontAwesomeIcon.Check, column, primary: true)) ModeratorReviewReport(report, "Resolved");
            ImGui.SameLine(); if (ModeratorButton("escalate", "Escalate", FontAwesomeIcon.ArrowUp, column)) ModeratorReviewReport(report, "Escalated");
            ImGui.SameLine(); if (ModeratorButton("reportActions", "Take action", FontAwesomeIcon.ChevronDown, column)) ImGui.OpenPopup("##modReportAction");
            if (BeginPartyPopup("##modReportAction", PartyMenuSize(6)))
            {
                if (ImGui.MenuItem("Start review")) ModeratorReviewReport(report, "Under review");
                if (ImGui.MenuItem("Dismiss report")) ModeratorReviewReport(report, "Dismissed");
                ImGui.Separator();
                if (ImGui.MenuItem("Warn user")) QueueModeratorAction("warn", report.Sender);
                if (ImGui.MenuItem("Mute user")) QueueModeratorAction("mute", report.Sender);
                if (ImGui.MenuItem("Temporarily exclude user")) QueueModeratorAction("kick", report.Sender);
                if (report.Group is null && ImGui.MenuItem("Remove message")) QueueModeratorAction("delete", sequence: ModNumber(report.Alpha, "sequence"));
                if (report.Group is { IsPublic: true } pub && ImGui.MenuItem("Remove message")) { moderatorMessageReport = pub; QueueModeratorAction("group-delete"); }
                ImGui.EndPopup();
            }
        });
    }

    private void ModeratorReviewReport(ModeratorReportItem report, string status)
    {
        if (CurrentSession is not { } session) return;
        var note = moderatorNote;
        ModeratorWork(async () =>
        {
            if (report.Group is { } group) await dmClient.Groups.ModeratorReviewAsync(session.Token, group.Id, status, note);
            else await AlphaChatAdminApi.RequestAsync(session.Token, "/moderator/action", new { action = "resolve", sequence = ModNumber(report.Alpha, "sequence"), status, note });
            return () => { moderatorSelectedReport = null; moderatorNote = ""; RefreshModeratorPanel(); };
        });
    }

    private void DrawModeratorUsers()
    {
        ModeratorCard("userFilters", Ui(111), () =>
        {
            ModeratorSearch("users", "Search users…", ref moderatorSearch);
            var selected = moderatorRestrictedOnly ? 1 : 0;
            EventSegments("modUsers", ["All users", "Restricted"], ref selected, MathF.Min(Ui(230), ImGui.GetContentRegionAvail().X));
            moderatorRestrictedOnly = selected == 1;
        });
        var users = ModRows("users").Where(u => (!moderatorRestrictedOnly || ModeratorRestrictionText(u) != "No restrictions") &&
            (ModText(u, "name") + ModText(u, "account")).Contains(moderatorSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (users.Length == 0)
            ModeratorCard("usersEmpty", Ui(226), () => ModeratorEmpty(FontAwesomeIcon.Users, "No matching users", "Try another name or change the filter."));
        else ModeratorCard("userList", MathF.Min(Ui(28 + users.Length * 74), moderatorSelectedUser is null ? MathF.Max(Ui(220), ImGui.GetContentRegionAvail().Y - Ui(12)) : Ui(280)), () =>
        {
            foreach (var user in users)
            {
                var account = ModText(user, "account"); var name = ModText(user, "name"); var p = ImGui.GetCursorScreenPos();
                var width = ImGui.GetContentRegionAvail().X; var height = Ui(62); var selected = moderatorSelectedUser == account;
                if (!ImGui.IsRectVisible(new Vector2(width, height))) { ImGui.Dummy(new(width, height)); continue; }
                ImGui.GetWindowDrawList().AddRectFilled(p, p + new Vector2(width, height), ImGui.GetColorU32(selected ? Accent with { W = .16f } : FrameBg), Ui(8));
                ImGui.SetCursorScreenPos(p + UiVec(10, 11)); ModeratorAvatar(ModeratorUser(account), name, Ui(38));
                ImGui.SetCursorScreenPos(p + UiVec(60, 10)); DrawUsername(account, FitPartyTextToWidth(name, width - Ui(170)), Vector4.One);
                ImGui.SetCursorScreenPos(p + UiVec(60, 34));
                var status = ModeratorRestrictionText(user); using (UiFonts.PushRelative(.84f)) ImGui.TextColored(status == "No restrictions" ? MutedText : ModeratorAmber, status);
                ImGui.SetCursorScreenPos(p + new Vector2(width - Ui(102), Ui(14)));
                if (ModeratorButton("manage" + account, "Manage", FontAwesomeIcon.ChevronDown, Ui(94))) { moderatorSelectedUser = account; moderatorScrollToUser = true; }
                ImGui.SetCursorScreenPos(p); ImGui.Dummy(new(width, height));
            }
        }, scroll: true);
        var selectedUser = ModRows("users").FirstOrDefault(u => ModText(u, "account") == moderatorSelectedUser);
        if (selectedUser.ValueKind != JsonValueKind.Object) return;
        var history = ModRows("audit").Where(r => ModText(r, "target") == moderatorSelectedUser).Take(5).ToArray();
        ModeratorCard("userDetails", Ui(264) + (history.Length == 0 ? Ui(64) : history.Sum(r => Ui(88) + ModeratorWrappedHeight(ModText(r, "reason"), ImGui.GetContentRegionAvail().X - Ui(40)))), () =>
        {
            var account = ModText(selectedUser, "account");
            ModeratorHeading(ModText(selectedUser, "name"), FontAwesomeIcon.UserShield);
            ModeratorBadge(ModeratorRestrictionText(selectedUser), ModeratorRestrictionText(selectedUser) == "No restrictions" ? Good : ModeratorAmber);
            var w = (ImGui.GetContentRegionAvail().X - Ui(20)) / 3;
            if (ModeratorButton("userWarn", "Warn", FontAwesomeIcon.Comment, w)) QueueModeratorAction("warn", account);
            ImGui.SameLine(); if (ModeratorButton("userMute", "Mute", FontAwesomeIcon.VolumeMute, w)) QueueModeratorAction("mute", account);
            ImGui.SameLine(); if (ModeratorButton("userKick", "Exclude", FontAwesomeIcon.UserSlash, w)) QueueModeratorAction("kick", account);
            if (ModeratorButton("userProfile", "View profile", FontAwesomeIcon.User) && CurrentSession is { } session) OpenProfilePage(session, account, ModText(selectedUser, "name"));
            ImGui.SameLine();
            using (UiDisabled.Scope(ModeratorRestrictionText(selectedUser) == "No restrictions"))
                if (ModeratorButton("lift", "Lift restriction", FontAwesomeIcon.Unlock)) QueueModeratorAction(ModNumber(selectedUser, "muteUntil") > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() ? "unmute" : "unban", account);
            ImGui.Separator(); ModeratorHeading("Moderation history", FontAwesomeIcon.History);
            if (history.Length == 0) ImGui.TextColored(MutedText, "No previous moderation actions.");
            foreach (var row in history) DrawModeratorAuditRow(row);
        });
        if (moderatorScrollToUser) { ImGui.SetScrollHereY(.85f); moderatorScrollToUser = false; }
    }

    private static string ModeratorRestrictionText(JsonElement user)
    {
        if (user.TryGetProperty("restriction", out var restriction) && restriction.ValueKind == JsonValueKind.Object)
        {
            var expires = ModNumber(restriction, "expires");
            return expires == 0 ? "Banned · administrator action" : "Excluded until " + ModTime(expires);
        }
        var mute = ModNumber(user, "muteUntil");
        return mute > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() ? "Muted until " + ModTime(mute) : "No restrictions";
    }

    private void DrawModeratorChats()
    {
        ModeratorCard("alphaControls", Ui(252), () =>
        {
            ModeratorHeading("Alpha Chat", FontAwesomeIcon.Comments);
            if (moderatorData.ValueKind != JsonValueKind.Object || !moderatorData.TryGetProperty("controls", out var controls)) { ImGui.TextColored(MutedText, "Refresh to load chat controls."); return; }
            var open = controls.GetProperty("posting").GetBoolean();
            ModeratorBadge(open ? "Posting open" : "Posting paused", open ? Good : ModeratorAmber);
            if (!open && ModText(controls, "updatedBy").Length > 0) ImGui.TextColored(MutedText, "Paused by " + ModName(ModText(controls, "updatedBy")));
            ImGui.Spacing(); ModeratorValueRow("Slow mode", ModNumber(controls, "slowSeconds") == 0 ? "Off" : ModNumber(controls, "slowSeconds") + " seconds");
            ModeratorValueRow("Watch-party sharing", controls.GetProperty("sharing").GetBoolean() ? "Allowed" : "Paused");
            ImGui.Spacing();
            if (ModeratorButton("chatControls", "Edit chat controls", FontAwesomeIcon.SlidersH))
            { QueueModeratorAction("controls"); moderatorSlow = (int)ModNumber(controls, "slowSeconds"); moderatorPosting = open; moderatorSharing = controls.GetProperty("sharing").GetBoolean(); }
        });
        if (moderatorGroups.Length == 0)
        {
            ModeratorCard("groupsEmpty", Ui(236), () => { ModeratorHeading("Public groups", FontAwesomeIcon.Users); ModeratorEmpty(FontAwesomeIcon.Comments, "No public groups to show", "Public groups will appear here when available."); }); return;
        }
        ModeratorHeading("Public groups", FontAwesomeIcon.Users);
        foreach (var group in moderatorGroups)
            ModeratorCard("group" + group.Id, Ui(174), () =>
            {
                ModeratorHeading(group.Name, FontAwesomeIcon.Comments);
                ImGui.TextColored(MutedText, $"{group.MemberCount} {(group.MemberCount == 1 ? "member" : "members")} · Slow mode: {(group.SlowSeconds == 0 ? "Off" : group.SlowSeconds + "s")}");
                ModeratorBadge(group.JoinLocked ? "New joins locked" : "Open to new members", group.JoinLocked ? ModeratorAmber : Good);
                if (ModeratorButton("groupManage" + group.Id, "Manage group", FontAwesomeIcon.Cog))
                { moderatorGroup = group; QueueModeratorAction("group-controls"); moderatorSlow = group.SlowSeconds; moderatorJoinLocked = group.JoinLocked; }
            });
    }

    private void DrawModeratorEvents()
    {
        ModeratorCard("eventFilters", Ui(111), () =>
        {
            ModeratorSearch("events", "Search events or organisers…", ref moderatorSearch);
            var selected = moderatorPastEvents ? 1 : 0;
            EventSegments("modEvents", ["Upcoming", "Past"], ref selected, MathF.Min(Ui(220), ImGui.GetContentRegionAvail().X)); moderatorPastEvents = selected == 1;
        });
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var listing = moderatorEvents.Where(e => !e.Cancelled && (moderatorPastEvents ? e.EndAt <= now : e.EndAt > now) &&
            (e.Title + e.Organiser.DisplayName).Contains(moderatorSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (listing.Length == 0)
        {
            ModeratorCard("eventsEmpty", Ui(236), () => { ModeratorHeading("Events", FontAwesomeIcon.CalendarAlt); ModeratorEmpty(FontAwesomeIcon.CalendarAlt, "No matching events", "Try another search or change the date filter."); }); return;
        }
        foreach (var e in listing)
        {
            var titleHeight = ModeratorWrappedHeight(e.Title, ImGui.GetContentRegionAvail().X - Ui(36));
            var descriptionHeight = MathF.Min(Ui(60), ModeratorWrappedHeight(e.Description, ImGui.GetContentRegionAvail().X - Ui(36)));
            ModeratorCard("event" + e.Id, Ui(161) + titleHeight + descriptionHeight, () =>
            {
                using (UiFonts.PushRelative(1.08f, true)) ImGui.TextWrapped(e.Title);
                ImGui.TextColored(AccentHover, ModTime(e.StartAt));
                ImGui.TextColored(MutedText, "Organised by " + e.Organiser.DisplayName);
                ImGui.TextWrapped(FitPartyTextToWidth(e.Description.Replace('\n', ' '), ImGui.GetContentRegionAvail().X * 2));
                ImGui.Spacing();
                var w = (ImGui.GetContentRegionAvail().X - Ui(20)) / 3;
                if (ModeratorButton("viewEvent" + e.Id, "View event", FontAwesomeIcon.CalendarAlt, w)) { currentPage = HomePage.Events; OpenEventDetails(e); }
                ImGui.SameLine(); if (ModeratorButton("editEvent" + e.Id, "Edit listing", FontAwesomeIcon.Pen, w))
                { moderatorEvent = e; moderatorEventTitle = e.Title; moderatorEventDescription = e.Description; QueueModeratorAction("event-edit"); }
                ImGui.SameLine(); if (ModeratorButton("removeEvent" + e.Id, "Remove", FontAwesomeIcon.Trash, w, danger: true)) { moderatorEvent = e; QueueModeratorAction("event-remove"); }
            });
        }
    }
}

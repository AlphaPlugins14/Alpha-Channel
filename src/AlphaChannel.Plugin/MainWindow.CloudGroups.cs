using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool cloudGroupEventsAttached, cloudGroupBusy;
    private string? cloudGroupError;
    private readonly HashSet<string> revealedCloudMessages = [];
    private readonly HashSet<string> notifiedCloudMessages = [];
    private GroupReport[] cloudGroupReports = [];
    private string? cloudRemoveMember;
    private bool cloudBanMember;
    private GroupMember[] cloudBannedMembers = [];
    private GroupDevice[] cloudMissingDevices = [];
    private bool cloudRefreshRequested;
    private DateTime cloudNextRefresh;
    private void MergeCloudGroupConversations()
    {
        conversations = [.. conversations.Where(c => !c.IsGroup && c.Members.Length == 1 && !GroupChatClient.IsGroup(c.ConversationId)), .. dmClient.Groups.Conversations(CurrentSession?.Token)];
        RestoreArchivedChatsWithNewMessages(conversations);
    }

    private void UpdateCloudGroups()
    {
        if (!cloudGroupEventsAttached) {
            cloudGroupEventsAttached = true;
            dmClient.Groups.Changed += (group, message, sender) => _ = Plugin.Framework.RunOnFrameworkThread(() => {
                if (Volatile.Read(ref disposeStarted) != 0 || CurrentSession is not { } session) return;
                if (!dmClient.Groups.IsCurrentAccount(session.AccountId, session.Token)) return;
                if (group is null || !dmClient.Groups.State.Groups.Any(g => g.Id == group)) ReconcileCloudGroupDisplayCache();
                MergeCloudGroupConversations();
                var current = dmClient.Groups.Conversations().FirstOrDefault(c => c.ConversationId == openConversationId);
                if (current is not null) {
                    openConversationMembers = current.Members;
                    // Receipt-only pushes update existing rows without fetching/decrypting history again.
                    openMessages = openMessages.Select(dmClient.Groups.WithReadReceipt).ToArray();
                    foreach (var member in current.Members) memberDisplayNameCache[member.AccountId] = member.DisplayName;
                }
                else if (GroupChatClient.IsGroup(openConversationId) && dmClient.Groups.Ready) {
                    openMessages = []; openConversationId = null; dmRosterOpen = false;
                }
                if (message is not null && sender is not null && sender != session.AccountId && !AlphaChatBlocked(sender) &&
                    dmClient.Groups.State.Groups.Any(g => g.Id == group) && notifiedCloudMessages.Add(message)) {
                    if (notifiedCloudMessages.Count > 2000) { notifiedCloudMessages.Clear(); notifiedCloudMessages.Add(message); }
                    QueueNotificationSound(GroupChatClient.Prefix + group);
                }
                if (current is not null && dmClient.Groups.Ready && (group is null || GroupChatClient.Prefix + group == openConversationId)) {
                    messageNextKeyCheckUtc = DateTime.MinValue;
                    // One refresh after reconnect also catches deletions missed offline,
                    // even when the group's last-message timestamp hasn't changed.
                    if (message is not null || group is null || openMessages.Length == 0) cloudRefreshRequested = true;
                }
            });
            dmClient.Groups.Locked += token => _ = Plugin.Framework.RunOnFrameworkThread(() => OnCloudGroupsLocked(token));
            dmClient.Groups.ConnectionInterrupted += token => _ = Plugin.Framework.RunOnFrameworkThread(() => OnCloudGroupsInterrupted(token));
        }
        if (CurrentSession is { } currentSession) dmClient.Groups.Start(currentSession.Token);
        else dmClient.Groups.Stop();
        if (cloudRefreshRequested && CurrentSession is { } active && GroupChatClient.IsGroup(openConversationId) &&
            dmClient.Groups.Ready && !messagesLoading && DateTime.UtcNow >= cloudNextRefresh) {
            cloudRefreshRequested = false; cloudNextRefresh = DateTime.UtcNow.AddMilliseconds(200);
            if (messageEncryptionReady != true) CheckCloudGroupEncryption(active, openConversationId!, false);
            RefreshMessages(active, true);
        }
    }

    private void CheckCloudGroupEncryption(CharacterSession session, string conversation, bool repair)
    {
        if (!repair && messageEncryptionReady == true && dmClient.Groups.Ready) return;
        if (messageCheckingKeys || (!repair && DateTime.UtcNow < messageNextKeyCheckUtc)) return;
        // Retry when a key/membership/connection event arrives, or on an explicit
        // retry click. Missing packages must not create a 15-second polling loop.
        messageCheckingKeys = true; messageNextKeyCheckUtc = DateTime.MaxValue;
        var generation = Interlocked.Increment(ref messageEncryptionCheckGeneration);
        _ = Task.Run(async () => {
            string? error = null;
            try { await dmClient.Groups.EnsureKeyAsync(session.Token, conversation); }
            catch (Exception e) { error = e is InvalidOperationException ? e.Message : "Group encryption could not be unlocked. Restore your original PrivateGroups folder if it was moved or deleted."; }
            await Plugin.Framework.RunOnFrameworkThread(() => {
                if (!IsCurrentMessageSession(session) || openConversationId != conversation || generation != Volatile.Read(ref messageEncryptionCheckGeneration)) return;
                messageCheckingKeys = false; messageEncryptionReady = error is null && dmClient.Groups.Ready;
                messageEncryptionError = error; messageMissingKeyName = null;
            });
        });
    }

    private void SendCloudGroupMessage(CharacterSession session, string conversation, string plaintext, bool showInFeed, string? draft, string? reply)
    {
        _ = Task.Run(async () => {
            try {
                var sent = await dmClient.Groups.SendAsync(session.Token, conversation, plaintext);
                await Plugin.Framework.RunOnFrameworkThread(() => {
                    if (!IsCurrentMessageSession(session) || !dmClient.Groups.Ready) return;
                    decryptedCache[sent.Id] = plaintext; UpdateChatsPreview(conversation, sent, plaintext);
                    if (openConversationId == conversation && !openMessages.Any(m => m.Id == sent.Id)) {
                        openMessages = [.. openMessages, dmClient.Groups.WithReadReceipt(sent)]; if (showInFeed) threadScrollToBottom = true;
                    }
                    MergeCloudGroupConversations();
                });
            } catch (Exception e) {
                await Plugin.Framework.RunOnFrameworkThread(() => {
                    if (!IsCurrentMessageSession(session)) return;
                    if(openConversationId==conversation)messagesError = e is InvalidOperationException ? e.Message : "Group message could not be sent. Retry when connected.";
                    RestoreDmDraft(conversation, draft, reply);
                });
            }
        });
    }
    private void RunCloudGroupAction(CharacterSession session, Func<Task> action)
    {
        if (cloudGroupBusy) return;
        cloudGroupBusy = true; cloudGroupError = null;
        _ = Task.Run(async () => {
            string? error = null;
            try { await action(); } catch (Exception e) { error = e is InvalidOperationException ? e.Message : "The group operation failed. Please retry."; }
            await Plugin.Framework.RunOnFrameworkThread(() => {
                cloudGroupBusy = false;
                if (!IsCurrentMessageSession(session)) return;
                cloudGroupError = error; MergeCloudGroupConversations();
                if (error is not null && GroupChatClient.IsGroup(openConversationId)) messagesError = error;
            });
        });
    }
    private bool IsCloudMessageHidden(MessageDto message) => GroupChatClient.IsMessage(message) &&
        AlphaChatBlocked(message.SenderAccountId) && !revealedCloudMessages.Contains(message.Id);
    private bool DrawCloudBlockedMessage(MessageDto message)
    {
        if (!IsCloudMessageHidden(message)) return false;
        using var id = ImRaii.PushId(message.Id);
        if (ImGui.Button("1 blocked message — click to view", new Vector2(ImGui.GetContentRegionAvail().X, Ui(34)))) revealedCloudMessages.Add(message.Id);
        ImGui.Dummy(UiVec(0, 5)); return true;
    }

    private void DrawCloudGroupInvitations(CharacterSession session)
    {
        if (!dmClient.Groups.Ready) ImGui.TextColored(MutedText, dmClient.Groups.Status);
        if (cloudGroupError is { } error) ImGui.TextWrapped(error);
        if (!dmClient.Groups.IsCurrentAccount(session.AccountId, session.Token)) return;
        foreach (var invite in dmClient.Groups.State.Invitations) {
            using var id = ImRaii.PushId(invite.Id);
            ImGui.TextUnformatted($"{invite.Inviter} invited you to {invite.Name}");
            using (UiDisabled.Scope(cloudGroupBusy || !dmClient.Groups.Ready)) {
                if (ImGui.Button("Join group")) RunCloudGroupAction(session, async () => { await dmClient.Groups.MembershipAsync(session.Token, invite.Id, "accept"); });
                ImGui.SameLine();
                if (ImGui.Button("Decline")) RunCloudGroupAction(session, async () => { await dmClient.Groups.MembershipAsync(session.Token, invite.Id, "decline"); });
            }
            ImGui.Separator();
        }
    }
#if DEBUG
    private void DrawCloudGroupReportsAdmin()
    {
        if (CurrentSession is not { } session || !ImGui.CollapsingHeader("Private chat and group reports")) return;
        if (cloudGroupError is { } error) ImGui.TextWrapped(error);
        if (ImGui.SmallButton("Refresh chat reports")) {
            RunCloudGroupAction(session, async () => {
                var reports = await dmClient.Groups.ReportsAsync(session.Token);
                await Plugin.Framework.RunOnFrameworkThread(() => { if (IsCurrentMessageSession(session)) cloudGroupReports = reports; });
            });
        }
        foreach (var report in cloudGroupReports.OrderByDescending(r => r.CreatedAt)) {
            using var id = ImRaii.PushId(report.Id);
            ImGui.TextWrapped($"Reported {(report.Direct ? "direct" : "group")} message from {report.SenderId}: {report.Plaintext}");
            DrawReportedGroupImage(session, report);
            if (ImGui.SmallButton("Resolve report")) RunCloudGroupAction(session, async () => {
                await dmClient.Groups.ResolveReportAsync(session.Token, report.Id);
                await Plugin.Framework.RunOnFrameworkThread(() => cloudGroupReports = cloudGroupReports.Where(r => r.Id != report.Id).ToArray());
            });
        }
    }
#endif
    private void DrawCloudGroupControls(CharacterSession session, string conversation)
    {
        var group = dmClient.Groups.State.Groups.FirstOrDefault(g => GroupChatClient.Prefix + g.Id == conversation);
        if (group is null) { ImGui.TextDisabled("Group chats disconnected"); return; }
        using var disabled = UiDisabled.Scope(cloudGroupBusy || !dmClient.Groups.Ready);
        DrawCloudGroupAppearanceEditor(session, group, conversation);
        if (group.IsPublic || group.OwnerId == session.AccountId) {
            if (AlphaChatButton("cloudInvitePeople","Invite people",Dalamud.Interface.FontAwesomeIcon.UserPlus,ImGui.GetContentRegionAvail().X,Ui(28))) { if (friendsDirty && !friendsLoading) RefreshFriends(session.Token); OpenChatsPopup("##cloudGroupInvite"); }
        }
        if (group.OwnerId == session.AccountId) {
            if (AlphaChatButton("cloudManageMembers","Manage members",Dalamud.Interface.FontAwesomeIcon.Users,ImGui.GetContentRegionAvail().X,Ui(28))) OpenChatsPopup("##cloudGroupMembers");
            if (group.IsPublic) {
                if (AlphaChatButton("cloudBannedMembers","Banned members",Dalamud.Interface.FontAwesomeIcon.UserSlash,ImGui.GetContentRegionAvail().X,Ui(28))) {
                    RunCloudGroupAction(session, async () => {
                        var members = await dmClient.Groups.BansAsync(session.Token, conversation);
                        await Plugin.Framework.RunOnFrameworkThread(() => { if (IsCurrentMessageSession(session)) cloudBannedMembers = members; });
                    });
                    OpenChatsPopup("##cloudGroupBans");
                }
            }
        }
        if (!group!.IsPublic && AlphaChatButton("cloudKeyAccess","Key access",Dalamud.Interface.FontAwesomeIcon.Key,ImGui.GetContentRegionAvail().X,Ui(28))) {
            cloudMissingDevices = [];
            RunCloudGroupAction(session, async () => {
                var info = await dmClient.Groups.InfoAsync(session.Token, conversation);
                await Plugin.Framework.RunOnFrameworkThread(() => { if (IsCurrentMessageSession(session)) cloudMissingDevices = info.Missing; });
            });
            OpenChatsPopup("##cloudGroupKeys");
        }
        if (AlphaChatButton("cloudLeaveGroup","Leave group",Dalamud.Interface.FontAwesomeIcon.SignOutAlt,ImGui.GetContentRegionAvail().X,Ui(28),danger:true)) OpenChatsPopup("##cloudGroupLeave");
        PrepareChatsPopup("##cloudGroupBans",UiVec(340,260));
        if (ImGui.BeginPopup("##cloudGroupBans")) {
            DrawSocialHeading("Banned members"); ImGui.Separator();
            if (cloudGroupBusy) ImGui.TextDisabled("Loading…");
            foreach (var member in cloudBannedMembers) {
                DrawUsername(member.AccountId, member.DisplayName); ImGui.SameLine();
                if (ImGui.SmallButton("Unban##" + member.AccountId)) {
                    var target = member.AccountId;
                    RunCloudGroupAction(session, async () => { await dmClient.Groups.MembershipAsync(session.Token, conversation, "unban", target); });
                    ImGui.CloseCurrentPopup();
                }
            }
            if (!cloudGroupBusy && cloudBannedMembers.Length == 0) ImGui.TextDisabled("No banned members.");
            ImGui.EndPopup();
        }
        PrepareChatsPopup("##cloudGroupKeys",UiVec(360,300));
        if (ImGui.BeginPopup("##cloudGroupKeys")) {
            DrawSocialHeading("Key access");
            ImGui.TextWrapped("Only share with a member whose new installation you recognise. Sharing allows that installation to decrypt this group's permitted history and future messages.");
            if (cloudGroupBusy) ImGui.TextUnformatted("Checking installations…");
            else if (cloudMissingDevices.Length == 0) ImGui.TextUnformatted("No installations are waiting for a key.");
            foreach (var device in cloudMissingDevices) {
                var name = group?.Members.FirstOrDefault(m => m.AccountId == device.AccountId)?.DisplayName ?? "Member";
                var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(device.SigningKey)))[..12];
                ImGui.TextUnformatted($"{name} · {fingerprint}");
                if (ImGui.Button("Share group key##" + device.DeviceId)) {
                    RunCloudGroupAction(session, async () => { await dmClient.Groups.ProvisionAsync(session.Token, conversation, device.DeviceId); });
                    ImGui.CloseCurrentPopup();
                }
            }
            ImGui.EndPopup();
        }
        PrepareChatsPopup("##cloudGroupInvite",UiVec(340,300));
        if (ImGui.BeginPopup("##cloudGroupInvite")) {
            ImGui.TextUnformatted("Invite a friend");
            using (ImRaii.Child("##inviteFriends", new Vector2(0,0), false)) {
                foreach (var friend in friends.Where(f => !group!.Members.Any(m => m.AccountId == f.AccountId) && !AlphaChatBlocked(f.AccountId)))
                    if (ImGui.Selectable(friend.DisplayName + "##" + friend.AccountId)) {
                        var target = friend.AccountId;
                        RunCloudGroupAction(session, async () => { await dmClient.Groups.InviteAsync(session.Token, conversation, target); }); ImGui.CloseCurrentPopup();
                    }
            }
            ImGui.EndPopup();
        }
        PrepareChatsPopup("##cloudGroupMembers",UiVec(360,340));
        if (ImGui.BeginPopup("##cloudGroupMembers")) {
            DrawSocialHeading("Manage members");
            if (group!.Members.Length == 1) DrawSocialHelperWrapped("You're the only member so far.");
            foreach (var member in group!.Members.Where(m => m.AccountId != session.AccountId)) {
                DrawUsername(member.AccountId, FitPartyTextToWidth(member.DisplayName,Ui(170))); ImGui.SameLine();
                if (ImGui.SmallButton("Remove##" + member.AccountId)) { cloudRemoveMember = member.AccountId; cloudBanMember = false; }
                if (group.IsPublic) { ImGui.SameLine(); if (ImGui.SmallButton("Ban##" + member.AccountId)) { cloudRemoveMember = member.AccountId; cloudBanMember = true; } }
            }
            if (cloudRemoveMember is not null) {
                ImGui.Separator(); ImGui.TextWrapped(cloudBanMember ? "Ban this member? They cannot rejoin until unbanned." : "Remove this member from the group?");
                if (ImGui.Button("Confirm removal")) {
                    var target = cloudRemoveMember; cloudRemoveMember = null;
                    var operation = cloudBanMember ? "ban" : "remove";
                    RunCloudGroupAction(session, async () => { await dmClient.Groups.MembershipAsync(session.Token, conversation, operation, target); }); ImGui.CloseCurrentPopup();
                }
            }
            ImGui.EndPopup();
        }
        PrepareChatsPopup("##cloudGroupLeave",UiVec(340,160));
        if (ImGui.BeginPopup("##cloudGroupLeave")) {
            DrawSocialHeading("Leave group?");
            ImGui.TextWrapped("You will stop receiving messages from this group.");
            if (ImGui.Button("Leave group##confirm")) {
                RunCloudGroupAction(session, async () => { await dmClient.Groups.MembershipAsync(session.Token, conversation, "leave"); }); ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine(); if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup(); ImGui.EndPopup();
        }
    }
}

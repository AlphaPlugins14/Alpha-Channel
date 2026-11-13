using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private const string BlockedDmMessage = "User blocked - You can not send or receive messages.";
    private const string BlockedGroupDmMessage = "You are unable to send messages into a group chat containing a user you have blocked. You can still see messages from unblocked users.";
    private readonly Dictionary<(string Account, string Target), bool> localBlockChanges = new();
    private readonly HashSet<(string Account, string Target)> unblocksInFlight = new();

    private bool IsOpenDmBlocked(CharacterSession session) => !Auth.GroupChatClient.IsGroup(openConversationId) &&
        openConversationMembers.Any(member =>
            localBlockChanges.TryGetValue((session.AccountId, member.AccountId), out var blocked)
                ? blocked : friendsAccountId == session.AccountId && blockedAccounts.Any(account => account.Id == member.AccountId));

    private void RecordLocalBlockChange(string accountId, string targetId, bool blocked)
    {
        localBlockChanges[(accountId, targetId)] = blocked; alphaChatMaintenanceDirty = true;
        if (blocked) foreach (var preview in chatsPagePreviews.Where(p => Auth.GroupChatClient.IsGroup(p.Key) && p.Value.SenderId == targetId))
            chatsPagePreviews.TryRemove(preview.Key, out _);
    }

    private void ConfirmRefreshedBlocks(string accountId, AccountSummaryDto[] loaded)
    {
        alphaChatMaintenanceDirty = true;
        // Retain a successful local change while an older refresh is still in
        // flight; discard it once a refresh confirms the server's new state.
        foreach (var change in localBlockChanges.Where(change => change.Key.Account == accountId).ToArray())
            if (loaded.Any(account => account.Id == change.Key.Target) == change.Value)
                localBlockChanges.Remove(change.Key);
    }

    private void UnblockAccount(CharacterSession session, string accountId)
    {
        var key = (session.AccountId, accountId);
        if (!unblocksInFlight.Add(key)) return;
        _ = Task.Run(async () =>
        {
            var succeeded = false;
            try { succeeded = await friendsClient.UnblockAsync(session.Token, accountId); }
            catch (Exception) { AepLog.Warning("[Friends] Unblock failed."); }
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                unblocksInFlight.Remove(key);
                if (CurrentSession?.AccountId != session.AccountId || CurrentSession.Token != session.Token) return;
                if (succeeded)
                {
                    RecordLocalBlockChange(session.AccountId, accountId, false);
                    blockedAccounts = blockedAccounts.Where(account => account.Id != accountId).ToArray();
                    friendsDirty = true;
                    conversationsDirty = true;
                    messagesError = null;
                }
                else if (!openConversationIsGroup && openConversationMembers.Any(member => member.AccountId == accountId))
                    messagesError = "Could not unblock this user. Please try again.";
                else friendsError = "Could not unblock this user. Please try again.";
            });
        });
    }

    private float BlockedDmNoticeHeight(float width) =>
        ImGui.CalcTextSize(openConversationIsGroup ? BlockedGroupDmMessage : BlockedDmMessage,
            false, MathF.Max(Ui(100f), width - Ui(24f))).Y + Ui(openConversationIsGroup ? 16f : 28f);

    private void DrawBlockedDmNotice(CharacterSession session, Vector2 origin, float width)
    {
        ImGui.SetCursorScreenPos(origin + UiVec(12f, 7f));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - Ui(24f));
        ImGui.TextColored(Danger, openConversationIsGroup ? BlockedGroupDmMessage : BlockedDmMessage);
        if (openConversationIsGroup && ImGui.IsItemHovered())
            UiFonts.SetTooltip("You can unblock via your friends list");
        ImGui.PopTextWrapPos();
        if (openConversationIsGroup) return;
        if (openConversationMembers.FirstOrDefault() is not { } member) return;
        var busy = unblocksInFlight.Contains((session.AccountId, member.AccountId));
        using (UiDisabled.Scope(busy))
        using (ImRaii.PushColor(ImGuiCol.Text, AccentHover).Push(ImGuiCol.Button, Vector4.Zero)
                   .Push(ImGuiCol.ButtonHovered, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.2f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(2f, 0f)))
        {
            if (ImGui.SmallButton(busy ? "Unblocking...##dmUnblock" : "Unblock?##dmUnblock"))
                UnblockAccount(session, member.AccountId);
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            ImGui.GetWindowDrawList().AddLine(new Vector2(min.X, max.Y), max, ImGui.GetColorU32(AccentHover));
        }
    }
}

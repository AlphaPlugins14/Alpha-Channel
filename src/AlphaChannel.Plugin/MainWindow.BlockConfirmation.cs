using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private sealed record PendingFriendAction(string AccountId, string DisplayName,
        string SessionAccountId, string SessionToken, bool RemovingFriend = false);
    private PendingFriendAction? pendingFriendAction;
    private Task<bool>? pendingFriendActionTask;
    private string? friendActionError;

    private void QueueFriendBlockConfirmation(string accountId, string displayName)
        => QueueFriendActionConfirmation(accountId, displayName, removingFriend: false);

    private void QueueFriendRemovalConfirmation(string accountId, string displayName)
        => QueueFriendActionConfirmation(accountId, displayName, removingFriend: true);

    private void QueueFriendActionConfirmation(string accountId, string displayName, bool removingFriend)
    {
        if (CurrentSession is not { } session || pendingFriendActionTask is not null) return;
        pendingFriendAction = new(accountId, displayName, session.AccountId, session.Token, removingFriend);
        friendActionError = null;
    }

    private void DrawFriendActionConfirmationOverlay()
    {
        if (pendingFriendAction is not { } target) return;
        if (CurrentSession is not { } session || session.AccountId != target.SessionAccountId ||
            session.Token != target.SessionToken)
        {
            pendingFriendAction = null;
            pendingFriendActionTask = null;
            friendActionError = null;
            return;
        }
        if (pendingFriendActionTask is { IsCompleted: true } completed)
        {
            pendingFriendActionTask = null;
            if (completed.IsCompletedSuccessfully && completed.Result)
            {
                if (!target.RemovingFriend)
                    RecordLocalBlockChange(session.AccountId, target.AccountId, true);
                friendsDirty = true;
                conversationsDirty = true;
                if (profilePageAccountId == target.AccountId) CloseProfilePage();
                pendingFriendAction = null;
                return;
            }
            friendActionError = target.RemovingFriend
                ? "Could not remove this friend. Please try again."
                : "Could not block this user. Please try again.";
        }

        var parentPosition = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var panelSize = new Vector2(MathF.Min(Ui(target.RemovingFriend ? 480f : 600f), parentSize.X - Ui(32f)),
            MathF.Min(Ui(target.RemovingFriend ? 260f : 520f), parentSize.Y - Ui(32f)));
        var panelPosition = parentPosition + (parentSize - panelSize) * 0.5f;
        ImGui.SetNextWindowPos(parentPosition, ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##friendBlockConfirmationOverlay", flags))
        {
            ImGui.End();
            return;
        }
        ImGui.GetWindowDrawList().AddRectFilled(parentPosition, parentPosition + parentSize,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.54f)));
        ImGui.SetCursorScreenPos(panelPosition);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(24f, 20f))
            .Push(ImGuiStyleVar.ItemSpacing, UiVec(8f, 12f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.025f, 0.03f, 0.06f, 0.995f))
            .Push(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.82f)))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##friendBlockConfirmationPanel", panelSize, true)))
        {
            if (panel)
            {
                using var uiFontScale4317=UiFonts.PushRelative(1.15f);
                DrawUsername(target.AccountId,target.RemovingFriend ? $"Remove {target.DisplayName} as a friend?" : $"Block {target.DisplayName}?");
                uiFontScale4317.Dispose();
                ImGui.Separator();
                if (target.RemovingFriend)
                {
                    ImGui.TextWrapped("You will be removed from each other's friends lists. This does not block them or delete your chat history.");
                    using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
                        ImGui.TextWrapped("You can add them as a friend again later.");
                }
                else
                {
                    ImGui.TextWrapped("You will be removed from each other's friends lists, and any pending friend request will be removed.");
                    ImGui.TextWrapped("Neither of you can send the other private messages, find the other through friend search, or add the other by username or friend code.");
                    ImGui.TextWrapped("Existing chat history stays available. Neither you nor they will be able to send messages in group chats that you're both in.");
                    ImGui.TextWrapped("Blocking currently does not prevent them from joining your Watch Party, kick them from a room, or hide their Watch Party chat messages.");
                    using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
                    {
                        ImGui.TextWrapped("They won't receive a notification saying you blocked them. Your name disappears from their friends list when it refreshes.");
                        ImGui.TextWrapped("You can unblock them from the Blocked Accounts panel in Settings → Other. Unblocking does not restore the friendship automatically.");
                    }
                }
                if (friendActionError is { } error)
                    using (ImRaii.PushColor(ImGuiCol.Text, Danger)) ImGui.TextWrapped(error);
                var buttonWidth = (ImGui.GetContentRegionAvail().X - Ui(10f)) * 0.5f;
                using (UiDisabled.Scope(pendingFriendActionTask is not null))
                {
                    if (ImGui.Button("Cancel##friendBlock", new Vector2(buttonWidth, Ui(38f))))
                        pendingFriendAction = null;
                    ImGui.SameLine(0f, Ui(10f));
                    using (ImRaii.PushColor(ImGuiCol.Button, Danger)
                        .Push(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.25f, 0.3f, 1f)))
                    {
                        var actionLabel = target.RemovingFriend ? "Remove friend" : "Block user";
                        var busyLabel = target.RemovingFriend ? "Removing..." : "Blocking...";
                        if (ImGui.Button($"{(pendingFriendActionTask is null ? actionLabel : busyLabel)}##friendBlock",
                                new Vector2(buttonWidth, Ui(38f))))
                        {
                            friendActionError = null;
                            pendingFriendActionTask = Task.Run(async () =>
                            {
                                try
                                {
                                    return target.RemovingFriend
                                        ? await friendsClient.RemoveFriendAsync(target.SessionToken, target.AccountId)
                                        : await friendsClient.BlockAsync(target.SessionToken, target.AccountId);
                                }
                                catch (Exception) { return false; }
                            });
                        }
                    }
                }
            }
        }
        ImGui.End();
    }
}

using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    internal readonly CharacterDirectoryClient CharacterDirectory = new();
    private string? characterDirectorySessionToken;
    private bool characterDirectoryBusy;
    private bool characterDirectorySettingsLoaded;
    private bool hideLinkedCharacterName;
    private string? characterDirectoryError;
    private DateTime characterDirectoryNextSync;

    internal void TickCharacterDirectory()
    {
        var session = CurrentSession;
        if (characterDirectorySessionToken != session?.Token)
        {
            characterDirectorySessionToken = session?.Token;
            characterDirectorySettingsLoaded = false;
            characterDirectoryBusy = false;
            characterDirectoryError = null;
            characterDirectoryNextSync = DateTime.MinValue;
        }
        if (session is not null && !characterDirectoryBusy && DateTime.UtcNow >= characterDirectoryNextSync)
            _ = SyncCharacterDirectoryAsync(session, null);
    }

    private async Task SyncCharacterDirectoryAsync(CharacterSession session, bool? hidden)
    {
        characterDirectoryBusy = true;
        characterDirectoryNextSync = DateTime.UtcNow.AddMinutes(60);
        try
        {
            var result = await CharacterDirectory.SyncAsync(session.Token, hidden, youtubeFeedToken).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (CurrentSession?.Token != session.Token || Volatile.Read(ref disposeStarted) != 0) return;
                if (!string.Equals(result.AccountId, session.AccountId, StringComparison.OrdinalIgnoreCase)) return;
                hideLinkedCharacterName = result.Hidden;
                characterDirectorySettingsLoaded = true;
                characterDirectoryError = null;
                if (profilePageData?.AccountId == session.AccountId)
                    profilePageData = profilePageData with { CharacterName = result.Hidden ? null : result.CharacterName,
                        CharacterWorld = result.Hidden ? null : result.CharacterWorld };
            });
        }
        catch (Exception)
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (CurrentSession?.Token != session.Token || Volatile.Read(ref disposeStarted) != 0) return;
                characterDirectoryError = hidden.HasValue ? "Couldn't save character visibility. Please try again." : "Character directory unavailable. Retrying shortly.";
                characterDirectoryNextSync = DateTime.UtcNow.AddMinutes(2);
            });
        }
        finally
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (CurrentSession?.Token == session.Token && Volatile.Read(ref disposeStarted) == 0) characterDirectoryBusy = false;
            });
        }
    }

    private void DrawLinkedCharacterVisibility(CharacterSession session)
    {
        ImGui.Spacing();
        var hidden = hideLinkedCharacterName;
        using (UiDisabled.Scope(!characterDirectorySettingsLoaded || characterDirectoryBusy))
            if (ImGui.Checkbox("Hide linked character name", ref hidden)) _ = SyncCharacterDirectoryAsync(session, hidden);
        using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
            DrawSettingsExplanation("Hide your character name on your profile and disable Alpha Channel interactions from the in-game character menu.");
        if (characterDirectoryBusy) ImGui.TextColored(MutedText, "Saving character directory…");
        if (characterDirectoryError is { } error)
        {
            ImGui.TextColored(Danger, error);
            if (!characterDirectoryBusy && ImGui.SmallButton("Retry##characterDirectory")) _ = SyncCharacterDirectoryAsync(session, null);
        }
    }

    internal bool CanUseCharacterMenuTarget(string accountId) => CurrentSession is { } session &&
        !string.Equals(session.AccountId, accountId, StringComparison.OrdinalIgnoreCase) && !AlphaChatBlocked(accountId);

    internal void RunCharacterMenuAction(AccountProfileDto target, string action)
    {
        if (CurrentSession is not { } session || !CanUseCharacterMenuTarget(target.AccountId)) return;
        switch (action)
        {
            case "friend": _ = AddCharacterDirectoryFriendAsync(session, target); break;
            case "profile": OpenUi(); OpenProfilePage(session, target.AccountId, target.DisplayName); break;
            case "invite":
                if (CanInviteToCurrentWatchParty())
                {
                    OpenUi();
                    StartOrOpenConversation(session, target.AccountId, target.DisplayName, CreateWatchPartyInviteMessage());
                }
                else Plugin.ChatGui.Print("[AlphaChannel] Join or host a Watch Party before inviting someone.");
                break;
            case "join": _ = JoinCharacterDirectoryPartyAsync(session, target); break;
        }
    }

    private async Task AddCharacterDirectoryFriendAsync(CharacterSession session, AccountProfileDto target)
    {
        if (target.CharacterName is null || target.CharacterWorld is null) return;
        var result = await friendsClient.SendRequestByCharacterAsync(session.Token, target.CharacterName, target.CharacterWorld).ConfigureAwait(false);
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (CurrentSession?.Token == session.Token && Volatile.Read(ref disposeStarted) == 0)
                HandleAddFriendByCharacterResult(result, target.DisplayName);
        });
    }

    private async Task JoinCharacterDirectoryPartyAsync(CharacterSession session, AccountProfileDto target)
    {
        if (!EnsureWatchPartyJoinAllowed()) return;
        try
        {
            var rooms = await roomsClient.ListAsync(session.Token).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (CurrentSession?.Token != session.Token || Volatile.Read(ref disposeStarted) != 0) return;
                var room = rooms?.FirstOrDefault(r => r.HostAccountId == target.AccountId);
                if (room is null) Plugin.ChatGui.Print("[AlphaChannel] This person is not hosting a joinable Watch Party.");
                else { OpenUi(); JoinPartyDirectoryRoom(room); }
            });
        }
        catch (Exception)
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (CurrentSession?.Token == session.Token && Volatile.Read(ref disposeStarted) == 0)
                    Plugin.ChatGui.Print("[AlphaChannel] Couldn't look up this Watch Party. Please try again.");
            });
        }
    }
}

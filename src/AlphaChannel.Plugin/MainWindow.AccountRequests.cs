using AlphaChannel.Plugin.Auth;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private ulong accountRequestContentId;
    private int accountSettingsGeneration;
    private bool displayNameSaving;

    private readonly record struct AccountRequest(CharacterSession Session, ulong ContentId, int Generation);

    private AccountRequest CaptureAccountRequest(CharacterSession session) =>
        new(session, Plugin.ReadLocalContentId(), accountSettingsGeneration);

    private bool IsCurrentAccountRequest(AccountRequest request) =>
        Volatile.Read(ref disposeStarted) == 0 && request.ContentId != 0 &&
        request.ContentId == Plugin.ReadLocalContentId() &&
        request.Generation == accountSettingsGeneration && ReferenceEquals(CurrentSession, request.Session) &&
        // Sign-out removes the persisted session immediately; the header's session
        // snapshot may not refresh until the next framework update.
        Plugin.Cfg.CharacterSessions.TryGetValue(request.ContentId, out var persisted) &&
        ReferenceEquals(persisted, request.Session);

    private void ResetAccountSettingsRequests()
    {
        accountSettingsGeneration++;
        displayNameSaving = inviteCodeRefreshing = false;
        displayNameError = null;
        myLinkedCharacters = null;
        lastDisplayNameSyncedFor = lastProfileSyncedFor = null;
    }

    private async Task SaveAccountDisplayNameAsync(CharacterSession session, string name)
    {
        if (displayNameSaving) return;
        var request = CaptureAccountRequest(session);
        displayNameSaving = true;
        try
        {
            var outcome = await authClient.UpdateDisplayNameAsync(session.Token, name).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (!IsCurrentAccountRequest(request)) return;
                if (outcome.Account is { } updated)
                {
                    session.DisplayName = updated.DisplayName;
                    onSessionChanged(session);
                    displayNameError = null;
                }
                else
                    displayNameError = outcome.NameTaken ? "That name's already taken - try another."
                        : outcome.InvalidFormat ? "That name doesn't fit the rules below." : "Couldn't save that name.";
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            AepLog.Warning("[Account] Username update failed.");
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (IsCurrentAccountRequest(request)) displayNameError = "Couldn't save that name.";
            }).ConfigureAwait(false);
        }
        finally
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (IsCurrentAccountRequest(request)) displayNameSaving = false;
            }).ConfigureAwait(false);
        }
    }

    private async Task RefreshAccountDetailsAsync(CharacterSession session)
    {
        var request = CaptureAccountRequest(session);
        try
        {
            var summary = await authClient.GetMeAsync(session.Token).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (!IsCurrentAccountRequest(request) || summary is null) return;
                session.InviteCode = summary.InviteCode;
                session.AvatarIcon = summary.AvatarIcon;
                session.AvatarColorHex = summary.AvatarColorHex;
                session.AvatarImageUrl = summary.AvatarImageUrl;
                session.Bio = summary.Bio;
                session.StatusMessage = summary.StatusMessage;
                onSessionChanged(session);
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            AepLog.Warning("[Account] Invite code refresh failed.");
        }
        finally
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (IsCurrentAccountRequest(request)) inviteCodeRefreshing = false;
            }).ConfigureAwait(false);
        }
    }

    private async Task LoadLinkedAccountCharactersAsync(CharacterSession session)
    {
        var request = CaptureAccountRequest(session);
        try
        {
            var characters = await authClient.GetMyCharactersAsync(session.Token).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (IsCurrentAccountRequest(request)) myLinkedCharacters = characters;
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            AepLog.Warning("[Account] Linked characters lookup failed.");
        }
    }
}

using System.Numerics;
using System.Security.Cryptography;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool guestRegistrationBusy, guestUpgradePopup, guestReferenceBusy, guestNameConflict;
    private string guestMigrationName = "", guestRegistrationError = "";
    private DateTime guestRetryAt, guestReferenceRetryAt;

    private void ResumeIncompleteGuestSignup()
    {
        // Resume accounts created by the older early-registration flow. A
        // token-only entry is an uncertain final request: retain it for a safe,
        // idempotent retry, without issuing another registration automatically.
        if (Plugin.Cfg.HasCompletedFirstLaunch ||
            Plugin.Cfg.CharacterSessions.GetValueOrDefault(0UL) is not { IsGuest: true } direct) return;
        namePromptInput = direct.DisplayName;
        if (string.IsNullOrEmpty(direct.AccountId)) return;
        CurrentSession = direct;
        onSessionChanged(direct);
        firstLaunchUsingAccount = false;
        firstLaunchStep = FirstLaunchStep.Topics;
    }

    internal void TickGuestAccounts()
    {
        if (Volatile.Read(ref disposeStarted) != 0) return;
        var cfg = Plugin.Cfg;
        var contentId = Plugin.ReadLocalContentId();
        // Copy the shared session into the character slot for legacy subsystems
        // which validate the active character entry before applying async work.
        if (!cfg.CharacterSessions.ContainsKey(contentId) && cfg.SessionForCharacter(contentId) is { } shared)
            cfg.CharacterSessions[contentId] = shared;
        if (IsOpen && cfg.HasCompletedFirstLaunch && cfg.SessionForCharacter(contentId) is null &&
            (!cfg.GuestAccessNoticeSeen || cfg.CharacterSessions.GetValueOrDefault(0UL) is { AccountId.Length: 0 }) &&
            !cfg.CharacterSessions.Values.Any(s => !s.IsGuest && !string.IsNullOrEmpty(s.AccountId)) &&
            !guestRegistrationBusy && !guestNameConflict && DateTime.UtcNow >= guestRetryAt)
        {
            var name = cfg.CharacterDisplayNames.GetValueOrDefault(contentId) ?? CurrentDisplayName;
            if (DisplayNameRules.IsValid(name ?? "")) BeginGuestRegistration(name!, false);
            else { guestNameConflict = true; guestUpgradePopup = true; }
        }
        if (cfg.SessionForCharacter(contentId) is not { } session) return;
        if (!session.IsGuest && !cfg.GuestAccessNoticeSeen) { cfg.GuestAccessNoticeSeen = true; cfg.Save(); }
        if (session.IsGuest && !cfg.GuestAccessNoticeSeen && cfg.HasCompletedFirstLaunch) guestUpgradePopup = true;
        // Reference collection is permitted to complete after conversion too.
        if (session.GuestReferenceRecorded || guestReferenceBusy || DateTime.UtcNow < guestReferenceRetryAt ||
            cfg.CharacterSessions.GetValueOrDefault(0UL)?.AccountId != session.AccountId) return;
        GuestCharacterReferenceRequest reference;
        try
        {
            var character = Plugin.ObjectTable.LocalPlayer;
            var homeWorld = character?.HomeWorld.Value.Name.ToString();
            if (contentId == 0 || character is null || string.IsNullOrWhiteSpace(character.Name.TextValue) || string.IsNullOrWhiteSpace(homeWorld)) return;
            reference = new(character.Name.TextValue, homeWorld, contentId.ToString());
        }
        catch (Exception) { return; } // Character sheets can be unavailable during login/zoning.
        guestReferenceBusy = true; guestReferenceRetryAt = DateTime.UtcNow.AddMinutes(1);
        _ = Task.Run(async () => {
            var recorded = false;
            try { recorded = await authClient.RecordGuestReferenceAsync(session.Token, reference, youtubeFeedToken); } catch (Exception) { }
            if (youtubeFeedToken.IsCancellationRequested) return;
            await Plugin.Framework.RunOnFrameworkThread(() => {
                guestReferenceBusy = false;
                if (recorded && cfg.CharacterSessions.Values.Any(s => s.AccountId == session.AccountId))
                {
                    foreach (var item in cfg.CharacterSessions.Values.Where(s => s.AccountId == session.AccountId)) item.GuestReferenceRecorded = true;
                    cfg.Save();
                }
            });
        });
    }

    private void BeginGuestRegistration(string name, bool firstLaunch)
    {
        if (guestRegistrationBusy) return;
        if (!DisplayNameRules.IsValid(name.Trim()))
        {
            firstLaunchNameError = guestRegistrationError = "Choose a valid username.";
            if (firstLaunch) firstLaunchStep = FirstLaunchStep.Username;
            return;
        }
        var cfg = Plugin.Cfg;
        var pending = cfg.CharacterSessions.GetValueOrDefault(0UL);
        if (pending is { AccountId.Length: > 0 })
        {
            if (firstLaunch) FinishGuestRegistration(pending, true);
            else guestRegistrationError = "This device already has a saved account. Reopen Alpha Channel to continue with it.";
            return;
        }
        pending ??= new CharacterSession { Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), IsGuest = true };
        pending.DisplayName = name.Trim();
        cfg.CharacterSessions[0] = pending;
        // Do not register an account unless its retry credential is durable.
        if (!cfg.SaveAccountCredentials())
        {
            firstLaunchNameError = guestRegistrationError = "Your login could not be saved securely on this device. Check available disk space and plugin-folder access, then try again.";
            if (firstLaunch) firstLaunchStep = FirstLaunchStep.Username;
            guestRetryAt = DateTime.UtcNow.AddMinutes(1);
            return;
        }
        guestRegistrationBusy = firstLaunchNameSubmitting = true;
        firstLaunchNameError = null;
        guestRegistrationError = ""; guestRetryAt = DateTime.UtcNow.AddMinutes(1);
        var credential = pending.Token;
        _ = Task.Run(async () => {
            GuestRegistrationResponse? response = null; string? error = null;
            try { (response, error) = await authClient.RegisterGuestAsync(credential, name.Trim(), youtubeFeedToken); }
            catch (Exception) { error = "Account registration is temporarily unavailable. Please try again later."; }
            if (youtubeFeedToken.IsCancellationRequested) return;
            await Plugin.Framework.RunOnFrameworkThread(() => {
                guestRegistrationBusy = firstLaunchNameSubmitting = false;
                if (cfg.CharacterSessions.GetValueOrDefault(0UL)?.Token != credential) return;
                if (response is null)
                {
                    firstLaunchNameError = guestRegistrationError = error ?? "Account registration failed.";
                    guestNameConflict = error?.Contains("username", StringComparison.OrdinalIgnoreCase) == true;
                    if (firstLaunch) firstLaunchStep = FirstLaunchStep.Username;
                    if (guestNameConflict && !firstLaunch) { guestMigrationName = name; guestUpgradePopup = true; }
                    return;
                }
                var a = response.Account;
                var registered = new CharacterSession { AccountId = a.AccountId, Token = response.Token,
                    Handle = a.Handle, DisplayName = a.DisplayName, InviteCode = a.InviteCode,
                    AvatarIcon = a.AvatarIcon, AvatarColorHex = a.AvatarColorHex, AvatarImageUrl = a.AvatarImageUrl,
                    Bio = a.Bio, StatusMessage = a.StatusMessage, IsGuest = a.IsGuest };
                // The registration endpoint is idempotent. Reuse the account ID
                // as the event ID so a lost response/retry cannot count twice.
                // Restoring an existing local session does not enter this path.
                RecordAdminRegistration(registered, registered.AccountId);
                FinishGuestRegistration(registered, firstLaunch);
            });
        });
    }

    private void FinishGuestRegistration(CharacterSession registered, bool firstLaunch)
    {
        var cfg = Plugin.Cfg;
        cfg.CharacterSessions[0] = registered;
        CurrentSession = registered;
        onSessionChanged(registered);
        // An idempotent retry may return an already-created account. Use the
        // server's actual username rather than pretending a new draft was claimed.
        namePromptInput = registered.DisplayName;
        cfg.Save();
        saveLocalDisplayName(registered.DisplayName);
        guestNameConflict = false;
        if (firstLaunch)
        {
            cfg.GuestAccessNoticeSeen = true;
            cfg.Save();
            firstLaunchStep = FirstLaunchStep.Topics;
            SubmitFirstLaunchSetup();
        }
        else guestUpgradePopup = true;
    }

    private void DrawGuestUpgradePopup()
    {
        if (!guestUpgradePopup || showingFirstLaunch) return;
        DrawEventWindow("guestAccessUpgrade", guestNameConflict ? "Choose your account username" : "Your account now has additional access!", UiVec(540, 410), () => {
            if (guestNameConflict)
            {
                ImGui.TextWrapped("Reserve a username to use your profile, friends, messages and Watch Parties. Your previous name may already be taken.");
                ImGui.Spacing(); ImGui.SetNextItemWidth(-1);
                ImGui.InputText("##guestMigrationName", ref guestMigrationName, DisplayNameRules.MaxLength);
                ImGui.TextWrapped(guestRegistrationError);
                return;
            }
            ImGui.TextWrapped("Create your profile and add socials.\n\nAdd friends and send messages.\n\nHost and join Watch Parties.\n\nPlay multiplayer games and discover groups and events.");
            ImGui.Spacing();
            ImGui.TextWrapped("Your login is saved on this device. Verify your character with XIVAuth so you can sign in again if you lose your plugin data or change devices.");
        }, () => guestUpgradePopup = false, () => {
            if (guestNameConflict)
            {
                if (!guestRegistrationBusy && GameLayoutButton("Reserve username", FontAwesomeIcon.Check, Ui(180), true))
                    BeginGuestRegistration(guestMigrationName, false);
            }
            else if (GameLayoutButton("Get started", FontAwesomeIcon.Check, Ui(150), true))
            { Plugin.Cfg.GuestAccessNoticeSeen = true; Plugin.Cfg.Save(); guestUpgradePopup = false; }
        });
    }
}

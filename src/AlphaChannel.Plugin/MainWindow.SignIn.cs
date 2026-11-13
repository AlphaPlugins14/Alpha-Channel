using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Sign-in/link UI for the XIVAuth device flow (see Auth/SignInFlow.cs for the actual network
// orchestration - this file is purely presentation plus the small bit of state needed to poll it
// from Draw() each frame). Reachable from the Settings page's Account section.
internal sealed partial class MainWindow
{
    private enum SignInState
    {
        Idle,
        Requesting,
        AwaitingBrowser,
        Onboarding,
        Succeeded,
        Failed,
    }

    private SignInState signInState = SignInState.Idle;
    private string signInVerificationUri = string.Empty;
    private string signInUserCode = string.Empty;
    private string signInStatusMessage = string.Empty;
    private CancellationTokenSource? signInCts;
    private int signInGeneration;
    private CharacterSession? pendingOnboardingSession;
    private bool firstLaunchSignInRequested;
    private string? guestUpgradeName;

    private LinkedCharacterDto[]? myLinkedCharacters;
    private string displayNameInput = string.Empty;
    private string? lastDisplayNameSyncedFor;
    private string? displayNameError;
    private bool inviteCodeRefreshing;
    private CancellationTokenSource? patreonConnectionCts;
    private int patreonConnectionGeneration;
    private bool patreonConnectionBusy;
    private string? patreonConnectionMessage;
    private bool patreonConnectionMessageIsError;
    private string onboardingNameInput = string.Empty;
    private string? onboardingNameError;
    private bool onboardingNameSubmitting;

    private string? lastProfileSyncedFor;
    private string? profileIconInput;
    private string profileColorInput = "#9966FA";
    private string? profileImageUrl;
    private string profileAvatarPathInput = string.Empty;
    private string? profileAvatarError;
    private bool profileAvatarBusy;

    private readonly PluginFilePicker profileAvatarFileDialog =
        new();
    private string profileBioInput = string.Empty;
    private string profileStatusInput = string.Empty;
    private bool profileSaving;
    private string? profileError;

    private enum ProfileAvatarMode
    {
        Image,
        Icon,
    }

    private ProfileAvatarMode profileAvatarMode = ProfileAvatarMode.Icon;

    internal void NotifySessionRefreshed(
        string? previousAvatarUrl,
        string? refreshedAvatarUrl)
    {
        thumbnails.Invalidate(ResolveAvatarUrl(previousAvatarUrl));
        thumbnails.Invalidate(ResolveAvatarUrl(refreshedAvatarUrl));
        lastDisplayNameSyncedFor = null;
        lastProfileSyncedFor = null;
    }

    private void DrawAccountSettings()
    {
        if (CurrentSession is { IsGuest: true } localAccount)
        {
            ImGui.TextWrapped("Your login is saved on this device. Verify your character with XIVAuth so you can sign in again if you lose your plugin data or change devices.");
            if (ImGui.Button("Verify with XIV Auth")) StartSignIn(localAccount);
            ImGui.Spacing();
        }
        if (CurrentSession is { } session)
        {
            // Sync once per session change so editing isn't reset every frame.
            if (lastDisplayNameSyncedFor != session.AccountId)
            {
                displayNameInput = session.DisplayName;
                lastDisplayNameSyncedFor = session.AccountId;
            }

            var displayNameValid =
                DisplayNameRules.IsValid(displayNameInput);

            DrawOtherSettingsPanel("##accountIdentityPanel", 1f, () =>
            {

                // ---------------------------------------------------------
                // Account card
                // ---------------------------------------------------------

                using (ImRaii.PushStyle(
                    ImGuiStyleVar.ChildRounding,
                    10f))
                using (ImRaii.PushColor(
                    ImGuiCol.ChildBg,
                    CardBg))
                using (var accountCard = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                    "##accountSettingsCard",
                    new Vector2(-1f, Ui(238f)),
                    false,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse)))
                {
                    if (accountCard)
                    {
                        // -------------------------------------------------
                        // Signed-in status
                        // -------------------------------------------------

                        ImGui.SetCursorPos(
                            UiVec(16f, 14f));

                        ImGui.TextColored(
                            Good,
                            SessionValidationInProgress
                                ? "Restoring session…"
                                : "Signed in");

                        ImGui.SetCursorPos(
                            UiVec(16f, 39f));

                        DrawUsername(session.AccountId,session.DisplayName,Vector4.One);

                        // -------------------------------------------------
                        // Username
                        // -------------------------------------------------

                        ImGui.SetCursorPos(
                            UiVec(16f, 70f));

                        using var uiFontScale5295=UiFonts.PushRelative(settingsControlsActive ? .90f : 0.80f);

                        DrawSettingsHelper("Username");

                        uiFontScale5295.Dispose();

                        ImGui.SetCursorPos(
                            UiVec(16f, 91f));

                        ImGui.SetNextItemWidth(
                            ImGui.GetWindowWidth() - 126f);

                        using (displayNameValid
                                   ? default
                                   : ImRaii.PushColor(
                                       ImGuiCol.Text,
                                       Danger))
                        using (ImRaii.PushStyle(
                            ImGuiStyleVar.FrameRounding,
                            8f)
                            .Push(
                                ImGuiStyleVar.FramePadding,
                                UiVec(12f, 8f)))
                        using (ImRaii.PushColor(
                            ImGuiCol.FrameBg,
                            FrameBg)
                            .Push(
                                ImGuiCol.FrameBgHovered,
                                FrameBgHover)
                            .Push(
                                ImGuiCol.FrameBgActive,
                                AccentFieldActive))
                        {
                            ImGui.InputText(
                                "##displayName",
                                ref displayNameInput,
                                DisplayNameRules.MaxLength);
                        }

                        ImGui.SameLine(0f, 8f);

                        using (UiDisabled.Scope(
                            displayNameSaving || !displayNameValid ||
                            displayNameInput.Trim() == session.DisplayName))
                        using (ImRaii.PushStyle(
                            ImGuiStyleVar.FrameRounding,
                            8f))
                        using (ImRaii.PushColor(
                            ImGuiCol.Button,
                            Accent)
                            .Push(
                                ImGuiCol.ButtonHovered,
                                AccentHover)
                            .Push(
                                ImGuiCol.ButtonActive,
                                AccentActive))
                        {
                            if (ImGui.Button(
                                "Save",
                                UiVec(86f, 34f)))
                            {
                                var newName =
                                    displayNameInput.Trim();
                                _ = SaveAccountDisplayNameAsync(session, newName);
                            }
                        }

                        ImGui.SetCursorPos(
                            UiVec(16f, 130f));

                        using var uiFontScale8308=UiFonts.PushRelative(settingsControlsActive ? .90f : 0.76f);

                        DrawSettingsHelper($"{DisplayNameRules.MinLength}-{DisplayNameRules.MaxLength} characters  •  letters, numbers, spaces, _ or -");

                        uiFontScale8308.Dispose();

                        // -------------------------------------------------
                        // Invite code
                        // -------------------------------------------------

                        ImGui.SetCursorPos(
                            UiVec(16f, 158f));

                        using var uiFontScale8924=UiFonts.PushRelative(settingsControlsActive ? .90f : 0.80f);

                        DrawSettingsHelper("Invite code");

                        uiFontScale8924.Dispose();

                        var inviteCodeDisplay =
                            session.InviteCode;

                        ImGui.SetCursorPos(
                            UiVec(16f, 179f));

                        ImGui.SetNextItemWidth(130f);

                        using (ImRaii.PushStyle(
                            ImGuiStyleVar.FrameRounding,
                            8f)
                            .Push(
                                ImGuiStyleVar.FramePadding,
                                UiVec(12f, 8f)))
                        using (ImRaii.PushColor(
                            ImGuiCol.FrameBg,
                            FrameBg))
                        {
                            ImGui.InputText(
                                "##inviteCode",
                                ref inviteCodeDisplay,
                                16,
                                ImGuiInputTextFlags.ReadOnly);
                        }

                        ImGui.SameLine(0f, 8f);

                        using (ImRaii.PushStyle(
                            ImGuiStyleVar.FrameRounding,
                            8f))
                        using (ImRaii.PushColor(
                            ImGuiCol.Button,
                            FrameBg)
                            .Push(
                                ImGuiCol.ButtonHovered,
                                FrameBgHover)
                            .Push(
                                ImGuiCol.ButtonActive,
                                AccentFieldActive))
                        {
                            if (ImGui.Button(
                                "Copy",
                                UiVec(76f, 34f)))
                            {
                                ImGui.SetClipboardText(
                                    session.InviteCode);
                            }
                        }

                        ImGui.SameLine(0f, 8f);

                        using (UiDisabled.Scope(
                            inviteCodeRefreshing))
                        using (ImRaii.PushStyle(
                            ImGuiStyleVar.FrameRounding,
                            8f))
                        using (ImRaii.PushColor(
                            ImGuiCol.Button,
                            FrameBg)
                            .Push(
                                ImGuiCol.ButtonHovered,
                                FrameBgHover)
                            .Push(
                                ImGuiCol.ButtonActive,
                                AccentFieldActive))
                        {
                            if (ImGui.Button(
                                inviteCodeRefreshing
                                    ? "..."
                                    : "Refresh",
                                UiVec(82f, 34f)))
                            {
                                inviteCodeRefreshing =
                                    true;

                                _ = RefreshAccountDetailsAsync(session);
                            }
                        }
                    }
                }

                // ---------------------------------------------------------
                // Errors / warnings
                // ---------------------------------------------------------

                if (session.DisplayName == session.Handle)
                {
                    ImGui.Dummy(
                        UiVec(0f, 7f));

                    ImGui.TextColored(
                        Danger,
                        "Pick a username so friends can find and add you.");
                }

                if (displayNameError is { Length: > 0 } nameError)
                {
                    ImGui.Dummy(
                        UiVec(0f, 6f));

                    ImGui.TextColored(
                        Danger,
                        nameError);
                }

                ImGui.Dummy(
                    UiVec(0f, 8f));

                using var uiFontScale13295=UiFonts.PushRelative(settingsControlsActive ? .90f : 0.78f);

                DrawSettingsHelper("Invite codes rotate automatically after they're redeemed.");

                uiFontScale13295.Dispose();

                ImGui.Dummy(
                    UiVec(0f, 10f));

                // ---------------------------------------------------------
                // Account actions
                // ---------------------------------------------------------

                using (ImRaii.PushStyle(
                    ImGuiStyleVar.FrameRounding,
                    8f))
                using (ImRaii.PushColor(
                    ImGuiCol.Button,
                    FrameBg)
                    .Push(
                        ImGuiCol.ButtonHovered,
                        FrameBgHover)
                    .Push(
                        ImGuiCol.ButtonActive,
                        AccentFieldActive))
                {
                    if (ImGui.Button(
                        "Show linked characters",
                        new Vector2(ImGui.CalcTextSize("Show linked characters").X+Ui(28f),Ui(34f))))
                    {
                        _ = LoadLinkedAccountCharactersAsync(session);
                    }
                }

                ImGui.SameLine(0f, 8f);

                using (ImRaii.PushStyle(
                    ImGuiStyleVar.FrameRounding,
                    8f))
                using (ImRaii.PushColor(
                    ImGuiCol.Button,
                    new Vector4(0.16f, 0.055f, 0.07f, 1f))
                    .Push(
                        ImGuiCol.ButtonHovered,
                        new Vector4(0.22f, 0.07f, 0.09f, 1f))
                    .Push(
                        ImGuiCol.ButtonActive,
                        new Vector4(0.25f, 0.08f, 0.10f, 1f)))
                {
                    if (ImGui.Button(
                        session.IsGuest ? "Verify character" : "Sign out",
                        UiVec(session.IsGuest ? 145f : 94f, 34f)))
                    {
                        if (session.IsGuest) StartSignIn(session);
                        else
                        {
                            CancelPatreonConnection();
                            _ = authClient.RevokeAsync(session.Token);
                            onSessionChanged(null);
                        }
                    }
                    if (session.IsGuest && ImGui.IsItemHovered()) UiFonts.SetTooltip("Verify your character so you can sign in again after changing devices or losing plugin data.");
                }

                // ---------------------------------------------------------
                // Linked characters
                // ---------------------------------------------------------

                DrawLinkedCharacterVisibility(session);
                if (myLinkedCharacters is { } characters)
                {
                    ImGui.Dummy(
                        UiVec(0f, 10f));

                    foreach (var character in characters)
                    {
                        DrawSettingsHelper($"{character.CharacterName} @ {character.World}" +
                            (character.IsPrimary
                                ? "  •  Primary"
                                : string.Empty));
                    }
                }

            }, autoHeight: true);

            DrawOtherSettingsPanel("##accountPatreonPanel", 1f, () =>
            {
                using var wrap = ImRaii.TextWrapPos(0f);
                SettingsSection("Patreon",
                    "Connect Patreon so Alpha Channel can verify your membership and tier.");
                DrawPatreonAccountSettings(session);
            }, autoHeight: true);

            DrawOtherSettingsPanel("##accountLiveStreamingPanel", 1f, () =>
            {
                using var wrap = ImRaii.TextWrapPos(0f);
                SettingsSection("Live streaming",
                    "Manage the secret key used for OBS, gameplay, and local-video broadcasts.");
                DrawStreamKeyAccountSettings(session);
            }, autoHeight: true);
        }
        else
        {
            DrawOtherSettingsPanel("##accountSignInPanel", 1f, () =>
            {
                // ---------------------------------------------------------
                // Signed-out state
                // ---------------------------------------------------------

                DrawSettingsHelper("Create an Alpha Channel account, or sign in with XIV Auth.");
                if (ImGui.Button("Sign up on Alpha Channel"))
                { guestNameConflict = true; guestMigrationName = CurrentDisplayName ?? ""; guestUpgradePopup = true; }
                if (!string.IsNullOrEmpty(guestRegistrationError)) ImGui.TextWrapped(guestRegistrationError);

                ImGui.Dummy(
                    UiVec(0f, 10f));

                var canSignIn =
                    !string.IsNullOrEmpty(CurrentCharacterName) &&
                    !string.IsNullOrEmpty(CurrentWorldName);

                using (UiDisabled.Scope(
                    !canSignIn ||
                    signInState is
                        SignInState.Requesting or
                        SignInState.AwaitingBrowser))
                using (ImRaii.PushStyle(
                    ImGuiStyleVar.FrameRounding,
                    8f))
                using (ImRaii.PushColor(
                    ImGuiCol.Button,
                    Accent)
                    .Push(
                        ImGuiCol.ButtonHovered,
                        AccentHover)
                    .Push(
                        ImGuiCol.ButtonActive,
                        AccentActive))
                {
                    if (ImGui.Button(
                        "Sign in with XIVAuth",
                        UiVec(160f, 36f)))
                    {
                        StartSignIn(
                            linkUsing: null);
                    }
                }

                if (Plugin.Cfg.CharacterSessions.Values
                    .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.AccountId)) is { } existing)
                {
                    ImGui.SameLine(0f, 8f);

                    using (UiDisabled.Scope(
                        signInState is
                            SignInState.Requesting or
                            SignInState.AwaitingBrowser))
                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.FrameRounding,
                        8f))
                    {
                        if (ImGui.Button(
                            $"Link to @{existing.Handle}",
                            UiVec(150f, 36f)))
                        {
                            StartSignIn(
                                existing);
                        }
                    }
                }
            }, autoHeight: true);
        }

        if (signInState == SignInState.Failed &&
            signInStatusMessage is { Length: > 0 } failure)
        {
            DrawOtherSettingsPanel("##accountSignInErrorPanel", 1f, () =>
            {
                using var wrap = ImRaii.TextWrapPos(0f);
                ImGui.TextColored(Danger, failure);
            }, autoHeight: true);
        }
    }

    private void DrawPatreonAccountSettings(CharacterSession session)
    {
        var configuredTier = ConfiguredPatreonTier();
        var patreonLinked = CurrentPatreonLinked();
        using var uiFontScale20373=UiFonts.PushRelative(settingsControlsActive ? .90f : 0.82f);
        ImGui.TextColored(
            configuredTier > 0 ? Good : patreonLinked ? Gold : MutedText,
            configuredTier > 0
                ? $"Patreon membership connected · Tier {configuredTier}"
                : patreonLinked ? "Patreon connected · No active membership tier detected."
                : "No verified Patreon membership is connected.");
        uiFontScale20373.Dispose();

        ImGui.Dummy(UiVec(0f, 8f));

        using (UiDisabled.Scope(patreonConnectionBusy))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 8f))
        using (ImRaii.PushColor(ImGuiCol.Button, PatreonOrange)
                   .Push(ImGuiCol.ButtonHovered, PatreonOrangeHover)
                   .Push(ImGuiCol.ButtonActive, PatreonOrangeHover))
        {
            if (ImGui.Button(
                    patreonConnectionBusy
                        ? "Waiting for Patreon..."
                        : patreonLinked
                            ? "Reconnect Patreon"
                            : "Connect Patreon",
                    UiVec(190f, 36f)))
            {
                StartPatreonConnection(session);
            }
        }

        if (patreonConnectionBusy)
        {
            ImGui.SameLine(0f, Ui(8f));
            if (ImGui.Button("Cancel##patreonConnection", UiVec(84f, 36f)))
            {
                CancelPatreonConnection();
                patreonConnectionMessage = "Patreon connection check cancelled.";
                patreonConnectionMessageIsError = false;
            }
        }

        if (patreonLinked)
        {
            ImGui.Dummy(UiVec(0f, 4f));
            DrawPatreonMembershipRecheck(session);
        }

        if (patreonConnectionMessage is { Length: > 0 } message)
        {
            ImGui.Dummy(UiVec(0f, 8f));
            ImGui.TextColored(
                patreonConnectionMessageIsError ? Danger : MutedText,
                message);
        }

        ImGui.Dummy(UiVec(0f, 6f));
        using var uiFontScale22277=UiFonts.PushRelative(settingsControlsActive ? .90f : 0.76f);
        DrawSettingsHelper("Your Patreon login happens in your browser. Alpha Channel never receives your Patreon password.");
        uiFontScale22277.Dispose();
    }

    private void CancelPatreonConnection()
    {
        lock (patreonStateLock)
        {
            // Invalidate late results as well as cancelling delays and in-flight HTTP.
            Interlocked.Increment(ref patreonConnectionGeneration);
            patreonConnectionCts?.Cancel();
            patreonConnectionCts = null;
            patreonConnectionBusy = false;
            patreonConnectionMessage = null;
            patreonConnectionMessageIsError = false;
        }
    }

    private void StartPatreonConnection(CharacterSession session)
    {
        CancelPatreonConnection();
        var flowCts = new CancellationTokenSource();
        var cancellationToken = flowCts.Token;
        int generation;
        var bearerToken = session.Token;
        lock (patreonStateLock)
        {
            patreonConnectionCts = flowCts;
            generation = Interlocked.Increment(ref patreonConnectionGeneration);
            patreonConnectionBusy = true;
            patreonConnectionMessageIsError = false;
            patreonConnectionMessage = "Opening Patreon...";
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var authorization = await patreonWorkerClient
                    .CreateAuthorizationUriAsync(bearerToken, cancellationToken)
                    .ConfigureAwait(false);
                if (generation != Volatile.Read(ref patreonConnectionGeneration) || cancellationToken.IsCancellationRequested)
                    return;
                
                Dalamud.Utility.Util.OpenLink(authorization.AuthorizationUri.ToString());
                patreonConnectionMessage =
                    "Finish signing in and authorizing in your browser. Checking membership...";

                for (var attempt = 0; attempt < 120; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken)
                        .ConfigureAwait(false);
                    bool connectionComplete;
                    try
                    {
                        connectionComplete = await patreonWorkerClient
                            .IsConnectionCompleteAsync(bearerToken, authorization.FlowId,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (HttpRequestException)
                    {
                        {}
                        continue;
                    }

                    if (!connectionComplete)
                        continue;

                    PatreonEntitlement entitlement;
                    try
                    {
                        entitlement = await patreonWorkerClient
                            .GetEntitlementAsync(bearerToken, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (HttpRequestException)
                    {
                        {}
                        continue;
                    }
                    if (generation != Volatile.Read(ref patreonConnectionGeneration) || cancellationToken.IsCancellationRequested)
                        return;
                    ApplyCompletedPatreonConnection(session.AccountId, entitlement);
                    RequestPendingPatreonBadges(session.AccountId);
                    var localTier = ConfiguredPatreonTier();

                    patreonConnectionMessageIsError = false;
                    patreonConnectionMessage = localTier > 0
                        ? $"Patreon connected successfully · Tier {localTier}."
                        : "Patreon linked. No tier yet; checking every 10 minutes for the next hour.";
                    
                    return;
                }

                if (generation != Volatile.Read(ref patreonConnectionGeneration) || cancellationToken.IsCancellationRequested)
                    return;
                patreonConnectionMessageIsError = true;
                patreonConnectionMessage =
                    "Timed out waiting for Patreon. You can press Connect Patreon to try again.";
                AepLog.Warning(
                    "[Patreon] Timed out waiting for the Worker to link the Patreon account.");
            }
            catch (OperationCanceledException)
            {
                
            }
            catch (Exception)
            {
                if (generation == Volatile.Read(ref patreonConnectionGeneration) && !cancellationToken.IsCancellationRequested)
                {
                    patreonConnectionMessageIsError = true;
                    patreonConnectionMessage =
                        "Patreon authorization failed. Check the plugin log for details.";
                }
                AepLog.Warning(
                    "[Patreon] Connection flow failed.");
            }
            finally
            {
                lock (patreonStateLock)
                {
                    if (generation == Volatile.Read(ref patreonConnectionGeneration))
                    {
                        patreonConnectionBusy = false;
                        if (ReferenceEquals(patreonConnectionCts, flowCts)) patreonConnectionCts = null;
                    }
                    flowCts.Dispose();
                }
            }
        });
    }

    private void DrawStreamKeyAccountSettings(
    CharacterSession session)
    {
        if (DrawAccountBroadcastRestriction("Live")) return;
        var cachedKey =
            Plugin.Cfg.StreamKeys
                .GetValueOrDefault(
                    session.AccountId);

        using var uiFontScale28239=UiFonts.PushRelative(settingsControlsActive ? .90f : 0.82f);

        ImGui.TextColored(
            cachedKey is null
                ? Gold
                : Good,
            cachedKey is null
                ? keyRotating ? "Preparing your secret stream key..."
                    : streamKeyExistsOnServer ? "Your existing stream key isn't saved on this installation."
                    : "Your secret stream key will be generated automatically."
                : "A secret stream key is ready for live streaming.");

        uiFontScale28239.Dispose();

        ImGui.Dummy(
            UiVec(0f, 8f));

        if (cachedKey is { Length: > 0 })
        {
            var displayKey =
                streamKeyRevealed
                    ? cachedKey
                    : new string(
                        '•',
                        Math.Min(
                            cachedKey.Length,
                            32));

            ImGui.SetNextItemWidth(
                -1f);

            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameRounding,
                       8f)
                   .Push(
                       ImGuiStyleVar.FramePadding,
                       UiVec(14f, 10f)))
            using (ImRaii.PushColor(
                       ImGuiCol.FrameBg,
                       FrameBg)
                   .Push(
                       ImGuiCol.FrameBgHovered,
                       FrameBgHover)
                   .Push(
                       ImGuiCol.FrameBgActive,
                       AccentFieldActive))
            {
                ImGui.InputText(
                    "##accountStreamKey",
                    ref displayKey,
                    256,
                    ImGuiInputTextFlags.ReadOnly);
            }

            ImGui.Dummy(
                UiVec(0f, 8f));

            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameRounding,
                       8f))
            {
                if (ImGui.Button(
                        streamKeyRevealed
                            ? "Hide key"
                            : "Reveal key",
                        UiVec(110f, 34f)))
                {
                    streamKeyRevealed =
                        !streamKeyRevealed;
                }

                ImGui.SameLine(
                    0f,
                    8f);

                if (ImGui.Button(
                        "Copy key",
                        UiVec(110f, 34f)))
                {
                    ImGui.SetClipboardText(
                        cachedKey);
                }

                ImGui.SameLine(
                    0f,
                    8f);
            }
        }

        using (UiDisabled.Scope(
                   keyRotating))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   cachedKey is null
                       ? Accent
                       : FrameBg)
               .Push(
                   ImGuiCol.ButtonHovered,
                   cachedKey is null
                       ? AccentHover
                       : FrameBgHover)
               .Push(
                   ImGuiCol.ButtonActive,
                   cachedKey is null
                       ? AccentActive
                       : AccentFieldActive))
        {
            if (ImGui.Button(
                    keyRotating
                        ? "Generating..."
                        : cachedKey is null && !streamKeyExistsOnServer
                            ? "Generate secret key"
                            : "Regenerate secret key",
                    UiVec(174f, 34f)))
            {
                if (cachedKey is null && !streamKeyExistsOnServer)
                {
                    _ = EnsureStreamKeyAsync(session);
                }
                else
                {
                    keyRegenerateConfirmPending =
                        true;
                }
            }
        }

        if (keyRegenerateConfirmPending)
        {
            ImGui.Dummy(
                UiVec(0f, 10f));

            ImGui.TextWrapped(
                "Regenerating this key will invalidate the previous key and disconnect any live-streaming software using it. Continue?");

            ImGui.Dummy(
                UiVec(0f, 8f));

            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameRounding,
                       7f))
            using (ImRaii.PushColor(
                       ImGuiCol.Button,
                       Danger))
            {
                if (ImGui.Button(
                        "Yes, regenerate",
                        UiVec(150f, 34f)))
                {
                    keyRegenerateConfirmPending =
                        false;

                    RotateStreamKey(
                        session);
                }
            }

            ImGui.SameLine(
                0f,
                8f);

            if (ImGui.Button(
                    "Cancel",
                    UiVec(90f, 34f)))
            {
                keyRegenerateConfirmPending =
                    false;
            }
        }

        if (keyError is { Length: > 0 } error)
        {
            ImGui.Dummy(
                UiVec(0f, 8f));

            ImGui.TextColored(
                Danger,
                error);
        }

        ImGui.Dummy(
            UiVec(0f, 6f));

        using var uiFontScale33711=UiFonts.PushRelative(settingsControlsActive ? .90f : 0.76f);

        DrawSettingsHelper("Keep this key private. Anyone with it could publish to your Alpha Channel stream.");

        uiFontScale33711.Dispose();

        ImGui.Dummy(
            UiVec(0f, 10f));

        DrawSettingsExplanation("Copy broadcast diagnostics for stream troubleshooting or Discord support.");

        ImGui.Dummy(
            UiVec(0f, 6f));

        var broadcastDiagnostics =
            video.BroadcastDiagnostics;

        var canCopyBroadcastDiagnostics =
            stream.Mode == StreamMode.Hosting &&
            broadcastDiagnostics.Active &&
            stream.Roster.Length > 0;

        using (UiDisabled.Scope(
                   !canCopyBroadcastDiagnostics))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        {
            if (ImGui.Button(
                    "Copy broadcast diagnostics",
                    UiVec(220f, 34f)))
            {
                ImGui.SetClipboardText(
                    broadcastDiagnostics.ToSanitizedSummary());
            }
        }

        if (!canCopyBroadcastDiagnostics &&
            ImGui.IsItemHovered(
                ImGuiHoveredFlags.AllowWhenDisabled))
        {
            UiFonts.SetTooltip(
                "You must be broadcasting with at least 1 active viewer to copy diagnostics");
        }

    }

    private void DrawProfileEditor(CharacterSession session)
    {
        if (lastProfileSyncedFor != session.AccountId)
        {
            profileIconInput = session.AvatarIcon;
            profileColorInput = session.AvatarColorHex;
            profileImageUrl = session.AvatarImageUrl;
            profileAvatarPathInput = string.Empty;
            profileAvatarError = null;
            profileBioInput = session.Bio ?? string.Empty;
            profileStatusInput = session.StatusMessage ?? string.Empty;

            profileAvatarMode =
                string.IsNullOrEmpty(session.AvatarImageUrl)
                    ? ProfileAvatarMode.Icon
                    : ProfileAvatarMode.Image;

            lastProfileSyncedFor = session.AccountId;
        }

        // =========================================================
        // PROFILE PICTURE
        // =========================================================

        DrawOtherSettingsPanel("##usernameColourSettings",140f,()=>
        {
            DrawSocialHeading("Username colour");
            DrawSettingsExplanation("Your colour and Patreon effect apply throughout Alpha Channel.");
            DrawUsername(session.AccountId,session.DisplayName);
            if(ImGui.Button("Change username colour",UiVec(200f,34f))) OpenAlphaChatSettings();
            if(usernameStyleError is { } error) ImGui.TextWrapped(error);
        },autoHeight:true);
        DrawProfileThemeSettings(session);

        var pictureHeightKey = "##profilePictureCard_" + profileAvatarMode;
        var pictureCardHeight = Ui(settingsPanelMeasuredHeights.GetValueOrDefault(pictureHeightKey,
            profileAvatarMode == ProfileAvatarMode.Image ? 470f : 950f));

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            10f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                UiVec(20f, 18f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            CardBg)
            .Push(
                ImGuiCol.Border,
                SettingsReviewBorder))
        using (var pictureCard = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
            "##profilePictureCard",
            new Vector2(-1f, pictureCardHeight),
            true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (pictureCard)
            {
                // -----------------------------------------------------
                // Heading
                // -----------------------------------------------------

                using var uiFontScale37874=UiFonts.PushRelative(1.10f);

                DrawSocialHeading("Profile picture",1.1f);

                uiFontScale37874.Dispose();

                ImGui.Dummy(UiVec(0f, 3f));

                DrawSettingsHelper("Choose an image or a styled icon.");

                ImGui.Dummy(UiVec(0f, 8f));

                // -----------------------------------------------------
                // Image / Icon selector
                // -----------------------------------------------------

                var selectorWidth = Ui(132f);
                var selectorHeight = Ui(38f);

                DrawProfileModeButton(
                    ProfileAvatarMode.Image,
                    FontAwesomeIcon.Image,
                    "Image",
                    selectorWidth,
                    selectorHeight);

                ImGui.SameLine(0f, 10f);

                DrawProfileModeButton(
                    ProfileAvatarMode.Icon,
                    FontAwesomeIcon.Smile,
                    "Icon",
                    selectorWidth,
                    selectorHeight);

                ImGui.Dummy(UiVec(0f, 8f));

                // -----------------------------------------------------
                // Divider
                // -----------------------------------------------------

                var dividerOrigin =
                    ImGui.GetCursorScreenPos();

                var dividerWidth =
                    ImGui.GetContentRegionAvail().X;

                ImGui.GetWindowDrawList()
                    .AddRectFilled(
                        dividerOrigin,
                        dividerOrigin +
                        new Vector2(dividerWidth, 1f),
                        ImGui.GetColorU32(SettingsReviewBorder));

                ImGui.Dummy(
                    new Vector2(dividerWidth, Ui(16f)));

                // =====================================================
                // IMAGE MODE
                // =====================================================

                if (profileAvatarMode == ProfileAvatarMode.Image)
                {
                    DrawAvatarChip(
                        profileIconInput,
                        profileColorInput,
                        96,
                        profileImageUrl);

                    ImGui.SameLine(0f, 18f);

                    ImGui.BeginGroup();

                    using var uiFontScale40428=UiFonts.PushRelative(1.05f);

                    DrawSocialHeading("Current image",1.1f);

                    uiFontScale40428.Dispose();

                    ImGui.Dummy(UiVec(0f, 5f));

                    if (profileImageUrl is { Length: > 0 })
                    {
                        DrawSettingsHelper("Custom profile image");

                        ImGui.Dummy(UiVec(0f, 7f));

                        ImGui.TextColored(
                            Good,
                            "Custom picture active.");
                    }
                    else
                    {
                        DrawSettingsHelper("No custom image selected.");

                        ImGui.Dummy(UiVec(0f, 7f));

                        DrawSettingsHelper("Upload an image below to use one.");
                    }

                    ImGui.EndGroup();

                    ImGui.Dummy(UiVec(0f, 8f));

                    // -------------------------------------------------
                    // Image actions
                    // -------------------------------------------------

                    var actionGap = Ui(10f);
                    var horizontalInset = Ui(8f);

                    var fullActionWidth =
                        ImGui.GetContentRegionAvail().X;

                    var actionWidth =
                        (fullActionWidth -
                         (horizontalInset * 2f) -
                         actionGap) / 2f;

                    ImGui.SetCursorPosX(
                        ImGui.GetCursorPosX() +
                        horizontalInset);

                    using (UiDisabled.Scope(profileAvatarBusy))
                    {
                        if (DrawProfileActionButton(
                            FontAwesomeIcon.Upload,
                            "Upload image",
                            "Choose a file",
                            Hex(0x38BDF8),
                            width: actionWidth))
                        {
                            profileAvatarError = null;

                            profileAvatarFileDialog.OpenFileDialog(
                                "Select Profile Picture",
                                ".png,.jpg,.jpeg,.webp",
                                (success, path) =>
                                {
                                    if (!success ||
                                        string.IsNullOrWhiteSpace(path))
                                    {
                                        return;
                                    }

                                    profileAvatarPathInput = path;

                                    UploadProfileAvatar(
                                        session,
                                        path);
                                });
                        }
                        ImGui.SameLine(0f, actionGap);

                        if (DrawProfileActionButton(
                            FontAwesomeIcon.Trash,
                            "Remove image",
                            "Revert to icon",
                            Hex(0xF87171),
                            disabled:
                                string.IsNullOrEmpty(profileImageUrl),
                            width: actionWidth))
                        {
                            ClearProfileAvatar(session);

                            profileAvatarMode =
                                ProfileAvatarMode.Icon;
                        }
                    }


                    ImGui.Dummy(UiVec(0f, 8f));

                    using var uiFontScale44447=UiFonts.PushRelative(settingsControlsActive ? .90f : 0.78f);

                    DrawSettingsHelper("Recommended: square PNG, JPG or WebP. Max 1 MB.");

                    uiFontScale44447.Dispose();

                    if (profileAvatarError is { Length: > 0 } avatarError)
                    {
                        ImGui.Dummy(UiVec(0f, 7f));

                        ImGui.TextColored(
                            Danger,
                            avatarError);
                    }
                }

                // =====================================================
                // ICON MODE
                // =====================================================

                else
                {
                    DrawAvatarChip(
                        profileIconInput,
                        profileColorInput,
                        96,
                        null);

                    ImGui.SameLine(0f, 18f);

                    ImGui.BeginGroup();

                    using var uiFontScale45512=UiFonts.PushRelative(1.05f);

                    DrawSocialHeading("Styled icon",1.1f);

                    uiFontScale45512.Dispose();

                    ImGui.Dummy(UiVec(0f, 5f));

                    DrawSettingsHelper("Choose an icon and colour for your avatar.");

                    if (profileImageUrl is { Length: > 0 })
                    {
                        ImGui.Dummy(UiVec(0f, 7f));

                        DrawSettingsHelper("Your custom image is currently active.");

                        ImGui.Dummy(UiVec(0f, 8f));

                        using (UiDisabled.Scope(profileAvatarBusy))
                        using (ImRaii.PushStyle(
                            ImGuiStyleVar.FrameRounding,
                            7f))
                        using (ImRaii.PushColor(
                            ImGuiCol.Button,
                            FrameBg)
                            .Push(
                                ImGuiCol.ButtonHovered,
                                FrameBgHover))
                        {
                            if (ImGui.Button(
                                "Use icon instead",
                                UiVec(130f, 30f)))
                            {
                                ClearProfileAvatar(session);
                            }
                        }
                    }

                    ImGui.EndGroup();

                    ImGui.Dummy(UiVec(0f, 8f));

                    // -------------------------------------------------
                    // Icon picker
                    // -------------------------------------------------

                    ImGui.TextColored(
                        Vector4.One,
                        "Icon");

                    ImGui.Dummy(UiVec(0f, 7f));

                    using (ImRaii.PushStyle(
    ImGuiStyleVar.ChildRounding,
    8f)
    .Push(
        ImGuiStyleVar.WindowPadding,
        UiVec(12f, 12f)))
                    using (ImRaii.PushColor(
                        ImGuiCol.ChildBg,
                        CardBg)
                        .Push(
                            ImGuiCol.Border,
                            SettingsReviewBorder))
                    using (var iconChild = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                        "##profileIconPicker",
                        new Vector2(-1f, Ui(178f)),
                        true,
                        ImGuiWindowFlags.NoScrollbar |
                        ImGuiWindowFlags.NoScrollWithMouse)))
                    {
                        if (iconChild)
                        {
                            DrawIconPicker(
                                ref profileIconInput);
                        }
                    }

                    ImGui.Dummy(UiVec(0f, 8f));

                    // -------------------------------------------------
                    // Color
                    // -------------------------------------------------

                    ImGui.TextColored(
                        Vector4.One,
                        "Colour");

                    ImGui.Dummy(UiVec(0f, 7f));

                    // Give the colour controls their own padded area.
                    using (ImRaii.PushStyle(
    ImGuiStyleVar.ChildRounding,
    8f)
    .Push(
        ImGuiStyleVar.WindowPadding,
        UiVec(14f, 14f)))
                    using (ImRaii.PushColor(
                        ImGuiCol.ChildBg,
                        CardBg)
                        .Push(
                            ImGuiCol.Border,
                            SettingsReviewBorder))
                    using (var colorChild = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                        "##profileColorPicker",
                        new Vector2(-1f, Ui(78f)),
                        true,
                        ImGuiWindowFlags.NoScrollbar |
                        ImGuiWindowFlags.NoScrollWithMouse)))
                    {
                        if (colorChild)
                        {
                            DrawColorPicker(
                                ref profileColorInput);
                        }
                    }

                    ImGui.Dummy(UiVec(0f, 10f));
                    DrawSettingsHelper("Save to apply changes");
                    ImGui.Dummy(UiVec(0f, 6f));
                    using (UiDisabled.Scope(profileSaving || profileAvatarBusy))
                    using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 8f))
                    using (ImRaii.PushColor(ImGuiCol.Button, Accent)
                        .Push(ImGuiCol.ButtonHovered, AccentHover)
                        .Push(ImGuiCol.ButtonActive, AccentActive))
                    {
                        if (ImGui.Button(profileSaving ? "Saving...##saveAvatar" : "Save##saveAvatar", UiVec(138f, 36f)))
                            SaveProfile(session);
                    }
                }
                settingsPanelMeasuredHeights[pictureHeightKey] = ImGui.GetCursorPosY() / Ui(1f) + 18f;
            }
        }

        ImGui.Dummy(UiVec(0f, 8f));

        // =========================================================
        // ABOUT YOU
        // =========================================================

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            10f)
            .Push(
                ImGuiStyleVar.WindowPadding,
                UiVec(20f, 18f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            CardBg)
            .Push(
                ImGuiCol.Border,
                SettingsReviewBorder))
        using (var aboutCard = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
            "##profileAboutCard",
            new Vector2(-1f, Ui(settingsPanelMeasuredHeights.GetValueOrDefault("##profileAboutCard",565f))),
                    true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (aboutCard)
            {
                using var uiFontScale51732=UiFonts.PushRelative(1.10f);

                DrawSocialHeading("About you",1.1f);

                uiFontScale51732.Dispose();

                ImGui.Dummy(UiVec(0f, 2f));

                DrawSettingsHelper("Let others know a bit about you.");

                ImGui.Dummy(UiVec(0f, 10f));

                // -----------------------------------------------------
                // Status
                // -----------------------------------------------------

                ImGui.TextColored(
                    Vector4.One,
                    "Status");

                ImGui.SameLine();

                using var uiFontScale52461=UiFonts.PushRelative(settingsControlsActive ? .90f : 0.76f);

                DrawSettingsHelper("Short message shown beside your name.");

                uiFontScale52461.Dispose();

                ImGui.Dummy(UiVec(0f, 5f));

                ImGui.SetNextItemWidth(-1f);

                using (ImRaii.PushStyle(
                    ImGuiStyleVar.FrameRounding,
                    8f)
                    .Push(
                        ImGuiStyleVar.FramePadding,
                        UiVec(12f, 9f)))
                using (ImRaii.PushColor(
                    ImGuiCol.FrameBg,
                    FrameBg)
                    .Push(
                        ImGuiCol.FrameBgHovered,
                        FrameBgHover)
                    .Push(
                        ImGuiCol.FrameBgActive,
                        AccentFieldActive))
                {
                    ImGui.InputTextWithHint(
                        "##status",
                        "What are you up to?",
                        ref profileStatusInput,
                        64);
                }

                using var uiFontScale53610=UiFonts.PushRelative(settingsControlsActive ? .90f : 0.72f);

                var statusCounter =
                    $"{profileStatusInput.Length} / 64";

                var statusCounterWidth =
                    ImGui.CalcTextSize(statusCounter).X;

                ImGui.SetCursorPosX(
                    ImGui.GetWindowWidth() -
                    statusCounterWidth -
                    22f);

                DrawSettingsHelper(statusCounter);

                uiFontScale53610.Dispose();

                ImGui.Dummy(UiVec(0f, 9f));

                // -----------------------------------------------------
                // Bio
                // -----------------------------------------------------

                ImGui.TextColored(
                    Vector4.One,
                    "Bio");

                ImGui.SameLine();

                using var uiFontScale54511=UiFonts.PushRelative(settingsControlsActive ? .90f : 0.76f);

                DrawSettingsHelper("A little about yourself. Shown on your profile.");

                uiFontScale54511.Dispose();

                ImGui.Dummy(UiVec(0f, 5f));

                using (ImRaii.PushStyle(
                    ImGuiStyleVar.FrameRounding,
                    8f)
                    .Push(
                        ImGuiStyleVar.FramePadding,
                        UiVec(12f, 10f)))
                using (ImRaii.PushColor(
                    ImGuiCol.FrameBg,
                    FrameBg)
                    .Push(
                        ImGuiCol.FrameBgHovered,
                        FrameBgHover)
                    .Push(
                        ImGuiCol.FrameBgActive,
                        AccentFieldActive))
                {
                    ImGui.InputTextMultiline(
                        "##bio",
                        ref profileBioInput,
                        160,
                        new Vector2(-1f, Ui(145f)));
                }

                using var uiFontScale55624=UiFonts.PushRelative(settingsControlsActive ? .90f : 0.72f);

                var bioCounter =
                    $"{profileBioInput.Length} / 160";

                var bioCounterWidth =
                    ImGui.CalcTextSize(bioCounter).X;

                ImGui.SetCursorPosX(
                    ImGui.GetWindowWidth() -
                    bioCounterWidth -
                    22f);

                DrawSettingsHelper(bioCounter);

                uiFontScale55624.Dispose();

                ImGui.Dummy(
                    UiVec(0f, 12f));

                using (ImRaii.PushStyle(
                           ImGuiStyleVar.FrameRounding,
                           8f))
                using (ImRaii.PushColor(
                           ImGuiCol.Button,
                           FrameBg)
                           .Push(
                               ImGuiCol.ButtonHovered,
                               CardBgHover)
                           .Push(
                               ImGuiCol.ButtonActive,
                               AccentActive))
                {
                    if (ImGui.Button(
                            "Preview my profile",
                            UiVec(-1f, 36f)))
                    {
                        OpenProfilePage(
                            session,
                            session.AccountId,
                            session.DisplayName);
                    }
                }

                if (ImGui.IsItemHovered())
                {
                    UiFonts.SetTooltip(
                        "Be sure to save any changes before previewing");
                }
                ImGui.Dummy(UiVec(0,12));
                DrawHistoryPrivacy(session);
                if (ProfileFriendsEnabled)
                {
                    ImGui.Dummy(UiVec(0,12));
                    DrawFriendsPrivacy(session);
                }
                ImGui.Dummy(UiVec(0,12));
                DrawSocialsPrivacy(session);
                settingsPanelMeasuredHeights["##profileAboutCard"]=ImGui.GetCursorPosY()/Ui(1f)+18f;
            }
        }

        ImGui.Dummy(UiVec(0f, 8f));

        // =========================================================
        // SAVE PROFILE
        // =========================================================

        var saveWidth = Ui(138f);

        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() +
            ImGui.GetContentRegionAvail().X -
            saveWidth);

        using (UiDisabled.Scope(profileSaving))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            Accent)
            .Push(
                ImGuiCol.ButtonHovered,
                AccentHover)
            .Push(
                ImGuiCol.ButtonActive,
                AccentActive))
        {
            if (ImGui.Button(
                profileSaving
                    ? "Saving..."
                    : "Save profile",
                new Vector2(
                    saveWidth,
                    Ui(40f))))
            {
                SaveProfile(session);
            }
        }

        if (profileError is { Length: > 0 } error)
        {
            ImGui.Dummy(UiVec(0f, 8f));

            ImGui.TextColored(
                Danger,
                error);
        }
    }

    private void DrawProfileModeButton(
    ProfileAvatarMode mode,
    FontAwesomeIcon icon,
    string label,
    float width,
    float height)
    {
        var selected =
            profileAvatarMode == mode;

        var origin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(width, height);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            selected
                ? Accent
                : FrameBg)
            .Push(
                ImGuiCol.ButtonHovered,
                selected
                    ? AccentHover
                    : FrameBgHover)
            .Push(
                ImGuiCol.ButtonActive,
                selected
                    ? AccentActive
                    : AccentFieldActive)
            .Push(
                ImGuiCol.Text,
                new Vector4(0f, 0f, 0f, 0f)))
        {
            if (ImGui.Button(
                $"##profileMode_{mode}",
                size))
            {
                profileAvatarMode = mode;
            }
        }

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                selected
                    ? Accent
                    : SettingsReviewBorder),
            8f,
            ImDrawFlags.None,
            selected
                ? 1.5f
                : 1f);

        var glyph =
            icon.ToIconString();

        Vector2 glyphSize;

        using (UiFonts.PushIcon())
        {
            glyphSize =
                ImGui.CalcTextSize(glyph);
        }

        var labelSize =
            ImGui.CalcTextSize(label);

        var gap = NativeUi(8f);

        var totalWidth =
            glyphSize.X +
            gap +
            labelSize.X;

        var contentX =
            origin.X +
            (size.X - totalWidth) * 0.5f;

        var glyphY =
            origin.Y +
            (size.Y - glyphSize.Y) * 0.5f;

        var labelY =
            origin.Y +
            (size.Y - labelSize.Y) * 0.5f;

        using (UiFonts.PushIcon())
        {
            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                    contentX,
                    glyphY), ImGui.GetColorU32(
                    selected
                        ? Vector4.One
                        : MutedText), glyph);
        }

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                contentX +
                glyphSize.X +
                gap,
                labelY), ImGui.GetColorU32(
                selected
                    ? Vector4.One
                    : MutedText), label);
    }

    // Same tile language as theme / background swatches — icon disc + title + muted subtitle.
    private static bool DrawProfileActionButton(
    FontAwesomeIcon icon,
    string title,
    string subtitle,
    Vector4 color,
    bool disabled = false,
    float width = 128f)
    {
        var size =
            new Vector2(
                width,
                Ui(50f));

        var origin =
            ImGui.GetCursorScreenPos();

        var drawList =
            ImGui.GetWindowDrawList();

        ImGui.PushID(title);

        var clicked = false;

        using (UiDisabled.Scope(disabled))
        {
            clicked =
                ImGui.InvisibleButton(
                    "##profileAction",
                    size);
        }

        var hovered =
            ImGui.IsItemHovered();

        ImGui.PopID();

        var fill =
            disabled
                ? new Vector4(
                    CardBg.X,
                    CardBg.Y,
                    CardBg.Z,
                    CardBg.W * 0.55f)
                : hovered
                    ? CardBgHover
                    : CardBg;

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(fill),
            9f);

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                hovered && !disabled
                    ? new Vector4(
                        color.X,
                        color.Y,
                        color.Z,
                        0.45f)
                    : SettingsReviewBorder),
            9f,
            ImDrawFlags.None,
            1f);

        var disc = NativeUi(30f);

        var discOrigin =
            origin +
            new Vector2(
                Ui(10f),
                (size.Y - disc) * 0.5f);

        drawList.AddRectFilled(
            discOrigin,
            discOrigin +
            new Vector2(disc, disc),
            ImGui.GetColorU32(
                new Vector4(
                    color.X,
                    color.Y,
                    color.Z,
                    disabled
                        ? 0.10f
                        : 0.20f)),
            8f);

        using (UiFonts.PushIcon())
        {
            var glyph =
                icon.ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), discOrigin +
                new Vector2(disc, disc) * 0.5f -
                glyphSize * 0.5f, ImGui.GetColorU32(
                    disabled
                        ? new Vector4(
                            color.X,
                            color.Y,
                            color.Z,
                            0.35f)
                        : color), glyph);
        }

        var titleColor =
            disabled
                ? new Vector4(
                    1f,
                    1f,
                    1f,
                    0.35f)
                : Vector4.One;

        var subtitleColor =
            disabled
                ? new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.35f)
                : MutedText;

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), origin +
            UiVec(50f, 9f), ImGui.GetColorU32(
                titleColor), title);

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), origin +
            UiVec(50f, 27f), ImGui.GetColorU32(
                subtitleColor), subtitle);

        return clicked &&
               !disabled;
    }


    private void ApplyAvatarSummary(CharacterSession session, AccountSummary updated)
    {
        // Same /avatars/{id}.ext URL after a replace — drop the cached GPU texture so the new bytes load.
        thumbnails.Invalidate(ResolveAvatarUrl(session.AvatarImageUrl));
        thumbnails.Invalidate(ResolveAvatarUrl(updated.AvatarImageUrl));

        session.AvatarIcon = updated.AvatarIcon;
        session.AvatarColorHex = updated.AvatarColorHex;
        session.AvatarImageUrl = updated.AvatarImageUrl;
        session.Bio = updated.Bio;
        session.StatusMessage = updated.StatusMessage;
        profileIconInput = updated.AvatarIcon;
        profileColorInput = updated.AvatarColorHex;
        profileImageUrl = updated.AvatarImageUrl;
        onSessionChanged(session);
        characterDirectoryNextSync = DateTime.MinValue;
    }

    private void UploadProfileAvatar(CharacterSession session, string rawPath)
    {
        var path = rawPath.Trim().Trim('"');
        if (path.Length == 0 || !File.Exists(path))
        {
            profileAvatarError = "Pick an existing png, jpg, or webp file.";
            return;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp"))
        {
            profileAvatarError = "Use a png, jpg, or webp image.";
            return;
        }

        var info = new FileInfo(path);
        if (info.Length > 1024 * 1024)
        {
            profileAvatarError = "Keep it under 1 MB.";
            return;
        }

        profileAvatarBusy = true;
        profileAvatarError = null;
        var token = session.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var updated = await authClient.UploadAvatarAsync(token, path);
                if (!ReferenceEquals(CurrentSession, session)) return;
                if (updated is null)
                    profileAvatarError = "Couldn't upload that picture.";
                else
                    ApplyAvatarSummary(session, updated);
            }
            catch (Exception)
            {
                AepLog.Warning("[Profile] Avatar upload failed.");
                if (ReferenceEquals(CurrentSession, session))
                    profileAvatarError = "Couldn't upload that picture.";
            }
            finally
            {
                if (ReferenceEquals(CurrentSession, session)) profileAvatarBusy = false;
            }
        });
    }

    private void ClearProfileAvatar(CharacterSession session)
    {
        profileAvatarBusy = true;
        profileAvatarError = null;
        var token = session.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var updated = await authClient.ClearAvatarAsync(token);
                if (!ReferenceEquals(CurrentSession, session)) return;
                if (updated is null)
                    profileAvatarError = "Couldn't remove the picture.";
                else
                    ApplyAvatarSummary(session, updated);
            }
            catch (Exception)
            {
                AepLog.Warning("[Profile] Avatar removal failed.");
                if (ReferenceEquals(CurrentSession, session))
                    profileAvatarError = "Couldn't remove the picture.";
            }
            finally
            {
                if (ReferenceEquals(CurrentSession, session)) profileAvatarBusy = false;
            }
        });
    }

    private void SaveProfile(CharacterSession session)
    {
        var token = session.Token;
        var request = new UpdateProfileRequest(null, profileIconInput, profileColorInput, profileBioInput, profileStatusInput);

        profileSaving = true;
        profileError = null;
        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await authClient.UpdateProfileAsync(token, request);
                if (!ReferenceEquals(CurrentSession, session)) return;
                if (outcome.Account is { } updated)
                    ApplyAvatarSummary(session, updated);
                else
                    profileError = "Couldn't save your profile.";
            }
            catch (Exception)
            {
                AepLog.Warning("[Profile] Save failed.");
                if (ReferenceEquals(CurrentSession, session))
                    profileError = "Couldn't save your profile.";
            }
            finally
            {
                if (ReferenceEquals(CurrentSession, session)) profileSaving = false;
            }
        });
    }

    private void StartSignIn(CharacterSession? linkUsing, bool fromFirstLaunch = false)
    {
        linkUsing ??= CurrentSession is { IsGuest: true } guest ? guest : null;
        var characterName = CurrentCharacterName;
        var world = CurrentWorldName;
        if (string.IsNullOrEmpty(characterName) || string.IsNullOrEmpty(world))
        {
            return;
        }

        signInCts?.Cancel();
        var requestCts = new CancellationTokenSource();
        signInCts = requestCts;
        var cancellationToken = requestCts.Token;
        var generation = Interlocked.Increment(ref signInGeneration);

        signInState = SignInState.Requesting;
        signInVerificationUri = string.Empty;
        signInUserCode = string.Empty;
        signInBrowserOpenError = null;
        signInLinkCopiedUntil = signInCodeCopiedUntil = DateTime.MinValue;
        signInStatusMessage = string.Empty;
        pendingOnboardingSession = null;
        onboardingNameSubmitting = false;
        firstLaunchSignInRequested = fromFirstLaunch;
        guestUpgradeName = !fromFirstLaunch && linkUsing is null && CurrentSession is null &&
            Plugin.Cfg.HasCompletedFirstLaunch ? CurrentDisplayName : null;

        var isLalafell = CurrentIsLalafell;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await signInFlow.RunAsync(characterName, world, isLalafell, linkUsing?.Token, start =>
                {
                    if (generation != Volatile.Read(ref signInGeneration) || cancellationToken.IsCancellationRequested) return;
                    signInVerificationUri = start.VerificationUriComplete ?? start.VerificationUri;
                    signInUserCode = start.UserCode;
                    signInState = SignInState.AwaitingBrowser;
                }, cancellationToken);

                if (generation != Volatile.Read(ref signInGeneration) || cancellationToken.IsCancellationRequested)
                    return;

                switch (result.Outcome)
                {
                case SignInOutcome.Success when result.IsNewAccount:
                    if (result.Session is { } registeredSession) RecordAdminRegistration(registeredSession);
                    // Keep the new session pending until its public username has
                    // been reserved. A guest can first try their local username.
                    pendingOnboardingSession = result.Session;
                    onboardingNameInput = guestUpgradeName ?? string.Empty;
                    onboardingNameError = null;
                    onboardingNameSubmitting = false;
                    if (fromFirstLaunch)
                    {
                        namePromptInput = string.Empty;
                        firstLaunchStep = FirstLaunchStep.Username;
                        signInState = SignInState.Succeeded;
                    }
                    else
                    {
                        signInState = SignInState.Onboarding;
                        if (guestUpgradeName is { Length: > 0 } &&
                            DisplayNameRules.IsValid(guestUpgradeName))
                            SubmitOnboarding();
                    }
                    break;
                case SignInOutcome.Success:
                    if (fromFirstLaunch)
                    {
                        if (result.Session is not { } existingAccount)
                        {
                            signInStatusMessage = "XIVAuth did not return an account. Please try again.";
                            signInState = SignInState.Failed;
                            break;
                        }

                        // A reinstall should use the account's already reserved
                        // username. Only new accounts need the username step.
                        onSessionChanged(existingAccount);
                        saveLocalDisplayName(existingAccount.DisplayName);
                        firstLaunchStep = FirstLaunchStep.Topics;
                    }
                    else if (guestUpgradeName is { Length: > 0 })
                    {
                        pendingOnboardingSession = result.Session;
                        onboardingNameInput = guestUpgradeName;
                        onboardingNameError = null;
                        onboardingNameSubmitting = false;
                        signInState = SignInState.Onboarding;
                        if (DisplayNameRules.IsValid(guestUpgradeName)) SubmitOnboarding();
                        break;
                    }
                    else
                    {
                        onSessionChanged(result.Session);
                    }
                    signInState = SignInState.Succeeded;
                    break;
                case SignInOutcome.Cancelled:
                    signInState = SignInState.Idle;
                    break;
                default:
                    signInStatusMessage = result.Message ?? "Sign-in failed.";
                    signInState = SignInState.Failed;
                    break;
                }
            }
            catch (Exception)
            {
                AepLog.Warning("[Auth] Sign-in flow failed.");
                if (generation == Volatile.Read(ref signInGeneration) && !cancellationToken.IsCancellationRequested)
                {
                    signInStatusMessage = "Sign-in failed. Please try again.";
                    signInState = SignInState.Failed;
                }
            }
            finally
            {
                if (ReferenceEquals(signInCts, requestCts)) signInCts = null;
                requestCts.Dispose();
            }
        });
    }

    private void SubmitOnboarding()
    {
        if (pendingOnboardingSession is not { } session || onboardingNameSubmitting)
        {
            return;
        }

        var name = onboardingNameInput.Trim();
        if (!DisplayNameRules.IsValid(name))
        {
            onboardingNameError = "Pick a username so friends can find you.";
            return;
        }

        onboardingNameError = null;
        onboardingNameSubmitting = true;
        var generation = Volatile.Read(ref signInGeneration);

        _ = Task.Run(async () =>
        {
            try
            {
                // Friend search uses this server-reserved display name.
                var outcome = await authClient.UpdateDisplayNameAsync(session.Token, name);
                if (generation != Volatile.Read(ref signInGeneration) ||
                    !ReferenceEquals(pendingOnboardingSession, session)) return;
                if (outcome.Account is not { } updated)
                {
                    onboardingNameError = outcome.NameTaken
                        ? guestUpgradeName is { Length: > 0 } &&
                          string.Equals(name, guestUpgradeName, StringComparison.OrdinalIgnoreCase)
                            ? $"Your previous username {guestUpgradeName} was not available. Please choose another."
                            : "That username is already taken. Please choose another."
                        : outcome.InvalidFormat
                            ? "That name doesn't fit the rules above."
                            : "Couldn't save that name, try again.";
                    return;
                }

                session.DisplayName = updated.DisplayName;
                onSessionChanged(session);
                saveLocalDisplayName(session.DisplayName);
                signInStatusMessage = guestUpgradeName is { Length: > 0 }
                    ? $"Your username {session.DisplayName} is now tied to your XIVAuth sign-in and can be used for friends to find you."
                    : $"Your account username is {session.DisplayName}.";
                onboardingNameSubmitting = false;
                pendingOnboardingSession = null;
                signInState = SignInState.Succeeded;
            }
            catch (Exception)
            {
                AepLog.Warning("[Auth] Username reservation failed.");
                if (generation == Volatile.Read(ref signInGeneration) &&
                    ReferenceEquals(pendingOnboardingSession, session))
                    onboardingNameError = "Couldn't save that name, try again.";
            }
            finally
            {
                if (generation == Volatile.Read(ref signInGeneration) &&
                    ReferenceEquals(pendingOnboardingSession, session))
                    onboardingNameSubmitting = false;
            }
        });
    }

    private void DrawSignInOverlay(bool showCloseButton = true)
    {
        if (signInState == SignInState.Idle) return;
        if (signInState == SignInState.Succeeded && firstLaunchSignInRequested)
        {
            firstLaunchSignInRequested = false;
            signInState = SignInState.Idle;
            return;
        }

        var parentPosition = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var panelWidth = MathF.Min(Ui(500f), parentSize.X - Ui(32f));
        var desiredHeight = signInState switch
        {
            SignInState.AwaitingBrowser => Ui(430f),
            SignInState.Onboarding => Ui(360f),
            SignInState.Requesting => Ui(200f),
            _ => Ui(260f)
        };
        var panelHeight = MathF.Min(desiredHeight, parentSize.Y - Ui(32f));
        var panelPosition = parentPosition + (parentSize - new Vector2(panelWidth, panelHeight)) * 0.5f;
        ImGui.SetNextWindowPos(parentPosition, ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        const ImGuiWindowFlags overlayFlags = ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##xivAuthSignInOverlay", overlayFlags))
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
            .Push(ImGuiStyleVar.ItemSpacing, UiVec(8f, 9f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
            .Push(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.82f)))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##xivAuthSignInPanel", new Vector2(panelWidth, panelHeight), true)))
        {
            if (panel)
            {
                var header = ImGui.GetCursorScreenPos();
                UiFonts.DrawCenteredIcon(ImGui.GetWindowDrawList(), Ui(27), header + UiVec(14, 14),
                    ImGui.GetColorU32(Accent), FontAwesomeIcon.CheckCircle.ToIconString());
                ImGui.SetCursorScreenPos(header + UiVec(40, 2));
                using (UiFonts.PushRelative(1.30f, emphasis: true))
                    ImGui.TextColored(Vector4.One, "Sign in with XIVAuth");
                ImGui.SetCursorScreenPos(header + UiVec(0, 42));
                DrawSignInOverlayContents();
            }
        }
        ImGui.End();
        if (showCloseButton) DrawSplashCloseButton();
    }

    private void DrawSignInOverlayContents()
    {
        switch (signInState)
        {
            case SignInState.Requesting:
                ImGui.TextWrapped("Starting sign-in...");
                break;

            case SignInState.AwaitingBrowser:
                DrawXivAuthBrowserPrompt();
                break;

            case SignInState.Onboarding:
                ImGui.TextColored(Accent, "Choose your account username");
                ImGui.Spacing();
                ImGui.TextWrapped(guestUpgradeName is { Length: > 0 }
                    ? $"We'll try to use your previous username {guestUpgradeName}. If it is taken, choose another below."
                    : "Friends will use this username to find you.");
                var onboardingNameValid = DisplayNameRules.IsValid(onboardingNameInput);
                ImGui.SetNextItemWidth(-1f);
                using (UiDisabled.Scope(onboardingNameSubmitting))
                using (onboardingNameValid || onboardingNameInput.Length == 0 ? default : ImRaii.PushColor(ImGuiCol.Text, Danger))
                {
                    ImGui.InputText("##onboardingName", ref onboardingNameInput, DisplayNameRules.MaxLength);
                }

                using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
                    ImGui.TextWrapped($"{DisplayNameRules.MinLength}-{DisplayNameRules.MaxLength} characters: letters, numbers, single spaces, _ or -.");
                if (onboardingNameError is { Length: > 0 } nameOnboardError)
                {
                    ImGui.TextColored(Danger, nameOnboardError);
                }

                ImGui.Spacing();
                using (UiDisabled.Scope(!onboardingNameValid || onboardingNameSubmitting))
                {
                    if (ImGui.Button(onboardingNameSubmitting ? "Saving..." : "Done",
                            new Vector2(ImGui.GetContentRegionAvail().X, Ui(38f))))
                    {
                        SubmitOnboarding();
                    }
                }

                break;

            case SignInState.Succeeded:
                if (firstLaunchSignInRequested)
                {
                    firstLaunchSignInRequested = false;
                    signInState = SignInState.Idle;
                    break;
                }
                ImGui.TextColored(Good, "Signed in!");
                if (!string.IsNullOrWhiteSpace(signInStatusMessage))
                    ImGui.TextWrapped(signInStatusMessage);
                using (UiFonts.PushRelative(settingsControlsActive ? .90f : 0.84f))
                using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
                    ImGui.TextWrapped("You can adjust your username in the account tab of settings page");
                ImGui.Spacing();
                if (ImGui.Button("Continue", new Vector2(ImGui.GetContentRegionAvail().X, Ui(38f))))
                {
                    signInState = SignInState.Idle;
                }

                break;

            case SignInState.Failed:
                ImGui.TextColored(Danger, signInStatusMessage.Length > 0 ? signInStatusMessage : "Sign-in failed.");
                if (ImGui.Button("Close", new Vector2(ImGui.GetContentRegionAvail().X, Ui(38f))))
                {
                    signInState = SignInState.Idle;
                    firstLaunchSignInRequested = false;
                }

                break;

            default:
                break;
        }

        if (signInState is SignInState.Requesting or SignInState.AwaitingBrowser)
        {
            ImGui.Spacing();
            var cancelWidth = Ui(100);
            ImGui.SetCursorPosY(MathF.Max(ImGui.GetCursorPosY(), ImGui.GetWindowSize().Y - ImGui.GetStyle().WindowPadding.Y - Ui(38)));
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, ImGui.GetContentRegionAvail().X - cancelWidth));
            using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7));
            using var border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, Ui(1));
            using var colours = ImRaii.PushColor(ImGuiCol.Button, FrameBg)
                .Push(ImGuiCol.ButtonHovered, CardBgHover).Push(ImGuiCol.Border, BorderSubtle);
            if (ImGui.Button("Cancel", new Vector2(cancelWidth, Ui(36))))
            {
                signInCts?.Cancel();
                signInState = SignInState.Idle;
                firstLaunchSignInRequested = false;
            }
        }
    }
}

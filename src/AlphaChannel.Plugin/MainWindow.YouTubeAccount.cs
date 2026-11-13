using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.YouTube;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private const string YouTubeBrowserSignInUrl =
        "https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F";

    private bool youtubeBrowserConnectionPending;
    private bool youtubeBrowserConnectionBusy;
    private string? youtubeBrowserConnectionMessage;
    private bool youtubeBrowserConnectionMessageIsError;

    private void SetYouTubeSessionNeedsReconnect(bool needsReconnect)
    {
        if (Plugin.Cfg.YouTubeSessionNeedsReconnect == needsReconnect) return;
        Plugin.Cfg.YouTubeSessionNeedsReconnect = needsReconnect;
        Plugin.Cfg.Save();
    }

    private void DrawEmbeddedBrowserYouTubeAccountSettings()
    {
        using var connectionDisabled = UiDisabled.Scope(youtubeBrowserConnectionBusy);
        var connected =
            YouTubeEmbeddedBrowserSession.IsConnected(Plugin.Cfg);

        if (connected && HasConnectedYouTubeProfile)
            DrawConnectedYouTubeProfile();
        else
            ImGui.TextColored(
                connected ? Plugin.Cfg.YouTubeSessionNeedsReconnect ? Danger : Good : MutedText,
                connected ? Plugin.Cfg.YouTubeSessionNeedsReconnect
                    ? "YouTube account needs reconnecting"
                    : "YouTube account connected" : "No YouTube account connected");

        if (connected && Plugin.Cfg.YouTubeSessionNeedsReconnect)
        {
            ImGui.Dummy(UiVec(0f, 8f));
            using var warningColor = ImRaii.PushColor(ImGuiCol.Text, Danger);
            ImGui.TextWrapped(
                "YouTube rejected the saved session cookies. Reconnect your account to resume account features.");
        }

        ImGui.Dummy(UiVec(0f, 6f));
        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            Math.Max(ImGui.GetContentRegionAvail().X, Ui(200f)));
        DrawSettingsHelper(connected
                ? "Alpha Channel uses this account for sign-in-required videos and, if enabled below, to sync likes, subscriptions and recommendations. Normal playback remains anonymous."
                : "Sign into YouTube in Alpha Channel's browser, then choose to use that account for account-required videos and optional feed syncing.");
        ImGui.PopTextWrapPos();

        ImGui.Dummy(UiVec(0f, 10f));
        var syncLikedVideos = Plugin.Cfg.SyncYouTubeLikedVideos;
        if (ImGui.Checkbox("Sync my liked videos from my YouTube account", ref syncLikedVideos))
        {
            Interlocked.Increment(ref youtubeLikedVideoSessionGeneration);
            Plugin.Cfg.SyncYouTubeLikedVideos = syncLikedVideos;
            Plugin.Cfg.Save();
        }

        if (ImGui.IsItemHovered())
            UiFonts.SetTooltip("Fetches your liked videos from YouTube, it does not pass videos you liked on Alpha Channel back to YouTube");

        ImGui.Dummy(UiVec(0f, 6f));
        var syncSubscriptions = Plugin.Cfg.SyncYouTubeSubscriptionVideos;
        if (ImGui.Checkbox("Sync videos from my YouTube subscriptions", ref syncSubscriptions))
        {
            Interlocked.Increment(ref youtubeLikedVideoSessionGeneration);
            Plugin.Cfg.SyncYouTubeSubscriptionVideos = syncSubscriptions;
            Plugin.Cfg.Save();
        }

        if (ImGui.IsItemHovered())
            UiFonts.SetTooltip("Merges videos from your YouTube subscriptions feed with Alpha Channel subscriptions. It does not change your YouTube subscriptions.");

        ImGui.Dummy(UiVec(0f, 6f));
        var syncRecommendations = Plugin.Cfg.SyncYouTubeRecommendedVideos;
        if (ImGui.Checkbox("Sync recommended videos from my YouTube account", ref syncRecommendations))
        {
            Interlocked.Increment(ref youtubeLikedVideoSessionGeneration);
            Plugin.Cfg.SyncYouTubeRecommendedVideos = syncRecommendations;
            youtubeBrowseRecommendedVideos = null;
            Plugin.Cfg.Save();
        }
        if (ImGui.IsItemHovered())
            UiFonts.SetTooltip("Fetches up to 15 YouTube recommendations at a time and keeps the latest 40 cached videos.");

        ImGui.Dummy(UiVec(0f, 12f));

        DrawSettingsHelper("Sign-in browser");
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo(
                "##youtubeSignInBrowser",
                Plugin.Cfg.YouTubeAccountUseWebView2 ? EdgeBrowserLabel : "Chromium")))
        {
            if (ImGui.Selectable("Chromium", !Plugin.Cfg.YouTubeAccountUseWebView2))
            {
                Plugin.Cfg.YouTubeAccountUseWebView2 = false;
                Plugin.Cfg.Save();
            }

            if (ImGui.Selectable(EdgeBrowserLabel, Plugin.Cfg.YouTubeAccountUseWebView2))
            {
                Plugin.Cfg.YouTubeAccountUseWebView2 = true;
                Plugin.Cfg.Save();
            }
            ImGui.EndCombo();
        }

        DrawSettingsHelper("Each browser keeps its own YouTube sign-in.");
        ImGui.Dummy(UiVec(0f, 12f));

        if (connected)
        {
            var gap = Ui(8f);
            var width =
                (ImGui.GetContentRegionAvail().X - gap) / 2f;

            DrawDjActionButton(
                "##refreshEmbeddedYouTubeAccount",
                FontAwesomeIcon.SyncAlt,
                "Refresh YouTube session",
                new Vector2(width, Ui(36f)),
                false,
                BeginEmbeddedBrowserYouTubeConnection,
                true);

            ImGui.SameLine(0f, gap);

            DrawDjActionButton(
                "##disconnectEmbeddedYouTubeAccount",
                FontAwesomeIcon.Unlink,
                "Disconnect YouTube account",
                new Vector2(width, Ui(36f)),
                false,
                DisconnectEmbeddedBrowserYouTubeAccount,
                false);
        }
        else
        {
            DrawDjActionButton(
                "##connectEmbeddedYouTubeAccount",
                FontAwesomeIcon.SignInAlt,
                "Connect YouTube Account",
                new Vector2(
                    ImGui.GetContentRegionAvail().X,
                    Ui(36f)),
                false,
                BeginEmbeddedBrowserYouTubeConnection,
                true);
        }

        if (!string.IsNullOrWhiteSpace(youtubeBrowserConnectionMessage))
        {
            ImGui.Dummy(UiVec(0f, 8f));
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(
                youtubeBrowserConnectionMessageIsError ? Danger : Good,
                youtubeBrowserConnectionMessage);
        }
    }

    private bool HasConnectedYouTubeProfile =>
        !string.IsNullOrWhiteSpace(Plugin.Cfg.YouTubeConnectedDisplayName) &&
        !string.IsNullOrWhiteSpace(Plugin.Cfg.YouTubeConnectedAvatarUrl);

    private void DrawConnectedYouTubeProfile()
    {
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(8f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(12f, 10f));
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
            .Push(ImGuiCol.Border, SettingsReviewBorder);
        using var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##connectedYouTubeProfile",
            new Vector2(-1f, Ui(148f)), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse));
        if (!panel) return;

        ImGui.TextColored(Plugin.Cfg.YouTubeSessionNeedsReconnect ? Danger : Good,
            Plugin.Cfg.YouTubeSessionNeedsReconnect ? "Reconnect this account:" : "Connected account:");
        ImGui.Dummy(UiVec(0f, 5f));
        DrawAvatarChip("User", null, Ui(48f), Plugin.Cfg.YouTubeConnectedAvatarUrl);
        ImGui.SameLine(0f, Ui(12f));
        using var text = ImRaii.Group();
        ImGui.TextUnformatted(Plugin.Cfg.YouTubeConnectedDisplayName);
        if (!string.IsNullOrWhiteSpace(Plugin.Cfg.YouTubeConnectedChannelName))
            DrawSettingsHelper($"Channel: {Plugin.Cfg.YouTubeConnectedChannelName}");
        if (!string.IsNullOrWhiteSpace(Plugin.Cfg.YouTubeConnectedHandle))
            ImGui.TextColored(Accent, Plugin.Cfg.YouTubeConnectedHandle);
    }

    private void BeginEmbeddedBrowserYouTubeConnection()
    {
        if (youtubeBrowserConnectionBusy) return;
        if (!screenController.Engine.TryPrepareBrowserPlayback())
        {
            youtubeBrowserConnectionMessage = screenController.Engine.LastError;
            youtubeBrowserConnectionMessageIsError = true;
            return;
        }
        youtubeBrowserConnectionPending =
            true;
        youtubeBrowserConnectionMessage =
            null;
        youtubeBrowserConnectionMessageIsError =
            false;

        browserAddress =
            YouTubeBrowserSignInUrl;
        currentPage =
            HomePage.Browser;

        var engine =
            screenController.Engine;

        var useWebView2 = Plugin.Cfg.YouTubeAccountUseWebView2;
        if (engine.Browser is { } browser &&
            (browser is WebViewBrowserRenderer) == useWebView2)
        {
            browser.Navigate(browserAddress);
        }
        else if (useWebView2)
        {
            StartWebViewBrowser();
        }
        else if (!engine.PlayBrowser(browserAddress))
        {
            youtubeBrowserConnectionPending =
                false;
            youtubeBrowserConnectionMessage =
                engine.LastError ??
                "The Alpha Channel browser could not be opened.";
            youtubeBrowserConnectionMessageIsError =
                true;
            currentPage =
                HomePage.Settings;
            settingsTab =
                SettingsTab.YouTube;
        }
    }

    private void DisconnectEmbeddedBrowserYouTubeAccount()
    {
        YouTubeEmbeddedBrowserSession.Disconnect(
            Plugin.Cfg);
        SetYouTubeSessionNeedsReconnect(false);
        ClearYouTubeLikedVideoCache();
        ClearYouTubeSubscriptionVideoCache();
        ClearYouTubeRecommendedVideoCache();

        video.CookiesPath =
            null;
        youtubeBrowserConnectionPending =
            false;
        youtubeBrowserConnectionMessage =
            "YouTube account disconnected. The browser itself remains signed in.";
        youtubeBrowserConnectionMessageIsError =
            false;
    }

    private void DrawPendingYouTubeBrowserConnection(
        BrowserRendererBase browser)
    {
        if (!youtubeBrowserConnectionPending)
            return;

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   Ui(8f))
               .Push(
                   ImGuiStyleVar.WindowPadding,
                   UiVec(14f, 9f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   CardBg)
               .Push(
                   ImGuiCol.Border,
                   new Vector4(Accent.X, Accent.Y, Accent.Z, 0.85f)))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                   "##youtubeBrowserConnection",
                   new Vector2(-1f, Ui(56f)),
                   true,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (!panel)
                return;

            var cancelWidth =
                Ui(92f);
            var connectWidth =
                Ui(190f);
            var gap =
                Ui(8f);
            var y =
                Ui(7f);
            var right =
                ImGui.GetWindowContentRegionMax().X;

            ImGui.PushTextWrapPos(
                right - cancelWidth - connectWidth - gap - Ui(20f));
            ImGui.TextColored(
                Vector4.One,
                youtubeBrowserConnectionBusy
                    ? "Checking the YouTube session…"
                    : "Finish signing in, then connect this YouTube account to Alpha Channel.");
            ImGui.PopTextWrapPos();

            ImGui.SetCursorPos(
                new Vector2(
                    right - cancelWidth - connectWidth - gap,
                    y));

            using (UiDisabled.Scope(youtubeBrowserConnectionBusy))
            {
                if (GameLayoutButton(
                        "Use This Account",
                        FontAwesomeIcon.Check,
                        connectWidth,
                        true))
                {
                    _ = CompleteEmbeddedBrowserYouTubeConnectionAsync(
                        browser);
                }

                ImGui.SameLine(0f, gap);

                if (GameLayoutButton(
                        "Cancel",
                        FontAwesomeIcon.Times,
                        cancelWidth))
                {
                    youtubeBrowserConnectionPending =
                        false;
                    youtubeBrowserConnectionMessage =
                        "YouTube account connection cancelled.";
                    youtubeBrowserConnectionMessageIsError =
                        false;
                    currentPage =
                        HomePage.Settings;
                    settingsTab =
                        SettingsTab.YouTube;
                }
            }
        }
    }

    private async Task CompleteEmbeddedBrowserYouTubeConnectionAsync(
        BrowserRendererBase browser)
    {
        if (youtubeBrowserConnectionBusy)
            return;

        youtubeBrowserConnectionBusy =
            true;
        youtubeBrowserConnectionMessage =
            null;

        ReleaseBrowserKeys(browser);
        screenController.Engine.SetBrowserControlsEnabled(false);
        browser.SetAccountConnectionPrivacy(true);
        var pageRestored = true;

        try
        {
            var result =
                await browser
                    .RequestYouTubeCookiesAsync()
                    .ConfigureAwait(false);

            if (Volatile.Read(ref disposeStarted) != 0) return;
            if (!result.Success ||
                string.IsNullOrWhiteSpace(result.Cookies))
            {
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (Volatile.Read(ref disposeStarted) != 0) return;
                    youtubeBrowserConnectionMessage = result.Error ?? "No signed-in YouTube session was found.";
                    youtubeBrowserConnectionMessageIsError = true;
                }).ConfigureAwait(false);
                return;
            }

            // A refreshed connection may belong to another account. Clear its
            // recommendations and invalidate any in-flight account-feed result.
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0) return;
                ClearYouTubeRecommendedVideoCache();
                Plugin.Cfg.YouTubeConnectedDisplayName = null;
                Plugin.Cfg.YouTubeConnectedChannelName = null;
                Plugin.Cfg.YouTubeConnectedHandle = null;
                Plugin.Cfg.YouTubeConnectedAvatarUrl = null;
            }).ConfigureAwait(false);
            youtubeFeedToken.ThrowIfCancellationRequested();

            var path =
                YouTubeEmbeddedBrowserSession.Save(
                    result.Cookies);

            // Profile information is optional. Pace this browser-driven lookup
            // like other authenticated work, without delaying connection for a
            // long rate-limit cooldown.
            try
            {
                using var lookupTimeout = CancellationTokenSource.CreateLinkedTokenSource(youtubeFeedToken);
                lookupTimeout.CancelAfter(TimeSpan.FromSeconds(55));
                using var permitTimeout = CancellationTokenSource.CreateLinkedTokenSource(lookupTimeout.Token);
                permitTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var permit = await YouTubeRequestScheduler.Shared.WaitAsync(
                    YouTubeRequestIdentity.Account, YouTubeRequestPriority.Manual, 2,
                    permitTimeout.Token).ConfigureAwait(false);
                pageRestored = false;
                var lookup = await browser.RequestYouTubeProfileAsync(lookupTimeout.Token).ConfigureAwait(false);
                pageRestored = lookup.PageRestored;
                if (lookup.Profile is { } profile)
                {
                    await Plugin.Framework.RunOnFrameworkThread(() =>
                    {
                        if (Volatile.Read(ref disposeStarted) != 0 || !ReferenceEquals(screenController.Engine.Browser, browser)) return;
                        Plugin.Cfg.YouTubeConnectedDisplayName = profile.DisplayName;
                        Plugin.Cfg.YouTubeConnectedChannelName = profile.ChannelName;
                        Plugin.Cfg.YouTubeConnectedHandle = profile.Handle;
                        Plugin.Cfg.YouTubeConnectedAvatarUrl = profile.AvatarUrl;
                    }).ConfigureAwait(false);
                }
                
            }
            catch (Exception)
            {
                // Never log browser account data, authentication or page URLs.
                
            }

            // Shut down the sign-in browser using the normal close path. The
            // cookies and optional profile have already been saved, so neither
            // browser host needs to remain running after connection.
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                var engine = screenController.Engine;
                if (Volatile.Read(ref disposeStarted) != 0 || !ReferenceEquals(engine.Browser, browser))
                    return;

                Interlocked.Increment(ref youtubeLikedVideoSessionGeneration);

                Plugin.Cfg.YouTubeEmbeddedBrowserSessionEnabled =
                    true;
                Plugin.Cfg.YouTubeSessionNeedsReconnect = false;
                Plugin.Cfg.Save();
                youtubeLikedVideosError = null;
                youtubeSubscriptionVideosError = null;
                youtubeRecommendedVideosError = null;
                youtubeBrowseRecommendedVideos = null;

                video.CookiesPath =
                    path;
                youtubeBrowserConnectionPending =
                    false;
                youtubeBrowserConnectionMessage =
                    "YouTube account connected. Enabled video feeds will sync automatically.";
                youtubeBrowserConnectionMessageIsError =
                    false;
                currentPage =
                    HomePage.Settings;
                settingsTab =
                    SettingsTab.YouTube;

                ReleaseBrowserKeys(browser);
                StopBrowserWatchPartyBroadcast();
                engine.StopVideo();
            }).ConfigureAwait(false);

        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref disposeStarted) == 0)
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (Volatile.Read(ref disposeStarted) != 0) return;
                    youtubeBrowserConnectionMessage = exception.Message;
                    youtubeBrowserConnectionMessageIsError = true;
                }).ConfigureAwait(false);
        }
        finally
        {
            if (Volatile.Read(ref disposeStarted) == 0)
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (Volatile.Read(ref disposeStarted) != 0) return;
                    if (!pageRestored && youtubeBrowserConnectionPending)
                    {
                        browserPrivacyMode = true;
                        browser.SetPrivacyMode(true);
                    }
                    browser.SetAccountConnectionPrivacy(false);
                    youtubeBrowserConnectionBusy = false;
                }).ConfigureAwait(false);
        }
    }
}

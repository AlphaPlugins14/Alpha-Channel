using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.BroadcastAllowance;
using System.Runtime.InteropServices;
using System.Text;

namespace AlphaChannel.Plugin;

// Alpha Channel embedded-browser page. Removing this file plus the labelled
// integration points removes the UI and Watch Party browser feature.
internal sealed partial class MainWindow
{
    private const float BrowserPageSpacing = 16f;
    private const string EdgeBrowserLabel = "Edge (WebView2)";
    private const string EdgeBrowserDescription =
        "Uses Microsoft Edge WebView2 inside Alpha Channel, with its own browser profile. Desktop Edge sign-ins and settings are separate.";

    private string browserAddress = "https://www.google.com/";
    private string? lastObservedBrowserAddress;
    private bool browserAddressDirty;
    private string? browserError;
    private bool browserBroadcastArmed;
    private bool browserBroadcastStartFailed;
    private bool browserBroadcastExpectedRunning;
    private DateTime nextBrowserBroadcastRetryUtc = DateTime.MinValue;
    private string? browserBroadcastPublishUrl;
    private string? browserBroadcastHlsUrl;

    internal string? ActiveBrowserBroadcastHlsUrl =>
        browserBroadcastArmed && screenController.Engine.IsBrowserBroadcasting
            ? browserBroadcastHlsUrl
            : null;

    internal bool IsBrowserBroadcastArmed => browserBroadcastArmed;

    internal string? ActiveBrowserBroadcastTitle =>
        browserBroadcastArmed
            ? "Web Browsing"
            : null;

    private bool browserPrivacyMode;
    private bool browserLibraryLoaded;
    private string? lastBrowserHistoryUrl;
    private readonly List<AlphaChannel.Plugin.Video.BrowserPageEntry> browserFavourites = [];
    private readonly List<AlphaChannel.Plugin.Video.BrowserPageEntry> browserHistory = [];
    private readonly AlphaChannel.Plugin.Video.BrowserLibraryStorage browserLibraryStorage = new();

    private readonly HashSet<int> browserKeysDown = [];
    private readonly Dictionary<int, long> browserKeyRepeatAt = [];
    private bool browserLeftMousePressed;
    private bool browserRightMousePressed;
    private int browserLastMouseX;
    private int browserLastMouseY;
    private static readonly int[] BrowserVirtualKeys =
        Enumerable.Range(0x30, 10).Concat(Enumerable.Range(0x41, 26))
            .Concat([0x08, 0x09, 0x0D, 0x1B, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25,
                0x26, 0x27, 0x28, 0x2D, 0x2E, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5,
                0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE])
            .ToArray();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyboardState,
        StringBuilder buffer, int bufferLength, uint flags, nint keyboardLayout);

    private static bool IsGameWindowForeground()
    {
        return GameWindowFocus.IsForeground;
    }

    private static unsafe bool IsGameTextInputActive()
    {
        var atkModule = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkModule.Instance();
        return atkModule is not null && atkModule->IsTextInputActive();
    }

    private static string NormalizeBrowserAddress(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
            return "https://www.google.com/";
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return uri.AbsoluteUri;
        if (!value.Any(char.IsWhiteSpace) && Uri.TryCreate("https://" + value, UriKind.Absolute, out uri) &&
            (uri.Host.Contains('.') || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             System.Net.IPAddress.TryParse(uri.Host, out _)))
            return uri.AbsoluteUri;
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(value);
    }

    private void DrawBrowserPage()
    {
        PollWebViewSetup();
        var engine = screenController.Engine;
        var browser = engine.Browser;
        if (browser is null)
        {
            DrawBrowserLaunchPage();
            DrawBrowserDialogs(null);
            return;
        }
        using var connectionDisabled = UiDisabled.Scope(youtubeBrowserConnectionBusy);
        // Only actual navigation updates the address display. Losing input focus
        // to the Go button must not replace a draft with the current page URL.
        if (browser.Address != lastObservedBrowserAddress)
        {
            lastObservedBrowserAddress = browser.Address;
            if (!browserAddressDirty) browserAddress = browser.Address;
        }
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8f, 8f));
        using var frames = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(10f, 7f)).Push(ImGuiStyleVar.FrameRounding, Ui(7f));
        using var colors = ImRaii.PushColor(ImGuiCol.FrameBg, FrameBg)
            .Push(ImGuiCol.FrameBgHovered, FrameBgHover);

        var gap = Ui(8f);
        using (UiDisabled.Scope(!browser.CanGoBack))
            if (BrowserToolbarIconButton("browserBack", FontAwesomeIcon.ArrowLeft, false, "Back")) browser.Back();
        ImGui.SameLine(0f, gap);
        using (UiDisabled.Scope(!browser.CanGoForward))
            if (BrowserToolbarIconButton("browserForward", FontAwesomeIcon.ArrowRight, false, "Forward")) browser.Forward();
        ImGui.SameLine(0f, gap);
        if (BrowserToolbarIconButton("browserRefresh",
                browser.IsLoading ? FontAwesomeIcon.Stop : FontAwesomeIcon.Sync, false,
                browser.IsLoading ? "Stop loading" : "Refresh"))
        {
            if (browser.IsLoading) browser.StopLoading(); else browser.Reload();
        }
        ImGui.SameLine(0f, gap);
        ImGui.SetNextItemWidth(MathF.Max(Ui(120f), ImGui.GetContentRegionAvail().X - Ui(394f)));
        // Keep the managed string updated on each edit, including paste. With
        // EnterReturnsTrue the binding can retain the old string until submit.
        if (ImGui.InputTextWithHint("##browserAddress", "Enter a web address or search...", ref browserAddress, 2048))
            browserAddressDirty = true;
        var addressEditing = ImGui.IsItemActive();
        var submit = ImGui.IsItemFocused() && ImGui.IsKeyPressed(ImGuiKey.Enter);
        ImGui.SameLine(0f, gap);
        if (GameLayoutButton("Go", FontAwesomeIcon.ArrowRight, Ui(84f), true) || submit)
        {
            var address = NormalizeBrowserAddress(browserAddress);
            browserAddressDirty = false;
            browser.Navigate(address);
        }
        ImGui.SameLine(0f, gap);
        var homepage = Plugin.Cfg.BrowserHomepageUrl;
        var hasHomepage = !string.IsNullOrWhiteSpace(homepage);
        if (BrowserToolbarIconButton("browserHome", FontAwesomeIcon.Home, false,
                hasHomepage ? "Open homepage" : "Set current page as homepage", muted: !hasHomepage))
        {
            if (hasHomepage)
            {
                browserAddressDirty = false;
                browserAddress = homepage;
                browser.Navigate(homepage);
            }
            else if (Uri.TryCreate(browser.Address, UriKind.Absolute, out var currentPage) &&
                     currentPage.Scheme is "http" or "https")
            {
                Plugin.Cfg.BrowserHomepageUrl = currentPage.AbsoluteUri;
                Plugin.Cfg.Save();
            }
        }
        ImGui.SameLine(0f, gap);
        if (BrowserToolbarIconButton("browserFavourites", FontAwesomeIcon.Star, false, "Favourites"))
            activeGameDialog = "Browser favourites";
        ImGui.SameLine(0f, gap);
        if (BrowserToolbarIconButton("browserHistory", FontAwesomeIcon.History, false, "History"))
            activeGameDialog = "Browser history";
        ImGui.SameLine(0f, gap);
        if (BrowserToolbarIconButton("browserPrivacy", browserPrivacyMode ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye,
                browserPrivacyMode, browserPrivacyMode
                    ? "Privacy mode is enabled. Viewers see a privacy screen and cannot hear browser audio."
                    : "Hide browser video and audio from viewers"))
        {
            browserPrivacyMode = !browserPrivacyMode;
            browser.SetPrivacyMode(browserPrivacyMode);
        }

        ImGui.SameLine(0f, gap);
        if (BrowserToolbarIconButton("browserAdBlock", FontAwesomeIcon.Ban, Plugin.Cfg.BrowserAdBlockEnabled,
                Plugin.Cfg.BrowserAdBlockEnabled ? "Ad blocking is on. Click to turn it off and reload." : "Ad blocking is off. Click to enable it and reload."))
        {
            Plugin.Cfg.BrowserAdBlockEnabled = !Plugin.Cfg.BrowserAdBlockEnabled;
            Plugin.Cfg.Save();
            browser.SetAdBlockEnabled(Plugin.Cfg.BrowserAdBlockEnabled);
        }
        ImGui.SameLine(0f, gap);
        if (BrowserToolbarIconButton("browserSettings", FontAwesomeIcon.Cog, false, "Browser settings"))
            activeGameDialog = "Browser settings";

        EnsureBrowserLibraryLoaded();
        if (!youtubeBrowserConnectionBusy) TrackBrowserHistory(browser);
        browser.SetPrivacyMode(browserPrivacyMode);
        var available = ImGui.GetContentRegionAvail();
        var controlsHeight = BrowserBroadcastPanelHeight() + Ui(62f) +
            (youtubeBrowserConnectionPending ? Ui(64f) : 0f);
        var viewHeight = MathF.Max(Ui(260f), MathF.Min(available.Y - controlsHeight, available.X * 9f / 16f));
        var viewWidth = MathF.Min(available.X, viewHeight * 16f / 9f);
        var surfaceCursorX = ImGui.GetCursorPosX();
        ImGui.SetCursorPosX(surfaceCursorX + (available.X - viewWidth) / 2f);
        var viewOrigin = ImGui.GetCursorScreenPos();
        var viewSize = new Vector2(viewWidth, viewHeight);
        var notice = browser.PendingNavigation;
        var noticeSize = new Vector2(MathF.Min(Ui(460f), viewWidth - Ui(24f)), ImGui.GetTextLineHeight() * 2f + Ui(72f));
        var noticeOrigin = viewOrigin + new Vector2(viewWidth - noticeSize.X - Ui(12f), Ui(12f));
        var overNotice = notice is not null && ImGui.IsMouseHoveringRect(noticeOrigin, noticeOrigin + noticeSize);

        // A plain ImGui.Image has no item ID, so ImGui treats a mouse drag over it as a
        // drag on the empty background of this borderless window. Use a real invisible
        // input item for the browser surface and paint the Chromium texture onto it.
        // The active item prevents window movement while the existing browser input
        // forwarding continues to receive clicks, drags, releases and wheel input.
        ImGui.InvisibleButton(
            "##embeddedBrowserSurface",
            viewSize,
            ImGuiButtonFlags.MouseButtonLeft |
            ImGuiButtonFlags.MouseButtonRight);
        ImGui.GetWindowDrawList().AddImage(
            new ImTextureID(engine.PreviewTextureHandle),
            viewOrigin,
            viewOrigin + viewSize);
        HandleBrowserInput(browser, viewOrigin, viewSize,
            addressEditing || submit || overNotice || youtubeBrowserConnectionBusy || activeGameDialog is not null,
            overNotice || youtubeBrowserConnectionBusy || activeGameDialog is not null);
        if (youtubeBrowserConnectionBusy)
            DrawYouTubeConnectionOverlay(viewOrigin, viewSize);
        else if (notice is not null)
            DrawBrowserNavigationNotice(browser, notice, noticeOrigin, noticeSize);

        ImGui.SetCursorPosX(surfaceCursorX);
        DrawPendingYouTubeBrowserConnection(browser);
        DrawBrowserBroadcastPanel();

        var third = (ImGui.GetContentRegionAvail().X - gap * 2f) / 3f;
        if (GameLayoutButton("Close browser", FontAwesomeIcon.Times, third, danger: true))
        {
            CloseCurrentBrowser();
        }
        ImGui.SameLine(0f, gap);
        if (GameLayoutButton("Control browser", FontAwesomeIcon.Keyboard, third, engine.BrowserControlsEnabled))
            engine.SetBrowserControlsEnabled(true);
        ImGui.SameLine(0f, gap);
        if (GameLayoutButton("Control FFXIV", FontAwesomeIcon.Desktop, third, !engine.BrowserControlsEnabled))
        {
            ReleaseBrowserKeys(browser);
            engine.SetBrowserControlsEnabled(false);
        }
        if (engine.BrowserControlsEnabled && ImGui.IsItemHovered())
            UiFonts.SetTooltip("Press Ctrl + F12 at any time to return keyboard control to FFXIV.");
        if (!string.IsNullOrWhiteSpace(browser.LastError ?? browserError))
            ImGui.TextColored(Danger, browser.LastError ?? browserError);
        DrawBrowserDialogs(browser);
    }

    private void DrawYouTubeConnectionOverlay(Vector2 origin, Vector2 size)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size,
            0xFF110A09); // Stay opaque even inside ImGui's disabled scope.
        const string message = "Connecting YouTube account with Alpha Channel…";
        var textSize = ImGui.CalcTextSize(message);
        draw.AddText(origin + (size - textSize) / 2f,
            ImGui.GetColorU32(Vector4.One), message);
        const string privacy = "Browser video and audio are hidden from your watch party.";
        var privacySize = ImGui.CalcTextSize(privacy);
        draw.AddText(origin + new Vector2((size.X - privacySize.X) / 2f,
            size.Y / 2f + Ui(22f)), ImGui.GetColorU32(MutedText), privacy);
    }

    private void DrawBrowserNavigationNotice(BrowserRendererBase browser, BrowserRendererBase.NavigationNotice notice,
        Vector2 origin, Vector2 size)
    {
        // A child over the existing surface shares the main viewport. Restoring
        // the cursor preserves page layout without a separate popup window.
        var cursor = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin + UiVec(0f, 3f), origin + size + UiVec(0f, 3f),
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.25f)), Ui(9f));
        ImGui.SetCursorScreenPos(origin);
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(12f, 10f))
            .Push(ImGuiStyleVar.ChildRounding, Ui(9f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
            .Push(ImGuiCol.Border, BorderSubtle))
        {
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##browserRedirectNotice", size, true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
            {
                using (UiFonts.PushIcon()) ImGui.TextColored(Accent, FontAwesomeIcon.Ban.ToIconString());
                ImGui.SameLine(0f, Ui(8f));
                ImGui.TextUnformatted("Redirect blocked");
                ImGui.SameLine(size.X - Ui(28f));
                using (UiFonts.PushIcon()) ImGui.TextColored(MutedText, FontAwesomeIcon.InfoCircle.ToIconString());
                if (ImGui.IsItemHovered())
                {
                    UiFonts.BeginTooltip();
                    ImGui.PushTextWrapPos(Ui(300f));
                    ImGui.TextUnformatted("Page redirects are blocked by default to prevent unwanted page visits when broadcasting");
                    ImGui.PopTextWrapPos();
                    ImGui.EndTooltip();
                }
                var shortUrl = notice.Url.Length > 96 ? notice.Url[..96] + "…" : notice.Url;
                ImGui.TextColored(MutedText, TruncateToWidth(shortUrl, ImGui.GetContentRegionAvail().X));
                if (ImGui.IsItemHovered())
                {
                    UiFonts.BeginTooltip();
                    ImGui.PushTextWrapPos(ImGui.GetFontSize() * 36f);
                    ImGui.TextUnformatted(notice.Url);
                    ImGui.PopTextWrapPos();
                    ImGui.EndTooltip();
                }
                ImGui.Spacing();
                ImGui.SetCursorPosX(MathF.Max(Ui(12f), size.X - Ui(434f)));
                // Plain labels also avoid drawing internal ImGui IDs as text.
                if (GameLayoutButton("Ignore", FontAwesomeIcon.Times, Ui(78f), height: 28f)) browser.ResolveNavigation(notice, false);
                ImGui.SameLine(0f, Ui(8f));
                if (GameLayoutButton("Ignore All", FontAwesomeIcon.Ban, Ui(106f), height: 28f)) browser.ResolveNavigation(notice, false, ignoreAll: true);
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Ignores all redirects from this page url during current session");
                ImGui.SameLine(0f, Ui(8f));
                if (GameLayoutButton("Allow", FontAwesomeIcon.ArrowRight, Ui(78f), true, height: 28f)) browser.ResolveNavigation(notice, true);
                ImGui.SameLine(0f, Ui(8f));
                if (GameLayoutButton("Always allow", FontAwesomeIcon.Check, Ui(136f), height: 28f))
                {
                    if (!Plugin.Cfg.BrowserRedirectWhitelist.Contains(notice.SourceHost, StringComparer.OrdinalIgnoreCase))
                        Plugin.Cfg.BrowserRedirectWhitelist.Add(notice.SourceHost);
                    Plugin.Cfg.Save();
                    browser.SetRedirectWhitelist(Plugin.Cfg.BrowserRedirectWhitelist);
                    browser.ResolveNavigation(notice, true);
                }
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip($"Redirects will always be allowed from {notice.SourceHost}");
            }
            ImGui.EndChild();
        }
        ImGui.SetCursorScreenPos(cursor);
    }

    private void EnsureBrowserLibraryLoaded()
    {
        if (browserLibraryLoaded) return;
        browserLibraryLoaded = true;
        var loaded = browserLibraryStorage.Load();
        browserFavourites.AddRange(loaded.Favourites);
        browserHistory.AddRange(loaded.History.Take(30));
        if (!string.IsNullOrWhiteSpace(loaded.RecoveryMessage))
            Plugin.ChatGui.Print("[AlphaChannel] " + loaded.RecoveryMessage);
    }

    private void TrackBrowserHistory(AlphaChannel.Plugin.Video.BrowserRendererBase browser)
    {
        if (browser.IsLoading || !Uri.TryCreate(browser.Address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || browser.Address == lastBrowserHistoryUrl) return;
        lastBrowserHistoryUrl = browser.Address;
        browserHistory.RemoveAll(page => string.Equals(page.Url, browser.Address, StringComparison.OrdinalIgnoreCase));
        browserHistory.Insert(0, new(browser.Title, browser.Address));
        if (browserHistory.Count > 30) browserHistory.RemoveRange(30, browserHistory.Count - 30);
        browserLibraryStorage.Save(browserFavourites, browserHistory);
    }

    private void DrawBrowserDialogs(AlphaChannel.Plugin.Video.BrowserRendererBase? browser)
    {
        EnsureBrowserLibraryLoaded();
        if (BeginGameDialog("Browser broadcast", "Browser broadcast", FontAwesomeIcon.BroadcastTower, 540f, 470f))
        {
            ImGui.TextWrapped("Choose the quality and frame rate for your broadcast.");
            ImGui.Spacing();
            DrawBrowserBroadcastAllowancePlaceholder();
            ImGui.Spacing();
            DrawBrowserBroadcastQualitySelector();
            ImGui.Spacing();
            ImGui.TextWrapped("If you experience lag or viewers report broadcast stream issues then lower FPS");
            ImGui.Spacing();
            var buttonWidth = (ImGui.GetContentRegionAvail().X - Ui(10f)) / 2f;
            if (GameLayoutButton("Cancel", FontAwesomeIcon.Times, buttonWidth)) activeGameDialog = null;
            ImGui.SameLine(0f, Ui(10f));
            using (UiDisabled.Scope(browser is null || browserBroadcastArmed || !CanStartBrowserBroadcast()))
            {
                if (GameLayoutButton("Start Broadcast", FontAwesomeIcon.BroadcastTower, buttonWidth, true))
                {
                    StartBrowserWatchPartyBroadcast();
                    if (browserBroadcastArmed) activeGameDialog = null;
                }
            }
            DrawMissingBroadcastStreamKeyTooltip(!browserBroadcastArmed);
            EndGameDialog();
        }
        if (BeginGameDialog("Browser settings", "Browser settings", FontAwesomeIcon.Cog, 620f, 600f))
        {
            DrawRetroGameSubheading("Redirect whitelist");
            DrawBrowserHelper("Websites on this list will always have redirects allowed. This list is saved across plugin restarts.");
            ImGui.Spacing();
            const string redirects = "We block redirects by default to prevent unwanted page visits while broadcasting your browser. This also prevents full-page redirect ads used by many streaming websites.";
            const string whitelist = "Alpha Channel uses an internal whitelist of 300+ domains that allow redirects by default.";
            float infoHeight;
            using (UiFonts.PushRelative(.90f))
                infoHeight = ImGui.CalcTextSize(redirects, false, ImGui.GetContentRegionAvail().X - Ui(24f)).Y
                    + ImGui.CalcTextSize(whitelist, false, ImGui.GetContentRegionAvail().X - Ui(24f)).Y;
            // Two padding edges, seven item gaps and the border. Keep the gaps
            // local to this panel so surrounding page styles cannot clip the checkbox.
            infoHeight += ImGui.GetTextLineHeight() + ImGui.GetFrameHeight() + Ui(68f);
            var listHeight = Math.Clamp(ImGui.GetContentRegionAvail().Y - infoHeight - Ui(145f), Ui(40f), Ui(100f));
            using (var hosts = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##browserRedirectWhitelist", new Vector2(0f, listHeight), false)))
            {
                if (hosts)
                {
                    if (Plugin.Cfg.BrowserRedirectWhitelist.Count == 0) DrawBrowserHelper("No websites whitelisted.");
                    foreach (var host in Plugin.Cfg.BrowserRedirectWhitelist.ToArray())
                    {
                        ImGui.PushID(host);
                        var rowWidth = ImGui.GetContentRegionAvail().X;
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextColored(Hex(0xE2E7EF), TruncateToWidth(host, MathF.Max(1f, rowWidth - Ui(98f))));
                        if (ImGui.IsItemHovered()) UiFonts.SetTooltip(host);
                        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - Ui(82f));
                        if (GameLayoutButton("Remove", FontAwesomeIcon.Times, Ui(82f), height: 28f))
                        {
                            Plugin.Cfg.BrowserRedirectWhitelist.Remove(host);
                            Plugin.Cfg.Save();
                            browser?.SetRedirectWhitelist(Plugin.Cfg.BrowserRedirectWhitelist);
                        }
                        ImGui.PopID();
                    }
                }
            }
            ImGui.Spacing();
            using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(12f, 12f))
                .Push(ImGuiStyleVar.ChildRounding, Ui(8f))
                .Push(ImGuiStyleVar.ItemSpacing, UiVec(8f, 6f)))
            using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, BorderSubtle))
            using (var info = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##browserRedirectInformation", new Vector2(0f, infoHeight), true,
                       ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
            {
                if (info)
                {
                    using (UiFonts.PushIcon()) ImGui.TextColored(Accent, FontAwesomeIcon.InfoCircle.ToIconString());
                    ImGui.SameLine(0f, Ui(8f));
                    DrawRetroGameSubheading("About redirects");
                    ImGui.Spacing();
                    DrawBrowserHelper(redirects);
                    ImGui.Spacing();
                    DrawBrowserHelper(whitelist);
                    ImGui.Spacing();
                    var useAlphaChannelWhitelist = Plugin.Cfg.UseAlphaChannelRedirectWhitelist;
                    if (ImGui.Checkbox("Use Alpha Channel whitelist", ref useAlphaChannelWhitelist))
                    {
                        Plugin.Cfg.UseAlphaChannelRedirectWhitelist = useAlphaChannelWhitelist;
                        Plugin.Cfg.Save();
                        browser?.SetAlphaChannelRedirectWhitelistEnabled(useAlphaChannelWhitelist);
                    }
                }
            }
            ImGui.Spacing();
            DrawBrowserHelper("Homepage is shared between Chromium and Edge.");
            using (UiDisabled.Scope(string.IsNullOrWhiteSpace(Plugin.Cfg.BrowserHomepageUrl)))
            {
                if (GameLayoutButton("Reset homepage assignment", FontAwesomeIcon.Undo,
                        ImGui.GetContentRegionAvail().X))
                {
                    Plugin.Cfg.BrowserHomepageUrl = string.Empty;
                    Plugin.Cfg.Save();
                }
            }
            ImGui.Spacing();
            if (GameLayoutButton("Done", FontAwesomeIcon.Check, ImGui.GetContentRegionAvail().X, true)) activeGameDialog = null;
            EndGameDialog();
        }
        if (BeginGameDialog("Browser favourites", "Browser favourites", FontAwesomeIcon.Star, 680f, 560f))
        {
            if (browser is not null)
            {
                var index = browserFavourites.FindIndex(page => string.Equals(page.Url, browser.Address, StringComparison.OrdinalIgnoreCase));
                if (GameLayoutButton(index >= 0 ? "Remove Current Page" : "Add Current Page",
                        index >= 0 ? FontAwesomeIcon.Times : FontAwesomeIcon.Star, ImGui.GetContentRegionAvail().X, true))
                {
                    if (index >= 0) browserFavourites.RemoveAt(index);
                    else browserFavourites.Insert(0, new(browser.Title, browser.Address));
                    browserLibraryStorage.Save(browserFavourites, browserHistory);
                }
                ImGui.Spacing();
            }
            DrawBrowserPageList(browserFavourites, browser, "No favourite pages yet.", true);
            if (GameLayoutButton("Done", FontAwesomeIcon.Check, ImGui.GetContentRegionAvail().X, true)) activeGameDialog = null;
            EndGameDialog();
        }
        if (BeginGameDialog("Browser history", "Browser history", FontAwesomeIcon.History, 680f, 550f))
        {
            DrawBrowserHelper("Your last 30 visited pages");
            ImGui.Spacing();
            DrawBrowserPageList(browserHistory, browser, "No browsing history yet.", false, Ui(104f));
            if (browserHistory.Count > 0 && GameLayoutButton("Clear history", FontAwesomeIcon.Trash, ImGui.GetContentRegionAvail().X, danger: true))
            {
                browserHistory.Clear();
                browserLibraryStorage.Save(browserFavourites, browserHistory);
            }
            if (GameLayoutButton("Done", FontAwesomeIcon.Check, ImGui.GetContentRegionAvail().X, true)) activeGameDialog = null;
            EndGameDialog();
        }
    }

    private void DrawBrowserPageList(List<AlphaChannel.Plugin.Video.BrowserPageEntry> pages,
        AlphaChannel.Plugin.Video.BrowserRendererBase? browser, string emptyText, bool allowDelete, float footerReserve = 0f)
    {
        if (pages.Count == 0) { ImGui.TextDisabled(emptyText); ImGui.Dummy(UiVec(0f, 24f)); return; }
        var height = MathF.Min(Ui(330f), pages.Count * Ui(54f));
        if (footerReserve > 0f) height = MathF.Max(1f, MathF.Min(height, ImGui.GetContentRegionAvail().Y - footerReserve));
        UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##browserPageList", UiVec(ImGui.GetContentRegionAvail().X, height), false));
        foreach (var page in pages.ToArray())
        {
            ImGui.PushID(page.Url);
            var deleteSpace = allowDelete ? Ui(50f) : 0f;
            using var pageColour = ImRaii.PushColor(ImGuiCol.Text, Hex(0xE2E7EF));
            if (GameLayoutButton(page.Title, FontAwesomeIcon.Globe, ImGui.GetContentRegionAvail().X - deleteSpace))
            {
                browserAddress = page.Url;
                if (browser is null) screenController.Engine.PlayBrowser(page.Url); else browser.Navigate(page.Url);
                activeGameDialog = null;
            }
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip(page.Url);
            if (allowDelete)
            {
                ImGui.SameLine(0f, Ui(8f));
                if (BrowserToolbarIconButton("deleteFavourite", FontAwesomeIcon.Trash, false,
                        "Remove from favourites", true))
                {
                    browserFavourites.Remove(page);
                    browserLibraryStorage.Save(browserFavourites, browserHistory);
                }
            }
            ImGui.PopID();
        }
        ImGui.EndChild();
        ImGui.Spacing();
    }

    private bool BrowserToolbarIconButton(string id, FontAwesomeIcon icon, bool selected, string tooltip,
        bool danger = false, bool muted = false)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = UiVec(42f, 36f);
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(8f));
        using var border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, selected || danger ? 0f : Ui(1f));
        using var colors = ImRaii.PushColor(ImGuiCol.Button,
                danger ? Danger : selected ? Accent : FrameBg)
            .Push(ImGuiCol.ButtonHovered, danger ? Danger : selected ? AccentHover : CardBgHover)
            .Push(ImGuiCol.ButtonActive, danger ? Danger : AccentActive)
            .Push(ImGuiCol.Border, BorderSubtle);
        var clicked = ImGui.Button("##" + id, size);
        var glyph = icon.ToIconString();
        Vector2 glyphSize;
        using (UiFonts.PushIcon()) glyphSize = ImGui.CalcTextSize(glyph);
        using (UiFonts.PushIcon())
            UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), origin + (size - glyphSize) / 2f, muted ? ImGui.GetColorU32(MutedText) : ImGui.GetColorU32(ImGuiCol.Text), glyph);
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip(tooltip);
        return clicked;
    }

    private void HandleBrowserInput(AlphaChannel.Plugin.Video.BrowserRendererBase browser, Vector2 origin, Vector2 size,
        bool addressEditing, bool blockMouse = false)
    {
        if (youtubeBrowserConnectionBusy)
        {
            ReleaseBrowserKeys(browser);
            screenController.Engine.SetBrowserControlsEnabled(false);
            if (browserLeftMousePressed) { browser.SendMouseClick(browserLastMouseX, browserLastMouseY, 0, true); browserLeftMousePressed = false; }
            if (browserRightMousePressed) { browser.SendMouseClick(browserLastMouseX, browserLastMouseY, 2, true); browserRightMousePressed = false; }
            return;
        }
        var hovered = ImGui.IsItemHovered();
        var browserFocusRequested = hovered && !blockMouse && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        var browserFocusLost = ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !browserFocusRequested;

        // GetAsyncKeyState is global, so browser control must follow the active
        // input target inside the game as well as the foreground application.
        if (!IsGameWindowForeground())
        {
            if (browserKeysDown.Count > 0) ReleaseBrowserKeys(browser);
            screenController.Engine.SetBrowserControlsEnabled(false);
            if (browserLeftMousePressed) { browser.SendMouseClick(browserLastMouseX, browserLastMouseY, 0, true); browserLeftMousePressed = false; }
            if (browserRightMousePressed) { browser.SendMouseClick(browserLastMouseX, browserLastMouseY, 2, true); browserRightMousePressed = false; }
            return;
        }
        var anotherTextInputActive = ImGui.GetIO().WantTextInput || IsGameTextInputActive();
        if (browserFocusLost || anotherTextInputActive)
        {
            if (browserKeysDown.Count > 0) ReleaseBrowserKeys(browser);
            screenController.Engine.SetBrowserControlsEnabled(false);
        }
        if (browserFocusRequested && !addressEditing && !anotherTextInputActive)
            screenController.Engine.SetBrowserControlsEnabled(true);

        if (screenController.Engine.BrowserControlsEnabled && !addressEditing)
            ForwardBrowserKeyboard(browser);
        else if (browserKeysDown.Count > 0)
            ReleaseBrowserKeys(browser);
        if ((!hovered || blockMouse) && !browserLeftMousePressed && !browserRightMousePressed) return;
        var mouse = ImGui.GetMousePos();
        var x = Math.Clamp((int)((mouse.X - origin.X) / size.X * AlphaChannel.Plugin.Video.BrowserRendererBase.Width), 0, AlphaChannel.Plugin.Video.BrowserRendererBase.Width - 1);
        var y = Math.Clamp((int)((mouse.Y - origin.Y) / size.Y * AlphaChannel.Plugin.Video.BrowserRendererBase.Height), 0, AlphaChannel.Plugin.Video.BrowserRendererBase.Height - 1);
        browserLastMouseX = x;
        browserLastMouseY = y;
        browser.SendMouseMove(x, y);
        if (hovered && !blockMouse && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            browser.SendMouseClick(x, y, 0, false);
            browserLeftMousePressed = true;
        }
        if (browserLeftMousePressed && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            browser.SendMouseClick(x, y, 0, true);
            browserLeftMousePressed = false;
        }
        if (hovered && !blockMouse && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            browser.SendMouseClick(x, y, 2, false);
            browserRightMousePressed = true;
        }
        if (browserRightMousePressed && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
        {
            browser.SendMouseClick(x, y, 2, true);
            browserRightMousePressed = false;
        }
        var wheel = ImGui.GetIO().MouseWheel;
        if (wheel != 0f) browser.SendMouseWheel(x, y, 0, (int)(wheel * 120));
    }

    private void ForwardBrowserKeyboard(AlphaChannel.Plugin.Video.BrowserRendererBase browser)
    {
        ImGui.SetNextFrameWantCaptureKeyboard(true);
        var modifiers = BrowserKeyModifiers();
        var now = Environment.TickCount64;
        foreach (var virtualKey in BrowserVirtualKeys)
        {
            var down = (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
            if (down && browserKeysDown.Add(virtualKey))
            {
                SendBrowserKeyPress(browser, virtualKey, modifiers);
                browserKeyRepeatAt[virtualKey] = now + 450;
            }
            else if (down && browserKeyRepeatAt.TryGetValue(virtualKey, out var repeatAt) && now >= repeatAt)
            {
                SendBrowserKeyPress(browser, virtualKey, modifiers);
                browserKeyRepeatAt[virtualKey] = now + 35;
            }
            else if (!down && browserKeysDown.Remove(virtualKey))
            {
                browser.SendKey(virtualKey, true, modifiers);
                browserKeyRepeatAt.Remove(virtualKey);
            }
        }
    }

    private static int BrowserKeyModifiers()
    {
        var modifiers = 0;
        if ((GetAsyncKeyState(0x10) & 0x8000) != 0) modifiers |= 2;
        if ((GetAsyncKeyState(0x11) & 0x8000) != 0) modifiers |= 4;
        if ((GetAsyncKeyState(0x12) & 0x8000) != 0) modifiers |= 8;
        return modifiers;
    }

    private static void SendBrowserKeyPress(AlphaChannel.Plugin.Video.BrowserRendererBase browser,
        int virtualKey, int modifiers)
    {
        browser.SendKey(virtualKey, false, modifiers);
        if ((modifiers & (4 | 8)) != 0) return;
        var keyboardState = new byte[256];
        for (var key = 0; key < keyboardState.Length; key++)
            if ((GetAsyncKeyState(key) & 0x8000) != 0) keyboardState[key] = 0x80;
        if ((GetKeyState(0x14) & 1) != 0) keyboardState[0x14] |= 1;
        var text = new StringBuilder(4);
        var count = ToUnicodeEx((uint)virtualKey, MapVirtualKey((uint)virtualKey, 0), keyboardState,
            text, text.Capacity, 0, GetKeyboardLayout(0));
        if (count > 0)
            for (var index = 0; index < count; index++) browser.SendCharacter(text[index], modifiers);
    }

    private void ReleaseBrowserKeys(AlphaChannel.Plugin.Video.BrowserRendererBase browser)
    {
        // Release with no modifiers: another foreground app may now own them.
        foreach (var virtualKey in browserKeysDown) browser.SendKey(virtualKey, true, 0);
        browserKeysDown.Clear();
        browserKeyRepeatAt.Clear();
    }

    private void DrawBrowserBroadcastPanel()
    {
        var engine = screenController.Engine;
        var status = browserBroadcastArmed
            ? browserBroadcastStartFailed
                ? "Broadcast failed"
                : engine.IsBrowserBroadcasting
                    ? "Live"
                    : "Waiting for viewers"
            : "Offline";
        if (browserBroadcastArmed && browserPrivacyMode && !browserBroadcastStartFailed)
            status = "Privacy mode";
        var statusColor = browserBroadcastArmed
            ? browserBroadcastStartFailed
                ? Danger
                : engine.IsBrowserBroadcasting
                    ? Good
                    : MutedText
            : MutedText;

        GameLayoutPanel(
            "##browserBroadcastPanel",
            ImGui.GetContentRegionAvail().X,
            BrowserBroadcastPanelHeight(),
            () =>
            {
                using var table = ImRaii.Table(
                    "##browserBroadcastColumns",
                    3,
                    ImGuiTableFlags.SizingStretchProp |
                    ImGuiTableFlags.BordersInnerV);
                if (!table)
                    return;

                ImGui.TableSetupColumn(
                    "Status",
                    ImGuiTableColumnFlags.WidthFixed,
                    Ui(120f));
                ImGui.TableSetupColumn(
                    "Allowance",
                    ImGuiTableColumnFlags.WidthStretch,
                    2.4f);
                ImGui.TableSetupColumn(
                    "Actions",
                    ImGuiTableColumnFlags.WidthFixed,
                    BrowserBroadcastActionWidth());
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                using (UiFonts.PushIcon())
                    ImGui.TextColored(Accent, FontAwesomeIcon.BroadcastTower.ToIconString());
                ImGui.SameLine(0f, Ui(6f));
                DrawRetroGameSubheading("Broadcast");
                using var uiFontScale38246=UiFonts.PushRelative(0.70f);
                ImGui.TextColored(statusColor, status);
                if (browserBroadcastArmed)
                {
                    var quality = Plugin.Cfg.BrowserBroadcastQuality.ForPatreonTier(ConfiguredPatreonTier());
                    var label = quality switch
                    {
                        BrowserBroadcastQuality.High => "High Quality",
                        BrowserBroadcastQuality.Standard => "Standard Quality",
                        _ => "Data Saver",
                    };
                    ImGui.TextColored(MutedText, $"{label} · {BrowserBroadcastQualityProfiles.NormalizeFrameRate(Plugin.Cfg.BrowserBroadcastFramesPerSecond)} FPS");
                }
                uiFontScale38246.Dispose();
                if (browserPrivacyMode && ImGui.IsItemHovered())
                    UiFonts.SetTooltip("Privacy mode enabled, stream is currently blacked out");

                ImGui.TableSetColumnIndex(1);
                DrawBrowserBroadcastAllowancePlaceholder();

                ImGui.TableSetColumnIndex(2);
                if (!browserBroadcastArmed && stream.Mode != StreamMode.Hosting)
                    DrawBrowserPartyButton(ImGui.GetContentRegionAvail().X);
                else
                    DrawBrowserBroadcastButton(ImGui.GetContentRegionAvail().X);
            });
    }

    private float BrowserBroadcastPanelHeight() => Ui(124f);

    private static float BrowserBroadcastActionWidth()
    {
        var labelWidth = ImGui.CalcTextSize("Create Watch Party").X;
        using var iconFont = UiFonts.PushIcon();
        return MathF.Max(Ui(186f), labelWidth + ImGui.CalcTextSize(FontAwesomeIcon.Users.ToIconString()).X + Ui(28f));
    }

    private static void DrawBrowserHelper(string text)
    {
        using var font = UiFonts.PushRelative(.90f);
        using var colour = ImRaii.PushColor(ImGuiCol.Text, MutedText);
        ImGui.TextWrapped(text);
    }

    private void DrawBrowserBroadcastQualitySelector()
    {
        var hasTier2 = HasConfiguredPatreonTier(2);
        var hasTier3 = HasConfiguredPatreonTier(3);
        var permittedQuality = Plugin.Cfg.BrowserBroadcastQuality.ForPatreonTier(ConfiguredPatreonTier());
        if (Plugin.Cfg.BrowserBroadcastQuality != permittedQuality)
        {
            Plugin.Cfg.BrowserBroadcastQuality = permittedQuality;
            Plugin.Cfg.Save();
        }

        using var uiFontScale40168=UiFonts.PushRelative(0.70f);
        ImGui.TextColored(MutedText, "BROADCAST QUALITY");
        uiFontScale40168.Dispose();

        var quality = Plugin.Cfg.BrowserBroadcastQuality;
        var preview = quality switch
        {
            BrowserBroadcastQuality.High => "High Quality",
            BrowserBroadcastQuality.Standard => "Standard Quality",
            _ => "Data Saver",
        };

        using (UiDisabled.Scope(browserBroadcastArmed))
        {
            ImGui.SetNextItemWidth(-1f);
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##browserBroadcastQuality", preview)))
            {
                if (ImGui.Selectable(
                        "Data Saver",
                        quality == BrowserBroadcastQuality.DataSaver))
                {
                    Plugin.Cfg.BrowserBroadcastQuality = BrowserBroadcastQuality.DataSaver;
                    Plugin.Cfg.Save();
                }

                if (quality == BrowserBroadcastQuality.DataSaver)
                    ImGui.SetItemDefaultFocus();

                using (UiDisabled.Scope(!hasTier2))
                {
                    if (ImGui.Selectable(
                            "Standard Quality  (Patreon Tier 2)",
                            quality == BrowserBroadcastQuality.Standard))
                    {
                        Plugin.Cfg.BrowserBroadcastQuality = BrowserBroadcastQuality.Standard;
                        Plugin.Cfg.Save();
                    }
                }
                if (!hasTier2 &&
                    ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    UiFonts.SetTooltip("Available with patreon tier 2");
                }

                using (UiDisabled.Scope(!hasTier3))
                {
                    if (ImGui.Selectable("High Quality (Patreon Tier 3)", quality == BrowserBroadcastQuality.High))
                    {
                        Plugin.Cfg.BrowserBroadcastQuality = BrowserBroadcastQuality.High;
                        Plugin.Cfg.Save();
                    }
                }
                if (!hasTier3 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    UiFonts.SetTooltip("Available with Patreon Tier 3 or higher");
                ImGui.EndCombo();
            }
        }

        if (browserBroadcastArmed &&
            ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            UiFonts.SetTooltip("Stop broadcasting to change quality.");
        }

        using var uiFontScale42730=UiFonts.PushRelative(0.70f);
        ImGui.TextColored(
            MutedText,
            quality switch
            {
                BrowserBroadcastQuality.High => "960 × 540 · ~1,900 Kbps",
                BrowserBroadcastQuality.Standard => "960 × 540 · ~1,500 Kbps",
                _ => "640 × 360 · ~700 Kbps",
            });
        using (ImRaii.TextWrapPos(0f))
            ImGui.TextColored(MutedText, "Higher quality consumes more broadcast allowance credits.");
        uiFontScale42730.Dispose();

        if (!hasTier2)
            DrawBrowserStandardQualityPatreonLink();

        ImGui.Dummy(UiVec(0f, 4f));
        DrawBrowserBroadcastFpsSelector();
    }

    private void DrawBrowserBroadcastFpsSelector()
    {
        var fps = BrowserBroadcastQualityProfiles.NormalizeFrameRate(Plugin.Cfg.BrowserBroadcastFramesPerSecond);
        if (fps != Plugin.Cfg.BrowserBroadcastFramesPerSecond)
        {
            Plugin.Cfg.BrowserBroadcastFramesPerSecond = fps;
            Plugin.Cfg.Save();
        }
        using var uiFontScale43777=UiFonts.PushRelative(0.70f);
        ImGui.TextColored(MutedText, "BROADCAST FPS");
        uiFontScale43777.Dispose();
        using (UiDisabled.Scope(browserBroadcastArmed))
        {
            ImGui.SetNextItemWidth(-1f);
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##browserBroadcastFps", $"{fps} FPS")))
            {
                foreach (var option in new[]
                {
                    (15, "15 FPS (Least CPU usage)"),
                    (20, "20 FPS (Suggested for saver / standard)"),
                    (25, "25 FPS (Suggested for high quality)"),
                    (30, "30 FPS (suggested for video streams)"),
                })
                {
                    if (ImGui.Selectable(option.Item2, fps == option.Item1))
                    {
                        Plugin.Cfg.BrowserBroadcastFramesPerSecond = option.Item1;
                        Plugin.Cfg.Save();
                    }
                    if (fps == option.Item1) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                UiFonts.SetTooltip("If you experience lag or viewers report broadcast stream issues then lower FPS");
        }
    }

    private void DrawBrowserStandardQualityPatreonLink()
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, Ui(18f));
        ImGui.InvisibleButton("##browserStandardQualityPatreon", size);
        var color = ImGui.IsItemHovered()
            ? new Vector4(1f, 0.67f, 0.27f, 1f)
            : PatreonOrange;
        var draw = ImGui.GetWindowDrawList();
        using (UiFonts.PushIcon())
            UiFonts.DrawText(draw, ImGui.GetFontSize(), origin + UiVec(1f, 1f), ImGui.GetColorU32(color), FontAwesomeIcon.Lock.ToIconString());
        UiFonts.DrawText(draw, ImGui.GetFontSize() * 0.70f, origin + UiVec(20f, 2f), ImGui.GetColorU32(color), "Standard Quality available with Patreon Tier 2.");
        if (ImGui.IsItemClicked())
            OpenPatreonMembershipPage();
    }

    private void DrawBrowserBroadcastAllowancePlaceholder()
    {
        if (DrawAccountBroadcastRestriction("Browser")) return;
        var allowance = BroadcastAllowanceDisplayFor(
            BroadcastAllowanceMethod.Browser);
        var remaining = allowance.Remaining;
        var remainingLabel = allowance.RemainingLabel;

        using (UiFonts.PushRelative(.76f))
        {
            var width = ImGui.GetContentRegionAvail().X;
            var startX = ImGui.GetCursorPosX();
            var headingWidth = ImGui.CalcTextSize(allowance.Heading).X;
            var remainingWidth = ImGui.CalcTextSize(remainingLabel).X;
            using (var wrap = ImRaii.TextWrapPos(0f)) ImGui.TextColored(MutedText, allowance.Heading);
            if (headingWidth + Ui(10f) + remainingWidth <= width)
                ImGui.SameLine(startX + width - remainingWidth);
            ImGui.TextColored(MutedText, remainingLabel);
        }
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, Ui(14f));
        var fillSize = new Vector2(size.X * remaining, size.Y);
        var draw = ImGui.GetWindowDrawList();
        ImGui.Dummy(size);

        draw.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(CardBg),
            Ui(7f));
        draw.AddRectFilled(
            origin,
            origin + fillSize,
            ImGui.GetColorU32(Accent),
            Ui(7f));
        draw.PushClipRect(origin, origin + fillSize, true);
        var stripeStep = Ui(13f);
        for (var x = origin.X - size.Y;
             x < origin.X + fillSize.X + size.Y;
             x += stripeStep)
        {
            draw.AddLine(
                new Vector2(x, origin.Y + size.Y),
                new Vector2(x + size.Y, origin.Y),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.16f)),
                Ui(4f));
        }
        draw.PopClipRect();
        draw.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(BorderSubtle),
            Ui(7f));

        using var uiFontScale48242=UiFonts.PushRelative(0.68f);
        ImGui.TextColored(
            MutedText,
            allowance.Detail);
        DrawPatreonUpgradeLink(ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X);
        uiFontScale48242.Dispose();
    }

    private void DrawBrowserPartyButton(float width)
    {
        using var disabled = UiDisabled.Scope(
            CurrentSession is null ||
            stream.Mode == StreamMode.Viewing);
        if (GameLayoutButton(stream.Mode == StreamMode.Viewing ? "Host required" : "Create Watch Party", FontAwesomeIcon.Users, width))
        {
            pendingWatchPartyMediaKind = PendingWatchPartyMediaKind.GameRoom;
            watchPartyCreationPopupOpen = true;
            createRoomPassword = string.Empty;
            createLockedRoomPasswordError = null;
        }
        if (stream.Mode == StreamMode.Viewing && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiFonts.SetTooltip("You must host a Watch Party to broadcast your browser.");
    }

    private bool CanStartBrowserBroadcast() => stream.Mode == StreamMode.Hosting && CurrentSession is { } session &&
        !string.IsNullOrWhiteSpace(Plugin.Cfg.StreamKeys.GetValueOrDefault(session.AccountId));

    private void DrawBrowserBroadcastButton(float width)
    {
        if (browserBroadcastArmed)
        {
            if (GameLayoutButton(browserBroadcastStartFailed ? "Stop Failed Broadcast" : screenController.Engine.IsBrowserBroadcasting ? "Stop Broadcast" : "Waiting for Viewers",
                    FontAwesomeIcon.Stop, width, danger: true)) StopBrowserWatchPartyBroadcast();
            return;
        }

        var canStart = CanStartBrowserBroadcast();
        using var disabled = UiDisabled.Scope(!canStart);
        if (GameLayoutButton("Start Broadcast", FontAwesomeIcon.BroadcastTower, width, true)) activeGameDialog = "Browser broadcast";
        DrawMissingBroadcastStreamKeyTooltip();
    }

    private void StartBrowserWatchPartyBroadcast()
    {
        if (!CanStartOtherStudioMedia()) return;
        if (CurrentSession is not { } session || stream.Mode != StreamMode.Hosting) return;
        if (!CanStartBroadcastWithAllowance(BroadcastAllowanceMethod.Browser)) return;
        var key = Plugin.Cfg.StreamKeys.GetValueOrDefault(session.AccountId);
        if (string.IsNullOrWhiteSpace(key)) return;
        var engine = screenController.Engine;
        if (!engine.IsActive) engine.RespawnScreen();
        browserBroadcastPublishUrl = $"{BuildRtmpServer()}/{key}";
        browserBroadcastHlsUrl = $"{BuildMyHlsUrl(session)}?broadcast={Guid.NewGuid():N}&ac_content=web-browsing";
        browserBroadcastStartFailed = false;
        browserBroadcastExpectedRunning = false;
        nextBrowserBroadcastRetryUtc = DateTime.MinValue;
        browserBroadcastArmed = true;
    }

    internal void UpdateBrowserBroadcastDemand()
    {
        if (!browserBroadcastArmed) return;
        var engine = screenController.Engine;
        if (stream.Mode != StreamMode.Hosting || !engine.IsPlayingBrowser || string.IsNullOrWhiteSpace(browserBroadcastPublishUrl))
        {
            StopBrowserWatchPartyBroadcast();
            return;
        }
        var hasViewer = stream.Roster.Length > 0;
        if (!hasViewer)
        {
            if (engine.IsBrowserBroadcasting) engine.StopBrowserBroadcast();
            browserBroadcastStartFailed = false;
            browserBroadcastExpectedRunning = false;
            nextBrowserBroadcastRetryUtc = DateTime.MinValue;
            return;
        }
        if (browserBroadcastExpectedRunning && !engine.IsBrowserBroadcasting)
        {
            browserBroadcastExpectedRunning = false;
            browserBroadcastStartFailed = true;
            nextBrowserBroadcastRetryUtc = DateTime.UtcNow.AddSeconds(10);
        }
        if (browserBroadcastStartFailed && DateTime.UtcNow >= nextBrowserBroadcastRetryUtc)
            browserBroadcastStartFailed = false;
        if (!engine.IsBrowserBroadcasting && !browserBroadcastStartFailed)
        {
            var quality = Plugin.Cfg.BrowserBroadcastQuality.ForPatreonTier(ConfiguredPatreonTier());

            browserBroadcastStartFailed =
                !engine.StartBrowserBroadcast(
                    browserBroadcastPublishUrl,
                    quality, Plugin.Cfg.BrowserBroadcastFramesPerSecond);
            browserBroadcastExpectedRunning = !browserBroadcastStartFailed;
            if (browserBroadcastExpectedRunning)
                { }
            if (browserBroadcastStartFailed)
                nextBrowserBroadcastRetryUtc = DateTime.UtcNow.AddSeconds(10);
        }
    }

    private void StopBrowserWatchPartyBroadcast()
    {
        screenController.Engine.StopBrowserBroadcast();
        browserBroadcastArmed = false;
        browserBroadcastStartFailed = false;
        browserBroadcastExpectedRunning = false;
        nextBrowserBroadcastRetryUtc = DateTime.MinValue;
        browserBroadcastPublishUrl = null;
        browserBroadcastHlsUrl = null;
    }

    private void RefreshBrowserPatreonAccess()
    {
        if (!HasConfiguredPatreonAccess())
        {
            patreonAccessConfirmed = false;
            return;
        }

        patreonAccessConfirmed = true;
    }

    private void DrawBrowserRefreshAccessButton(string id, float width)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, Ui(28f));
        if (ImGui.InvisibleButton(id, size))
        {
            RefreshBrowserPatreonAccess();
        }

        var hovered = ImGui.IsItemHovered();
        var label = "Refresh access";
        var textSize = ImGui.CalcTextSize(label);
        var textOrigin = origin + new Vector2(
            (size.X - textSize.X) * 0.5f,
            (size.Y - textSize.Y) * 0.5f);
        var color = hovered ? AccentHover : MutedText;
        var drawList = ImGui.GetWindowDrawList();
        UiFonts.DrawText(drawList, ImGui.GetFontSize(), textOrigin, ImGui.GetColorU32(color), label);
        drawList.AddLine(
            new Vector2(textOrigin.X, textOrigin.Y + textSize.Y + Ui(1f)),
            new Vector2(textOrigin.X + textSize.X, textOrigin.Y + textSize.Y + Ui(1f)),
            ImGui.GetColorU32(color),
            Ui(1f));
    }
}

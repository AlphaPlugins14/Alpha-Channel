using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly (FontAwesomeIcon Icon, string Text)[] BrowserHubChromiumFeatures =
    [
        (FontAwesomeIcon.Desktop, "Windows + Linux"),
        (FontAwesomeIcon.PlayCircle, "YouTube and general browsing"),
        (FontAwesomeIcon.Ban, "Limited streaming service support")
    ];
    private static readonly (FontAwesomeIcon Icon, string Text)[] BrowserHubEdgeFeatures =
    [
        (FontAwesomeIcon.Desktop, "Windows only"),
        (FontAwesomeIcon.Video, "Broader streaming compatibility"),
        (FontAwesomeIcon.Film, "Supports paid streaming platforms")
    ];

    private void DrawBrowserLaunchPage()
    {
        using var id = ImRaii.PushId("browserHub");
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8, 8));
        using var frames = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(10, 6))
            .Push(ImGuiStyleVar.FrameRounding, Ui(7));
        DrawBrowserHubBanner();
        SetGameLayoutRowGap(Ui(14));
        var width = ImGui.GetContentRegionAvail().X;
        var columns = width >= Ui(620);
        var cardWidth = columns ? (width - Ui(14)) / 2 : width;
        var height = MathF.Max(BrowserHubCardHeight(cardWidth, BrowserHubChromiumFeatures),
            BrowserHubCardHeight(cardWidth, BrowserHubEdgeFeatures));
        DrawBrowserHubCard(false, cardWidth, height);
        if (columns) ImGui.SameLine(0, Ui(14));
        else SetGameLayoutRowGap(Ui(14));
        DrawBrowserHubCard(true, cardWidth, height);
        SetGameLayoutRowGap(Ui(14));
        DrawBrowserHubSharing(width);
        SetGameLayoutRowGap(Ui(14));
        DrawBrowserHubPrivacy(width);
        if (webViewSetupTask is not null)
            ImGui.TextColored(MutedText, "Preparing Edge (WebView2)... The Microsoft Evergreen runtime may need to install first.");
        var error = browserError ?? screenController.Engine.LastError;
        if (!string.IsNullOrWhiteSpace(error))
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, Danger);
            ImGui.TextWrapped(error);
        }
    }

    private void DrawBrowserHubBanner()
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var art = width >= Ui(650);
        var size = new Vector2(width, Ui(148));
        ImGui.Dummy(size);
        var draw = ImGui.GetWindowDrawList();
        DrawArcadeGradientPanel(draw, origin, size, HubSurface(0x0D203B), HubSurface(0x050B17), Accent, 10);
        draw.PushClipRect(origin + UiVec(1, 1), origin + size - UiVec(1, 1), true);
        var textWidth = width - Ui(art ? 240 : 40);
        using (UiFonts.PushRegularRelative(.8f))
            draw.AddText(origin + UiVec(20, 20), ImGui.GetColorU32(AccentHover), "E X P L O R E  T O G E T H E R");
        using (UiFonts.PushRelative(1.9f, emphasis: true))
            DrawWrappedLines(draw, origin + UiVec(20, 45), textWidth, ImGui.GetTextLineHeight(), 2,
                ImGui.GetColorU32(Vector4.One), "Your window to the web.", out _);
        using (UiFonts.PushRegularRelative(.96f))
            DrawWrappedLines(draw, origin + UiVec(20, 105), textWidth, ImGui.GetTextLineHeight(), 2,
                ImGui.GetColorU32(MutedText), "Browse, stream and share with your Watch Party.", out _);
        if (art) DrawBrowserHubArtwork(draw, origin + new Vector2(width - Ui(220), Ui(18)));
        draw.PopClipRect();
    }

    private static void DrawBrowserHubArtwork(ImDrawListPtr draw, Vector2 origin)
    {
        void Window(Vector2 position, Vector2 size)
        {
            draw.AddRectFilled(position, position + size, ImGui.GetColorU32(HubSurface(0x0C1B33)), Ui(6));
            draw.AddRectFilled(position, position + new Vector2(size.X, Ui(19)), ImGui.GetColorU32(HubSelected(0x17345D)), Ui(6));
            draw.AddRect(position, position + size, ImGui.GetColorU32(HubTint(0x3489F5)), Ui(6));
            for (var i = 0; i < 3; i++)
                draw.AddCircleFilled(position + UiVec(10 + i * 9, 9), Ui(2), ImGui.GetColorU32(AccentHover));
            draw.AddLine(position + UiVec(40, 9), position + new Vector2(size.X - Ui(10), Ui(9)), ImGui.GetColorU32(Accent), Ui(3));
        }
        Window(origin + UiVec(48, 0), UiVec(148, 96));
        var play = origin + UiVec(165, 57);
        draw.AddTriangleFilled(play + UiVec(-8, -12), play + UiVec(-8, 12), play + UiVec(10, 0), ImGui.GetColorU32(AccentHover));
        Window(origin + UiVec(0, 24), UiVec(146, 100));
        var center = origin + UiVec(73, 85);
        var ink = ImGui.GetColorU32(AccentHover);
        draw.AddCircle(center, Ui(30), ink, 36, Ui(2));
        for (var i = 0; i < 36; i++)
        {
            var a = i * MathF.Tau / 36;
            var b = (i + 1) * MathF.Tau / 36;
            draw.AddLine(center + UiVec(14 * MathF.Cos(a), 30 * MathF.Sin(a)),
                center + UiVec(14 * MathF.Cos(b), 30 * MathF.Sin(b)), ink, Ui(2));
        }
        draw.AddLine(center + UiVec(-30, 0), center + UiVec(30, 0), ink, Ui(2));
        draw.AddLine(center + UiVec(-25, -15), center + UiVec(25, -15), ink, Ui(2));
        draw.AddLine(center + UiVec(-25, 15), center + UiVec(25, 15), ink, Ui(2));
    }

    private static float BrowserHubCardHeight(float width, (FontAwesomeIcon Icon, string Text)[] features)
    {
        using var font = UiFonts.PushRegularRelative(.9f);
        var rows = features.Sum(feature => MathF.Max(Ui(28), ImGui.CalcTextSize(feature.Text, false, MathF.Max(Ui(60), width - Ui(80))).Y) + Ui(12));
        return BrowserHubHeaderHeight(width) + Ui(74) + rows;
    }

    private static float BrowserHubHeaderHeight(float width)
    {
        using var font = UiFonts.PushRegularRelative(.94f);
        var descriptionHeight = MathF.Max(
            ImGui.CalcTextSize("Watch more of the web.", false, width - Ui(122)).Y,
            ImGui.CalcTextSize("Fast, familiar browsing.", false, width - Ui(122)).Y);
        return MathF.Max(Ui(126), Ui(86) + descriptionHeight + Ui(16));
    }

    private void DrawBrowserHubCard(bool edge, float width, float height)
    {
        BrowserHubPanel(edge ? "edge" : "chromium", width, height, () =>
        {
            var logoOrigin = ImGui.GetCursorScreenPos();
            var logo = GetCapabilityImage(edge ? "webview2-browser.png" : "chromium-browser.png")?.GetWrapOrDefault();
            if (logo is not null)
                ImGui.GetWindowDrawList().AddImage(logo.Handle, logoOrigin, logoOrigin + UiVec(68, 68));
            else UiFonts.DrawIcon(ImGui.GetWindowDrawList(), Ui(56), logoOrigin, ImGui.GetColorU32(Accent), FontAwesomeIcon.Globe.ToIconString());
            ImGui.SetCursorPos(UiVec(104, 18));
            using (UiFonts.PushRelative(1.5f, emphasis: true))
                ImGui.TextUnformatted(TruncateToWidth(edge ? EdgeBrowserLabel : "Chromium", width - Ui(122)));
            if (edge && ImGui.IsItemHovered()) UiFonts.SetTooltip(EdgeBrowserDescription);
            ImGui.SetCursorPos(UiVec(104, 51));
            using (UiFonts.PushRegularRelative(.86f))
            {
                var badge = edge ? "Streaming" : "Everyday browsing";
                var badgeOrigin = ImGui.GetCursorScreenPos();
                var badgeSize = ImGui.CalcTextSize(badge) + UiVec(18, 8);
                var draw = ImGui.GetWindowDrawList();
                draw.AddRectFilled(badgeOrigin, badgeOrigin + badgeSize, ImGui.GetColorU32(HubSelected(0x17345D)), Ui(10));
                draw.AddRect(badgeOrigin, badgeOrigin + badgeSize, ImGui.GetColorU32(AccentHover), Ui(10));
                draw.AddText(badgeOrigin + UiVec(9, 4), ImGui.GetColorU32(Vector4.One), badge);
                ImGui.Dummy(badgeSize);
            }
            ImGui.SetCursorPos(UiVec(104, 86));
            using (UiFonts.PushRegularRelative(.94f))
            using (ImRaii.TextWrapPos(width - Ui(18)))
                ImGui.TextColored(MutedText, edge ? "Watch more of the web." : "Fast, familiar browsing.");
            ImGui.SetCursorPosY(BrowserHubHeaderHeight(width));
            ImGui.Separator();
            ImGui.Spacing();
            foreach (var feature in edge ? BrowserHubEdgeFeatures : BrowserHubChromiumFeatures)
            {
                var row = ImGui.GetCursorScreenPos();
                using var font = UiFonts.PushRegularRelative(.9f);
                var textWidth = MathF.Max(Ui(60), width - Ui(80));
                var rowHeight = MathF.Max(Ui(28), ImGui.CalcTextSize(feature.Text, false, textWidth).Y);
                UiFonts.DrawCenteredIcon(ImGui.GetWindowDrawList(), Ui(23), row + new Vector2(Ui(12), rowHeight / 2), ImGui.GetColorU32(Vector4.One), feature.Icon.ToIconString());
                ImGui.SetCursorScreenPos(row + new Vector2(Ui(42), (rowHeight - ImGui.CalcTextSize(feature.Text, false, textWidth).Y) / 2));
                using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + textWidth)) ImGui.TextUnformatted(feature.Text);
                ImGui.SetCursorScreenPos(row + new Vector2(0, rowHeight + Ui(12)));
            }
            ImGui.SetCursorPos(new Vector2(Ui(18), height - Ui(56)));
            DrawBrowserHubLaunchButton(edge, width - Ui(36));
        });
    }

    private void DrawBrowserHubLaunchButton(bool edge, float width)
    {
        var chromium = screenController.Engine.Resources.Chromium;
        var downloading = !edge && chromium.Status == ChromiumPackageStatus.Downloading;
        var retry = !edge && chromium.Status == ChromiumPackageStatus.Failed;
        using var disabled = UiDisabled.Scope(webViewSetupTask is not null || downloading);
        var label = edge ? "Launch Edge" : downloading ? "Downloading Chromium" : retry ? "Retry Chromium Download" : "Launch Chromium";
        if (GameLayoutButton(label, edge ? FontAwesomeIcon.Play : FontAwesomeIcon.Globe, width, true, height: 36))
        {
            browserError = null;
            if (edge) StartWebViewBrowser();
            else if (retry) screenController.Engine.Resources.RetryChromiumDownload();
            else if (chromium.Status == ChromiumPackageStatus.Waiting) screenController.Engine.Resources.PrepareChromium();
            else if (!screenController.Engine.PlayBrowser(NormalizeBrowserAddress(browserAddress))) browserError = screenController.Engine.LastError;
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            if (downloading) UiFonts.SetTooltip("Downloading Chromium files, please check back soon.");
            else if (retry) UiFonts.SetTooltip(chromium.Error ?? "Chromium download failed. Click to retry.");
        }
    }

    private void DrawBrowserHubSharing(float width)
    {
        var columns = width >= Ui(680);
        var textWidth = width - Ui(columns ? 322 : 102);
        var paragraphInset = Ui(84);
        var paragraphWidth = width - paragraphInset * 2;
        const string detail = "Share your browser picture and audio with your Watch Party.";
        const string control = "Your friends watch the stream while you control the browser.";
        const string sharingPrivacy = "Your Watch Party viewers only see a video stream of the browser (think screen sharing). They do not load any web pages themselves or receive any details from the browser such as login sessions.";
        float textHeight;
        using (UiFonts.PushRegularRelative(.9f))
            textHeight = ImGui.CalcTextSize(detail, false, textWidth).Y + ImGui.CalcTextSize(control, false, textWidth).Y
                + ImGui.CalcTextSize(sharingPrivacy, false, paragraphWidth).Y + ImGui.GetStyle().ItemSpacing.Y;
        BrowserHubPanel("sharing", width, Ui(columns ? 86 : 136) + textHeight, () =>
        {
            var origin = ImGui.GetCursorPos();
            UiFonts.DrawCenteredIcon(ImGui.GetWindowDrawList(), Ui(48), ImGui.GetCursorScreenPos() + UiVec(24, 32),
                ImGui.GetColorU32(AccentHover), FontAwesomeIcon.Users.ToIconString());
            ImGui.SetCursorPos(origin + UiVec(66, 0));
            ImGui.BeginGroup();
            using (UiFonts.PushRelative(1.2f, emphasis: true)) ImGui.TextUnformatted("Browse together");
            using (UiFonts.PushRegularRelative(.9f))
            using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + textWidth))
            {
                ImGui.TextColored(MutedText, detail);
                ImGui.TextColored(MutedText, control);
            }
            ImGui.EndGroup();
            var textBottom = ImGui.GetCursorPosY();
            if (columns) ImGui.SetCursorPos(new Vector2(width - Ui(220), Ui(30)));
            else ImGui.SetCursorPosX(origin.X);
            if (stream.Mode == StreamMode.Hosting)
            {
                if (GameLayoutButton("Open Watch Party", FontAwesomeIcon.Users, columns ? Ui(202) : width - Ui(36))) currentPage = HomePage.WatchAlong;
            }
            else DrawBrowserPartyButton(columns ? Ui(202) : width - Ui(36));
            ImGui.SetCursorPos(new Vector2(paragraphInset, MathF.Max(textBottom, ImGui.GetCursorPosY())));
            using (UiFonts.PushRegularRelative(.9f))
            using (ImRaii.TextWrapPos(width - paragraphInset))
                ImGui.TextColored(MutedText, sharingPrivacy);
        });
    }

    private void DrawBrowserHubPrivacy(float width)
    {
        var columns = width >= Ui(600);
        var sectionWidth = columns ? (width - Ui(66)) / 2 : width - Ui(36);
        const string privacy = "Enable privacy mode before entering personal details to hide your browser picture and audio from viewers.";
        const string sessions = "Sign-ins are separate between browsers. Browsing history is shared.";
        float leftHeight, rightHeight;
        using (UiFonts.PushRegularRelative(.9f))
        {
            leftHeight = ImGui.CalcTextSize(privacy, false, sectionWidth - Ui(60)).Y;
            rightHeight = ImGui.CalcTextSize(sessions, false, sectionWidth - Ui(60)).Y;
        }
        var height = columns ? Ui(76) + MathF.Max(leftHeight, rightHeight) : Ui(128) + leftHeight + rightHeight;
        BrowserHubPanel("privacy", width, height, () =>
        {
            var start = ImGui.GetCursorPos();
            Section(FontAwesomeIcon.EyeSlash, "Private sign-in", privacy);
            if (columns)
            {
                var divider = ImGui.GetWindowPos() + new Vector2(width / 2, Ui(16));
                ImGui.GetWindowDrawList().AddLine(divider, divider + new Vector2(0, height - Ui(32)), ImGui.GetColorU32(HubTint(0x24456B)));
                ImGui.SetCursorPos(start + new Vector2(sectionWidth + Ui(30), 0));
            }
            else ImGui.Spacing();
            Section(FontAwesomeIcon.Globe, "Browser sessions", sessions);
        });

        void Section(FontAwesomeIcon icon, string heading, string text)
        {
            ImGui.BeginGroup();
            var start = ImGui.GetCursorPos();
            UiFonts.DrawCenteredIcon(ImGui.GetWindowDrawList(), Ui(42), ImGui.GetCursorScreenPos() + UiVec(22, 30),
                ImGui.GetColorU32(AccentHover), icon.ToIconString());
            ImGui.SetCursorPos(start + UiVec(60, 0));
            using (UiFonts.PushRelative(1.08f, emphasis: true)) ImGui.TextUnformatted(heading);
            ImGui.SetCursorPosX(start.X + Ui(60));
            using (UiFonts.PushRegularRelative(.9f))
            using (ImRaii.TextWrapPos(start.X + sectionWidth)) ImGui.TextColored(MutedText, text);
            ImGui.EndGroup();
        }
    }

    private void BrowserHubPanel(string id, float width, float height, Action content)
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(18, 18));
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10));
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, HubSurface(0x0D1729))
            .Push(ImGuiCol.Border, HubTint(0x24456B));
        using var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(id, new Vector2(width, height), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse));
        if (panel) content();
    }
}

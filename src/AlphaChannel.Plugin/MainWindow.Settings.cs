using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

// Settings uses stacked, labeled preference panels, separate from the media cards
// used on Home/Player.
internal sealed partial class MainWindow
{

    // ---------------------------------------------------------
    // YouTube subscription management
    // ---------------------------------------------------------

    private string subscriptionChannelInput = string.Empty;
    private bool isAddingManualSubscription;
    private string? subscriptionMessage;
    private bool subscriptionMessageIsError;

    private string? subscriptionClipboardMessage;
    private bool subscriptionClipboardMessageIsError;
    private bool manageSubscriptionsOpen;
    private bool manageTopicsOpen;

    private sealed class SubscriptionClipboardPayload
    {
        public string? Type { get; set; }

        public int Version { get; set; }

        public List<SubscriptionClipboardChannel>? Channels { get; set; }
    }

    private sealed class SubscriptionClipboardChannel
    {
        public string? ChannelId { get; set; }

        public string? ChannelName { get; set; }
    }

    private bool topicSelectionLimitWarning;

    private readonly HashSet<string> subscriptionNamesLoading =
    new(StringComparer.OrdinalIgnoreCase);

    private static readonly (string Text, string Glow)[] HonorificColorPresets =
    [
        ("#A92FCD", "#4B1642"),
        ("#CD2F2F", "#4B1616"),
        ("#CD9C2F", "#4B4216"),
        ("#57CD2F", "#264B16"),
        ("#2FCDB6", "#164B42"),
        ("#2F39CD", "#161C4B"),
    ];

    private enum SettingsTab
    {
        Account,
        Profile,
        Appearance,
        YouTube,
        Other,
    }

    private SettingsTab settingsTab = SettingsTab.Account;

    // Keep controls opaque even over custom wallpaper, and preserve the selected
    // background's hue while separating fields from the surrounding cards.
    private static bool settingsControlsActive;
    private static Vector4 SettingsControlBg => Vector4.Lerp(Colors.CardBg, new Vector4(0f, 0f, 0f, 1f), 0.48f);
    private static Vector4 SettingsControlHover => Vector4.Lerp(SettingsControlBg, Colors.Accent, 0.22f);

    private void DrawSettings()
    {
        var previous = settingsControlsActive;
        settingsControlsActive = true;
        try
        {
            using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, Ui(1f)).Push(ImGuiStyleVar.ItemSpacing,UiVec(8f,6f));
            using var colors = ImRaii.PushColor(ImGuiCol.FrameBg, SettingsControlBg)
                .Push(ImGuiCol.FrameBgHovered, SettingsControlHover)
                .Push(ImGuiCol.FrameBgActive, SettingsControlHover)
                .Push(ImGuiCol.Border, SettingsReviewBorder);
            DrawSettingsContents();
        }
        finally
        {
            settingsControlsActive = previous;
        }
    }

    private void DrawSettingsContents()
    {
        // ---------------------------------------------------------
        // Settings tabs
        // ---------------------------------------------------------

        var tabGap = Ui(8f);

        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        const int settingsTabCount = 5;
        var tabWidth = (availableWidth - tabGap * (settingsTabCount - 1)) / settingsTabCount;

        DrawSettingsTab(
            SettingsTab.Account,
            "Account",
            tabWidth);

        ImGui.SameLine(0f, tabGap);

        DrawSettingsTab(
            SettingsTab.Profile,
            "Profile",
            tabWidth);

        ImGui.SameLine(0f, tabGap);

        DrawSettingsTab(
            SettingsTab.Appearance,
            "Appearance",
            tabWidth);

        ImGui.SameLine(0f, tabGap);

        DrawSettingsTab(
            SettingsTab.YouTube,
            "YouTube",
            tabWidth);

        ImGui.SameLine(0f, tabGap);

        DrawSettingsTab(
            SettingsTab.Other,
            "Other",
            tabWidth);


        ImGui.Dummy(
            UiVec(0f, 8f));

        // Divider under tabs.
        var dividerOrigin =
            ImGui.GetCursorScreenPos();

        var dividerWidth =
            ImGui.GetContentRegionAvail().X;

        ImGui.GetWindowDrawList()
            .AddRectFilled(
                dividerOrigin,
                dividerOrigin +
                new Vector2(
                    dividerWidth,
                    1f),
                ImGui.GetColorU32(
                    SettingsReviewBorder));

        ImGui.Dummy(
            new Vector2(
                dividerWidth,
                Ui(18f)));

        // ---------------------------------------------------------
        // Selected tab
        // ---------------------------------------------------------

        switch (settingsTab)
        {
            case SettingsTab.Account:
                SettingsSection(
      "Account",
      "Sign-in, username, Patreon, invite code, and live-streaming credentials.");

                DrawAccountSettings();
                break;

            case SettingsTab.Profile:
                SettingsSection(
                    "Profile",
                    "Your avatar, status, bio, and profile appearance.");

                if (CurrentSession is { } session)
                {
                    DrawProfileEditor(session);
                }
                else
                {
                    DrawPlainEmpty(
                        "Sign in to edit your profile.");
                }

                break;

            case SettingsTab.Appearance:
                DrawAppearanceSettings();
                break;

            case SettingsTab.YouTube:
                DrawYouTubeSettings();
                break;

            case SettingsTab.Other:
                DrawOtherSettings();
                break;
        }
    }

    private static void SettingsSection(string title, string blurb)
    {
        DrawSocialHeading(title,1.1f);
        DrawSettingsHelper(blurb);
        ImGui.Spacing();
    }

    private void DrawSettingsTab(
    SettingsTab tab,
    string label,
    float width)
    {
        var selected =
            settingsTab == tab;

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
                selected
                    ? Vector4.One
                    : MutedText))
        {
            if (ImGui.Button(
                $"##settingsTab_{tab}",
                new Vector2(
                    width,
                    Ui(40f))))
            {
                settingsTab = tab;
            }

            var buttonMin =
                ImGui.GetItemRectMin();

            var buttonMax =
                ImGui.GetItemRectMax();

            var labelSize =
                ImGui.CalcTextSize(label);

            UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), new Vector2(
                        buttonMin.X +
                        (buttonMax.X -
                         buttonMin.X -
                         labelSize.X) *
                        0.5f,

                        buttonMin.Y +
                        (buttonMax.Y -
                         buttonMin.Y -
                         labelSize.Y) *
                        0.5f), ImGui.GetColorU32(
                        selected
                            ? Vector4.One
                            : MutedText), label);
        }
    }

    private void DrawWindowSizeSettings()
    {
        using var uiFontScale8476=UiFonts.PushRelative(1.10f);
        ImGui.TextColored(Hex(0xE2E7EF), "Window size");
        uiFontScale8476.Dispose();

        ImGui.Dummy(UiVec(0f, 3f));
        using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
            DrawSettingsExplanation(Plugin.Cfg.NativeUiSizing
                ? "Choose Design, 1080 or 4K resize limits. 1080 allows half the Design minimum width and height. Native interface sizing follows your window width."
                : "Design uses 100% interface size; 4K uses 150%. 1080 keeps Design sizing and allows half its minimum width and height.");

        ImGui.Dummy(UiVec(0f, 12f));

        ReadOnlySpan<UiWindowSizePreset> presets =
        [
            UiWindowSizePreset.Design,
            UiWindowSizePreset.Compact1080,
            UiWindowSizePreset.Uhd,
        ];

        var gap = Ui(8f);
        var buttonWidth = MathF.Max(
            Ui(88f),
            (ImGui.GetContentRegionAvail().X - gap * (presets.Length-1)) / presets.Length);
        var buttonHeight = Ui(34f);

        for (var i = 0; i < presets.Length; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine(0f, gap);
            }

            var preset = presets[i];
            var selected = Plugin.Cfg.WindowSizePreset == preset;
            var label = $"{ThemeCatalog.Label(preset)}##windowSize_{preset}";

            using (selected
                ? ImRaii.PushColor(ImGuiCol.Button, Accent)
                    .Push(ImGuiCol.ButtonHovered, AccentHover)
                    .Push(ImGuiCol.ButtonActive, AccentActive)
                : ImRaii.PushColor(ImGuiCol.Button, FrameBg)
                    .Push(ImGuiCol.ButtonHovered, FrameBgHover)
                    .Push(ImGuiCol.ButtonActive, FrameBgHover))
            {
                if (ImGui.Button(label, new Vector2(buttonWidth, buttonHeight)) && !selected)
                {
                    ApplyWindowSizePreset(preset);
                }
            }
        }

        var applied = ClampWindowSize(userWindowSize);
        ImGui.Dummy(UiVec(0f, 10f));
        using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
        ImGui.TextWrapped(
            $"Applied  {applied.X:0} × {applied.Y:0}  — drag a window edge to resize");
    }

    private void DrawAppearanceSettings()
    {
        SettingsSection(
            "Appearance",
            "Colours and window chrome.");

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
        using (var sizeCard = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
            "##appearanceWindowSizeCard",
            new Vector2(-1f, Ui(settingsPanelMeasuredHeights.GetValueOrDefault("##appearanceWindowSizeCard", 230f))),
            true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (sizeCard)
            {
                DrawWindowSizeSettings();
                settingsPanelMeasuredHeights["##appearanceWindowSizeCard"] = ImGui.GetCursorPosY() / Ui(1f) + 18f;
            }
        }

        ImGui.Dummy(
            UiVec(0f, 8f));

        const string nativeSizingDescription =
            "Choose the text size and spacing you prefer. Changes apply immediately. " +
            "Compare both modes on the homepage to find the best fit for your screen.";
        var descriptionHeight = ImGui.CalcTextSize(
            nativeSizingDescription,
            false,
            MathF.Max(1f, ImGui.GetContentRegionAvail().X - Ui(40f))).Y;
        var nativeSizingHeight = descriptionHeight + ImGui.GetFontSize() * 1.1f +
            InterfaceSizingChoiceHeight() + ImGui.GetStyle().ItemSpacing.Y * 5f +
            Ui(40f) + ImGui.GetFrameHeight();

        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 10f)
            .Push(ImGuiStyleVar.WindowPadding, UiVec(20f, 18f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
            .Push(ImGuiCol.Border, SettingsReviewBorder))
        using (var nativeSizingCard = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
            "##appearanceNativeUiSizingCard",
            new Vector2(-1f, nativeSizingHeight),
            true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (nativeSizingCard)
            {
                using var uiFontScale13019=UiFonts.PushRelative(1.10f);
                DrawSocialHeading("Interface size");
                uiFontScale13019.Dispose();
                ImGui.Dummy(UiVec(0f, 4f));
                var choiceWidth = MathF.Min(
                    (ImGui.GetContentRegionAvail().X - Ui(10f)) * .5f,
                    MathF.Max(Ui(220f), ImGui.CalcTextSize("Standard").X + Ui(84f)));
                if (DrawInterfaceSizingChoice("Standard", !Plugin.Cfg.NativeUiSizing, choiceWidth))
                {
                    Plugin.Cfg.NativeUiSizing = false;
                    Plugin.Cfg.HasCompletedInterfaceSizingSetup = true;
                    Plugin.Cfg.Save();
                }
                ImGui.SameLine(0f, Ui(10f));
                if (DrawInterfaceSizingChoice("Native", Plugin.Cfg.NativeUiSizing, choiceWidth))
                {
                    Plugin.Cfg.NativeUiSizing = true;
                    Plugin.Cfg.HasCompletedInterfaceSizingSetup = true;
                    Plugin.Cfg.Save();
                }
                DrawSettingsExplanation(nativeSizingDescription);
                if (ImGui.Button("Compare interface sizes"))
                    OpenInterfaceSizingComparison();
            }
        }
        ImGui.Dummy(UiVec(0f, 8f));

        // =========================================================
        // ACCENT COLOUR
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
        using (var accentCard = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
            "##appearanceAccentCard",
            new Vector2(-1f, Ui(205f)),
            true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (accentCard)
            {
                using var uiFontScale14989=UiFonts.PushRelative(1.10f);

                DrawSocialHeading("Accent colour",1.1f);

                uiFontScale14989.Dispose();

                ImGui.Dummy(
                    UiVec(0f, 3f));

                DrawSettingsHelper("Choose the highlight colour used for buttons, tabs and selected items.");

                ImGui.Dummy(
                    UiVec(0f, 8f));

                var available =
                    ImGui.GetContentRegionAvail().X;

                // On normal Settings widths, put the controls left
                // and the mini preview on the right.
                var previewWidth = Ui(210f);
                var previewHeight = Ui(82f);
                var previewGap = Ui(24f);

                var optionsWidth =
                    MathF.Max(
                        280f,
                        available -
                        previewWidth -
                        previewGap);

                ImGui.BeginGroup();

                var startX =
                    ImGui.GetCursorPosX();

                ImGui.PushItemWidth(
                    optionsWidth);

                DrawThemeSettings(
                    optionsWidth);

                ImGui.PopItemWidth();

                ImGui.EndGroup();

                ImGui.SameLine(
                    0f,
                    previewGap);

                DrawAccentPreview(
                    new Vector2(
                        previewWidth,
                        previewHeight));
            }
        }

        ImGui.Dummy(
            UiVec(0f, 8f));

        // =========================================================
        // APP BACKGROUND
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
        using (var backgroundCard = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
            "##appearanceBackgroundCard",
            new Vector2(-1f, Ui(230f)),
            true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (backgroundCard)
            {
                using var uiFontScale17565=UiFonts.PushRelative(1.10f);

                DrawSocialHeading("Plugin background",1.1f);

                uiFontScale17565.Dispose();

                ImGui.Dummy(
                    UiVec(0f, 3f));

                DrawSettingsHelper("Choose the background used throughout AlphaChannel.");

                ImGui.Dummy(
                    UiVec(0f, 8f));

                ImGui.TextColored(
                    Vector4.One,
                    "Background style");

                ImGui.Dummy(
                    UiVec(0f, 8f));

                DrawBackgroundSettings();
            }
        }

        ImGui.Dummy(UiVec(0f,8f));
        DrawDecorationThemeSettings();

    }

    private void DrawYouTubeSettings()
    {
        SettingsSection(
            "YouTube settings",
            "Manage YouTube data and video settings");

        DrawOtherSettingsPanel(
            "##otherYouTubeQualityCard",
            250f,
            () =>
            {
                SettingsSection(
                    "YouTube video quality",
                    "Choose between faster startup or higher-resolution YouTube playback.");

                var preferHighQualityYouTube =
                    Plugin.Cfg.PreferHighQualityYouTubeVideos;

                if (ImGui.Checkbox(
                        "Prefer higher-resolution YouTube videos",
                        ref preferHighQualityYouTube))
                {
                    Plugin.Cfg.PreferHighQualityYouTubeVideos =
                        preferHighQualityYouTube;
                    Plugin.Cfg.Save();
                }

                ImGui.Dummy(UiVec(0f, 6f));

                if (preferHighQualityYouTube)
                {
                    ImGui.TextColored(
                        new Vector4(1.00f, 0.72f, 0.28f, 1.00f),
                        "Higher-resolution mode is enabled.");

                    ImGui.PushTextWrapPos(
                        ImGui.GetCursorPosX() +
                        Math.Max(ImGui.GetContentRegionAvail().X, 200f));
                    DrawSettingsHelper("YouTube videos may take between 5–10 seconds longer to begin " +
                        "playing when high resolution is enabled due to the way Alpha " +
                        "Channel has to fetch those videos.");
                    ImGui.PopTextWrapPos();
                }
                else
                {
                    ImGui.PushTextWrapPos(
                        ImGui.GetCursorPosX() +
                        Math.Max(ImGui.GetContentRegionAvail().X, 200f));
                    DrawSettingsHelper("When this setting is enabled Alpha Channel will try to fetch higher resolution videos " +
                        "but it can delay the start of the YouTube videos by 2 to 8 seconds.");
                    ImGui.PopTextWrapPos();
                }

                ImGui.Dummy(UiVec(0f, 8f));
                DrawSettingsHelper("Changes apply to the next YouTube video.");
            });

        DrawHiddenYouTubeChannelSettings();

        // Profile details, wrapped copy and reconnect notices change the content
        // height. Let this card fit its contents instead of estimating each state.
        if (youtubeConnectionScrollFrames > 0)
        {
            youtubeConnectionScrollFrames--;
            ImGui.SetScrollFromPosY(ImGui.GetCursorPosY(), .05f);
        }
        DrawOtherSettingsPanel(
            "##youtubeAccountCard",
            1f,
            () =>
            {
                using (ImRaii.TextWrapPos(0f))
                    SettingsSection(
                        "YouTube account",
                        "Connect the account signed into Alpha Channel's browser for videos that require sign-in.");
                DrawEmbeddedBrowserYouTubeAccountSettings();
            }, autoHeight: true);

        var subscriptionMessageRows =
            (isAddingManualSubscription || subscriptionMessage is not null ? 1 : 0) +
            (!string.IsNullOrWhiteSpace(subscriptionClipboardMessage) ? 1 : 0);
        DrawOtherSettingsPanel(
            "##otherYouTubeSubscriptionsCard",
            455f + subscriptionMessageRows * 24f,
            () =>
            {
                SettingsSection(
                    "YouTube subscriptions",
                    "Manage the channels used for your subscription feed.");
                DrawYouTubeSubscriptionSettings();
            });

        DrawOtherSettingsPanel(
            "##otherSubscribedTopicsCard",
            195f,
            () =>
            {
                SettingsSection(
                    "Subscribed topics",
                    "Choose the topics used for the Browse Videos Topics tab.");

                var subscribedTopicCount = GetSubscribedTopicCount();
                ImGui.TextColored(
                    subscribedTopicCount is >= 3 and <= 15
                        ? AccentHover
                        : MutedText,
                    $"{subscribedTopicCount} / 15 topics selected");

                ImGui.SameLine();
                using (UiFonts.PushIcon())
                    ImGui.TextDisabled(FontAwesomeIcon.InfoCircle.ToIconString());

                if (ImGui.IsItemHovered())
                    UiFonts.SetTooltip(
                        "Choose between 3 and 15 topics. These help Alpha Channel show relevant YouTube videos and can be changed here later.");

                ImGui.Dummy(UiVec(0f, 12f));

                DrawDjActionButton(
                    "##manageYouTubeTopics",
                    FontAwesomeIcon.List,
                    "Manage topics",
                    new Vector2(
                        ImGui.GetContentRegionAvail().X,
                        Ui(36f)),
                    false,
                    () => manageTopicsOpen = true,
                    true);
            });

    }

    private void DrawInWorldBeaconSettings()
    {
        DrawSocialHeading("In-world beacons");
        using (ImRaii.PushColor(ImGuiCol.Text,MutedText))
            ImGui.TextWrapped("Discover other watch parties around Eorzea via their in-game beacon.");
        ImGui.Spacing();
        var enabled = Plugin.Cfg.WorldMode;
        if (ImGui.Checkbox("Enable in-world beacons",ref enabled))
        {
            Plugin.Cfg.WorldMode = enabled;
            Plugin.Cfg.Save();
        }
        ImGui.Spacing();
        var sideBySide = ImGui.GetContentRegionAvail().X >= Ui(480);
        if (sideBySide && ImGui.BeginTable("##beaconSettingsPreview",2,ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Details",ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Preview",ImGuiTableColumnFlags.WidthFixed,Ui(136));
            ImGui.TableNextColumn();
            DrawInWorldBeaconHelp();
            ImGui.TableNextColumn();
            DrawInWorldBeaconPreview(enabled);
            ImGui.EndTable();
        }
        else
        {
            DrawInWorldBeaconHelp();
            ImGui.Spacing();
            DrawInWorldBeaconPreview(enabled);
        }
    }

    private void DrawInWorldBeaconHelp()
    {
        DrawSettingsExplanation("Enabling this setting lets you see other players' beacons and displays your own in-game while hosting a public Watch Party.");
        ImGui.Spacing();
        DrawSettingsExplanation("Click a beacon to view its room and instantly join that Watch Party.");
        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text,MutedText))
            DrawSettingsExplanation("If you hide your character name on the Account settings tab, your beacon will no longer be visible to anyone else, even with beacons enabled. You can still see other players' beacons.");
        if (Plugin.Cfg.WorldMode)
        {
            if (worldBeaconError is not null) ImGui.TextWrapped("Your beacon is temporarily unavailable. We'll retry automatically.");
            if (worldReadError is not null) ImGui.TextWrapped("Beacon discovery is temporarily unavailable. We'll retry automatically.");
        }
    }

    private void DrawInWorldBeaconPreview(bool enabled)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = UiVec(128,172);
        ImGui.Dummy(size);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin,origin+size,ImGui.GetColorU32(new Vector4(0.04f,0.07f,0.12f,1f)),Ui(10));
        draw.AddRect(origin,origin+size,ImGui.GetColorU32(new Vector4(0.15f,0.4f,0.55f,0.45f)),Ui(10));
        DrawWorldCrystal(draw,origin+UiVec(64,75),false,enabled?0.85f:0.4f);
        const string caption = "Beacon preview";
        var textSize = ImGui.CalcTextSize(caption);
        draw.AddText(origin+new Vector2((size.X-textSize.X)*0.5f,Ui(149)),ImGui.GetColorU32(MutedText),caption);
    }

    private void DrawOtherSettings()
    {
        DrawDiscordActivitySettings();
        SettingsSection(
            "Other settings",
            "Optional integrations and additional plugin behaviour.");

        DrawOtherSettingsPanel("##worldModeSettings", 235f, DrawInWorldBeaconSettings, autoHeight: true);

        DrawOtherSettingsPanel("##blockedAccountsSettings", 160f, DrawBlockedAccountsSettings, autoHeight: true);

        DrawOtherSettingsPanel("##liveBroadcastBufferingSettings", 180f, () =>
        {
            SettingsSection("Live Broadcast Buffering", "Choose how much extra video to buffer when watching a live broadcast.");
            int[] seconds = [0, 5, 10, 20];
            string[] options = ["Standard (default)", "Extra 5 seconds", "Extra 10 seconds", "Extra 20 seconds"];
            var selected = Array.IndexOf(seconds, Video.LiveBroadcastBufferPolicy.NormalizeExtra(Plugin.Cfg.LiveBroadcastExtraBufferSeconds));
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##liveBroadcastExtraBuffer", ref selected, options, options.Length))
            {
                Plugin.Cfg.LiveBroadcastExtraBufferSeconds = seconds[selected];
                Plugin.Cfg.Save();
            }
            DrawSettingsExplanation("Increasing buffering can help when a stream is laggy or keeps pausing. It increases startup time and puts you further behind the broadcaster.");
            ImGui.Spacing();
            DrawSettingsExplanation("Adds to each broadcast type's normal buffer. Applies when the next live broadcast starts or reconnects. Does not change YouTube, Twitch or radio playback.");
        }, autoHeight: true);

        DrawOtherSettingsPanel("##tvBehaviourSettings", 180f, () =>
        {
            SettingsSection("TV behaviour", "Choose how your TV responds while you play.");
            DrawSettingsHelper("When the Watch Party host starts playback");
            var playbackStart = (int)Plugin.Cfg.ViewerPlaybackStartBehaviour;
            string[] playbackStartOptions = ["Do not spawn TV, prompt only", "Spawn TV automatically", "Open mini video player"];
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##viewerPlaybackStartBehaviour", ref playbackStart, playbackStartOptions, playbackStartOptions.Length))
            {
                Plugin.Cfg.ViewerPlaybackStartBehaviour = (ViewerPlaybackStartBehaviour)playbackStart;
                Plugin.Cfg.Save();
            }
            DrawSettingsExplanation("Applies only when viewing a Watch Party. Hosts are unaffected. Prompt only shows the TV prompt on the Watch Party page.");
            ImGui.Spacing();
            DrawSettingsHelper("When entering instanced content");
            var behaviour = Plugin.Cfg.InstancedContentTvBehaviour == TvDutyBehaviour.StopPlayback ? 0 : 1;
            ImGui.SetNextItemWidth(-1);
            string[] options = ["Stop playback and despawn TV", "Switch to mini player and despawn TV"];
            if (ImGui.Combo("##instancedContentTvBehaviour", ref behaviour, options, options.Length))
            {
                Plugin.Cfg.InstancedContentTvBehaviour = behaviour == 0 ? TvDutyBehaviour.StopPlayback : TvDutyBehaviour.MiniPlayer;
                Plugin.Cfg.Save();
            }
            DrawSettingsExplanation("Applies when playing solo or viewing a Watch Party. Hosts are unaffected. Stopping ends audio and video playback while keeping you in your Watch Party.");
            ImGui.Spacing();
            DrawSettingsHelper("When moving too far from the TV");
            var distance = Plugin.Cfg.DistanceTvBehaviour == TvDutyBehaviour.StopPlayback ? 0 : 1;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##distanceTvBehaviour", ref distance, options, options.Length))
            {
                Plugin.Cfg.DistanceTvBehaviour = distance == 0 ? TvDutyBehaviour.StopPlayback : TvDutyBehaviour.MiniPlayer;
                Plugin.Cfg.Save();
            }
            DrawSettingsExplanation("Watch Party hosts always switch to the mini player so their party keeps playing.");
            ImGui.Spacing();
            DrawSettingsHelper("During cutscenes");
            var cutscene = (int)Plugin.Cfg.CutsceneTvBehaviour;
            string[] cutsceneOptions = ["No effect", "Stop playback", "Lower volume", "Mute"];
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##cutsceneTvBehaviour", ref cutscene, cutsceneOptions, cutsceneOptions.Length))
            {
                Plugin.Cfg.CutsceneTvBehaviour = (TvInterruptionBehaviour)cutscene;
                Plugin.Cfg.Save();
            }
            ImGui.Spacing();
            DrawSettingsHelper("Entering combat");
            var combat = (int)Plugin.Cfg.CombatTvBehaviour;
            string[] combatOptions = ["No effect", "Stop playback", "Lower volume"];
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##combatTvBehaviour", ref combat, combatOptions, combatOptions.Length))
            {
                Plugin.Cfg.CombatTvBehaviour = (TvInterruptionBehaviour)combat;
                Plugin.Cfg.Save();
            }
            DrawSettingsExplanation("Lower volume keeps 70% of your usual playback volume in combat and 30% during cutscenes, then restores afterwards. Stopped playback stays stopped until you resume it. Viewers remain in their party and resume at the host's current position. Stopping as host also stops the shared playback.");

        }, autoHeight: true);

        DrawOtherSettingsPanel("##gposeSettings", 120f, () =>
        {
            SettingsSection("GPose", "Control your TV while taking photos.");
            var showControlsDuringGpose = Plugin.Cfg.ShowControlsDuringGpose;
            if (ImGui.Checkbox("Keep plugin controls visible during GPose", ref showControlsDuringGpose))
            {
                Plugin.Cfg.ShowControlsDuringGpose = showControlsDuringGpose;
                Plugin.PluginInterface.UiBuilder.DisableGposeUiHide = showControlsDuringGpose;
                Plugin.Cfg.Save();
            }
            DrawSettingsExplanation("Keeps open Alpha Channel windows and controls available during GPose. Cutscene and instanced-content behaviour still follows your existing settings.");
        }, autoHeight: true);

        DrawViewerAudioVisualizerSettings();
        DrawAlphaChatSettings();
        DrawNotificationSoundSettings();

        DrawOtherSettingsPanel(
            "##otherMiniDockCard",
            190f,
            () =>
            {
                SettingsSection(
                    "Mini dock",
                    "Quick access to your TV, mini player, volume and lighting controls.");
                var dockDisplay = Math.Clamp((int)Plugin.Cfg.MiniDockDisplay,0,2);
                string[] dockOptions = ["Always display", "Display when playing content or in a Watch Party", "Always hidden"];
                ImGui.SetNextItemWidth(-1);
                if (ImGui.Combo("##miniDockDisplay", ref dockDisplay, dockOptions, dockOptions.Length))
                {
                    Plugin.Cfg.MiniDockDisplay = (MiniDockVisibility)dockDisplay;
                    Plugin.Cfg.Save();
                }
                ImGui.Dummy(UiVec(0f, 8f));
                PlaybackHud.DrawSettingsPreview(MathF.Min(Ui(300f), ImGui.GetContentRegionAvail().X));
            });

        DrawOtherSettingsPanel(
            "##otherHonorificStatusCard",
            198f,
            () =>
            {
                SettingsSection(
                    "Honorific status",
                    "Show your current Alpha Channel activity as a temporary Honorific title.");

                var displayStatus =
                    Plugin.Cfg.DisplayAlphaChannelStatusInHonorific;
                if (DrawSettingsToggle(
                        "##displayAlphaChannelHonorificStatus",
                        "Display Alpha Channel status in Honorific Title",
                        ref displayStatus))
                {
                    Plugin.Cfg.DisplayAlphaChannelStatusInHonorific =
                        displayStatus;
                    Plugin.Cfg.Save();
                }

                ImGui.Dummy(UiVec(0f, 11f));
                using var uiFontScale32893=UiFonts.PushRelative(settingsControlsActive ? .90f : 0.76f);
                DrawSettingsHelper("Title colour");
                uiFontScale32893.Dispose();
                DrawHonorificColorPresets();

                ImGui.Dummy(UiVec(0f, 8f));
                using var uiFontScale33126=UiFonts.PushRelative(settingsControlsActive ? .90f : 0.76f);
                using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
                    ImGui.TextWrapped("Requires Honorific. Our activity status returns after 30 seconds without another title change. Stopping our status leaves other plugins' titles alone.");
                uiFontScale33126.Dispose();
            }, autoHeight: true);
    }

    private static void DrawHonorificColorPresets()
    {
        var selected = Math.Clamp(
            Plugin.Cfg.HonorificStatusColorPreset,
            0,
            HonorificColorPresets.Length - 1);
        var size = UiVec(38f, 27f);
        var gap = Ui(8f);

        for (var index = 0; index < HonorificColorPresets.Length; index++)
        {
            if (index > 0)
            {
                ImGui.SameLine(0f, gap);
            }

            var preset = HonorificColorPresets[index];
            var origin = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton($"##honorificColor{index}", size);

            var drawList = ImGui.GetWindowDrawList();
            var glowColor = ImGui.GetColorU32(ParseAvatarColor(preset.Glow));
            var textColor = ImGui.GetColorU32(ParseAvatarColor(preset.Text));
            drawList.AddRectFilled(origin, origin + size, glowColor, Ui(7f));
            drawList.AddRectFilled(
                origin + UiVec(6f, 5f),
                origin + size - UiVec(6f, 5f),
                textColor,
                Ui(5f));
            drawList.AddRect(
                origin,
                origin + size,
                ImGui.GetColorU32(index == selected ? Vector4.One : SettingsReviewBorder),
                Ui(7f),
                ImDrawFlags.None,
                index == selected ? Ui(2f) : Ui(1f));

            if (ImGui.IsItemClicked() && index != selected)
            {
                Plugin.Cfg.HonorificStatusColorPreset = index;
                Plugin.Cfg.Save();
                selected = index;
            }

            if (ImGui.IsItemHovered())
            {
                UiFonts.SetTooltip($"{preset.Text}  |  {preset.Glow}");
            }
        }
    }

    private readonly Dictionary<string, float> settingsPanelMeasuredHeights = new();

    private void DrawOtherSettingsPanel(
        string id,
        float height,
        Action draw,
        bool autoHeight = false)
    {
        autoHeight |= settingsControlsActive;
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
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
            id,
            new Vector2(-1f, Ui(autoHeight
                ? settingsPanelMeasuredHeights.GetValueOrDefault(id, 850f)
                : height)),
            true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (panel)
            {
                draw();
                if (autoHeight)
                {
                    // Cursor Y includes every item and wrapped line, even below
                    // this frame's clip. Include the bottom padding for next frame.
                    settingsPanelMeasuredHeights[id] = ImGui.GetCursorPosY() / Ui(1f) + 18f;
                }
            }
        }

        ImGui.Dummy(UiVec(0f, 8f));
    }

    private static bool DrawSettingsToggle(
        string id,
        string label,
        ref bool value)
    {
        var switchSize = UiVec(38f, 20f);
        var switchOrigin = ImGui.GetCursorScreenPos();
        var changed = false;

        if (ImGui.InvisibleButton(
                id,
                switchSize))
        {
            value = !value;
            changed = true;
        }

        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            switchOrigin,
            switchOrigin + switchSize,
            ImGui.GetColorU32(
                value
                    ? Accent
                    : hovered
                        ? SettingsControlHover
                        : SettingsControlBg),
            switchSize.Y * 0.5f);

        drawList.AddRect(
            switchOrigin,
            switchOrigin + switchSize,
            ImGui.GetColorU32(hovered ? AccentHover : SettingsReviewBorder),
            switchSize.Y * 0.5f,
            ImDrawFlags.None,
            Ui(1f));

        var knobCenter = new Vector2(
            value
                ? switchOrigin.X + switchSize.X - switchSize.Y * 0.5f
                : switchOrigin.X + switchSize.Y * 0.5f,
            switchOrigin.Y + switchSize.Y * 0.5f);

        drawList.AddCircleFilled(
            knobCenter,
            Ui(7f),
            ImGui.GetColorU32(Vector4.One),
            18);

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        ImGui.SameLine(0f, Ui(9f));
        ImGui.TextUnformatted(label);

        if (ImGui.IsItemClicked())
        {
            value = !value;
            changed = true;
        }

        return changed;
    }

    private void DrawYouTubeSubscriptionSettings()
    {
        // ---------------------------------------------------------
        // Add channel manually
        // ---------------------------------------------------------

        DrawSocialHeading("Subscribe to a channel",1.1f);

        ImGui.Dummy(
            UiVec(0f, 4f));

        DrawSettingsHelper("Enter the exact YouTube channel name.");

        ImGui.Dummy(
            UiVec(0f, 8f));

        var buttonWidth = Ui(110f);
        var gap = NativeUi(10f);

        ImGui.SetNextItemWidth(
            ImGui.GetContentRegionAvail().X -
            buttonWidth -
            gap);

        bool submitted;

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f)
            .Push(
                ImGuiStyleVar.FramePadding,
                UiVec(12f, 9f)))
        {
            submitted =
                ImGui.InputTextWithHint(
                    "##manualYouTubeSubscription",
                    "Channel name...",
                    ref subscriptionChannelInput,
                    128,
                    ImGuiInputTextFlags.EnterReturnsTrue);
        }

        ImGui.SameLine(
            0f,
            gap);

        bool clicked;

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
        using (UiDisabled.Scope(
            isAddingManualSubscription ||
            string.IsNullOrWhiteSpace(
                subscriptionChannelInput)))
        {
            clicked =
                ImGui.Button(
                    "Subscribe##manualYouTubeSubscription",
                    new Vector2(
                        buttonWidth,
                        Ui(36f)));
        }

        if ((submitted || clicked) &&
            !isAddingManualSubscription &&
            !string.IsNullOrWhiteSpace(
                subscriptionChannelInput))
        {
            var channelName =
                subscriptionChannelInput.Trim();

            subscriptionMessage = null;
            isAddingManualSubscription = true;

            _ = AddManualYouTubeSubscriptionAsync(
                channelName);
        }

        if (isAddingManualSubscription)
        {
            ImGui.Dummy(
                UiVec(0f, 6f));

            DrawSettingsHelper("Finding channel...");
        }
        else if (subscriptionMessage is not null)
        {
            ImGui.Dummy(
                UiVec(0f, 6f));

            ImGui.TextColored(
                subscriptionMessageIsError
                    ? Danger
                    : Good,
                subscriptionMessage);
        }

        ImGui.Dummy(
            UiVec(0f, 8f));

        // ---------------------------------------------------------
        // Current subscriptions
        // ---------------------------------------------------------

        DrawSocialHeading("Subscribed channels",1.1f);

        ImGui.Dummy(
            UiVec(0f, 5f));

        using (ImRaii.PushColor(
            ImGuiCol.Text,
            MutedText))
        {
            DrawSettingsExplanation(
                "Subscriptions are stored on this device. Export them before moving devices or deleting the plugin, or to share them with friends.");
        }

        ImGui.Dummy(
            UiVec(0f, 9f));

        DrawDjActionButton(
            "##manageYouTubeSubscriptions",
            FontAwesomeIcon.List,
            "Manage subscriptions",
            new Vector2(
                ImGui.GetContentRegionAvail().X,
                Ui(36f)),
            false,
            () => manageSubscriptionsOpen = true,
            true);

        ImGui.Dummy(
            UiVec(0f, 9f));

        var clipboardGap = Ui(10f);
        var clipboardButtonWidth =
            (ImGui.GetContentRegionAvail().X - clipboardGap) * 0.5f;

        DrawDjActionButton(
            "##exportYouTubeSubscriptions",
            FontAwesomeIcon.Copy,
            "Export subscriptions",
            new Vector2(
                clipboardButtonWidth,
                Ui(34f)),
            false,
            ExportYouTubeSubscriptionsToClipboard,
            true);

        ImGui.SameLine(
            0f,
            clipboardGap);

        DrawDjActionButton(
            "##importYouTubeSubscriptions",
            FontAwesomeIcon.Clipboard,
            "Import subscriptions",
            new Vector2(
                clipboardButtonWidth,
                Ui(34f)),
            false,
            ImportYouTubeSubscriptionsFromClipboard,
            true);

        if (!string.IsNullOrWhiteSpace(
                subscriptionClipboardMessage))
        {
            ImGui.Dummy(
                UiVec(0f, 6f));

            ImGui.TextColored(
                subscriptionClipboardMessageIsError
                    ? Danger
                    : Good,
                subscriptionClipboardMessage);
        }

    }

    private void DrawYouTubeSubscriptionsOverlay()
    {
        if (!manageSubscriptionsOpen)
        {
            return;
        }

        var parentPosition = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var panelWidth = MathF.Min(
            Ui(800f),
            parentSize.X - Ui(40f));
        var panelHeight = MathF.Min(
            Ui(580f),
            parentSize.Y - Ui(40f));
        var panelPosition = parentPosition +
            new Vector2(
                (parentSize.X - panelWidth) * 0.5f,
                (parentSize.Y - panelHeight) * 0.5f);

        ImGui.SetNextWindowPos(
            parentPosition,
            ImGuiCond.Always);
        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);

        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;

        if (!BeginWindowOverlay(
                "##youtubeSubscriptionsOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        ImGui.GetWindowDrawList().AddRectFilled(
            parentPosition,
            parentPosition + parentSize,
            ImGui.GetColorU32(
                new Vector4(0f, 0f, 0f, 0.58f)));

        ImGui.SetCursorScreenPos(panelPosition);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            Ui(14f))
            .Push(
                ImGuiStyleVar.ChildBorderSize,
                Ui(1f))
            .Push(
                ImGuiStyleVar.WindowPadding,
                UiVec(22f, 20f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            CardBg)
            .Push(
                ImGuiCol.Border,
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.82f)))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
            "##youtubeSubscriptionsPanel",
            new Vector2(panelWidth, panelHeight),
            true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (panel)
            {
                DrawYouTubeSubscriptionsOverlayContents();
            }
        }

        ImGui.End();
    }

    private void DrawYouTubeSubscriptionsOverlayContents()
    {
        var subscriptions = Plugin.Cfg.SubscribedYouTubeChannelIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var uiFontScale47118=UiFonts.PushRelative(1.15f);
        ImGui.TextColored(
            Vector4.One,
            "Manage subscriptions");
        uiFontScale47118.Dispose();

        DrawSettingsHelper(subscriptions.Count == 1
                ? "1 subscribed YouTube channel"
                : $"{subscriptions.Count} subscribed YouTube channels");

        ImGui.Dummy(UiVec(0f, 8f));
        ImGui.Separator();
        ImGui.Dummy(UiVec(0f, 10f));

        var listHeight = MathF.Max(
            Ui(120f),
            ImGui.GetContentRegionAvail().Y - Ui(58f));

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            Ui(9f))
            .Push(
                ImGuiStyleVar.ChildBorderSize,
                Ui(1f))
            .Push(
                ImGuiStyleVar.WindowPadding,
                UiVec(8f, 8f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            CardBg)
            .Push(
                ImGuiCol.Border,
                SettingsReviewBorder))
        using (var list = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
            "##youtubeSubscriptionsList",
            new Vector2(-1f, listHeight),
            true)))
        {
            if (list)
            {
                if (subscriptions.Count == 0)
                {
                    ImGui.Dummy(UiVec(0f, 12f));
                    DrawSettingsHelper("You aren't subscribed to any channels yet.");
                }
                else
                {
                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.CellPadding,
                        UiVec(5f, 5f)))
                    {
                        if (ImGui.BeginTable(
                                "##youtubeSubscriptionsGrid",
                                2,
                                ImGuiTableFlags.SizingStretchSame))
                        {
                            foreach (var channelId in subscriptions)
                            {
                                ImGui.TableNextColumn();
                                ImGui.PushID(
                                    $"managedSubscription_{channelId}");
                                DrawYouTubeSubscriptionSettingsRow(
                                    channelId);
                                ImGui.PopID();
                            }

                            ImGui.EndTable();
                        }
                    }
                }
            }
        }

        ImGui.Dummy(UiVec(0f, 12f));

        var closeSize = UiVec(120f, 36f);
        ImGui.SetCursorPosX(
            ImGui.GetWindowWidth() - closeSize.X - Ui(22f));
        DrawDjActionButton(
            "##closeYouTubeSubscriptions",
            FontAwesomeIcon.Times,
            "Close",
            closeSize,
            false,
            () => manageSubscriptionsOpen = false,
            true);
    }

    private void DrawYouTubeTopicsOverlay()
    {
        if (!manageTopicsOpen)
        {
            return;
        }

        var parentPosition = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var panelWidth = MathF.Min(
            Ui(960f),
            parentSize.X - Ui(40f));
        var panelHeight = MathF.Min(
            Ui(620f),
            parentSize.Y - Ui(40f));
        var panelPosition = parentPosition +
            new Vector2(
                (parentSize.X - panelWidth) * 0.5f,
                (parentSize.Y - panelHeight) * 0.5f);

        ImGui.SetNextWindowPos(
            parentPosition,
            ImGuiCond.Always);
        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);

        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;

        if (!BeginWindowOverlay(
                "##youtubeTopicsOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        ImGui.GetWindowDrawList().AddRectFilled(
            parentPosition,
            parentPosition + parentSize,
            ImGui.GetColorU32(
                new Vector4(0f, 0f, 0f, 0.58f)));

        ImGui.SetCursorScreenPos(panelPosition);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            Ui(14f))
            .Push(
                ImGuiStyleVar.ChildBorderSize,
                Ui(1f))
            .Push(
                ImGuiStyleVar.WindowPadding,
                UiVec(22f, 20f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            CardBg)
            .Push(
                ImGuiCol.Border,
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.82f)))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
            "##youtubeTopicsPanel",
            new Vector2(panelWidth, panelHeight),
            true,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (panel)
            {
                DrawYouTubeTopicsOverlayContents();
            }
        }

        ImGui.End();
    }

    private void DrawYouTubeTopicsOverlayContents()
    {
        using var uiFontScale52958=UiFonts.PushRelative(1.15f);
        ImGui.TextColored(
            Vector4.One,
            "Manage topics");
        uiFontScale52958.Dispose();

        DrawSettingsHelper("Choose the topics used for the Browse Videos Topics tab.");

        ImGui.Dummy(UiVec(0f, 8f));

        var subscribedTopicCount = GetSubscribedTopicCount();
        ImGui.TextColored(
            subscribedTopicCount is >= 3 and <= 15
                ? AccentHover
                : new Vector4(1f, 0.55f, 0.35f, 1f),
            $"{subscribedTopicCount} / 15 topics selected");

        if (topicSelectionLimitWarning)
        {
            ImGui.SameLine(0f, Ui(12f));
            ImGui.TextColored(
                new Vector4(1f, 0.55f, 0.35f, 1f),
                "You can select up to 15 topics.");
        }

        ImGui.Dummy(UiVec(0f, 8f));
        ImGui.Separator();
        ImGui.Dummy(UiVec(0f, 10f));

        var topicGridHeight = MathF.Max(
            Ui(250f),
            ImGui.GetContentRegionAvail().Y - Ui(58f));
        var topicSignatureBefore =
            GetBrowseTopicSignature(GetEnabledTrendingTopics());

        DrawTrendingTopicTags(
            columnHeight: topicGridHeight,
            availableWidthOverride: ImGui.GetContentRegionAvail().X);

        var topicSignatureAfter =
            GetBrowseTopicSignature(GetEnabledTrendingTopics());

        if (!string.Equals(
                topicSignatureBefore,
                topicSignatureAfter,
                StringComparison.Ordinal))
        {
            NotifyTopicSettingsChanged();
        }

        ImGui.Dummy(UiVec(0f, 12f));

        var closeSize = UiVec(120f, 36f);
        ImGui.SetCursorPosX(
            ImGui.GetWindowWidth() - closeSize.X - Ui(22f));
        DrawDjActionButton(
            "##closeYouTubeTopics",
            FontAwesomeIcon.Times,
            "Close",
            closeSize,
            false,
            () =>
            {
                manageTopicsOpen = false;
                topicSelectionLimitWarning = false;
            },
            true);
    }

    private void ExportYouTubeSubscriptionsToClipboard()
    {
        var channels =
            Plugin.Cfg.SubscribedYouTubeChannelIds
                .Where(
                    channelId =>
                        !string.IsNullOrWhiteSpace(
                            channelId))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .Select(
                    channelId =>
                    {
                        Plugin.Cfg.SubscribedYouTubeChannelNames.TryGetValue(
                            channelId,
                            out var channelName);

                        return new SubscriptionClipboardChannel
                        {
                            ChannelId = channelId.Trim(),
                            ChannelName = string.IsNullOrWhiteSpace(channelName)
                                ? null
                                : channelName.Trim(),
                        };
                    })
                .ToList();

        var payload =
            new SubscriptionClipboardPayload
            {
                Type = "AlphaChannelYouTubeSubscriptions",
                Version = 1,
                Channels = channels,
            };

        ImGui.SetClipboardText(
            JsonSerializer.Serialize(payload));

        subscriptionClipboardMessage =
            channels.Count == 1
                ? "1 subscription copied to the clipboard."
                : $"{channels.Count} subscriptions copied to the clipboard.";

        subscriptionClipboardMessageIsError =
            false;
    }

    private void ImportYouTubeSubscriptionsFromClipboard()
    {
        try
        {
            var clipboardText =
                ImGui.GetClipboardText();

            if (string.IsNullOrWhiteSpace(
                    clipboardText))
            {
                SetSubscriptionClipboardError(
                    "The clipboard is empty.");

                return;
            }

            var payload =
                JsonSerializer.Deserialize<SubscriptionClipboardPayload>(
                    clipboardText);

            if (payload is null ||
                !string.Equals(
                    payload.Type,
                    "AlphaChannelYouTubeSubscriptions",
                    StringComparison.Ordinal) ||
                payload.Version != 1 ||
                payload.Channels is null ||
                payload.Channels.Count > 1000)
            {
                SetSubscriptionClipboardError(
                    "The clipboard does not contain a valid Alpha Channel subscriptions list.");

                return;
            }

            var importedChannels =
                new Dictionary<string, string?>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var channel in payload.Channels)
            {
                if (channel is null)
                {
                    SetSubscriptionClipboardError(
                        "The clipboard does not contain a valid Alpha Channel subscriptions list.");

                    return;
                }

                var channelId =
                    channel.ChannelId?.Trim();

                var channelName =
                    channel.ChannelName?.Trim();

                if (string.IsNullOrWhiteSpace(channelId) ||
                    channelId.Length > 200 ||
                    channelName?.Length > 300)
                {
                    SetSubscriptionClipboardError(
                        "The clipboard does not contain a valid Alpha Channel subscriptions list.");

                    return;
                }

                importedChannels.TryAdd(
                    channelId,
                    string.IsNullOrWhiteSpace(channelName)
                        ? null
                        : channelName);
            }

            var existingIds =
                new HashSet<string>(
                    Plugin.Cfg.SubscribedYouTubeChannelIds,
                    StringComparer.OrdinalIgnoreCase);

            var addedCount = 0;
            var existingCount = 0;
            var changed = false;

            foreach (var (channelId, channelName) in importedChannels)
            {
                if (existingIds.Add(channelId))
                {
                    Plugin.Cfg.SubscribedYouTubeChannelIds.Add(
                        channelId);

                    addedCount++;
                    changed = true;
                }
                else
                {
                    existingCount++;
                }

                if (!string.IsNullOrWhiteSpace(channelName) &&
                    (!Plugin.Cfg.SubscribedYouTubeChannelNames.TryGetValue(
                         channelId,
                         out var savedName) ||
                     string.IsNullOrWhiteSpace(savedName)))
                {
                    Plugin.Cfg.SubscribedYouTubeChannelNames[channelId] =
                        channelName;

                    changed = true;
                }
            }

            if (changed)
            {
                Plugin.Cfg.Save();
            }

            subscriptionClipboardMessageIsError =
                false;

            subscriptionClipboardMessage =
                addedCount switch
                {
                    0 when importedChannels.Count == 0 =>
                        "The imported subscriptions list is empty.",
                    0 =>
                        "All imported subscriptions were already in your list.",
                    1 when existingCount > 0 =>
                        $"Added 1 subscription; {existingCount} already existed.",
                    1 =>
                        "Added 1 subscription.",
                    _ when existingCount > 0 =>
                        $"Added {addedCount} subscriptions; {existingCount} already existed.",
                    _ =>
                        $"Added {addedCount} subscriptions.",
                };
        }
        catch (JsonException)
        {
            SetSubscriptionClipboardError(
                "The clipboard does not contain a valid Alpha Channel subscriptions list.");
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[Subscriptions] Clipboard import failed.");

            SetSubscriptionClipboardError(
                "The subscriptions list could not be imported.");
        }
    }

    private void SetSubscriptionClipboardError(
        string message)
    {
        subscriptionClipboardMessage =
            message;

        subscriptionClipboardMessageIsError =
            true;
    }

    private void DrawYouTubeSubscriptionSettingsRow(
    string channelId)
    {
        var rowHeight = Ui(46f);
        var removeSize = Ui(32f);

        var origin =
            ImGui.GetCursorScreenPos();

        var width =
            ImGui.GetContentRegionAvail().X;

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            origin,
            origin +
            new Vector2(
                width,
                rowHeight),
            ImGui.GetColorU32(
                CardBg),
            8f);

        drawList.AddRect(
            origin,
            origin +
            new Vector2(
                width,
                rowHeight),
            ImGui.GetColorU32(
                SettingsReviewBorder),
            8f);

        var hasSavedName =
     Plugin.Cfg.SubscribedYouTubeChannelNames
         .TryGetValue(
             channelId,
             out var savedName)
     &&
     !string.IsNullOrWhiteSpace(
         savedName);

        var channelName =
            hasSavedName
                ? savedName!
                : "Loading channel...";

        if (!hasSavedName &&
            subscriptionNamesLoading.Add(
                channelId))
        {
            _ = LoadSubscriptionChannelNameAsync(
                channelId);
        }

        // Channel icon
        using (UiFonts.PushIcon())
        {
            UiFonts.DrawText(drawList, ImGui.GetFontSize(), origin +
                UiVec(12f, 13f), ImGui.GetColorU32(
                    AccentHover), FontAwesomeIcon.User.ToIconString());
        }

        var channelTextPosition =
            origin + UiVec(34f, 14f);

        drawList.PushClipRect(
            channelTextPosition,
            new Vector2(
                origin.X + width - removeSize - Ui(16f),
                origin.Y + rowHeight),
            true);
        UiFonts.DrawText(drawList, ImGui.GetFontSize(), channelTextPosition, ImGui.GetColorU32(
                Vector4.One), channelName);
        drawList.PopClipRect();

        // Remove button
        ImGui.SetCursorScreenPos(
            new Vector2(
                origin.X +
                width -
                removeSize -
                Ui(7f),
                origin.Y + Ui(7f)));

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            7f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            new Vector4(
                0.09f,
                0.05f,
                0.07f,
                1f))
            .Push(
                ImGuiCol.ButtonHovered,
                new Vector4(
                    0.18f,
                    0.07f,
                    0.09f,
                    1f))
            .Push(
                ImGuiCol.ButtonActive,
                new Vector4(
                    0.22f,
                    0.08f,
                    0.10f,
                    1f)))
        {
            bool removeClicked;
            using (UiFonts.PushIcon())
            {
                removeClicked = ImGui.Button(
                    $"{FontAwesomeIcon.Trash.ToIconString()}##removeSubscription",
                    new Vector2(
                        removeSize,
                        removeSize));
            }

            if (ImGui.IsItemHovered())
            {
                UiFonts.SetTooltip($"Remove {channelName}");
            }

            if (removeClicked)
            {
                Plugin.Cfg
                    .SubscribedYouTubeChannelIds
                    .RemoveAll(
                        id => string.Equals(
                            id,
                            channelId,
                            StringComparison.OrdinalIgnoreCase));

                Plugin.Cfg
                    .SubscribedYouTubeChannelNames
                    .Remove(
                        channelId);

                Plugin.Cfg.Save();
            }
        }

        // Invisible spacer so ImGui advances past our
        // manually-drawn row.
        ImGui.SetCursorScreenPos(
            origin);

        ImGui.Dummy(
            new Vector2(
                width,
                rowHeight));
    }

    private void DrawThemeSettings(float availableWidth)
    {
        var gap = Ui(12f);
        var buttonSize = Ui(38f);
        var themes = Enum.GetValues<UiTheme>();
        var columns = Math.Clamp((int)((availableWidth + gap) / (buttonSize + gap)), 1, themes.Length);
        var originX = ImGui.GetCursorPosX();
        for (var first = 0; first < themes.Length; first += columns)
        {
            var count = Math.Min(columns, themes.Length - first);
            var rowWidth = buttonSize * count + gap * (count - 1);
            ImGui.SetCursorPosX(originX + MathF.Max(0f, (availableWidth - rowWidth) * 0.5f));
            for (var index = 0; index < count; index++)
            {
                if (index > 0) ImGui.SameLine(0f, gap);
                var theme = themes[first + index];
                DrawThemeOption(theme, ThemeCatalog.Get(theme).Accent, buttonSize);
            }
        }
    }


    private void DrawBackgroundSettings()
    {
        var gap = NativeUi(8f);

        var available =
            ImGui.GetContentRegionAvail().X;

        // Six built-in background styles.
        var width =
            (available -
             gap * 5f) / 6f;

        DrawBackgroundOption(
            UiBackground.Theme,
            width);

        ImGui.SameLine(
            0f,
            gap);

        DrawBackgroundOption(
            UiBackground.Midnight,
            width);

        ImGui.SameLine(
            0f,
            gap);

        DrawBackgroundOption(
            UiBackground.Void,
            width);

        ImGui.SameLine(
            0f,
            gap);

        DrawBackgroundOption(
            UiBackground.Slate,
            width);

        ImGui.SameLine(
            0f,
            gap);

        DrawBackgroundOption(
            UiBackground.Warm,
            width);

        ImGui.SameLine(
            0f,
            gap);

        DrawBackgroundOption(
            UiBackground.Carbon,
            width);
    }


    private void DrawBackgroundOption(
        UiBackground background,
        float width)
    {
        var selected =
            Plugin.Cfg.UiBackground ==
            background;

        var label =
            ThemeCatalog.Label(
                background);

        var swatch =
            background == UiBackground.Theme
                ? ThemeCatalog
                    .Get(Plugin.Cfg.UiTheme)
                    .WindowBg
                : ThemeCatalog.Swatch(
                    background);

        var size =
            new Vector2(
                width,
                Ui(38f));

        var origin =
            ImGui.GetCursorScreenPos();

        var drawList =
            ImGui.GetWindowDrawList();

        ImGui.PushID(
            (int)background + 100);

        var clicked =
            ImGui.InvisibleButton(
                "##bg",
                size);

        var hovered =
            ImGui.IsItemHovered();

        ImGui.PopID();

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                selected
                    ? new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.18f)
                    : hovered
                        ? CardBgHover
                        : CardBg),
            8f);

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

        var circleCenter =
            origin +
            new Vector2(
                Ui(17f),
                size.Y * 0.5f);

        drawList.AddCircleFilled(
            circleCenter,
            6.5f,
            ImGui.GetColorU32(
                swatch));

        drawList.AddCircle(
            circleCenter,
            6.5f,
            ImGui.GetColorU32(
                new Vector4(
                    1f,
                    1f,
                    1f,
                    0.25f)),
            0,
            1f);

        var labelSize =
            ImGui.CalcTextSize(
                label);

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                origin.X + Ui(29f),
                origin.Y +
                (size.Y -
                 labelSize.Y) * 0.5f), ImGui.GetColorU32(
                selected
                    ? Vector4.One
                    : MutedText), label);

        if (clicked &&
            !selected)
        {
            Plugin.Cfg.UiBackground =
                background;

            Plugin.Cfg.Save();

            Colors =
                ThemeCatalog.Get(
                    Plugin.Cfg.UiTheme,
                    background);
        }
    }


    private void DrawThemeOption(
     UiTheme theme,
     Vector4 swatch,
     float size)
    {
        var selected =
            Plugin.Cfg.UiTheme == theme;

        var origin =
            ImGui.GetCursorScreenPos();

        var buttonSize =
            new Vector2(size, size);

        ImGui.PushID((int)theme);

        var clicked =
            ImGui.InvisibleButton(
                "##theme",
                buttonSize);

        var hovered =
            ImGui.IsItemHovered();

        ImGui.PopID();

        var drawList =
            ImGui.GetWindowDrawList();

        var center =
            origin +
            buttonSize * 0.5f;

        var outerRadius =
            size * 0.5f;

        var innerRadius =
            selected
                ? 10f
                : 9f;

        // Button background.
        drawList.AddCircleFilled(
            center,
            outerRadius,
            ImGui.GetColorU32(
                hovered
                    ? CardBgHover
                    : CardBg));

        // Subtle border.
        drawList.AddCircle(
            center,
            outerRadius,
            ImGui.GetColorU32(
                selected
                    ? swatch
                    : SettingsReviewBorder),
            0,
            selected
                ? 2f
                : 1f);

        // Colour swatch.
        drawList.AddCircleFilled(
            center,
            innerRadius,
            ImGui.GetColorU32(swatch));

        // Selected check.
        if (selected)
        {
            using (UiFonts.PushIcon())
            {
                var check =
                    FontAwesomeIcon.Check
                        .ToIconString();

                var checkSize =
                    ImGui.CalcTextSize(check);

                UiFonts.DrawText(drawList, ImGui.GetFontSize(), center -
                    checkSize * 0.5f, ImGui.GetColorU32(Vector4.One), check);
            }
        }

        if (hovered)
        {
            UiFonts.SetTooltip(
                ThemeCatalog.Label(theme));
        }

        if (clicked && !selected)
        {
            Plugin.Cfg.UiTheme =
                theme;

            Plugin.Cfg.Save();

            Colors =
                ThemeCatalog.Get(
                    theme,
                    Plugin.Cfg.UiBackground);
        }
    }


    // =============================================================
    // NEW HELPER — ACCENT PREVIEW
    // =============================================================

    private static void DrawAccentPreview(
        Vector2 size)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var drawList =
            ImGui.GetWindowDrawList();

        ImGui.Dummy(size);

        // Outer mini-window.
        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                CardBg),
            9f);

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                SettingsReviewBorder),
            9f);

        var pad = NativeUi(8f);

        var innerMin =
            origin +
            new Vector2(
                pad,
                Ui(22f));

        var innerMax =
            origin +
            size -
            new Vector2(
                pad,
                pad);

        drawList.AddRectFilled(
            innerMin,
            innerMax,
            ImGui.GetColorU32(
                CardBg),
            5f);

        // Tiny title bar marks.
        drawList.AddRectFilled(
            origin +
            UiVec(14f, 10f),
            origin +
            UiVec(52f, 14f),
            ImGui.GetColorU32(
                Accent),
            3f);

        drawList.AddCircleFilled(
            origin +
            new Vector2(
                size.X - Ui(34f),
                Ui(12f)),
            2f,
            ImGui.GetColorU32(
                MutedText));

        drawList.AddCircleFilled(
            origin +
            new Vector2(
                size.X - Ui(22f),
                Ui(12f)),
            2f,
            ImGui.GetColorU32(
                MutedText));

        // Mini selected tab.
        var tabMin =
            innerMin +
            UiVec(8f, 8f);

        var tabMax =
            tabMin +
            UiVec(50f, 7f);

        drawList.AddRectFilled(
            tabMin,
            tabMax,
            ImGui.GetColorU32(
                Accent),
            3f);

        // Content lines.
        drawList.AddRectFilled(
            innerMin +
            UiVec(8f, 28f),
            innerMin +
            UiVec(90f, 33f),
            ImGui.GetColorU32(
                new Vector4(
                    0.45f,
                    0.47f,
                    0.55f,
                    0.45f)),
            2f);

        drawList.AddRectFilled(
            innerMin +
            UiVec(8f, 40f),
            innerMin +
            UiVec(64f, 44f),
            ImGui.GetColorU32(
                new Vector4(
                    0.45f,
                    0.47f,
                    0.55f,
                    0.30f)),
            2f);

        // Accent action.
        var buttonMax =
            innerMax -
            UiVec(8f, 8f);

        var buttonMin =
            buttonMax -
            UiVec(70f, 23f);

        drawList.AddRectFilled(
            buttonMin,
            buttonMax,
            ImGui.GetColorU32(
                Accent),
            5f);

        using (UiFonts.PushIcon())
        {
            var check =
                FontAwesomeIcon.Check
                    .ToIconString();

            var checkSize =
                ImGui.CalcTextSize(
                    check);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), buttonMin +
                new Vector2(
                    Ui(9f),
                    (Ui(23f) -
                     checkSize.Y) * 0.5f), ImGui.GetColorU32(
                    Vector4.One), check);
        }
    }


    // =============================================================
    // NEW HELPER — HOME ILLUSTRATION PREVIEW
    // =============================================================

    private void DrawHomeHeroPreview(
        Vector2 size)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var drawList =
            ImGui.GetWindowDrawList();

        ImGui.Dummy(size);

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                CardBg),
            9f);

        if (homeHero is not null &&
            Plugin.Cfg.ShowHomeHeroImage)
        {
            var imageOrigin =
    origin +
    UiVec(6f, 6f);

            var imageSize =
                size -
                UiVec(12f, 12f);

            var (uv0, uv1) =
                CoverUvs(
                    homeHero.Width,
                    homeHero.Height,
                    imageSize.X,
                    imageSize.Y);

            drawList.AddImageRounded(
                homeHero.Handle,
                imageOrigin,
                imageOrigin + imageSize,
                uv0,
                uv1,
                ImGui.GetColorU32(Vector4.One),
                7f);
        }
        else
        {
            var icon =
                FontAwesomeIcon.Image
                    .ToIconString();

            Vector2 iconSize;

            using (UiFonts.PushIcon())
            {
                iconSize =
                    ImGui.CalcTextSize(
                        icon);

                UiFonts.DrawText(drawList, ImGui.GetFontSize(), origin +
                    (size - iconSize) *
                    0.5f, ImGui.GetColorU32(
                        MutedText), icon);
            }
        }

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                SettingsReviewBorder),
            9f,
            ImDrawFlags.None,
            1f);
    }


    private void DrawTrendingTopicTags(
        float columnHeight = 405f,
        float? availableWidthOverride = null)
    {
        var availableWidth =
            availableWidthOverride ??
            ImGui.GetContentRegionAvail().X;

        var columnGap = Ui(8f);

        var columnWidth =
            MathF.Max(
                150f,
                (
                    availableWidth -
                    (columnGap * 2f)
                ) /
                3f);

        DrawTopicColumn(
            "Entertainment",
            columnWidth,
            columnHeight,
            () =>
            {
                Plugin.Cfg.TrendingGaming =
                    DrawTrendingTopicRow(
                        "Gaming",
                        Plugin.Cfg.TrendingGaming);

                Plugin.Cfg.TrendingMMORPG =
                    DrawTrendingTopicRow(
                        "MMORPG",
                        Plugin.Cfg.TrendingMMORPG);

                Plugin.Cfg.TrendingFinalFantasy =
                    DrawTrendingTopicRow(
                        "Final Fantasy",
                        Plugin.Cfg.TrendingFinalFantasy);

                Plugin.Cfg.TrendingFfxiv =
                    DrawTrendingTopicRow(
                        "FFXIV",
                        Plugin.Cfg.TrendingFfxiv);

                Plugin.Cfg.TrendingAnime =
                    DrawTrendingTopicRow(
                        "Anime",
                        Plugin.Cfg.TrendingAnime);

                Plugin.Cfg.TrendingMovies =
                    DrawTrendingTopicRow(
                        "Movies",
                        Plugin.Cfg.TrendingMovies);

                Plugin.Cfg.TrendingTvShows =
                    DrawTrendingTopicRow(
                        "TV Shows",
                        Plugin.Cfg.TrendingTvShows);

                Plugin.Cfg.TrendingMusic =
                    DrawTrendingTopicRow(
                        "Music",
                        Plugin.Cfg.TrendingMusic);

                Plugin.Cfg.TrendingMemes =
                    DrawTrendingTopicRow(
                        "Memes",
                        Plugin.Cfg.TrendingMemes);

                Plugin.Cfg.TrendingCartoons =
                    DrawTrendingTopicRow(
                        "Cartoons",
                        Plugin.Cfg.TrendingCartoons);

                Plugin.Cfg.TrendingHorror =
                    DrawTrendingTopicRow(
                        "Horror",
                        Plugin.Cfg.TrendingHorror);

                Plugin.Cfg.TrendingSciFi =
                    DrawTrendingTopicRow(
                        "Sci-Fi",
                        Plugin.Cfg.TrendingSciFi);

                Plugin.Cfg.TrendingComedy =
                    DrawTrendingTopicRow(
                        "Comedy",
                        Plugin.Cfg.TrendingComedy);

                Plugin.Cfg.TrendingMinecraft =
                    DrawTrendingTopicRow(
                        "Minecraft",
                        Plugin.Cfg.TrendingMinecraft);

                Plugin.Cfg.TrendingDisney =
                    DrawTrendingTopicRow(
                        "Disney",
                        Plugin.Cfg.TrendingDisney);

                Plugin.Cfg.TrendingFantasy =
                    DrawTrendingTopicRow(
                        "Fantasy",
                        Plugin.Cfg.TrendingFantasy);
            });

        ImGui.SameLine(
            0f,
            columnGap);

        DrawTopicColumn(
            "World & Knowledge",
            columnWidth,
            columnHeight,
            () =>
            {
                Plugin.Cfg.TrendingWildlife =
                    DrawTrendingTopicRow(
                        "Wildlife",
                        Plugin.Cfg.TrendingWildlife);

                Plugin.Cfg.TrendingArchitecture =
                    DrawTrendingTopicRow(
                        "Architecture",
                        Plugin.Cfg.TrendingArchitecture);

                Plugin.Cfg.TrendingScience =
                    DrawTrendingTopicRow(
                        "Science",
                        Plugin.Cfg.TrendingScience);

                Plugin.Cfg.TrendingSpace =
                    DrawTrendingTopicRow(
                        "Space",
                        Plugin.Cfg.TrendingSpace);

                Plugin.Cfg.TrendingHistory =
                    DrawTrendingTopicRow(
                        "History",
                        Plugin.Cfg.TrendingHistory);

                Plugin.Cfg.TrendingTechnology =
                    DrawTrendingTopicRow(
                        "Technology",
                        Plugin.Cfg.TrendingTechnology);

                Plugin.Cfg.TrendingUrbanExploration =
                    DrawTrendingTopicRow(
                        "Urban Exploration",
                        Plugin.Cfg.TrendingUrbanExploration);
            });

        ImGui.SameLine(
            0f,
            columnGap);

        DrawTopicColumn(
            "Lifestyle & Creative",
            columnWidth,
            columnHeight,
            () =>
            {
                Plugin.Cfg.TrendingPets =
                    DrawTrendingTopicRow(
                        "Pets",
                        Plugin.Cfg.TrendingPets);

                Plugin.Cfg.TrendingFood =
                    DrawTrendingTopicRow(
                        "Food",
                        Plugin.Cfg.TrendingFood);

                Plugin.Cfg.TrendingTravel =
                    DrawTrendingTopicRow(
                        "Travel",
                        Plugin.Cfg.TrendingTravel);

                Plugin.Cfg.TrendingCars =
                    DrawTrendingTopicRow(
                        "Cars",
                        Plugin.Cfg.TrendingCars);

                Plugin.Cfg.TrendingSports =
                    DrawTrendingTopicRow(
                        "Sports",
                        Plugin.Cfg.TrendingSports);

                Plugin.Cfg.TrendingArtsAndCrafts =
                    DrawTrendingTopicRow(
                        "Arts & Crafts",
                        Plugin.Cfg.TrendingArtsAndCrafts);

                Plugin.Cfg.TrendingCosplaying =
                    DrawTrendingTopicRow(
                        "Cosplaying",
                        Plugin.Cfg.TrendingCosplaying);

                Plugin.Cfg.TrendingDiy =
                    DrawTrendingTopicRow(
                        "DIY",
                        Plugin.Cfg.TrendingDiy);

                Plugin.Cfg.TrendingFashion =
                    DrawTrendingTopicRow(
                        "Fashion",
                        Plugin.Cfg.TrendingFashion);
            });

        Plugin.Cfg.Save();
    }

    private void DrawTopicColumn(
      string heading,
      float width,
      float height,
      Action drawRows)
    {
        ImGui.PushID(
            heading);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildRounding,
            7f))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.ChildBorderSize,
            1f))
        using (ImRaii.PushColor(
            ImGuiCol.Border,
            new Vector4(
                Accent.X,
                Accent.Y,
                Accent.Z,
                0.55f)))
        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            CardBg))
        using (var child =
            UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                "##topicColumn",
                new Vector2(
                    width,
                    height),
                true)))
        {
            if (child)
            {
                ImGui.TextColored(
                    AccentHover,
                    heading);

                ImGui.Dummy(
                    new Vector2(
                        0f,
                        1f));

                using (ImRaii.PushStyle(
                    ImGuiStyleVar.ItemSpacing,
                    UiVec(4f, 1f)))
                {
                    drawRows();
                }
            }
        }

        ImGui.PopID();
    }

    private bool DrawTrendingTopicRow(
      string label,
      bool selected)
    {
        var selectionLimitReached =
            !selected &&
            GetSubscribedTopicCount() >= 15;

        if (selectionLimitReached)
        {
            UiDisabled.Begin();
        }

        var rowLabel =
            selected
                ? $"✓  {label}"
                : $"    {label}";

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            2f))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FramePadding,
            UiVec(5f, 0f)))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.ButtonTextAlign,
            new Vector2(
                0f,
                0.5f)))
        using (ImRaii.PushColor(
            ImGuiCol.Text,
            selected
                ? AccentHover
                : Vector4.One))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            selected
                ? new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.18f)
                : new Vector4(
                    0f,
                    0f,
                    0f,
                    0f)))
        using (ImRaii.PushColor(
            ImGuiCol.ButtonHovered,
            selected
                ? new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.28f)
                : CardBgHover))
        using (ImRaii.PushColor(
            ImGuiCol.ButtonActive,
            new Vector4(
                Accent.X,
                Accent.Y,
                Accent.Z,
                0.35f)))
        {
            if (ImGui.Button(
                    $"{rowLabel}##topicRow_{label}",
                    UiVec(-1f, 17f)))
            {
                selected =
                    !selected;

                topicSelectionLimitWarning =
                    false;
            }
        }

        if (selectionLimitReached)
        {
            UiDisabled.End();

            var attemptedSelection =
                ImGui.IsItemHovered(
                    ImGuiHoveredFlags.AllowWhenDisabled) &&
                ImGui.IsMouseClicked(
                    ImGuiMouseButton.Left);

            if (attemptedSelection)
            {
                topicSelectionLimitWarning =
                    true;
            }
        }

        return selected;
    }
}
